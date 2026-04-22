using System;
using System.Reflection;
using System.Linq;
using System.Collections.Generic;
using DeezSpoTag.Services.Download.Deezer;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DeezerEngineProcessorEpisodeHelpersTests
{
    [Theory]
    [InlineData("/artist/show/track/listen.mp3", true)]
    [InlineData("/artist/show/track/stream.mp3", true)]
    [InlineData("/artist/show/track/download/?secret=1", true)]
    [InlineData("/artist/show/track/file.mp3", false)]
    public void IsHearThisTrackStreamPath_DetectsSupportedPatterns(string absolutePath, bool expected)
    {
        var method = typeof(DeezerEngineProcessor).GetMethod(
            "IsHearThisTrackStreamPath",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var actual = Assert.IsType<bool>(method!.Invoke(null, new object?[] { absolutePath }));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("https://stream56.hearthis.at/artist/track/listen.mp3?foo=bar", "https://stream56.hearthis.at/artist/track")]
    [InlineData("https://stream75.hearthis.at/artist/track/download/?secret=abc", "https://stream75.hearthis.at/artist/track")]
    public void BuildHearThisTrackPageUri_StripsStreamAndDownloadSuffixes(string inputUrl, string expected)
    {
        var method = typeof(DeezerEngineProcessor).GetMethod(
            "BuildHearThisTrackPageUri",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var actual = method!.Invoke(null, new object?[] { new Uri(inputUrl) }) as Uri;
        Assert.NotNull(actual);
        Assert.Equal(expected, actual!.ToString().TrimEnd('/'));
    }

    [Fact]
    public void BuildEpisodeRanges_CoversFullLengthInAscendingRanges()
    {
        var method = typeof(DeezerEngineProcessor).GetMethod(
            "BuildEpisodeRanges",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, new object?[] { 25_000_000L, 6 });
        var ranges = Assert.IsAssignableFrom<IEnumerable<(long Start, long End)>>(result).ToList();

        Assert.NotEmpty(ranges);
        Assert.Equal(0L, ranges[0].Start);
        Assert.Equal(25_000_000L - 1, ranges[^1].End);
        for (var index = 1; index < ranges.Count; index++)
        {
            Assert.Equal(ranges[index - 1].End + 1, ranges[index].Start);
            Assert.True(ranges[index].End >= ranges[index].Start);
        }
    }

    [Fact]
    public void ResolveFirstNonEmpty_ReturnsFirstNonBlankValue()
    {
        var method = typeof(DeezerEngineProcessor).GetMethod(
            "ResolveFirstNonEmpty",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var values = new string?[] { null, "   ", "", "kept", "other" };
        var actual = method!.Invoke(null, new object?[] { values }) as string;

        Assert.Equal("kept", actual);
    }
}
