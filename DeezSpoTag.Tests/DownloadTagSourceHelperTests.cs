using DeezSpoTag.Services.Download.Shared;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DownloadTagSourceHelperTests
{
    [Theory]
    [InlineData("engine", "engine")]
    [InlineData("spotify", "spotify")]
    [InlineData("deezer", "deezer")]
    [InlineData("apple", "apple")]
    [InlineData("qobuz", "qobuz")]
    [InlineData("tidal", "tidal")]
    [InlineData("amazon", "amazon")]
    [InlineData("unknown", "deezer")]
    public void NormalizeStoredSource_ReturnsExpectedValue(string input, string expected)
    {
        var actual = DownloadTagSourceHelper.NormalizeStoredSource(input, DownloadTagSourceHelper.DeezerSource);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("deezer", "deezer")]
    [InlineData("spotify", "spotify")]
    [InlineData("apple", "apple")]
    [InlineData("itunes", "apple")]
    [InlineData("qobuz", "qobuz")]
    [InlineData("tidal", "tidal")]
    [InlineData("amazon", "amazon")]
    [InlineData(null, null)]
    public void NormalizeResolvedDownloadTagSource_ReturnsExpectedValue(string? input, string? expected)
    {
        var actual = DownloadTagSourceHelper.NormalizeResolvedDownloadTagSource(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ResolveDownloadTagSource_UsesExplicitSourceBeforeEngineCandidates()
    {
        var actual = DownloadTagSourceHelper.ResolveDownloadTagSource("spotify", "deezer", "qobuz");

        Assert.Equal("spotify", actual);
    }

    [Fact]
    public void ResolveDownloadTagSource_UsesFirstSupportedEngineCandidate_WhenConfiguredToFollowEngine()
    {
        var actual = DownloadTagSourceHelper.ResolveDownloadTagSource("engine", "apple", "qobuz", "deezer");

        Assert.Equal("apple", actual);
    }

    [Fact]
    public void ResolveDownloadTagSource_ResolvesNonSpotifyNonDeezerEngines_WhenConfiguredToFollowEngine()
    {
        var actual = DownloadTagSourceHelper.ResolveDownloadTagSource("engine", "apple", "tidal", "amazon");

        Assert.Equal("apple", actual);
    }

    [Fact]
    public void ResolveDownloadTagSource_ReturnsNull_WhenNoSupportedEngineCandidateExists()
    {
        var actual = DownloadTagSourceHelper.ResolveDownloadTagSource("engine", "unknown");

        Assert.Null(actual);
    }
}
