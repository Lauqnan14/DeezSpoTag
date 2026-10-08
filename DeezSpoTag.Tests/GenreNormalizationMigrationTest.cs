using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Core.Utils;
using DeezSpoTag.Services.Genre;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Controllers.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Covers the move of genre normalization out of the general application settings
/// and into Genre Intelligence.
///
/// The values themselves did not change meaning. What changed is where they live
/// and who reads them, so the tests are mostly about a user's existing
/// preferences surviving that move untouched.
/// </summary>
public sealed class GenreNormalizationMigrationTest : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "genre-normalization-" + Guid.NewGuid());

    public GenreNormalizationMigrationTest() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }

        GC.SuppressFinalize(this);
    }

    private PersonalGenreStore CreateStore()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Library"] = "Data Source=" + Path.Combine(_directory, "library.db")
        }).Build();
        return new PersonalGenreStore(configuration);
    }

    private static GenreNormalizationProvider CreateProvider(PersonalGenreStore store)
        => new(store, NullLogger<GenreNormalizationProvider>.Instance);

    private static DeezSpoTagSettings LegacySettings(
        bool enabled = true,
        IEnumerable<GenreTagAliasRule>? aliases = null,
        IEnumerable<string>? blocked = null)
    {
#pragma warning disable CS0618 // Constructing the deprecated shape is the point of this test.
        return new DeezSpoTagSettings
        {
            NormalizeGenreTags = enabled,
            GenreTagAliasRules = (aliases ?? new[] { new GenreTagAliasRule { Alias = "Afro-Pop", Canonical = "Afropop" } }).ToList(),
            GenreTagBlockList = (blocked ?? new[] { "other", "others", "Worldwide" }).ToList()
        };
#pragma warning restore CS0618
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExistingEnabledStateMigratesUnchanged(bool enabled)
    {
        var store = CreateStore();
        await store.SaveSettingsAsync(new PersonalGenreSettings(Enabled: true, MaxGenres: 3));
        var provider = CreateProvider(store);

        await provider.MigrateLegacySettingsAsync(LegacySettings(enabled: enabled));

        var migrated = await store.GetSettingsAsync();
        Assert.Equal(enabled, migrated.NormalizeGenreTags);
    }

    [Fact]
    public async Task CustomAliasRulesMigrateUnchanged()
    {
        var store = CreateStore();
        var provider = CreateProvider(store);
        var legacy = LegacySettings(aliases:
        [
            new GenreTagAliasRule { Alias = "Drill", Canonical = "Trap" },
            new GenreTagAliasRule { Alias = "Grime", Canonical = "Electronic" }
        ]);

        await provider.MigrateLegacySettingsAsync(legacy);

        var migrated = await store.GetSettingsAsync();
        var aliases = migrated.GenreTagAliasRules!.ToDictionary(rule => rule.Alias, rule => rule.Canonical);
        Assert.Equal("Trap", aliases["Drill"]);
        Assert.Equal("Electronic", aliases["Grime"]);
        // The shipped defaults are folded back in, as they were before the move.
        Assert.Equal("Afropop", aliases["Afro-Pop"]);
        Assert.Equal("HipHop", aliases["Hip-Hop"]);
    }

    [Fact]
    public async Task CustomBlockListMigratesUnchanged()
    {
        var store = CreateStore();
        var provider = CreateProvider(store);
        var legacy = LegacySettings(blocked: ["other", "others", "Worldwide", "Misc", "Various"]);

        await provider.MigrateLegacySettingsAsync(legacy);

        var migrated = await store.GetSettingsAsync();
        var blocked = migrated.GenreTagBlockList!.ToList();
        Assert.Contains("Misc", blocked);
        Assert.Contains("Various", blocked);
        Assert.Contains("other", blocked);
        Assert.Contains("Worldwide", blocked);
    }

    /// <summary>
    /// The shipped default rules are re-added when a user's list is missing them.
    ///
    /// This is the behaviour the settings normalizer had before the move, and this
    /// task is a change of ownership, not of meaning. Preserving it matters: if the
    /// migration instead dropped the defaults, a user who never edited the list
    /// would silently lose normalization for values they rely on.
    /// </summary>
    [Fact]
    public async Task ShippedDefaultAliasesAreRestoredTheWayTheyWereBefore()
    {
        var store = CreateStore();
        var provider = CreateProvider(store);
        var legacy = LegacySettings(aliases: [new GenreTagAliasRule { Alias = "Drill", Canonical = "Trap" }]);

        await provider.MigrateLegacySettingsAsync(legacy);

        var migrated = await store.GetSettingsAsync();
        // Keyed the way the normalizer keys them: "Hip-Hop" and "Hip Hop" are the
        // same rule, so the stored list keeps the first and the lookup collapses
        // the rest. A dictionary keyed by the raw alias would be wrong here.
        var aliases = migrated.GenreTagAliasRules!
            .GroupBy(rule => GenreTagAliasNormalizer.ToLookupKey(rule.Alias), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Canonical);

        Assert.Equal("Trap", aliases[GenreTagAliasNormalizer.ToLookupKey("Drill")]);
        Assert.Equal("Afropop", aliases[GenreTagAliasNormalizer.ToLookupKey("Afro-Pop")]);
        Assert.Equal("HipHop", aliases[GenreTagAliasNormalizer.ToLookupKey("Hip-Hop")]);
        // "Hip-Hop" and "Hip Hop" are the same lookup key, so they collapse to one
        // stored rule: the shipped four become three.
        Assert.Equal(3, migrated.GenreTagAliasRules!.Count);
    }

    /// <summary>
    /// A custom alias that shadows a shipped default keeps the user's value, so
    /// the default cannot overwrite a deliberate choice.
    /// </summary>
    [Fact]
    public async Task ACustomAliasOverridesAShippedDefaultOfTheSameName()
    {
        var store = CreateStore();
        var provider = CreateProvider(store);
        var legacy = LegacySettings(aliases: [new GenreTagAliasRule { Alias = "Afro-Pop", Canonical = "Afrobeat" }]);

        await provider.MigrateLegacySettingsAsync(legacy);

        var migrated = await store.GetSettingsAsync();
        var aliases = migrated.GenreTagAliasRules!.ToDictionary(rule => rule.Alias, rule => rule.Canonical);
        Assert.Equal("Afrobeat", aliases["Afro-Pop"]);
    }

    [Fact]
    public async Task RunningTheMigrationTwiceDoesNotOverwriteLaterChanges()
    {
        var store = CreateStore();
        var provider = CreateProvider(store);
        await provider.MigrateLegacySettingsAsync(LegacySettings(
            enabled: true,
            aliases: [new GenreTagAliasRule { Alias = "FirstRunOnly", Canonical = "Imported" }],
            blocked: ["other"]));

        // The user then changes the values in Genre Intelligence.
        var current = await store.GetSettingsAsync();
        await store.SaveSettingsAsync(current with
        {
            NormalizeGenreTags = false,
            GenreTagBlockList = ["OnlyThis"],
            GenreTagAliasRules = [new PersonalGenreAliasRule("Mine", "Yours")]
        });

        // A second startup must not re-import the legacy values.
        await provider.MigrateLegacySettingsAsync(LegacySettings(
            enabled: true,
            aliases: [new GenreTagAliasRule { Alias = "FirstRunOnly", Canonical = "Imported" }],
            blocked: ["other"]));

        var after = await store.GetSettingsAsync();
        Assert.False(after.NormalizeGenreTags);
        Assert.Equal(new[] { "OnlyThis" }, after.GenreTagBlockList);
        var aliases = after.GenreTagAliasRules!.ToDictionary(rule => rule.Alias, rule => rule.Canonical);
        Assert.Equal("Yours", aliases["Mine"]);
        // The value the first import brought in is gone, which is only possible if
        // the second startup ignored the legacy file rather than re-reading it.
        Assert.False(aliases.ContainsKey("FirstRunOnly"));
    }

    [Fact]
    public async Task MigrationIsSkippedWhenThereAreNoLegacySettings()
    {
        var store = CreateStore();
        await store.SaveSettingsAsync(new PersonalGenreSettings(Enabled: true, MaxGenres: 3));
        var provider = CreateProvider(store);

        await provider.MigrateLegacySettingsAsync(null);

        // Nothing to import, but the marker is written so a later startup does not
        // pick up a stale configuration file.
        Assert.True(await store.HasMigrationAsync(GenreNormalizationProvider.LegacySettingsMigrationName));
        var after = await store.GetSettingsAsync();
        Assert.Equal(3, after.MaxGenres);
    }

    [Fact]
    public async Task OtherSettingsAreNotDisturbedByTheMigration()
    {
        var store = CreateStore();
        await store.SaveSettingsAsync(new PersonalGenreSettings(Enabled: true, MaxGenres: 7, IncludeParentGenres: true));
        var provider = CreateProvider(store);

        await provider.MigrateLegacySettingsAsync(LegacySettings());

        var migrated = await store.GetSettingsAsync();
        Assert.True(migrated.Enabled);
        Assert.Equal(7, migrated.MaxGenres);
        Assert.True(migrated.IncludeParentGenres);
    }

    /// <summary>
    /// A save persists what the user configured, with the shipped defaults folded
    /// in alongside it.
    ///
    /// The fold is pre-existing behaviour, not something this move introduced:
    /// the settings normalizer re-added the defaults on every save. The custom
    /// rules survive and the defaults come back, which is what a user who added
    /// one rule to the stock list has always ended up with.
    /// </summary>
    [Fact]
    public async Task SavedNormalizationRoundTripsThroughTheStore()
    {
        var store = CreateStore();
        await store.SaveSettingsAsync(new PersonalGenreSettings(
            Enabled: true,
            MaxGenres: 4,
            NormalizeGenreTags: true,
            GenreTagAliasRules: [new PersonalGenreAliasRule("A", "B"), new PersonalGenreAliasRule("C", "D")],
            GenreTagBlockList: ["x", "y"]));

        var reloaded = await store.GetSettingsAsync();
        Assert.True(reloaded.NormalizeGenreTags);
        Assert.Equal(4, reloaded.MaxGenres);
        var aliases = reloaded.GenreTagAliasRules!
            .ToDictionary(rule => rule.Alias, rule => rule.Canonical);
        Assert.Equal("B", aliases["A"]);
        Assert.Equal("D", aliases["C"]);
        Assert.Equal("Afropop", aliases["Afro-Pop"]);
        // The block list has no such fold: it has always been purely the user's.
        Assert.Equal(new[] { "x", "y" }, reloaded.GenreTagBlockList);
    }

    [Fact]
    public async Task AnUnsetBlockListFallsBackToTheDefault()
    {
        var store = CreateStore();
        await store.SaveSettingsAsync(new PersonalGenreSettings(Enabled: true));

        var reloaded = await store.GetSettingsAsync();
        Assert.Null(reloaded.GenreTagBlockList);
        Assert.Equal(GenreTagAliasNormalizer.DefaultBlockedGenres, reloaded.EffectiveBlockList);
    }

    [Fact]
    public async Task AnExplicitlyEmptyBlockListStaysEmpty()
    {
        var store = CreateStore();
        await store.SaveSettingsAsync(new PersonalGenreSettings(Enabled: true, GenreTagBlockList: []));

        var reloaded = await store.GetSettingsAsync();
        Assert.Empty(reloaded.GenreTagBlockList!);
        Assert.Empty(reloaded.EffectiveBlockList);
    }

    /// <summary>
    /// A save that does not mention the normalization fields leaves them alone.
    ///
    /// This drives the real controller action rather than restating its merge, so
    /// a change to that merge cannot leave the test green against a copy. The
    /// failure it guards against is quiet: the settings API takes one object for
    /// two unrelated features, so a client that only means to change the genre
    /// count sends no normalization fields, and binding those onto a fully
    /// populated record would read their defaults as instructions — switching the
    /// toggle off and resetting the rules through an unrelated edit.
    /// </summary>
    [Fact]
    public async Task APartialSaveDoesNotDiscardTheNormalizationValues()
    {
        var store = CreateStore();
        await store.SaveSettingsAsync(new PersonalGenreSettings(
            Enabled: true,
            MaxGenres: 3,
            NormalizeGenreTags: true,
            GenreTagAliasRules: [new PersonalGenreAliasRule("Drill", "Trap")],
            GenreTagBlockList: ["Misc"]));

        var controller = CreateController(store);
        // A client that sends only the resolver fields.
        var result = await controller.UpdateSettings(
            new PersonalGenreSettingsRequest { Enabled = true, MaxGenres = 6 }, CancellationToken.None);

        var returned = ReadResponse(result);
        Assert.Equal(6, returned.MaxGenres);
        Assert.True(returned.NormalizeGenreTags);
        var aliases = returned.GenreTagAliasRules!
            .ToDictionary(rule => rule.Alias, rule => rule.Canonical);
        Assert.Equal("Trap", aliases["Drill"]);
        Assert.Equal(new[] { "Misc" }, returned.GenreTagBlockList);

        // And the same is true of what was persisted, not just what was returned.
        var stored = await store.GetSettingsAsync();
        Assert.True(stored.NormalizeGenreTags);
        Assert.Equal(new[] { "Misc" }, stored.GenreTagBlockList);
    }

    /// <summary>
    /// A full save still writes every value, so the merge does not freeze the
    /// normalization fields against the user's own edits.
    /// </summary>
    [Fact]
    public async Task AFullSaveWritesEveryNormalizationValue()
    {
        var store = CreateStore();
        await store.SaveSettingsAsync(new PersonalGenreSettings(Enabled: true, MaxGenres: 3));
        var controller = CreateController(store);

        var result = await controller.UpdateSettings(new PersonalGenreSettingsRequest
        {
            Enabled = true,
            MaxGenres = 3,
            NormalizeGenreTags = true,
            GenreTagAliasRules = [new PersonalGenreAliasRule("Drill", "Trap")],
            GenreTagBlockList = ["Misc"]
        }, CancellationToken.None);

        var returned = ReadResponse(result);
        Assert.True(returned.NormalizeGenreTags);
        Assert.Equal(new[] { "Misc" }, returned.GenreTagBlockList);
        var stored = await store.GetSettingsAsync();
        Assert.True(stored.NormalizeGenreTags);
        Assert.Equal(new[] { "Misc" }, stored.GenreTagBlockList);
        var aliases = stored.GenreTagAliasRules!
            .ToDictionary(rule => rule.Alias, rule => rule.Canonical);
        Assert.Equal("Trap", aliases["Drill"]);
    }

    private PersonalGenreApiController CreateController(PersonalGenreStore store)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Library"] = "Data Source=" + Path.Combine(_directory, "library.db")
        }).Build();
        var repository = new DeezSpoTag.Services.Library.LibraryRepository(
            configuration, NullLogger<DeezSpoTag.Services.Library.LibraryRepository>.Instance);
        var service = new DeezSpoTag.Web.Services.PersonalGenreService(
            store, repository, NullLogger<DeezSpoTag.Web.Services.PersonalGenreService>.Instance,
            CreateProvider(store));
        return new PersonalGenreApiController(service);
    }

    /// <summary>
    /// Reads the settings the action returned through the wire shape rather than
    /// the record, so this also covers what a client actually receives.
    /// </summary>
    private static SettingsResponse ReadResponse(Microsoft.AspNetCore.Mvc.IActionResult result)
    {
        var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        return System.Text.Json.JsonSerializer.Deserialize<SettingsResponse>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private sealed class SettingsResponse
    {
        public bool Enabled { get; set; }
        public int MaxGenres { get; set; }
        public bool PreserveUnmappedTags { get; set; }
        public bool IncludeParentGenres { get; set; }
        public bool NormalizeGenreTags { get; set; }
        public List<PersonalGenreAliasRule>? GenreTagAliasRules { get; set; }
        public List<string>? GenreTagBlockList { get; set; }
    }

    /// <summary>
    /// The request type must be able to express "not supplied" for every field,
    /// including the toggle. A non-nullable binding would read the absent
    /// boolean as an instruction to switch normalization off.
    /// </summary>
    [Fact]
    public void AnEmptyRequestChangesNothing()
    {
        var request = new PersonalGenreSettingsRequest();
        Assert.Null(request.Enabled);
        Assert.Null(request.MaxGenres);
        Assert.Null(request.PreserveUnmappedTags);
        Assert.Null(request.IncludeParentGenres);
        Assert.Null(request.NormalizeGenreTags);
        Assert.Null(request.GenreTagAliasRules);
        Assert.Null(request.GenreTagBlockList);
    }

    /// <summary>
    /// An explicitly emptied list is an instruction, not an omission, so it is
    /// honoured rather than filled back in from the stored value.
    /// </summary>
    [Fact]
    public async Task APartialSaveStillHonoursAnExplicitlyEmptyList()
    {
        var store = CreateStore();
        await store.SaveSettingsAsync(new PersonalGenreSettings(
            Enabled: true, GenreTagBlockList: ["Misc"]));

        await store.SaveSettingsAsync(new PersonalGenreSettings(Enabled: true, GenreTagBlockList: []));

        var after = await store.GetSettingsAsync();
        Assert.Empty(after.GenreTagBlockList!);
    }

    /// <summary>
    /// An emptied list survives a restart.
    ///
    /// This is the case an in-process test cannot catch. The schema migration runs
    /// on every startup, and an earlier version normalised an empty stored list
    /// back to NULL on each one, which handed the user the default block list again
    /// every time they launched the app. Emptiness is a durable user decision, so
    /// the assertion has to reopen the database, not re-read the same handle.
    /// </summary>
    [Fact]
    public async Task AnEmptiedBlockListSurvivesAReopen()
    {
        var path = Path.Combine(_directory, "restart.db");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Library"] = "Data Source=" + path
        }).Build();

        var first = new PersonalGenreStore(configuration);
        await first.SaveSettingsAsync(new PersonalGenreSettings(Enabled: true, GenreTagBlockList: []));

        // Reopen: a fresh handle, and the schema migration runs again, exactly as
        // it does on the next application start.
        var second = new PersonalGenreStore(configuration);
        await second.GetSettingsAsync();

        var after = await second.GetSettingsAsync();
        Assert.NotNull(after.GenreTagBlockList);
        Assert.Empty(after.GenreTagBlockList!);
        Assert.Empty(after.EffectiveBlockList);
    }

    /// <summary>
    /// A configured block list also survives a reopen, unchanged.
    /// </summary>
    [Fact]
    public async Task AConfiguredBlockListSurvivesAReopen()
    {
        var path = Path.Combine(_directory, "restart-configured.db");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Library"] = "Data Source=" + path
        }).Build();

        var first = new PersonalGenreStore(configuration);
        await first.SaveSettingsAsync(new PersonalGenreSettings(Enabled: true, GenreTagBlockList: ["Misc", "Various"]));

        var second = new PersonalGenreStore(configuration);
        await second.GetSettingsAsync();

        var after = await second.GetSettingsAsync();
        Assert.Equal(new[] { "Misc", "Various" }, after.GenreTagBlockList);
    }

    /// <summary>
    /// A host with no Genre Intelligence store still honours the configuration file.
    ///
    /// The worker and API hosts have no library database, so they never get the
    /// provider and never get migrated. Falling back to the shipped defaults there
    /// would quietly switch normalization off for a user who had it on, in exactly
    /// the hosts that write tags.
    /// </summary>
    [Fact]
    public void AHostWithoutTheStoreFallsBackToTheConfigurationFile()
    {
        var root = Path.Combine(_directory, "no-store");
        var configFolder = Path.Combine(root, "deezspotag");
        Directory.CreateDirectory(configFolder);
        File.WriteAllText(Path.Combine(configFolder, "config.json"), """
        {
          "NormalizeGenreTags": true,
          "GenreTagAliasRules": [ { "Alias": "Hip Hop", "Canonical": "HipHop" } ],
          "GenreTagBlockList": [ "other", "Misc" ]
        }
        """);

        var previous = Environment.GetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR", root);
            // Constructed with no provider, which is how those hosts get it.
            var service = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);

            var settings = service.LoadSettings();

            Assert.True(settings.GenreNormalization.Enabled);
            Assert.Equal(
                "HipHop",
                settings.GenreNormalization.AliasMap[GenreTagAliasNormalizer.ToLookupKey("Hip Hop")]);
            Assert.Equal(new[] { "other", "Misc" }, settings.GenreNormalization.BlockList);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR", previous);
        }
    }

    [Fact]
    public void TheSnapshotPreparesTheAliasLookupFromGenreIntelligenceValues()
    {
        var snapshot = GenreNormalizationSnapshot.Create(
            enabled: true,
            rules: [new PersonalGenreAliasRule("Hip Hop", "HipHop")],
            blockList: ["other"]);

        Assert.True(snapshot.Enabled);
        Assert.Equal("HipHop", snapshot.AliasMap[GenreTagAliasNormalizer.ToLookupKey("Hip Hop")]);
        Assert.Equal(new[] { "other" }, snapshot.BlockList);
    }

    [Fact]
    public void TheSnapshotIsInertWhenNormalizationIsOff()
    {
        var snapshot = GenreNormalizationSnapshot.Create(
            enabled: false,
            rules: [new PersonalGenreAliasRule("Hip Hop", "HipHop")],
            blockList: ["other"]);

        // The alias map is empty so nothing is rewritten, but the block list still
        // applies: a blocked value was never written, toggle or not.
        Assert.False(snapshot.Enabled);
        Assert.Empty(snapshot.AliasMap);
        Assert.Equal(new[] { "other" }, snapshot.BlockList);
    }

    [Fact]
    public async Task SavingThroughGenreIntelligenceRefreshesTheProviderCache()
    {
        var store = CreateStore();
        var provider = CreateProvider(store);
        var repository = new DeezSpoTag.Services.Library.LibraryRepository(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = "Data Source=" + Path.Combine(_directory, "library.db")
            }).Build(),
            NullLogger<DeezSpoTag.Services.Library.LibraryRepository>.Instance);
        var service = new DeezSpoTag.Web.Services.PersonalGenreService(
            store, repository, NullLogger<DeezSpoTag.Web.Services.PersonalGenreService>.Instance, provider);

        // Nothing has read the store yet, so the cache still holds the shipped
        // defaults, which have normalization off.
        Assert.False(provider.IsLoaded);
        Assert.False(provider.Current.Enabled);

        await service.SaveSettingsAsync(new PersonalGenreSettings(
            Enabled: true, NormalizeGenreTags: true, GenreTagBlockList: ["blocked"]));

        // A hot-path consumer must see the new value without a restart.
        Assert.True(provider.IsLoaded);
        Assert.True(provider.Current.Enabled);
        Assert.Equal(new[] { "blocked" }, provider.Current.BlockList);
    }

    /// <summary>
    /// Reading the cache never waits on the database, and before the first load it
    /// reports the shipped defaults rather than blocking the caller.
    ///
    /// This matters because the readers are synchronous download and tag-write
    /// paths. A blocking read there would be a stall in the middle of tagging a
    /// file, and the guardrail suite rejects that shape outright.
    /// </summary>
    [Fact]
    public void ReadingTheCacheBeforeItIsLoadedReturnsTheDefaultsWithoutBlocking()
    {
        var provider = CreateProvider(CreateStore());

        Assert.False(provider.IsLoaded);
        Assert.False(provider.Current.Enabled);
        Assert.Equal(GenreTagAliasNormalizer.DefaultBlockedGenres, provider.Current.BlockList);
        // Reading again must not have quietly marked it loaded either.
        Assert.False(provider.IsLoaded);
    }
}
