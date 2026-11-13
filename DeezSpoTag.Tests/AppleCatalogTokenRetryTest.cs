using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     Apple catalog developer-token recovery regressions. Account-access failures must not
///     invalidate the shared developer token, delayed failures must not invalidate a token
///     refreshed by another request, and token acquisition must respect its budget.
/// </summary>
public sealed class AppleCatalogTokenRetryTest
{
    private const string ConfiguredToken = "eyJhbGciOiJIUzI1NiJ9.eyJ1c2VyIjoiY29uZmlndXJlZCJ9.c2lnbmF0dXJlQWJj";
    private const string ScrapedTokenOne = "eyJhbGciOiJIUzI1NiJ9.eyJ1c2VyIjoic2NyYXBlZC0xIn0.c2lnbmF0dXJlMQ";
    private const string ScrapedTokenTwo = "eyJhbGciOiJIUzI1NiJ9.eyJ1c2VyIjoic2NyYXBlZC0yIn0.c2lnbmF0dXJlMg";
    private const string MediaUserToken = "media-user-token-0123456789";

    private sealed class ProbeHandler : HttpMessageHandler
    {
        public int CatalogRequests;
        public int ScrapePageRequests;
        private readonly ConcurrentQueue<Func<HttpResponseMessage>> _catalogResponses = new();
        public TimeSpan CatalogDelay = TimeSpan.Zero;
        public bool HoldCatalog;
        public TaskCompletionSource RequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCatalog = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TimeSpan DefaultScrapeDelay = TimeSpan.Zero;
        public bool ScrapeFails;

        public void EnqueueCatalogResponse(Func<HttpResponseMessage> response) => _catalogResponses.Enqueue(response);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host.StartsWith("amp-api", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref CatalogRequests);
                RequestStarted.TrySetResult();
                if (HoldCatalog) await ReleaseCatalog.Task.WaitAsync(cancellationToken);
                if (CatalogDelay > TimeSpan.Zero) await Task.Delay(CatalogDelay, cancellationToken);
                if (_catalogResponses.TryDequeue(out var next)) return next();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json")
                };
            }

            Interlocked.Increment(ref ScrapePageRequests);
            if (ScrapeFails) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (DefaultScrapeDelay > TimeSpan.Zero) await Task.Delay(DefaultScrapeDelay, cancellationToken);
            var jwt = ScrapePageRequests switch
            {
                1 => ScrapedTokenOne,
                _ => ScrapedTokenTwo
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"<html>\"{jwt}\"</html>", Encoding.UTF8, "text/html")
            };
        }
    }

    private static (AppleMusicCatalogService Service, ProbeHandler Handler) CreateService(
        string? configuredToken, string directory, ProbeHandler? handler = null)
    {
        var configFolder = System.IO.Path.Join(directory, "config");
        System.IO.Directory.CreateDirectory(configFolder);
        System.IO.Directory.CreateDirectory(System.IO.Path.Join(directory, "autotag"));
        System.IO.File.WriteAllText(System.IO.Path.Join(directory, "autotag", "applemusic.json"),
            System.Text.Json.JsonSerializer.Serialize(new { authorizationToken = configuredToken, mediaUserToken = "" }));
        var settings = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
        foreach (var (field, value) in new[]
                 {
                     ("_configFolder", configFolder), ("_settingsFilePath", System.IO.Path.Join(configFolder, "config.json"))
                 })
        {
            typeof(DeezSpoTagSettingsService)
                .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(settings, value);
        }
        var config = configuredToken is null
            ? "{\"appleMusic\":{\"storefront\":\"us\",\"mediaUserToken\":\"\"}}"
            : "{\"appleMusic\":{\"storefront\":\"us\",\"mediaUserToken\":\"\",\"authorizationToken\":\"" + configuredToken + "\"}}";
        System.IO.File.WriteAllText(System.IO.Path.Join(configFolder, "config.json"), config);
        var probe = handler ?? new ProbeHandler();
        var service = new AppleMusicCatalogService(
            new ClientFactory(probe), settings, NullLogger<AppleMusicCatalogService>.Instance,
            new MemoryCache(new MemoryCacheOptions()));
        return (service, probe);
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static void CacheToken(AppleMusicCatalogService service, string token)
    {
        typeof(AppleMusicCatalogService)
            .GetMethod("CacheToken", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [token, DateTimeOffset.UtcNow.AddHours(1)]);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task AccountAccessForbiddenDoesNotInvalidateTheDeveloperToken(HttpStatusCode status)
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("apple-token-403-").FullName;
        try
        {
            var (service, handler) = CreateService(ConfiguredToken, directory);
            handler.EnqueueCatalogResponse(() => new HttpResponseMessage(status));
            await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
                service.GetSongAsync("1234", "us", "en-US", CancellationToken.None, MediaUserToken));
            // Terminal for the request: no invalidation loop, no retries, no rescrapes.
            Assert.Equal(1, handler.CatalogRequests);
            Assert.Equal(0, handler.ScrapePageRequests);
            Assert.Equal(ConfiguredToken, await service.GetCatalogTokenAsync(CancellationToken.None));
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ConcurrentAccountRejectionsDoNotTriggerDeveloperTokenScraping()
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("apple-concurrent-account-").FullName;
        try
        {
            var (service, handler) = CreateService(ConfiguredToken, directory);
            handler.CatalogDelay = TimeSpan.FromMilliseconds(30);
            var requests = new System.Collections.Generic.List<Task>();
            for (var index = 0; index < 8; index++) handler.EnqueueCatalogResponse(() => new HttpResponseMessage(HttpStatusCode.Forbidden));
            for (var index = 0; index < 8; index++)
                requests.Add(Assert.ThrowsAnyAsync<HttpRequestException>(() => service.GetSongAsync(index.ToString(System.Globalization.CultureInfo.InvariantCulture), "us", "en-US", CancellationToken.None, MediaUserToken)));
            await Task.WhenAll(requests);
            Assert.Equal(8, handler.CatalogRequests);
            Assert.Equal(0, handler.ScrapePageRequests);
            Assert.Equal(ConfiguredToken, await service.GetCatalogTokenAsync(CancellationToken.None));
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task DelayedAccountForbiddenPreservesTokenRefreshedByAnotherRequest()
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("apple-token-race-").FullName;
        try
        {
            var (service, handler) = CreateService(ConfiguredToken, directory);
            handler.HoldCatalog = true;
            handler.EnqueueCatalogResponse(() => new HttpResponseMessage(HttpStatusCode.Forbidden));
            var request = service.GetSongAsync("1234", "us", "en-US", CancellationToken.None, MediaUserToken);
            // Another request refreshes the shared token while the delayed failure is in flight.
            await handler.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            CacheToken(service, ScrapedTokenTwo);
            handler.ReleaseCatalog.TrySetResult();
            await Assert.ThrowsAnyAsync<HttpRequestException>(() => request);
            // The delayed failure observed the older token and must not invalidate the newer one.
            Assert.Equal(ScrapedTokenTwo, await service.GetCatalogTokenAsync(CancellationToken.None));
            Assert.Equal(1, handler.CatalogRequests);
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task DelayedUnauthorizedDoesNotInvalidateNewerTokenOrRescrape()
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("apple-token-401-race-").FullName;
        try
        {
            var (service, handler) = CreateService(ConfiguredToken, directory);
            handler.HoldCatalog = true;
            handler.EnqueueCatalogResponse(() => new HttpResponseMessage(HttpStatusCode.Unauthorized));
            handler.EnqueueCatalogResponse(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[]}") });
            var request = service.GetSongAsync("1234", "us", "en-US", CancellationToken.None);
            await handler.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            CacheToken(service, ScrapedTokenTwo);
            handler.ReleaseCatalog.TrySetResult();
            using var result = await request;
            Assert.Equal(ScrapedTokenTwo, await service.GetCatalogTokenAsync(CancellationToken.None));
            Assert.Equal(0, handler.ScrapePageRequests);
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task GenuineDeveloperTokenExpiryInvalidatesAndRecoversThroughScrape()
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("apple-token-expiry-").FullName;
        try
        {
            var (service, handler) = CreateService(null, directory);
            handler.EnqueueCatalogResponse(() => new HttpResponseMessage(HttpStatusCode.Unauthorized));
            handler.EnqueueCatalogResponse(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[]}") });
            using var result = await service.GetSongAsync("1234", "us", "en-US", CancellationToken.None);
            Assert.NotNull(result);
            // One scrape for the initial token, one re-scrape after the genuine rejection.
            Assert.Equal(2, handler.ScrapePageRequests);
            Assert.Equal(2, handler.CatalogRequests);
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ExplicitInvalidBearerTokenRecoversEvenWithMediaUserAuthentication()
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("apple-bearer-expiry-").FullName;
        try
        {
            var (service, handler) = CreateService(null, directory);
            handler.EnqueueCatalogResponse(() => {
                var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                response.Headers.WwwAuthenticate.Add(new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "error=\"invalid_token\""));
                return response;
            });
            handler.EnqueueCatalogResponse(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[]}") });
            using var result = await service.GetSongAsync("1234", "us", "en-US", CancellationToken.None, MediaUserToken);
            Assert.Equal(2, handler.ScrapePageRequests);
            Assert.Equal(2, handler.CatalogRequests);
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task NonJsonPayloadRetriesWithoutInvalidatingTheToken()
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("apple-token-nonjson-").FullName;
        try
        {
            var (service, handler) = CreateService(null, directory);
            handler.EnqueueCatalogResponse(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html>temporary interstitial</html>", Encoding.UTF8, "text/html")
            });
            handler.EnqueueCatalogResponse(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[]}") });
            using var result = await service.GetSongAsync("1234", "us", "en-US", CancellationToken.None);
            Assert.NotNull(result);
            // An HTML payload is not proof the token expired: no re-scrape.
            Assert.Equal(1, handler.ScrapePageRequests);
            Assert.Equal(2, handler.CatalogRequests);
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task TokenAcquisitionBudgetBoundsLockWaitAndScraping()
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("apple-token-budget-").FullName;
        try
        {
            var (service, handler) = CreateService(null, directory);
            handler.DefaultScrapeDelay = TimeSpan.FromSeconds(30);
            var method = typeof(AppleMusicCatalogService).GetMethod("GetTokenAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var started = DateTimeOffset.UtcNow;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                (Task<string>)method.Invoke(service, [CancellationToken.None, TimeSpan.FromMilliseconds(500)])!);
            Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(10),
                "Token acquisition must respect its budget instead of hanging on scraping.");
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task TokenBudgetIncludesWaitingBehindAnotherRefresh()
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("apple-token-lock-").FullName;
        try
        {
            var (service, _) = CreateService(null, directory);
            var gate = (SemaphoreSlim)typeof(AppleMusicCatalogService).GetField("_tokenRefreshLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
            await gate.WaitAsync();
            try
            {
                var method = typeof(AppleMusicCatalogService).GetMethod("GetTokenAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    (Task<string>)method.Invoke(service, [CancellationToken.None, TimeSpan.FromMilliseconds(100)])!);
                Assert.Equal(0, gate.CurrentCount);
            }
            finally { gate.Release(); }
            Assert.False(string.IsNullOrWhiteSpace(await service.GetCatalogTokenAsync(CancellationToken.None)));
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task StaleTokenFallbackAfterFailedRefreshIsPreserved()
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("apple-token-stale-").FullName;
        try
        {
            var (service, handler) = CreateService(null, directory);
            CacheToken(service, ScrapedTokenOne);
            typeof(AppleMusicCatalogService)
                .GetField("_cachedTokenExpiresAt", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(service, DateTimeOffset.UtcNow.AddMinutes(-5));
            handler.ScrapeFails = true;
            Assert.Equal(ScrapedTokenOne, await service.GetCatalogTokenAsync(CancellationToken.None));
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }
}
