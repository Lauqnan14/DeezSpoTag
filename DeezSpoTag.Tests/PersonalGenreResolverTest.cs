using System;
using System.Linq;
using DeezSpoTag.Services.Genre;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class PersonalGenreResolverTest
{
    [Fact]
    public void BongoFlava_IsCanonicalGenre_NotHipHopOrAfropop()
    {
        var resolution = PersonalGenreResolver.Resolve(
        [
            new PersonalGenreEvidence("audiomack", "Bongo-Flava", PersonalGenreTaxonKind.Style, 1d, "track")
        ]);

        Assert.Equal("Bongo Flava", resolution.PrimaryGenre);
        Assert.Contains("Bongo Flava", resolution.Genres);
        Assert.DoesNotContain("Hip-Hop", resolution.Genres);
        Assert.DoesNotContain("Afropop", resolution.Genres);
    }

    [Fact]
    public void Afrosounds_IsContextOnly_NeverFinalGenre()
    {
        var resolution = PersonalGenreResolver.Resolve(
        [
            new PersonalGenreEvidence("audiomack", "Afrosounds", PersonalGenreTaxonKind.Genre, 1d, "track"),
            new PersonalGenreEvidence("audiomack", "Amapiano", PersonalGenreTaxonKind.Style, 1d, "track")
        ]);

        Assert.Equal("Amapiano", resolution.PrimaryGenre);
        Assert.Contains("Afrosounds", resolution.Contexts);
        Assert.DoesNotContain("Afrosounds", resolution.Genres);
    }

    [Fact]
    public void Afrobeat_And_Afrobeats_RemainDistinct()
    {
        var resolution = PersonalGenreResolver.Resolve(
        [
            new PersonalGenreEvidence("embedded", "Afrobeat", PersonalGenreTaxonKind.Genre, 1d, "track"),
            new PersonalGenreEvidence("lastfm", "Afrobeats", PersonalGenreTaxonKind.Genre, 1d, "track")
        ]);

        Assert.Contains("Afrobeat", resolution.Genres);
        Assert.Contains("Afrobeats", resolution.Genres);
        Assert.NotEqual(
            PersonalGenreTaxonomy.Normalize("Afrobeat"),
            PersonalGenreTaxonomy.Normalize("Afrobeats"));
    }

    [Fact]
    public void Afropop_IsStyle_WithMultipleParents()
    {
        Assert.True(PersonalGenreTaxonomy.TryGetById("afropop", out var taxon));
        Assert.Equal(PersonalGenreTaxonKind.Style, taxon.Kind);
        Assert.Contains("afrobeats", taxon.ParentIds!);
        Assert.Contains("pop", taxon.ParentIds!);
    }

    [Fact]
    public void UserRule_WinsOverBuiltInTaxonomy_AtPointNineEightAuthority()
    {
        var rules = new[]
        {
            new PersonalGenreRule(
                42,
                "Amapiano",
                "house",
                Source: "audiomack",
                Priority: 5000,
                Enabled: true)
        };

        var resolution = PersonalGenreResolver.Resolve(
        [
            new PersonalGenreEvidence("audiomack", "Amapiano", PersonalGenreTaxonKind.Style, 0.2d, "track")
        ],
        rules: rules);

        Assert.Equal("House", resolution.PrimaryGenre);
        Assert.Contains("42", resolution.AppliedRuleIds);
        Assert.DoesNotContain("Amapiano", resolution.Genres);
        var house = Assert.Single(resolution.Classifications.Where(item => item.TaxonId == "house"));
        Assert.Equal(0.98d, house.Confidence, 3);
    }

    [Fact]
    public void UserLock_ReplacesAutomaticClassificationForSameKind()
    {
        var resolution = PersonalGenreResolver.Resolve(
        [
            new PersonalGenreEvidence("audiomack", "Amapiano", PersonalGenreTaxonKind.Genre, 1d, "track")
        ],
        locks:
        [
            new PersonalGenreLock(99, "bongo-flava")
        ]);

        Assert.Equal("Bongo Flava", resolution.PrimaryGenre);
        Assert.DoesNotContain("Amapiano", resolution.Genres);
        var locked = Assert.Single(resolution.Classifications.Where(item => item.TaxonId == "bongo-flava"));
        Assert.True(locked.UserLocked);
        Assert.Equal(1d, locked.Confidence, 3);
    }

    [Fact]
    public void SourceSpecificRule_DoesNotAffectOtherProvider()
    {
        var rules = new[]
        {
            new PersonalGenreRule(
                7,
                "Trap",
                "pop",
                Source: "lastfm",
                Priority: 5000,
                Enabled: true)
        };

        var resolution = PersonalGenreResolver.Resolve(
        [
            new PersonalGenreEvidence("audiomack", "Trap", PersonalGenreTaxonKind.Style, 1d, "track")
        ],
        rules: rules);

        Assert.Contains("Trap", resolution.Styles);
        Assert.DoesNotContain("Pop", resolution.Genres);
        Assert.Empty(resolution.AppliedRuleIds);
    }

    [Fact]
    public void ContextOnlyCustomTaxon_CannotBecomeGenreEvenWithWrongKind()
    {
        var customTaxa = new[]
        {
            new PersonalGenreTaxon(
                "regional-bucket",
                "Regional Bucket",
                PersonalGenreTaxonKind.Genre,
                ContextOnly: true)
        };

        var resolution = PersonalGenreResolver.Resolve(
        [
            new PersonalGenreEvidence("manual", "Regional Bucket", PersonalGenreTaxonKind.Genre, 1d, "track")
        ],
        customTaxa: customTaxa);

        Assert.Null(resolution.PrimaryGenre);
        Assert.DoesNotContain("Regional Bucket", resolution.Genres);
        Assert.Contains("Regional Bucket", resolution.Contexts);
    }

    [Fact]
    public void Swahili_IsLanguage_NotContextOrGenre()
    {
        var resolution = PersonalGenreResolver.Resolve(
        [
            new PersonalGenreEvidence("manual", "Swahili", PersonalGenreTaxonKind.Language, 1d, "track")
        ]);

        Assert.Null(resolution.PrimaryGenre);
        Assert.Contains("Swahili", resolution.Languages);
        Assert.DoesNotContain("Swahili", resolution.Contexts);
        Assert.DoesNotContain("Swahili", resolution.Genres);
    }

    [Fact]
    public void Zilizopendwa_IsScene_NotGenre()
    {
        var resolution = PersonalGenreResolver.Resolve(
        [
            new PersonalGenreEvidence("manual", "Zilizopendwa", PersonalGenreTaxonKind.Scene, 1d, "track")
        ]);

        Assert.Null(resolution.PrimaryGenre);
        Assert.Contains("Zilizopendwa", resolution.Scenes);
        Assert.DoesNotContain("Zilizopendwa", resolution.Genres);
    }

    [Fact]
    public void CustomSceneAndLanguage_PreserveTheirDimensions()
    {
        var customTaxa = new[]
        {
            new PersonalGenreTaxon(
                "dar-club-scene",
                "Dar Club Scene",
                PersonalGenreTaxonKind.Scene,
                ContextOnly: true),
            new PersonalGenreTaxon(
                "luganda",
                "Luganda",
                PersonalGenreTaxonKind.Language,
                ContextOnly: true)
        };

        var resolution = PersonalGenreResolver.Resolve(
        [
            new PersonalGenreEvidence("manual", "Dar Club Scene", PersonalGenreTaxonKind.Scene, 1d, "track"),
            new PersonalGenreEvidence("manual", "Luganda", PersonalGenreTaxonKind.Language, 1d, "track")
        ],
        customTaxa: customTaxa);

        Assert.Contains("Dar Club Scene", resolution.Scenes);
        Assert.Contains("Luganda", resolution.Languages);
        Assert.DoesNotContain("Dar Club Scene", resolution.Contexts);
        Assert.DoesNotContain("Luganda", resolution.Contexts);
    }

    [Fact]
    public void Context_NeverBecomesPrimaryGenre()
    {
        var resolution = PersonalGenreResolver.Resolve(
        [
            new PersonalGenreEvidence("manual", "Tanzania", PersonalGenreTaxonKind.Context, 1d, "track"),
            new PersonalGenreEvidence("audiomack", "Bongo Flava", PersonalGenreTaxonKind.Genre, 1d, "track")
        ]);

        Assert.Equal("Bongo Flava", resolution.PrimaryGenre);
        Assert.Contains("Tanzania", resolution.Contexts);
        Assert.DoesNotContain("Tanzania", resolution.Genres);
    }

    [Fact]
    public void UnknownProviderBucket_IsFallbackNotForcedCanonicalMapping()
    {
        var resolution = PersonalGenreResolver.Resolve(
        [
            new PersonalGenreEvidence("audiomack", "Regional Pop Bucket", PersonalGenreTaxonKind.Genre, 1d, "track"),
            new PersonalGenreEvidence("audiomack", "Amapiano", PersonalGenreTaxonKind.Style, 1d, "track")
        ]);

        Assert.Equal("Amapiano", resolution.PrimaryGenre);
        Assert.Contains("Regional Pop Bucket", resolution.Genres);
        var fallback = Assert.Single(resolution.Classifications.Where(item => item.Name == "Regional Pop Bucket"));
        Assert.Equal(0.20d, fallback.Confidence, 3);
    }

    [Fact]
    public void ProviderFallback_CanBeDisabled()
    {
        var resolution = PersonalGenreResolver.Resolve(
        [
            new PersonalGenreEvidence("audiomack", "Regional Pop Bucket", PersonalGenreTaxonKind.Genre, 1d, "track")
        ],
        settings: new PersonalGenreSettings(PreserveProviderFallback: false));

        Assert.Null(resolution.PrimaryGenre);
        Assert.Empty(resolution.Genres);
    }

    [Fact]
    public void RawProviderMapping_CanOverrideExistingCanonicalValue()
    {
        var mappings = new[]
        {
            new PersonalGenreMapping(
                1,
                "Afrosounds",
                "bongo-flava",
                Source: "audiomack",
                Priority: 500,
                Enabled: true)
        };

        var resolution = PersonalGenreResolver.Resolve(
        [
            new PersonalGenreEvidence(
                "audiomack",
                "Afrosounds",
                PersonalGenreTaxonKind.Genre,
                1d,
                "track",
                CanonicalValue: "Afropop")
        ],
        mappings: mappings);

        Assert.Equal("Bongo Flava", resolution.PrimaryGenre);
        Assert.DoesNotContain("Afropop", resolution.Styles);
    }

    [Fact]
    public void CustomTaxonAlias_ParticipatesInResolution()
    {
        var customTaxa = new[]
        {
            new PersonalGenreTaxon(
                "coastal-fusion-test",
                "Coastal Fusion Test",
                PersonalGenreTaxonKind.Genre,
                Aliases: ["Coastal Test Sound", "Coastal Fusion Alias"])
        };

        var resolution = PersonalGenreResolver.Resolve(
        [
            new PersonalGenreEvidence("lastfm", "Coastal Test Sound", PersonalGenreTaxonKind.Genre, 1d, "track")
        ],
        customTaxa: customTaxa);

        Assert.Equal("Coastal Fusion Test", resolution.PrimaryGenre);
        Assert.Contains("Coastal Fusion Test", resolution.Genres);
    }

    [Fact]
    public void CustomStyle_CanContributeBuiltInParentGenre()
    {
        var customTaxa = new[]
        {
            new PersonalGenreTaxon(
                "urban-fusion-test",
                "Urban Fusion Test",
                PersonalGenreTaxonKind.Style,
                ParentIds: ["hip-hop"])
        };

        var resolution = PersonalGenreResolver.Resolve(
        [
            new PersonalGenreEvidence("lastfm", "Urban Fusion Test", PersonalGenreTaxonKind.Style, 1d, "track")
        ],
        customTaxa: customTaxa,
        settings: new PersonalGenreSettings(IncludeParentGenres: true));

        Assert.Contains("Urban Fusion Test", resolution.Styles);
        Assert.Contains("Hip-Hop", resolution.Genres);
    }

    [Fact]
    public void UserLock_CanTargetCustomTaxon()
    {
        var customTaxa = new[]
        {
            new PersonalGenreTaxon("custom-locked-genre", "Custom Locked Genre", PersonalGenreTaxonKind.Genre)
        };

        var resolution = PersonalGenreResolver.Resolve(
        [
            new PersonalGenreEvidence("audiomack", "Amapiano", PersonalGenreTaxonKind.Genre, 1d, "track")
        ],
        locks:
        [
            new PersonalGenreLock(99, "custom-locked-genre")
        ],
        customTaxa: customTaxa);

        Assert.Equal("Custom Locked Genre", resolution.PrimaryGenre);
        Assert.DoesNotContain("Amapiano", resolution.Genres);
        var locked = Assert.Single(resolution.Classifications.Where(item => item.TaxonId == "custom-locked-genre"));
        Assert.True(locked.UserLocked);
        Assert.Equal(1d, locked.Confidence, 3);
    }

    [Fact]
    public void IgnoreMapping_SuppressesClassificationAndProviderFallback()
    {
        var mappings = new[]
        {
            new PersonalGenreMapping(
                11,
                "Regional Pop Bucket",
                "pop",
                Source: "audiomack",
                Action: PersonalGenreMappingAction.Ignore)
        };

        var resolution = PersonalGenreResolver.Resolve(
        [
            new PersonalGenreEvidence(
                "audiomack",
                "Regional Pop Bucket",
                PersonalGenreTaxonKind.Genre,
                1d,
                "track")
        ],
        mappings: mappings);

        Assert.Null(resolution.PrimaryGenre);
        Assert.Empty(resolution.Genres);
        var decision = Assert.Single(resolution.Decisions);
        Assert.Equal("ignored", decision.Outcome);
        Assert.Null(decision.TaxonId);
    }

    [Fact]
    public void AmbiguousMapping_SuppressesClassificationAndProviderFallback()
    {
        var mappings = new[]
        {
            new PersonalGenreMapping(
                12,
                "Urban",
                "hip-hop",
                Source: "lastfm",
                Action: PersonalGenreMappingAction.Ambiguous)
        };

        var resolution = PersonalGenreResolver.Resolve(
        [
            new PersonalGenreEvidence(
                "lastfm",
                "Urban",
                PersonalGenreTaxonKind.Genre,
                1d,
                "track")
        ],
        mappings: mappings);

        Assert.Null(resolution.PrimaryGenre);
        Assert.Empty(resolution.Genres);
        var decision = Assert.Single(resolution.Decisions);
        Assert.Equal("ambiguous", decision.Outcome);
    }

    [Fact]
    public void ContextOnlyMapping_CannotCreateFinalGenre()
    {
        var mappings = new[]
        {
            new PersonalGenreMapping(
                13,
                "East African",
                "bongo-flava",
                Source: "manual",
                Action: PersonalGenreMappingAction.ContextOnly)
        };

        var resolution = PersonalGenreResolver.Resolve(
        [
            new PersonalGenreEvidence(
                "manual",
                "East African",
                PersonalGenreTaxonKind.Genre,
                1d,
                "track")
        ],
        mappings: mappings);

        Assert.Null(resolution.PrimaryGenre);
        Assert.Empty(resolution.Genres);
        Assert.Contains("Bongo Flava", resolution.Contexts);
        var decision = Assert.Single(resolution.Decisions);
        Assert.Equal("context_only", decision.Outcome);
        Assert.Equal("bongo-flava", decision.TaxonId);
    }

    [Fact]
    public void TrackLock_OutranksAlbumAndArtistLocksForSameKind()
    {
        var resolution = PersonalGenreResolver.Resolve(
        [
            new PersonalGenreEvidence(
                "audiomack",
                "Amapiano",
                PersonalGenreTaxonKind.Genre,
                1d,
                "track")
        ],
        locks:
        [
            new PersonalGenreLock(99, "afrobeats", ScopeType: "artist", ScopeId: 1),
            new PersonalGenreLock(99, "hip-hop", ScopeType: "album", ScopeId: 2),
            new PersonalGenreLock(99, "bongo-flava", ScopeType: "track", ScopeId: 99)
        ]);

        Assert.Equal("Bongo Flava", resolution.PrimaryGenre);
        Assert.DoesNotContain("Hip-Hop", resolution.Genres);
        Assert.DoesNotContain("Afrobeats", resolution.Genres);
        var locked = Assert.Single(resolution.Classifications.Where(item => item.UserLocked));
        Assert.Equal("bongo-flava", locked.TaxonId);
        Assert.Contains("user-lock:track", locked.Sources);
    }

    [Fact]
    public void AlbumLock_OutranksArtistLockWhenTrackLockIsAbsent()
    {
        var resolution = PersonalGenreResolver.Resolve(
        [
            new PersonalGenreEvidence(
                "audiomack",
                "Amapiano",
                PersonalGenreTaxonKind.Genre,
                1d,
                "track")
        ],
        locks:
        [
            new PersonalGenreLock(99, "afrobeats", ScopeType: "artist", ScopeId: 1),
            new PersonalGenreLock(99, "hip-hop", ScopeType: "album", ScopeId: 2)
        ]);

        Assert.Equal("Hip-Hop", resolution.PrimaryGenre);
        Assert.DoesNotContain("Afrobeats", resolution.Genres);
        var locked = Assert.Single(resolution.Classifications.Where(item => item.UserLocked));
        Assert.Equal("hip-hop", locked.TaxonId);
        Assert.Contains("user-lock:album", locked.Sources);
    }

    [Fact]
    public void DecisionTrail_RecordsCanonicalTaxonomyClassification()
    {
        var resolution = PersonalGenreResolver.Resolve(
        [
            new PersonalGenreEvidence(
                "essentia-discogs519",
                "Electronic---House",
                PersonalGenreTaxonKind.Genre,
                0.9d,
                "audio",
                CanonicalValue: "House")
        ]);

        Assert.Equal("House", resolution.PrimaryGenre);
        var decision = Assert.Single(resolution.Decisions);
        Assert.Equal("classified", decision.Outcome);
        Assert.Equal("house", decision.TaxonId);
        Assert.Contains("canonical evidence value", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("discogs", "track", PersonalGenreTaxonKind.Style, 0.90)]
    [InlineData("discogs", "track", PersonalGenreTaxonKind.Genre, 0.85)]
    [InlineData("audiomack", "track", PersonalGenreTaxonKind.Genre, 0.88)]
    [InlineData("audiomack", "editorial", PersonalGenreTaxonKind.Genre, 0.82)]
    [InlineData("audiomack", "album", PersonalGenreTaxonKind.Genre, 0.80)]
    [InlineData("audiomack", "artist", PersonalGenreTaxonKind.Genre, 0.60)]
    [InlineData("lastfm", "track", PersonalGenreTaxonKind.Style, 0.72)]
    [InlineData("lastfm", "artist", PersonalGenreTaxonKind.Style, 0.50)]
    [InlineData("spotify", "artist", PersonalGenreTaxonKind.Genre, 0.55)]
    [InlineData("essentia-discogs519", "audio", PersonalGenreTaxonKind.Style, 0.40)]
    public void SourceAuthorityCaps_MatchPersonalGenreDesign(
        string source,
        string scope,
        PersonalGenreTaxonKind kind,
        double expected)
    {
        var cap = PersonalGenreResolver.GetAuthorityCap(
            new PersonalGenreEvidence(source, "x", kind, 1d, scope));

        Assert.Equal(expected, cap, 3);
    }

    [Fact]
    public void IndependentProviders_CorroborateWithoutDoubleCountingSameProvider()
    {
        var oneProvider = PersonalGenreResolver.Resolve(
        [
            new PersonalGenreEvidence("lastfm", "Amapiano", PersonalGenreTaxonKind.Genre, 1d, "track"),
            new PersonalGenreEvidence("lastfm", "Amapiano", PersonalGenreTaxonKind.Genre, 1d, "track")
        ]);
        var corroborated = PersonalGenreResolver.Resolve(
        [
            new PersonalGenreEvidence("lastfm", "Amapiano", PersonalGenreTaxonKind.Genre, 1d, "track"),
            new PersonalGenreEvidence("audiomack", "Amapiano", PersonalGenreTaxonKind.Genre, 1d, "track")
        ]);

        var oneScore = Assert.Single(oneProvider.Classifications.Where(item => item.TaxonId == "amapiano")).Confidence;
        var corroboratedScore = Assert.Single(corroborated.Classifications.Where(item => item.TaxonId == "amapiano")).Confidence;

        Assert.Equal(0.72d, oneScore, 3);
        Assert.True(corroboratedScore > oneScore);
    }
}
