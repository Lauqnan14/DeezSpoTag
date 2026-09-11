using System;
using System.Linq;
using System.Text.Json;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Web.Services.Audiomack;
using DeezSpoTag.Web.Services.Vibe;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Orchestration-level coverage: the background service must fuse Audiomack,
/// Last.fm track/artist, Discogs519 and Essentia mood evidence into resolved
/// semantics with provenance, and every online failure must stay non-fatal.
/// </summary>
public sealed class VibeOrchestrationTest
{
    private static readonly AudiomackVibeMetadata AfrosoundsTrack = new()
    {
        TrackId = "8526341",
        Url = "https://audiomack.com/piano-pusha/song/amapiano-nights",
        Title = "Amapiano Nights",
        Artists = new[] { "Piano Pusha" },
        PrimaryGenre = "Afrosounds",
        Subgenres = new[] { "Amapiano", "Afrobeats" },
        Moods = new[] { "Happy", "Party" },
        MatchConfidence = 0.94
    };

    private static readonly LastFmTagService.LastFmTagEvidence[] TrackTags = new[]
    {
        new LastFmTagService.LastFmTagEvidence { Name = "Amapiano", Count = 100, RelativeWeight = 1.0, Scope = VibeEvidenceScope.Track },
        new LastFmTagService.LastFmTagEvidence { Name = "Party", Count = 63, RelativeWeight = 0.63, Scope = VibeEvidenceScope.Track }
    };

    private static TrackAnalysisBackgroundService.AnalysisOutput Output(
        string genreModel = "discogs519-maest-30s-pw-519l",
        string valenceSource = "deam-msd-musicnn-2",
        string arousalSource = "deam-msd-musicnn-2")
        => new(
            null, null, null, null, null, null, null, null, null, null,
            new[] { "Electronic---House" }, null,
            0.78, null, null, null, 0.71, null, null,
            null, null, null, null, null, null, null, null, null,
            new[]
            {
                new TrackAnalysisBackgroundService.VibeGenreEvidenceDto("Electronic---House", 0.73, genreModel)
            },
            genreModel, valenceSource, arousalSource);

    [Fact]
    public void FullSourceCase_ResolvesAudiomackAuthority_WithAllFourSources()
    {
        var vibe = TrackAnalysisBackgroundService.BuildVibeSemanticsCore(
            Output(),
            null, // embedded
            AfrosoundsTrack, TrackTags, artistTags: null);

        Assert.Equal(new[] { "Afrosounds" }, vibe.ResolvedGenres);
        Assert.Equal(new[] { "Amapiano", "Afrobeats" }, vibe.ResolvedStyles);
        Assert.Equal(new[] { "Happy", "Party" }, vibe.ResolvedMoods);

        var sources = ParseSources(vibe.SemanticEvidenceJson);
        Assert.Equal(4, sources.Length);
        Assert.Contains("audiomack", sources);
        Assert.Contains("lastfm", sources);
        Assert.Contains("essentia-discogs519", sources);
        Assert.Contains("essentia-mood", sources);
        Assert.Equal("discogs519-maest-30s-pw-519l", vibe.GenreModel);
    }

    [Fact]
    public void AudiomackUnavailable_AnalysisSucceedsWithRetainedEvidence()
    {
        var vibe = TrackAnalysisBackgroundService.BuildVibeSemanticsCore(
            Output(),
            null, // embedded
            null, TrackTags, null);

        Assert.Contains("Amapiano", vibe.ResolvedStyles);
        Assert.Contains("Electronic", vibe.ResolvedGenres);
        var sources = ParseSources(vibe.SemanticEvidenceJson);
        Assert.Contains("lastfm", sources);
        Assert.Contains("essentia-discogs519", sources);
        Assert.Contains("essentia-mood", sources);
        Assert.DoesNotContain("audiomack", sources);
    }

    [Fact]
    public void LastFmUnavailable_AudiomackAndAcousticStillComplete()
    {
        var vibe = TrackAnalysisBackgroundService.BuildVibeSemanticsCore(
            Output(),
            null, // embedded
            AfrosoundsTrack, null, null);

        Assert.Equal(new[] { "Afrosounds" }, vibe.ResolvedGenres);
        Assert.Equal(new[] { "Amapiano", "Afrobeats" }, vibe.ResolvedStyles);
        Assert.Contains("audiomack", ParseSources(vibe.SemanticEvidenceJson));
    }

    [Fact]
    public void BothOnlineSourcesUnavailable_VibeStillProducesAcousticEvidence()
    {
        var vibe = TrackAnalysisBackgroundService.BuildVibeSemanticsCore(
            Output(valenceSource: "deam-msd-musicnn-2", arousalSource: "deam-msd-musicnn-2"),
            null, null, null, null);

        Assert.NotEmpty(vibe.ResolvedGenres);
        Assert.Equal("discogs519-maest-30s-pw-519l", vibe.GenreModel);
        Assert.Equal("deam-msd-musicnn-2", vibe.ValenceSource);
        Assert.Equal("deam-msd-musicnn-2", vibe.ArousalSource);
    }

    [Fact]
    public void WeakAudiomackMatch_NoAudiomackEvidenceAppears()
    {
        // A below-gate match never reaches the resolver: FindTrackAsync returns null.
        var vibe = TrackAnalysisBackgroundService.BuildVibeSemanticsCore(
            Output(),
            null, // embedded
            null, TrackTags, null);

        Assert.DoesNotContain("audiomack", ParseSources(vibe.SemanticEvidenceJson));
    }

    [Fact]
    public void SameTierValues_AllRetainWithinOneEntry()
    {
        var vibe = TrackAnalysisBackgroundService.BuildVibeSemanticsCore(
            Output(),
            null, // embedded
            AfrosoundsTrack, null, null);

        // Both audiomack styles and both audiomack moods survive — the winning tier
        // retains all of its values, never just the single highest.
        Assert.Equal(2, vibe.ResolvedStyles.Count);
        Assert.Equal(2, vibe.ResolvedMoods.Count);
    }

    [Fact]
    public void CompletedAnalysisResult_CarriesResolvedFieldsAndProvenance()
    {
        var track = new TrackAnalysisInputDto(1, 1, "/music/x.flac", 1000);
        var metrics = new TrackAnalysisBackgroundService.TrackSignalMetrics(0.5, 0.1, 0.2, 120, 480, 5, "C", 0.8, -8, 6, 0.5);
        var vibe = TrackAnalysisBackgroundService.BuildVibeSemanticsCore(
            Output(),
            null, // embedded
            AfrosoundsTrack, TrackTags, null);

        var result = TrackAnalysisBackgroundService.CreateCompletedAnalysisResult(
            track, metrics, Output(), null, vibe);

        Assert.Equal(new[] { "Afrosounds" }, result.ResolvedGenres);
        Assert.Contains("Amapiano", result.ResolvedStyles!);
        Assert.Equal(new[] { "Happy", "Party" }, result.ResolvedMoods);
        Assert.NotNull(result.SemanticEvidenceJson);
        Assert.Equal("discogs519-maest-30s-pw-519l", result.GenreModel);
        Assert.Equal("deam-msd-musicnn-2", result.ValenceSource);
        Assert.Equal("deam-msd-musicnn-2", result.ArousalSource);
        // Legacy fields stay intact for compatibility.
        Assert.NotNull(result.MoodTags);
        Assert.NotNull(result.EssentiaGenres);
    }

    [Fact]
    public void SemanticEvidenceJson_UsesTheDirectiveShape()
    {
        var vibe = TrackAnalysisBackgroundService.BuildVibeSemanticsCore(
            Output(),
            null, // embedded
            AfrosoundsTrack, TrackTags, null);

        using var document = JsonDocument.Parse(vibe.SemanticEvidenceJson!);
        var first = document.RootElement.EnumerateArray().First();
        foreach (var name in new[] { "source", "kind", "rawValue", "canonicalValue", "scope", "strength", "matchConfidence", "finalWeight" })
        {
            Assert.True(first.TryGetProperty(name, out _), $"missing {name}");
        }
    }


    [Fact]
    public void EmbeddedTags_HardAnchorResolution_OverOnlineAndAcoustic()
    {
        var embedded = new EmbeddedVibeMetadata(
            new[] { "Afrosounds" },
            new[] { "Amapiano", "Afrobeats" },
            new[] { "Happy", "Party" });

        var vibe = TrackAnalysisBackgroundService.BuildVibeSemanticsCore(
            Output(), embedded, AfrosoundsTrack, TrackTags, null);

        // Embedded wins every dimension even when the acoustic model disagrees.
        Assert.Equal(new[] { "Afrosounds" }, vibe.ResolvedGenres);
        Assert.Equal(new[] { "Amapiano", "Afrobeats" }, vibe.ResolvedStyles);
        Assert.Equal(new[] { "Happy", "Party" }, vibe.ResolvedMoods);
        // Lower tiers remain evidence.
        Assert.Contains("essentia-discogs519", ParseSources(vibe.SemanticEvidenceJson));
        Assert.Contains("source\":\"embedded", vibe.SemanticEvidenceJson);
        Assert.Contains("finalWeight\":1", vibe.SemanticEvidenceJson);
    }

    [Fact]
    public void EmbeddedFingerprint_IsDeterministic_AndChangesWithValues()
    {
        var one = new EmbeddedVibeMetadata(new[] { "Afrosounds" }, new[] { "Amapiano" }, new[] { "Happy" });
        var same = new EmbeddedVibeMetadata(new[] { "afrosounds" }, new[] { "amapiano" }, new[] { "happy" });
        var different = new EmbeddedVibeMetadata(new[] { "House" }, new[] { "Amapiano" }, new[] { "Happy" });

        Assert.Equal(one.ComputeFingerprint(), same.ComputeFingerprint());
        Assert.NotEqual(one.ComputeFingerprint(), different.ComputeFingerprint());
    }

    [Fact]
    public void MissingEmbeddedDimension_FallsThroughIndependently()
    {
        var embedded = new EmbeddedVibeMetadata(
            new[] { "Afrosounds" },
            Array.Empty<string>(),
            new[] { "Happy", "Party" });

        var vibe = TrackAnalysisBackgroundService.BuildVibeSemanticsCore(
            Output(), embedded, AfrosoundsTrack, TrackTags, null);

        // Genre anchored by embedded; style falls through to the winning lower tier.
        Assert.Equal(new[] { "Afrosounds" }, vibe.ResolvedGenres);
        Assert.Contains("Amapiano", vibe.ResolvedStyles);
        Assert.Equal(new[] { "Happy", "Party" }, vibe.ResolvedMoods);
    }

    private static string[] ParseSources(string? evidenceJson)
        => string.IsNullOrWhiteSpace(evidenceJson)
            ? Array.Empty<string>()
            : JsonDocument.Parse(evidenceJson).RootElement
                .EnumerateArray()
                .Select(item => item.GetProperty("source").GetString()!)
                .Distinct()
                .ToArray();
}
