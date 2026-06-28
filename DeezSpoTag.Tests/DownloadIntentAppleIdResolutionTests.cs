using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Web.Services.AutoTag;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DownloadIntentAppleIdResolutionTests
{
    [Theory]
    [InlineData("https://open.spotify.com/track/4gMgiXfqyzZLMhsksGmbQV", "SpotifyId", "4gMgiXfqyzZLMhsksGmbQV")]
    [InlineData("https://www.deezer.com/track/359542303", "DeezerId", "359542303")]
    [InlineData("https://music.apple.com/us/song/break-da-law/1447452170?i=1447452170", "AppleId", "1447452170")]
    [InlineData("https://tidal.com/track/451457120", "TidalId", "451457120")]
    [InlineData("https://play.qobuz.com/track/123456789", "QobuzId", "123456789")]
    [InlineData("https://music.amazon.com/albums/B012345678?trackAsin=B087654321", "AmazonId", "B087654321")]
    public void ApplySourceUrlIdentity_PopulatesIdsFromDirectEngineUrls(
        string sourceUrl,
        string propertyName,
        string expectedValue)
    {
        var intent = new DownloadIntent
        {
            SourceUrl = sourceUrl
        };
        var method = typeof(DownloadIntentService).GetMethod(
            "ApplySourceUrlIdentity",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        method!.Invoke(null, [intent]);

        var property = typeof(DownloadIntent).GetProperty(propertyName);
        Assert.NotNull(property);
        Assert.Equal(expectedValue, property!.GetValue(intent));
    }

    [Fact]
    public async Task ResolveAppleIdViaItunesMatcherAsync_ResolvesAppleIdFromTrackMetadata()
    {
        var intent = new DownloadIntent
        {
            SourceService = "tidal",
            SourceUrl = "https://tidal.com/track/451457120",
            Title = "break da law",
            Artist = "21 Savage",
            Album = "i am > i was",
            Isrc = "USSM12503434",
            DurationMs = 177000
        };

        var itunesPayload = """
        {
          "resultCount": 1,
          "results": [
            {
              "wrapperType": "track",
              "kind": "song",
              "artistName": "21 Savage",
              "collectionName": "i am > i was",
              "trackName": "break da law",
              "trackId": 1447452170,
              "trackTimeMillis": 177000,
              "isrc": "USSM12503434",
              "trackViewUrl": "https://music.apple.com/us/song/break-da-law/1447452170?i=1447452170"
            }
          ]
        }
        """;
        var itunesClient = new ItunesClient(
            new HttpClient(new StaticJsonHandler(itunesPayload), disposeHandler: true),
            NullLogger<ItunesClient>.Instance);
        itunesClient.SetRateLimit(-1);

        var service = (DownloadIntentService)RuntimeHelpers.GetUninitializedObject(typeof(DownloadIntentService));
        SetPrivateField(service, "_itunesMatcher", new ItunesMatcher(itunesClient));

        var method = typeof(DownloadIntentService).GetMethod(
            "ResolveAppleIdViaItunesMatcherAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var settings = new DeezSpoTagSettings
        {
            AppleMusic = new AppleMusicSettings
            {
                Storefront = "us"
            }
        };
        var task = (Task<string?>)method!.Invoke(service, new object?[]
        {
            intent,
            settings,
            CancellationToken.None
        })!;

        var resolved = await task;
        Assert.Equal("1447452170", resolved);
    }

    [Fact]
    public async Task ResolveAppleIdForStorefrontAsync_ResolvesByIsrc_WhenAppleIdIsMissing()
    {
        const string isrc = "USUM71605647";
        const string expectedAppleId = "1440871064";
        var storefront = "us";
        var cacheKey = $"apple:isrc:v2:{storefront}:{isrc}";

        using var cache = new MemoryCache(new MemoryCacheOptions());
        cache.Set(cacheKey, $$"""{"data":[{"id":"{{expectedAppleId}}"}]}""");

        var settingsService = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
        var catalog = new AppleMusicCatalogService(
            new ThrowingHttpClientFactory(),
            settingsService,
            NullLogger<AppleMusicCatalogService>.Instance,
            cache);

        var service = (DownloadIntentService)RuntimeHelpers.GetUninitializedObject(typeof(DownloadIntentService));
        SetPrivateField(service, "_appleCatalogService", catalog);
        SetPrivateField(service, "_logger", NullLogger<DownloadIntentService>.Instance);

        var method = typeof(DownloadIntentService).GetMethod(
            "ResolveAppleIdForStorefrontAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var settings = new DeezSpoTagSettings
        {
            AppleMusic = new AppleMusicSettings
            {
                Storefront = storefront
            }
        };

        var task = (Task<string?>)method!.Invoke(service, new object?[]
        {
            string.Empty,
            "https://example.invalid/no-apple-id",
            isrc,
            false,
            false,
            settings,
            CancellationToken.None
        })!;

        var resolved = await task;
        Assert.Equal(expectedAppleId, resolved);
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new ThrowingHandler(), disposeHandler: true);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException(
                "Unexpected outbound HTTP request in unit test.",
                null,
                HttpStatusCode.InternalServerError);
    }

    private sealed class StaticJsonHandler(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
    }
}
