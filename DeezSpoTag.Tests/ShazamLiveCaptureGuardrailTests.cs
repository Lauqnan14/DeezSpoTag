using System;
using System.IO;
using System.Reflection;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Guardrails for the Shazam live-capture path: microphone capture fidelity, the
/// recognition request contract, and keeping slow work off the request thread.
/// </summary>
public sealed class ShazamLiveCaptureGuardrailTests
{
    [Fact]
    public void LiveCapture_UploadsAtTheCapturedRateWithoutClientResampling()
    {
        var source = ReadWebFile("wwwroot", "js", "shazam-listen.js");

        // Naive decimation without an anti-aliasing filter folds content above 8 kHz back
        // into the band the fingerprint peaks come from. The recognizer downsamples with a
        // proper filter itself, so the client must hand over what it captured.
        Assert.DoesNotContain("resampleMono", source, StringComparison.Ordinal);
        Assert.Contains("const encodeWavBlob = (floatChunks, sr) =>", source, StringComparison.Ordinal);
        Assert.Contains("const capturedRate = Math.round(Number(sr));", source, StringComparison.Ordinal);

        // Asking for a 16 kHz context lets the browser resample properly instead.
        Assert.Contains("const TARGET_SAMPLE_RATE = 16000;", source, StringComparison.Ordinal);
        Assert.Contains("new AudioContextCtor({ sampleRate: TARGET_SAMPLE_RATE })", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveCapture_PrefersAudioWorkletAndKeepsScriptProcessorFallback()
    {
        var source = ReadWebFile("wwwroot", "js", "shazam-listen.js");
        var worklet = ReadWebFile("wwwroot", "js", "shazam-capture-processor.js");

        // A ScriptProcessorNode runs on the main thread, so layout or GC jank drops buffers
        // and the resulting time discontinuity shifts every later fingerprint peak.
        Assert.Contains("context.audioWorklet.addModule(captureWorkletUrl)", source, StringComparison.Ordinal);
        Assert.Contains("'shazam-capture-processor'", source, StringComparison.Ordinal);
        Assert.Contains("registerProcessor('shazam-capture-processor'", worklet, StringComparison.Ordinal);

        // Insecure-origin LAN sessions have no AudioWorklet, and this path supports them.
        Assert.Contains("context.createScriptProcessor(CAPTURE_BLOCK_SIZE, 1, 1)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveCapture_TimesOutRequestsAndStaysCancellableWhileSearching()
    {
        var source = ReadWebFile("wwwroot", "js", "shazam-listen.js");

        Assert.Contains("const RECOGNITION_REQUEST_TIMEOUT_MS", source, StringComparison.Ordinal);
        Assert.Contains("createRequestSignal(options?.signal)", source, StringComparison.Ordinal);
        Assert.Contains("signal: request.signal", source, StringComparison.Ordinal);

        // Cancel must not early-return on 'searching': that left a stalled lookup showing a
        // spinner with no way out short of a page reload.
        var cancelHandler = ExtractBetween(source, "const cancel = overlay.querySelector('#shzCaptureCancel');", "const fallbackBtn");
        Assert.DoesNotContain("state === 'searching'", cancelHandler, StringComparison.Ordinal);
        Assert.Contains("activeRecognitionController.abort();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveCapture_RejectsUnusableSamplesBeforeSpendingARoundTrip()
    {
        var source = ReadWebFile("wwwroot", "js", "shazam-listen.js");

        Assert.Contains("const describeUnusableSample", source, StringComparison.Ordinal);
        Assert.Contains("SILENCE_PEAK_THRESHOLD", source, StringComparison.Ordinal);
        Assert.Contains("CLIPPING_RATIO_THRESHOLD", source, StringComparison.Ordinal);

        // Both the speculative early attempt and the final upload are gated: a silent or
        // clipped sample can never fingerprint, and an early match drawn from one would
        // navigate away on the least trustworthy audio of the session.
        Assert.Contains("console.debug('Shazam early attempt skipped.', unusable);", source, StringComparison.Ordinal);
        Assert.Contains("console.debug('Shazam final attempt rejected before upload.', unusable);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MicRecognition_AnswersBeforeEnrichmentAndQueuesTheLookups()
    {
        var source = ReadWebFile("Controllers", "Api", "ShazamApiController.cs");

        Assert.Contains("_enrichmentQueue.TryEnqueue(new ShazamEnrichmentRequest(", source, StringComparison.Ordinal);
        Assert.Contains("BuildPendingMatchPayload(", source, StringComparison.Ordinal);

        // Discovery lookups each spawn a Python process; awaiting them inline is what kept
        // the capture overlay spinning after the match was already known.
        Assert.DoesNotContain("BuildMatchPayloadAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_discoveryService", source, StringComparison.Ordinal);

        // The recognizer wait must not park a request thread while holding a gate slot.
        Assert.Contains("await _recognitionService.RecognizeAudioOnlyAsync(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PendingMatchPayload_MarksEnrichmentPendingAndReportsWhenItWillNotRun()
    {
        var controllerType = typeof(ShazamRecognitionService).Assembly
            .GetType("DeezSpoTag.Web.Controllers.Api.ShazamRecognitionApiController")!;
        var method = controllerType.GetMethod("BuildPendingMatchPayload", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var queued = method!.Invoke(null, [BuildLogoMatchPayload(controllerType), true])!;
        Assert.True(ReadNested<bool>(queued, "enrichment", "pending"));
        Assert.Null(ReadProperty<string>(queued, "reason"));
        Assert.True(ReadProperty<bool>(queued, "matched"));

        var notQueued = method.Invoke(null, [BuildLogoMatchPayload(controllerType), false])!;
        Assert.False(ReadNested<bool>(notQueued, "enrichment", "pending"));
        Assert.Equal("enrichment_failed", ReadProperty<string>(notQueued, "reason"));
    }

    [Fact]
    public void EnrichmentWorker_PublishesLookupsIntoTheLogoResultCache()
    {
        var source = ReadWebFile("Services", "ShazamEnrichmentQueueService.cs");

        Assert.Contains("ShazamRecognitionApiController.BuildMatchPayload(", source, StringComparison.Ordinal);
        Assert.Contains("StoreResult(request.ClientRequestId, payload);", source, StringComparison.Ordinal);

        // A saturated queue has to fail the write so the caller can mark the payload final
        // rather than leaving the client polling for an enrichment that never arrives.
        Assert.Contains("FullMode = BoundedChannelFullMode.Wait", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ShazamResults_PollsForPendingEnrichmentAndRendersTheMatchFirst()
    {
        var source = ReadWebFile("Views", "Shazam", "Results.cshtml");

        Assert.Contains("const isEnrichmentPending", source, StringComparison.Ordinal);
        Assert.Contains("awaitEnrichedLogoResult", source, StringComparison.Ordinal);
        Assert.Contains("ENRICHMENT_POLL_ATTEMPTS", source, StringComparison.Ordinal);

        // The identification is the answer the user asked for; it must not wait on the
        // discovery sections.
        Assert.Contains("renderPayload(appliedPayload.match, [], []);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RecognizerRuntimeProbe_NeverRunsOnTheRequestPath()
    {
        var source = ReadWebFile("Services", "ShazamRecognitionService.cs");

        // Probing spawns processes and can escalate to a multi-minute pip bootstrap. It used
        // to run inline under a lock that blocked every concurrent Shazam request.
        Assert.DoesNotContain("GetRuntimeProbe()", source, StringComparison.Ordinal);
        Assert.Contains("public async Task RefreshRuntimeProbeAsync", source, StringComparison.Ordinal);
        Assert.Contains("public bool IsRuntimeProbeStale", source, StringComparison.Ordinal);

        var worker = ReadWebFile("Services", "ShazamRecognizerProbeHostedService.cs");
        Assert.Contains("RefreshRuntimeProbeAsync(stoppingToken)", worker, StringComparison.Ordinal);

        // pip fills a pipe buffer long before it finishes; both streams must be drained
        // before the wait or the child blocks writing and never exits.
        var bootstrap = ExtractBetween(
            source,
            "private async Task<string?> TryBootstrapShazamRuntimeAsync(",
            "private string GetRuntimeSetupScriptPath()");
        var readIndex = bootstrap.IndexOf("process.StandardOutput.ReadToEndAsync", StringComparison.Ordinal);
        var waitIndex = bootstrap.IndexOf("process.WaitForExitAsync", StringComparison.Ordinal);
        Assert.True(readIndex >= 0 && waitIndex > readIndex, "Bootstrap must drain stdout before waiting for exit.");
    }

    [Fact]
    public void RecognizerScriptDeadline_FitsInsideTheProcessTimeout()
    {
        var serviceType = typeof(ShazamRecognitionService);
        var processTimeout = (TimeSpan)serviceType
            .GetField("RecognizerProcessTimeout", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;
        var scriptTimeoutSeconds = (int)serviceType
            .GetField("RecognizerScriptTimeoutSeconds", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetRawConstantValue()!;

        // The script's own deadline has to win the race, otherwise every slow lookup is
        // killed by the host and reported as "recognizer failed" instead of a no-match.
        Assert.True(
            scriptTimeoutSeconds < processTimeout.TotalSeconds,
            $"Script timeout {scriptTimeoutSeconds}s must be below the {processTimeout.TotalSeconds}s process timeout.");

        var script = ReadWebFile("Tools", "shazam_port", "recognize.py");
        Assert.Contains("--max-retries", script, StringComparison.Ordinal);

        var callerSource = ReadWebFile("Services", "ShazamRecognitionService.cs");
        Assert.Contains("startInfo.ArgumentList.Add(\"--timeout\");", callerSource, StringComparison.Ordinal);
        Assert.Contains("startInfo.ArgumentList.Add(\"--max-retries\");", callerSource, StringComparison.Ordinal);
    }

    private static object BuildLogoMatchPayload(Type controllerType)
    {
        var payloadType = controllerType.GetNestedType("ShazamLogoMatchPayload", BindingFlags.NonPublic)!;
        var recognition = new ShazamRecognitionInfo
        {
            TrackId = "match-1",
            Title = "Matched Song",
            Artist = "Matched Artist"
        };

        return Activator.CreateInstance(
            payloadType,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            null,
            [
                recognition,
                "Matched Song Matched Artist",
                null,
                Array.Empty<ShazamTrackCard>(),
                Array.Empty<ShazamTrackCard>(),
                "logo",
                "final",
                "logo-1",
                "request-1"
            ],
            null)!;
    }

    private static T ReadProperty<T>(object source, string propertyName)
    {
        var value = source.GetType().GetProperty(propertyName)?.GetValue(source);
        return value is T typed ? typed : default!;
    }

    private static T ReadNested<T>(object source, string objectProperty, string propertyName)
    {
        var nested = source.GetType().GetProperty(objectProperty)!.GetValue(source)!;
        return ReadProperty<T>(nested, propertyName);
    }

    private static string ExtractBetween(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Marker not found: {startMarker}");

        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }

    private static string ReadWebFile(params string[] segments)
    {
        var path = Path.Join(ResolveRepoRoot(), "DeezSpoTag.Web");
        foreach (var segment in segments)
        {
            path = Path.Join(path, segment);
        }

        Assert.True(File.Exists(path), $"Missing file: {path}");
        return File.ReadAllText(path);
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Join(current.FullName, "Directory.Build.props")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Unable to locate repository root from test output path.");
    }
}
