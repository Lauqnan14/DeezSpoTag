using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Apple;
using DeezSpoTag.Services.Download.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AppleQueueHelpersArtworkDownloadTests
{
    [Fact]
    public async Task DownloadAppleArtworkAsync_RawAcArtwork_ClampsToSourceDimensions()
    {
        var handler = new CapturingHttpMessageHandler();
        var downloader = new ImageDownloader(
            NullLogger<ImageDownloader>.Instance,
            new StubHttpClientFactory(handler));
        var settings = BuildSettings();
        var outputPath = BuildTempOutputPath();

        var downloaded = await AppleQueueHelpers.DownloadAppleArtworkAsync(
            downloader,
            new AppleQueueHelpers.AppleArtworkDownloadRequest
            {
                RawUrl = "https://is1-ssl.mzstatic.com/image/thumb/Music211/v4/7b/d6/6d/7bd66d99-3c31-8c1d-e81d-3353e86ae938/artwork.jpg/1200x1200ac.jpg",
                OutputPath = outputPath,
                Settings = settings,
                Size = 5000,
                Overwrite = "y",
                PreferMaxQuality = true,
                Logger = NullLogger.Instance
            },
            CancellationToken.None);

        Assert.NotNull(downloaded);
        Assert.Single(handler.RequestedUrls);
        Assert.Contains("/1200x1200ac.jpg", handler.RequestedUrls[0], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/5000x5000ac.jpg", handler.RequestedUrls[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DownloadAppleArtworkAsync_RawBbArtwork_UsesConfiguredMaxSize()
    {
        var handler = new CapturingHttpMessageHandler();
        var downloader = new ImageDownloader(
            NullLogger<ImageDownloader>.Instance,
            new StubHttpClientFactory(handler));
        var settings = BuildSettings();
        var outputPath = BuildTempOutputPath();

        var downloaded = await AppleQueueHelpers.DownloadAppleArtworkAsync(
            downloader,
            new AppleQueueHelpers.AppleArtworkDownloadRequest
            {
                RawUrl = "https://is1-ssl.mzstatic.com/image/thumb/Music115/v4/6b/ca/47/6bca47fd-8a58-0652-8de8-475394e8159d/pr_source.png/1200x1200bb.jpg",
                OutputPath = outputPath,
                Settings = settings,
                Size = 1200,
                Overwrite = "y",
                PreferMaxQuality = true,
                Logger = NullLogger.Instance
            },
            CancellationToken.None);

        Assert.NotNull(downloaded);
        Assert.Single(handler.RequestedUrls);
        Assert.Contains("/5000x5000bb.jpg", handler.RequestedUrls[0], StringComparison.OrdinalIgnoreCase);
    }

    private static DeezSpoTagSettings BuildSettings()
        => new()
        {
            AppleArtworkSize = 1200,
            AppleArtworkSizeText = "5000x5000",
            OverwriteFile = "y"
        };

    private static string BuildTempOutputPath()
    {
        var directory = Path.Join(Path.GetTempPath(), "deezspotag-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Join(directory, "artist.jpg");
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CapturingHttpMessageHandler : HttpMessageHandler
    {
        public List<string> RequestedUrls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUrls.Add(request.RequestUri?.ToString() ?? string.Empty);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("ok"u8.ToArray())
            });
        }
    }
}
