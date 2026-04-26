using System;
using System.Collections.Generic;
using DeezSpoTag.Web.Services.AutoTag;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class OneTaggerMatchingAccuracyTests
{
    [Fact]
    public void MatchTrack_RawExactWithDurationEvidence_CanReportPerfectAccuracy()
    {
        var info = new AutoTagAudioInfo
        {
            Title = "All Eyes On Me",
            Artist = "Artist",
            Artists = new List<string> { "Artist" },
            DurationSeconds = 180
        };
        var candidate = new TestTrack("All Eyes On Me", new List<string> { "Artist" }, TimeSpan.FromSeconds(180));

        var match = OneTaggerMatching.MatchTrack(
            info,
            new[] { candidate },
            StrictConfig(),
            Selectors(),
            matchArtist: true);

        Assert.NotNull(match);
        Assert.Equal(1.0, match!.Accuracy);
    }

    [Fact]
    public void MatchTrack_CleanedTitleFallback_DoesNotReportPerfectAccuracy()
    {
        var info = new AutoTagAudioInfo
        {
            Title = "All Eyes On Me (feat. Burna Boy)",
            Artist = "Artist",
            Artists = new List<string> { "Artist" },
            DurationSeconds = 180
        };
        var candidate = new TestTrack("All Eyes On Me", new List<string> { "Artist" }, null);

        var match = OneTaggerMatching.MatchTrack(
            info,
            new[] { candidate },
            StrictConfig(),
            Selectors(),
            matchArtist: true);

        Assert.NotNull(match);
        Assert.True(match!.Accuracy < 1.0);
        Assert.True(match.Accuracy >= StrictConfig().Strictness);
    }

    private static AutoTagMatchingConfig StrictConfig()
    {
        return new AutoTagMatchingConfig
        {
            Strictness = 0.7,
            MatchDuration = true,
            MaxDurationDifferenceSeconds = 4
        };
    }

    private static OneTaggerMatching.TrackSelectors<TestTrack> Selectors()
    {
        return new OneTaggerMatching.TrackSelectors<TestTrack>(
            track => track.Title,
            _ => null,
            track => track.Artists,
            track => track.Duration,
            _ => null);
    }

    private sealed record TestTrack(string Title, List<string> Artists, TimeSpan? Duration);
}
