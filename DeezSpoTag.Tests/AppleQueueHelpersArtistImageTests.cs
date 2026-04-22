using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download.Apple;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AppleQueueHelpersArtistImageTests
{
    [Fact]
    public async Task ResolveItunesArtistImageAsync_ReturnsNull_ForNonSquareCardImage()
    {
        var factory = new StubHttpClientFactory(new StubHttpMessageHandler(request =>
        {
            var uri = request.RequestUri?.ToString() ?? string.Empty;
            if (uri.Contains("itunes.apple.com/search", StringComparison.OrdinalIgnoreCase))
            {
                return Json(
                    """
                    {"resultCount":1,"results":[{"artistName":"Boondocks Gang","artistLinkUrl":"https://music.apple.com/us/artist/boondocks-gang/1471213104?uo=4"}]}
                    """);
            }

            if (uri.Contains("music.apple.com/us/artist/boondocks-gang/1471213104", StringComparison.OrdinalIgnoreCase))
            {
                return Html("""
                    <html><head><meta property="og:image" content="https://is1-ssl.mzstatic.com/image/thumb/Music122/v4/d6/70/43/d67043d4-3b12-4cdf-45e0-70a5792b87ed/196922264023_Cover.jpg/1200x630cw.png"></head><body></body></html>
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var result = await AppleQueueHelpers.ResolveItunesArtistImageAsync(
            factory,
            "Boondocks Gang",
            size: 1200,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveItunesArtistImageAsync_ReturnsSquareArtistArtwork()
    {
        var factory = new StubHttpClientFactory(new StubHttpMessageHandler(request =>
        {
            var uri = request.RequestUri?.ToString() ?? string.Empty;
            if (uri.Contains("itunes.apple.com/search", StringComparison.OrdinalIgnoreCase))
            {
                return Json(
                    """
                    {"resultCount":1,"results":[{"artistName":"Boondocks Gang","artistLinkUrl":"https://music.apple.com/us/artist/boondocks-gang/1471213104?uo=4"}]}
                    """);
            }

            if (uri.Contains("music.apple.com/us/artist/boondocks-gang/1471213104", StringComparison.OrdinalIgnoreCase))
            {
                return Html("""
                    <html><head><meta property="og:image" content="https://is1-ssl.mzstatic.com/image/thumb/Music122/v4/d6/70/43/d67043d4-3b12-4cdf-45e0-70a5792b87ed/196922264023_Cover.jpg/300x300bb.jpg"></head><body></body></html>
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var result = await AppleQueueHelpers.ResolveItunesArtistImageAsync(
            factory,
            "Boondocks Gang",
            size: 1200,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains("/1200x1200", result!, StringComparison.Ordinal);
    }

    private static HttpResponseMessage Json(string content)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(content)
        };

    private static HttpResponseMessage Html(string content)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(content)
        };

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
