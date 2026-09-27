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
            new PersonalGenreEvidence("audiomack", "Bongo-Flava", PersonalGenreTaxonKind.Style, 1d)
        ]);

        Assert.Equal("Bongo Flava", resolution.PrimaryGenre);
        Assert.Contains("Bongo Flava", resolution.Genres);
        Assert.DoesNotContain("Hip-Hop", resolution.Genres);
        Assert.DoesNotContain("Afropop", resolution.Genres);
    }

    [Fact]
    public void Afrobeat_And_Afrobeats_RemainDistinct()
    {
        var resolution = PersonalGenreResolver.Resolve(
        [
            new PersonalGenreEvidence("embedded", "Afrobeat", PersonalGenreTaxonKind.Genre, 1d),
            new PersonalGenreEvidence("lastfm", "Afrobeats", PersonalGenreTaxonKind.Genre, 0.8d)
        ]);

        Assert.Contains("Afrobeat", resolution.Genres);
        Assert.Contains("Afrobeats", resolution.Genres);
        Assert.NotEqual(
            PersonalGenreTaxonomy.Normalize("Afrobeat"),
            PersonalGenreTaxonomy.Normalize("Afrobeats"));
    }

    [Fact]
    public void UserRule_WinsOverBuiltInTaxonomy()
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
            new PersonalGenreEvidence("audiomack", "Amapiano", PersonalGenreTaxonKind.Style, 1d)
        ],
        rules: rules);

        Assert.Equal("House", resolution.PrimaryGenre);
        Assert.Contains("42", resolution.AppliedRuleIds);
        Assert.DoesNotContain("Amapiano", resolution.Genres);
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
            new PersonalGenreEvidence("audiomack", "Trap", PersonalGenreTaxonKind.Style, 1d)
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
            new PersonalGenreEvidence("manual", "Tanzania", PersonalGenreTaxonKind.Context, 1d),
            new PersonalGenreEvidence("audiomack", "Bongo Flava", PersonalGenreTaxonKind.Genre, 1d)
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
            new PersonalGenreEvidence("audiomack", "Afrosounds", PersonalGenreTaxonKind.Genre, 1d),
            new PersonalGenreEvidence("audiomack", "Amapiano", PersonalGenreTaxonKind.Style, 1d)
        ]);

        Assert.Equal("Amapiano", resolution.PrimaryGenre);
        Assert.Contains("Afrosounds", resolution.Genres);
    }

    [Fact]
    public void ProviderFallback_CanBeDisabled()
    {
        var resolution = PersonalGenreResolver.Resolve(
        [
            new PersonalGenreEvidence("audiomack", "Afrosounds", PersonalGenreTaxonKind.Genre, 1d)
        ],
        settings: new PersonalGenreSettings(PreserveProviderFallback: false));

        Assert.Null(resolution.PrimaryGenre);
        Assert.Empty(resolution.Genres);
    }
}
