using System;
using System.Collections.Generic;
using System.Reflection;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Core.Utils;
using DeezSpoTag.Web.Services.AutoTag;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AutoTagTitleIdentityTest
{
    private static readonly MethodInfo ApplyTitleLossyOverwriteGuardMethod =
        typeof(LocalAutoTagRunner).GetMethod(
            "ApplyTitleLossyOverwriteGuard",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("LocalAutoTagRunner.ApplyTitleLossyOverwriteGuard not found.");

    private static readonly MethodInfo EvaluateGlobalMismatchGuardMethod =
        typeof(LocalAutoTagRunner).GetMethod(
            "EvaluateGlobalMismatchGuard",
            BindingFlags.NonPublic | BindingFlags.Static,
            [typeof(AutoTagAudioInfo), typeof(AutoTagMatchResult), typeof(AutoTagMatchingConfig)])
        ?? throw new InvalidOperationException("LocalAutoTagRunner.EvaluateGlobalMismatchGuard not found.");

    private static readonly MethodInfo EvaluateGlobalMismatchGuardWithTrustMethod =
        typeof(LocalAutoTagRunner).GetMethod(
            "EvaluateGlobalMismatchGuard",
            BindingFlags.NonPublic | BindingFlags.Static,
            [typeof(AutoTagAudioInfo), typeof(AutoTagMatchResult), typeof(AutoTagMatchingConfig), typeof(string), typeof(bool)])
        ?? throw new InvalidOperationException("LocalAutoTagRunner.EvaluateGlobalMismatchGuard(trust) not found.");

    private static readonly MethodInfo RestoreTrustedCoreIdentityMethod =
        typeof(LocalAutoTagRunner).GetMethod(
            "RestoreTrustedCoreIdentity",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("LocalAutoTagRunner.RestoreTrustedCoreIdentity not found.");

    private static readonly MethodInfo AddResolvedIdentityIfMissingMethod =
        typeof(LocalAutoTagRunner).GetMethod(
            "AddResolvedIdentityIfMissing",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("LocalAutoTagRunner.AddResolvedIdentityIfMissing not found.");

    [Fact]
    public void TrustedCoreIdentity_IsRestoredAfterExternalResolution()
    {
        var original = new AutoTagAudioInfo
        {
            Title = "Slut",
            Artist = "Kyst",
            Artists = ["Kyst"],
            Album = "Slut",
            Isrc = "QZTB32437342",
            HasEmbeddedTitle = true,
            HasEmbeddedArtist = true
        };
        var working = new AutoTagAudioInfo
        {
            Title = "POUNDS",
            Artist = "Armanii",
            Artists = ["Armanii"],
            Album = "NO SIGNAL",
            Isrc = "different-isrc",
            HasEmbeddedTitle = true,
            HasEmbeddedArtist = true
        };

        RestoreTrustedCoreIdentityMethod.Invoke(null, [original, working, "/downloads/Kyst/Slut/01 - Slut.flac"]);

        Assert.Equal("Slut", working.Title);
        Assert.Equal("Kyst", working.Artist);
        Assert.Equal(["Kyst"], working.Artists);
        Assert.Equal("Slut", working.Album);
        Assert.Equal("QZTB32437342", working.Isrc);
    }

    [Fact]
    public void WeakCoreIdentity_RemainsEligibleForExternalRecovery()
    {
        var original = new AutoTagAudioInfo
        {
            Title = "unknown",
            Artist = "unknown",
            Artists = ["unknown"],
            HasEmbeddedTitle = true,
            HasEmbeddedArtist = true
        };
        var working = new AutoTagAudioInfo
        {
            Title = "Recovered Title",
            Artist = "Recovered Artist",
            Artists = ["Recovered Artist"],
            Album = "Recovered Album",
            Isrc = "recovered-isrc",
            HasEmbeddedTitle = true,
            HasEmbeddedArtist = true
        };

        RestoreTrustedCoreIdentityMethod.Invoke(null, [original, working, "/downloads/unknown/01 - track.flac"]);

        Assert.Equal("Recovered Title", working.Title);
        Assert.Equal("Recovered Artist", working.Artist);
        Assert.Equal(["Recovered Artist"], working.Artists);
        Assert.Equal("Recovered Album", working.Album);
        Assert.Equal("recovered-isrc", working.Isrc);
    }

    [Fact]
    public void ResolvedIdentity_FillsMissingIdsWithoutReplacingExistingIds()
    {
        var info = new AutoTagAudioInfo
        {
            Tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["SPOTIFY_TRACK_ID"] = ["4DRdx3ZYIeQY7Hqy4qpAEu"]
            }
        };

        AddResolvedIdentityIfMissingMethod.Invoke(null, [info, "SPOTIFY_TRACK_ID", "wrong-id"]);
        AddResolvedIdentityIfMissingMethod.Invoke(null, [info, "DEEZER_TRACK_ID", "2938015811"]);
        AddResolvedIdentityIfMissingMethod.Invoke(null, [info, "TIDAL_TRACK_ID", " "]);

        Assert.Equal(["4DRdx3ZYIeQY7Hqy4qpAEu"], info.Tags["SPOTIFY_TRACK_ID"]);
        Assert.Equal(["2938015811"], info.Tags["DEEZER_TRACK_ID"]);
        Assert.False(info.Tags.ContainsKey("TIDAL_TRACK_ID"));
    }

    [Theory]
    [InlineData("musicbrainz")]
    [InlineData("spotify")]
    [InlineData("deezer")]
    [InlineData("itunes")]
    [InlineData("discogs")]
    [InlineData("beatport")]
    [InlineData("boomplay")]
    [InlineData("bandcamp")]
    [InlineData("traxsource")]
    [InlineData("bpmsupreme")]
    [InlineData("shazam")]
    [InlineData("lastfm")]
    public void OverwriteGuard_DoesNotReplaceSourceTitleWithNearMissAlternative(string platformId)
    {
        var effective = new TagSettings { Title = true };
        var incoming = new AutoTagTrack { Title = "Hold Me Closer" };

        ApplyTitleLossyOverwriteGuardMethod.Invoke(
            null,
            [effective, incoming, "Hold Me Close", platformId]);

        Assert.False(effective.Title);
        Assert.Equal("Hold Me Close", incoming.Title);
    }

    [Theory]
    [InlineData("musicbrainz")]
    [InlineData("spotify")]
    [InlineData("deezer")]
    [InlineData("itunes")]
    [InlineData("discogs")]
    [InlineData("beatport")]
    [InlineData("boomplay")]
    [InlineData("bandcamp")]
    [InlineData("traxsource")]
    [InlineData("bpmsupreme")]
    [InlineData("shazam")]
    [InlineData("lastfm")]
    public void OverwriteGuard_StillAllowsPunctuationAndEditionNormalizations(string platformId)
    {
        var effective = new TagSettings { Title = true };
        var incoming = new AutoTagTrack { Title = "Hey Girl" };

        ApplyTitleLossyOverwriteGuardMethod.Invoke(
            null,
            [effective, incoming, "Hey, Girl", platformId]);

        Assert.True(effective.Title);
        Assert.Equal("Hey Girl", incoming.Title);
    }

    [Theory]
    [InlineData("musicbrainz")]
    [InlineData("spotify")]
    [InlineData("shazam")]
    public void OverwriteGuard_AllowsUnrelatedTitleCorrections(string platformId)
    {
        var effective = new TagSettings { Title = true };
        var incoming = new AutoTagTrack { Title = "Resolved Title" };

        ApplyTitleLossyOverwriteGuardMethod.Invoke(
            null,
            [effective, incoming, "Incorrect old title", platformId]);

        Assert.True(effective.Title);
        Assert.Equal("Resolved Title", incoming.Title);
    }

    [Fact]
    public void SharedMatcher_RejectsNearMissAlternativeTitlesUsedByAllTextPlatforms()
    {
        var info = new AutoTagAudioInfo
        {
            Title = "Hold Me Close",
            Artist = "Same Artist",
            Artists = ["Same Artist"],
            DurationSeconds = 210
        };
        var candidate = new TestTrack("Hold Me Closer", ["Same Artist"], TimeSpan.FromSeconds(210));

        var match = OneTaggerMatching.MatchTrack(
            info,
            new[] { candidate },
            new AutoTagMatchingConfig
            {
                Strictness = 0.7,
                MatchDuration = true,
                MaxDurationDifferenceSeconds = 4
            },
            new OneTaggerMatching.TrackSelectors<TestTrack>(
                track => track.Title,
                _ => null,
                track => track.Artists,
                track => track.Duration,
                _ => null),
            matchArtist: true);

        Assert.Null(match);
    }

    [Fact]
    public void QualityGuard_RejectsNearMissAlternativeTitleForSearchMatches()
    {
        var info = new AutoTagAudioInfo
        {
            Title = "Hold Me Close",
            Artist = "Same Artist",
            Artists = ["Same Artist"],
            DurationSeconds = 210
        };
        var match = new AutoTagMatchResult
        {
            Accuracy = 0.95,
            MatchStrategy = "text",
            Track = new AutoTagTrack
            {
                Title = "Hold Me Closer",
                Artists = ["Same Artist"],
                Duration = TimeSpan.FromSeconds(210)
            }
        };

        var reason = EvaluateGlobalMismatchGuardMethod.Invoke(
            null,
            [
                info,
                match,
                new AutoTagMatchingConfig
                {
                    Strictness = 0.7,
                    MatchDuration = true,
                    MaxDurationDifferenceSeconds = 4
                }
            ]) as string;

        Assert.Equal("match rejected by quality guard (title identity)", reason);
    }

    [Theory]
    [InlineData("id", null)]
    [InlineData("text", "QZTB32437342")]
    public void QualityGuard_RejectsCrossTrackMatchBeforeIdentityShortcuts(string matchStrategy, string? candidateIsrc)
    {
        var source = new AutoTagAudioInfo
        {
            Title = "Slut",
            Artist = "Kyst",
            Artists = ["Kyst"],
            Isrc = "QZTB32437342",
            HasEmbeddedTitle = true,
            HasEmbeddedArtist = true
        };
        var match = new AutoTagMatchResult
        {
            Accuracy = 1,
            MatchStrategy = matchStrategy,
            Track = new AutoTagTrack
            {
                Title = "POUNDS",
                Artists = ["Armanii"],
                Isrc = candidateIsrc
            }
        };

        var reason = EvaluateGlobalMismatchGuardWithTrustMethod.Invoke(
            null,
            [source, match, new AutoTagMatchingConfig { Strictness = 0.7 }, "/downloads/Kyst/Slut/01 - Slut.flac", false]) as string;

        Assert.Equal("match rejected by quality guard (title identity)", reason);
    }

    [Fact]
    public void QualityGuard_AllowsCompatibleAuthoritativeIdMatch()
    {
        var source = new AutoTagAudioInfo
        {
            Title = "Slut",
            Artist = "Kyst",
            Artists = ["Kyst"],
            HasEmbeddedTitle = true,
            HasEmbeddedArtist = true
        };
        var match = new AutoTagMatchResult
        {
            Accuracy = 1,
            MatchStrategy = "id",
            Track = new AutoTagTrack { Title = "Slut", Artists = ["Kyst"] }
        };

        var reason = EvaluateGlobalMismatchGuardWithTrustMethod.Invoke(
            null,
            [source, match, new AutoTagMatchingConfig { Strictness = 0.7 }, "/downloads/Kyst/Slut/01 - Slut.flac", false]) as string;

        Assert.Null(reason);
    }

    [Fact]
    public void TitleIdentity_TreatsPunctuationAsSameWorkAndNearMissAsDifferentWork()
    {
        Assert.True(TrackTitleMatcher.HasCompatibleTitleIdentity("Hey, Girl", "Hey Girl"));
        Assert.True(TrackTitleMatcher.HasCompatibleTitleIdentity("She's Hot", "Shes Hot"));
        Assert.False(TrackTitleMatcher.HasCompatibleTitleIdentity("Hold Me Close", "Hold Me Closer"));
        Assert.False(TrackTitleMatcher.HasCompatibleTitleIdentity("Close", "Closer"));
    }

    private sealed record TestTrack(string Title, List<string> Artists, TimeSpan? Duration);
}
