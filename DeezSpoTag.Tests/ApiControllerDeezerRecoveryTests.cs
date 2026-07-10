using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Web.Controllers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ApiControllerDeezerRecoveryTests
{
    [Fact]
    public async Task RecoverDeezerTracksAsync_Returns_ThisTimeIPromise_ForDavilleQuery()
    {
        using var httpClient = CreateStubbedPublicDeezerClient();
        var httpClientFactory = new FixedHttpClientFactory(httpClient);
        var musicServices = new ApiController.ApiControllerMusicServices(
            appleCatalog: null!,
            httpClientFactory: httpClientFactory,
            spotifyIdResolver: null!,
            spotifyArtworkResolver: null!,
            spotifyArtistService: null!,
            amazonMusicMetadataService: null!);

        var controller = new ApiController(new ApiController.ApiControllerDependencies
        {
            Logger = NullLogger<ApiController>.Instance,
            DeezerClient = null!,
            DeezerGatewayService = null!,
            SettingsService = null!,
            LoginStorage = null!,
            LibraryConfigStore = null!,
            ArtistPageCache = null!,
            MusicServices = musicServices,
            TracklistSongCacheStore = null!,
            CrossDeviceSyncService = null!,
            SpotifyHomeFeedRuntimeService = null!,
            TidalAccessTokenProvider = null!
        });

        var recoverMethod = typeof(ApiController).GetMethod(
            "RecoverDeezerTracksAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RecoverDeezerTracksAsync method not found.");

        var task = recoverMethod.Invoke(
            controller,
            new object?[] { "da'ville - this time I promise", CancellationToken.None }) as Task<List<object>>;

        Assert.NotNull(task);
        var results = await task!;
        Assert.NotEmpty(results);

        var hasExpectedTrack = results.Any(item =>
            string.Equals(ReadAnonymousProperty(item, "deezerId"), "63392638", StringComparison.Ordinal)
            && string.Equals(ReadAnonymousProperty(item, "name"), "This Time I Promise", StringComparison.OrdinalIgnoreCase)
            && string.Equals(ReadAnonymousProperty(item, "artist"), "Da'ville", StringComparison.OrdinalIgnoreCase));

        Assert.True(hasExpectedTrack, "Expected Deezer track 63392638 (This Time I Promise by Da'ville) was not recovered.");
    }

    [Fact]
    public async Task SearchViaDeezerApiAsync_Returns_Track_FromPublicFallback_WhenSdkIsUnavailable()
    {
        using var httpClient = CreateStubbedPublicDeezerClient();
        var httpClientFactory = new FixedHttpClientFactory(httpClient);
        var musicServices = new ApiController.ApiControllerMusicServices(
            appleCatalog: null!,
            httpClientFactory: httpClientFactory,
            spotifyIdResolver: null!,
            spotifyArtworkResolver: null!,
            spotifyArtistService: null!,
            amazonMusicMetadataService: null!);

        var controller = new ApiController(new ApiController.ApiControllerDependencies
        {
            Logger = NullLogger<ApiController>.Instance,
            DeezerClient = null!,
            DeezerGatewayService = null!,
            SettingsService = null!,
            LoginStorage = null!,
            LibraryConfigStore = null!,
            ArtistPageCache = null!,
            MusicServices = musicServices,
            TracklistSongCacheStore = null!,
            CrossDeviceSyncService = null!,
            SpotifyHomeFeedRuntimeService = null!,
            TidalAccessTokenProvider = null!
        });

        var method = typeof(ApiController).GetMethod(
            "SearchViaDeezerApiAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SearchViaDeezerApiAsync method not found.");

        var task = method.Invoke(
            controller,
            new object?[] { "da'ville - this time I promise", CancellationToken.None });
        Assert.NotNull(task);

        await ((Task)task!);
        var result = task!.GetType().GetProperty("Result")!.GetValue(task);
        Assert.NotNull(result);

        var tracks = result!.GetType().GetProperty("Tracks")!.GetValue(result) as IEnumerable<object>;
        Assert.NotNull(tracks);

        var hasExpectedTrack = tracks!.Any(item =>
            string.Equals(ReadAnonymousProperty(item, "deezerId"), "63392638", StringComparison.Ordinal)
            && string.Equals(ReadAnonymousProperty(item, "name"), "This Time I Promise", StringComparison.OrdinalIgnoreCase)
            && string.Equals(ReadAnonymousProperty(item, "artist"), "Da'ville", StringComparison.OrdinalIgnoreCase));

        Assert.True(hasExpectedTrack, "Expected Deezer track 63392638 (This Time I Promise by Da'ville) was not returned by SearchViaDeezerApiAsync.");
    }

    [Fact]
    public void ShouldRunDeezerTrackRecovery_ReturnsTrue_ForPunctuationHeavyQuery_WhenMergedResultsDoNotMatch()
    {
        var method = typeof(ApiController).GetMethod(
            "ShouldRunDeezerTrackRecovery",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ShouldRunDeezerTrackRecovery method not found.");

        var mergedTracks = new List<object>
        {
            new { name = "Different Song", artist = "Another Artist" },
            new { name = "Unrelated Track", artist = "Unknown" }
        };

        var shouldRecover = method.Invoke(null, new object?[] { "da'ville - this time I promise", mergedTracks });
        Assert.IsType<bool>(shouldRecover);
        Assert.True((bool)shouldRecover!);
    }

    [Fact]
    public async Task RecoverDeezerTracksAsync_ReturnsEmpty_WhenPublicFallbackTimesOut()
    {
        using var httpClient = CreateTimeoutPublicDeezerClient();
        var controller = CreateController(httpClient);
        var recoverMethod = typeof(ApiController).GetMethod(
            "RecoverDeezerTracksAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RecoverDeezerTracksAsync method not found.");

        var task = recoverMethod.Invoke(
            controller,
            new object?[] { "da'ville - this time I promise", CancellationToken.None }) as Task<List<object>>;

        Assert.NotNull(task);
        var results = await task!;
        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchViaDeezerApiAsync_ReturnsEmptySections_WhenPublicFallbackTimesOut()
    {
        using var httpClient = CreateTimeoutPublicDeezerClient();
        var controller = CreateController(httpClient);
        var method = typeof(ApiController).GetMethod(
            "SearchViaDeezerApiAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SearchViaDeezerApiAsync method not found.");

        var task = method.Invoke(
            controller,
            new object?[] { "da'ville - this time I promise", CancellationToken.None });
        Assert.NotNull(task);

        await ((Task)task!);
        var result = task!.GetType().GetProperty("Result")!.GetValue(task);
        Assert.NotNull(result);

        var tracks = result!.GetType().GetProperty("Tracks")!.GetValue(result) as IEnumerable<object>;
        var albums = result.GetType().GetProperty("Albums")!.GetValue(result) as IEnumerable<object>;
        var artists = result.GetType().GetProperty("Artists")!.GetValue(result) as IEnumerable<object>;
        var playlists = result.GetType().GetProperty("Playlists")!.GetValue(result) as IEnumerable<object>;

        Assert.NotNull(tracks);
        Assert.NotNull(albums);
        Assert.NotNull(artists);
        Assert.NotNull(playlists);
        Assert.Empty(tracks!);
        Assert.Empty(albums!);
        Assert.Empty(artists!);
        Assert.Empty(playlists!);
    }

    private static string ReadAnonymousProperty(object item, string propertyName)
    {
        var value = item.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(item);
        return value?.ToString() ?? string.Empty;
    }

    private static ApiController CreateController(HttpClient httpClient)
    {
        var httpClientFactory = new FixedHttpClientFactory(httpClient);
        var musicServices = new ApiController.ApiControllerMusicServices(
            appleCatalog: null!,
            httpClientFactory: httpClientFactory,
            spotifyIdResolver: null!,
            spotifyArtworkResolver: null!,
            spotifyArtistService: null!,
            amazonMusicMetadataService: null!);

        return new ApiController(new ApiController.ApiControllerDependencies
        {
            Logger = NullLogger<ApiController>.Instance,
            DeezerClient = null!,
            DeezerGatewayService = null!,
            SettingsService = null!,
            LoginStorage = null!,
            LibraryConfigStore = null!,
            ArtistPageCache = null!,
            MusicServices = musicServices,
            TracklistSongCacheStore = null!,
            CrossDeviceSyncService = null!,
            SpotifyHomeFeedRuntimeService = null!,
            TidalAccessTokenProvider = null!
        });
    }

    private static HttpClient CreateStubbedPublicDeezerClient()
        => new(new StubHttpMessageHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var payload = path.EndsWith("/search/track", StringComparison.OrdinalIgnoreCase)
                ? """
                  {
                    "data": [
                      {
                        "id": 63392638,
                        "title": "This Time I Promise",
                        "link": "https://www.deezer.com/track/63392638",
                        "artist": {
                          "id": 7462,
                          "name": "Da'ville"
                        },
                        "album": {
                          "id": 6218718,
                          "title": "Krazy Love",
                          "cover_xl": "https://e-cdns-images.dzcdn.net/images/cover/test/1000x1000-000000-80-0-0.jpg"
                        }
                      }
                    ],
                    "total": 1
                  }
                  """
                : """{"data":[],"total":0}""";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
        }));

    private static HttpClient CreateTimeoutPublicDeezerClient()
        => new(new TimeoutHttpMessageHandler());

    private sealed class FixedHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public FixedHttpClientFactory(HttpClient client)
        {
            _client = client;
        }

        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private sealed class TimeoutHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(new OperationCanceledException("Simulated HttpClient timeout."));
    }
}
