using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DownloadEngineArtworkHelperTests
{
    [Fact]
    public async Task ResolveStandardAudioCoverUrlsAsync_IncludesPayloadCover_AsFallback()
    {
        var settings = new DeezSpoTagSettings
        {
            ArtworkFallbackEnabled = false,
            ArtworkFallbackOrder = string.Empty
        };

        var result = await DownloadEngineArtworkHelper.ResolveStandardAudioCoverUrlsAsync(
            new DownloadEngineArtworkHelper.StandardAudioCoverResolveRequest(
                settings,
                AppleCatalog: null,
                HttpClientFactory: null,
                SpotifyArtworkResolver: null,
                SpotifyIdResolver: null,
                DeezerClient: null,
                AppleId: null,
                Title: "Hot Body",
                Artist: "Ayra Starr",
                Album: "Hot Body",
                DeezerId: "3466216111",
                PayloadCover: "https://is1-ssl.mzstatic.com/image/thumb/Music211/v4/x/y/z/cover.jpg/640x640bb.jpg",
                Isrc: "USUG12506371",
                Logger: NullLogger.Instance),
            CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(
            "https://is1-ssl.mzstatic.com/image/thumb/Music211/v4/x/y/z/cover.jpg/640x640bb.jpg",
            result[0]);
    }
}
