using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using DeezSpoTag.Services.Genre;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Parent edges from the verified Global parentage batch, and the guarantees
/// that hierarchy does not become a second Genre Intelligence.
/// </summary>
public sealed class GenreTaxonomyHierarchyTest : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("genre-hierarchy-").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    [Fact]
    public void VerifiedMultiParentStyleKeepsBothParents()
    {
        Assert.True(ResearchedGenreCatalog.Current.TryGetById("folk-pop", out var researched));
        Assert.Equal(["folk", "pop"], researched.ParentIds);

        var catalog = new PersonalGenreCatalog();
        Assert.True(catalog.TryGetById("folk-pop", out var runtime));
        Assert.Equal(["folk", "pop"], runtime.ParentIds);
    }

    [Fact]
    public void ResearchedParentIdsAreTranslatedToRetainedBuiltInRuntimeIds()
    {
        // The audited workbook names this Genre rnb, which is also the built-in
        // runtime id. The older researched id r-b was the same term and was not
        // kept beside it.
        Assert.True(ResearchedGenreCatalog.Current.TryGetById("adult-contemporary-r-b", out var researched));
        Assert.Equal(["rnb"], researched.ParentIds);

        var catalog = new PersonalGenreCatalog();
        Assert.True(catalog.TryGetById("rnb", out var rnb));
        Assert.Equal(PersonalGenreTaxonKind.Genre, rnb.Kind);
        Assert.True(catalog.TryGetById("adult-contemporary-r-b", out var child));
        Assert.Equal(["rnb"], child.ParentIds);
        Assert.False(catalog.TryGetById("r-b", out _));
    }

    [Fact]
    public void UnapprovedParentageDoesNotInventAParent()
    {
        var catalog = new PersonalGenreCatalog();
        Assert.True(catalog.TryGetById("aboriginal", out var noSafe));
        Assert.True(catalog.TryGetById("alternative-pop-rock", out var recheck));
        Assert.Empty(noSafe.ParentIds ?? []);
        Assert.Equal(["pop", "rock"], recheck.ParentIds);
        // hypertechno is the reconciled spelling of hyper techno. The verified
        // parentage row used the alias id, so it was excluded instead of splitting
        // that identity or attaching the EDM parent.
        Assert.True(ResearchedGenreCatalog.Current.TryMatch("hypertechno", out var hyper, out _));
        Assert.Equal("hyper-techno", hyper.Id);
        Assert.Equal(["techno"], hyper.ParentIds);

        // v17 left this style unverified. Recognition stays; no parent is invented.
        Assert.True(catalog.TryGetById("industrial-dance", out var held));
        Assert.Equal(PersonalGenreTaxonKind.Style, held.Kind);
        Assert.Equal(["industrial", "electronic"], held.ParentIds);
    }

    [Fact]
    public void VerifiedV17StylesGainParentsWithoutInventingInfluences()
    {
        var catalog = new PersonalGenreCatalog();
        Assert.True(catalog.TryGetById("industrial-metal", out var metal));
        Assert.Equal(["industrial", "metal"], metal.ParentIds);
        Assert.True(catalog.TryGetById("jazz-funk", out var jazzFunk));
        Assert.Equal(["jazz", "funk"], jazzFunk.ParentIds);
        Assert.True(catalog.TryGetById("jazz-rock", out var jazzRock));
        Assert.Equal(["jazz", "rock"], jazzRock.ParentIds);

        var observed = new[] { new GenreTagObservation("industrial metal", PersonalGenreTaxonKind.Style) };
        var off = PersonalGenreResolver.Resolve(observed, settings: new PersonalGenreSettings(IncludeParentGenres: false));
        Assert.Equal(["industrial metal"], off.Styles);
        Assert.Empty(off.Genres);

        var on = PersonalGenreResolver.Resolve(observed, settings: new PersonalGenreSettings(IncludeParentGenres: true));
        Assert.Equal(["industrial metal"], on.Styles);
        Assert.Equal(
            ["industrial", "metal"],
            on.Classifications.Where(item => item.Status == "derived_parent").Select(item => item.TaxonId));
    }

    [Fact]
    public void VerifiedV26StylesAddParentsWithoutRemovingEarlierEdges()
    {
        var catalog = new PersonalGenreCatalog();
        Assert.True(catalog.TryGetById("pop-rock", out var popRock));
        Assert.Equal(["pop", "rock"], popRock.ParentIds);
        Assert.True(catalog.TryGetById("trip-hop", out var tripHop));
        // The Europe audit classifies Trip-Hop as a Genre. The runtime catalog
        // keeps the built-in hip-hop parent and no longer adds electronic.
        Assert.Equal("Genre", tripHop.Kind.ToString());
        Assert.Equal(["hip-hop"], tripHop.ParentIds);
        Assert.True(catalog.TryGetById("industrial-metal", out var earlier));
        Assert.Equal(["industrial", "metal"], earlier.ParentIds);

        var on = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("trip hop", PersonalGenreTaxonKind.Style)],
            settings: new PersonalGenreSettings(IncludeParentGenres: true));
        Assert.Equal(["Trip-Hop"], on.Genres);
        // A Style observation that resolves to the Genre does not invent a parent.
        Assert.Empty(on.Classifications.Where(item => item.Status == "derived_parent"));
    }

    [Fact]
    public void VerifiedAfricaStylesAddGlobalCompletionsWithoutRegionalParents()
    {
        var catalog = new PersonalGenreCatalog();
        Assert.True(catalog.TryGetById("afropop", out var afropop));
        Assert.Equal(["afrobeats", "pop"], afropop.ParentIds);
        Assert.True(catalog.TryGetById("afropiano", out var afropiano));
        Assert.Equal(["house", "pop"], afropiano.ParentIds);
        Assert.True(catalog.TryGetById("ethio-jazz", out var ethioJazz));
        Assert.Equal(["jazz"], ethioJazz.ParentIds);
        Assert.True(catalog.TryGetById("azmari", out var azmari));
        Assert.Equal(["ethiopic-music"], azmari.ParentIds);
        Assert.True(catalog.TryGetById("motswako", out var motswako));
        Assert.Equal(["hip-hop"], motswako.ParentIds);
    }

    [Fact]
    public void VerifiedLatinAmericaCaribbeanStylesAddGlobalCompletionsWithoutRegionalParents()
    {
        var catalog = new PersonalGenreCatalog();
        Assert.True(catalog.TryGetById("latin-jazz", out var latinJazz));
        Assert.Equal(["jazz"], latinJazz.ParentIds);
        Assert.True(catalog.TryGetById("samba-jazz", out var sambaJazz));
        Assert.Equal(["samba"], sambaJazz.ParentIds);
        Assert.True(catalog.TryGetById("reggae-panameño", out var reggaePanameno));
        Assert.Equal(["reggae"], reggaePanameno.ParentIds);
        Assert.True(catalog.TryGetById("rock-en-español", out var rockEnEspanol));
        Assert.Equal(["rock"], rockEnEspanol.ParentIds);
        Assert.True(catalog.TryGetById("moombahton", out var moombahton));
        Assert.Equal(["electronic-dance-music"], moombahton.ParentIds);
        Assert.True(catalog.TryGetById("aboio", out var aboio));
        Assert.Empty(aboio.ParentIds ?? []);
        Assert.True(catalog.TryGetById("afropop", out var afropop));
        Assert.Equal(["afrobeats", "pop"], afropop.ParentIds);
    }

    [Fact]
    public void BuiltInParentsSurviveAndAreNotDuplicated()
    {
        var catalog = new PersonalGenreCatalog();
        Assert.True(catalog.TryGetById("boom-bap", out var boomBap));
        Assert.Equal(["hip-hop"], boomBap.ParentIds);
        Assert.True(catalog.TryGetById("kenyan-drill", out var kenyan));
        Assert.Equal(["hip-hop"], kenyan.ParentIds);
        Assert.True(catalog.TryGetById("bongo-flava", out var bongo));
        Assert.Equal("bongo-flava", bongo.Id);
    }

    [Fact]
    public void IncludeParentGenresIsOffByDefaultAndOnWalksEveryVerifiedParent()
    {
        var observed = new[] { new GenreTagObservation("folk pop", PersonalGenreTaxonKind.Style) };

        var off = PersonalGenreResolver.Resolve(observed, settings: new PersonalGenreSettings(IncludeParentGenres: false));
        Assert.Equal(["folk pop"], off.Styles);
        Assert.Empty(off.Genres);

        var on = PersonalGenreResolver.Resolve(observed, settings: new PersonalGenreSettings(IncludeParentGenres: true));
        Assert.Equal(["folk pop"], on.Styles);
        Assert.Equal(["Folk", "Pop"], on.Genres);
        Assert.Equal(2, on.Classifications.Count(item => item.Status == "derived_parent"));
    }

    [Fact]
    public void ParentExpansionWalksAMultiLevelChainOncePerAncestor()
    {
        // Level A -> Level B -> Folk. Level B is a style, so only the genre
        // ancestor is emitted, and it is emitted once. The same walk serves
        // built-in and researched parents.
        PersonalGenreTaxon[] custom =
        [
            new("level-a", "Level A", PersonalGenreTaxonKind.Style, ParentIds: ["level-b"]),
            new("level-b", "Level B", PersonalGenreTaxonKind.Style, ParentIds: ["folk"])
        ];
        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Level A", PersonalGenreTaxonKind.Style)],
            customTaxa: custom,
            settings: new PersonalGenreSettings(IncludeParentGenres: true));

        Assert.Equal(["Level A"], resolution.Styles);
        Assert.Equal(["Folk"], resolution.Genres);
        Assert.DoesNotContain("Level B", resolution.Genres);
        Assert.DoesNotContain("Level B", resolution.Styles);
    }

    [Fact]
    public void ADiamondHierarchyEmitsTheSharedAncestorOnce()
    {
        PersonalGenreTaxon[] custom =
        [
            new("diamond-a", "Diamond A", PersonalGenreTaxonKind.Style, ParentIds: ["diamond-b", "diamond-c"]),
            new("diamond-b", "Diamond B", PersonalGenreTaxonKind.Style, ParentIds: ["folk"]),
            new("diamond-c", "Diamond C", PersonalGenreTaxonKind.Style, ParentIds: ["folk"])
        ];

        var resolution = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Diamond A", PersonalGenreTaxonKind.Style)],
            customTaxa: custom,
            settings: new PersonalGenreSettings(IncludeParentGenres: true));

        Assert.Equal(["Diamond A"], resolution.Styles);
        Assert.Equal(["Folk"], resolution.Genres);
    }

    [Fact]
    public void ExplicitGenresStayAheadOfDerivedParentsAndMaxGenresStaysRetired()
    {
        var resolution = PersonalGenreResolver.Resolve(
        [
            new GenreTagObservation("folk pop", PersonalGenreTaxonKind.Style),
            new GenreTagObservation("Folk", PersonalGenreTaxonKind.Genre)
        ],
            settings: new PersonalGenreSettings(MaxGenres: 1, IncludeParentGenres: true));

        Assert.Equal(["folk pop"], resolution.Styles);
        Assert.Equal(["Folk", "Pop"], resolution.Genres);
        Assert.Contains(resolution.Classifications, item => item.TaxonId == "folk" && item.Status == "suggested");
        Assert.Contains(resolution.Classifications, item => item.TaxonId == "pop" && item.Status == "derived_parent");
    }

    [Fact]
    public void ABlockedParentIsNotExpandedAndABlockedStyleStaysBlocked()
    {
        var partial = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("folk pop", PersonalGenreTaxonKind.Style)],
            blockedIdentities: ["Folk"],
            settings: new PersonalGenreSettings(IncludeParentGenres: true));
        Assert.Equal(["folk pop"], partial.Styles);
        Assert.Equal(["Pop"], partial.Genres);
        Assert.DoesNotContain("Folk", partial.Genres);

        var blocked = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("hindie", PersonalGenreTaxonKind.Style)],
            blockedIdentities: ["hindie"]);
        Assert.Empty(blocked.Styles);
        Assert.Contains(blocked.Decisions, decision => decision.Outcome == "blocked");

        var blockedName = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("Indian Indie", PersonalGenreTaxonKind.Style)],
            blockedIdentities: ["Indian Indie"]);
        Assert.Empty(blockedName.Styles);
        Assert.Contains(blockedName.Decisions, decision => decision.Outcome == "blocked");
    }

    [Fact]
    public void AResearchedStyleUsesTheExistingNormalizationPath()
    {
        var snapshot = new GenreSemanticSnapshot(
            [new GenreTagObservation("D-Pop Spelling", PersonalGenreTaxonKind.Style)],
            DateTimeOffset.Parse("2026-10-13T00:00:00Z"));
        var normalization = GenreNormalizationSnapshot.Create(
            true,
            [new PersonalGenreAliasRule("D-Pop Spelling", "dance-pop")],
            []);

        var preprocessed = GenreNormalizationPreprocessor.Apply(snapshot, normalization);
        Assert.Equal("dance-pop", Assert.Single(preprocessed.Observations).RawValue);
        Assert.Equal("D-Pop Spelling", Assert.Single(preprocessed.Observations).OriginalValue);

        var resolution = PersonalGenreResolver.Resolve(preprocessed.Observations.ToArray());
        Assert.Equal(["dance-pop"], resolution.Styles);
        Assert.Contains(resolution.Classifications, item => item.TaxonId == "dance-pop");

        var untouched = PersonalGenreResolver.Resolve(
            [new GenreTagObservation("D-Pop Spelling", PersonalGenreTaxonKind.Style)],
            settings: new PersonalGenreSettings(NormalizeGenreTags: false, PreserveUnmappedTags: true));
        Assert.Empty(untouched.Styles);
        Assert.Contains(untouched.Preserved, item => item.Value == "D-Pop Spelling");
    }

    [Fact]
    public void TheBlockListRemovesAResearchedStyleBeforeClassification()
    {
        var result = GenreNormalizationPreprocessor.Apply(
            new GenreSemanticSnapshot(
                [new GenreTagObservation("dance-pop", PersonalGenreTaxonKind.Style)],
                DateTimeOffset.Parse("2026-10-13T00:00:00Z")),
            GenreNormalizationSnapshot.Create(false, null, ["dance-pop"]));

        Assert.Empty(result.Observations);
        Assert.Equal("dance-pop", Assert.Single(result.Removed).Value);
    }

    [Fact]
    public void CleanupRecognizesAResearchedStyleAndStillPreservesUnknownValues()
    {
        var resolution = PersonalGenreResolver.Resolve(
        [
            new GenreTagObservation("dance-pop", PersonalGenreTaxonKind.Genre),
            new GenreTagObservation("Not A Real Genre Zzq", PersonalGenreTaxonKind.Genre)
        ],
            settings: new PersonalGenreSettings(PreserveUnmappedTags: true));

        Assert.Equal(["dance-pop"], resolution.Styles);
        Assert.Empty(resolution.Genres);
        Assert.Contains(resolution.Preserved, item => item.Value == "Not A Real Genre Zzq");
        Assert.DoesNotContain(resolution.Decisions, decision =>
            decision.RawValue == "Not A Real Genre Zzq" && decision.Outcome == "removed");
    }

    [Fact]
    public void TaxonomyParentsDoNotConstructTheChildStyle()
    {
        var resolution = PersonalGenreResolver.Resolve(
        [
            new GenreTagObservation("Folk", PersonalGenreTaxonKind.Genre),
            new GenreTagObservation("Pop", PersonalGenreTaxonKind.Genre)
        ],
            settings: new PersonalGenreSettings(IncludeParentGenres: true));

        Assert.Equal(["Folk", "Pop"], resolution.Genres);
        Assert.DoesNotContain(resolution.Styles, style => style.Contains("folk pop", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(resolution.Classifications, item => item.TaxonId == "folk-pop");

        var root = JsonNode.Parse(StyleConstructionRuleCatalogTest.TestRulesJson)!;
        var rules = StyleConstructionRuleCatalog.Parse(root.ToJsonString(), new PersonalGenreCatalog());
        var catalog = new PersonalGenreCatalog();
        Assert.True(catalog.TryGetById("hip-hop", out var hipHop));
        var fact = new StyleConstructionSemanticFact
        {
            FactId = "hip-hop",
            Kind = hipHop.Kind,
            CanonicalId = hipHop.Id,
            CanonicalValue = hipHop.Name,
            Scope = ConstructionEvidenceScope.Track,
            Origin = ConstructionEvidenceOrigin.ExplicitFile,
            Source = "audio-file",
            ProvenanceReference = "test-observation:hip-hop",
            OriginalValue = hipHop.Name,
            OriginalField = hipHop.Kind,
            KnownAt = DateTimeOffset.Parse("2026-10-13T00:00:00Z")
        };
        var decision = Assert.Single(StyleConstructionEvaluator.Evaluate(
            new StyleConstructionEvidenceSnapshot(
                1, 42, fact.KnownAt, ResearchedGenreCatalog.Current.Version, PersonalGenreResolver.Version, [fact], new([])),
            rules,
            catalog).Decisions);
        Assert.Equal(StyleConstructionOutcome.Qualified, decision.Outcome);
        Assert.DoesNotContain(decision.EvidenceUsed, used => used.CanonicalId == "folk-pop");
    }

    [Fact]
    public async Task SavedPreferencesSurviveTheTaxonomyExpansion()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Library"] = "Data Source=" + Path.Combine(_directory, "library.db")
        }).Build();
        var store = new PersonalGenreStore(configuration);
        var settings = new PersonalGenreSettings(
            Enabled: false,
            MaxGenres: 7,
            PreserveUnmappedTags: false,
            IncludeParentGenres: true,
            NormalizeGenreTags: true,
            GenreTagAliasRules: [new PersonalGenreAliasRule("D-Pop Spelling", "dance-pop")],
            GenreTagBlockList: ["dance-pop", "hindie"]);
        await store.SaveSettingsAsync(settings);
        await store.UpsertCustomTaxonAsync(new PersonalGenreTaxon(
            "my-kept-style", "My Kept Style", PersonalGenreTaxonKind.Style, ParentIds: ["hip-hop"]));
        await store.UpsertMappingAsync(new PersonalGenreMapping(0, "local bucket", "hip-hop"));
        await store.UpsertRuleAsync(new PersonalGenreRule(0, "local rule", "pop"));
        // Track locks reference track(id). A stub row is enough; the full library schema is not.
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            "Data Source=" + Path.Combine(_directory, "library.db")))
        {
            await connection.OpenAsync();
            await using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS track (id INTEGER PRIMARY KEY);
                INSERT OR IGNORE INTO track (id) VALUES (42);
                """;
            await create.ExecuteNonQueryAsync();
        }

        await store.SaveLockAsync(new PersonalGenreLock(42, "hip-hop"));

        var reloaded = new PersonalGenreStore(configuration);
        var stored = await reloaded.GetSettingsAsync();
        Assert.False(stored.Enabled);
        Assert.Equal(7, stored.MaxGenres);
        Assert.False(stored.PreserveUnmappedTags);
        Assert.True(stored.IncludeParentGenres);
        Assert.True(stored.NormalizeGenreTags);
        Assert.Contains(stored.GenreTagAliasRules!, rule => rule.Alias == "D-Pop Spelling" && rule.Canonical == "dance-pop");
        Assert.Equal(["dance-pop", "hindie"], stored.GenreTagBlockList);

        var custom = Assert.Single(await reloaded.GetCustomTaxaAsync(), taxon => taxon.Id == "my-kept-style");
        Assert.Equal("My Kept Style", custom.Name);
        Assert.Equal(["hip-hop"], custom.ParentIds);
        Assert.Contains(await reloaded.GetMappingsAsync(), mapping =>
            mapping.MatchValue == "local bucket" && mapping.TargetTaxonId == "hip-hop");
        Assert.Contains(await reloaded.GetRulesAsync(), rule =>
            rule.MatchValue == "local rule" && rule.TargetTaxonId == "pop");
        Assert.Contains(await reloaded.GetLocksAsync(42), item => item.TaxonId == "hip-hop" && item.Enabled);
    }
}
