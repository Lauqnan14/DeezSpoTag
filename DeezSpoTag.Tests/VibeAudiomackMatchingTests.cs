using DeezSpoTag.Web.Services.Audiomack;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// A false Audiomack match is unacceptable for Vibe: candidates are scored against
/// the requested artist/title and only results at or above the confidence gate
/// contribute Audiomack evidence.
/// </summary>
public sealed class VibeAudiomackMatchingTests
{
    private static readonly AudiomackSongCandidate ExactCandidate = new(
        Id: "8526341",
        Title: "Amapiano Nights",
        Artist: "Piano Pusha",
        Album: "Piano Season",
        Genre: "afrosounds",
        Mood: "happy",
        Isrc: null,
        Label: null,
        DurationSeconds: 372,
        ArtworkUrl: null,
        ReleasedDate: null,
        Url: "https://audiomack.com/piano-pusha/song/amapiano-nights",
        UrlSlug: "amapiano-nights",
        ArtistSlug: "piano-pusha",
        UploaderName: "Piano Pusha",
        AlbumId: "771002");

    [Fact]
    public void ExactMatch_ScoresAboveTheGate()
    {
        var score = AudiomackVibeMetadataService.ScoreCandidate(ExactCandidate, "Piano Pusha", "Amapiano Nights");
        Assert.True(score >= AudiomackVibeMetadataService.DefaultMinimumMatchConfidence);
    }

    [Fact]
    public void FeaturedArtistAndCasingDifferences_StillMatch()
    {
        var score = AudiomackVibeMetadataService.ScoreCandidate(
            ExactCandidate with { Title = "Amapiano Nights (feat. Guest Vocalist)" },
            "piano pusha",
            "Amapiano Nights feat. Guest Vocalist");
        Assert.True(score >= AudiomackVibeMetadataService.DefaultMinimumMatchConfidence);
    }

    [Fact]
    public void WeakMatch_BelowTheGate_ProducesNoEvidence()
    {
        var score = AudiomackVibeMetadataService.ScoreCandidate(
            ExactCandidate with { Title = "Totally Different Song", Artist = "Other Person" },
            "Piano Pusha",
            "Amapiano Nights");
        Assert.True(score < AudiomackVibeMetadataService.DefaultMinimumMatchConfidence);
    }

    [Fact]
    public void RemixMarker_IsVersionDrift_NotTheSameTrack()
    {
        var score = AudiomackVibeMetadataService.ScoreCandidate(
            ExactCandidate with { Title = "Amapiano Nights (Sped Up Remix)" },
            "Piano Pusha",
            "Amapiano Nights");
        Assert.True(score < AudiomackVibeMetadataService.DefaultMinimumMatchConfidence);
    }

    [Fact]
    public void EmptyCandidate_ScoresZero()
    {
        var score = AudiomackVibeMetadataService.ScoreCandidate(
            ExactCandidate with { Title = "", Artist = null },
            "Piano Pusha",
            "Amapiano Nights");
        Assert.Equal(0d, score);
    }
}
