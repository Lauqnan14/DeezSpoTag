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
    public async Task ResolveStandardAudioCoverUrlsAsync_ExcludesPayloadCover_WhenFallbackDisabled()
    {
        var settings = new DeezSpoTagSettings
        {
            ArtworkFallbackEnabled = false,
            ArtworkFallbackOrder = "apple,deezer"
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

        Assert.Empty(result);
    }

    [Fact]
    public async Task ResolveStandardAudioCoverUrlsAsync_ExcludesPayloadCover_WhenSingleSourcePreferred()
    {
        var settings = new DeezSpoTagSettings
        {
            ArtworkFallbackEnabled = true,
            ArtworkFallbackOrder = "apple"
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
                PayloadCover: "https://cdn-images.dzcdn.net/images/cover/example/1000x1000.jpg",
                Isrc: "USUG12506371",
                Logger: NullLogger.Instance),
            CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task ResolveStandardAudioCoverUrlsAsync_IncludesPayloadCover_AsFinalFallback_WhenFallbackEnabled()
    {
        var settings = new DeezSpoTagSettings
        {
            ArtworkFallbackEnabled = true,
            ArtworkFallbackOrder = "apple,deezer"
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
