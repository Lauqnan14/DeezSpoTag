using System;
using System.Linq;
using System.Reflection;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Web.Services.Vibe;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Last.fm evidence must preserve raw counts and relative weights
/// (count / highest count), keep the floor as a named constant, and classify
/// untyped community tags without inventing kinds.
/// </summary>
public sealed class VibeLastFmEvidenceTests
{
    [Fact]
    public void RelativeWeights_AreCountOverHighest()
    {
        var entries = new[] { ("amapiano", 100), ("south african", 71), ("party", 60), ("dance", 48), ("house", 31) };
        var build = typeof(LastFmTagService).GetMethod(
            "BuildEvidence",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(build);

        var result = Assert.IsType<System.Collections.Generic.List<LastFmTagService.LastFmTagEvidence>>(
            build!.Invoke(null, new object[] { entries.Select(e => (e.Item1, e.Item2)), VibeEvidenceScope.Track }));

        Assert.Equal(1.0, result[0].RelativeWeight);
        Assert.Equal(0.71, result[1].RelativeWeight);
        Assert.Equal(0.6, result[2].RelativeWeight);
        Assert.Equal(0.31, result[^1].RelativeWeight);
        Assert.Equal(100, result[0].Count);
        Assert.All(result, tag => Assert.Equal(VibeEvidenceScope.Track, tag.Scope));
    }

    [Fact]
    public void VibeWeightFloor_IsAConstantNotMagicNumber()
    {
        Assert.Equal(0.20, LastFmTagService.VibeRelativeWeightFloor);
        Assert.Equal(0.75, LastFmTagService.VibeTrackTagWeight);
        Assert.Equal(0.40, LastFmTagService.VibeArtistTagWeight);
    }

    [Theory]
    [InlineData("party", VibeSemanticKind.Mood)]
    [InlineData("Dance", VibeSemanticKind.Mood)]
    [InlineData("uplifting", VibeSemanticKind.Mood)]
    [InlineData("amapiano", VibeSemanticKind.Unknown)]
    [InlineData("south african", VibeSemanticKind.Unknown)]
    public void UntypedTags_ClassifyConservatively(string tag, VibeSemanticKind expected)
    {
        Assert.Equal(expected, VibeSemanticNormalizer.ClassifyLastFmTag(tag));
    }

    [Theory]
    [InlineData("rnb", "R&B")]
    [InlineData("R&B", "R&B")]
    [InlineData("rhythm and blues", "R&B")]
    [InlineData("afro house", "Afro-House")]
    [InlineData("afropop", "Afropop")]
    [InlineData("hip hop", "Hip-Hop")]
    public void Canonicalizer_NormalizesKnownAliases(string input, string expected)
    {
        Assert.Equal(expected, VibeSemanticNormalizer.Canonicalize(input));
    }

    [Fact]
    public void JunkTag_SeenLive_IsFilteredFromEvidence()
    {
        var entries = new[] { ("seen live", 500), ("amapiano", 100) };
        var build = typeof(LastFmTagService).GetMethod(
            "BuildEvidence",
            BindingFlags.NonPublic | BindingFlags.Static);
        var result = Assert.IsType<System.Collections.Generic.List<LastFmTagService.LastFmTagEvidence>>(
            build!.Invoke(null, new object[] { entries.Select(e => (e.Item1, e.Item2)), VibeEvidenceScope.Track }));

        Assert.Single(result);
        Assert.Equal("amapiano", result[0].Name);
        Assert.Equal(1.0, result[0].RelativeWeight);
    }
}
