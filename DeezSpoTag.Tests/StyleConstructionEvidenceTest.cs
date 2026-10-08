using System;
using System.Linq;
using DeezSpoTag.Services.Genre;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class StyleConstructionEvidenceTest
{
    private static readonly DateTimeOffset ReadAt = DateTimeOffset.Parse("2026-10-07T10:00:00Z");

    [Fact]
    public void CanonicalAliasAndNormalizationPreserveFileLineage()
    {
        GenreTagObservation[] observations = [new("k-hiphop", PersonalGenreTaxonKind.Genre) { OriginalValue = "K-Hiphop " }];
        var result = Build(observations);
        var fact = Assert.Single(result.SemanticFacts);
        Assert.Equal("Korean Hip-Hop", fact.CanonicalValue);
        Assert.Equal(PersonalGenreTaxonKind.Style, fact.Kind);
        Assert.Equal(ConstructionEvidenceOrigin.ExplicitFile, fact.Origin);
        Assert.Equal(ConstructionEvidenceScope.Track, fact.Scope);
        Assert.Equal("audio-file", fact.Source);
        Assert.Equal("observation:0", fact.ProvenanceReference);
        Assert.Equal("K-Hiphop ", fact.OriginalValue);
        Assert.Equal(PersonalGenreTaxonKind.Genre, fact.OriginalField);
        Assert.Equal(ReadAt, fact.KnownAt);
        Assert.Null(fact.ArtistId);
        Assert.Equal(ConstructionArtistRole.Unknown, fact.ArtistRole);
        Assert.Contains(ConstructionTransformation.Normalized, fact.Transformations);
        Assert.Contains(ConstructionTransformation.AliasCanonicalized, fact.Transformations);
        Assert.Contains(ConstructionTransformation.FieldMoved, fact.Transformations);
    }

    [Fact]
    public void AllSixCanonicalDimensionsCanSupplyExplicitFacts()
    {
        var catalog = new PersonalGenreCatalog();
        foreach (var kind in Enum.GetValues<PersonalGenreTaxonKind>())
        {
            var term = catalog.Taxa.First(t => t.Kind == kind);
            var fact = Assert.Single(Build([new(term.Name, kind)]).SemanticFacts);
            Assert.Equal(term.Id, fact.CanonicalId);
            Assert.Equal(kind, fact.Kind);
        }
    }

    [Fact]
    public void MappingAndRuleRetainInstructionIdentityAndPrecedence()
    {
        PersonalGenreMapping[] mappings = [new(7, "local choice", "hip-hop"), new(3, "local choice", "rock", Priority: 1)];
        PersonalGenreRule[] rules = [new(9, "local choice", "rock", Priority: 1), new(8, "local choice", "hip-hop", Priority: 1)];
        var mapped = Assert.Single(Build([new("local choice", PersonalGenreTaxonKind.Genre)], mappings: mappings).SemanticFacts);
        Assert.Equal(ConstructionEvidenceOrigin.UserMapped, mapped.Origin);
        Assert.Equal("mapping:7/observation:0", mapped.ProvenanceReference);
        var ruled = Assert.Single(Build([new("local choice", PersonalGenreTaxonKind.Genre)], mappings: mappings, rules: rules).SemanticFacts);
        Assert.Equal(ConstructionEvidenceOrigin.UserRule, ruled.Origin);
        Assert.Equal("rule:8/observation:0", ruled.ProvenanceReference);
    }

    [Theory]
    [InlineData("brand new community value")]
    [InlineData("Bosstown Sound")]
    [InlineData("Terror EBM")]
    public void UnqualifiedRawValuesNeverProduceFacts(string value)
        => Assert.Empty(Build([new(value, PersonalGenreTaxonKind.Genre)]).SemanticFacts);

    [Fact]
    public void IgnoredBlockedAndDerivedParentValuesNeverBecomeIndependentFacts()
    {
        Assert.Empty(Build([new("ignore me", PersonalGenreTaxonKind.Genre)],
            mappings: [new(1, "ignore me", "hip-hop", Action: PersonalGenreMappingAction.Ignore)]).SemanticFacts);
        var snapshot = new GenreSemanticSnapshot([new("Worldwide", PersonalGenreTaxonKind.Genre)], ReadAt);
        var normalized = GenreNormalizationPreprocessor.Apply(snapshot, GenreNormalizationSnapshot.Create(true, null, ["Worldwide"]));
        Assert.Empty(Build(normalized.Observations.ToArray()).SemanticFacts);
        var styleFacts = Build([new("Kenyan Drill", PersonalGenreTaxonKind.Style)], settings: new(IncludeParentGenres: true));
        Assert.Single(styleFacts.SemanticFacts);
        Assert.DoesNotContain(styleFacts.SemanticFacts, f => f.Kind == PersonalGenreTaxonKind.Genre);
    }

    [Fact]
    public void LocksAndGenreLimitCannotResurrectRemovedClassifications()
    {
        var catalog = new PersonalGenreCatalog();
        catalog.TryMatch("Asakaa", out var asakaa);
        catalog.TryMatch("Kenyan Drill", out var kenyan);
        PersonalGenreLock[] locks = [new(42, asakaa.Id, ScopeType: "artist", ScopeId: 2), new(42, kenyan.Id, ScopeType: "track", ScopeId: 42)];
        var preview = Build([new("Asakaa", PersonalGenreTaxonKind.Style)], locks: locks);
        Assert.Empty(preview.SemanticFacts);
        Assert.Equal(kenyan.Id, Assert.Single(preview.UserAuthority.StyleLocks).TaxonId);
        var matching = Build([new("Kenyan Drill", PersonalGenreTaxonKind.Style)], locks: locks);
        Assert.Single(matching.SemanticFacts);
        Assert.Single(Build([new("Hip-Hop", PersonalGenreTaxonKind.Genre), new("Rock", PersonalGenreTaxonKind.Genre)], settings: new(MaxGenres: 1)).SemanticFacts);
    }

    [Fact]
    public void CustomRecognitionRequiresAnExplicitMappingOrRule()
    {
        PersonalGenreTaxon[] custom = [new("my-scene", "My Scene", PersonalGenreTaxonKind.Scene, Aliases: ["mine"])];
        Assert.Empty(Build([new("My Scene", PersonalGenreTaxonKind.Scene)], custom: custom).SemanticFacts);
        Assert.Empty(Build([new("mine", PersonalGenreTaxonKind.Scene)], custom: custom).SemanticFacts);
        var fact = Assert.Single(Build([new("mine", PersonalGenreTaxonKind.Scene)], custom: custom,
            mappings: [new(1, "mine", "my-scene")]).SemanticFacts);
        Assert.Equal(ConstructionEvidenceOrigin.UserMapped, fact.Origin);
    }

    [Fact]
    public void UnprovableInstructionAttributionFailsClosed()
    {
        GenreTagObservation[] observations = [new("mine", PersonalGenreTaxonKind.Genre)];
        var resolution = PersonalGenreResolver.Resolve(observations, mappings: [new(1, "mine", "hip-hop")]);
        var snapshot = StyleConstructionEvidenceBuilder.Build(42, new(observations, ReadAt), resolution, new(), [], [], []);
        Assert.Empty(snapshot.SemanticFacts);
    }

    [Fact]
    public void ContextOnlyMappingQualifiesOnlyWhenTheTargetHasACanonicalContextIdentity()
    {
        var valid = Build([new("local context", PersonalGenreTaxonKind.Genre)],
            mappings: [new(4, "local context", "kenya", Action: PersonalGenreMappingAction.ContextOnly)]);
        var fact = Assert.Single(valid.SemanticFacts);
        Assert.Equal(PersonalGenreTaxonKind.Context, fact.Kind);
        Assert.Equal(ConstructionEvidenceOrigin.UserMapped, fact.Origin);
        Assert.Equal("mapping:4/observation:0", fact.ProvenanceReference);
        var changedKind = Build([new("local context", PersonalGenreTaxonKind.Genre)],
            mappings: [new(4, "local context", "hip-hop", Action: PersonalGenreMappingAction.ContextOnly)]);
        Assert.Empty(changedKind.SemanticFacts);
    }

    [Fact]
    public void ASceneAliasRetainsAliasTransformationDespiteContextOnlyCleanupOutcome()
    {
        var fact = Assert.Single(Build([new("Swahili Oldies", PersonalGenreTaxonKind.Scene)]).SemanticFacts);
        Assert.Equal("zilizopendwa", fact.CanonicalId);
        Assert.Equal("Swahili Oldies", fact.OriginalValue);
        Assert.Contains(ConstructionTransformation.AliasCanonicalized, fact.Transformations);
    }

    private static StyleConstructionEvidenceSnapshot Build(GenreTagObservation[] observations,
        PersonalGenreMapping[]? mappings = null, PersonalGenreRule[]? rules = null,
        PersonalGenreLock[]? locks = null, PersonalGenreTaxon[]? custom = null, PersonalGenreSettings? settings = null,
        StyleConstructionArtistIdentity? artistIdentity = null, StyleConstructionArtistLocation? artistLocation = null)
    {
        var resolution = PersonalGenreResolver.Resolve(observations, mappings, rules, locks, custom, settings);
        return StyleConstructionEvidenceBuilder.Build(42, new(observations, ReadAt), resolution,
            new(custom), locks ?? [], mappings ?? [], rules ?? [], artistIdentity, artistLocation);
    }
}
