using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class MediaServerImageCacheServiceTests : IDisposable
{
    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0x01, 0x02, 0x03];
    private readonly string _root = Path.Join(Path.GetTempPath(), $"deezspotag-media-image-{Guid.NewGuid():N}");

    [Fact]
    public async Task GetAsync_PersistsSuccessfulImageAndAvoidsSecondUpstreamRequest()
    {
        var handler = new SequenceHandler(CreateImageResponse());
        var service = CreateService(handler);

        var first = await service.GetAsync("plex", "/library/metadata/1/thumb/2", "http://plex/image", CancellationToken.None);
        var second = await service.GetAsync("plex", "/library/metadata/1/thumb/2", "http://plex/image", CancellationToken.None);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal("image/jpeg", first.ContentType);
        Assert.Equal(JpegBytes, second.Bytes);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_RetriesRateLimitedImageAndCachesSuccess()
    {
        var rateLimited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        rateLimited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
        var handler = new SequenceHandler(rateLimited, CreateImageResponse());
        var service = CreateService(handler);

        var result = await service.GetAsync("plex", "/library/metadata/3/thumb/4", "http://plex/image", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(JpegBytes, result.Bytes);
        Assert.Equal(2, handler.RequestCount);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private MediaServerImageCacheService CreateService(SequenceHandler handler)
    {
        Directory.CreateDirectory(_root);
        return new MediaServerImageCacheService(
            new StubHttpClientFactory(handler),
            new StubWebHostEnvironment(_root),
            NullLogger<MediaServerImageCacheService>.Instance);
    }

    private static HttpResponseMessage CreateImageResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(JpegBytes)
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        return response;
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class SequenceHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class StubWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "DeezSpoTag.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = contentRootPath;
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
