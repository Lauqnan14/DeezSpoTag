using System.Reflection;
using System;
using System.IO;
using DeezSpoTag.Services.Download.Amazon;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Tidal;
using Xunit;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

namespace DeezSpoTag.Tests;

public sealed class DownloadLifecycleCheckpointTest : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"download-checkpoint-{Guid.NewGuid():N}");

    public DownloadLifecycleCheckpointTest() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ProviderRecovery_ExpiredTicketBudgetDoesNotSendARequest()
    {
        var sent = 0;
        await Assert.ThrowsAsync<TimeoutException>(() => ZarzSignedSessionContract.SendTicketedDownloadWithinBudgetAsync(
            async _ => { await Task.Delay(100); return "ticket"; },
            (_, _) => { sent++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); },
            TimeSpan.FromMilliseconds(20), CancellationToken.None));
        Assert.Equal(0, sent);
    }

    [Fact]
    public async Task ProviderRecovery_NormalizesContractBeforeReusingTheOperation()
    {
        var sent = 0;
        var tickets = 0;
        using var response = await ZarzSignedSessionContract.SendTicketedDownloadAsync(
            _ => Task.FromResult($"ticket-{++tickets}"),
            (_, _) => Task.FromResult(new HttpResponseMessage(++sent == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new StringContent(sent == 1 ? """{"origin":" Provider ","code":" provider_unavailable ","retryable":true,"retry_mode":" SAME_OPERATION ","retry_after_seconds":1}""" : "{}") }),
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, sent);
        Assert.Equal(1, tickets);
    }

    [Fact]
    public async Task ProviderRecovery_DoesNotWaitOrRetryPastTheRemainingBudget()
    {
        var method = typeof(ZarzSignedSessionContract).GetMethod("SendTicketedDownloadWithinBudgetAsync");
        Assert.NotNull(method);
        var sent = 0;
        var tickets = 0;
        Func<CancellationToken, Task<string>> issue = _ => Task.FromResult($"ticket-{++tickets}");
        Func<string, CancellationToken, Task<HttpResponseMessage>> send = (_, _) =>
        {
            sent++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            { Content = new StringContent("""{"origin":"provider","code":"PROVIDER_UNAVAILABLE","retryable":true,"retry_mode":"same_operation","retry_after_seconds":60}""") });
        };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(async () => await (Task<HttpResponseMessage>)method!.Invoke(null,
            [issue, send, TimeSpan.FromMilliseconds(200), CancellationToken.None])!);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Equal(1, sent);
        Assert.Equal(1, tickets);
    }

    [Fact]
    public async Task ProviderRecovery_PreservesTransientNetworkRetries()
    {
        var requests = 0;
        var tickets = 0;
        using var response = await ZarzSignedSessionContract.SendTicketedDownloadAsync(
            _ => Task.FromResult($"ticket-{++tickets}"),
            (_, _) => ++requests == 1
                ? throw new HttpRequestException("connection reset")
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)),
            CancellationToken.None);
        Assert.Equal(2, requests);
        Assert.Equal(2, tickets);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task InterruptedAudioTransfer_ResumesOnlyWithMatchingValidatorAndRange()
    {
        var helper = typeof(EngineAudioPostDownloadHelper).Assembly.GetType("DeezSpoTag.Services.Download.Shared.Utils.DownloadStreamHelper")!;
        var method = helper.GetMethod("DownloadFileAsync");
        Assert.NotNull(method);
        var calls = 0;
        using var client = new HttpClient(new RecoveryHandler(request =>
        {
            calls++;
            HttpResponseMessage response;
            if (calls == 1)
            {
                response = new(HttpStatusCode.OK) { Content = new StreamContent(new BrokenAudioStream()) };
                response.Content.Headers.ContentLength = 10;
            }
            else
            {
                Assert.Equal(5, request.Headers.Range!.Ranges.Single().From);
                Assert.Equal("\"recording-1\"", request.Headers.IfRange!.EntityTag!.Tag);
                response = new(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([5, 6, 7, 8, 9]) };
                response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(5, 9, 10);
            }
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"recording-1\"");
            return response;
        }));
        var path = Path.Combine(_root, "resumed.flac");
        await (Task)method!.Invoke(null, [client, "https://audio.invalid/recording", path, null, CancellationToken.None, null])!;
        Assert.Equal(2, calls);
        Assert.Equal(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, await System.IO.File.ReadAllBytesAsync(path));
        Assert.False(System.IO.File.Exists(path + ".partial"));
    }

    [Fact]
    public async Task ProviderRecovery_PreservesLegacyTransientRetriesWithoutAProviderContract()
    {
        var requests = 0;
        var issued = 0;
        using var response = await ZarzSignedSessionContract.SendTicketedDownloadAsync(
            _ => Task.FromResult($"ticket-{++issued}"),
            (_, _) => Task.FromResult(new HttpResponseMessage(++requests == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new StringContent(requests == 1 ? "{\"error\":\"Temporarily unavailable\"}" : "{}") }),
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, issued);
        Assert.Equal(2, requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InterruptedAudioTransfer_NeverCombinesDifferentRepresentations(bool invalidRange)
    {
        var helper = typeof(EngineAudioPostDownloadHelper).Assembly.GetType("DeezSpoTag.Services.Download.Shared.Utils.DownloadStreamHelper")!;
        var method = helper.GetMethod("DownloadFileAsync")!;
        var calls = 0;
        byte[] replacement = [90, 91, 92, 93, 94, 95, 96, 97, 98, 99];
        using var client = new HttpClient(new RecoveryHandler(request =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            if (calls == 1)
            {
                response.Content = new StreamContent(new BrokenAudioStream());
                response.Content.Headers.ContentLength = 10;
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"old\"");
            }
            else
            {
                if (calls == 2) Assert.NotNull(request.Headers.Range);
                if (calls == 3) Assert.Null(request.Headers.Range);
                response.Content = new ByteArrayContent(replacement);
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"new\"");
                if (calls == 2 && invalidRange)
                {
                    response.StatusCode = HttpStatusCode.PartialContent;
                    response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(5, 9, 10);
                }
            }
            return response;
        }));
        var path = Path.Combine(_root, "representation.flac");
        await (Task)method.Invoke(null, [client, "https://audio.invalid/recording", path, null, CancellationToken.None, null])!;
        Assert.Equal(invalidRange ? 3 : 2, calls);
        Assert.Equal(replacement, await System.IO.File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ProviderRecovery_CancellationStopsTheAdvertisedWaitWithoutAnotherTicket()
    {
        var issued = 0;
        var sent = 0;
        using var cancellation = new CancellationTokenSource();
        Func<CancellationToken, Task<string>> issue = _ => Task.FromResult($"ticket-{++issued}");
        Func<string, CancellationToken, Task<HttpResponseMessage>> send = (_, _) =>
        {
            sent++;
            cancellation.CancelAfter(50);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{\"origin\":\"provider\",\"code\":\"PROVIDER_UNAVAILABLE\",\"retryable\":true,\"retry_mode\":\"same_operation\",\"retry_after_seconds\":60}")
            });
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ZarzSignedSessionContract.SendTicketedDownloadWithinBudgetAsync(issue, send, TimeSpan.FromSeconds(90), cancellation.Token));
        Assert.Equal(1, issued);
        Assert.Equal(1, sent);
    }

    private sealed class RecoveryHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(send(request));
    }

    private sealed class BrokenAudioStream : MemoryStream
    {
        private bool sent;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (sent) throw new IOException("connection reset after five bytes");
            new byte[] { 0, 1, 2, 3, 4 }.CopyTo(buffer.Span);
            sent = true;
            return ValueTask.FromResult(5);
        }
    }

    [Theory]
    [InlineData("same_operation", 1)]
    [InlineData("poll_existing", 1)]
    [InlineData("new_ticket", 2)]
    public async Task ProviderRecovery_UsesTheTicketRequiredByTheProvider(string mode, int expectedTickets)
    {
        var method = typeof(ZarzSignedSessionContract).GetMethod("SendTicketedDownloadAsync");
        Assert.NotNull(method);
        var tickets = 0;
        var used = new List<string>();
        Func<CancellationToken, Task<string>> issue = _ => Task.FromResult($"ticket-{++tickets}");
        Func<string, CancellationToken, Task<HttpResponseMessage>> send = (ticket, _) =>
        {
            used.Add(ticket);
            return Task.FromResult(new HttpResponseMessage(used.Count == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = new StringContent(used.Count == 1
                    ? $"{{\"origin\":\"provider\",\"code\":\"PROVIDER_UNAVAILABLE\",\"retryable\":true,\"retry_mode\":\"{mode}\",\"retry_after_seconds\":0}}"
                    : "{}")
            });
        };
        using var response = await (Task<HttpResponseMessage>)method!.Invoke(null, [issue, send, CancellationToken.None])!;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedTickets, tickets);
        Assert.Equal(expectedTickets == 1, used[0] == used[1]);
    }

    [Theory]
    [InlineData("none", false)]
    [InlineData("unknown_mode", true)]
    public async Task ProviderRecovery_DoesNotRepeatTerminalOrUnknownInstructions(string mode, bool retryable)
    {
        var method = typeof(ZarzSignedSessionContract).GetMethod("SendTicketedDownloadAsync");
        Assert.NotNull(method);
        var requests = 0;
        Func<CancellationToken, Task<string>> issue = _ => Task.FromResult("ticket");
        Func<string, CancellationToken, Task<HttpResponseMessage>> send = (_, _) =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent($"{{\"origin\":\"provider\",\"code\":\"PROVIDER_UNAVAILABLE\",\"retryable\":{retryable.ToString().ToLowerInvariant()},\"retry_mode\":\"{mode}\"}}")
            });
        };
        using var response = await (Task<HttpResponseMessage>)method!.Invoke(null, [issue, send, CancellationToken.None])!;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, requests);
    }

    [Fact]
    public void FinalizationFailure_PreservesTheUnderlyingTaggingCause()
    {
        var type = typeof(EngineAudioPostDownloadHelper).Assembly.GetType(
            "DeezSpoTag.Services.Download.Shared.DownloadFinalizationException")!;
        var cause = new InvalidOperationException("tidal tagging failed", new IOException("MP4 tag verification failed"));
        var failure = Activator.CreateInstance(type, "tag_writing", "Audio downloaded; tag writing will be retried.", cause)!;
        Assert.Equal("MP4 tag verification failed", type.GetProperty("OriginalMessage")!.GetValue(failure));
    }

    [Fact]
    public void ExistingValidAudio_IsAdoptedAndResumedWithoutAcquisition()
    {
        var path = Path.Combine(_root, "retained.wav");
        WriteWave(path, 24);
        var payload = new AmazonQueueItem
        {
            Engine = "amazon",
            Quality = "HD_FLAC",
            FilePath = path
        };

        Assert.True(InvokeBool("TryAdoptExistingAudio", payload));
        Assert.True(payload.AudioAcquired);
        Assert.Equal(path, payload.AcquiredAudioPath);
        Assert.True(payload.AcquiredFileSizeBytes > 0);
        Assert.Equal("pending", payload.FinalizationStage);

        var arguments = new object?[] { payload, null };
        Assert.True(InvokeBool("TryResume", arguments));
        Assert.Equal(path, arguments[1]);
    }

    [Fact]
    public void ExistingValidTidalAudio_IsAdoptedFromTheExactCollisionPath()
    {
        var path = Path.Combine(_root, "existing-tidal.wav");
        WriteWave(path, 24);
        var payload = new TidalQueueItem
        {
            Engine = "tidal",
            Quality = "HI_RES",
            AutoIndex = 0,
            FallbackPlan =
            [
                new DeezSpoTag.Services.Download.Fallback.FallbackPlanStep(
                    "step-0", "tidal", "HI_RES", [], "direct_url")
            ]
        };

        Assert.True(InvokeBool("TryAdoptExistingAudioAtPath", payload, path));
        Assert.True(payload.AudioAcquired);
        Assert.Equal(path, payload.AcquiredAudioPath);
        Assert.Equal("tidal", payload.AcquiredEngine);
    }

    [Fact]
    public void MissingCheckpointAudio_ClearsAcquisitionAndReturnsToDownloader()
    {
        var payload = new AmazonQueueItem
        {
            Engine = "amazon",
            Quality = "HD_FLAC",
            AudioAcquired = true,
            AcquiredAudioPath = Path.Combine(_root, "missing.flac"),
            AcquiredFileSizeBytes = 100,
            FinalizationStage = "tag_writing"
        };
        var arguments = new object?[] { payload, null };

        Assert.False(InvokeBool("TryResume", arguments));
        Assert.False(payload.AudioAcquired);
        Assert.Empty(payload.AcquiredAudioPath);
        Assert.Empty(payload.FinalizationStage);
    }

    [Fact]
    public void TaggingSizeChange_DoesNotInvalidateOtherwiseValidAcquiredAudio()
    {
        var path = Path.Combine(_root, "retagged.wav");
        WriteWave(path, 24);
        var originalSize = new FileInfo(path).Length;
        var payload = new AmazonQueueItem
        {
            Engine = "amazon",
            Quality = "HD_FLAC",
            AudioAcquired = true,
            AcquiredAudioPath = path,
            AcquiredFileSizeBytes = originalSize - 128,
            FinalizationStage = "tag_writing"
        };
        var arguments = new object?[] { payload, null };

        Assert.True(InvokeBool("TryResume", arguments));
        Assert.Equal(originalSize, payload.AcquiredFileSizeBytes);
        Assert.Equal(path, arguments[1]);
    }

    [Fact]
    public void SharedAndSpecializedEngines_UseTheSameAcquiredAudioCheckpoint()
    {
        var shared = ReadSource("DeezSpoTag.Services", "Download", "Shared", "EngineQueueProcessorHelper.cs");
        var qobuz = ReadSource("DeezSpoTag.Services", "Download", "Qobuz", "QobuzEngineProcessor.cs");
        var apple = ReadSource("DeezSpoTag.Services", "Download", "Apple", "AppleEngineProcessor.cs");
        var deezer = ReadSource("DeezSpoTag.Services", "Download", "Deezer", "DeezerEngineProcessor.cs");

        Assert.Contains("DownloadLifecycleCheckpoint.TryResume", shared, StringComparison.Ordinal);
        Assert.Contains("DownloadLifecycleCheckpoint.TryResume", qobuz, StringComparison.Ordinal);
        Assert.Contains("DownloadLifecycleCheckpoint.TryResume", apple, StringComparison.Ordinal);
        Assert.Contains("DownloadLifecycleCheckpoint.TryResume", deezer, StringComparison.Ordinal);
        Assert.DoesNotContain("post-download settings failed", shared, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FinalizationFailurePath_DoesNotAdvanceEngineFallback()
    {
        var checkpoint = ReadSource("DeezSpoTag.Services", "Download", "Shared", "DownloadLifecycleCheckpoint.cs");
        var shared = ReadSource("DeezSpoTag.Services", "Download", "Shared", "EngineQueueProcessorHelper.cs");

        Assert.Contains("PersistFinalizationFailureAsync", checkpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("TryAdvanceAsync", checkpoint, StringComparison.Ordinal);
        var catchStart = shared.IndexOf("catch (DownloadFinalizationException ex)", StringComparison.Ordinal);
        Assert.True(catchStart >= 0);
        var catchBody = shared.Substring(catchStart, Math.Min(900, shared.Length - catchStart));
        Assert.Contains("PersistFinalizationFailureAsync", catchBody, StringComparison.Ordinal);
        Assert.DoesNotContain("TryAdvanceAsync", catchBody, StringComparison.Ordinal);
    }

    [Fact]
    public void FinalizationFailure_RetainsAudioAndUsesConciseQueueMessages()
    {
        var checkpoint = ReadSource("DeezSpoTag.Services", "Download", "Shared", "DownloadLifecycleCheckpoint.cs");
        var postDownload = ReadSource("DeezSpoTag.Services", "Download", "Shared", "EngineAudioPostDownloadHelper.cs");
        var repository = ReadSource("DeezSpoTag.Services", "Download", "Queue", "DownloadQueueRepository.cs");

        Assert.Contains("HasRetainedAcquiredAudio(payloadJson)", repository, StringComparison.Ordinal);
        Assert.Contains("Audio downloaded; waiting to retry required album artwork.", postDownload, StringComparison.Ordinal);
        Assert.Contains("Audio downloaded; tag writing will be retried.", postDownload, StringComparison.Ordinal);
        Assert.Contains("Audio downloaded; embedded artwork verification will be retried.", postDownload, StringComparison.Ordinal);
        Assert.Contains("retry_waiting", checkpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Delete", checkpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("TryDelete", checkpoint, StringComparison.Ordinal);
    }

    [Fact]
    public void TidalAudio_IsStagedCheckpointedAndPromotedBeforePostProcessing()
    {
        var shared = ReadSource("DeezSpoTag.Services", "Download", "Shared", "EngineQueueProcessorHelper.cs");
        var tidal = ReadSource("DeezSpoTag.Services", "Download", "Tidal", "TidalDownloadService.cs");
        var processor = ReadSource("DeezSpoTag.Services", "Download", "Tidal", "TidalEngineProcessor.cs");

        var guard = shared.IndexOf("EnsurePlanStepSatisfiedAsync", StringComparison.Ordinal);
        var checkpoint = shared.IndexOf("PersistAcquiredAsync", guard, StringComparison.Ordinal);
        var acceptance = shared.IndexOf("AcceptAcquiredAudioAsync", checkpoint, StringComparison.Ordinal);
        Assert.True(guard >= 0 && checkpoint > guard && acceptance > checkpoint);
        Assert.Contains("AcquiredStagingMarker", tidal, StringComparison.Ordinal);
        Assert.Contains("PromoteAcceptedAudioAsync", processor, StringComparison.Ordinal);
        Assert.Contains("TidalExistingFinalDestinationException", processor, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualRetry_PreservesARecoverableAcquiredAudioCheckpoint()
    {
        var app = ReadSource("DeezSpoTag.Services", "Download", "Shared", "DeezSpoTagApp.cs");

        Assert.Contains("HasRecoverableAcquiredAudio", app, StringComparison.Ordinal);
        Assert.Contains("if (!preserveAcquiredAudio)", app, StringComparison.Ordinal);
        Assert.Contains("ReadJsonInt(payloadObj, \"AutoIndex\", \"autoIndex\")", app, StringComparison.Ordinal);
    }

    public void Dispose() => Directory.Delete(_root, true);

    private static bool InvokeBool(string methodName, params object?[] arguments)
    {
        var type = typeof(EngineQueueItemBase).Assembly.GetType(
            "DeezSpoTag.Services.Download.Shared.DownloadLifecycleCheckpoint",
            true)!;
        var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        return (bool)method.Invoke(null, arguments)!;
    }

    private static string ReadSource(params string[] parts)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../..", Path.Combine(parts)));
        return File.ReadAllText(path);
    }

    private static void WriteWave(string path, short bitsPerSample)
    {
        const short channels = 2;
        const int sampleRate = 44100;
        var blockAlign = (short)(channels * (bitsPerSample / 8));
        var byteRate = sampleRate * blockAlign;
        var dataSize = byteRate;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write("data"u8.ToArray());
        writer.Write(dataSize);
        writer.Write(new byte[dataSize]);
    }
}
