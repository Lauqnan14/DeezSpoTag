using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations.Plex;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class PlexApiClientTimeoutHandlingTests
{
    [Fact]
    public async Task SearchTracksAsync_ReturnsEmpty_WhenRequestTimesOut()
    {
        using var httpClient = new HttpClient(new TimeoutHandler());
        var client = new PlexApiClient(NullLogger<PlexApiClient>.Instance, httpClient);

        var result = await client.SearchTracksAsync(
            "http://plex.local:32400",
            "token",
            "kendrick lamar",
            CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task SearchTracksAsync_Throws_WhenCallerCancellationIsRequested()
    {
        using var httpClient = new HttpClient(new CancellationAwareHandler());
        var client = new PlexApiClient(NullLogger<PlexApiClient>.Instance, httpClient);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await client.SearchTracksAsync(
                "http://plex.local:32400",
                "token",
                "kendrick lamar",
                cts.Token));
    }

    [Fact]
    public async Task LockArtistArtworkAsync_SetsThumbAndArtLockedFlags()
    {
        using var handler = new CaptureRequestHandler();
        using var httpClient = new HttpClient(handler);
        var client = new PlexApiClient(NullLogger<PlexApiClient>.Instance, httpClient);

        var locked = await client.LockArtistArtworkAsync(
            "http://plex.local:32400",
            "token",
            "7",
            "1234",
            lockPoster: true,
            lockBackground: true,
            CancellationToken.None);

        Assert.True(locked);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Put, handler.LastRequest!.Method);
        var uri = handler.LastRequest.RequestUri!.ToString();
        Assert.Contains("/library/sections/7/all", uri, StringComparison.Ordinal);
        Assert.Contains("type=8", uri, StringComparison.Ordinal);
        Assert.Contains("id=1234", uri, StringComparison.Ordinal);
        Assert.Contains("thumb.locked=1", uri, StringComparison.Ordinal);
        Assert.Contains("art.locked=1", uri, StringComparison.Ordinal);
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new TaskCanceledException("The request timed out.");
        }
    }

    private sealed class CancellationAwareHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class CaptureRequestHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
