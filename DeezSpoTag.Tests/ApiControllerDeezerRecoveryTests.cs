using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
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
        var httpClientFactory = new FixedHttpClientFactory(new HttpClient());
        var musicServices = new ApiController.ApiControllerMusicServices(
            appleCatalog: null!,
            httpClientFactory: httpClientFactory,
            spotifyIdResolver: null!,
            spotifyArtworkResolver: null!,
            spotifyArtistService: null!);

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
            SpotifyHomeFeedRuntimeService = null!
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
        var httpClientFactory = new FixedHttpClientFactory(new HttpClient());
        var musicServices = new ApiController.ApiControllerMusicServices(
            appleCatalog: null!,
            httpClientFactory: httpClientFactory,
            spotifyIdResolver: null!,
            spotifyArtworkResolver: null!,
            spotifyArtistService: null!);

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
            SpotifyHomeFeedRuntimeService = null!
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

    private static string ReadAnonymousProperty(object item, string propertyName)
    {
        var value = item.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(item);
        return value?.ToString() ?? string.Empty;
    }

    private sealed class FixedHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public FixedHttpClientFactory(HttpClient client)
        {
            _client = client;
        }

        public HttpClient CreateClient(string name) => _client;
    }
}
