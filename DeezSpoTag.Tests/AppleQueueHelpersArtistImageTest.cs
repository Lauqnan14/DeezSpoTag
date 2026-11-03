using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download.Apple;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AppleQueueHelpersArtistImageTest
{
    [Theory]
    [InlineData("The Notorious B.I.G.", "5499810")]
    [InlineData("2 Chainz", "294142934")]
    public async Task ArtistPageUsesTheRequestedArtistsPortraitInsteadOfRelatedArtists(string artist, string artistId)
    {
        var factory = new StubHttpClientFactory(new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "itunes.apple.com")
                return Json(System.Text.Json.JsonSerializer.Serialize(new { results = new[] { new { artistName = artist, artistLinkUrl = "https://music.apple.com/us/artist/example/" + artistId } } }));
            return Html("""
                <html><body><img src="https://is1-ssl.mzstatic.com/image/thumb/AMCArtistImages/other/related.png/500x500bb.jpg">
                <script id="serialized-server-data" type="application/json">
                {"data":[{"data":{"sections":[{"items":[{"id":"ARTIST_ID","contentDescriptor":{"kind":"artist","identifiers":{"storeAdamID":"ARTIST_ID"}},"circleArtwork":{"dictionary":{"width":1200,"height":1200,"url":"https://is1-ssl.mzstatic.com/image/thumb/Features/correct/portrait.png/{w}x{h}bb.jpg"}}}]}]}}]}
                </script></body></html>
                """.Replace("ARTIST_ID", artistId, StringComparison.Ordinal));
        }));

        var image = await AppleQueueHelpers.ResolveItunesArtistImageAsync(factory, artist, 1198, NullLogger.Instance, CancellationToken.None);
        Assert.NotNull(image);
        Assert.Contains("/correct/portrait.png/", image, StringComparison.Ordinal);
        Assert.DoesNotContain("related.png", image, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArtistPageWithOnlyRelatedArtistPicturesDoesNotReturnTheirPortrait()
    {
        var factory = new StubHttpClientFactory(new StubHttpMessageHandler(request => request.RequestUri!.Host == "itunes.apple.com"
            ? Json("""{"results":[{"artistName":"Missing Primary Portrait","artistLinkUrl":"https://music.apple.com/us/artist/example/123456"}]}""")
            : Html("""<html><img src="https://is1-ssl.mzstatic.com/image/thumb/AMCArtistImages/other/related.png/500x500bb.jpg"></html>""")));
        Assert.Null(await AppleQueueHelpers.ResolveItunesArtistImageAsync(factory, "Missing Primary Portrait", 1198, NullLogger.Instance, CancellationToken.None));
    }

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

    [Fact]
    public async Task ResolveItunesArtistImageAsync_UsesEmbeddedSquarePortraitInsteadOfWideSocialCard()
    {
        var factory = new StubHttpClientFactory(new StubHttpMessageHandler(request =>
        {
            var uri = request.RequestUri?.ToString() ?? string.Empty;
            if (uri.Contains("itunes.apple.com/search", StringComparison.OrdinalIgnoreCase))
            {
                return Json(
                    """
                    {"resultCount":1,"results":[{"artistName":"The Ben","artistLinkUrl":"https://music.apple.com/us/artist/the-ben/343631056"}]}
                    """);
            }

            if (uri.Contains("music.apple.com/us/artist/the-ben/343631056", StringComparison.OrdinalIgnoreCase))
            {
                return Html("""
                    <html><head><meta property="og:image" content="https://is1-ssl.mzstatic.com/image/thumb/AMCArtistImages221/v4/a/b/c/file.png/1200x630cw.png"></head>
                    <body><script type="application/ld+json">{"image":"https://is1-ssl.mzstatic.com/image/thumb/AMCArtistImages221/v4/a/b/c/file.png/486x486bb.png"}</script></body></html>
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var result = await AppleQueueHelpers.ResolveItunesArtistImageAsync(
            factory,
            "The Ben",
            1200,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains("/1200x1200", result!, StringComparison.Ordinal);
        Assert.DoesNotContain("1200x630", result, StringComparison.Ordinal);
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
