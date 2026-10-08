using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeezSpoTag.Services.Genre;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class StyleConstructionEvaluatorTest
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-07T10:00:00Z");

    [Fact]
    public void AllRequiredGroupsMustMatchButAnyAlternativeCanSatisfyOneGroup()
    {
        var rules = Rules(rule => rule["requiredAll"]!.AsArray().Add(Group("second", "rock", "pop")));
        var missing = Evaluate(rules, Fact("hip-hop"));
        Assert.Equal(StyleConstructionOutcome.MissingEvidence, missing.Outcome);
        Assert.Equal(new[] { "parent" }, missing.SatisfiedPredicates);
        Assert.Equal(new[] { "second" }, missing.MissingPredicates);
        var qualified = Evaluate(rules, Fact("hip-hop"), Fact("pop"));
        Assert.Equal(StyleConstructionOutcome.Qualified, qualified.Outcome);
        Assert.Empty(qualified.MissingPredicates);
        Assert.Equal(2, qualified.EvidenceUsed.Count);
        // Qualified is a preview verdict, not an applied construction. The decision
        // carries no Style to write and no persistence hook, which is asserted here
        // by the absence of any constructed-style member on the type itself.
        Assert.DoesNotContain(
            typeof(StyleConstructionDecision).GetProperties(),
            property => property.PropertyType == typeof(PersonalGenreTaxon)
                || property.Name.Contains("Constructed", StringComparison.Ordinal));
    }

    [Fact]
    public void SupportingFactsCannotReplaceOneRequiredFact()
    {
        var decision = Evaluate(Rules(rule => rule["supporting"]!.AsArray().Add(Group("support", "pop"))), Fact("pop"));
        Assert.Equal(StyleConstructionOutcome.MissingEvidence, decision.Outcome);
        Assert.Contains("parent", decision.MissingPredicates);
        Assert.Contains("support", decision.SatisfiedPredicates);
    }

    [Fact]
    public void ConflictsRetainMissingRequirementsAndConcreteFacts()
    {
        var decision = Evaluate(Rules(rule => rule["forbiddenAny"]!.AsArray().Add(Group("contradiction", "rock"))), Fact("rock"));
        Assert.Equal(StyleConstructionOutcome.Conflicted, decision.Outcome);
        Assert.Contains("parent", decision.MissingPredicates);
        Assert.Equal("rock", Assert.Single(decision.ConflictingFacts).CanonicalId);
        Assert.Empty(decision.EvidenceUsed);
    }

    [Theory]
    [InlineData("origin", "origin")]
    [InlineData("scope", "scope")]
    [InlineData("kind", "canonical")]
    [InlineData("name", "canonical")]
    [InlineData("unknown", "canonical")]
    [InlineData("future", "snapshot")]
    [InlineData("source", "provenance")]
    [InlineData("provenance", "provenance")]
    public void IneligibleFactsAreRejectedWithCausalReasons(string change, string reason)
    {
        var fact = Fact("hip-hop");
        fact = change switch
        {
            "origin" => fact with { Origin = ConstructionEvidenceOrigin.UserMapped },
            "scope" => fact with { Scope = ConstructionEvidenceScope.Album },
            "kind" => fact with { Kind = PersonalGenreTaxonKind.Style },
            "name" => fact with { CanonicalValue = "Hip Hop alias" },
            "unknown" => fact with { CanonicalId = "raw-vibe-unknown" },
            "future" => fact with { KnownAt = At.AddSeconds(1) },
            "source" => fact with { Source = "" },
            "provenance" => fact with { ProvenanceReference = "" },
            _ => throw new InvalidOperationException()
        };
        var decision = Evaluate(Rules(), fact);
        Assert.Equal(StyleConstructionOutcome.MissingEvidence, decision.Outcome);
        Assert.Empty(decision.EvidenceUsed);
        Assert.Contains(decision.RejectedAlternatives, r => r.FactId == fact.FactId && r.Reason.Contains(reason, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("unknownRole")]
    [InlineData("missingArtist")]
    [InlineData("missingBinding")]
    [InlineData("featuredRole")]
    public void ArtistScopedFactsRequireProvenIdentityBindingAndRequiredRole(string change)
    {
        var rules = Rules(rule =>
        {
            rule["allowedScopes"] = new JsonArray("Artist");
            rule["requiredAll"]![0]!["anyOf"]![0]!["artistRole"] = "Main";
        });
        var fact = Fact("hip-hop") with { Scope = ConstructionEvidenceScope.Artist, ArtistId = 123,
            ArtistRole = ConstructionArtistRole.Main, ArtistBindingReference = "track:42/main:123" };
        var incomplete = change switch
        {
            "unknownRole" => fact with { ArtistRole = ConstructionArtistRole.Unknown },
            "missingArtist" => fact with { ArtistId = null },
            "missingBinding" => fact with { ArtistBindingReference = null },
            "featuredRole" => fact with { ArtistRole = ConstructionArtistRole.Featured },
            _ => throw new InvalidOperationException()
        };
        Assert.Equal(StyleConstructionOutcome.MissingEvidence, Evaluate(rules, incomplete).Outcome);
        Assert.Equal(StyleConstructionOutcome.Qualified, Evaluate(rules, fact).Outcome);
    }

    [Theory]
    [InlineData("kenyan-drill")]
    [InlineData("asakaa")]
    public void ExistingStyleAuthoritySuppressesAnOtherwiseQualifiedPreview(string lockedId)
    {
        var snapshot = Snapshot(Fact("hip-hop")) with { UserAuthority = new([new(42, lockedId)]) };
        var decision = Assert.Single(StyleConstructionEvaluator.Evaluate(snapshot, Rules(), new()).Decisions);
        Assert.Equal(StyleConstructionOutcome.SuppressedByUserAuthority, decision.Outcome);
        Assert.Contains("Style lock", decision.Explanation, StringComparison.Ordinal);
        var noFact = snapshot with { SemanticFacts = [] };
        Assert.Equal(StyleConstructionOutcome.MissingEvidence,
            Assert.Single(StyleConstructionEvaluator.Evaluate(noFact, Rules(), new()).Decisions).Outcome);
    }

    [Fact]
    public void ProductionRulesRemainRecognitionOnlyRegardlessOfDescriptiveEvidence()
    {
        var result = StyleConstructionEvaluator.Evaluate(Snapshot(Fact("hip-hop"), Fact("kenya"), Fact("swahili")),
            StyleConstructionRuleCatalog.Default, new());
        Assert.Equal(3, result.Decisions.Count);
        Assert.All(result.Decisions, d =>
        {
            Assert.Equal(StyleConstructionOutcome.RecognitionOnly, d.Outcome);
            Assert.NotEmpty(d.Research.ResearchGaps);
            Assert.Empty(d.MissingPredicates);
        });
    }

    /// <summary>
    /// The shipped dataset must contain no rule that can reach Qualified.
    /// </summary>
    /// <remarks>
    /// RecognitionOnly is a rule property, not an outcome this evaluator happens to
    /// produce, so the assertion is on the rules themselves. It is what guarantees
    /// no production run can report a qualifying construction, independent of the
    /// evidence supplied.
    /// </remarks>
    [Fact]
    public void NoShippedRuleIsAbleToQualify()
    {
        Assert.All(StyleConstructionRuleCatalog.Default.Candidates, rule =>
        {
            Assert.False(rule.Enabled, $"{rule.RuleId} must not be enabled.");
            Assert.False(rule.InferenceAllowed, $"{rule.RuleId} must not allow inference.");
            Assert.Equal(ConstructionResearchDisposition.RecognitionOnly, rule.ResearchDisposition);
            Assert.Empty(rule.RequiredAll);
        });
    }

    [Fact]
    public void RepeatEvaluationAndInputOrderingDoNotChangeDecisions()
    {
        var rules = Rules(rule => rule["supporting"]!.AsArray().Add(Group("support", "rock", "pop")));
        var first = StyleConstructionEvaluator.Evaluate(Snapshot(Fact("pop"), Fact("hip-hop"), Fact("rock")), rules, new());
        var again = StyleConstructionEvaluator.Evaluate(first.Snapshot, rules, new());
        var reversed = StyleConstructionEvaluator.Evaluate(Snapshot(Fact("rock"), Fact("hip-hop"), Fact("pop")), rules, new());
        Assert.Equal(JsonSerializer.Serialize(first.Decisions), JsonSerializer.Serialize(again.Decisions));
        Assert.Equal(JsonSerializer.Serialize(first.Decisions), JsonSerializer.Serialize(reversed.Decisions));
    }

    private static StyleConstructionDecision Evaluate(StyleConstructionRuleCatalog rules, params StyleConstructionSemanticFact[] facts)
        => Assert.Single(StyleConstructionEvaluator.Evaluate(Snapshot(facts), rules, new()).Decisions);

    private static StyleConstructionEvidenceSnapshot Snapshot(params StyleConstructionSemanticFact[] facts)
        => new(1, 42, At, ResearchedGenreCatalog.Current.Version, PersonalGenreResolver.Version, facts, new([]));

    private static StyleConstructionSemanticFact Fact(string id)
    {
        var catalog = new PersonalGenreCatalog();
        Assert.True(catalog.TryGetById(id, out var term));
        return new() { FactId = id, Kind = term.Kind, CanonicalId = id, CanonicalValue = term.Name,
            Scope = ConstructionEvidenceScope.Track, Origin = ConstructionEvidenceOrigin.ExplicitFile,
            Source = "audio-file", ProvenanceReference = "test-observation:" + id, OriginalValue = term.Name,
            OriginalField = term.Kind, KnownAt = At };
    }

    private static StyleConstructionRuleCatalog Rules(Action<JsonNode>? edit = null)
    {
        var root = JsonNode.Parse(StyleConstructionRuleCatalogTest.TestRulesJson)!;
        edit?.Invoke(root["candidates"]![0]!);
        return StyleConstructionRuleCatalog.Parse(root.ToJsonString(), new());
    }

    private static JsonNode Group(string predicateId, params string[] ids)
        => JsonNode.Parse(JsonSerializer.Serialize(new { predicateId, anyOf = ids.Select(id => new { kind = "Genre", canonicalId = id }) }))!;
}
