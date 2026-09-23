using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DeezSpoTag.Web.Services.Vibe;

internal sealed class VibeAnalyzerWorker : IAsyncDisposable
{
    private const int MaxDiagnosticCharacters = 8192;
    private readonly Func<ProcessStartInfo> _startInfoFactory;
    private readonly TimeSpan _requestTimeout;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly object _stateLock = new();
    private readonly StringBuilder _diagnostics = new();
    private Process? _process;
    private Task? _stderrDrainTask;
    private int _startCount;
    private string _state = VibeAnalyzerWorkerStates.Stopped;
    private string? _lastFailureReason;
    private DateTimeOffset? _lastFailureAtUtc;
    private bool _disposed;

    internal VibeAnalyzerWorker(Func<ProcessStartInfo> startInfoFactory, TimeSpan requestTimeout)
    {
        _startInfoFactory = startInfoFactory;
        _requestTimeout = requestTimeout;
    }

    internal VibeAnalyzerWorkerSnapshot GetSnapshot()
    {
        lock (_stateLock)
        {
            return new VibeAnalyzerWorkerSnapshot(_state, _lastFailureReason, _lastFailureAtUtc, _startCount);
        }
    }

    internal async Task<VibeAnalyzerWorkerResult> AnalyzeAsync(string filePath, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                var process = await EnsureStartedAsync().ConfigureAwait(false);
                var requestId = Guid.NewGuid().ToString("N");
                var request = JsonSerializer.Serialize(new { requestId, filePath });
                await process.StandardInput.WriteLineAsync(request.AsMemory(), cancellationToken).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);

                using var timeout = new CancellationTokenSource(_requestTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                string? responseLine;
                try
                {
                    responseLine = await process.StandardOutput.ReadLineAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    var reason = $"Vibe analyzer worker timed out after {(int)_requestTimeout.TotalSeconds}s.";
                    await FailAndStopAsync(reason).ConfigureAwait(false);
                    return VibeAnalyzerWorkerResult.Failure(reason);
                }
                catch (OperationCanceledException)
                {
                    await StopProcessAsync().ConfigureAwait(false);
                    throw;
                }

                if (responseLine is null)
                {
                    var reason = process.HasExited
                        ? $"Vibe analyzer worker exited with code {process.ExitCode}. {GetDiagnostics()}".Trim()
                        : "Vibe analyzer worker closed its output stream.";
                    await FailAndStopAsync(reason).ConfigureAwait(false);
                    return VibeAnalyzerWorkerResult.Failure(reason);
                }

                JsonDocument response;
                try
                {
                    response = JsonDocument.Parse(responseLine);
                }
                catch (JsonException)
                {
                    var reason = "Vibe analyzer worker returned invalid JSON.";
                    await FailAndStopAsync(reason).ConfigureAwait(false);
                    return VibeAnalyzerWorkerResult.Failure(reason);
                }

                using (response)
                {
                    var root = response.RootElement;
                    if (!root.TryGetProperty("requestId", out var responseId)
                        || !string.Equals(responseId.GetString(), requestId, StringComparison.Ordinal))
                    {
                        var reason = "Vibe analyzer worker returned a mismatched request ID.";
                        await FailAndStopAsync(reason).ConfigureAwait(false);
                        return VibeAnalyzerWorkerResult.Failure(reason);
                    }

                    if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                    {
                        var reason = "Vibe analyzer worker returned a malformed response without a valid ok property.";
                        await FailAndStopAsync(reason).ConfigureAwait(false);
                        return VibeAnalyzerWorkerResult.Failure(reason);
                    }

                    if (!ok.GetBoolean())
                    {
                        var reason = root.TryGetProperty("message", out var message)
                            ? message.GetString()
                            : "Vibe analyzer worker reported a failure.";
                        reason = string.IsNullOrWhiteSpace(reason) ? "Vibe analyzer worker reported a failure." : reason;
                        RecordFailure(reason);
                        return VibeAnalyzerWorkerResult.Failure(reason);
                    }
                }

                SetState(VibeAnalyzerWorkerStates.Ready, null);
                return VibeAnalyzerWorkerResult.Success(responseLine);
            }
            catch (OperationCanceledException)
            {
                await StopProcessAsync().ConfigureAwait(false);
                throw;
            }
            catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
            {
                var reason = $"Vibe analyzer worker communication failed: {ex.Message}";
                await FailAndStopAsync(reason).ConfigureAwait(false);
                return VibeAnalyzerWorkerResult.Failure(reason);
            }
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private async Task<Process> EnsureStartedAsync()
    {
        if (_process is { HasExited: false })
        {
            return _process;
        }

        await StopProcessAsync().ConfigureAwait(false);
        var startInfo = _startInfoFactory();
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start vibe analyzer worker.");
        lock (_stateLock)
        {
            _diagnostics.Clear();
            _process = process;
            _startCount++;
            _state = VibeAnalyzerWorkerStates.Starting;
        }
        _stderrDrainTask = DrainStderrAsync(process);
        return process;
    }

    private async Task DrainStderrAsync(Process process)
    {
        var buffer = new char[2048];
        try
        {
            while (true)
            {
                var read = await process.StandardError.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                lock (_stateLock)
                {
                    _diagnostics.Append(buffer, 0, read);
                    if (_diagnostics.Length > MaxDiagnosticCharacters)
                    {
                        _diagnostics.Remove(0, _diagnostics.Length - MaxDiagnosticCharacters);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Process shutdown closes redirected streams.
        }
    }

    private string GetDiagnostics()
    {
        lock (_stateLock)
        {
            return _diagnostics.ToString().Trim();
        }
    }

    private async Task FailAndStopAsync(string reason)
    {
        RecordFailure(reason);
        await StopProcessAsync(preserveFailureState: true).ConfigureAwait(false);
    }

    private void RecordFailure(string reason)
    {
        var safeReason = DeezSpoTag.Core.Security.LogSanitizer.OneLine(reason, 253);
        lock (_stateLock)
        {
            _state = VibeAnalyzerWorkerStates.Failed;
            _lastFailureReason = safeReason;
            _lastFailureAtUtc = DateTimeOffset.UtcNow;
        }
    }

    private void SetState(string state, string? failureReason)
    {
        lock (_stateLock)
        {
            _state = state;
            if (failureReason is not null)
            {
                _lastFailureReason = failureReason;
                _lastFailureAtUtc = DateTimeOffset.UtcNow;
            }
        }
    }

    private async ValueTask StopProcessAsync(bool preserveFailureState = false)
    {
        Process? process;
        Task? stderrDrain;
        lock (_stateLock)
        {
            process = _process;
            stderrDrain = _stderrDrainTask;
            _process = null;
            _stderrDrainTask = null;
            if (!preserveFailureState)
            {
                _state = VibeAnalyzerWorkerStates.Stopped;
            }
        }

        if (process is null)
        {
            return;
        }

        try
        {
            process.StandardInput.Close();
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process exited between state checks.
        }

        if (stderrDrain is not null)
        {
            try
            {
                await stderrDrain.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // Stream teardown is bounded during worker replacement.
            }
        }
        process.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        await _requestLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopProcessAsync().ConfigureAwait(false);
        }
        finally
        {
            _requestLock.Release();
            _requestLock.Dispose();
        }
    }
}

internal static class VibeAnalyzerWorkerStates
{
    internal const string Starting = "starting";
    internal const string Ready = "ready";
    internal const string Failed = "failed";
    internal const string Stopped = "stopped";
}

internal sealed record VibeAnalyzerWorkerResult(bool Succeeded, string? PayloadJson, string? FailureReason)
{
    internal static VibeAnalyzerWorkerResult Success(string payloadJson) => new(true, payloadJson, null);
    internal static VibeAnalyzerWorkerResult Failure(string reason) => new(false, null, reason);
}

public sealed record VibeAnalyzerWorkerSnapshot(
    string State,
    string? LastFailureReason,
    DateTimeOffset? LastFailureAtUtc,
    int StartCount);
