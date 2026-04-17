using System.Reflection;
using DeezSpoTag.Services.Download.Deezer;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DeezerEngineProcessorPayloadPreferenceTests
{
    [Theory]
    [InlineData("spotify", true)]
    [InlineData("deezer", false)]
    [InlineData("qobuz", false)]
    [InlineData("apple", false)]
    [InlineData(null, false)]
    public void ShouldPreferPayloadMetadata_OnlyWhenResolvedDownloadTagSourceIsSpotify(string? resolvedDownloadTagSource, bool expected)
    {
        var method = typeof(DeezerEngineProcessor).GetMethod(
            "ShouldPreferPayloadMetadata",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var actual = Assert.IsType<bool>(method!.Invoke(null, new object?[] { resolvedDownloadTagSource }));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("deezer", "spotify", "spotify")]
    [InlineData("spotify", "deezer", "deezer")]
    [InlineData("spotify", null, "spotify")]
    [InlineData(null, "spotify", "spotify")]
    [InlineData(null, null, "deezer")]
    public void ResolveTagSource_PrefersResolvedDownloadTagSourceThenPayloadThenDeezerDefault(
        string? payloadSourceService,
        string? resolvedDownloadTagSource,
        string expected)
    {
        var method = typeof(DeezerEngineProcessor).GetMethod(
            "ResolveTagSource",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var actual = Assert.IsType<string>(method!.Invoke(null, new object?[] { payloadSourceService, resolvedDownloadTagSource }));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("spotify", "sp-id", "dz-id", "sp-id")]
    [InlineData("spotify", null, "dz-id", "")]
    [InlineData("deezer", "sp-id", "dz-id", "dz-id")]
    [InlineData("qobuz", "sp-id", "dz-id", "dz-id")]
    public void ResolveTagSourceId_UsesPlatformSpecificSourceIdWithSafeFallback(
        string resolvedTagSource,
        string? spotifyId,
        string deezerId,
        string expected)
    {
        var method = typeof(DeezerEngineProcessor).GetMethod(
            "ResolveTagSourceId",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var payload = new DeezerQueueItem
        {
            SpotifyId = spotifyId ?? string.Empty,
            DeezerId = deezerId
        };

        var actual = Assert.IsType<string>(method!.Invoke(null, new object?[] { payload, resolvedTagSource }));
        Assert.Equal(expected, actual);
    }
}
