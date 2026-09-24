using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using DeezSpoTag.Web.Services.Vibe;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class VibeAnalyzerWorkerTest : IAsyncLifetime
{
    private readonly string _tempDirectory = Directory.CreateTempSubdirectory("deez-vibe-worker-").FullName;

    [Fact]
    public async Task AnalyzeAsync_DrainsLargeStderrAndReusesOneProcess()
    {
        var startsPath = Path.Join(_tempDirectory, "starts.txt");
        await using var worker = CreateWorker(startsPath, TimeSpan.FromSeconds(5));

        var first = await worker.AnalyzeAsync("large-stderr", CancellationToken.None);
        var second = await worker.AnalyzeAsync("success", CancellationToken.None);

        Assert.True(first.Succeeded, first.FailureReason);
        Assert.True(second.Succeeded, second.FailureReason);
        using var payload = JsonDocument.Parse(Assert.IsType<string>(first.PayloadJson));
        Assert.Equal("enhanced", payload.RootElement.GetProperty("AnalysisMode").GetString());
        Assert.Equal(1, File.ReadAllLines(startsPath).Length);
        Assert.Equal(1, worker.GetSnapshot().StartCount);
    }

    [Fact]
    public async Task AnalyzeAsync_RestartsAfterMalformedResponse()
    {
        var startsPath = Path.Join(_tempDirectory, "malformed-starts.txt");
        await using var worker = CreateWorker(startsPath, TimeSpan.FromSeconds(5));

        var malformed = await worker.AnalyzeAsync("malformed", CancellationToken.None);
        var recovered = await worker.AnalyzeAsync("success", CancellationToken.None);

        Assert.False(malformed.Succeeded);
        Assert.Contains("invalid JSON", malformed.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.True(recovered.Succeeded, recovered.FailureReason);
        Assert.Equal(2, File.ReadAllLines(startsPath).Length);
    }

    [Fact]
    public async Task AnalyzeAsync_RestartsWhenRequiredOkPropertyIsMissing()
    {
        var startsPath = Path.Join(_tempDirectory, "missing-ok-starts.txt");
        await using var worker = CreateWorker(startsPath, TimeSpan.FromSeconds(5));

        var malformed = await worker.AnalyzeAsync("missing-ok", CancellationToken.None);
        var recovered = await worker.AnalyzeAsync("success", CancellationToken.None);

        Assert.False(malformed.Succeeded);
        Assert.Contains("malformed", malformed.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.True(recovered.Succeeded, recovered.FailureReason);
        Assert.Equal(2, File.ReadAllLines(startsPath).Length);
    }

    [Fact]
    public async Task AnalyzeAsync_RecordsSanitizedBoundedStartupFailure()
    {
        var unsafeReason = "missing-worker\r\nSECRET=" + new string('x', 400);
        await using var worker = new VibeAnalyzerWorker(
            () => new ProcessStartInfo(unsafeReason),
            TimeSpan.FromSeconds(1));

        var result = await worker.AnalyzeAsync("unused", CancellationToken.None);
        var snapshot = worker.GetSnapshot();

        Assert.False(result.Succeeded);
        Assert.Equal(VibeAnalyzerWorkerStates.Failed, snapshot.State);
        Assert.NotNull(snapshot.LastFailureAtUtc);
        Assert.NotNull(snapshot.LastFailureReason);
        Assert.DoesNotContain('\r', snapshot.LastFailureReason);
        Assert.DoesNotContain('\n', snapshot.LastFailureReason);
        Assert.True(snapshot.LastFailureReason.Length <= 256);
    }

    [Fact]
    public async Task AnalyzeAsync_RestartsAfterProcessExit()
    {
        var startsPath = Path.Join(_tempDirectory, "crash-starts.txt");
        await using var worker = CreateWorker(startsPath, TimeSpan.FromSeconds(5));

        var crashed = await worker.AnalyzeAsync("crash", CancellationToken.None);
        var recovered = await worker.AnalyzeAsync("success", CancellationToken.None);

        Assert.False(crashed.Succeeded);
        var crashReason = Assert.IsType<string>(crashed.FailureReason);
        Assert.True(
            crashReason.Contains("exited", StringComparison.OrdinalIgnoreCase)
            || crashReason.Contains("closed its output stream", StringComparison.OrdinalIgnoreCase),
            $"Unexpected worker crash reason: {crashReason}");
        Assert.True(recovered.Succeeded, recovered.FailureReason);
        Assert.Equal(2, File.ReadAllLines(startsPath).Length);
    }

    [Fact]
    public async Task AnalyzeAsync_TimeoutStopsWorkerAndPreventsLateResponseReuse()
    {
        var startsPath = Path.Join(_tempDirectory, "timeout-starts.txt");
        await using var worker = CreateWorker(startsPath, TimeSpan.FromMilliseconds(250));

        var timedOut = await worker.AnalyzeAsync("hang", CancellationToken.None);
        var recovered = await worker.AnalyzeAsync("success", CancellationToken.None);

        Assert.False(timedOut.Succeeded);
        Assert.Contains("timed out", timedOut.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.True(recovered.Succeeded, recovered.FailureReason);
        Assert.Equal(2, File.ReadAllLines(startsPath).Length);
    }

    [Fact]
    public async Task AnalyzeAsync_CallerCancellationStopsWorker()
    {
        var startsPath = Path.Join(_tempDirectory, "cancel-starts.txt");
        await using var worker = CreateWorker(startsPath, TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.AnalyzeAsync("hang", cancellation.Token));
        var recovered = await worker.AnalyzeAsync("success", CancellationToken.None);

        Assert.True(recovered.Succeeded, recovered.FailureReason);
        Assert.Equal(2, File.ReadAllLines(startsPath).Length);
    }

    private VibeAnalyzerWorker CreateWorker(string startsPath, TimeSpan timeout)
    {
        var scriptPath = Path.Join(_tempDirectory, "fake_worker.py");
        if (!File.Exists(scriptPath))
        {
            File.WriteAllText(scriptPath, FakeWorkerScript);
        }

        return new VibeAnalyzerWorker(
            () =>
            {
                var startInfo = new ProcessStartInfo("python3")
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add(scriptPath);
                startInfo.ArgumentList.Add(startsPath);
                return startInfo;
            },
            timeout);
    }

    private const string FakeWorkerScript = """
import json
import sys
import time

with open(sys.argv[1], "a", encoding="utf-8") as starts:
    starts.write("start\n")

for line in sys.stdin:
    request = json.loads(line)
    path = request["filePath"]
    if path == "large-stderr":
        sys.stderr.write("x" * 131072)
        sys.stderr.flush()
    elif path == "malformed":
        print("not-json", flush=True)
        continue
    elif path == "missing-ok":
        print(json.dumps({"requestId": request["requestId"], "message": "missing protocol property"}), flush=True)
        continue
    elif path == "crash":
        sys.exit(23)
    elif path == "hang":
        time.sleep(30)
    print(json.dumps({"requestId": request["requestId"], "ok": True, "AnalysisMode": "enhanced"}), flush=True)
""";

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        return Task.CompletedTask;
    }
}
