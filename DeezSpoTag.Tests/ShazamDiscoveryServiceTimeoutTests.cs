using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ShazamDiscoveryServiceTimeoutTests
{
    [Fact]
    public async Task GetRelatedTracksAsync_WhenHttpClientTimesOut_ReturnsEmptyList()
    {
        using var httpClient = new HttpClient(new DelayedHandler(TimeSpan.FromMilliseconds(200)))
        {
            Timeout = TimeSpan.FromMilliseconds(25)
        };
        var service = new ShazamDiscoveryService(httpClient, NullLogger<ShazamDiscoveryService>.Instance);

        var tracks = await service.GetRelatedTracksAsync("123456", cancellationToken: CancellationToken.None);

        Assert.Empty(tracks);
    }

    [Fact]
    public async Task GetRelatedTracksAsync_WhenCallerCancels_ThrowsCancellation()
    {
        using var httpClient = new HttpClient(new DelayedHandler(TimeSpan.FromMilliseconds(200)))
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var service = new ShazamDiscoveryService(httpClient, NullLogger<ShazamDiscoveryService>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.GetRelatedTracksAsync("123456", cancellationToken: cts.Token));
    }

    private sealed class DelayedHandler : HttpMessageHandler
    {
        private readonly TimeSpan _delay;

        public DelayedHandler(TimeSpan delay)
        {
            _delay = delay;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(_delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            };
        }
    }
}
