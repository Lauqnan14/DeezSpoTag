using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Web.Controllers.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Guards the allow-listed cover-art proxy used by the Tracklist page. The proxy must never
/// become an open proxy, and it must pass the image through when the host is allowed.
/// </summary>
public sealed class ExternalImagesApiControllerTest
{
    private const string SpotifyCover = "https://i.scdn.co/image/ab67616d00001e02d80c3d4b23528b53c4b04d49";

    private static ExternalImagesApiController CreateController(
        StubHandler handler,
        out List<string> requestedUris)
    {
        var controller = new ExternalImagesApiController(new StubClientFactory(handler))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        requestedUris = handler.RequestedUris;
        return controller;
    }

    private static StubHandler ImageHandler()
        => new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("fake-image-bytes"))
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg") }
            }
        });

    [Fact]
    public async Task AllowedSpotifyHostIsProxied()
    {
        var handler = ImageHandler();
        var controller = CreateController(handler, out var requestedUris);

        var result = await controller.Get(SpotifyCover, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("image/jpeg", file.ContentType);
        Assert.Equal("fake-image-bytes", Encoding.UTF8.GetString(file.FileContents));
        Assert.Equal(new[] { SpotifyCover }, requestedUris);
    }

    [Fact]
    public async Task DisallowedHostIsRejectedBeforeAnyRequest()
    {
        var handler = ImageHandler();
        var controller = CreateController(handler, out var requestedUris);

        var result = await controller.Get("https://evil.example.com/tracker.gif", CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(requestedUris);
    }

    [Fact]
    public async Task InternalAddressesAreRejectedBeforeAnyRequest()
    {
        var handler = ImageHandler();
        var controller = CreateController(handler, out var requestedUris);

        var result = await controller.Get("https://127.0.0.1/admin", CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(requestedUris);
    }

    [Fact]
    public async Task FileSchemeIsRejected()
    {
        var handler = ImageHandler();
        var controller = CreateController(handler, out _);

        var result = await controller.Get("file:///etc/passwd", CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task PlainHttpIsRejected()
    {
        var handler = ImageHandler();
        var controller = CreateController(handler, out var requestedUris);

        var result = await controller.Get("http://i.scdn.co/image/abc", CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(requestedUris);
    }

    [Fact]
    public async Task MissingUrlIsRejected()
    {
        var handler = ImageHandler();
        var controller = CreateController(handler, out _);

        var result = await controller.Get(null, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task UpstreamFailureStatusIsNotMasked()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var controller = CreateController(handler, out _);

        var result = await controller.Get(SpotifyCover, CancellationToken.None);

        var status = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(404, status.StatusCode);
    }

    [Fact]
    public async Task NonImageContentTypeIsRejected()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>not an image</html>", Encoding.UTF8, "text/html")
        });
        var controller = CreateController(handler, out _);

        var result = await controller.Get(SpotifyCover, CancellationToken.None);

        var status = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, status.StatusCode);
    }

    private sealed class StubClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StubClientFactory(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public List<string> RequestedUris { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestedUris.Add(request.RequestUri?.ToString() ?? string.Empty);
            return Task.FromResult(_responder(request));
        }
    }
}
