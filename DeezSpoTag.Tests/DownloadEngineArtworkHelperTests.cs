using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Web.Services;
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
                CollectionType: "track",
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
                CollectionType: "track",
                DeezerId: "3466216111",
                PayloadCover: "https://cdn-images.dzcdn.net/images/cover/example/1000x1000.jpg",
                Isrc: "USUG12506371",
                Logger: NullLogger.Instance),
            CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(
            "https://cdn-images.dzcdn.net/images/cover/example/1000x1000.jpg",
            result[0]);
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
                CollectionType: "track",
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

    [Fact]
    public async Task ResolveArtistImageUrlAsync_UsesLastFm_WhenArtistOrderIncludesLastFm()
    {
        var settings = new DeezSpoTagSettings
        {
            ArtistArtworkFallbackEnabled = true,
            ArtistArtworkFallbackOrder = "lastfm"
        };
        var resolver = new TestLastFmArtistImageResolver("https://lastfm.example/artist.jpg");

        var result = await DownloadEngineArtworkHelper.ResolveArtistImageUrlAsync(
            new DownloadEngineArtworkHelper.ArtistImageResolveRequest(
                AppleCatalog: null,
                HttpClientFactory: null,
                settings,
                DeezerClient: null,
                SpotifyArtworkResolver: null,
                LastFmArtistImageResolver: resolver,
                AppleId: null,
                DeezerId: null,
                SpotifyId: null,
                Artist: "Da'Ville",
                Logger: NullLogger.Instance),
            CancellationToken.None);

        Assert.Equal("https://lastfm.example/artist.jpg", result);
        Assert.Equal("Da'Ville", resolver.RequestedArtist);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://lastfm.freetls.fastly.net/i/u/300x300/2a96cbd8b46e442fc41c2b86b821562f.png")]
    public void LastFmArtistImageService_RejectsInvalidAndPlaceholderUrls(string url)
    {
        Assert.False(LastFmArtistImageService.IsValidImageUrl(url));
    }

    [Fact]
    public void LastFmArtistImageService_ExtractsGalleryImagesAndRejectsPlaceholders()
    {
        const string html = """
            <h2 class="subpage-title">Photos</h2>
            <a href="/music/Alicio/+images/8b1f6f35d03f4af0b8a7cfb56fb11a99">
                <img src="https://lastfm.freetls.fastly.net/i/u/300x300/2a96cbd8b46e442fc41c2b86b821562f.jpg">
            </a>
            <a href="/music/Alicio/+images/8b1f6f35d03f4af0b8a7cfb56fb11a99">
                <img src="//lastfm.freetls.fastly.net/i/u/ar0/8b1f6f35d03f4af0b8a7cfb56fb11a99.jpg">
            </a>
            <a href="/music/Alicio/+images/8b1f6f35d03f4af0b8a7cfb56fb11a99">
                <img src="https://lastfm.freetls.fastly.net/i/u/300x300/8b1f6f35d03f4af0b8a7cfb56fb11a99.jpg">
            </a>
            <a href="/music/Alicio/+images/57c3d7c02f5e4a66b325c4dc9f0cf02a">
                <img src="https://lastfm.freetls.fastly.net/i/u/300x300/57c3d7c02f5e4a66b325c4dc9f0cf02a.jpg">
            </a>
            <section class="similar-albums-body">
                <a href="/music/Other+Artist">
                    <img src="https://lastfm.freetls.fastly.net/i/u/300x300/9904b1b63f4449f0a42299d74d7b1910.jpg">
                </a>
            </section>
            """;

        var result = LastFmArtistImageService.ExtractGalleryImages(html, 8, "Alicio");

        Assert.Equal(2, result.Count);
        Assert.Equal("https://lastfm.freetls.fastly.net/i/u/ar0/8b1f6f35d03f4af0b8a7cfb56fb11a99.jpg", result[0].Url);
        Assert.Equal("Last.fm gallery", result[0].Label);
        Assert.All(result, candidate => Assert.Equal("lastfm", candidate.Source));
    }

    private sealed class TestLastFmArtistImageResolver : ILastFmArtistImageResolver
    {
        private readonly string? _url;

        public TestLastFmArtistImageResolver(string? url)
        {
            _url = url;
        }

        public string? RequestedArtist { get; private set; }

        public Task<string?> ResolveArtistImageByNameAsync(string? artistName, CancellationToken cancellationToken)
        {
            RequestedArtist = artistName;
            return Task.FromResult(_url);
        }
    }
}
