using System;
using System.Collections.Generic;
using System.Reflection;
using DeezSpoTag.Web.Services.AutoTag;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class BoomplayReliabilityGuardTests
{
    private static readonly MethodInfo EvaluateBoomplayReliabilityGuardMethod =
        typeof(LocalAutoTagRunner).GetMethod(
            "EvaluateBoomplayReliabilityGuard",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("LocalAutoTagRunner.EvaluateBoomplayReliabilityGuard not found.");

    [Fact]
    public void EvaluateBoomplayReliabilityGuard_RejectsWeakArtistMatch()
    {
        var info = new AutoTagAudioInfo
        {
            Title = "Calm Down",
            Artist = "Rema",
            Artists = new List<string> { "Rema" },
            DurationSeconds = 240
        };
        var match = new AutoTagMatchResult
        {
            Accuracy = 0.99,
            Track = new AutoTagTrack
            {
                Title = "Calm Down",
                Artists = new List<string> { "Different Artist" },
                Duration = TimeSpan.FromSeconds(240)
            }
        };
        var matchingConfig = new AutoTagMatchingConfig
        {
            Strictness = 0.7,
            MatchDuration = true,
            MaxDurationDifferenceSeconds = 4
        };

        var reason = Assert.IsType<string>(EvaluateBoomplayReliabilityGuardMethod.Invoke(null, new object?[] { info, match, matchingConfig }));

        Assert.Contains("artist mismatch", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateBoomplayReliabilityGuard_RejectsDurationMismatchWithoutMatchingIsrc()
    {
        var info = new AutoTagAudioInfo
        {
            Title = "All Eyes On Me",
            Artist = "Burna Boy",
            Artists = new List<string> { "Burna Boy" },
            DurationSeconds = 240
        };
        var match = new AutoTagMatchResult
        {
            Accuracy = 0.99,
            Track = new AutoTagTrack
            {
                Title = "All Eyes On Me",
                Artists = new List<string> { "Burna Boy" },
                Duration = TimeSpan.FromSeconds(300)
            }
        };
        var matchingConfig = new AutoTagMatchingConfig
        {
            Strictness = 0.7,
            MatchDuration = true,
            MaxDurationDifferenceSeconds = 4
        };

        var reason = Assert.IsType<string>(EvaluateBoomplayReliabilityGuardMethod.Invoke(null, new object?[] { info, match, matchingConfig }));

        Assert.Contains("duration mismatch", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateBoomplayReliabilityGuard_RejectsLowAccuracyWhenIsrcMissing()
    {
        var info = new AutoTagAudioInfo
        {
            Title = "All Eyes On Me",
            Artist = "Burna Boy",
            Artists = new List<string> { "Burna Boy" },
            DurationSeconds = 240
        };
        var match = new AutoTagMatchResult
        {
            Accuracy = 0.75,
            Track = new AutoTagTrack
            {
                Title = "All Eyes On Me",
                Artists = new List<string> { "Burna Boy" },
                Duration = TimeSpan.FromSeconds(240)
            }
        };
        var matchingConfig = new AutoTagMatchingConfig
        {
            Strictness = 0.7,
            MatchDuration = true,
            MaxDurationDifferenceSeconds = 4
        };

        var reason = Assert.IsType<string>(EvaluateBoomplayReliabilityGuardMethod.Invoke(null, new object?[] { info, match, matchingConfig }));

        Assert.Contains("accuracy", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateBoomplayReliabilityGuard_AcceptsStrongCorroboratedMatch()
    {
        var info = new AutoTagAudioInfo
        {
            Title = "All Eyes On Me",
            Artist = "Burna Boy",
            Artists = new List<string> { "Burna Boy" },
            Isrc = "QMDA62565022",
            DurationSeconds = 240
        };
        var match = new AutoTagMatchResult
        {
            Accuracy = 1.0,
            Track = new AutoTagTrack
            {
                Title = "All Eyes On Me",
                Artists = new List<string> { "Burna Boy" },
                Isrc = "QMDA62565022",
                Duration = TimeSpan.FromSeconds(240)
            }
        };
        var matchingConfig = new AutoTagMatchingConfig
        {
            Strictness = 0.7,
            MatchDuration = true,
            MaxDurationDifferenceSeconds = 4
        };

        var reason = EvaluateBoomplayReliabilityGuardMethod.Invoke(null, new object?[] { info, match, matchingConfig });

        Assert.Null(reason);
    }
}
