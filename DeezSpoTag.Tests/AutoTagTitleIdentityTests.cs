using System;
using System.Collections.Generic;
using System.Reflection;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Core.Utils;
using DeezSpoTag.Web.Services.AutoTag;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AutoTagTitleIdentityTests
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
