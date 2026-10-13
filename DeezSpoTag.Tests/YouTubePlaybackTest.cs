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
        var resolver = fixture.CreateResolver();
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
        var resolver = fixture.CreateResolver();
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
            var resolver = fixture.CreateResolver();
            await Task.WhenAll(resolver.ResolveAsync(Id, CancellationToken.None), resolver.ResolveAsync(Id, CancellationToken.None));
            Assert.Single(File.ReadAllLines(counter));
            File.WriteAllText(executable, "#!/bin/sh\nexec sleep 30\n");
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ResolveAsync("xxxxxxxxxxx", cancel.Token));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ExtractorThrottlingIsReportedWithoutLeakingDiagnostics()
    {
        if (!OperatingSystem.IsLinux()) return;
        var directory = Directory.CreateTempSubdirectory("yt-throttle-").FullName;
        try
        {
            var executable = Path.Join(directory, "extractor");
            await File.WriteAllTextAsync(executable, "#!/bin/sh\necho 'HTTP Error 429 https://secret.test/signed?token=private' >&2\nexit 1\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var fixture = new Fixture(executable);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.CreateResolver().ResolveAsync(Id, CancellationToken.None));
            Assert.Contains("throttled", error.Message);
            Assert.DoesNotContain("secret.test", error.Message);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task GuestSessionPersistsAcrossResolverRestartAndRefreshesOnceOnRejection()
    {
        var directory = Directory.CreateTempSubdirectory("yt-session-test-").FullName;
        try
        {
            var executable = Path.Join(directory, "extractor");
            var scopes = Path.Join(directory, "token-scopes");
            var json = JsonSerializer.Serialize(new { id = Id, vcodec = "none", acodec = "mp4a", ext = "m4a", url = "https://rr1.googlevideo.com/file?expire=" + DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() });
            await File.WriteAllTextAsync(executable, "#!/bin/sh\necho \"$XDG_CACHE_HOME\" >> '" + scopes + "'\nprintf '%s' '" + json + "'\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var fixture = new Fixture(executable);
            await fixture.CreateResolver().ResolveAsync(Id, CancellationToken.None);
            Assert.Equal(1, fixture.Bootstrap.Calls);
            Assert.True(File.Exists(fixture.SessionFile));
            fixture.Cache.Remove("youtube:stream:" + executable + ":" + Id);
            await fixture.CreateResolver().ResolveAsync(Id, CancellationToken.None);
            Assert.Equal(1, fixture.Bootstrap.Calls);
            fixture.Cache.Remove("youtube:stream:" + executable + ":" + Id);
            await File.WriteAllTextAsync(executable, "#!/bin/sh\necho \"$XDG_CACHE_HOME\" >> '" + scopes + "'\ncase \"$*\" in *fixture-visitor-1*) echo 'Sign in to confirm you are not a bot' >&2; exit 1;; esac\nprintf '%s' '" + json + "'\n");
            await fixture.CreateResolver().ResolveAsync(Id, CancellationToken.None);
            Assert.Equal(2, fixture.Bootstrap.Calls);
            Assert.Contains("fixture-visitor-2", await File.ReadAllTextAsync(fixture.SessionFile));
            var tokenScopes = File.ReadAllLines(scopes);
            Assert.Equal(tokenScopes[0], tokenScopes[1]);
            Assert.Equal(tokenScopes[1], tokenScopes[2]);
            Assert.NotEqual(tokenScopes[2], tokenScopes[3]);
            Assert.All(tokenScopes, scope => Assert.DoesNotContain("fixture-visitor", scope));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task MissingProviderAndInvalidBootstrapFailWithoutFabricatingIdentity()
    {
        using var fixture = new Fixture();
        fixture.Bootstrap.BodyOverride = "{}";
        var invalid = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CreateResolver().ResolveAsync(Id, CancellationToken.None));
        Assert.Contains("invalid data", invalid.Message);
        Assert.False(File.Exists(fixture.SessionFile));
        File.Delete(Path.Join(fixture.DirectoryPath, "runtime", "plugins", "bgutil-ytdlp-pot-provider.zip"));
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CreateResolver().ResolveAsync(Id, CancellationToken.None));
        Assert.Contains("runtime is missing", missing.Message);
        Assert.Equal(1, fixture.Bootstrap.Calls);
    }

    [Theory]
    [InlineData("HTTP Error 429", "throttled")]
    [InlineData("Video unavailable at https://secret.test/?signature=429abc", "unavailable")]
    [InlineData("not available in your country", "geographic")]
    [InlineData("Sign in to confirm you are not a bot", "session")]
    [InlineData("PO Token provider failed", "provider")]
    public void SafeDiagnosticsHaveDistinctCategories(string text, string category)
    {
        var failure = YouTubePlaybackResolver.ClassifyFailure(text + " https://secret.test/token");
        Assert.Equal(category, failure.Category);
        Assert.DoesNotContain("secret.test", failure.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedStreamRefreshesOnceAndPreservesTheRange(bool rejectAgain)
    {
        var directory = Directory.CreateTempSubdirectory("yt-proxy-refresh-").FullName;
        try
        {
            var executable = Path.Join(directory, "extractor");
            var json = JsonSerializer.Serialize(new { id = Id, vcodec = "none", acodec = "mp4a", ext = "m4a", url = "https://rr2.googlevideo.com/file?expire=" + DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() });
            await File.WriteAllTextAsync(executable, "#!/bin/sh\nprintf '%s' '" + json + "'\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var fixture = new Fixture(executable);
            fixture.Cache.Set("youtube:stream:" + executable + ":" + Id,
                new YouTubePlaybackResolver.StreamInfo("https://rr1.googlevideo.com/old", "audio/mp4", DateTimeOffset.UtcNow.AddMinutes(5)));
            using var handler = new Handler { RejectFirst = true, RejectAgain = rejectAgain };
            var api = fixture.Controller;
            api.ControllerContext = new() { HttpContext = new DefaultHttpContext() };
            api.Response.Body = new MemoryStream(); api.Request.Headers.Range = "bytes=0-3";
            var result = await api.Stream(Id, fixture.CreateResolver(), new Factory(handler), CancellationToken.None);
            Assert.Equal(2, handler.Calls);
            Assert.Equal("bytes=0-3", handler.Range);
            if (rejectAgain)
            {
                Assert.Equal(502, Assert.IsType<ObjectResult>(result).StatusCode);
                Assert.Equal(0, api.Response.Body.Length);
            }
            else
            {
                Assert.IsType<EmptyResult>(result);
                Assert.Equal(206, api.Response.StatusCode); Assert.Equal(4, api.Response.Body.Length);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("HTTP Error 429", 1)]
    [InlineData("not available in your country", 1)]
    [InlineData("Sign in to confirm you are not a bot", 2)]
    [InlineData("PO Token provider failed", 1)]
    public async Task RecoveryLimitsAttemptsAndCoalescesRepeatedFailures(string diagnostic, int attempts)
    {
        var directory = Directory.CreateTempSubdirectory("yt-recovery-limit-").FullName;
        try
        {
            var executable = Path.Join(directory, "extractor"); var counter = Path.Join(directory, "calls");
            await File.WriteAllTextAsync(executable, "#!/bin/sh\necho call >> '" + counter + "'\necho '" + diagnostic + "' >&2\nexit 1\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var fixture = new Fixture(executable); var resolver = fixture.CreateResolver();
            await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(Id, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(Id, CancellationToken.None));
            Assert.Equal(attempts, File.ReadAllLines(counter).Length);
            Assert.Equal(attempts, fixture.Bootstrap.Calls);
            if (diagnostic.Contains("429", StringComparison.Ordinal))
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync("aaaaaaaaaaa", CancellationToken.None));
                Assert.Equal(1, File.ReadAllLines(counter).Length);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task CallerCancellationInterruptsSessionRecovery()
    {
        var directory = Directory.CreateTempSubdirectory("yt-recovery-cancel-").FullName;
        try
        {
            var executable = Path.Join(directory, "extractor");
            await File.WriteAllTextAsync(executable, "#!/bin/sh\necho 'Sign in to confirm you are not a bot' >&2\nexit 1\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var fixture = new Fixture(executable); fixture.Bootstrap.HoldSecond = true;
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.CreateResolver().ResolveAsync(Id, cancellation.Token));
            Assert.Equal(2, fixture.Bootstrap.Calls);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task UnsatisfiableRangeIsForwardedWithoutRefreshingTheStream()
    {
        using var fixture = new Fixture();
        fixture.Cache.Set("youtube:stream:yt-dlp:" + Id, new YouTubePlaybackResolver.StreamInfo("https://rr1.googlevideo.com/file", "audio/mp4", DateTimeOffset.UtcNow.AddMinutes(5)));
        using var handler = new Handler { StatusOverride = HttpStatusCode.RequestedRangeNotSatisfiable };
        fixture.Controller.ControllerContext = new() { HttpContext = new DefaultHttpContext() };
        fixture.Controller.Response.Body = new MemoryStream(); fixture.Controller.Request.Headers.Range = "bytes=99-";
        await fixture.Controller.Stream(Id, fixture.CreateResolver(), new Factory(handler), CancellationToken.None);
        Assert.Equal(416, fixture.Controller.Response.StatusCode); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task MalformedPersistedGuestIdentityIsReplacedByBootstrap()
    {
        using var fixture = new Fixture("/missing/extractor");
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.SessionFile)!);
        await File.WriteAllTextAsync(fixture.SessionFile, "{broken");
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CreateResolver().ResolveAsync(Id, CancellationToken.None));
        Assert.Contains("extractor is missing", failure.Message);
        using var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.SessionFile));
        Assert.Equal("fixture-visitor-1", persisted.RootElement.GetProperty("VisitorData").GetString());
        Assert.Equal(1, fixture.Bootstrap.Calls);
    }

    [Fact]
    public async Task AStartedResponseIsNeverRefreshedOrOverwritten()
    {
        using var fixture = new Fixture();
        fixture.Cache.Set("youtube:stream:yt-dlp:" + Id, new YouTubePlaybackResolver.StreamInfo("https://rr1.googlevideo.com/file", "audio/mp4", DateTimeOffset.UtcNow.AddMinutes(5)));
        using var handler = new Handler { RejectFirst = true };
        var context = new DefaultHttpContext();
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>(new StartedResponseFeature());
        fixture.Controller.ControllerContext = new() { HttpContext = context };
        fixture.Controller.Response.Body = new MemoryStream();
        await fixture.Controller.Response.Body.WriteAsync(new byte[] { 1, 2, 3 });
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Controller.Stream(Id, fixture.CreateResolver(), new Factory(handler), CancellationToken.None));
        Assert.Equal(1, handler.Calls); Assert.Equal(3, fixture.Controller.Response.Body.Length);
        Assert.True(fixture.Cache.TryGetValue("youtube:stream:yt-dlp:" + Id, out object? _));
    }

    private sealed class StartedResponseFeature : Microsoft.AspNetCore.Http.Features.HttpResponseFeature
    {
        public override bool HasStarted => true;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "yt-playback-" + Guid.NewGuid());
        public readonly DeezSpoTagSettingsService Settings = new(NullLogger<DeezSpoTagSettingsService>.Instance);
        public readonly MemoryCache Cache = new(new MemoryCacheOptions());
        public readonly YouTubeBrowseApiController Controller;
        public readonly BootstrapHandler Bootstrap = new();
        public string DirectoryPath => directory;
        public string SessionFile => Path.Join(directory, "cache", "youtube-playback", "session.json");
        public YouTubePlaybackResolver CreateResolver() => new(Settings, Cache, new Factory(Bootstrap), directory, Path.Join(directory, "runtime"));
        public Fixture(string executable = "yt-dlp")
        {
            Directory.CreateDirectory(Path.Join(directory, "runtime", "server", "build"));
            Directory.CreateDirectory(Path.Join(directory, "runtime", "plugins"));
            File.WriteAllText(Path.Join(directory, "runtime", "server", "build", "generate_once.js"), "fixture");
            File.WriteAllText(Path.Join(directory, "runtime", "plugins", "bgutil-ytdlp-pot-provider.zip"), "fixture");
            var file = Path.Combine(directory, "config.json");
            typeof(DeezSpoTagSettingsService).GetField("_configFolder", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Settings, directory);
            typeof(DeezSpoTagSettingsService).GetField("_settingsFilePath", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Settings, file);
            File.WriteAllText(file, JsonSerializer.Serialize(new { youTubeExtractorPath = executable }));
            Controller = new(new YouTubeMusicSearchClient(new HttpClient()), Settings, Cache, NullLogger<YouTubeBrowseApiController>.Instance);
        }
        public void Dispose() { Cache.Dispose(); Directory.Delete(directory, true); }
    }
    private sealed class BootstrapHandler : HttpMessageHandler
    {
        public int Calls;
        public string? BodyOverride;
        public bool HoldSecond;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            if (HoldSecond && Calls == 2) await Task.Delay(Timeout.Infinite, token);
            object?[] device = new object?[14]; device[13] = "fixture-visitor-" + Calls;
            var json = JsonSerializer.Serialize(new object?[] { new object?[] { null, null, new object?[] { new object?[] { device }, null } } });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(BodyOverride ?? json) };
        }
    }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, false); }
    private sealed class Handler : HttpMessageHandler
    {
        public string? Range;
        public bool Redirect;
        public HttpStatusCode? StatusOverride;
        public bool RejectFirst;
        public bool RejectAgain;
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Range = request.Headers.Range?.ToString();
            var response = new HttpResponseMessage(StatusOverride ?? (RejectFirst && (Calls == 1 || RejectAgain) ? HttpStatusCode.Forbidden : Redirect ? HttpStatusCode.Redirect : HttpStatusCode.PartialContent)) { Content = new ByteArrayContent([1, 2, 3, 4]) };
            response.Content.Headers.ContentRange = new(0, 3, 20);
            return Task.FromResult(response);
        }
    }
}
