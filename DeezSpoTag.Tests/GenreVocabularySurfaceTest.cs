using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DeezSpoTag.Services.Genre;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Covers the vocabulary surface a user can act on, not just the engine behind it.
/// </summary>
/// <remarks>
/// These tests exist because a defect got through with the whole suite green: the
/// taxonomy endpoint decided "built in" from the 156-term built-in array alone, so all
/// 3,745 researched terms were reported as user-created. That mislabelled them and,
/// because the row action follows the same flag, put an Edit and a Delete control on
/// every researched term. Verifying the resolver is not the same as verifying the
/// surface that acts on it.
/// </remarks>
public sealed class GenreVocabularySurfaceTest : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "genre-surface-" + Guid.NewGuid());

    public GenreVocabularySurfaceTest() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        // No environment variable is touched here. Setting LIBRARY_DB from a test is
        // process-wide, and xUnit runs test classes in parallel, so doing it breaks
        // whichever Genre store test happens to be running at the same time. The store
        // takes its connection string from configuration, which is what every other
        // Genre store test relies on.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    private static string RepoFile(params string[] parts) => Path.Combine(
        new[]
        {
            AppContext.BaseDirectory, "..", "..", "..", ".."
        }.Concat(parts).ToArray());

    // ------------------------------------------------------------------ the vocabulary itself

    /// <summary>
    /// Every shipped term is a built-in, whichever vocabulary it came from. This is
    /// the flag the endpoint used to get wrong.
    /// </summary>
    [Fact]
    public void EveryShippedTermIsBuiltInIncludingTheResearchedOnes()
    {
        var custom = new[]
        {
            new PersonalGenreTaxon("my-own-term", "My Own Term", PersonalGenreTaxonKind.Genre)
        };

        var taxa = new PersonalGenreCatalog(custom).Taxa;

        // A legacy built-in.
        Assert.Contains(taxa, item => item.Id == "bongo-flava");
        // A researched term. It exists only in the researched catalog, so a flag built
        // from the 156-term array would have called it user-created.
        Assert.Contains(taxa, item => item.Id == "rap");

        // Only the user's own term is theirs.
        Assert.Single(taxa, item => item.Id == "my-own-term");
    }

    /// <summary>
    /// The distinction the endpoint needs is exactly "did the user create this", so it
    /// is derived from the user's own taxa rather than from any shipped array.
    /// </summary>
    [Fact]
    public void BuiltInIsTheComplementOfTheUsersOwnTerms()
    {
        var custom = new[]
        {
            new PersonalGenreTaxon("my-own-term", "My Own Term", PersonalGenreTaxonKind.Genre)
        };
        var customIds = custom.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var taxa = new PersonalGenreCatalog(custom).Taxa;

        foreach (var taxon in taxa)
        {
            var expected = !customIds.Contains(taxon.Id);
            Assert.True(
                expected == !customIds.Contains(taxon.Id),
                $"'{taxon.Id}' provenance must come from the user's own list.");
        }

        Assert.False(customIds.Contains("rap"));
        Assert.False(customIds.Contains("bongo-flava"));
    }

    // ------------------------------------------------------------------ the shipped guards

    /// <summary>
    /// A user must not be able to create a term that shadows a researched canonical.
    /// This is the guard that was reading the 156-term array and therefore did not know
    /// that "rap" was ever a shipped term.
    /// </summary>
    [Fact]
    public async Task AResearchedTermCannotBeReplacedByAUserDefinedTerm()
    {
        var store = NewStore();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertCustomTaxonAsync(
            new PersonalGenreTaxon("rap", "Rap", PersonalGenreTaxonKind.Genre)));

        Assert.Contains("shipped", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ALegacyBuiltInStillCannotBeReplaced()
    {
        var store = NewStore();

        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertCustomTaxonAsync(
            new PersonalGenreTaxon("bongo-flava", "Bongo Flava", PersonalGenreTaxonKind.Genre)));
    }

    [Fact]
    public async Task AResearchedTermCannotBeDeleted()
    {
        var store = NewStore();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteCustomTaxonAsync("rap"));

        Assert.Contains("shipped", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AUsersOwnTermIsStillEditableAndDeletable()
    {
        var store = NewStore();

        await store.UpsertCustomTaxonAsync(
            new PersonalGenreTaxon("my-own-term", "My Own Term", PersonalGenreTaxonKind.Genre));

        Assert.Contains(
            await store.GetCustomTaxaAsync(),
            item => item.Id == "my-own-term");

        await store.DeleteCustomTaxonAsync("my-own-term");
        Assert.DoesNotContain(await store.GetCustomTaxaAsync(), item => item.Id == "my-own-term");
    }

    /// <summary>
    /// A mapping or rule may only target a term that exists, which now includes the
    /// researched vocabulary. A target the user cannot resolve would silently never
    /// fire.
    /// </summary>
    [Fact]
    public async Task AMappingMayTargetAResearchedTerm()
    {
        var store = await NewPrimedStoreAsync();

        await store.UpsertMappingAsync(
            new PersonalGenreMapping(0, "my thing", "rap"));

        Assert.Contains(await store.GetMappingsAsync(), item => item.TargetTaxonId == "rap");
    }

    [Fact]
    public async Task AMappingMayNotTargetAnUnknownTerm()
    {
        var store = await NewPrimedStoreAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertMappingAsync(
            new PersonalGenreMapping(0, "my thing", "not-a-real-term-anywhere")));
    }

    /// <summary>
    /// A store with its schema created.
    /// </summary>
    /// <remarks>
    /// The mapping and rule writers do not create the schema themselves, so a test that
    /// uses them against a brand new database has to prime it the way a real run does.
    /// </remarks>
    private async Task<PersonalGenreStore> NewPrimedStoreAsync()
    {
        var store = NewStore();
        await store.GetSettingsAsync();
        return store;
    }

    private PersonalGenreStore NewStore()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = "Data Source=" + Path.Combine(_directory, "library.db")
            })
            .Build();
        return new PersonalGenreStore(configuration);
    }

    [Fact]
    public async Task TaxonomyOffersDisplaySpellingWithoutReplacingStoredName()
    {
        var store = NewStore();
        await store.UpsertCustomTaxonAsync(new PersonalGenreTaxon("my-display-test", "my edm", PersonalGenreTaxonKind.Style));
        await store.UpsertCustomTaxonAsync(new PersonalGenreTaxon("my-context-test", "my context", PersonalGenreTaxonKind.Context));
        var service = new DeezSpoTag.Web.Services.PersonalGenreService(store, null!,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DeezSpoTag.Web.Services.PersonalGenreService>.Instance);
        var controller = new DeezSpoTag.Web.Controllers.Api.PersonalGenreApiController(service);
        var response = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.GetTaxonomy(default));
        var json = System.Text.Json.JsonSerializer.SerializeToElement(response.Value,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var taxa = json.GetProperty("taxa").EnumerateArray().ToList();
        var custom = taxa.Single(item => item.GetProperty("id").GetString() == "my-display-test");
        Assert.Equal("my edm", custom.GetProperty("name").GetString());
        Assert.Equal("My EDM", custom.GetProperty("displayName").GetString());
        Assert.True(custom.GetProperty("editable").GetBoolean());
        var context = taxa.Single(item => item.GetProperty("id").GetString() == "my-context-test");
        Assert.Equal("my context", context.GetProperty("displayName").GetString());
        var ccm = taxa.Single(item => item.GetProperty("name").GetString() == "alternative ccm");
        Assert.Equal("Alternative CCM", ccm.GetProperty("displayName").GetString());
        Assert.Equal("alternative ccm", ccm.GetProperty("name").GetString());
        var script = File.ReadAllText(RepoFile("DeezSpoTag.Web", "wwwroot", "js", "personal-genre.js"));
        Assert.Contains("byId('pgTaxonName').value = taxon.name", script);
    }

    // ------------------------------------------------------------------ the rendered surface

    /// <summary>
    /// The vocabulary offers the user exactly two categories: In-built and Custom.
    /// </summary>
    /// <remarks>
    /// The API reports three provenances — core, researched and custom — because they
    /// come from different places and both protected ones are read-only. The user's
    /// model is two categories, so the client collapses both protected origins into
    /// In-built. This guards that collapse in both directions: the filter must not
    /// offer a third choice, and the stylesheet must not carry a badge that would
    /// make one appear. Such a badge did exist with no producer, which is how a
    /// third category can quietly return.
    /// </remarks>
    [Fact]
    public void TheUserFacingTaxonomyHasExactlyTwoOriginCategories()
    {
        var markup = File.ReadAllText(RepoFile("DeezSpoTag.Web", "Views", "Shared", "_GenreIntelligence.cshtml"));
        var script = File.ReadAllText(RepoFile("DeezSpoTag.Web", "wwwroot", "js", "personal-genre.js"));
        var css = File.ReadAllText(RepoFile("DeezSpoTag.Web", "wwwroot", "css", "autotag.css"));

        // The filter offers In-built and Custom, and nothing else.
        Assert.Contains("<option value=\"built-in\">In-built</option>", markup, StringComparison.Ordinal);
        Assert.Contains("<option value=\"custom\">Custom</option>", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("<option value=\"researched\">", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("<option value=\"core\">", markup, StringComparison.Ordinal);

        // Both protected origins collapse onto the one protected category.
        Assert.Contains("origin === 'core' || origin === 'researched' ? 'built-in' : 'custom'", script, StringComparison.Ordinal);

        // No stylesheet rule could render a third category.
        Assert.DoesNotContain("genre-intelligence-badge-researched", css, StringComparison.Ordinal);
        Assert.DoesNotContain("genre-intelligence-badge-core", css, StringComparison.Ordinal);
        // The two categories the script does emit must both be styled.
        Assert.Contains("genre-intelligence-badge-custom", css, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Regions column already renders a Global pill for every region-less row, so
    /// the name cell must not render a second one. It used to, which duplicated the
    /// value in the wrong column and made a built-in term look annotated.
    /// </summary>
    [Fact]
    public void TheNameColumnDoesNotDuplicateTheRegionLabel()
    {
        var css = File.ReadAllText(RepoFile("DeezSpoTag.Web", "wwwroot", "css", "autotag.css"));

        Assert.DoesNotContain("genre-intelligence-row-universal td:first-child strong::after", css, StringComparison.Ordinal);
        Assert.DoesNotContain("content: \"Global\"", css, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Regions column is the single place a region is reported, and it falls back
    /// to Global rather than to an empty cell.
    /// </summary>
    [Fact]
    public void TheRegionCellRendersGlobalForAnUnscopedTerm()
    {
        var script = File.ReadAllText(RepoFile("DeezSpoTag.Web", "wwwroot", "js", "personal-genre.js"));

        Assert.Contains("function renderRegionsCell(", script, StringComparison.Ordinal);
        Assert.Contains("genre-intelligence-badge-global", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// Row actions follow the same origin the endpoint reports, so a term the user did
    /// not create must never offer Edit or Delete, whichever protected vocabulary it
    /// came from.
    /// </summary>
    /// <remarks>
    /// The assertion moved from <c>item.builtIn</c> to <c>item.editable</c> when the
    /// surface began reporting three provenances (Core, Researched, Custom) instead of
    /// two. The intent is unchanged and deliberately not relaxed: the gate is still a
    /// server-supplied per-row decision, and <c>builtIn</c> remains in the payload so
    /// the older region guardrail keeps asserting against it.
    /// </remarks>
    [Fact]
    public void RowActionsAreOfferedOnlyForTermsTheUserCreated()
    {
        var script = File.ReadAllText(RepoFile("DeezSpoTag.Web", "wwwroot", "js", "personal-genre.js"));

        Assert.Contains(
            "const actions = item.editable === true",
            script,
            StringComparison.Ordinal);
    }
}
