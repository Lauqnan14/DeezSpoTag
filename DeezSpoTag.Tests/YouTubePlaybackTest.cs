using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations.YouTube;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Controllers.Api;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class YouTubePlaybackTest
{
    [Fact]
    public async Task NonMusicSegments_FilterValidateAndSort()
    {
        using var http = new HttpClient(new SegmentHandler(HttpStatusCode.OK, """
        [{"category":"music_offtopic","actionType":"skip","segment":[20,25],"UUID":"b"},
         {"category":"music_offtopic","actionType":"skip","segment":[1,5],"UUID":"a"},
         {"category":"sponsor","actionType":"skip","segment":[6,8],"UUID":"c"},
         {"category":"music_offtopic","actionType":"mute","segment":[6,8],"UUID":"d"},
         {"category":"music_offtopic","actionType":"skip","segment":[-1,2],"UUID":"e"},
         {"category":"music_offtopic","actionType":"skip","segment":[9,8],"UUID":"f"},
         {"category":"music_offtopic","actionType":"skip","segment":["bad",8],"UUID":"g"}]
        """));
        var segments = await new YouTubeMusicSearchClient(http).GetNonMusicSegmentsAsync(Id, CancellationToken.None);
        Assert.Equal(2, segments.Count);
        Assert.Equal("a", segments[0].Uuid);
        Assert.Equal(1, segments[0].StartSeconds);
        Assert.Equal(25, segments[1].EndSeconds);
    }

    [Fact]
    public async Task NonMusicSegments_NoCoverageInvalidIdAndServiceFailure()
    {
        using var http = new HttpClient(new SegmentHandler(HttpStatusCode.NotFound, ""));
        var client = new YouTubeMusicSearchClient(http);
        Assert.Empty(await client.GetNonMusicSegmentsAsync(Id, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetNonMusicSegmentsAsync("invalid", CancellationToken.None));
        using var failed = new HttpClient(new SegmentHandler(HttpStatusCode.ServiceUnavailable, ""));
        await Assert.ThrowsAsync<HttpRequestException>(() => new YouTubeMusicSearchClient(failed).GetNonMusicSegmentsAsync(Id, CancellationToken.None));
    }

    private sealed class SegmentHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("sponsor.ajay.app", request.RequestUri!.Host);
            Assert.Contains("music_offtopic", Uri.UnescapeDataString(request.RequestUri.Query));
            Assert.Contains(Id, request.RequestUri.Query);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private const string Id = "dQw4w9WgXcQ";
    [Fact]
    public void StreamingProcess_CannotDownloadOrReadAmbientConfiguration()
    {
        var process = YouTubePlaybackResolver.BuildProcess("yt-dlp", Id);
        Assert.False(process.UseShellExecute);
        Assert.Contains("--simulate", process.ArgumentList);
        Assert.Contains("--skip-download", process.ArgumentList);
        Assert.Contains("--ignore-config", process.ArgumentList);
        Assert.Contains("--no-plugin-dirs", process.ArgumentList);
        Assert.Contains("--no-cache-dir", process.ArgumentList);
        Assert.DoesNotContain("--cookies", process.ArgumentList);
        Assert.Throws<ArgumentException>(() => YouTubePlaybackResolver.BuildProcess("yt-dlp", "; touch /tmp/file"));
    }

    [Theory]
    [InlineData("https://evil.test/file")]
    [InlineData("https://googlevideo.com.evil.test/file")]
    [InlineData("http://rr1.googlevideo.com/file")]
    [InlineData("https://rr1.googlevideo.com:8080/file")]
    [InlineData("https://user:pass@rr1.googlevideo.com/file")]
    public void StreamingHost_RejectsUntrustedDestinations(string url) => Assert.False(YouTubePlaybackResolver.IsMediaUrl(url));

    [Fact]
    public void StreamingJson_RejectsWrongIdentityVideoAndExpiredMedia()
    {
        var now = DateTimeOffset.UtcNow;
        string Payload(string id, string videoCodec, long expiry) => JsonSerializer.Serialize(new { id, vcodec = videoCodec, acodec = "mp4a", ext = "m4a", url = "https://rr1.googlevideo.com/videoplayback?expire=" + expiry });
        var valid = YouTubePlaybackResolver.ParseStream(Payload(Id, "none", now.AddHours(1).ToUnixTimeSeconds()), Id, now);
        Assert.Equal("audio/mp4", valid.ContentType);
        Assert.Throws<InvalidOperationException>(() => YouTubePlaybackResolver.ParseStream(Payload("xxxxxxxxxxx", "none", now.AddHours(1).ToUnixTimeSeconds()), Id, now));
        Assert.Throws<InvalidOperationException>(() => YouTubePlaybackResolver.ParseStream(Payload(Id, "h264", now.AddHours(1).ToUnixTimeSeconds()), Id, now));
        Assert.Throws<InvalidOperationException>(() => YouTubePlaybackResolver.ParseStream(Payload(Id, "none", now.ToUnixTimeSeconds()), Id, now));
    }

    [Fact]
    public async Task StreamProxy_ForwardsRangeAndRejectsRedirects()
    {
        using var fixture = new Fixture();
        fixture.Cache.Set("youtube:stream:yt-dlp:" + Id, new YouTubePlaybackResolver.StreamInfo("https://rr1.googlevideo.com/videoplayback", "audio/mp4", DateTimeOffset.UtcNow.AddMinutes(5)));
        var resolver = new YouTubePlaybackResolver(fixture.Settings, fixture.Cache);
        using var handler = new Handler();
        var api = fixture.Controller;
        api.ControllerContext = new() { HttpContext = new DefaultHttpContext() };
        api.Response.Body = new MemoryStream();
        api.Request.Headers.Range = "bytes=0-3";
        await api.Stream(Id, resolver, new Factory(handler), CancellationToken.None);
        Assert.Equal("bytes=0-3", handler.Range);
        Assert.Equal(206, api.Response.StatusCode);
        Assert.Equal("bytes 0-3/20", api.Response.Headers.ContentRange.ToString());
        Assert.Equal(4, api.Response.Body.Length);
        handler.Redirect = true;
        api.ControllerContext = new() { HttpContext = new DefaultHttpContext() };
        Assert.Equal(502, Assert.IsType<ObjectResult>(await api.Stream(Id, resolver, new Factory(handler), CancellationToken.None)).StatusCode);
        api.Request.Headers.Range = "bytes=0-1,4-5";
        Assert.IsType<BadRequestObjectResult>(await api.Stream(Id, resolver, new Factory(handler), CancellationToken.None));
    }

    [Fact]
    public async Task MissingExtractorAndCancelledRequest_AreExplicitFailures()
    {
        using var fixture = new Fixture("/missing/youtube-stream-extractor");
        var resolver = new YouTubePlaybackResolver(fixture.Settings, fixture.Cache);
        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(Id, CancellationToken.None));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ResolveAsync(Id, cancelled.Token));
    }

    [Fact]
    public async Task StreamingProcess_CoalescesRequestsAndStopsOnCancellation()
    {
        if (!OperatingSystem.IsLinux()) return;
        var directory = Path.Combine(Path.GetTempPath(), "yt-extractor-fixture-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var executable = Path.Combine(directory, "extractor");
            var counter = Path.Combine(directory, "calls");
            var json = JsonSerializer.Serialize(new { id = Id, vcodec = "none", acodec = "mp4a", ext = "m4a", url = "https://rr1.googlevideo.com/videoplayback?expire=" + DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() });
            File.WriteAllText(executable, "#!/bin/sh\necho call >> '" + counter + "'\nprintf '%s' '" + json + "'\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var fixture = new Fixture(executable);
            var resolver = new YouTubePlaybackResolver(fixture.Settings, fixture.Cache);
            await Task.WhenAll(resolver.ResolveAsync(Id, CancellationToken.None), resolver.ResolveAsync(Id, CancellationToken.None));
            Assert.Single(File.ReadAllLines(counter));
            File.WriteAllText(executable, "#!/bin/sh\nexec sleep 30\n");
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ResolveAsync("xxxxxxxxxxx", cancel.Token));
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "yt-playback-" + Guid.NewGuid());
        public readonly DeezSpoTagSettingsService Settings = new(NullLogger<DeezSpoTagSettingsService>.Instance);
        public readonly MemoryCache Cache = new(new MemoryCacheOptions());
        public readonly YouTubeBrowseApiController Controller;
        public Fixture(string executable = "yt-dlp")
        {
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "config.json");
            typeof(DeezSpoTagSettingsService).GetField("_configFolder", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Settings, directory);
            typeof(DeezSpoTagSettingsService).GetField("_settingsFilePath", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Settings, file);
            File.WriteAllText(file, JsonSerializer.Serialize(new { youTubeExtractorPath = executable }));
            Controller = new(new YouTubeMusicSearchClient(new HttpClient()), Settings, Cache, NullLogger<YouTubeBrowseApiController>.Instance);
        }
        public void Dispose() { Cache.Dispose(); Directory.Delete(directory, true); }
    }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, false); }
    private sealed class Handler : HttpMessageHandler
    {
        public string? Range;
        public bool Redirect;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Range = request.Headers.Range?.ToString();
            var response = new HttpResponseMessage(Redirect ? HttpStatusCode.Redirect : HttpStatusCode.PartialContent) { Content = new ByteArrayContent([1, 2, 3, 4]) };
            response.Content.Headers.ContentRange = new(0, 3, 20);
            return Task.FromResult(response);
        }
    }
}
