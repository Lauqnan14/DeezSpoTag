using DeezSpoTag.Services.Download.Utils;
using Xunit;
using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Net;
using System.Net.Http;
using DeezSpoTag.Web.Services;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace DeezSpoTag.Tests;

public sealed class DeezerPlaceholderArtworkTest
{
    [Theory]
    [InlineData("https://e-cdns-images.dzcdn.net/images/artist//500x500-000000-80-0-0.jpg")]
    [InlineData("https://e-cdns-images.dzcdn.net/images/artist//1000x1000-000000-80-0-0.jpg")]
    [InlineData("https://cdn-images.dzcdn.net/images/cover//500x500-000000-80-0-0.jpg")]
    public void EmptyHashPlaceholdersAreBlocked(string url)
    {
        Assert.False(DeezerImageUrlValidator.IsAllowedDeezerImageUrl(url));
    }

    [Theory]
    [InlineData("d41d8cd98f00b204e9800998ecf8427e")]
    [InlineData("522c7b1de6d02790c348da447d3fd2b7")]
    [InlineData("c34f636093a87af8fd7dda0a10184280")]
    [InlineData("df237cf6f464c6fed6a4cc208db28aeb")]
    [InlineData("8ad2e6f95167c3fc4113de04cccda30f")]
    public void KnownPlaceholderHashesAreBlocked(string hash)
    {
        Assert.False(DeezerImageUrlValidator.HasUsableDeezerMd5(hash));
        Assert.False(DeezerImageUrlValidator.IsAllowedDeezerImageUrl(
            $"https://e-cdns-images.dzcdn.net/images/artist/{hash}/1000x1000-000000-80-0-0.jpg"));
    }

    // Actual 120px Deezer placeholder observed on the local Antslive artist folder.
    internal static byte[] PlaceholderJpeg() => Convert.FromBase64String("/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAAYEBQYFBAYGBQYHBwYIChAKCgkJChQODwwQFxQYGBcUFhYaHSUfGhsjHBYWICwgIyYnKSopGR8tMC0oMCUoKSj/2wBDAQcHBwoIChMKChMoGhYaKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCj/wgARCAB4AHgDAREAAhEBAxEB/8QAGgABAAMBAQEAAAAAAAAAAAAAAAIEBQEDB//EABUBAQEAAAAAAAAAAAAAAAAAAAAB/9oADAMBAAIQAxAAAAH6NQAAAAAAAAAAAAAAAAAAAAAAAAETyPUkAAAAADMLBXNMAAAAA4Z5omcaB0AAAAAokCZeAAAAAPM8CBM9j1AAAAKB0sEiJXOF8AAAGcaIABnGiAAADNOgAHDSAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB/8QAIxAAAgICAQQCAwAAAAAAAAAAAQIAAxMwEQQhMjMSIDFQYP/aAAgBAQABBQL9mTxMqQHnb3tfCJ3qfWfx0/lOo8hssrPPNsrrPOtnVYbxM8F4iurarXJZaYK1E+Kw1rGolTkNoq931t92hwUsyvMrzK8yvMrxOXs/gP/EABQRAQAAAAAAAAAAAAAAAAAAAHD/2gAIAQMBAT8BYf/EABQRAQAAAAAAAAAAAAAAAAAAAHD/2gAIAQIBAT8BYf/EACMQAAECBQMFAAAAAAAAAAAAAAABIQIRMDHhEiIyEEBBUGD/2gAIAQEABj8C9m5catday9EqzgPJOOo6jIpxHQZaWmE3KWOKFjappi7iaGDBgwYJr8D/AP/EACYQAAECBQMDBQAAAAAAAAAAAAEAESEwQVFhMYGxcdHwECBQYPH/2gAIAQEAAT8h+TADkAF4QgA5AibcsOAodRXbjkTNRGwqt6G4rBaBMC3zKlxRW+a8zQB7KpC8HVSBaAPaVsWhVdkCu3qvyEXb0XYConrRpJiJ8+6AGxJIQ5EFhTCmFMKYUAUOTD6D/9oADAMBAAIAAwAAABAAAAAAAAAAAAAAAAAAAAAAAAAASQAAAAAAQAAAAAAQQAAAAACCAAAAAACCAAAACAQSAAAAAAAAAAAACSQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAf/8QAFBEBAAAAAAAAAAAAAAAAAAAAcP/aAAgBAwEBPxBh/8QAGREAAgMBAAAAAAAAAAAAAAAAAREAMGBA/9oACAECAQE/EMiO9x2K4Zb/xAAnEAEAAgEEAQMDBQAAAAAAAAABABEhMDFRYZFBgfAQUKEgcbHB8f/aAAgBAQABPxD7n39pamSqeU7+0N6oPMG0vaH87YlBMilrBAztpiwN0SAhhY+z9AAy0vuwUTuAai25twNI9TFjw5+ag27DkbV3qWgbgywH5WifL/MR+GRmEdyYdJgqA29SjpdXx3mzBcpZj/py3sLlJMS3fHeFVQuGSv30DeG9vF+/6hc3q/eeroJY3uLGfjOn5zp+c6fnOn5zp+cUxvZgUaVvLLeWW8st5Zby/c//2Q==");

    [Theory]
    [InlineData(64)]
    [InlineData(120)]
    [InlineData(1000)]
    public async Task PlaceholderContentIsNotValidArtistArtworkRegardlessOfSize(int size)
    {
        var path = Path.Join(Path.GetTempPath(), "deezer-placeholder-" + Guid.NewGuid().ToString("N") + ".jpg");
        try
        {
            using var image = Image.Load(PlaceholderJpeg());
            image.Mutate(x => x.Resize(size, size));
            Assert.True(DeezerImageUrlValidator.IsArtistPlaceholder(image));
            await image.SaveAsJpegAsync(path);
            Assert.Null(await DownloadEngineArtworkHelper.ReadSquareArtistArtworkDimensionsAsync(path, CancellationToken.None));
            var protectedExisting = typeof(DownloadEngineArtworkHelper).GetMethod("IsProtectedExistingArtwork", BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.False((bool)protectedExisting.Invoke(null, new object[] { path, "n" })!);
            var validate = typeof(DownloadEngineArtworkHelper).GetMethod("ValidateArtistArtworkAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
            var task = (Task)validate.Invoke(null, new object[] { path, false, NullLogger.Instance, CancellationToken.None })!;
            await task;
            Assert.Null(task.GetType().GetProperty("Result")!.GetValue(task));
            Assert.False(File.Exists(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task PlainLightArtworkIsNotMistakenForThePersonOutline()
    {
        var path = Path.Join(Path.GetTempPath(), "real-plain-art-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            using var image = new Image<Rgba32>(200, 200, new Rgba32(233, 233, 233));
            await image.SaveAsPngAsync(path);
            Assert.NotNull(await DownloadEngineArtworkHelper.ReadSquareArtistArtworkDimensionsAsync(path, CancellationToken.None));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task KnownPlaceholderUrlNeverReachesTheImageDownloaderHttpClient()
    {
        var factory = new PlaceholderFactory();
        var downloader = new ImageDownloader(NullLogger<ImageDownloader>.Instance, factory);
        var path = Path.Join(Path.GetTempPath(), "blocked-art-" + Guid.NewGuid().ToString("N") + ".jpg");
        var result = await downloader.DownloadImageAsync(
            "https://cdn-images.dzcdn.net/images/artist/8ad2e6f95167c3fc4113de04cccda30f/1000x1000.jpg", path);
        Assert.Null(result);
        Assert.Equal(0, factory.Requests);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ArtistCatalogNeverCachesPlaceholderUrlsOrImageContent(bool knownUrl)
    {
        var root = Path.Join(Path.GetTempPath(), "placeholder-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var factory = new PlaceholderFactory();
            var catalog = (ArtistArtworkCatalogService)RuntimeHelpers.GetUninitializedObject(typeof(ArtistArtworkCatalogService));
            void Set(string name, object value) => typeof(ArtistArtworkCatalogService)
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(catalog, value);
            Set("_cacheRoot", root);
            Set("_httpClients", factory);
            Set("_logger", NullLogger<ArtistArtworkCatalogService>.Instance);
            var url = knownUrl
                ? "https://cdn-images.dzcdn.net/images/artist/df237cf6f464c6fed6a4cc208db28aeb/1000x1000.jpg"
                : "https://images.test/new-unrecognised-url.jpg";
            var candidateType = typeof(ArtistArtworkCatalogService).GetNestedType("RemoteCandidate", BindingFlags.NonPublic)!;
            var candidate = Activator.CreateInstance(candidateType, new object?[] { "deezer", "deezer:42", url, null, null })!;
            var method = typeof(ArtistArtworkCatalogService).GetMethod("CacheCandidateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var result = await (Task<string?>)method.Invoke(catalog, new object[] { 42L, candidate, CancellationToken.None })!;
            Assert.Null(result);
            Assert.Equal(knownUrl ? 0 : 1, factory.Requests);
            Assert.Empty(Directory.Exists(root) ? Directory.GetFiles(root, "*", SearchOption.AllDirectories) : Array.Empty<string>());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExistingPlaceholderDoesNotPreventDownloadingARealArtistImage()
    {
        var path = Path.Join(Path.GetTempPath(), "replace-placeholder-" + Guid.NewGuid().ToString("N") + ".jpg");
        try
        {
            await File.WriteAllBytesAsync(path, PlaceholderJpeg());
            var factory = new PlaceholderFactory { ServeRealImage = true };
            var downloader = new ImageDownloader(NullLogger<ImageDownloader>.Instance, factory);
            Assert.Equal(path, await downloader.DownloadImageAsync("https://images.test/real.jpg", path, overwrite: "n"));
            Assert.Equal(1, factory.Requests);
            using var image = Image.Load(path);
            Assert.False(DeezerImageUrlValidator.IsArtistPlaceholder(image));
        }
        finally { File.Delete(path); }
    }

    private sealed class PlaceholderFactory : IHttpClientFactory
    {
        public int Requests;
        public bool ServeRealImage;
        public HttpClient CreateClient(string name) => new(new PlaceholderHandler(this));
        private sealed class PlaceholderHandler(PlaceholderFactory owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Requests++;
                using var image = owner.ServeRealImage
                    ? new Image<Rgba32>(512, 512, new Rgba32(25, 80, 210))
                    : Image.Load(PlaceholderJpeg());
                image.Mutate(x => x.Resize(512, 512));
                using var stream = new MemoryStream();
                image.SaveAsJpeg(stream);
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(stream.ToArray()) };
                response.Content.Headers.ContentType = new("image/jpeg");
                return Task.FromResult(response);
            }
        }
    }

    [Fact]
    public void RealArtworkIsStillAllowed()
    {
        Assert.True(DeezerImageUrlValidator.IsAllowedDeezerImageUrl(
            "https://e-cdns-images.dzcdn.net/images/artist/31b52d923da9434d4b9bde58b896fd97/1000x1000-000000-80-0-0.jpg"));
        Assert.True(DeezerImageUrlValidator.HasUsableDeezerMd5("31b52d923da9434d4b9bde58b896fd97"));
    }

    [Fact]
    public void NonDeezerHostsAreNotRejected()
    {
        Assert.True(DeezerImageUrlValidator.IsAllowedDeezerImageUrl(
            "https://is1-ssl.mzstatic.com/image/thumb/Music/v4/aa/bb/cc/file.jpg/1200x1200bb.jpg"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingUrlsAreBlocked(string? url)
    {
        Assert.False(DeezerImageUrlValidator.IsAllowedDeezerImageUrl(url));
    }
}
