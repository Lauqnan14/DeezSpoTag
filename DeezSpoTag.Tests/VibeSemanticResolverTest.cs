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
