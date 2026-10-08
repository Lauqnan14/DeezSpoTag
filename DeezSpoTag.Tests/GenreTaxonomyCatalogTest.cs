using System;
using System.Collections.Generic;
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
        Assert.Equal("researched-master-2026-10-06-v1", Catalog.Version);
        Assert.Equal(64, Catalog.SourceSha256.Length);
        Assert.All(Catalog.SourceSha256, character => Assert.Contains(character, "0123456789abcdef"));
    }

    /// <summary>
    /// The researched master publishes 1,351 Genres and 2,395 Styles. One researched
    /// Style spelling collides with another under the shared lookup key and is
    /// carried as an alias rather than a second canonical record, so the canonical
    /// Style count is one lower while every published term stays reachable.
    /// </summary>
    [Fact]
    public void TheCatalogHoldsTheResearchedGenreAndStyleCounts()
    {
        Assert.Equal(1351, Catalog.Genres);
        Assert.Equal(2394, Catalog.Styles);
        Assert.Equal(3745, Catalog.Terms.Count);
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
    [InlineData("swedish hip-hop", "Swedish Hip Hop", false)]
    [InlineData("powernoise", "power noise", false)]
    [InlineData("spanish rock", "rock en español", true)]
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
        Assert.Equal(66, Catalog.ExclusionReasons.Count);

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
}
