using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ImageDownloaderCancellationTests
{
    [Fact]
    public async Task DownloadImageAsync_ReturnsNull_WhenHttpRequestTimesOut()
    {
        var downloader = new ImageDownloader(
            NullLogger<ImageDownloader>.Instance,
            new StubHttpClientFactory(new TimeoutHandler()));
        var targetPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.jpg");

        var result = await downloader.DownloadImageAsync(
            "https://example.com/cover.jpg",
            targetPath,
            cancellationToken: CancellationToken.None);

        Assert.Null(result);
        Assert.False(File.Exists(targetPath));
    }

    [Fact]
    public async Task DownloadImageAsync_Throws_WhenCallerTokenIsCanceled()
    {
        var downloader = new ImageDownloader(
            NullLogger<ImageDownloader>.Instance,
            new StubHttpClientFactory(new CancellationAwareHandler()));
        var targetPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.jpg");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await downloader.DownloadImageAsync(
                "https://example.com/cover.jpg",
                targetPath,
                cancellationToken: cts.Token));
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new TaskCanceledException("A task was canceled.");
    }

    private sealed class CancellationAwareHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
