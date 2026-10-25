using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations.Qobuz;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Qobuz;
using DeezSpoTag.Services.Download.Shared;
using System.Text.Json;
using System.Net;
using System.Net.Http;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class QobuzQualityFallbackTest
{
    [Fact]
    public void DownloadedAudio_WithoutEmbeddedIdentityIsNotMarkedVerified()
    {
        var root = Path.Combine(Path.GetTempPath(), "qobuz-untagged-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "audio.wav");
            DeliveredAudioQualityGuardTest.WriteWave(path, 16);
            var method = typeof(QobuzDownloadService).GetMethod("ValidateDownloadedAudioIdentity", BindingFlags.NonPublic | BindingFlags.Static)!;
            var result = method.Invoke(null, new object[] { path,
                new QobuzDownloadRequest { TrackName = "Song", ArtistName = "Artist", Isrc = "USGZ20930467" } })!;
            Assert.True((bool)result.GetType().GetProperty("Success")!.GetValue(result)!);
            Assert.False((bool)result.GetType().GetProperty("Conclusive")!.GetValue(result)!);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void DownloadedAudio_WithOneConflictingIsrcIsRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "qobuz-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "audio.wav");
            DeliveredAudioQualityGuardTest.WriteWave(path, 16);
            using (var audio = TagLib.File.Create(path))
            {
                audio.Tag.Title = "Song";
                audio.Tag.Performers = ["Artist"];
                audio.Tag.ISRC = "USGZ20930468";
                audio.Save();
            }
            using (var saved = TagLib.File.Create(path)) Assert.Equal("USGZ20930468", saved.Tag.ISRC);
            var method = typeof(QobuzDownloadService).GetMethod("ValidateDownloadedAudioIdentity", BindingFlags.NonPublic | BindingFlags.Static)!;
            var result = method.Invoke(null, new object[] { path,
                new QobuzDownloadRequest { TrackName = "Song", ArtistName = "Artist", Isrc = "USGZ20930467" } })!;
            Assert.False((bool)result.GetType().GetProperty("Success")!.GetValue(result)!);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task VerifiedSession_CooldownResetPreservesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var providers = new EmptyQualityProviders { Failure = new TaskCanceledException() };
        var service = new QobuzDownloadService(NullLogger<QobuzDownloadService>.Instance, Options.Create(new QobuzApiConfig()),
            new CountingCredentials(), new ZarzSignedSessionCoordinator(NullLogger<ZarzSignedSessionCoordinator>.Instance), providers);
        var method = typeof(QobuzDownloadService).GetMethod("ResetVerifiedProviderBackoffAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            (Task)method.Invoke(service, new object[] { cancellation.Token })!);
    }

    [Fact]
    public async Task VerifiedSession_IsNotRejectedWhenCooldownRegistryCannotBeRead()
    {
        var providers = new EmptyQualityProviders { Failure = new IOException("registry read failed") };
        var service = new QobuzDownloadService(NullLogger<QobuzDownloadService>.Instance, Options.Create(new QobuzApiConfig()),
            new CountingCredentials(), new ZarzSignedSessionCoordinator(NullLogger<ZarzSignedSessionCoordinator>.Instance), providers);
        var method = typeof(QobuzDownloadService).GetMethod("ResetVerifiedProviderBackoffAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(service, new object[] { CancellationToken.None })!;
    }

    [Fact]
    public async Task FreshVerifiedSession_ClearsOnlyEnabledSignedProviderBackoff()
    {
        var name = "verified-" + Guid.NewGuid().ToString("N");
        var other = "disabled-" + Guid.NewGuid().ToString("N");
        var providers = new EmptyQualityProviders { Providers = new[]
        {
            new QobuzPublicProvider("enabled", name, "zarz-v2", "https://example.invalid", null, null, null, true, "online", null, null, null, null, null, null),
            new QobuzPublicProvider("disabled", other, "zarz-v2", "https://example.invalid", null, null, null, false, "disabled", null, null, null, null, null, null)
        } };
        var service = new QobuzDownloadService(NullLogger<QobuzDownloadService>.Instance, Options.Create(new QobuzApiConfig()),
            new CountingCredentials(), new ZarzSignedSessionCoordinator(NullLogger<ZarzSignedSessionCoordinator>.Instance), providers);
        var dictionary = (System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset>)typeof(QobuzDownloadService)
            .GetField("ProviderBackoffUntil", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        dictionary[name] = dictionary[other] = DateTimeOffset.UtcNow.AddMinutes(10);
        try
        {
            var method = typeof(QobuzDownloadService).GetMethod("ResetVerifiedProviderBackoffAsync", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            await (Task)method.Invoke(service, new object[] { CancellationToken.None })!;
            Assert.False(dictionary.ContainsKey(name));
            Assert.True(dictionary.ContainsKey(other));
        }
        finally { dictionary.TryRemove(name, out _); dictionary.TryRemove(other, out _); }
    }

    [Fact]
    public async Task ExpiredPreResolvedStream_FreshUrlDeliversTheOriginalAudio()
    {
        var root = Path.Combine(Path.GetTempPath(), "qobuz-stream-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var input = Path.Combine(root, "fixture.flac");
            var processInfo = new System.Diagnostics.ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardError = true };
            foreach (var argument in new[] { "-v", "error", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo", "-t", "0.2", input })
                processInfo.ArgumentList.Add(argument);
            using (var process = System.Diagnostics.Process.Start(processInfo)!)
            {
                var error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                Assert.True(process.ExitCode == 0, error);
            }
            var audio = await File.ReadAllBytesAsync(input);
            var handler = new RenewedStreamHandler(audio);
            using var client = new HttpClient(handler);
            var service = CreateQualityService(new ValidCredentials());
            foreach (var field in new[] { "_apiClient", "_downloadClient" })
                typeof(QobuzDownloadService).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(service, client);
            var request = new QobuzDownloadRequest
            {
                TrackUrl = "https://play.qobuz.com/track/74444082", TrackName = "Song", ArtistName = "Artist",
                OutputDir = root, Quality = "6",
                ResolvedQuality = new QobuzQualityResolution(74444082, "6", "6", "https://expired.invalid/audio", 16, 44.1, "zarz")
            };
            var path = await service.DownloadByUrlAsync(request, CancellationToken.None);
            Assert.Equal(audio, await File.ReadAllBytesAsync(path));
            Assert.Equal(new[] { "expired", "resolve", "fresh" }, handler.Requests);
            Assert.Equal("6", request.Quality);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class ValidCredentials : IQobuzCredentialProvider
    {
        public Task<QobuzOfficialCredentials> GetCredentialsAsync(CancellationToken token)
            => Task.FromResult(new QobuzOfficialCredentials("app", "token", "secret"));
    }

    private sealed class RenewedStreamHandler(byte[] audio) : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.Host == "expired.invalid")
            {
                Requests.Add("expired");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            }
            if (request.RequestUri.Host == "fresh.invalid")
            {
                Requests.Add("fresh");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(audio) });
            }
            Requests.Add("resolve");
            Assert.Contains("format_id=6", request.RequestUri.Query, StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"url\":\"https://fresh.invalid/audio\",\"bit_depth\":16,\"sampling_rate\":44.1}") });
        }
    }
    [Fact]
    public async Task ExpiredPreResolvedStream_ResumesFreshResolutionAtTheSameQuality()
    {
        var credentials = new CountingCredentials();
        var service = CreateQualityService(credentials);
        using var client = new HttpClient(new ExpiredStreamHandler());
        typeof(QobuzDownloadService).GetField("_downloadClient", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, client);
        var root = Path.Combine(Path.GetTempPath(), "qobuz-expired-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var request = new QobuzDownloadRequest
            {
                TrackUrl = "https://play.qobuz.com/track/74444082", TrackName = "Song", ArtistName = "Artist",
                OutputDir = root, Quality = "6",
                ResolvedQuality = new QobuzQualityResolution(74444082, "6", "6", "https://expired.invalid/audio", 16, 44.1, "zarz")
            };
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadByUrlAsync(request, CancellationToken.None));
            Assert.Equal("official unavailable", failure.Message);
            Assert.Equal(1, credentials.Calls);
            Assert.Null(request.ResolvedQuality);
            Assert.Equal("6", request.Quality);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class CountingCredentials : IQobuzCredentialProvider
    {
        public int Calls { get; private set; }
        public Task<QobuzOfficialCredentials> GetCredentialsAsync(CancellationToken token)
        {
            Calls++;
            return Task.FromException<QobuzOfficialCredentials>(new InvalidOperationException("official unavailable"));
        }
    }

    private sealed class ExpiredStreamHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
    }

    [Fact]
    public async Task ResolveQuality_PreservesOfficialFailureWhenNoPublicStreamSucceeds()
    {
        var failure = new InvalidOperationException("Qobuz official API returned HTTP 401");
        var service = CreateQualityService(new FailingCredentials(failure));
        var result = await Assert.ThrowsAsync<AggregateException>(() => service.ResolveQualityAsync(74444082, "7", CancellationToken.None));
        Assert.Contains(failure, result.InnerExceptions);
    }

    [Fact]
    public async Task ResolveQuality_CallerCancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = CreateQualityService(new FailingCredentials(new OperationCanceledException(cancellation.Token)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ResolveQualityAsync(74444082, "7", cancellation.Token));
    }

    private static QobuzDownloadService CreateQualityService(IQobuzCredentialProvider credentials)
        => new(NullLogger<QobuzDownloadService>.Instance, Options.Create(new QobuzApiConfig()), credentials,
            new ZarzSignedSessionCoordinator(NullLogger<ZarzSignedSessionCoordinator>.Instance), new EmptyQualityProviders());

    private sealed class FailingCredentials(Exception failure) : IQobuzCredentialProvider
    {
        public Task<QobuzOfficialCredentials> GetCredentialsAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromException<QobuzOfficialCredentials>(failure);
        }
    }

    private sealed class EmptyQualityProviders : IQobuzPublicProviderRegistry
    {
        public Exception? Failure { get; set; }
        public IReadOnlyList<QobuzPublicProvider> Providers { get; set; } = [];
        public Task<IReadOnlyList<QobuzPublicProvider>> GetProvidersAsync(CancellationToken ct)
            => Failure == null ? Task.FromResult(Providers) : Task.FromException<IReadOnlyList<QobuzPublicProvider>>(Failure);
        public Task<IReadOnlyList<QobuzPublicProvider>> CheckEnabledProvidersAsync(CancellationToken ct) => GetProvidersAsync(ct);
        public Task<QobuzPublicProvider?> SetEnabledAsync(string id, bool enabled, CancellationToken ct) => Task.FromResult<QobuzPublicProvider?>(null);
        public Task RecordSuccessAsync(string id, long ms, CancellationToken ct) => Task.CompletedTask;
        public Task RecordFailureAsync(string id, string category, long ms, DateTimeOffset? cooldown, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public void QualityUnknown_DoesNotClaimCatalogQualityIsBelowRequested()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
        var processor = File.ReadAllText(Path.Join(root, "DeezSpoTag.Services/Download/Qobuz/QobuzEngineProcessor.cs"));
        Assert.Contains("? FallbackFailureClassifier.ProviderQualityUnknown", processor);
        var downloader = File.ReadAllText(Path.Join(root, "DeezSpoTag.Services/Download/Qobuz/QobuzDownloadService.cs"));
        var start = downloader.IndexOf("public async Task<QobuzQualityResolution?> ResolveQualityAsync", StringComparison.Ordinal);
        var end = downloader.IndexOf("private static bool IsReusableResolution", start, StringComparison.Ordinal);
        var quality = downloader[start..end];
        Assert.Contains("failures.Add(ex)", quality);
        Assert.Contains("failures.Add(attempt.Failure)", quality);
        Assert.Contains("throw new AggregateException", quality);
    }

    [Fact]
    public void BuildRequest_UsesOnlyTheCurrentPersistedPlanQuality()
    {
        var request = QobuzRequestBuilder.BuildRequest(
            new QobuzQueueItem
            {
                Title = "Track",
                Artist = "Artist",
                Album = "Album",
                Quality = "27"
            },
            new DeezSpoTagSettings
            {
                Service = "qobuz",
                FallbackBitrate = true,
                QobuzQuality = "6"
            });

        Assert.Equal("27", request.Quality);
        Assert.Null(typeof(QobuzDownloadRequest).GetProperty("AllowQualityFallback"));
        Assert.Null(typeof(QobuzDownloadService).GetMethod(
            "GetQualityFallbackOrder",
            BindingFlags.NonPublic | BindingFlags.Static));
    }

    [Fact]
    public void QobuzRuntime_DoesNotReplaceThePersistedPlanQualityFromDownloadCallback()
    {
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Services/Download/Qobuz/QobuzEngineProcessor.cs"));
        var source = File.ReadAllText(sourcePath);
        var methodStart = source.IndexOf("private async Task SyncResolvedQualityAsync", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("private async Task<QobuzQueueItem?> DeserializeAndStartAsync", methodStart, StringComparison.Ordinal);

        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        var method = source[methodStart..methodEnd];
        Assert.Contains("payload.QobuzResolvedQuality = resolvedQuality;", method, StringComparison.Ordinal);
        Assert.DoesNotContain("payload.Quality = resolvedQuality;", method, StringComparison.Ordinal);
    }

    [Fact]
    public void FallbackCoordinator_AdvancesOnlyFromPersistedAutoIndex()
    {
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Services/Download/Fallback/EngineFallbackCoordinator.cs"));
        var source = File.ReadAllText(sourcePath);
        var methodStart = source.IndexOf("private static int ResolveNextPlanIndex", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("private static SourceResolutionRequest", methodStart, StringComparison.Ordinal);

        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        var method = source[methodStart..methodEnd];
        Assert.Contains("return request.AutoIndex + 1;", method, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.Max", method, StringComparison.Ordinal);
        Assert.DoesNotContain("FindStepIndex", method, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyQobuzPayload_IsRealignedToItsPersistedPlanStepBeforeValidation()
    {
        var payload = new QobuzQueueItem
        {
            Engine = "qobuz",
            SourceService = "qobuz",
            Quality = "6",
            AutoIndex = 0,
            FallbackPlan =
            [
                new("step-0", "qobuz", "7", [], "direct_url"),
                new("step-1", "tidal", "HI_RES", [], "direct_url"),
                new("step-2", "amazon", "ULTRA_HD_FLAC", [], "direct_url"),
                new("step-3", "qobuz", "6", [], "direct_url")
            ]
        };
        var method = typeof(QobuzEngineProcessor).GetMethod(
            "AlignPayloadToPersistedPlanStep",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        method.Invoke(null, [payload]);

        Assert.Equal("7", payload.Quality);
        Assert.Equal(0, payload.AutoIndex);
        Assert.Equal("7", payload.ResolvedQuality);
        Assert.Equal(0, payload.ResolvedAutoIndex);
    }

    [Theory]
    [InlineData(16, 44.1, "6")]
    [InlineData(24, 48, "7")]
    [InlineData(24, 96, "7")]
    [InlineData(32, 192, "27")]
    public void MapCatalogQuality_MapsQobuzMetadataToEngineQuality(int bitDepth, double sampleRate, string expected)
    {
        Assert.Equal(expected, InvokeMapCatalogQuality(bitDepth, sampleRate));
    }

    [Theory]
    [InlineData("27", "6", "6")]
    [InlineData("27", "7", "7")]
    [InlineData("27", "27", "27")]
    [InlineData("7", "27", "7")]
    [InlineData("6", "27", "6")]
    public void SelectQualityWithinCatalogCeiling_UsesCatalogAsMaximumQuality(
        string requested,
        string catalog,
        string expected)
    {
        Assert.Equal(expected, InvokeSelectQualityWithinCatalogCeiling(requested, catalog));
    }

    [Fact]
    public void ToQueuePayload_IncludesQobuzCatalogQualityDecisionFields()
    {
        var payload = new QobuzQueueItem
        {
            Id = "queue-1",
            Title = "Track",
            Artist = "Artist",
            Quality = "6",
            QobuzRequestedQuality = "27",
            QobuzResolvedQuality = "6",
            QobuzMaximumBitDepth = 16,
            QobuzMaximumSamplingRate = 44.1,
            QobuzCatalogQuality = "6",
            QobuzQualityDecisionReason = "catalog_quality_lower_than_requested"
        };

        var json = JsonSerializer.Serialize(payload.ToQueuePayload());

        Assert.Contains("\"qobuzRequestedQuality\":\"27\"", json);
        Assert.Contains("\"qobuzResolvedQuality\":\"6\"", json);
        Assert.Contains("\"qobuzMaximumBitDepth\":16", json);
        Assert.Contains("\"qobuzMaximumSamplingRate\":44.1", json);
        Assert.Contains("\"qobuzCatalogQuality\":\"6\"", json);
        Assert.Contains("\"qobuzQualityDecisionReason\":\"catalog_quality_lower_than_requested\"", json);
    }

    [Fact]
    public void ProviderUnknownPath_DoesNotContinueToAudioDownload()
    {
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Services/Download/Qobuz/QobuzEngineProcessor.cs"));
        var source = File.ReadAllText(sourcePath);
        var unknownBranchStart = source.IndexOf(
            "payload.QobuzQualityDecisionReason = \"provider_quality_unknown\";",
            StringComparison.Ordinal);

        Assert.True(unknownBranchStart >= 0);
        var unknownBranchEnd = source.IndexOf(
            "payload.QobuzMaximumBitDepth = resolution.BitDepth",
            unknownBranchStart,
            StringComparison.Ordinal);
        Assert.True(unknownBranchEnd > unknownBranchStart);
        var unknownBranch = source[unknownBranchStart..unknownBranchEnd];

        Assert.Contains("QobuzQualityDecisionResult.Skip", unknownBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("QobuzQualityDecisionResult.Continue", unknownBranch, StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogQualityBelowCurrentStep_SkipsBeforeAudioTransfer()
    {
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Services/Download/Qobuz/QobuzEngineProcessor.cs"));
        var source = File.ReadAllText(sourcePath);
        var lowerBranchStart = source.IndexOf("if (lowersRequestedQuality)", StringComparison.Ordinal);
        var lowerBranchEnd = source.IndexOf("request.Quality = selectedQuality;", lowerBranchStart, StringComparison.Ordinal);

        Assert.True(lowerBranchStart >= 0 && lowerBranchEnd > lowerBranchStart);
        var lowerBranch = source[lowerBranchStart..lowerBranchEnd];
        Assert.Contains("QobuzQualityDecisionResult.Skip", lowerBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadOnceAsync", lowerBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("payload.Quality = selectedQuality", lowerBranch, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingCanonicalDestination_IsAdoptedInsteadOfRedownloaded()
    {
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Services/Download/Qobuz/QobuzEngineProcessor.cs"));
        var source = File.ReadAllText(sourcePath);
        var methodStart = source.IndexOf("private async Task<string> DownloadOrAdoptExistingAsync", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("private async Task<QobuzQualityDecisionResult>", methodStart, StringComparison.Ordinal);

        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        var method = source[methodStart..methodEnd];
        Assert.Contains("QobuzExistingFinalDestinationException", method, StringComparison.Ordinal);
        Assert.Contains("TryAdoptExistingAudioAtPath(context.Payload, existing.FilePath)", method, StringComparison.Ordinal);
        Assert.Contains("return DownloadPathResolver.ResolveIoPath(existing.FilePath);", method, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"download_url":"https://audio.test/track.flac","quality_label":"6","bit_depth":16,"sampling_rate":44.1}""", 16, 44.1, "6")]
    [InlineData("""{"data":{"url":"https://audio.test/track.flac","quality_label":"7","bit_depth":24,"sampling_rate":96}}""", 24, 96, "7")]
    [InlineData("""{"url":"https://audio.test/track.flac","quality_label":"27","bit_depth":"24","sampling_rate":"192000"}""", 24, 192, "27")]
    public void ProviderQualityResolution_DrivesThePlanWithoutCatalogLookup(
        string json,
        int expectedBitDepth,
        double expectedSamplingRate,
        string expectedQuality)
    {
        var method = typeof(QobuzDownloadService).GetMethod(
            "TryExtractQualityResolution",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var args = new object?[] { json, "test", 280142894L, "7", "test-provider", null };
        var success = Assert.IsType<bool>(method!.Invoke(null, args));
        var resolution = Assert.IsType<QobuzQualityResolution>(args[5]);

        Assert.True(success);
        Assert.Equal(expectedBitDepth, resolution.BitDepth);
        Assert.Equal(expectedSamplingRate, resolution.SamplingRate);
        Assert.Equal(expectedQuality, resolution.AvailableQualityCode);
        Assert.Equal(expectedQuality, InvokeMapCatalogQuality(resolution.BitDepth, resolution.SamplingRate));
    }

    [Fact]
    public void QobuzProcessor_UsesProviderQualityResolutionAndNotCatalogTrackQuality()
    {
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Services/Download/Qobuz/QobuzEngineProcessor.cs"));
        var source = File.ReadAllText(sourcePath);

        Assert.Contains("_qobuzDownloader.ResolveQualityAsync(", source, StringComparison.Ordinal);
        Assert.Contains("request.ResolvedQuality = resolution;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetTrackQuality(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveCatalogQualitySignalAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SixteenBitProviderResult_SkipsHiResButSatisfiesCdLossless()
    {
        const string availableQuality = "6";

        Assert.Equal("6", InvokeSelectQualityWithinCatalogCeiling("7", availableQuality));
        Assert.Equal("6", InvokeSelectQualityWithinCatalogCeiling("6", availableQuality));
        Assert.NotEqual("7", InvokeSelectQualityWithinCatalogCeiling("7", availableQuality));
    }

    [Fact]
    public void ToQueuePayload_IncludesSourceSettingsSnapshot()
    {
        var payload = new QobuzQueueItem
        {
            Id = "queue-1",
            Title = "Track",
            Artist = "Artist",
            Quality = "27",
            SourceSettingsSnapshot = QueueSourceSettingsSnapshot.Capture(new DeezSpoTagSettings
            {
                Service = "custom",
                QobuzQuality = "27",
                FallbackSearch = true,
                DownloadEngineOrder = new DownloadEngineOrderSettings
                {
                    Enabled = true,
                    Engines = new List<DownloadEngineOrderItem>
                    {
                        new()
                        {
                            Engine = "qobuz",
                            Enabled = true,
                            Qualities = new List<DownloadEngineQualityItem>
                            {
                                new() { Quality = "27", Enabled = true }
                            }
                        }
                    }
                }
            })
        };

        var json = JsonSerializer.Serialize(payload.ToQueuePayload());

        Assert.Contains("\"sourceSettingsSnapshot\"", json);
        Assert.Contains("\"Service\":\"custom\"", json);
        Assert.Contains("\"QobuzQuality\":\"27\"", json);
        Assert.Contains("\"FallbackSearch\":true", json);
        Assert.Contains("\"DownloadEngineOrder\"", json);
    }

    private static string InvokeMapCatalogQuality(int bitDepth, double sampleRate)
    {
        var method = typeof(QobuzEngineProcessor).GetMethod(
            "MapCatalogQuality",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var result = method!.Invoke(null, new object[] { bitDepth, sampleRate });
        return Assert.IsType<string>(result);
    }

    private static string InvokeSelectQualityWithinCatalogCeiling(string requested, string catalog)
    {
        var method = typeof(QobuzEngineProcessor).GetMethod(
            "SelectQualityWithinCatalogCeiling",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var result = method!.Invoke(null, new object[] { requested, catalog });
        return Assert.IsType<string>(result);
    }

}
