using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download.Tidal;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class TidalApiProviderSourceTests
{
    [Fact]
    public async Task GetRotatedProvidersAsync_UsesSeedProviders_WhenGistFetchFails()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "deezspotag-tests", Guid.NewGuid().ToString("N"));
        using var scope = new TestConfigRootScope(rootPath);
        var service = new TidalApiProviderSource(
            new StubHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)),
            NullLogger<TidalApiProviderSource>.Instance);

        var providers = await service.GetRotatedProvidersAsync(CancellationToken.None);

        Assert.Equal(8, providers.Count);
        Assert.Equal("https://vogel.qqdl.site", providers[0]);
        Assert.Contains("https://triton.squid.wtf", providers);
    }

    [Fact]
    public async Task RememberSuccessAsync_RotatesLastSuccessfulProviderToTheEnd()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "deezspotag-tests", Guid.NewGuid().ToString("N"));
        using var scope = new TestConfigRootScope(rootPath);
        var service = new TidalApiProviderSource(
            new StubHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)),
            NullLogger<TidalApiProviderSource>.Instance);

        await service.RememberSuccessAsync("https://vogel.qqdl.site", CancellationToken.None);
        var providers = await service.GetRotatedProvidersAsync(CancellationToken.None);

        Assert.Equal("https://maus.qqdl.site", providers[0]);
        Assert.Equal("https://vogel.qqdl.site", providers[^1]);
    }

    [Fact]
    public async Task GetRotatedProvidersAsync_UsesFetchedGistProviders_WhenAvailable()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "deezspotag-tests", Guid.NewGuid().ToString("N"));
        using var scope = new TestConfigRootScope(rootPath);
        var service = new TidalApiProviderSource(
            new StubHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[\"https://one.example\",\"https://two.example\"]")
            }),
            NullLogger<TidalApiProviderSource>.Instance);

        var providers = await service.GetRotatedProvidersAsync(CancellationToken.None);

        Assert.Equal(["https://one.example", "https://two.example"], providers);
    }

    private sealed class StubHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> responder) : IHttpClientFactory
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder = responder;

        public HttpClient CreateClient(string name) => new(new StubHttpMessageHandler(_responder));
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }
}
