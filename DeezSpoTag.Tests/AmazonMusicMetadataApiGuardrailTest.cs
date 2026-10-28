using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

[Collection("Settings Config Isolation")]
public sealed class AmazonMusicMetadataApiGuardrailTest
{
    [Fact]
    public async Task PermanentSessionRejection_DoesNotRebootstrapIndefinitely()
    {
        var root = Path.Join(Path.GetTempPath(), "amazon-session-rejection-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            using var config = new TestConfigRootScope(root);
            var auth = new PlatformAuthService(new BootstrapEnvironment(root), NullLogger<PlatformAuthService>.Instance,
                new EphemeralDataProtectionProvider());
            using var handler = new BootstrapHandler(HttpStatusCode.Forbidden);
            var service = new AmazonMusicMetadataService(handler, auth, NullLogger<AmazonMusicMetadataService>.Instance);
            var failure = await Assert.ThrowsAsync<HttpRequestException>(() => service.SearchAsync("Song Artist", "track", 25, CancellationToken.None));
            Assert.Equal(HttpStatusCode.Forbidden, failure.StatusCode);
            Assert.Equal(2, handler.BootstrapCount);
            Assert.Equal(2, handler.CatalogCount);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleAsinDownload_RetriesOneVerifiedIdentityAndProducesValidatedAudio(bool interruptFirstRun)
    {
        var root = Path.Join(Path.GetTempPath(), "amazon-download-recovery-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            using var config = new TestConfigRootScope(root);
            var fixture = Path.Join(root, "fixture.flac");
            var start = new System.Diagnostics.ProcessStartInfo("ffmpeg") { RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[] { "-v", "error", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo", "-t", "1", "-c:a", "flac", "-y", fixture })
                start.ArgumentList.Add(argument);
            using (var process = System.Diagnostics.Process.Start(start)!)
            {
                var errors = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                Assert.True(process.ExitCode == 0, await errors);
            }
            var audio = await File.ReadAllBytesAsync(fixture);
            using var handler = new AmazonDownloadHandler(audio) { FailTransfers = interruptFirstRun ? 4 : 0 };
            using var client = new HttpClient(handler);
            var sessions = new DeezSpoTag.Services.Download.Shared.ZarzSignedSessionCoordinator(
                NullLogger<DeezSpoTag.Services.Download.Shared.ZarzSignedSessionCoordinator>.Instance);
            await sessions.EnsureSessionAsync("amazon", (_, _) => Task.FromResult(
                new DeezSpoTag.Services.Download.Shared.ZarzSessionBootstrapResult(
                    new DeezSpoTag.Services.Download.Shared.ZarzSignedSession { InstallId = "fixture", SessionId = "fixture",
                        SessionSecret = "fixture-secret", ExpiresAt = DateTimeOffset.UtcNow.AddDays(2) }, null)), CancellationToken.None);
            var auth = new PlatformAuthService(new BootstrapEnvironment(root), NullLogger<PlatformAuthService>.Instance,
                new EphemeralDataProtectionProvider());
            using var catalog = new BootstrapHandler { TrackResponseBody = """{"items":[{"deeplink":"/tracks/B000000002","primaryText":"Song","artistName":"Artist","durationMs":1000}]}""" };
            var resolver = new AmazonMusicMetadataService(catalog, auth, NullLogger<AmazonMusicMetadataService>.Instance);
            var downloader = new DeezSpoTag.Services.Download.Amazon.AmazonDownloadService(
                NullLogger<DeezSpoTag.Services.Download.Amazon.AmazonDownloadService>.Instance, new AmazonRegistry(), sessions, resolver);
            typeof(DeezSpoTag.Services.Download.Amazon.AmazonDownloadService).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(downloader, client);
            var request = new DeezSpoTag.Services.Download.Amazon.AmazonDownloadRequest {
                AmazonId = "B000000001", TrackName = "Song", ArtistName = "Artist", AlbumName = "Album", DurationSeconds = 1,
                Quality = "HD_FLAC", OutputDir = Path.Join(root, "downloads"), FilenameFormat = "{artist} - {title}"
            };
            if (interruptFirstRun)
                await Assert.ThrowsAsync<IOException>(() => downloader.DownloadAsync(request, false, null, null, CancellationToken.None));
            var result = await downloader.DownloadAsync(request, false, null, null, CancellationToken.None);
            Assert.True(File.Exists(result));
            using var tagged = TagLib.File.Create(result);
            // Provider acquisition renames and validates; the queue's existing post-download
            // stage owns tag writing. Do not invent a second tag writer in this service.
            Assert.Contains("Song", Path.GetFileName(result));
            Assert.Contains("Artist", Path.GetFileName(result));
            Assert.True(tagged.Properties.Duration.TotalSeconds > 0);
            Assert.Equal(interruptFirstRun ? 2 : 1, catalog.DetailRequests);
            Assert.Equal(interruptFirstRun ? new[] { "B000000001", "B000000002", "B000000001", "B000000002" }
                : new[] { "B000000001", "B000000002" }, handler.Asins);
            Assert.All(handler.Codecs, codec => Assert.Equal("flac", codec));
            Assert.Equal(interruptFirstRun ? 4 : 2, handler.Tickets);
            Assert.Equal(interruptFirstRun ? 5 : 1, handler.Transfers);
            if (interruptFirstRun) Assert.Equal(20, handler.LastOffset);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "TRACK_NOT_FOUND", false, true)]
    [InlineData(HttpStatusCode.NotFound, "TRACK_UNAVAILABLE", true, false)]
    [InlineData(HttpStatusCode.NotFound, "QUALITY_UNAVAILABLE", false, false)]
    [InlineData(HttpStatusCode.NotFound, "VERIFY_REQUIRED", false, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "TRACK_NOT_FOUND", false, false)]
    public void CatalogRecovery_OnlyRunsForTerminalTrackAbsence(HttpStatusCode status, string code, bool retryable, bool expected)
    {
        var method = typeof(DeezSpoTag.Services.Download.Amazon.AmazonDownloadService).GetMethod("IsTerminalTrackNotFound", BindingFlags.NonPublic | BindingFlags.Static)!;
        var body = JsonSerializer.Serialize(new { origin = "provider", code, retryable, retry_mode = "none" });
        Assert.Equal(expected, method.Invoke(null, new object[] { status, body }));
    }

    [Fact]
    public void AmazonTransferPath_IsStableAcrossRetriesAndSeparatesRecordingQualityAndKey()
    {
        var method = typeof(DeezSpoTag.Services.Download.Amazon.AmazonDownloadService).GetMethod("BuildAcquisitionPath", BindingFlags.NonPublic | BindingFlags.Static)!;
        var request = new DeezSpoTag.Services.Download.Amazon.AmazonDownloadRequest { TrackName = "Song", ArtistName = "Artist", Quality = "HD_FLAC" };
        string PathFor(string asin, string codec, string key) => (string)method.Invoke(null, new object?[] { "/tmp", asin, codec, key, ".flac", request })!;
        var original = PathFor("B000000001", "flac", "key");
        Assert.Equal(original, PathFor("B000000001", "flac", "key"));
        Assert.NotEqual(original, PathFor("B000000002", "flac", "key"));
        Assert.NotEqual(original, PathFor("B000000001", "eac3", "key"));
        Assert.NotEqual(original, PathFor("B000000001", "flac", "new-key"));
        request.Quality = "ULTRA_HD_FLAC";
        Assert.NotEqual(original, PathFor("B000000001", "flac", "key"));
    }

    private sealed class AmazonDownloadHandler(byte[] audio) : HttpMessageHandler
    {
        public System.Collections.Generic.List<string> Asins { get; } = [];
        public System.Collections.Generic.List<string> Codecs { get; } = [];
        public int FailTransfers { get; init; }
        public long LastOffset { get; private set; }
        public int Tickets { get; private set; }
        public int Transfers { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/tickets", StringComparison.Ordinal))
            {
                Tickets++;
                return new(HttpStatusCode.OK) { Content = new StringContent("""{"ticket":"fixture-ticket"}""") };
            }
            if (path.EndsWith("/amazeamazeamaze", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var asin = body.RootElement.GetProperty("asin").GetString()!;
                Asins.Add(asin);
                Codecs.Add(body.RootElement.GetProperty("codec").GetString()!);
                return asin == "B000000001"
                    ? new(HttpStatusCode.NotFound) { Content = new StringContent("""{"origin":"provider","code":"TRACK_NOT_FOUND","retryable":false,"retry_mode":"none"}""") }
                    : new(HttpStatusCode.OK) { Content = new StringContent("""{"audio":{"url":"https://audio.invalid/fixture.flac","codec":"flac"}}""") };
            }
            Assert.Equal("audio.invalid", request.RequestUri.Host);
            Transfers++;
            LastOffset = request.Headers.Range?.Ranges.Single().From ?? 0;
            var result = new HttpResponseMessage(LastOffset > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = Transfers <= FailTransfers
                    ? new StreamContent(new InterruptedAmazonAudio(audio, (int)LastOffset))
                    : new ByteArrayContent(audio[(int)LastOffset..])
            };
            result.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"fixture-recording\"");
            result.Content.Headers.ContentLength = audio.Length - LastOffset;
            if (LastOffset > 0) result.Content.Headers.ContentRange =
                new System.Net.Http.Headers.ContentRangeHeaderValue(LastOffset, audio.Length - 1, audio.Length);
            return result;
        }
    }

    private sealed class InterruptedAmazonAudio(byte[] audio, int offset) : MemoryStream
    {
        private bool read;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (read) throw new IOException("Interrupted fixture transfer");
            audio.AsSpan(offset, 5).CopyTo(buffer.Span);
            read = true;
            return ValueTask.FromResult(5);
        }
    }

    private sealed class AmazonRegistry : DeezSpoTag.Integrations.Amazon.IAmazonPublicProviderRegistry
    {
        public Task<System.Collections.Generic.IReadOnlyList<DeezSpoTag.Integrations.Amazon.AmazonPublicProvider>> GetProvidersAsync(CancellationToken ct)
            => Task.FromResult<System.Collections.Generic.IReadOnlyList<DeezSpoTag.Integrations.Amazon.AmazonPublicProvider>>(
                [new("fixture", "fixture", "download", "", null, null, true, "ready", null, null, null, null, null, null)]);
        public Task<System.Collections.Generic.IReadOnlyList<DeezSpoTag.Integrations.Amazon.AmazonPublicProvider>> CheckEnabledProvidersAsync(CancellationToken ct) => GetProvidersAsync(ct);
        public Task<DeezSpoTag.Integrations.Amazon.AmazonPublicProvider?> SetEnabledAsync(string id, bool enabled, CancellationToken ct) => throw new NotSupportedException();
        public Task RecordSuccessAsync(string id, long ms, CancellationToken ct) => Task.CompletedTask;
        public Task RecordFailureAsync(string id, string category, long ms, CancellationToken ct) => Task.CompletedTask;
    }

    [Theory]
    [InlineData("Song", "Artist", 180000, "USABC1234567", true)]
    [InlineData("Wrong Song", "Artist", 180000, "USABC1234567", false)]
    [InlineData("Song (Live)", "Artist", 180000, "USABC1234567", false)]
    [InlineData("Song", "Wrong Artist", 180000, "USABC1234567", false)]
    [InlineData("Song", "Artist", 185000, "USABC1234567", false)]
    [InlineData("Song", "Artist", 0, "USABC1234567", false)]
    [InlineData("Song", "Artist", 180000, "USABC7654321", false)]
    public async Task CanonicalAsinRecovery_RequiresTheSameRecording(string title, string artist, int duration, string isrc, bool accepted)
    {
        var method = typeof(AmazonMusicMetadataService).GetMethod("ResolveAmazonCanonicalTrackAsync");
        Assert.NotNull(method);
        var root = Path.Join(Path.GetTempPath(), "amazon-canonical-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            using var config = new TestConfigRootScope(root);
            var auth = new PlatformAuthService(new BootstrapEnvironment(root), NullLogger<PlatformAuthService>.Instance,
                new EphemeralDataProtectionProvider());
            using var handler = new BootstrapHandler { TrackResponseBody = JsonSerializer.Serialize(new { items = new[] {
                new { deeplink = "/tracks/B000000002", primaryText = title, artistName = artist, durationMs = duration, isrc }
            } }) };
            var service = new AmazonMusicMetadataService(handler, auth, NullLogger<AmazonMusicMetadataService>.Instance);
            var task = (Task<DeezSpoTag.Services.Download.Fallback.AmazonFallbackTrackResolution?>)method!.Invoke(service,
                new object?[] { "B000000001", "Song", "Artist", null, 180000, "USABC1234567", CancellationToken.None })!;
            var result = await task;
            Assert.Equal(accepted ? "B000000002" : null, result?.Id);
            Assert.Equal(1, handler.DetailRequests);
            Assert.Equal(1, handler.CatalogCount);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task RejectedGuestSession_IsRebuiltOnceWithFreshPayloadHeaders(HttpStatusCode rejection)
    {
        var root = Path.Join(Path.GetTempPath(), "amazon-session-refresh-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            using var config = new TestConfigRootScope(root);
            var auth = new PlatformAuthService(new BootstrapEnvironment(root), NullLogger<PlatformAuthService>.Instance,
                new EphemeralDataProtectionProvider());
            await auth.SaveAsync(new PlatformAuthState { AmazonMusic = new AmazonMusicAuth { Host = "music.amazon.co.uk" } });
            using var handler = new BootstrapHandler { RejectFirstCatalogRequest = rejection };
            var service = new AmazonMusicMetadataService(handler, auth, NullLogger<AmazonMusicMetadataService>.Instance);
            await service.SearchAsync("Song Artist", "track", 25, CancellationToken.None);
            Assert.Equal(2, handler.BootstrapCount);
            Assert.Equal(2, handler.CatalogCount);
            Assert.Contains("guest-csrf-2", handler.SearchBody);
            Assert.Contains("music.amazon.co.uk", handler.BootstrapUri);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void MatchingIsrc_DoesNotOverrideAConflictingAmazonTitle()
    {
        var method = typeof(AmazonMusicMetadataService).GetMethod("IsAcceptedResolvedTrack", BindingFlags.NonPublic | BindingFlags.Static)!;
        var track = new AmazonCatalogItem("B012345678", "track", "An Entirely Different Song", "Nivea", "Album", "", "", 188000, "FR8GV1715720", false);
        Assert.False((bool)method.Invoke(null, new object?[] { track, "Don't Mess With My Man", "Nivea", "Album", 188000, "FR8GV1715720" })!);
    }

    [Fact]
    public async Task SingleToAlbumException_DoesNotDisplaceAnExistingValidRelease()
    {
        var root = Path.Join(Path.GetTempPath(), "amazon-release-precedence-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            using var config = new TestConfigRootScope(root);
            var auth = new PlatformAuthService(new BootstrapEnvironment(root), NullLogger<PlatformAuthService>.Instance,
                new EphemeralDataProtectionProvider());
            await auth.SaveAsync(new PlatformAuthState { AmazonMusic = new AmazonMusicAuth { Host = "music.amazon.com" } });
            using var handler = new BootstrapHandler
            {
                SearchResponseBody = System.Text.Json.JsonSerializer.Serialize(new { items = new[]
                {
                    (Id: "B00O9WYR5O", Album: "My Life"), (Id: "B003YT64H2", Album: "Missing You - Single")
                }.Select(item => new
                {
                    primaryText = "Missing You", secondaryText = "I-Octane",
                    iconButton = new { observer = new { storageKey = "B00O9WYQQ2:" + item.Id } },
                    contextMenu = new { options = new[] { new { text = "Go to album", headerText = item.Album } } }
                }) }),
                TrackResponseBody = "{\"items\":[]}"
            };
            var service = new AmazonMusicMetadataService(handler, auth, NullLogger<AmazonMusicMetadataService>.Instance);
            var result = await service.ResolveTrackAsync("Missing You", "I-Octane", "Missing You - Single", 255000, "US4R31001332", CancellationToken.None);
            Assert.Equal("B003YT64H2", result?.Id);
            Assert.Equal(0, handler.DetailRequests);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("Missing You - Single", 255000, 255000, "Missing You", "I-Octane", "", true)]
    [InlineData("Missing You", 255000, 257000, "Missing You", "I-Octane", "", true)]
    [InlineData("Another Album", 255000, 255000, "Missing You", "I-Octane", "", false)]
    [InlineData("Missing You - Single", 255000, 258000, "Missing You", "I-Octane", "", false)]
    [InlineData("Missing You - Single", 255000, 0, "Missing You", "I-Octane", "", false)]
    [InlineData("Missing You - Single", 0, 255000, "Missing You", "I-Octane", "", false)]
    [InlineData("Missing You - Single", 255000, 255000, "Missing You (Dirty)", "I-Octane", "", false)]
    [InlineData("Missing You - Single", 255000, 255000, "Missing You", "Another Artist", "", false)]
    [InlineData("Missing You - Single", 255000, 255000, "Missing You", "I-Octane", "USVPR1200009", false)]
    public void SingleToAlbumException_RequiresConfirmedRecordingEvidence(string album, int expectedDuration,
        int deliveredDuration, string title, string artist, string isrc, bool accepted)
    {
        var candidate = new AmazonCatalogItem("B00O9WYR5O", "track", title, artist, "My Life", "", "", null, "", false);
        var details = new AmazonTrack(candidate.Id, title, "", "My Life", "", "", deliveredDuration, 1, isrc, candidate.Id, false);
        var method = typeof(AmazonMusicMetadataService).GetMethod("MatchSingleAlbumCandidate", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var result = method.Invoke(null, new object?[] { candidate, details, "Missing You", "I-Octane", album, expectedDuration, "US4R31001332" });
        Assert.Equal(accepted, result != null);
    }

    [Theory]
    [InlineData(255000, "B00O9WYR5O", true)]
    [InlineData(0, "B00O9WYR5O", false)]
    [InlineData(258000, "B00O9WYR5O", false)]
    [InlineData(255000, "B003YT64H2", false)]
    public async Task SingleToAlbumResolution_HydratesOnlyTheExactSearchIdentity(int duration, string detailId, bool accepted)
    {
        var root = Path.Join(Path.GetTempPath(), "amazon-single-album-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            using var config = new TestConfigRootScope(root);
            var auth = new PlatformAuthService(new BootstrapEnvironment(root), NullLogger<PlatformAuthService>.Instance,
                new EphemeralDataProtectionProvider());
            await auth.SaveAsync(new PlatformAuthState { AmazonMusic = new AmazonMusicAuth { Host = "music.amazon.com" } });
            using var handler = new BootstrapHandler
            {
                SearchResponseBody = "{\"items\":[{\"primaryText\":\"Missing You\",\"secondaryText\":\"I-Octane\",\"iconButton\":{\"observer\":{\"storageKey\":\"B00O9WYQQ2:B00O9WYR5O\"}},\"contextMenu\":{\"options\":[{\"text\":\"Go to album\",\"headerText\":\"My Life\"}]}}]}",
                TrackResponseBody = System.Text.Json.JsonSerializer.Serialize(new { items = new[] { new {
                    deeplink = "https://music.amazon.com/tracks/" + detailId, primaryText = "Missing You", durationMs = duration,
                    contextMenu = new { options = new[] { new { text = "Go to album", headerText = "My Life" } } }
                } } })
            };
            var service = new AmazonMusicMetadataService(handler, auth, NullLogger<AmazonMusicMetadataService>.Instance);
            var result = await service.ResolveTrackAsync("Missing You", "I-Octane", "Missing You - Single", 255000, "US4R31001332", CancellationToken.None);
            Assert.Equal(accepted, result != null);
            Assert.Equal(1, handler.DetailRequests);
            if (accepted)
            {
                Assert.Equal("B00O9WYR5O", result!.Id);
                Assert.Equal(255000, result.DurationMs);
                Assert.Equal("I-Octane", result.Artist);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task TypedSearchAccessFailure_IsNotReportedAsAnEmptyCatalog()
    {
        var root = Path.Join(Path.GetTempPath(), "amazon-search-failure-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            using var config = new TestConfigRootScope(root);
            var auth = new PlatformAuthService(new BootstrapEnvironment(root), NullLogger<PlatformAuthService>.Instance,
                new EphemeralDataProtectionProvider());
            await auth.SaveAsync(new PlatformAuthState { AmazonMusic = new AmazonMusicAuth { Host = "music.amazon.com" } });
            using var handler = new BootstrapHandler(HttpStatusCode.Forbidden);
            var service = new AmazonMusicMetadataService(handler, auth, NullLogger<AmazonMusicMetadataService>.Instance);
            var failure = await Assert.ThrowsAsync<HttpRequestException>(() => service.SearchAsync("Song Artist", "track", 25, CancellationToken.None));
            Assert.Equal(HttpStatusCode.Forbidden, failure.StatusCode);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("music.amazon.com")]
    [InlineData("music.amazon.co.uk")]
    public async Task AnonymousBootstrapUsesSkyfirePostAndPassesIssuedCsrfToSearch(string host)
    {
        var root = Path.Join(Path.GetTempPath(), "amazon-bootstrap-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            using var scope = new TestConfigRootScope(root);
            var auth = new PlatformAuthService(new BootstrapEnvironment(root), NullLogger<PlatformAuthService>.Instance,
                new EphemeralDataProtectionProvider());
            await auth.SaveAsync(new PlatformAuthState { AmazonMusic = new AmazonMusicAuth { Host = host } });
            using var handler = new BootstrapHandler();
            var service = new AmazonMusicMetadataService(handler, auth, NullLogger<AmazonMusicMetadataService>.Instance);
            await service.SearchAsync("Believer", "track", 3, CancellationToken.None);
            Assert.Equal(HttpMethod.Post, handler.BootstrapMethod);
            Assert.Equal($"https://{host}/config.json?skipToken=false&clientApplication=skyfire", handler.BootstrapUri);
            Assert.False(handler.HadCookie);
            Assert.NotNull(handler.SearchBody);
            using var body = JsonDocument.Parse(handler.SearchBody!);
            using var headers = JsonDocument.Parse(body.RootElement.GetProperty("headers").GetString()!);
            using var csrf = JsonDocument.Parse(headers.RootElement.GetProperty("x-amzn-csrf").GetString()!);
            Assert.Equal("guest-csrf", csrf.RootElement.GetProperty("token").GetString());
            Assert.Equal("issued-ts", csrf.RootElement.GetProperty("timestamp").GetString());
            Assert.Equal("issued-nonce", csrf.RootElement.GetProperty("rndNonce").GetString());
            using var authentication = JsonDocument.Parse(headers.RootElement.GetProperty("x-amzn-authentication").GetString()!);
            Assert.Equal(string.Empty, authentication.RootElement.GetProperty("accessToken").GetString());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class BootstrapHandler(HttpStatusCode searchStatus = HttpStatusCode.OK) : HttpMessageHandler, IHttpClientFactory
    {
        public HttpStatusCode? RejectFirstCatalogRequest { get; set; }
        public int BootstrapCount { get; private set; }
        public int CatalogCount { get; private set; }
        public string? SearchResponseBody { get; set; }
        public string? TrackResponseBody { get; set; }
        public int DetailRequests { get; private set; }
        public HttpMethod? BootstrapMethod { get; private set; }
        public string? BootstrapUri { get; private set; }
        public bool HadCookie { get; private set; }
        public string? SearchBody { get; private set; }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            HadCookie |= request.Headers.Contains("Cookie");
            string response;
            if (request.RequestUri!.AbsolutePath == "/config.json")
            {
                BootstrapCount++;
                BootstrapMethod = request.Method;
                BootstrapUri = request.RequestUri.AbsoluteUri;
                response = request.Method == HttpMethod.Post && request.RequestUri.Query == "?skipToken=false&clientApplication=skyfire"
                    ? "{\"sessionId\":\"guest-session\",\"deviceId\":\"guest-device\",\"csrf\":{\"token\":\"guest-csrf\",\"ts\":\"issued-ts\",\"rnd\":\"issued-nonce\"}}"
                    : "{\"sessionId\":\"guest-session\",\"deviceId\":\"guest-device\"}";
            }
            else
            {
                CatalogCount++;
                SearchBody = await request.Content!.ReadAsStringAsync(token);
                if (request.RequestUri.AbsolutePath.EndsWith("/searchCatalogTracks", StringComparison.Ordinal))
                    response = SearchResponseBody ?? "{\"methods\":[]}";
                else
                {
                    DetailRequests++;
                    response = TrackResponseBody ?? "{\"methods\":[]}";
                }
            }
            if (RejectFirstCatalogRequest.HasValue) response = response.Replace("guest-csrf", "guest-csrf-" + BootstrapCount);
            return new HttpResponseMessage(request.RequestUri!.AbsolutePath == "/config.json" ? HttpStatusCode.OK
                : CatalogCount == 1 && RejectFirstCatalogRequest.HasValue ? RejectFirstCatalogRequest.Value : searchStatus)
                { Content = new StringContent(response) };
        }
    }

    private sealed class BootstrapEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "DeezSpoTag.Tests";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public void AmazonMetadataSessionFailure_ReportsMissingFieldNamesWithoutValues()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AmazonMusicMetadataService.cs");
        var start = source.IndexOf("if (string.IsNullOrWhiteSpace(sessionId) ||", StringComparison.Ordinal);
        var end = source.IndexOf("return new AmazonSession", start, StringComparison.Ordinal);
        var failure = source[start..end];
        Assert.Contains("missingFields.Add(\"sessionId\")", failure);
        Assert.Contains("missingFields.Add(\"deviceId\")", failure);
        Assert.Contains("missingFields.Add(\"csrf\")", failure);
        Assert.DoesNotContain("missingFields.Add(sessionId)", failure);
        Assert.DoesNotContain("missingFields.Add(deviceId)", failure);
        Assert.DoesNotContain("missingFields.Add(csrf)", failure);
    }

    [Fact]
    public void AmazonMetadata_UsesRegionalSkillEndpointsWithLegacyFallback()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AmazonMusicMetadataService.cs");

        Assert.Contains("RegionConfigs", source, StringComparison.Ordinal);
        Assert.Contains("https://na.web.skill.music.a2z.com/api", source, StringComparison.Ordinal);
        Assert.Contains("https://eu.web.skill.music.a2z.com/api", source, StringComparison.Ordinal);
        Assert.Contains("https://fe.web.skill.music.a2z.com/api", source, StringComparison.Ordinal);
        Assert.Contains("https://na.mesk.skill.music.a2z.com/api", source, StringComparison.Ordinal);
        Assert.Contains("https://eu.mesk.skill.music.a2z.com/api", source, StringComparison.Ordinal);
        Assert.Contains("https://fe.mesk.skill.music.a2z.com/api", source, StringComparison.Ordinal);
        Assert.Contains("ResolveRegionConfig(host)", source, StringComparison.Ordinal);
        Assert.Contains("session.SkillApiBaseUrls", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AmazonMetadata_UsesOnlyDedicatedTypedSearchEndpoints()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AmazonMusicMetadataService.cs");

        Assert.Contains("SearchTracksPath = \"/searchCatalogTracks\"", source, StringComparison.Ordinal);
        Assert.Contains("SearchAlbumsPath = \"/searchCatalogAlbums\"", source, StringComparison.Ordinal);
        Assert.Contains("SearchArtistsPath = \"/searchCatalogArtists\"", source, StringComparison.Ordinal);
        Assert.Contains("SearchPlaylistsPath = \"/searchCatalogPlaylists\"", source, StringComparison.Ordinal);
        Assert.Contains("SearchCommunityPlaylistsPath = \"/searchCommunityPlaylists\"", source, StringComparison.Ordinal);
        Assert.Contains("TrySearchTypedAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchAllPath", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/showSearch", source, StringComparison.Ordinal);
        Assert.Contains("TrySearchCommunityPlaylistsAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AmazonMetadata_UsesTypedCatalogFetchWithoutDeeplinkFallback()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AmazonMusicMetadataService.cs");

        Assert.Contains("TrackInfoPath = \"/cosmicTrack/displayCatalogTrack\"", source, StringComparison.Ordinal);
        Assert.Contains("AlbumInfoPath = \"/showCatalogAlbum\"", source, StringComparison.Ordinal);
        Assert.Contains("PlaylistInfoPath = \"/showCatalogPlaylist\"", source, StringComparison.Ordinal);
        Assert.Contains("CommunityPlaylistInfoPath = \"/showLibraryPlaylist\"", source, StringComparison.Ordinal);
        Assert.Contains("FetchCatalogDocumentAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FetchCatalogDocumentOrHomeAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FetchHomeDocumentAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("showHome", source, StringComparison.Ordinal);
        Assert.DoesNotContain("deeplink home fetch", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AmazonTrackResolution_UsesValidatedTypedTrackSearchOnly()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AmazonMusicMetadataService.cs");

        Assert.Contains("SearchAsync(session, trackQuery, \"track\"", source, StringComparison.Ordinal);
        Assert.Contains("ResolveTrackFromCatalogAsync", source, StringComparison.Ordinal);
        Assert.Contains("ResolveTrackCatalogCandidatesAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ExpandAlbumTracksForSearchAsync", source, StringComparison.Ordinal);
        Assert.Contains("ResolveAtmosTrackAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AmazonTrackResolution_ValidatesAlbumVariant()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AmazonMusicMetadataService.cs");

        Assert.Contains("string? album,\n        int? durationMs", source, StringComparison.Ordinal);
        Assert.Contains("Album: album", source, StringComparison.Ordinal);
        Assert.Contains("MatchSingleAlbumCandidate", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AmazonMetadata_WiresRemainingReferenceMetadataEndpoints()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "AmazonMusicMetadataService.cs");

        Assert.Contains("SearchCommunityPlaylistsPath = \"/searchCommunityPlaylists\"", source, StringComparison.Ordinal);
        Assert.Contains("CommunityPlaylistInfoPath = \"/showLibraryPlaylist\"", source, StringComparison.Ordinal);
        Assert.Contains("ArtistTopTracksPath = \"/showCatalogTracks\"", source, StringComparison.Ordinal);
        Assert.Contains("ExtractCommunityPlaylistSearchItems", source, StringComparison.Ordinal);
        Assert.Contains("FetchArtistTopTracksAsync", source, StringComparison.Ordinal);
        Assert.Contains("IsCommunityPlaylistUrl", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AmazonSession_UsesCurrentMontanaCsrfToken()
    {
        using var document = JsonDocument.Parse("""{"montanaCsrf":"current-token"}""");

        var token = ResolveAmazonCsrf(document.RootElement);

        Assert.Equal("current-token", token);
    }

    [Fact]
    public void AmazonSession_KeepsLegacyCsrfTokenCompatibility()
    {
        using var document = JsonDocument.Parse("""{"csrf":{"token":"legacy-token","ts":"1","rnd":"2"}}""");

        var token = ResolveAmazonCsrf(document.RootElement);

        Assert.Equal("legacy-token", token);
    }

    [Fact]
    public void AmazonSession_RejectsResponseWithoutCsrfToken()
    {
        using var document = JsonDocument.Parse("""{"sessionId":"session","deviceId":"device"}""");

        var token = ResolveAmazonCsrf(document.RootElement);

        Assert.Null(token);
    }

    private static string? ResolveAmazonCsrf(JsonElement root)
    {
        var method = typeof(AmazonMusicMetadataService).GetMethod(
            "ResolveAmazonCsrf",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method.Invoke(null, [root]) as string;
    }

    private static string ReadSource(params string[] relativeParts)
    {
        var repoRoot = ResolveRepoRoot();
        return File.ReadAllText(Path.Combine(new[] { repoRoot }.Concat(relativeParts).ToArray()));
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Directory.Build.props")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Unable to locate repository root.");
    }
}
