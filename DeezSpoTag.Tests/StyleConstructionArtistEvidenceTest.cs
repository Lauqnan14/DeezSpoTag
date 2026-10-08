using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeezSpoTag.Services.Genre;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Artist identity, role and location on real builder output.
/// </summary>
/// <remarks>
/// <para>
/// The evidence record has always carried <c>ArtistId</c>, <c>ArtistRole</c> and
/// <c>ArtistBindingReference</c>, but nothing ever populated them and every fact was
/// hard-coded to track scope. Role-qualified predicates therefore worked only against
/// hand-built facts. These tests drive the real builder, so the artist contract is
/// exercised the way production reaches it.
/// </para>
/// <para>
/// The behaviour being pinned is deliberately conservative. A binding that the caller
/// cannot prove leaves the role unknown, and an unknown role fails closed against any
/// predicate that names one.
/// </para>
/// </remarks>
public sealed class StyleConstructionArtistEvidenceTest
{
    private static readonly DateTimeOffset ReadAt = DateTimeOffset.Parse("2026-10-07T10:00:00Z");

    private static StyleConstructionEvidenceSnapshot Build(
        StyleConstructionArtistIdentity? identity,
        StyleConstructionArtistLocation? location,
        params GenreTagObservation[] observations)
    {
        var resolution = PersonalGenreResolver.Resolve(observations);
        return StyleConstructionEvidenceBuilder.Build(
            42, new(observations, ReadAt), resolution, new(), [], [], [], identity, location);
    }

    private static GenreTagObservation Genre(string value) => new(value, PersonalGenreTaxonKind.Genre);
    private static GenreTagObservation Style(string value) => new(value, PersonalGenreTaxonKind.Style);

    private static StyleConstructionArtistIdentity MainArtist => new(
        7, ConstructionArtistRole.Main, "track-credit:7", ConstructionEvidenceScope.Artist);

    private static StyleConstructionArtistIdentity FeaturedArtist => new(
        9, ConstructionArtistRole.Featured, "track-credit:9", ConstructionEvidenceScope.Artist);

    // ---------------------------------------------------------------- binding

    [Fact]
    public void WithoutABindingNoFactClaimsAnArtistOrAnythingBeyondTrackScope()
    {
        var fact = Assert.Single(Build(null, null, Genre("Hip-Hop")).SemanticFacts);

        Assert.Null(fact.ArtistId);
        Assert.Null(fact.ArtistBindingReference);
        Assert.Equal(ConstructionArtistRole.Unknown, fact.ArtistRole);
        Assert.Equal(ConstructionEvidenceScope.Track, fact.Scope);
    }

    [Fact]
    public void AProvenMainArtistBindingReachesTheFacts()
    {
        var snapshot = Build(MainArtist, null, Genre("Hip-Hop"));
        var fact = Assert.Single(snapshot.SemanticFacts);

        Assert.Equal(7, fact.ArtistId);
        Assert.Equal(ConstructionArtistRole.Main, fact.ArtistRole);
        Assert.Equal("track-credit:7", fact.ArtistBindingReference);
        Assert.Equal(ConstructionEvidenceScope.Artist, fact.Scope);
        Assert.Equal(MainArtist, snapshot.ArtistIdentity);
    }

    [Fact]
    public void AFeaturedBindingIsRecordedDistinctlyFromAMainOne()
    {
        var fact = Assert.Single(Build(FeaturedArtist, null, Genre("Hip-Hop")).SemanticFacts);

        Assert.Equal(9, fact.ArtistId);
        Assert.Equal(ConstructionArtistRole.Featured, fact.ArtistRole);
        Assert.Equal("track-credit:9", fact.ArtistBindingReference);
    }

    [Fact]
    public void AnAlbumArtistBindingKeepsTheRoleUnknown()
    {
        // The library binds a track through its album. An album artist is not the
        // track's main artist, so the role must not claim otherwise.
        var binding = new StyleConstructionArtistIdentity(
            7, ConstructionArtistRole.Unknown, "library-album-artist:7", ConstructionEvidenceScope.Artist);

        var fact = Assert.Single(Build(binding, null, Genre("Hip-Hop")).SemanticFacts);

        Assert.Equal(7, fact.ArtistId);
        Assert.Equal("library-album-artist:7", fact.ArtistBindingReference);
        Assert.Equal(ConstructionArtistRole.Unknown, fact.ArtistRole);
    }

    [Fact]
    public void EveryFactInASnapshotCarriesTheSameBinding()
    {
        var snapshot = Build(MainArtist, null,
            Genre("Hip-Hop"),
            Style("Trap"));

        Assert.Equal(2, snapshot.SemanticFacts.Count);
        Assert.All(snapshot.SemanticFacts, fact =>
        {
            Assert.Equal(7, fact.ArtistId);
            Assert.Equal("track-credit:7", fact.ArtistBindingReference);
        });
    }

    [Fact]
    public void ATablessBindingReferenceIsCarriedThroughAndFailsTheBindingGate()
    {
        var binding = new StyleConstructionArtistIdentity(
            7, ConstructionArtistRole.Main, "", ConstructionEvidenceScope.Artist);

        var snapshot = Build(binding, null, Genre("Hip-Hop"));
        var fact = Assert.Single(snapshot.SemanticFacts);

        // The builder records what it was given; the evaluator is what refuses it.
        Assert.Equal(7, fact.ArtistId);
        Assert.Equal("", fact.ArtistBindingReference);
        Assert.Contains(RejectedReasons(snapshot), reason => reason.Contains("binding", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AMissingArtistIdFailsTheBindingGateEvenWithARoleAndReference()
    {
        var binding = new StyleConstructionArtistIdentity(
            0, ConstructionArtistRole.Main, "track-credit:7", ConstructionEvidenceScope.Artist);

        var snapshot = Build(binding, null, Genre("Hip-Hop"));
        Assert.Contains(RejectedReasons(snapshot), reason => reason.Contains("binding", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------- location provenance

    [Fact]
    public void StructuredLocationIsRetainedWithItsProvenance()
    {
        var location = new StyleConstructionArtistLocation(
            "Kenya", "Nairobi", "Nairobi County", "manual",
            "artist-location-override:7", ReadAt.AddDays(-1), "manual-override");

        var snapshot = Build(MainArtist, location, Genre("Kenyan Drill"));

        var retained = Assert.IsType<StyleConstructionArtistLocation>(snapshot.ArtistLocation);
        Assert.Equal("Kenya", retained.Country);
        Assert.Equal("Nairobi", retained.City);
        Assert.Equal("Nairobi County", retained.Region);
        Assert.Equal("manual", retained.Source);
        Assert.Equal("artist-location-override:7", retained.SourceReference);
        Assert.Equal(ReadAt.AddDays(-1), retained.KnownAt);
        Assert.Equal("manual-override", retained.ResolutionMethod);
        Assert.True(retained.HasValue);
    }

    [Fact]
    public void LocationIsRetainedButNeverBecomesAClassification()
    {
        // The file mentions no place at all, so there is nothing location could support
        // even in principle. This is the guarantee that no Country + Genre -> Style
        // shortcut exists.
        var location = new StyleConstructionArtistLocation(
            "Kenya", "Nairobi", null, "manual", "artist-location-override:7", ReadAt, "manual-override");

        var snapshot = Build(MainArtist, location, Genre("Hip-Hop"));

        Assert.Single(snapshot.SemanticFacts);
        Assert.DoesNotContain(snapshot.SemanticFacts, fact => fact.CanonicalId == "kenyan-drill");
        Assert.DoesNotContain(snapshot.SemanticFacts, fact => fact.CanonicalValue.Contains("Kenya", StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyLocationProvenanceReportsNoValue()
    {
        Assert.False(new StyleConstructionArtistLocation(null, null, null, "manual", null, null, null).HasValue);
        Assert.True(new StyleConstructionArtistLocation("Kenya", null, null, "manual", null, null, null).HasValue);
    }

    [Fact]
    public void NoLocationMeansNoProvenanceOnTheSnapshot()
    {
        var snapshot = Build(MainArtist, null, Genre("Hip-Hop"));
        Assert.Null(snapshot.ArtistLocation);
    }

    // ---------------------------------------------------------------- role predicates

    [Theory]
    [InlineData(ConstructionArtistRole.Main)]
    [InlineData(ConstructionArtistRole.Featured)]
    public void ARoleQualifiedPredicateIsSatisfiedWhenTheBindingProvesThatRole(ConstructionArtistRole required)
    {
        var identity = new StyleConstructionArtistIdentity(7, required, "track-credit:7", ConstructionEvidenceScope.Artist);
        var snapshot = Build(identity, null, Genre("Hip-Hop"));

        var decision = Evaluate(snapshot, required);

        Assert.Equal(StyleConstructionOutcome.Qualified, decision.Outcome);
        Assert.Equal("parent", Assert.Single(decision.SatisfiedPredicates));
        Assert.Single(decision.EvidenceUsed);
    }

    [Fact]
    public void AnUnknownRoleFailsClosedAtTheBindingGateRatherThanTheRoleGate()
    {
        // An album-artist binding cannot say the track's main artist is this artist.
        var binding = new StyleConstructionArtistIdentity(
            7, ConstructionArtistRole.Unknown, "library-album-artist:7", ConstructionEvidenceScope.Artist);

        var decision = Evaluate(Build(binding, null, Genre("Hip-Hop")), ConstructionArtistRole.Main);

        Assert.Equal(StyleConstructionOutcome.MissingEvidence, decision.Outcome);
        Assert.Equal("parent", Assert.Single(decision.MissingPredicates));
        Assert.Empty(decision.EvidenceUsed);
        // An unknown role is exactly why the binding is not proven, so the fact is
        // refused before the role comparison is ever reached.
        Assert.Contains(decision.RejectedAlternatives,
            rejected => rejected.Reason.Contains("must be proven", StringComparison.Ordinal));
        Assert.Contains(decision.RejectedAlternatives, rejected => rejected.FactId is not null);
    }

    [Fact]
    public void ARoleQualifiedPredicateFailsClosedWithNoBindingAtAll()
    {
        var decision = Evaluate(Build(null, null, Genre("Hip-Hop")), ConstructionArtistRole.Main);

        Assert.Equal(StyleConstructionOutcome.MissingEvidence, decision.Outcome);
        Assert.Empty(decision.EvidenceUsed);
    }

    [Fact]
    public void ATrackScopedFactIsRejectedByAnArtistOnlyPredicate()
    {
        // A rule that will accept nothing but artist-scoped evidence must refuse a
        // track observation, and must say why.
        var decision = Evaluate(Build(null, null, Genre("Hip-Hop")), null, allowArtistScope: false);

        Assert.Equal(StyleConstructionOutcome.MissingEvidence, decision.Outcome);
        Assert.Empty(decision.EvidenceUsed);
        Assert.Contains(decision.RejectedAlternatives,
            rejected => rejected.Reason.Contains("scope", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnArtistScopedRuleAcceptsAnArtistScopedFact()
    {
        // The counterpart to the case above: once a binding exists and the scope
        // matches, artist-scoped evidence is usable.
        var decision = Evaluate(Build(MainArtist, null, Genre("Hip-Hop")), null, allowArtistScope: false);

        Assert.Equal(StyleConstructionOutcome.Qualified, decision.Outcome);
        Assert.Single(decision.EvidenceUsed);
    }

    // ---------------------------------------------------------------- helpers

    private static string[] RejectedReasons(StyleConstructionEvidenceSnapshot snapshot)
        => Preview(snapshot, null, true)
            .Decisions
            .SelectMany(decision => decision.RejectedAlternatives.Select(rejected => rejected.Reason))
            .ToArray();

    private static StyleConstructionDecision Evaluate(
        StyleConstructionEvidenceSnapshot snapshot, ConstructionArtistRole? required, bool allowArtistScope = true)
        => Assert.Single(Preview(snapshot, required, allowArtistScope).Decisions);

    private static StyleConstructionPreview Preview(
        StyleConstructionEvidenceSnapshot snapshot, ConstructionArtistRole? required, bool allowArtistScope)
        => StyleConstructionEvaluator.Evaluate(snapshot, Rules(required, null, allowArtistScope), new());

    /// <summary>
    /// A synthetic rule, so a role-qualified predicate can be exercised.
    /// </summary>
    /// <remarks>
    /// The shipped dataset contains only recognition-only rules by design, so testing
    /// a predicate at all requires a rule of our own. This changes nothing about the
    /// shipped dataset.
    /// </remarks>
    private static StyleConstructionRuleCatalog Rules(
        ConstructionArtistRole? required, ConstructionArtistRole? actual, bool allowArtistScope)
    {
        var allowedOrigins = new JsonArray(
            ConstructionEvidenceOrigin.ExplicitFile.ToString(),
            ConstructionEvidenceOrigin.UserMapped.ToString(),
            ConstructionEvidenceOrigin.UserRule.ToString());
        // allowArtistScope: true accepts both scopes; false makes the rule
        // artist-only, which is how a track-scoped observation gets refused.
        var allowedScopes = allowArtistScope
            ? new JsonArray(ConstructionEvidenceScope.Track.ToString(), ConstructionEvidenceScope.Artist.ToString())
            : new JsonArray(ConstructionEvidenceScope.Artist.ToString());

        var alternative = new JsonObject
        {
            ["kind"] = PersonalGenreTaxonKind.Genre.ToString(),
            ["canonicalId"] = "hip-hop"
        };
        if (required is { } role) alternative["artistRole"] = role.ToString();

        var json = $$"""
        {
          "schemaVersion": 1,
          "datasetVersion": "test-only-role",
          "candidates": [
            {
              "ruleId": "ROLE-TEST",
              "version": "1",
              "targetStyleId": "kenyan-drill",
              "enabled": true,
              "inferenceAllowed": true,
              "researchDisposition": "Inferable",
              "requiredAll": [
                { "predicateId": "parent", "anyOf": [ {{alternative.ToJsonString()}} ] }
              ],
              "supporting": [],
              "forbiddenAny": [],
              "allowedEvidenceOrigins": {{allowedOrigins.ToJsonString()}},
              "allowedScopes": {{allowedScopes.ToJsonString()}},
              "researchSources": [
                {
                  "title": "Test only", "url": "https://example.invalid/role",
                  "sourceType": "test", "access": "test", "observedAt": "2026-10-07",
                  "supportedClaims": ["Exercises a role-qualified predicate."]
                }
              ],
              "researchRationale": "Test fixture only.",
              "research": {
                "definition": "d", "musicalAncestry": "a", "requiredParentGenres": "p",
                "existingStyleRelationships": "s", "sceneRelationship": "sc",
                "countryRelationship": "c", "cityRelationship": "ci",
                "languageRelationship": "l", "emergencePeriod": "e", "eraRequirement": "er",
                "geographyRequirement": "g",
                "recognitionAliases": ["none"],
                "sufficientEvidence": "A satisfied role-qualified predicate.",
                "insufficientEvidence": ["none"],
                "falsePositives": ["none"],
                "disqualifyingCases": ["none"],
                "researchGaps": ["none"]
              }
            }
          ]
        }
        """;

        return StyleConstructionRuleCatalog.Parse(json, new());
    }

    [Fact]
    public void TheTestFixtureRuleItselfIsInferableSoARolePredicateCanBeReached()
    {
        // Guards the harness: if this rule stopped being inferable, every role test
        // above would silently pass by short-circuiting to RecognitionOnly.
        var rule = Assert.Single(Rules(ConstructionArtistRole.Main, null, true).Candidates);
        Assert.True(rule.Enabled);
        Assert.True(rule.InferenceAllowed);
        Assert.Equal(ConstructionResearchDisposition.Inferable, rule.ResearchDisposition);
        Assert.Single(rule.RequiredAll);
    }
}
