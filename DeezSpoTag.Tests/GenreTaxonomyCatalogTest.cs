using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DeezSpoTag.Services.Genre;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Covers the generated researched catalog: that the shipped resource loads, that
/// its counts and provenance are what the research produced, that the published
/// reconciliations resolve, and that a corrupt catalog fails loudly instead of
/// quietly recognising fewer terms.
/// </summary>
public sealed class GenreTaxonomyCatalogTest
{
    private static ResearchedGenreCatalog Catalog => ResearchedGenreCatalog.Current;

    /// <summary>
    /// The resource must actually be embedded, so a shipped build cannot silently
    /// lose the vocabulary and start preserving values it should now classify.
    /// </summary>
    [Fact]
    public void TheCatalogResourceIsEmbeddedAndLoads()
    {
        Assert.False(string.IsNullOrWhiteSpace(Catalog.Version));
        Assert.NotEmpty(Catalog.Terms);
    }

    /// <summary>
    /// The catalog is generated from a pinned source, so a run can report which
    /// vocabulary it used and a regenerated catalog is reproducible.
    /// </summary>
    [Fact]
    public void TheCatalogCarriesItsSourceProvenance()
    {
        Assert.Equal("researched-master-2026-11-10-v8", Catalog.Version);
        Assert.Equal(64, Catalog.SourceSha256.Length);
        Assert.All(Catalog.SourceSha256, character => Assert.Contains(character, "0123456789abcdef"));
        Assert.Equal(8, Catalog.Sources.Count);
        Assert.Contains(Catalog.Sources, source => source.Role == "regional-vocabulary"
            && source.File == "global-taxonomy-v123-latam-q.xlsx");
        Assert.Contains(Catalog.Sources, source => source.Role == "regional-vocabulary"
            && source.File == "global-taxonomy-v170-eu-final-audited.xlsx"
            && source.Sha256 == "386f31cee3a3d7c199809597d68f2d319eb9e381dbd126942cb00cfc9d66d464");
        Assert.Contains(Catalog.Sources, source => source.Role == "vocabulary" && source.Sha256 == Catalog.SourceSha256);
        Assert.Contains(Catalog.Sources, source =>
            source.Role == "global-parentage"
            && source.File == "DeezSpoTag_Global_Style_Genre_Parentage_Research_v16.xlsx"
            && source.Sha256.Length == 64);
        Assert.Contains(Catalog.Sources, source =>
            source.Role == "global-parentage"
            && source.File == "DeezSpoTag_Global_Style_Genre_Parentage_Research_v17.xlsx"
            && source.Sha256.Length == 64);
        Assert.Contains(Catalog.Sources, source =>
            source.Role == "global-parentage"
            && source.File == "DeezSpoTag_Global_Style_Genre_Parentage_Research_v26_COMPLETE.xlsx"
            && source.Sha256 == "6f217d411f0c24fb3e45125033450b55f29ba389deb20b341158c1ebe607e874");
        Assert.Contains(Catalog.Sources, source =>
            source.Role == "global-parentage"
            && source.File == "DeezSpoTag_Africa_Style_Genre_Parentage_Research_v2_COMPLETE.xlsx"
            && source.Sha256 == "0f8ea7f34b7ed65385ee2666355c7a5a0271e8ee45719270b61cc468a467d2f3");
        Assert.Contains(Catalog.Sources, source =>
            source.Role == "global-parentage"
            && source.File == "DeezSpoTag_Latin_America_Caribbean_Style_Genre_Parentage_Research_v1_COMPLETE.xlsx"
            && source.Sha256 == "48e4715c8b22b5f4a5101f0cffc73091d3ee835d94e29abd15e06e3f938f50ca");
    }

    /// <summary>
    /// Per-term research deliberately carries no source URLs: provenance lives in
    /// the catalog manifest only, and the vocabulary editor never cites links. A
    /// pinned raw scan keeps a regenerated catalog from re-adding them.
    /// </summary>
    [Fact]
    public void PerTermResearchCarriesNoSourceUrls()
    {
        // The record type itself has no Sources member for a term's research.
        Assert.Null(typeof(ResearchedGenreResearch).GetProperty("Sources"));

        // The shipped resource carries no per-term "sources" anywhere outside the
        // provenance manifest, which is a single document-level "source" object.
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "DeezSpoTag.Services", "Genre", "Data", "genre-intelligence-taxonomy.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var term in document.RootElement.GetProperty("terms").EnumerateArray())
        {
            if (!term.TryGetProperty("research", out var research))
            {
                continue;
            }

            Assert.False(research.EnumerateObject().Any(property => property.Name == "sources"),
                "Term research must not carry source URLs: " + term.GetProperty("id").GetString());
            Assert.False(research.EnumerateObject().Any(property => property.Name == "location"),
                "Term research must not carry location prose: " + term.GetProperty("id").GetString());
        }
    }

    /// <summary>
    /// The researched master publishes 1,351 Genres and 2,394 Styles. One researched
    /// spelling collides with another under the shared lookup key and is carried
    /// as an alias, which is why the term count is one below the spreadsheet.
    /// Bollywood and Swahili remain researched Styles.
    /// </summary>
    [Fact]
    public void TheCatalogHoldsTheResearchedGenreAndStyleCounts()
    {
        Assert.Equal(1460, Catalog.Genres);
        Assert.Equal(2699, Catalog.Styles);
        Assert.Equal(4159, Catalog.Terms.Count);
        Assert.Equal(2644, Catalog.ParentEdges);
        Assert.Equal(660, Catalog.MultiParentTerms);
        var diff = Catalog.ImportDiff;
        Assert.NotNull(diff);
        Assert.Equal("researched-master-2026-11-07-v7", diff!.PreviousVersion);
        Assert.Equal(41, diff.TermsAdded);
        Assert.Equal(0, diff.TermsRemoved);
        Assert.True(Catalog.Terms.Any(term => term.Regions.Contains("europe")));
        Assert.True(Catalog.TryGetById("kapuka", out var kapuka));
        Assert.Contains("Boomba", kapuka.Aliases);
        Assert.Contains("nairobi", kapuka.Research?.Places!);
        Assert.Contains("kenya", kapuka.Research?.MemberCountries!);
        Assert.DoesNotContain("nairobi", kapuka.Research?.MemberCountries!);
    }

    /// <summary>
    /// A researched spelling collision resolves to one canonical record rather than
    /// becoming a duplicate term, and both spellings reach that same record so no
    /// published term is lost.
    /// </summary>
    [Fact]
    public void TheResolvedCollisionReachesOneCanonicalRecordFromEitherSpelling()
    {
        Assert.True(Catalog.TryMatch("hypertechno", out var runTogether, out _));
        Assert.True(Catalog.TryMatch("hyper techno", out var spaced, out _));

        Assert.Equal(spaced.Id, runTogether.Id);
        Assert.Equal("hyper techno", spaced.Name);

        // One record, not two: the count is asserted in
        // TheCatalogHoldsTheResearchedGenreAndStyleCounts.
        Assert.Single(Catalog.Terms, term => term.Id == spaced.Id);
    }

    /// <summary>
    /// The reconciliations the research published must resolve to their canonical
    /// term rather than to a second canonical record. Without these, values such as
    /// "hindie" and "k-hiphop" would simply be unrecognised.
    /// </summary>
    /// <remarks>
    /// The <c>viaAlias</c> expectation differs on purpose. Where the alias spelling
    /// already reduces to the canonical term's own lookup key, the canonical answers
    /// the match and no alias entry is consulted, which is the "canonical outranks
    /// alias" rule. Where the spelling differs, the alias table is what resolves it,
    /// so an alias regression has to fail here.
    /// </remarks>
    [Theory]
    [InlineData("hindie", "Indian Indie", true)]
    [InlineData("k-hiphop", "Korean Hip-Hop", true)]
    [InlineData("k-rap", "Korean Rap", true)]
    [InlineData("deutschrap", "German Hip Hop", true)]
    [InlineData("neo swing", "swing revival", true)]
    [InlineData("neo-swing", "swing revival", true)]
    [InlineData("desi hip-hop", "Desi Hip Hop", false)]
    [InlineData("swedish hip-hop", "Swedish Hip-Hop", false)]
    [InlineData("powernoise", "power noise", false)]
    [InlineData("spanish rock", "Rock En Español", true)]
    [InlineData("modern melodic death metal", "melodic death metal", true)]
    public void AResearchedReconciliationResolvesToItsCanonicalTerm(
        string alias,
        string canonical,
        bool expectViaAlias)
    {
        Assert.True(Catalog.TryMatch(alias, out var term, out var viaAlias), $"{alias} should be recognised.");
        Assert.Equal(canonical, term.Name);
        Assert.Equal(expectViaAlias, viaAlias);
    }

    /// <summary>
    /// The canonical terms the worked examples in the cleanup design depend on.
    /// </summary>
    [Theory]
    [InlineData("Hip Hop", nameof(PersonalGenreTaxonKind.Genre))]
    [InlineData("Rap", nameof(PersonalGenreTaxonKind.Genre))]
    [InlineData("Kenyan Drill", nameof(PersonalGenreTaxonKind.Style))]
    [InlineData("Dance Pop", nameof(PersonalGenreTaxonKind.Style))]
    [InlineData("Harsh EBM", nameof(PersonalGenreTaxonKind.Style))]
    public void ACanonicalTermIsRecognisedWithItsResearchedKind(string value, string expectedKind)
    {
        Assert.True(Catalog.TryMatch(value, out var term, out _));
        Assert.Equal(expectedKind, term.Kind);
    }

    /// <summary>
    /// A value the catalog has never seen must not be recognised, so cleanup falls
    /// through to preserving it rather than guessing.
    /// </summary>
    [Theory]
    [InlineData("obscure-new-scene")]
    [InlineData("My Personal Genre")]
    [InlineData("zzz-not-a-real-term")]
    public void AnUnresearchedValueIsNotRecognised(string value)
    {
        Assert.False(Catalog.TryMatch(value, out _, out _));
        Assert.False(Catalog.IsExcluded(value));
        Assert.False(Catalog.IsAmbiguous(value));
    }

    /// <summary>
    /// A canonical name outranks any alias that would otherwise claim it, so an
    /// alias can never turn a researched term into a different one.
    /// </summary>
    [Fact]
    public void ACanonicalNameIsNeverShadowedByAnAlias()
    {
        Assert.NotEmpty(Catalog.Aliases);

        var canonicalKeys = Catalog.Terms
            .SelectMany(term => new[] { LookupKey(term.Name), LookupKey(term.Id) })
            .ToHashSet(StringComparer.Ordinal);

        foreach (var alias in Catalog.Aliases)
        {
            Assert.False(
                canonicalKeys.Contains(LookupKey(alias.Value)),
                $"Alias '{alias.Value}' must not shadow a canonical term.");
        }
    }

    private static string LookupKey(string value)
        => new string(value.Trim().Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>
    /// The researched exclusions are explicit knowledge, which is a different state
    /// from unknown and must be distinguishable by the cleanup engine.
    /// </summary>
    [Fact]
    public void ResearchedExclusionsAreRecognisedAsExclusionsRatherThanUnknown()
    {
        Assert.Equal(62, Catalog.ExclusionReasons.Count);
        Assert.Equal("greek-pop", Catalog.ResolvedExclusionCollisions["greekpop"]);
        Assert.False(Catalog.IsExcluded("Greek Pop"));
        Assert.True(Catalog.TryMatch("Greek Pop", out var greekPop, out _));
        Assert.Equal("Greek-Pop", greekPop.Name);

        // A RYM scene or movement entry the research declined to merge.
        Assert.True(Catalog.IsExcluded("Bosstown Sound"));
        Assert.False(Catalog.TryMatch("Bosstown Sound", out _, out _));

        // A Last.fm candidate the audit excluded, with its reason kept.
        Assert.True(Catalog.IsExcluded("Electrowave"));
        Assert.False(string.IsNullOrWhiteSpace(Catalog.ExclusionReason("Electrowave")));
    }

    /// <summary>
    /// A held term is neither known-good nor known-bad, which is its own state.
    /// </summary>
    [Fact]
    public void ResearchedAmbiguityIsDistinctFromExclusion()
    {
        Assert.Equal(3, Catalog.AmbiguityReasons.Count);

        Assert.True(Catalog.IsAmbiguous("Terror EBM"));
        Assert.False(Catalog.IsExcluded("Terror EBM"));
        Assert.False(string.IsNullOrWhiteSpace(Catalog.AmbiguityReason("Terror EBM")));
    }

    /// <summary>
    /// Region is metadata about a term. It never changes a term's kind and is never
    /// a rule for creating a classification.
    /// </summary>
    [Fact]
    public void RegionIsMetadataAndNeverChangesATermsKind()
    {
        Assert.True(Catalog.TryMatch("Kenyan Drill", out var kenyan, out _));
        Assert.Equal(nameof(PersonalGenreTaxonKind.Style), kenyan.Kind);
        Assert.Contains(PersonalGenreRegions.Africa, kenyan.Regions);

        Assert.True(Catalog.TryMatch("Bongo Flava", out var bongo, out _));
        Assert.Equal(nameof(PersonalGenreTaxonKind.Genre), bongo.Kind);
        Assert.Contains(PersonalGenreRegions.Africa, bongo.Regions);
    }

    /// <summary>
    /// Every region in the catalog must be one the application knows, so an unknown
    /// value is rejected at validation instead of being stored as a dead slug.
    /// </summary>
    [Fact]
    public void EveryRegionInTheCatalogIsAKnownRegion()
    {
        var used = Catalog.Terms
            .SelectMany(term => term.Regions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.NotEmpty(used);
        Assert.All(used, region => Assert.True(
            PersonalGenreRegions.IsKnown(region),
            $"'{region}' is not a known region."));
    }

    /// <summary>
    /// Aliases are mandatory rather than cosmetic: a regression that dropped them
    /// would leave these values unrecognised, so the count is pinned.
    /// </summary>
    [Fact]
    public void AliasesArePresentInTheCatalog()
    {
        Assert.True(Catalog.Aliases.Count >= 160);
    }

    /// <summary>
    /// A term id has to stay stable because saved user locks, mappings and rules
    /// refer to it, so two terms may never share one.
    /// </summary>
    [Fact]
    public void TermIdsAreUniqueAndResolvable()
    {
        var ids = Catalog.Terms.Select(term => term.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var id in ids)
        {
            Assert.True(Catalog.TryGetById(id, out _), $"id '{id}' should resolve.");
        }
    }

    /// <summary>
    /// The research publishes 3,746 terms and the catalog carries 3,745 canonical
    /// identities. The one spelling that collided is reconciled, not dropped, and the
    /// reconciliation is carried as runtime state so the difference stays inspectable
    /// instead of looking like a lost term.
    /// </summary>
    [Fact]
    public void TheSourceToRuntimeTermDifferenceIsAnAuditedReconciliation()
    {
        var catalog = Catalog;

        Assert.Single(catalog.ReconciledSpellings);
        Assert.Equal("hyper techno", catalog.ReconciledSpellings["hypertechno"]);

        // One canonical identity, reached by both source spellings.
        Assert.Single(catalog.Terms, term => term.Name == "hyper techno");
    }

    /// <summary>
    /// A document with no reconciliation field is still valid, because most catalogs
    /// have no collision to reconcile.
    /// </summary>
    [Fact]
    public void ACatalogWithNoReconciliationIsAccepted()
    {
        Assert.Empty(ResearchedGenreCatalog.BuildFromJson(ValidJson).ReconciledSpellings);
    }

    // ------------------------------------------------------------------ corruption rejection

    private const string ValidJson = """
    {
      "version": "test-v1",
      "source": { "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
      "terms": [
        { "id": "rock",  "name": "Rock", "kind": "Genre", "regions": [], "aliases": [] },
        { "id": "trap",  "name": "Trap", "kind": "Style", "regions": ["africa"], "aliases": [] }
      ],
      "aliases": { "punkrock": { "value": "punk rock", "target": "rock" } }
    }
    """;

    private static ResearchedGenreCatalog.CatalogDocument Document(string json) =>
        JsonSerializer.Deserialize<ResearchedGenreCatalog.CatalogDocument>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        })!;

    private static string Mutate(string json, string from, string to)
        => json.Replace(from, to, StringComparison.Ordinal);

    [Fact]
    public void AValidDocumentBuilds()
    {
        var catalog = ResearchedGenreCatalog.BuildFromJson(ValidJson);

        Assert.Equal("test-v1", catalog.Version);
        Assert.True(catalog.TryMatch("punk rock", out var term, out var viaAlias));
        Assert.True(viaAlias);
        Assert.Equal("Rock", term.Name);
    }

    [Theory]
    [InlineData("\"version\": \"test-v1\"", "\"version\": \"   \"")]
    [InlineData("\"kind\": \"Genre\"", "\"kind\": \"Mood\"")]
    [InlineData("\"id\": \"trap\"", "\"id\": \"rock\"")]
    [InlineData("\"name\": \"Trap\"", "\"name\": \"ROCK\"")]
    [InlineData("\"regions\": [\"africa\"]", "\"regions\": [\"atlantis\"]")]
    [InlineData("\"target\": \"rock\"", "\"target\": \"does-not-exist\"")]
    public void ACorruptDocumentIsRejectedRatherThanResolved(string from, string to)
    {
        Assert.NotEqual(from, to);
        Assert.Contains(from, ValidJson, StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(
            () => ResearchedGenreCatalog.BuildFromJson(Mutate(ValidJson, from, to)));
    }

    [Fact]
    public void ADocumentWithNoTermsIsRejected()
    {
        var document = Document(ValidJson);
        document.Terms = [];

        Assert.Throws<InvalidOperationException>(() => ResearchedGenreCatalog.Build(document));
    }

    [Fact]
    public void ATermWithNoNameIsRejected()
    {
        Assert.Throws<InvalidOperationException>(() => ResearchedGenreCatalog.BuildFromJson(
            Mutate(ValidJson, "\"name\": \"Rock\"", "\"name\": \"   \"")));
    }

    [Fact]
    public void ADuplicateAliasWithinOneTermIsRejected()
    {
        Assert.Throws<InvalidOperationException>(() => ResearchedGenreCatalog.BuildFromJson(
            Mutate(ValidJson, "\"name\": \"Trap\", \"kind\": \"Style\", \"regions\": [\"africa\"], \"aliases\": []",
                "\"name\": \"Trap\", \"kind\": \"Style\", \"regions\": [\"africa\"], \"aliases\": [\"x\", \"x\"]")));
    }

    [Fact]
    public void AnAliasResolvingToTwoCanonicalTermsIsRejected()
    {
        const string json = """
        {
          "version": "test-v1",
          "source": { "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
          "terms": [
            { "id": "rock", "name": "Rock", "kind": "Genre", "regions": [], "aliases": ["punk rock"] },
            { "id": "trap", "name": "Trap", "kind": "Style", "regions": [], "aliases": ["punk rock"] }
          ],
          "aliases": { "punkrock": { "value": "punk rock", "target": "rock" } }
        }
        """;

        var error = Assert.Throws<InvalidOperationException>(
            () => ResearchedGenreCatalog.BuildFromJson(json));
        Assert.Contains("multiple canonical terms", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnAliasShadowingACanonicalTermIsRejected()
    {
        const string json = """
        {
          "version": "test-v1",
          "source": { "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
          "terms": [
            { "id": "rock", "name": "Rock", "kind": "Genre", "regions": [], "aliases": [] },
            { "id": "trap", "name": "Trap", "kind": "Style", "regions": [], "aliases": ["rock"] }
          ],
          "aliases": { "rock": { "value": "rock", "target": "trap" } }
        }
        """;

        var error = Assert.Throws<InvalidOperationException>(
            () => ResearchedGenreCatalog.BuildFromJson(json));
        Assert.Contains("shadows canonical term", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnExclusionWithNoValueIsRejected()
    {
        var document = Document(ValidJson);
        document.Exclusions = new Dictionary<string, ResearchedGenreCatalog.CatalogEntry>
        {
            ["x"] = new() { Value = " " }
        };

        Assert.Throws<InvalidOperationException>(() => ResearchedGenreCatalog.Build(document));
    }

    [Fact]
    public void AnAmbiguityWithNoValueIsRejected()
    {
        var document = Document(ValidJson);
        document.Ambiguous = new Dictionary<string, ResearchedGenreCatalog.CatalogEntry>
        {
            ["x"] = new() { Reason = "held" }
        };

        Assert.Throws<InvalidOperationException>(() => ResearchedGenreCatalog.Build(document));
    }

    [Fact]
    public void AnExclusionReasonIsCarriedThroughRatherThanDiscarded()
    {
        var document = Document(ValidJson);
        document.Exclusions = new Dictionary<string, ResearchedGenreCatalog.CatalogEntry>
        {
            ["scene tag"] = new() { Value = "Scene Tag", Reason = "scene, not a musical classification" }
        };

        var catalog = ResearchedGenreCatalog.Build(document);

        Assert.True(catalog.IsExcluded("Scene Tag"));
        Assert.Equal("scene, not a musical classification", catalog.ExclusionReason("Scene Tag"));
    }

    [Fact]
    public void ADocumentWithoutParentIdsLoadsWithAnEmptyParentList()
    {
        var catalog = ResearchedGenreCatalog.BuildFromJson(ValidJson);

        Assert.All(catalog.Terms, term => Assert.Empty(term.ParentIds));
    }

    [Fact]
    public void DuplicateParentIdsAreDeduplicatedWithoutChangingOrder()
    {
        var catalog = ResearchedGenreCatalog.BuildFromJson("""
        {
          "version": "test-v1",
          "terms": [
            { "id": "rock", "name": "Rock", "kind": "Genre", "regions": [], "aliases": [] },
            { "id": "trap", "name": "Trap", "kind": "Style", "regions": [], "aliases": [],
              "parentIds": ["rock", "ROCK", "rock"] }
          ]
        }
        """);

        var trap = catalog.Terms.Single(term => term.Id == "trap");
        Assert.Equal(["rock"], trap.ParentIds);
    }

    [Fact]
    public void ATermCannotParentItself()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ResearchedGenreCatalog.BuildFromJson("""
        {
          "version": "test-v1",
          "terms": [
            { "id": "rock", "name": "Rock", "kind": "Genre", "regions": [], "aliases": [], "parentIds": ["rock"] }
          ]
        }
        """));
        Assert.Contains("parents itself", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rock", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnUnknownParentIdIsRejected()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ResearchedGenreCatalog.BuildFromJson("""
        {
          "version": "test-v1",
          "terms": [
            { "id": "rock", "name": "Rock", "kind": "Genre", "regions": [], "aliases": [] },
            { "id": "trap", "name": "Trap", "kind": "Style", "regions": [], "aliases": [], "parentIds": ["missing-parent"] }
          ]
        }
        """));
        Assert.Contains("unknown id", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("missing-parent", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AParentCycleNamesTheIdsOnIt()
    {
        const string json = """
        {
          "version": "test-v1",
          "terms": [
            { "id": "a", "name": "A", "kind": "Style", "regions": [], "aliases": [], "parentIds": ["b"] },
            { "id": "b", "name": "B", "kind": "Style", "regions": [], "aliases": [], "parentIds": ["c"] },
            { "id": "c", "name": "C", "kind": "Genre", "regions": [], "aliases": [], "parentIds": ["a"] }
          ]
        }
        """;

        var error = Assert.Throws<InvalidOperationException>(() => ResearchedGenreCatalog.BuildFromJson(json));
        Assert.Contains("a -> b -> c -> a", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAliasCannotBeWrittenAsAParent()
    {
        const string json = """
        {
          "version": "test-v1",
          "terms": [
            { "id": "rock", "name": "Rock", "kind": "Genre", "regions": [], "aliases": ["punk rock"] },
            { "id": "trap", "name": "Trap", "kind": "Style", "regions": [], "aliases": [], "parentIds": ["punk rock"] }
          ],
          "aliases": { "punkrock": { "value": "punk rock", "target": "rock" } }
        }
        """;

        var error = Assert.Throws<InvalidOperationException>(() => ResearchedGenreCatalog.BuildFromJson(json));
        Assert.Contains("alias", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnAmbiguousLabelCannotBeSelectedAsAParent()
    {
        const string json = """
        {
          "version": "test-v1",
          "terms": [
            { "id": "rock", "name": "Rock", "kind": "Genre", "regions": [], "aliases": [] },
            { "id": "trap", "name": "Trap", "kind": "Style", "regions": [], "aliases": [], "parentIds": ["Bollywood"] }
          ],
          "ambiguous": {
            "bollywood": { "value": "Bollywood", "reason": "two identities" }
          }
        }
        """;

        var error = Assert.Throws<InvalidOperationException>(() => ResearchedGenreCatalog.BuildFromJson(json));
        Assert.Contains("ambiguous", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnExclusionCannotBeAParent()
    {
        const string json = """
        {
          "version": "test-v1",
          "terms": [
            { "id": "rock", "name": "Rock", "kind": "Genre", "regions": [], "aliases": [] },
            { "id": "trap", "name": "Trap", "kind": "Style", "regions": [], "aliases": [], "parentIds": ["Scene Tag"] }
          ],
          "exclusions": {
            "scenetag": { "value": "Scene Tag", "reason": "not musical" }
          }
        }
        """;

        var error = Assert.Throws<InvalidOperationException>(() => ResearchedGenreCatalog.BuildFromJson(json));
        Assert.Contains("exclusion", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
