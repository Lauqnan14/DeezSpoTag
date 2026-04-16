using DeezSpoTag.Services.Download.Shared;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DownloadTagSourceHelperTests
{
    [Theory]
    [InlineData("engine", "engine")]
    [InlineData("spotify", "spotify")]
    [InlineData("deezer", "deezer")]
    [InlineData("qobuz", "qobuz")]
    [InlineData("unknown", "deezer")]
    public void NormalizeStoredSource_ReturnsExpectedValue(string input, string expected)
    {
        var actual = DownloadTagSourceHelper.NormalizeStoredSource(input, DownloadTagSourceHelper.DeezerSource);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("deezer", "deezer")]
    [InlineData("spotify", "spotify")]
    [InlineData("qobuz", "qobuz")]
    [InlineData("apple", null)]
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

        Assert.Equal("qobuz", actual);
    }

    [Fact]
    public void ResolveDownloadTagSource_ReturnsNull_WhenNoSupportedEngineCandidateExists()
    {
        var actual = DownloadTagSourceHelper.ResolveDownloadTagSource("engine", "apple", "tidal", "amazon");

        Assert.Null(actual);
    }
}
