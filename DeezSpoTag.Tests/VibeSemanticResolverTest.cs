using DeezSpoTag.Services.Genre;
using System;
using System.Linq;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Web.Services.Audiomack;
using DeezSpoTag.Web.Services.Vibe;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Acceptance example from the Vibe directive: Audiomack's creator metadata wins
/// resolution, corroborating Last.fm raises confidence, acoustic evidence stays
/// visible without overriding semantics, and conflicts are retained.
/// </summary>
public sealed class VibeSemanticResolverTest
{
    [Fact]
    public void EmbeddedRegionalStyleRetainsPriorityAndAcousticDisagreement()
    {
        var result = VibeSemanticResolver.Resolve(
            new EmbeddedVibeMetadata(new[] { "Gengetone" }, new[] { "Gengetone" }, Array.Empty<string>()),
            null, null, null,
            new[] { new VibeSemanticResolver.AcousticGenreEvidence("Hip-Hop---Trap", 0.8, "discogs519") }, null);
        Assert.Equal(new[] { "Gengetone" }, result.ResolvedGenres);
        Assert.Contains(result.SemanticEvidence, e => e.RawValue == "Hip-Hop---Trap" && e.Source == "essentia-discogs519");
    }

    [Fact]
    public void PreferencesNormalizeGenresAndStylesButRetainBlockedEvidence()
    {
        var preferences = GenreNormalizationSnapshot.Create(true,
            new[] { new PersonalGenreAliasRule("Local rap", "Gengetone") }, new[] { "Junk" });
        var result = VibeSemanticResolver.Resolve(
            new EmbeddedVibeMetadata(new[] { "Local rap", "Junk" }, new[] { "Local rap" }, new[] { "Happy" }),
            null, null, null, null, null, preferences);
        Assert.Equal(new[] { "Gengetone" }, result.ResolvedGenres);
        Assert.Equal(new[] { "Gengetone" }, result.ResolvedStyles);
        Assert.Equal(new[] { "Happy" }, result.ResolvedMoods);
        Assert.Contains(result.SemanticEvidence, e => e.RawValue == "Local rap" && e.CanonicalValue == "Gengetone");
        Assert.Contains(result.SemanticEvidence, e => e.RawValue == "Junk" && !e.Eligible && e.ExclusionReason != null);
    }

    [Fact]
    public void UserSpellingWinsOverBuiltInVibeAliases()
    {
        var preferences = GenreNormalizationSnapshot.Create(true,
            new[] { new PersonalGenreAliasRule("Hip Hop", "HipHop") }, Array.Empty<string>());
        var result = VibeSemanticResolver.Resolve(
            new EmbeddedVibeMetadata(new[] { "Hip Hop" }, Array.Empty<string>(), Array.Empty<string>()),
            null, null, null, null, null, preferences);
        Assert.Equal(new[] { "HipHop" }, result.ResolvedGenres);
    }

    [Fact]
    public void PreferencesUseOriginalLabelsBeforeBuiltInSpellingAliases()
    {
        var preferences = GenreNormalizationSnapshot.Create(true,
            new[] { new PersonalGenreAliasRule("Rhythm and blues", "Soul") }, Array.Empty<string>());
        var result = VibeSemanticResolver.Resolve(
            new EmbeddedVibeMetadata(new[] { "Rhythm and blues" }, Array.Empty<string>(), Array.Empty<string>()),
            null, null, null,
            new[] { new VibeSemanticResolver.AcousticGenreEvidence("Funk / Soul---Soul", 0.8, "discogs519") }, null, preferences);
        Assert.Equal(new[] { "Soul" }, result.ResolvedGenres);
        Assert.Contains(result.SemanticEvidence, e => e.RawValue == "Rhythm and blues" && e.CanonicalValue == "Soul");
        Assert.Contains(result.SemanticEvidence, e => e.RawValue == "Funk / Soul---Soul" && e.Kind == VibeSemanticKind.Style && e.CanonicalValue == "Soul");
    }

    private static readonly AudiomackVibeMetadata AmapianoTrack = new()
    {
        TrackId = "8526341",
        Title = "Amapiano Nights",
        Artists = new[] { "Piano Pusha" },
        PrimaryGenre = "Afrosounds",
        Subgenres = new[] { "Amapiano", "Afrobeats" },
        Moods = new[] { "Happy", "Party" },
        MatchConfidence = 0.93
    };

    private static readonly LastFmTagService.LastFmTagEvidence[] LastFmTrack = new[]
    {
        new LastFmTagService.LastFmTagEvidence { Name = "Amapiano", Count = 100, RelativeWeight = 1.0, Scope = VibeEvidenceScope.Track },
        new LastFmTagService.LastFmTagEvidence { Name = "Afrobeats", Count = 74, RelativeWeight = 0.74, Scope = VibeEvidenceScope.Track },
        new LastFmTagService.LastFmTagEvidence { Name = "Party", Count = 61, RelativeWeight = 0.61, Scope = VibeEvidenceScope.Track },
        new LastFmTagService.LastFmTagEvidence { Name = "Dance", Count = 42, RelativeWeight = 0.42, Scope = VibeEvidenceScope.Track }
    };

    private static readonly VibeSemanticResolver.AcousticGenreEvidence[] AcousticGenres = new[]
    {
        new VibeSemanticResolver.AcousticGenreEvidence("Electronic---House", 0.71, "discogs519-maest-30s-pw-519l"),
        new VibeSemanticResolver.AcousticGenreEvidence("Electronic---Deep House", 0.53, "discogs519-maest-30s-pw-519l"),
        new VibeSemanticResolver.AcousticGenreEvidence("Funk / Soul---Afrobeat", 0.32, "discogs519-maest-30s-pw-519l")
    };

    [Fact]
    public void Audiomack_IsTheResolvedAuthority_NotTheAcousticModel()
    {
        var resolution = VibeSemanticResolver.Resolve(
            null, // embedded
            AmapianoTrack, LastFmTrack, null, AcousticGenres, null);

        Assert.Equal(new[] { "Afrosounds" }, resolution.ResolvedGenres);
        Assert.Contains("Amapiano", resolution.ResolvedStyles);
        Assert.Contains("Afrobeats", resolution.ResolvedStyles);
        Assert.Equal(new[] { "Happy", "Party" }, resolution.ResolvedMoods);
        // The acoustic model must NOT become the resolved genre.
        Assert.DoesNotContain("Electronic", resolution.ResolvedGenres);
    }

    [Fact]
    public void CorroboratingLastFm_RaisesStyleConfidence()
    {
        var resolution = VibeSemanticResolver.Resolve(
            null, // embedded
            AmapianoTrack, LastFmTrack, null, AcousticGenres, null);

        var amapiano = resolution.SemanticEvidence
            .Where(item => item.CanonicalValue == "Amapiano")
            .ToList();
        Assert.Contains(amapiano, item => item.Source == "audiomack");
        Assert.Contains(amapiano, item => item.Source == "lastfm");
        var total = amapiano.Sum(item => item.FinalWeight);
        var audiomackOnly = amapiano.Where(item => item.Source == "audiomack").Sum(item => item.FinalWeight);
        Assert.True(total > audiomackOnly, "Corroboration must raise Amapiano's combined confidence.");
    }

    [Fact]
    public void AcousticSourceName_DerivesTheSourceFromTheModelThatProducedTheLabel()
    {
        Assert.Equal("essentia-discogs519", VibeSemanticResolver.AcousticSourceName("discogs519-maest-30s-pw-519l"));
        Assert.Equal("essentia-discogs400", VibeSemanticResolver.AcousticSourceName("discogs400-discogs-effnet"));
        Assert.Equal("essentia-acoustic", VibeSemanticResolver.AcousticSourceName(null));
        Assert.Equal("essentia-acoustic", VibeSemanticResolver.AcousticSourceName("   "));
        Assert.Equal("essentia-acoustic", VibeSemanticResolver.AcousticSourceName("some-other-head"));
    }

    [Fact]
    public void Discogs400GenreEvidence_IsNotReportedAsDiscogs519()
    {
        // A Discogs400 downgrade must be visible in the evidence provenance rather
        // than silently claiming the Discogs519/MAEST head produced the label.
        var downgraded = new[]
        {
            new VibeSemanticResolver.AcousticGenreEvidence("Electronic---House", 0.61, "discogs400-discogs-effnet")
        };

        var resolution = VibeSemanticResolver.Resolve(
            null, // embedded
            null, // audiomack
            null, // lastfm track
            null, // lastfm artist
            downgraded,
            null);

        Assert.Contains(resolution.SemanticEvidence, item => item.Source == "essentia-discogs400");
        Assert.DoesNotContain(resolution.SemanticEvidence, item => item.Source == "essentia-discogs519");
    }

    [Fact]
    public void Evidence_RetainsAllIndependentSources()
    {
        var resolution = VibeSemanticResolver.Resolve(
            null, // embedded
            AmapianoTrack, LastFmTrack, null, AcousticGenres,
            new[] { new VibeSemanticResolver.AcousticMoodEvidence("Happy", 0.79) });

        Assert.Contains(resolution.SemanticEvidence, item => item.Source == "essentia-discogs519" && item.CanonicalValue == "House");
        Assert.Contains(resolution.SemanticEvidence, item => item.Source == "essentia-discogs519" && item.CanonicalValue == "Afrobeat");
        Assert.Contains(resolution.SemanticEvidence, item => item.Source == "essentia-mood" && item.CanonicalValue == "Happy");
        // Acoustic House is style-kind evidence and stays visible though unresolved.
        Assert.Contains(resolution.SemanticEvidence, item => item.Kind == VibeSemanticKind.Style && item.CanonicalValue == "House");
    }

    [Fact]
    public void AudiomackUnavailable_FallsBackToLastFmThenAcoustic()
    {
        var resolution = VibeSemanticResolver.Resolve(
            null, // embedded
            null, LastFmTrack, null, AcousticGenres, null);

        // Last.fm community tags resolve as styles; acoustic broad genres win genres.
        Assert.Contains("Amapiano", resolution.ResolvedStyles);
        Assert.Contains("Electronic", resolution.ResolvedGenres);
        Assert.Contains("Funk / Soul", resolution.ResolvedGenres);
    }

    [Fact]
    public void AudiomackConflict_IsKeptWithSupportingEvidence()
    {
        var audiomack = AmapianoTrack with { Subgenres = new[] { "Afro-Fusion" } };
        var resolution = VibeSemanticResolver.Resolve(
            null, // embedded
            audiomack, LastFmTrack, null, AcousticGenres, null);

        Assert.Contains("Afro-Fusion", resolution.ResolvedStyles);
        Assert.Contains(resolution.SemanticEvidence, item => item.CanonicalValue == "Afrobeats");
        Assert.Contains(resolution.SemanticEvidence, item => item.CanonicalValue == "Afrobeat");
    }

    [Fact]
    public void EverythingUnavailable_ResolvesEmptyButSucceeds()
    {
        var resolution = VibeSemanticResolver.Resolve(null, null, null, null, null, null);
        Assert.Empty(resolution.ResolvedGenres);
        Assert.Empty(resolution.ResolvedStyles);
        Assert.Empty(resolution.ResolvedMoods);
        Assert.Empty(resolution.SemanticEvidence);
    }
}
