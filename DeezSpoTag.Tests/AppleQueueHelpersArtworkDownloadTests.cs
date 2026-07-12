using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
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

    [Theory]
    [InlineData("mp4,webp,gif", new[] { "mp4", "webp", "gif" })]
    [InlineData("WEBP, mp4, invalid, gif, mp4", new[] { "webp", "mp4", "gif" })]
    [InlineData("", new[] { "mp4" })]
    [InlineData("invalid", new[] { "mp4" })]
    public void ResolveAnimatedArtworkFormats_NormalizesConfiguredFormats(
        string configuredFormats,
        string[] expectedFormats)
    {
        var settings = BuildSettings();
        settings.AnimatedArtworkFormats = configuredFormats;

        var formats = AppleQueueHelpers.ResolveAnimatedArtworkFormats(settings);

        Assert.Equal(expectedFormats, formats);
    }

    [Fact]
    public void DownloadArtwork_SourceCoverPrecedesProviderFallbacks()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Services/Download/Shared/DownloadEngineArtworkHelper.cs"));
        var payloadAdd = source.IndexOf("AddCoverUrl(coverUrls, payloadCandidate.Url);", StringComparison.Ordinal);
        var fallbackLoop = source.IndexOf("foreach (var fallback in fallbackOrder)", StringComparison.Ordinal);

        Assert.True(payloadAdd >= 0 && payloadAdd < fallbackLoop);
    }

    [Fact]
    public void AnimatedArtwork_RequiresExactSourceAlbumEdition()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Services/Download/Shared/EngineAudioPostDownloadHelper.cs"));

        Assert.Contains("AreExactArtworkAlbumsCompatible(payload.Album, payload.AppleAlbumName)", source, StringComparison.Ordinal);
        Assert.Contains("does not match source release", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnimatedArtworkConversion_ExistingMp4StillCreatesWebpAndGif()
    {
        var root = Path.Join(Path.GetTempPath(), "deezspotag-animated-artwork", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Join(root, "source.mp4");
        var outputBase = Path.Join(root, "cover - square_animated_artwork");
        var existingMp4 = $"{outputBase}.mp4";
        var liveSource = Environment.GetEnvironmentVariable("DEEZSPOTAG_ANIMATED_ARTWORK_SOURCE");
        if (!string.IsNullOrWhiteSpace(liveSource) && File.Exists(liveSource))
        {
            File.Copy(liveSource, source);
        }
        else
        {
            await RunProcessAsync("ffmpeg", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc=size=64x64:rate=12", "-t", "1", "-pix_fmt", "yuv420p", source);
        }
        File.Copy(source, existingMp4);
        var originalHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(existingMp4)));

        var method = typeof(AppleQueueHelpers).GetMethod(
            "SaveAnimatedArtworkVariantAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task<IReadOnlyList<string>>>(method!.Invoke(null, new object[]
        {
            source,
            outputBase,
            new[] { "mp4", "webp", "gif" },
            NullLogger.Instance,
            CancellationToken.None
        }));

        var savedPaths = await task;
        Assert.Contains(existingMp4, savedPaths);
        Assert.Contains($"{outputBase}.webp", savedPaths);
        Assert.Contains($"{outputBase}.gif", savedPaths);
        Assert.Equal(originalHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(existingMp4))));
        await AssertValidVideoAsync($"{outputBase}.webp");
        await AssertValidVideoAsync($"{outputBase}.gif");
    }

    [Fact]
    public async Task SaveExistingAnimatedArtworkVariantsAsync_ReusesExistingMp4AndCreatesRequestedFormats()
    {
        var root = Path.Join(Path.GetTempPath(), "deezspotag-animated-artwork", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var existingMp4 = Path.Join(root, "cover - square_animated_artwork.mp4");
        await RunProcessAsync("ffmpeg", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc=size=64x64:rate=12", "-t", "1", "-pix_fmt", "yuv420p", existingMp4);
        var originalHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(existingMp4)));

        var savedPaths = await AppleQueueHelpers.SaveExistingAnimatedArtworkVariantsAsync(
            new AppleQueueHelpers.AnimatedArtworkSaveRequest
            {
                OutputDir = root,
                BaseFileName = "cover",
                OutputFormats = new[] { "mp4", "webp", "gif" },
                Logger = NullLogger.Instance
            },
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Contains(existingMp4, savedPaths);
        Assert.Contains(Path.Join(root, "cover - square_animated_artwork.webp"), savedPaths);
        Assert.Contains(Path.Join(root, "cover - square_animated_artwork.gif"), savedPaths);
        Assert.Equal(originalHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(existingMp4))));
        await AssertValidVideoAsync(Path.Join(root, "cover - square_animated_artwork.webp"));
        await AssertValidVideoAsync(Path.Join(root, "cover - square_animated_artwork.gif"));
    }

    private static async Task AssertValidVideoAsync(string path)
    {
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 0);
        await RunProcessAsync("ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_name,width,height", "-of", "default=noprint_wrappers=1", path);
    }

    private static async Task RunProcessAsync(string executable, params string[] arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo(executable)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(startInfo);
        Assert.NotNull(process);
        await process!.WaitForExitAsync();
        var error = await process.StandardError.ReadToEndAsync();
        Assert.True(process.ExitCode == 0, error);
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
