using System.Text.Json;

namespace DeezSpoTag.Web.Services;

public sealed class ArtistMetadataAutomationCoordinator : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(30);
    private const int CheckpointSaveEvery = 25;
    private readonly ArtistMetadataCacheRefreshService _cacheRefresh;
    private readonly ArtistMetadataUpdaterService _targetUpdate;
    private readonly UserPreferencesStore _preferences;
    private readonly ILogger<ArtistMetadataAutomationCoordinator> _logger;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _statusLock = new();
    private readonly string _statePath;
    private readonly string _legacyStatePath;
    private ArtistMetadataAutomationStatus _status = ArtistMetadataAutomationStatus.Idle();
    private Task? _activeOperation;
    private CancellationTokenSource? _activeCts;
    private CancellationToken _shutdownToken = CancellationToken.None;
    private ArtistMetadataActiveRun? _checkpoint;
    private int _sinceCheckpointSave;

    public ArtistMetadataAutomationCoordinator(
        ArtistMetadataCacheRefreshService cacheRefresh,
        ArtistMetadataUpdaterService targetUpdate,
        UserPreferencesStore preferences,
        IWebHostEnvironment environment,
        ILogger<ArtistMetadataAutomationCoordinator> logger)
    {
        _cacheRefresh = cacheRefresh;
        _targetUpdate = targetUpdate;
        _preferences = preferences;
        _logger = logger;
        _statePath = Path.Join(AppDataPaths.GetDataRoot(environment), "library-artist-images", "metadata-automation-state.json");
        _legacyStatePath = Path.Join(AppDataPaths.GetDataRoot(environment), "library-artist-images", "spotify", "metadata-updater-state.json");
    }

    public ArtistMetadataAutomationStatus GetStatus()
    {
        lock (_statusLock)
        {
            return _status with { TargetUpdate = _targetUpdate.GetStatus() };
        }
    }

    public bool Cancel()
    {
        var cts = _activeCts;
        if (cts is null || cts.IsCancellationRequested)
        {
            return false;
        }

        try
        {
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public Task<bool> EnqueueCacheRefreshAsync(ArtistMetadataCacheRefreshRequest request, CancellationToken cancellationToken)
        => EnqueueAsync(
            "cache-refresh",
            async token => IsCacheRefreshComplete(await RunCacheRefreshAsync(request, automatic: false, token)),
            cancellationToken,
            resuming: null,
            cacheRequest: request);

    public Task<bool> EnqueueTargetUpdateAsync(MetadataUpdaterRunRequest request, CancellationToken cancellationToken)
        => EnqueueAsync(
            "target-update",
            token => RunTargetUpdateAsync(request, automatic: false, token),
            cancellationToken,
            resuming: null,
            targetRequest: request);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _shutdownToken = stoppingToken;
        try
        {
            await ResumeInterruptedRunAsync(stoppingToken);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "Artist metadata resume was cancelled.");
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(ex, "Artist metadata resume failed.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunScheduledOperationsAsync(stoppingToken);
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogWarning(ex, "Artist metadata schedule evaluation was cancelled unexpectedly.");
                if (!await DelayOrStopAsync(stoppingToken))
                {
                    break;
                }
            }
            catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
            {
                _logger.LogWarning(ex, "Artist metadata schedule evaluation failed.");
                if (!await DelayOrStopAsync(stoppingToken))
                {
                    break;
                }
            }
        }
    }

    private static async Task<bool> DelayOrStopAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(PollInterval, stoppingToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task ResumeInterruptedRunAsync(CancellationToken cancellationToken)
    {
        var state = await LoadStateAsync(cancellationToken);
        var run = state.ActiveRun;
        if (run is null)
        {
            return;
        }

        _logger.LogInformation(
            "Resuming interrupted artist metadata {Operation} ({Completed} artist(s) already done).",
            run.Operation,
            run.CompletedArtistIds.Count);

        var preferences = await _preferences.LoadAsync();
        if (string.Equals(run.Operation, "cache-refresh", StringComparison.Ordinal))
        {
            var cacheRequest = run.CacheRequest ?? BuildCacheRequest(preferences);
            await EnqueueAsync(
                "cache-refresh",
                async token => IsCacheRefreshComplete(await RunCacheRefreshAsync(cacheRequest, run.Automatic, token)),
                cancellationToken,
                resuming: run);
            return;
        }

        var targetRequest = run.TargetRequest ?? BuildTargetRequest(preferences);
        await EnqueueAsync(
            "target-update",
            token => RunTargetUpdateAsync(targetRequest, run.Automatic, token),
            cancellationToken,
            resuming: run);
    }

    private async Task RunScheduledOperationsAsync(CancellationToken cancellationToken)
    {
        if (_activeOperation is { IsCompleted: false })
        {
            return;
        }

        var preferences = await _preferences.LoadAsync();
        var state = await LoadStateAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;

        // A run left incomplete stays resumable: continue it on the next tick rather than waiting
        // for a restart or for the interval to come round again.
        if (state.ActiveRun is not null)
        {
            await ResumeInterruptedRunAsync(cancellationToken);
            return;
        }

        var cacheDue = IsDue(state.LastCacheRefreshUtc, preferences.MetadataCacheRefreshIntervalDays, now);
        var updateDue = IsDue(state.LastTargetUpdateUtc, preferences.MetadataTargetUpdateIntervalDays, now);
        UpdateScheduleStatus(state, preferences, now);
        if (!cacheDue && !updateDue)
        {
            return;
        }

        if (cacheDue)
        {
            var cacheRequest = BuildCacheRequest(preferences);
            if (await EnqueueAsync(
                    "cache-refresh",
                    async token => IsCacheRefreshComplete(await RunCacheRefreshAsync(cacheRequest, automatic: true, token)),
                    cancellationToken,
                    cacheRequest: cacheRequest,
                    automatic: true))
            {
                await WaitForActiveOperationAsync();
            }
        }

        if (updateDue)
        {
            var targetRequest = BuildTargetRequest(preferences);
            if (await EnqueueAsync(
                    "target-update",
                    token => RunTargetUpdateAsync(targetRequest, automatic: true, token),
                    cancellationToken,
                    targetRequest: targetRequest,
                    automatic: true))
            {
                await WaitForActiveOperationAsync();
            }
        }

        UpdateScheduleStatus(await LoadStateAsync(cancellationToken), preferences, DateTimeOffset.UtcNow);
    }

    private async Task WaitForActiveOperationAsync()
    {
        var active = _activeOperation;
        if (active is null)
        {
            return;
        }

        try
        {
            await active;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Artist metadata operation ended with an error.");
        }
    }

    private async Task<bool> EnqueueAsync(
        string operation,
        Func<CancellationToken, Task<bool>> run,
        CancellationToken cancellationToken,
        ArtistMetadataActiveRun? resuming = null,
        ArtistMetadataCacheRefreshRequest? cacheRequest = null,
        MetadataUpdaterRunRequest? targetRequest = null,
        bool automatic = false)
    {
        if (!await _operationGate.WaitAsync(0, cancellationToken))
        {
            return false;
        }

        try
        {
            if (_activeOperation is { IsCompleted: false })
            {
                _operationGate.Release();
                return false;
            }

            lock (_statusLock)
            {
                _status = _status with { ActiveOperation = operation };
            }
            _activeCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken);
            _checkpoint = resuming ?? new ArtistMetadataActiveRun
            {
                Operation = operation,
                Automatic = automatic,
                StartedAtUtc = DateTimeOffset.UtcNow,
                CacheRequest = cacheRequest,
                TargetRequest = targetRequest
            };
            _activeOperation = RunManualOperationAsync(operation, run, _activeCts);
            return true;
        }
        catch
        {
            _operationGate.Release();
            throw;
        }
    }

    /// <summary>
    /// A refresh only counts as complete when every artist was processed. Treating a partially
    /// failed sweep as complete stamps LastCacheRefreshUtc and locks the remaining artists out
    /// until the next interval, so failures must leave the run resumable instead.
    /// </summary>
    private bool IsCacheRefreshComplete(ArtistMetadataCacheRefreshResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            return false;
        }

        if (result.Failed > 0)
        {
            _logger.LogWarning(
                "Artist metadata cache refresh finished with {Failed} of {Total} artist(s) failing; leaving the run resumable.",
                result.Failed,
                result.Total);
            return false;
        }

        return true;
    }

    private async Task RunManualOperationAsync(
        string operation,
        Func<CancellationToken, Task<bool>> run,
        CancellationTokenSource cts)
    {
        try
        {
            await PersistCheckpointAsync(force: true);
            var completed = await run(cts.Token);
            if (!completed)
            {
                return;
            }

            var state = await LoadStateAsync(CancellationToken.None);
            if (operation == "cache-refresh")
            {
                state.LastCacheRefreshUtc = DateTimeOffset.UtcNow;
            }
            else
            {
                state.LastTargetUpdateUtc = DateTimeOffset.UtcNow;
            }
            state.ActiveRun = null;
            await SaveStateAsync(state, CancellationToken.None);
            var preferences = await _preferences.LoadAsync();
            UpdateScheduleStatus(state, preferences, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Artist metadata {Operation} was cancelled.", operation);
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(ex, "Artist metadata {Operation} failed.", operation);
            RecordOperationFailure(operation, ex.Message);
        }
        finally
        {
            _checkpoint = null;
            _activeCts = null;
            cts.Dispose();
            _operationGate.Release();
            lock (_statusLock)
            {
                _status = _status with { ActiveOperation = null };
            }
        }
    }

    private void RecordOperationFailure(string operation, string message)
    {
        lock (_statusLock)
        {
            _status = operation == "cache-refresh"
                ? _status with
                {
                    CacheRefresh = _status.CacheRefresh with
                    {
                        Running = false,
                        Phase = "Cache refresh failed",
                        Message = message,
                        CurrentArtist = null,
                        CompletedAtUtc = DateTimeOffset.UtcNow
                    }
                }
                : _status;
        }
    }

    private async Task PersistCheckpointAsync(bool force)
    {
        var checkpoint = _checkpoint;
        if (checkpoint is null)
        {
            return;
        }

        if (!force && ++_sinceCheckpointSave < CheckpointSaveEvery)
        {
            return;
        }

        _sinceCheckpointSave = 0;
        try
        {
            var state = await LoadStateAsync(CancellationToken.None);
            state.ActiveRun = checkpoint;
            await SaveStateAsync(state, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Artist metadata checkpoint save failed.");
        }
    }

    private async Task<ArtistMetadataCacheRefreshResult> RunCacheRefreshAsync(
        ArtistMetadataCacheRefreshRequest request,
        bool automatic,
        CancellationToken cancellationToken)
    {
        UpdateCacheStatus(new ArtistMetadataCacheStatus(true, automatic, "Refreshing artist metadata cache", null, 0, 0, null, DateTimeOffset.UtcNow, null));
        var progress = new Progress<ArtistMetadataOperationProgress>(value =>
        {
            UpdateCacheProgress(value);
            NoteArtistCompleted(value.CompletedArtistId);
        });
        var result = await _cacheRefresh.RefreshAsync(
            request,
            progress,
            CheckpointCompletedIds(),
            cancellationToken);
        UpdateCacheStatus(GetStatus().CacheRefresh with
        {
            Running = false,
            Phase = result.Error is null ? "Cache refresh completed" : "Cache refresh failed",
            Message = result.Error ?? $"{result.Succeeded} succeeded, {result.Failed} failed.",
            ProcessedArtists = result.Total,
            TotalArtists = result.Total,
            SuccessfulArtists = result.Succeeded,
            FailedArtists = result.Failed,
            CurrentArtist = null,
            CompletedAtUtc = DateTimeOffset.UtcNow
        });
        return result;
    }

    private async Task<bool> RunTargetUpdateAsync(
        MetadataUpdaterRunRequest request,
        bool automatic,
        CancellationToken cancellationToken)
    {
        lock (_statusLock)
        {
            _status = _status with { ActiveOperation = "target-update" };
        }
        var progress = new Progress<ArtistMetadataOperationProgress>(value => NoteArtistCompleted(value.CompletedArtistId));
        return await _targetUpdate.RunAndWaitAsync(
            request,
            automatic,
            progress,
            CheckpointCompletedIds(),
            cancellationToken);
    }

    private void UpdateCacheProgress(ArtistMetadataOperationProgress value)
    {
        lock (_statusLock)
        {
            if (!_status.CacheRefresh.Running)
            {
                return;
            }

            _status = _status with
            {
                CacheRefresh = _status.CacheRefresh with
                {
                    ProcessedArtists = value.Processed,
                    TotalArtists = value.Total,
                    CurrentArtist = value.CurrentArtist,
                    SuccessfulArtists = value.Succeeded,
                    FailedArtists = value.Failed
                }
            };
        }
    }

    private IReadOnlySet<long>? CheckpointCompletedIds()
    {
        var checkpoint = _checkpoint;
        return checkpoint is { CompletedArtistIds.Count: > 0 }
            ? checkpoint.CompletedArtistIds.ToHashSet()
            : null;
    }

    private void NoteArtistCompleted(long? artistId)
    {
        if (artistId is not > 0 || _checkpoint is not { } checkpoint)
        {
            return;
        }

        checkpoint.CompletedArtistIds.Add(artistId.Value);
        _ = PersistCheckpointAsync(force: false);
    }

    private void UpdateCacheStatus(ArtistMetadataCacheStatus status)
    {
        lock (_statusLock)
        {
            _status = _status with { CacheRefresh = status, ActiveOperation = status.Running ? "cache-refresh" : _status.ActiveOperation };
        }
    }

    private void UpdateScheduleStatus(ArtistMetadataAutomationState state, UserPreferencesDto preferences, DateTimeOffset now)
    {
        lock (_statusLock)
        {
            _status = _status with
            {
                LastCacheRefreshUtc = state.LastCacheRefreshUtc,
                LastTargetUpdateUtc = state.LastTargetUpdateUtc,
                NextCacheRefreshUtc = NextDue(state.LastCacheRefreshUtc, preferences.MetadataCacheRefreshIntervalDays, now),
                NextTargetUpdateUtc = NextDue(state.LastTargetUpdateUtc, preferences.MetadataTargetUpdateIntervalDays, now)
            };
        }
    }

    private static ArtistMetadataCacheRefreshRequest BuildCacheRequest(UserPreferencesDto preferences)
        => new(
            null,
            ParseFolderId(preferences.MetadataUpdaterFolderId),
            preferences.MetadataUpdaterSource,
            preferences.MetadataUpdaterIncludePopularSongs);

    private static MetadataUpdaterRunRequest BuildTargetRequest(UserPreferencesDto preferences)
        => new()
        {
            Source = preferences.MetadataUpdaterSource,
            Targets = preferences.MetadataUpdaterTargets,
            FolderId = ParseFolderId(preferences.MetadataUpdaterFolderId),
            IncludeAvatar = preferences.MetadataUpdaterIncludeAvatar,
            IncludeBackground = preferences.MetadataUpdaterIncludeBackground,
            IncludeBio = preferences.MetadataUpdaterIncludeBio,
            IncludePopularSongs = preferences.MetadataUpdaterIncludePopularSongs,
            MissingArtistArtworkOnly = preferences.MetadataUpdaterMissingArtistArtworkOnly,
            OcrTextArtBlockingEnabled = preferences.MetadataUpdaterOcrTextArtBlocking,
            IncludeAllArtists = true,
            Force = true
        };

    private static long? ParseFolderId(string? value) => long.TryParse(value, out var id) && id > 0 ? id : null;
    private static bool IsDue(DateTimeOffset? lastRun, int intervalDays, DateTimeOffset now)
        => intervalDays > 0 && (!lastRun.HasValue || now - lastRun.Value >= TimeSpan.FromDays(intervalDays));
    private static DateTimeOffset? NextDue(DateTimeOffset? lastRun, int intervalDays, DateTimeOffset now)
        => intervalDays <= 0 ? null : lastRun?.AddDays(intervalDays) ?? now;

    private async Task<ArtistMetadataAutomationState> LoadStateAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_statePath))
        {
            return await MigrateLegacyStateAsync(cancellationToken);
        }
        await using var stream = File.OpenRead(_statePath);
        return await JsonSerializer.DeserializeAsync<ArtistMetadataAutomationState>(stream, cancellationToken: cancellationToken)
            ?? new ArtistMetadataAutomationState();
    }

    private async Task<ArtistMetadataAutomationState> MigrateLegacyStateAsync(CancellationToken cancellationToken)
    {
        var state = new ArtistMetadataAutomationState();
        if (!File.Exists(_legacyStatePath))
        {
            return state;
        }

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(_legacyStatePath, cancellationToken));
        if (document.RootElement.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
        {
            DateTimeOffset? latest = null;
            foreach (var artist in artists.EnumerateArray())
            {
                foreach (var property in new[] { "lastPushedAtUtc", "updatedAtUtc" })
                {
                    if (artist.TryGetProperty(property, out var value)
                        && value.ValueKind == JsonValueKind.String
                        && DateTimeOffset.TryParse(value.GetString(), out var timestamp)
                        && (!latest.HasValue || timestamp > latest.Value))
                    {
                        latest = timestamp;
                    }
                }
            }
            state.LastCacheRefreshUtc = latest;
            state.LastTargetUpdateUtc = latest;
        }

        await SaveStateAsync(state, cancellationToken);
        return state;
    }

    private async Task SaveStateAsync(ArtistMetadataAutomationState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        var temporary = _statePath + ".tmp";
        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, state, cancellationToken: cancellationToken);
        }
        File.Move(temporary, _statePath, true);
    }
}

public sealed class ArtistMetadataAutomationState
{
    public int Version { get; set; } = 3;
    public DateTimeOffset? LastCacheRefreshUtc { get; set; }
    public DateTimeOffset? LastTargetUpdateUtc { get; set; }
    public ArtistMetadataActiveRun? ActiveRun { get; set; }
}

public sealed class ArtistMetadataActiveRun
{
    public string Operation { get; set; } = string.Empty;
    public bool Automatic { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public ArtistMetadataCacheRefreshRequest? CacheRequest { get; set; }
    public MetadataUpdaterRunRequest? TargetRequest { get; set; }
    public List<long> CompletedArtistIds { get; set; } = new();
}

public sealed record ArtistMetadataAutomationStatus(
    string? ActiveOperation,
    ArtistMetadataCacheStatus CacheRefresh,
    MetadataUpdaterStatusSnapshot TargetUpdate,
    DateTimeOffset? LastCacheRefreshUtc,
    DateTimeOffset? LastTargetUpdateUtc,
    DateTimeOffset? NextCacheRefreshUtc,
    DateTimeOffset? NextTargetUpdateUtc)
{
    public static ArtistMetadataAutomationStatus Idle()
        => new(null, ArtistMetadataCacheStatus.Idle(), MetadataUpdaterStatusSnapshot.Idle(), null, null, null, null);
}

public sealed record ArtistMetadataCacheStatus(
    bool Running,
    bool Automatic,
    string Phase,
    string? Message,
    int ProcessedArtists,
    int TotalArtists,
    string? CurrentArtist,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    int SuccessfulArtists = 0,
    int FailedArtists = 0)
{
    public static ArtistMetadataCacheStatus Idle() => new(false, false, "Idle", null, 0, 0, null, null, null);
}
