using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeezSpoTag.Services.Genre;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class StyleConstructionRuleCatalogTest
{
    [Fact]
    public void ProductionCandidatesHaveResearchButNoExecutableInference()
    {
        var rules = StyleConstructionRuleCatalog.Default;
        Assert.Equal(3, rules.Candidates.Count);
        var catalog = new PersonalGenreCatalog();
        Assert.Equal(new[] { "Asakaa", "Kenyan Drill", "Korean Hip-Hop" }, rules.Candidates.Select(r =>
        {
            Assert.True(catalog.TryGetById(r.TargetStyleId, out var term));
            Assert.Equal(PersonalGenreTaxonKind.Style, term.Kind);
            Assert.False(r.Enabled);
            Assert.False(r.InferenceAllowed);
            Assert.Equal(ConstructionResearchDisposition.RecognitionOnly, r.ResearchDisposition);
            Assert.Empty(r.RequiredAll);
            Assert.NotEmpty(r.ResearchSources);
            Assert.NotEmpty(r.Research.InsufficientEvidence);
            Assert.NotEmpty(r.Research.FalsePositives);
            Assert.NotEmpty(r.Research.DisqualifyingCases);
            Assert.NotEmpty(r.Research.ResearchGaps);
            Assert.False(string.IsNullOrWhiteSpace(r.Research.SufficientEvidence));
            foreach (var source in r.ResearchSources)
            {
                Assert.StartsWith("https://", source.Url);
                Assert.NotEmpty(source.SupportedClaims);
            }
            return term.Name;
        }).ToArray());
        Assert.False(catalog.TryMatch("Asakaa Drill", out _)); // research alias proposals do not register new recognition
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("duplicateRule")]
    [InlineData("version")]
    [InlineData("targetMissing")]
    [InlineData("targetGenre")]
    [InlineData("disposition")]
    [InlineData("origin")]
    [InlineData("scope")]
    [InlineData("emptyRequired")]
    [InlineData("emptyAlternatives")]
    [InlineData("missingPredicateId")]
    [InlineData("duplicatePredicateId")]
    [InlineData("unknownCanonicalId")]
    [InlineData("wrongKind")]
    [InlineData("unknownKind")]
    [InlineData("missingKind")]
    [InlineData("role")]
    [InlineData("geography")]
    [InlineData("era")]
    [InlineData("recognitionEnabled")]
    [InlineData("insufficientResearch")]
    public void MalformedOrUnsupportedRulesAreRejected(string change)
    {
        var root = JsonNode.Parse(TestRulesJson)!;
        var rule = root["candidates"]![0]!;
        var predicate = rule["requiredAll"]![0]!;
        var alternative = predicate["anyOf"]![0]!;
        switch (change)
        {
            case "schema": root["schemaVersion"] = 2; break;
            case "duplicateRule": root["candidates"]!.AsArray().Add(rule.DeepClone()); break;
            case "version": rule["version"] = ""; break;
            case "targetMissing": rule["targetStyleId"] = "nonexistent"; break;
            case "targetGenre": rule["targetStyleId"] = "hip-hop"; break;
            case "disposition": rule["researchDisposition"] = "Maybe"; break;
            case "origin": rule["allowedEvidenceOrigins"]![0] = "Vibe"; break;
            case "scope": rule["allowedScopes"]![0] = "Country"; break;
            case "emptyRequired": rule["requiredAll"] = new JsonArray(); break;
            case "emptyAlternatives": predicate["anyOf"] = new JsonArray(); break;
            case "missingPredicateId": predicate["predicateId"] = ""; break;
            case "duplicatePredicateId": rule["supporting"]!.AsArray().Add(predicate.DeepClone()); break;
            case "unknownCanonicalId": alternative["canonicalId"] = "unknown"; break;
            case "wrongKind": alternative["kind"] = "Style"; break;
            case "unknownKind": alternative["kind"] = "Country"; break;
            case "missingKind": alternative.AsObject().Remove("kind"); break;
            case "role": alternative["artistRole"] = "Unknown"; break;
            case "geography": alternative["country"] = "Ghana"; break;
            case "era": rule["era"] = "2020s"; break;
            case "recognitionEnabled": rule["researchDisposition"] = "RecognitionOnly"; break;
            case "insufficientResearch": rule["research"]!["sufficientEvidence"] = ""; break;
        }
        Assert.ThrowsAny<Exception>(() => StyleConstructionRuleCatalog.Parse(root.ToJsonString(), new()));
    }

    [Fact]
    public void SyntheticPredicateGroupsAreValidatedForEvaluatorTestsOnly()
    {
        var rules = StyleConstructionRuleCatalog.Parse(TestRulesJson, new());
        Assert.Single(rules.Candidates);
        Assert.Equal("TEST-ONLY", rules.Candidates[0].RuleId);
    }

    // Synthetic logical fixture. These predicates make no claim about Asakaa or any musical Style.
    internal const string TestRulesJson = """
    {
      "schemaVersion":1,"datasetVersion":"test-only",
      "candidates":[{
        "ruleId":"TEST-ONLY","version":"1","targetStyleId":"kenyan-drill",
        "enabled":true,"inferenceAllowed":true,"researchDisposition":"Inferable",
        "requiredAll":[{"predicateId":"parent","anyOf":[{"kind":"Genre","canonicalId":"hip-hop"}]}],
        "supporting":[],"forbiddenAny":[],
        "allowedEvidenceOrigins":["ExplicitFile"],"allowedScopes":["Track"],
        "researchSources":[{"title":"Synthetic fixture","url":"https://example.org/test-only","sourceType":"TestOnly","access":"Synthetic","observedAt":"2026-10-07","supportedClaims":["Logical evaluator tests only"]}],
        "researchRationale":"Test-only logical fixture, not a musical inference claim.",
        "research":{
          "definition":"Test fixture","musicalAncestry":"Not a musical claim",
          "requiredParentGenres":"Test-only Hip-Hop predicate","existingStyleRelationships":"None asserted",
          "sceneRelationship":"None asserted","countryRelationship":"None asserted","cityRelationship":"None asserted",
          "languageRelationship":"None asserted","emergencePeriod":"Not applicable",
          "eraRequirement":"Not applicable","geographyRequirement":"Not applicable","recognitionAliases":[],
          "sufficientEvidence":"Synthetic predicate satisfaction only",
          "insufficientEvidence":["Supporting alone"],"falsePositives":["Not musical research"],
          "disqualifyingCases":["Synthetic forbidden predicate"],"researchGaps":["Not a production inference rule"]
        }
      }]
    }
    """;
}
