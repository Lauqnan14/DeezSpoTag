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
