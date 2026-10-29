using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DeezSpoTag.Web.Services;

/// <summary>One private Node process owns SAP authentication; only song metadata crosses its pipes.</summary>
public sealed class ShazamRelatedRuntimeService(IWebHostEnvironment environment, ILogger<ShazamRelatedRuntimeService> logger)
    : IHostedService, IAsyncDisposable
{
    private const int MaximumLineLength = 4 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _slots = new(32);
    private Session? _session;
    private bool _stopped;
    private sealed class Session(Process process)
    {
        internal Process Process { get; } = process;
        internal SemaphoreSlim Writer { get; } = new(1);
        internal ConcurrentDictionary<string, TaskCompletionSource<JsonDocument>> Pending { get; } = new();
        internal Task? Reader { get; set; }
        internal Task? ErrorReader { get; set; }
    }
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<JsonDocument> GetRelatedAsync(string shazamId, string language, string country, CancellationToken cancellationToken)
    {
        if (shazamId.Length is < 1 or > 20 || shazamId.Any(c => c is < '0' or > '9'))
            throw new ArgumentException("A numeric Shazam ID is required.", nameof(shazamId));
        cancellationToken.ThrowIfCancellationRequested();
        if (!await _slots.WaitAsync(0, cancellationToken)) throw new HttpRequestException("shazam_upstream_busy");
        Session? session = null;
        var requestId = Guid.NewGuid().ToString("N");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(32));
        try
        {
            session = GetSession();
            var completion = new TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Pending[requestId] = completion;
            using var registration = deadline.Token.Register(() => completion.TrySetCanceled(deadline.Token));
            await session.Writer.WaitAsync(deadline.Token);
            try
            {
                var line = JsonSerializer.Serialize(new { requestId, shazamId, language, country });
                await session.Process.StandardInput.WriteLineAsync(line.AsMemory(), deadline.Token);
                await session.Process.StandardInput.FlushAsync(deadline.Token);
            }
            finally { session.Writer.Release(); }
            var result = await completion.Task;
            if (!result.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            {
                result.Dispose();
                throw new HttpRequestException("shazam_upstream_unavailable");
            }
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new HttpRequestException("shazam_upstream_timeout"); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { throw new HttpRequestException("shazam_helper_unavailable"); }
        finally
        {
            if (session?.Pending.TryRemove(requestId, out var abandoned) == true && abandoned.Task.IsCompletedSuccessfully)
                abandoned.Task.Result.Dispose();
            _slots.Release();
        }
    }
    private Session GetSession()
    {
        lock (_gate)
        {
            if (_stopped) throw new HttpRequestException("shazam_helper_stopped");
            if (_session is { } existing && !existing.Process.HasExited) return existing;
            var script = Path.Join(environment.ContentRootPath, "Tools", "shazam_related", "runtime.mjs");
            if (!File.Exists(script)) script = Path.Join(AppContext.BaseDirectory, "Tools", "shazam_related", "runtime.mjs");
            if (!File.Exists(script)) throw new HttpRequestException("shazam_helper_missing");
            var start = new ProcessStartInfo("node") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            start.ArgumentList.Add(script);
            start.Environment["NODE_USE_ENV_PROXY"] = "1";
            var process = new Process { StartInfo = start };
            process.Start();
            var session = new Session(process);
            _session = session;
            session.Reader = ReadResultsAsync(session);
            session.ErrorReader = DrainErrorsAsync(session);
            return session;
        }
    }
    private async Task ReadResultsAsync(Session session)
    {
        var line = new StringBuilder();
        var buffer = new char[8192];
        try
        {
            int count;
            while ((count = await session.Process.StandardOutput.ReadAsync(buffer)) > 0)
            {
                for (var index = 0; index < count; index++)
                {
                    var character = buffer[index];
                    if (character != '\n')
                    {
                        if (line.Length >= MaximumLineLength) throw new InvalidDataException();
                        line.Append(character); continue;
                    }
                    var document = JsonDocument.Parse(line.ToString()); line.Clear();
                    if (document.RootElement.TryGetProperty("requestId", out var id) && id.ValueKind == JsonValueKind.String
                        && session.Pending.TryRemove(id.GetString()!, out var completion))
                    {
                        if (!completion.TrySetResult(document)) document.Dispose();
                    }
                    else document.Dispose();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or ObjectDisposedException)
        { logger.LogWarning("Shazam related helper transport stopped."); }
        finally
        {
            lock (_gate) { if (ReferenceEquals(_session, session)) _session = null; }
            foreach (var entry in session.Pending)
                if (session.Pending.TryRemove(entry.Key, out var completion)) completion.TrySetException(new HttpRequestException("shazam_helper_stopped"));
            try { if (!session.Process.HasExited) session.Process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            session.Process.Dispose();
        }
    }
    private static async Task DrainErrorsAsync(Session session)
    {
        // Never forward raw runtime/HTTP error text, even if a dependency prints it.
        var buffer = new char[1024];
        try { while (await session.Process.StandardError.ReadAsync(buffer) > 0) { } }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { }
    }
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Session? session;
        lock (_gate) { _stopped = true; session = _session; _session = null; }
        if (session is null) return;
        try { if (!session.Process.HasExited) session.Process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        if (session.Reader is not null) await session.Reader.WaitAsync(cancellationToken);
        if (session.ErrorReader is not null) await session.ErrorReader.WaitAsync(cancellationToken);
    }
    public async ValueTask DisposeAsync() { await StopAsync(CancellationToken.None); _slots.Dispose(); }
}
