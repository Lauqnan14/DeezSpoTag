using System;
using System.Collections.Generic;
using System.Reflection;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Web.Services.AutoTag;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class BoomplayMatcherGuardrailTests
{
    private static readonly MethodInfo CollectCandidateIdsMethod =
        typeof(BoomplayMatcher).GetMethod(
            "CollectCandidateIds",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BoomplayMatcher.CollectCandidateIds not found.");

    private static readonly MethodInfo IsIdMatchCandidateConsistentMethod =
        typeof(BoomplayMatcher).GetMethod(
            "IsIdMatchCandidateConsistent",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BoomplayMatcher.IsIdMatchCandidateConsistent not found.");

    [Fact]
    public void CollectCandidateIds_IgnoresNonBoomplaySongUrls()
    {
        var info = new AutoTagAudioInfo
        {
            Tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["URL"] = new List<string> { "https://example.com/songs/4133539" },
                ["WWWAUDIOFILE"] = new List<string> { "https://www.deezer.com/track/4609601" }
            }
        };

        var ids = Assert.IsType<List<string>>(CollectCandidateIdsMethod.Invoke(null, new object?[] { info }));

        Assert.Empty(ids);
    }

    [Fact]
    public void IsIdMatchCandidateConsistent_RejectsInstrumentalTitleDrift()
    {
        var info = new AutoTagAudioInfo
        {
            Title = "Spotlight (feat. Usher)",
            Artist = "Gucci Mane",
            Artists = new List<string> { "Gucci Mane" },
            Isrc = "USWB10904424"
        };
        var candidate = new BoomplayTrackMetadata
        {
            Id = "4133539",
            Title = "Spotlight (Instrumental)",
            Artist = "Gucci Mane",
            Isrc = "USWB10904424"
        };
        var matchingConfig = new AutoTagMatchingConfig
        {
            Strictness = 0.7,
            MatchDuration = true,
            MaxDurationDifferenceSeconds = 4
        };

        var isConsistent = Assert.IsType<bool>(IsIdMatchCandidateConsistentMethod.Invoke(null, new object?[] { info, candidate, matchingConfig }));

        Assert.False(isConsistent);
    }

    [Fact]
    public void IsIdMatchCandidateConsistent_RejectsWhenIsrcConflicts()
    {
        var info = new AutoTagAudioInfo
        {
            Title = "Like This",
            Artist = "Marques Houston",
            Artists = new List<string> { "Marques Houston" },
            Isrc = "QMHSJ1300043"
        };
        var candidate = new BoomplayTrackMetadata
        {
            Id = "9999999",
            Title = "Like This",
            Artist = "Marques Houston",
            Isrc = "USWB10904424"
        };
        var matchingConfig = new AutoTagMatchingConfig
        {
            Strictness = 0.7,
            MatchDuration = true,
            MaxDurationDifferenceSeconds = 4
        };

        var isConsistent = Assert.IsType<bool>(IsIdMatchCandidateConsistentMethod.Invoke(null, new object?[] { info, candidate, matchingConfig }));

        Assert.False(isConsistent);
    }
}
