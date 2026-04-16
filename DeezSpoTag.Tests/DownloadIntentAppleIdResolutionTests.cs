using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DownloadIntentAppleIdResolutionTests
{
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
}
