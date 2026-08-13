using System;
using System.IO;
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
    [Theory]
    [InlineData(1200, 1200, true)]
    [InlineData(1000, 995, true)]
    [InlineData(1200, 630, false)]
    [InlineData(0, 1200, false)]
    public void ArtistArtworkDimensions_RequireSquareImage(int width, int height, bool expected)
    {
        Assert.Equal(expected, DownloadEngineArtworkHelper.IsSquareArtistArtworkDimensions(width, height));
    }

    [Theory]
    [InlineData("deezer", "spotify", "n", false)]
    [InlineData("deezer", "spotify", "y", true)]
    [InlineData("unknown", "spotify", "t", true)]
    [InlineData("spotify", "spotify", "y", false)]
    public void ExistingArtistArtwork_SeparatesProviderPreferenceFromOverwritePolicy(
        string currentProvider,
        string preferredProvider,
        string overwrite,
        bool expected)
    {
        Assert.Equal(
            expected,
            DownloadEngineArtworkHelper.ShouldRefreshExistingArtistArtwork(
                currentProvider,
                preferredProvider,
                overwrite));
    }

    [Fact]
    public void DownloadArtwork_ConsumesPersistedIdsWithoutResolvingAgain()
    {
        var repoRoot = Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var helperSource = File.ReadAllText(Path.Join(
            repoRoot,
            "DeezSpoTag.Services",
            "Download",
            "Shared",
            "DownloadEngineArtworkHelper.cs"));
        var postDownloadSource = File.ReadAllText(Path.Join(
            repoRoot,
            "DeezSpoTag.Services",
            "Download",
            "Shared",
            "EngineAudioPostDownloadHelper.cs"));
        var intentSource = File.ReadAllText(Path.Join(
            repoRoot,
            "DeezSpoTag.Web",
            "Services",
            "DownloadIntentService.cs"));

        Assert.DoesNotContain("ITrackIdentityResolver", helperSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ISpotifyIdResolver", helperSource, StringComparison.Ordinal);
        Assert.DoesNotContain("TrackIdentityResolutionRequest", helperSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ITrackIdentityResolver", postDownloadSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ISpotifyIdResolver", postDownloadSource, StringComparison.Ordinal);
        Assert.Contains("SpotifyId = execution.Request.Payload.SpotifyId", postDownloadSource, StringComparison.Ordinal);
        Assert.Contains("payload.AppleId", postDownloadSource, StringComparison.Ordinal);
        Assert.Contains("ResolveAppleArtworkIdentity(execution)", postDownloadSource, StringComparison.Ordinal);
        Assert.Contains("ArtworkFallbackHelper.ResolveOrder(settings)", intentSource, StringComparison.Ordinal);
        Assert.Contains("ArtworkFallbackHelper.ResolveArtistOrder(settings)", intentSource, StringComparison.Ordinal);
        Assert.Contains("LyricsSettingsPolicy.CanFetchLyrics(settings)", intentSource, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadPrefetchStatus_NamesAnimatedArtworkExplicitly()
    {
        var repoRoot = Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var postDownloadSource = File.ReadAllText(Path.Join(
            repoRoot,
            "DeezSpoTag.Services",
            "Download",
            "Shared",
            "EngineAudioPostDownloadHelper.cs"));

        Assert.Contains("parts.Add(\"animated artwork\")", postDownloadSource, StringComparison.Ordinal);
        Assert.Contains("DescribePrefetchWork", postDownloadSource, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Fetching artwork and lyrics\"", postDownloadSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveStandardAudioCoverUrlsAsync_ExcludesPayloadCover_WhenFallbackDisabled()
    {
        var settings = new DeezSpoTagSettings
        {
            ArtworkFallbackEnabled = false,
            ArtworkFallbackOrder = "apple,deezer",
            AppleArtworkSizeText = "640x640"
        };

        var result = await DownloadEngineArtworkHelper.ResolveStandardAudioCoverUrlsAsync(
            new DownloadEngineArtworkHelper.StandardAudioCoverResolveRequest(
                settings,
                AppleCatalog: null,
                HttpClientFactory: null,
                SpotifyArtworkResolver: null,
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
    public async Task ResolveStandardAudioCoverUrlsAsync_ExcludesKnownProviderPayloadCover_WhenSourceIsNotAllowed()
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

        Assert.Empty(result);
    }

    [Fact]
    public async Task ResolveStandardAudioCoverUrlsAsync_IncludesPayloadCover_AsFinalFallback_WhenFallbackEnabled()
    {
        var settings = new DeezSpoTagSettings
        {
            ArtworkFallbackEnabled = true,
            ArtworkFallbackOrder = "apple,deezer",
            AppleArtworkSizeText = "640x640"
        };

        var result = await DownloadEngineArtworkHelper.ResolveStandardAudioCoverUrlsAsync(
            new DownloadEngineArtworkHelper.StandardAudioCoverResolveRequest(
                settings,
                AppleCatalog: null,
                HttpClientFactory: null,
                SpotifyArtworkResolver: null,
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
    public async Task ResolveStandardAudioCoverUrlsAsync_NormalizesDeezerPayloadCoverToTechnicalSize()
    {
        var settings = new DeezSpoTagSettings
        {
            ArtworkFallbackEnabled = true,
            ArtworkFallbackOrder = "deezer",
            LocalArtworkSize = 1200
        };

        var result = await DownloadEngineArtworkHelper.ResolveStandardAudioCoverUrlsAsync(
            new DownloadEngineArtworkHelper.StandardAudioCoverResolveRequest(
                settings,
                AppleCatalog: null,
                HttpClientFactory: null,
                SpotifyArtworkResolver: null,
                DeezerClient: null,
                AppleId: null,
                Title: "Personal",
                Artist: "Shun Breezy",
                Album: "Personal",
                CollectionType: "track",
                DeezerId: "210143191",
                PayloadCover: "https://e-cdns-images.dzcdn.net/images/cover/34dce81dde8c29a07525d4c87b7878c5/500x500-000000-80-0-0.jpg",
                Isrc: null,
                Logger: NullLogger.Instance),
            CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(
            "https://e-cdns-images.dzcdn.net/images/cover/34dce81dde8c29a07525d4c87b7878c5/1000x1000-000000-80-0-0.jpg",
            result[0]);
    }

    [Fact]
    public async Task ResolveStandardAudioCoverUrlsAsync_DoesNotLetDeezerPayloadCoverOutrankEarlierProvider()
    {
        var settings = new DeezSpoTagSettings
        {
            ArtworkFallbackEnabled = true,
            ArtworkFallbackOrder = "spotify,deezer",
            LocalArtworkSize = 1000
        };
        var spotify = new TestSpotifyArtworkResolver
        {
            AlbumCoverUrl = "https://i.scdn.co/image/ab67616d0000b273spotifycover"
        };

        var result = await DownloadEngineArtworkHelper.ResolveStandardAudioCoverUrlsAsync(
            new DownloadEngineArtworkHelper.StandardAudioCoverResolveRequest(
                settings,
                AppleCatalog: null,
                HttpClientFactory: null,
                SpotifyArtworkResolver: spotify,
                DeezerClient: null,
                AppleId: null,
                Title: "Personal",
                Artist: "Shun Breezy",
                Album: "Personal",
                CollectionType: "track",
                DeezerId: "210143191",
                PayloadCover: "https://e-cdns-images.dzcdn.net/images/cover/34dce81dde8c29a07525d4c87b7878c5/500x500-000000-80-0-0.jpg",
                Isrc: null,
                Logger: NullLogger.Instance)
                {
                    SpotifyId = "spotify-track"
                },
            CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal("https://i.scdn.co/image/ab67616d0000b273spotifycover", result[0]);
        Assert.Equal(
            "https://e-cdns-images.dzcdn.net/images/cover/34dce81dde8c29a07525d4c87b7878c5/1000x1000-000000-80-0-0.jpg",
            result[1]);
    }

    [Fact]
    public async Task ResolveStandardAudioCoverUrlsAsync_PreservesUnknownPayloadCoverAsSafetyCandidate()
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
                DeezerClient: null,
                AppleId: null,
                Title: "Personal",
                Artist: "Shun Breezy",
                Album: "Personal",
                CollectionType: "track",
                DeezerId: null,
                PayloadCover: "https://covers.example.com/release/cover.jpg",
                Isrc: null,
                Logger: NullLogger.Instance),
            CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("https://covers.example.com/release/cover.jpg", result[0]);
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

        var result = await DownloadEngineArtworkHelper.ResolveArtistArtworkAsync(
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

        Assert.NotNull(result);
        Assert.Equal("https://lastfm.example/artist.jpg", result!.Url);
        Assert.Equal("lastfm", result.Provider);
        Assert.Equal("exact-name", result.ResolutionMethod);
        Assert.Equal("Da'Ville", resolver.RequestedArtist);
    }

    [Fact]
    public async Task ResolveArtistArtworkAsync_UsesDirectSpotifyArtistIdBeforeTrackAndName()
    {
        var settings = new DeezSpoTagSettings
        {
            ArtistArtworkFallbackEnabled = true,
            ArtistArtworkFallbackOrder = "spotify,apple,deezer"
        };
        var spotify = new TestSpotifyArtworkResolver
        {
            ArtistIdUrl = "https://i.scdn.co/image/artist-square"
        };

        var result = await DownloadEngineArtworkHelper.ResolveArtistArtworkAsync(
            new DownloadEngineArtworkHelper.ArtistImageResolveRequest(
                null,
                null,
                settings,
                null,
                spotify,
                null,
                null,
                null,
                "spotify-track",
                "Da'Ville",
                NullLogger.Instance)
            {
                SpotifyArtistId = "spotify-artist"
            },
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("spotify", result!.Provider);
        Assert.Equal("artist-id", result.ResolutionMethod);
        Assert.Equal("spotify-artist", result.ProviderArtistId);
        Assert.Equal(1, spotify.ArtistIdCalls);
        Assert.Equal(0, spotify.TrackCalls);
        Assert.Equal(0, spotify.NameCalls);
    }

    [Fact]
    public async Task ResolveArtistArtworkAsync_StopsAfterFirstConfiguredProviderSucceeds()
    {
        var settings = new DeezSpoTagSettings
        {
            ArtistArtworkFallbackEnabled = true,
            ArtistArtworkFallbackOrder = "lastfm,spotify"
        };
        var lastFm = new TestLastFmArtistImageResolver("https://lastfm.example/square.jpg");
        var spotify = new TestSpotifyArtworkResolver { NameUrl = "https://spotify.example/square.jpg" };

        var result = await DownloadEngineArtworkHelper.ResolveArtistArtworkAsync(
            new DownloadEngineArtworkHelper.ArtistImageResolveRequest(
                null,
                null,
                settings,
                null,
                spotify,
                lastFm,
                null,
                null,
                null,
                "Da'Ville",
                NullLogger.Instance),
            CancellationToken.None);

        Assert.Equal("lastfm", result?.Provider);
        Assert.Equal(0, spotify.NameCalls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://lastfm.freetls.fastly.net/i/u/300x300/2a96cbd8b46e442fc41c2b86b821562f.png")]
    [InlineData("https://lastfm-img.freetls.fastly.net/i/u/300x300/2a96cbd8b46e442fc41c2b86b821562f.png")]
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
                <img src="//lastfm-img.freetls.fastly.net/i/u/ar0/8b1f6f35d03f4af0b8a7cfb56fb11a99.jpg">
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
        Assert.Equal("https://lastfm-img.freetls.fastly.net/i/u/ar0/8b1f6f35d03f4af0b8a7cfb56fb11a99.jpg", result[0].Url);
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

    private sealed class TestSpotifyArtworkResolver : ISpotifyArtworkResolver
    {
        public string? AlbumCoverUrl { get; init; }
        public string? ArtistIdUrl { get; init; }
        public string? TrackUrl { get; init; }
        public string? NameUrl { get; init; }
        public int ArtistIdCalls { get; private set; }
        public int TrackCalls { get; private set; }
        public int NameCalls { get; private set; }

        public Task<string?> ResolveAlbumCoverUrlAsync(
            string? spotifyTrackId,
            CancellationToken cancellationToken,
            string? requestedAlbumTitle = null,
            bool rejectCompilationAlbumCandidate = false)
            => Task.FromResult(AlbumCoverUrl);

        public Task<string?> ResolveArtistImageUrlAsync(string? spotifyTrackId, CancellationToken cancellationToken)
        {
            TrackCalls++;
            return Task.FromResult(TrackUrl);
        }

        public Task<string?> ResolveArtistImageByArtistIdAsync(string? spotifyArtistId, CancellationToken cancellationToken)
        {
            ArtistIdCalls++;
            return Task.FromResult(ArtistIdUrl);
        }

        public Task<string?> ResolveArtistImageByNameAsync(string? artistName, CancellationToken cancellationToken)
        {
            NameCalls++;
            return Task.FromResult(NameUrl);
        }
    }
}
