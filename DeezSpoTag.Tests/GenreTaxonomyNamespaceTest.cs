using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DeezSpoTag.Services.Genre;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// The taxonomy namespace invariant: one normalized lookup key resolves to exactly one
/// semantic taxon.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the guard had the right shape but the wrong vocabulary. It
/// compared a proposed custom taxon against the original 156 built-ins only, so every
/// researched term was shadowable: a user could create "detroit-trap-2" / "Detroit
/// Trap" and take the lookup key owned by the researched term "detroit trap" without
/// ever being told.
/// </para>
/// <para>
/// The rejection is server-side on purpose. A UI preflight is a courtesy, and the
/// vocabulary-surface work already showed exactly why a check that lives only in the
/// client, or only in the engine, is not enough. When a value must mean something
/// different, the supported mechanism is a user rule, a mapping or a lock; creating a
/// second taxon for the same key corrupts the namespace instead of expressing a
/// preference.
/// </para>
/// </remarks>
public sealed class GenreTaxonomyNamespaceTest : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "genre-namespace-" + Guid.NewGuid());

    public GenreTaxonomyNamespaceTest() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        // No environment variable is touched here. Doing so is process-wide, xUnit
        // runs test classes in parallel, and PersonalGenreStore prefers the variable
        // over configuration, so setting it breaks whichever Genre store test happens
        // to be running alongside. Configuration is what every other store test uses.
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    private async Task<PersonalGenreStore> NewStoreAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] =
                    "Data Source=" + Path.Combine(_directory, "library.db")
            })
            .Build();
        var store = new PersonalGenreStore(configuration);
        await store.GetSettingsAsync();
        return store;
    }

    // ------------------------------------------------------------------ group 1
    // A protected canonical cannot be shadowed by a different custom id.

    /// <summary>
    /// "thai-trap" and "detroit trap" are researched terms, "Cha-Cha-Chá" and "Afro-Soul"
    /// are legacy protected ones. Each is refused under a fresh id, so the guard is
    /// checking the whole protected vocabulary rather than one array inside it.
    /// </summary>
    [Theory]
    [InlineData("detroit-trap-2", "Detroit Trap")]      // researched canonical
    [InlineData("thai-trap-2", "Thai-Trap")]            // researched canonical, punctuated differently
    [InlineData("cha-cha-cha-2", "Cha Cha Cha")]        // legacy protected canonical
    [InlineData("afro-soul-2", "Afro Soul")]            // legacy protected canonical
    public async Task AProtectedCanonicalCannotBeShadowedByADifferentCustomId(
        string id,
        string name)
    {
        var store = await NewStoreAsync();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertCustomTaxonAsync(
            new PersonalGenreTaxon(id, name, PersonalGenreTaxonKind.Style)));

        Assert.Contains("already resolves from", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The refusal names the value the user typed and the canonical term it collides
    /// with, so the message tells them what to do rather than only that no.
    /// </summary>
    [Fact]
    public async Task TheRefusalNamesBothTheValueAndTheTermItCollidesWith()
    {
        var store = await NewStoreAsync();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertCustomTaxonAsync(
            new PersonalGenreTaxon("detroit-trap-2", "Detroit-Trap", PersonalGenreTaxonKind.Style)));

        Assert.Contains("Detroit-Trap", error.Message, StringComparison.Ordinal);
        Assert.Contains("detroit trap", error.Message, StringComparison.Ordinal);
        Assert.Contains("mapping", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An exact id match on a protected term is still refused outright, so the exact
    /// case cannot be made to work while the punctuation variant is refused.
    /// </summary>
    [Fact]
    public async Task AProtectedIdCannotBeReplacedDirectly()
    {
        var store = await NewStoreAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertCustomTaxonAsync(
            new PersonalGenreTaxon("detroit-trap", "Detroit Trap", PersonalGenreTaxonKind.Style)));
    }

    /// <summary>
    /// Writing directly over a researched term through the persistence layer is
    /// refused, so the namespace cannot be corrupted by bypassing the taxonomy form.
    /// </summary>
    [Fact]
    public async Task AResearchedTermCannotBeDeletedByASupposedUser()
    {
        var store = await NewStoreAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.DeleteCustomTaxonAsync("detroit-trap"));
    }

    // ------------------------------------------------------------------ group 2
    // A protected alias cannot be claimed, as a canonical name or as an alias.

    [Fact]
    public async Task AProtectedAliasCannotBeClaimedByACustomCanonicalName()
    {
        var store = await NewStoreAsync();

        // "hindie" is a researched alias of "Indian Indie".
        var error = await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertCustomTaxonAsync(
            new PersonalGenreTaxon("hindie-local", "Hindie", PersonalGenreTaxonKind.Style)));

        Assert.Contains("already resolves from", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Indian Indie", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProtectedAliasCannotBeClaimedByACustomAlias()
    {
        var store = await NewStoreAsync();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertCustomTaxonAsync(
            new PersonalGenreTaxon(
                "my-local-scene",
                "My Local Scene",
                PersonalGenreTaxonKind.Style,
                Aliases: ["hindie"])));

        Assert.Contains("already resolves from", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Indian Indie", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProtectedIdCannotBeClaimedAsACustomAlias()
    {
        var store = await NewStoreAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertCustomTaxonAsync(
            new PersonalGenreTaxon(
                "another-local-term",
                "Another Local Term",
                PersonalGenreTaxonKind.Style,
                Aliases: ["detroit-trap"])));
    }

    // ------------------------------------------------------------------ group 3
    // Two custom taxa cannot occupy the same normalized key.

    [Fact]
    public async Task TwoCustomTaxaCannotOccupyTheSameNormalizedKey()
    {
        var store = await NewStoreAsync();

        await store.UpsertCustomTaxonAsync(
            new PersonalGenreTaxon("my-first-scene", "My First Scene", PersonalGenreTaxonKind.Style));

        var error = await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertCustomTaxonAsync(
            new PersonalGenreTaxon("my-second-scene", "My-First-Scene", PersonalGenreTaxonKind.Style)));

        Assert.Contains("already resolves from", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("My First Scene", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACustomAliasCannotStealAnotherCustomTaxonsKey()
    {
        var store = await NewStoreAsync();

        await store.UpsertCustomTaxonAsync(
            new PersonalGenreTaxon("my-third-scene", "My Third Scene", PersonalGenreTaxonKind.Style));

        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertCustomTaxonAsync(
            new PersonalGenreTaxon(
                "my-fourth-scene",
                "My Fourth Scene",
                PersonalGenreTaxonKind.Style,
                Aliases: ["My-Third-Scene"])));
    }

    /// <summary>
    /// A taxon whose alias repeats its own name answers to fewer keys than it has
    /// values. That redundancy is the user's, and the guard must not mistake it for a
    /// collision with somebody else.
    /// </summary>
    [Fact]
    public async Task ATaxonWhoseAliasRepeatsItsOwnNameIsAccepted()
    {
        var store = await NewStoreAsync();

        var created = await store.UpsertCustomTaxonAsync(new PersonalGenreTaxon(
            "my-eighth-scene",
            "My Eighth Scene",
            PersonalGenreTaxonKind.Style,
            Aliases: ["My-Eighth-Scene", "MES"]));

        Assert.Equal("my-eighth-scene", created.Id);
    }

    // ------------------------------------------------------------------ group 4
    // Editing does not conflict with itself, but does with every other taxon.

    [Fact]
    public async Task EditingACustomTaxonDoesNotConflictWithItself()
    {
        var store = await NewStoreAsync();

        var created = await store.UpsertCustomTaxonAsync(new PersonalGenreTaxon(
            "my-fifth-scene",
            "My Fifth Scene",
            PersonalGenreTaxonKind.Style,
            Aliases: ["MFS"],
            Regions: ["africa"]));

        Assert.Equal("my-fifth-scene", created.Id);

        // Same id and name, changed kind, parents, aliases and regions.
        var edited = await store.UpsertCustomTaxonAsync(new PersonalGenreTaxon(
            "my-fifth-scene",
            "My Fifth Scene",
            PersonalGenreTaxonKind.Substyle,
            ParentIds: ["hip-hop"],
            Aliases: ["MFS", "My Fifth Scene Extended"],
            Regions: ["africa", "europe"]));

        Assert.Equal("my-fifth-scene", edited.Id);
        Assert.Single(await store.GetCustomTaxaAsync(), item => item.Id == "my-fifth-scene");
    }

    [Fact]
    public async Task ARenameThatKeepsTheSameLookupKeyIsAllowed()
    {
        var store = await NewStoreAsync();

        await store.UpsertCustomTaxonAsync(
            new PersonalGenreTaxon("my-ninth-scene", "My Ninth Scene", PersonalGenreTaxonKind.Style));

        // Only the spelling changes; the key it answers to does not.
        await store.UpsertCustomTaxonAsync(
            new PersonalGenreTaxon("my-ninth-scene", "My-Ninth-Scene", PersonalGenreTaxonKind.Style));
    }

    [Fact]
    public async Task EditingACustomTaxonStillConflictsWithEveryOtherTaxon()
    {
        var store = await NewStoreAsync();

        await store.UpsertCustomTaxonAsync(
            new PersonalGenreTaxon("my-sixth-scene", "My Sixth Scene", PersonalGenreTaxonKind.Style));
        await store.UpsertCustomTaxonAsync(
            new PersonalGenreTaxon("my-seventh-scene", "My Seventh Scene", PersonalGenreTaxonKind.Style));

        // Editing one into the other's key is refused even though its own id is stable.
        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertCustomTaxonAsync(
            new PersonalGenreTaxon("my-sixth-scene", "My Seventh Scene", PersonalGenreTaxonKind.Style)));

        // And editing one onto a protected key is refused too.
        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertCustomTaxonAsync(
            new PersonalGenreTaxon("my-sixth-scene", "Detroit Trap", PersonalGenreTaxonKind.Style)));

        // The refused edits left the original term untouched.
        Assert.Equal(
            "My Sixth Scene",
            (await store.GetCustomTaxaAsync()).Single(item => item.Id == "my-sixth-scene").Name);
    }

    // --------------------------------------------- the supported alternative stays open

    /// <summary>
    /// The namespace guard refuses to express an opinion, so the supported mechanism has
    /// to keep working. A mapping says something different about a value without
    /// creating a second taxon for the same key.
    /// </summary>
    [Fact]
    public async Task AUserMappingRemainsTheWayToOverrideAValue()
    {
        var store = await NewStoreAsync();

        await store.UpsertMappingAsync(new PersonalGenreMapping(0, "detroit trap", "cha-cha-cha"));

        Assert.Contains(
            await store.GetMappingsAsync(),
            mapping => mapping.MatchValue == "detroit trap"
                && mapping.TargetTaxonId == "cha-cha-cha");
    }

    [Fact]
    public async Task ACustomTermWithAFreeKeyIsStillAccepted()
    {
        var store = await NewStoreAsync();

        var created = await store.UpsertCustomTaxonAsync(new PersonalGenreTaxon(
            "my-own-scene",
            "My Own Scene",
            PersonalGenreTaxonKind.Style,
            Aliases: ["My Own Scene Extended", "MOS"],
            Regions: ["south-asia"]));

        Assert.Equal("my-own-scene", created.Id);
        Assert.Contains(await store.GetCustomTaxaAsync(), item => item.Id == "my-own-scene");
    }

    // ------------------------------------------------------------------ the guard is not vacuous

    /// <summary>
    /// A 3,745-term vocabulary means an over-broad guard would refuse nearly
    /// everything. A run of ordinary invented terms must all still be accepted, which is
    /// what keeps the refusal meaningful.
    /// </summary>
    [Fact]
    public async Task OrdinaryNewTermsAreNotCaughtByAnOverBroadGuard()
    {
        var store = await NewStoreAsync();

        for (var index = 0; index < 40; index++)
        {
            var created = await store.UpsertCustomTaxonAsync(new PersonalGenreTaxon(
                "workshop-scene-" + index,
                "Workshop Scene " + index,
                PersonalGenreTaxonKind.Style,
                Aliases: ["WS" + index]));

            Assert.Equal("workshop-scene-" + index, created.Id);
        }

        Assert.Equal(40, (await store.GetCustomTaxaAsync()).Count);
    }
}