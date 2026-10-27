using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeezSpoTag.Web.Services;

public sealed class ArtistMetadataAutomationCoordinator : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(30);

    /// <summary>How long shutdown waits for an in-flight run to unwind and flush its checkpoint.</summary>
    private static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Consecutive run-level failures tolerated before a run is abandoned. Without a cap, a run that
    /// throws during preparation is re-enqueued on every poll tick forever.
    /// </summary>
    private const int MaxConsecutiveRunFailures = 3;
    private readonly ArtistMetadataCacheRefreshService _cacheRefresh;
    private readonly LibraryArtistMetadataQueueService? _artistQueue;
    private readonly ArtistMetadataUpdaterService _targetUpdate;
    private readonly UserPreferencesStore _preferences;
    private readonly ILogger<ArtistMetadataAutomationCoordinator> _logger;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private readonly SemaphoreSlim _stateWriteGate = new(1, 1);
    private readonly object _statusLock = new();
    private readonly string _statePath;
    private readonly string _outcomeJournalPath;
    private readonly string _legacyStatePath;
    private ArtistMetadataAutomationStatus _status = ArtistMetadataAutomationStatus.Idle();
    private Task? _activeOperation;
    private CancellationTokenSource? _activeCts;
    private CancellationTokenSource? _userCancelCts;
    private CancellationToken _shutdownToken = CancellationToken.None;
    private volatile bool _shutdownRequested;
    private ArtistMetadataActiveRun? _checkpoint;
    private readonly object _checkpointLock = new();
    private ArtistMetadataOutcomeJournal? _outcomeJournal;
    private Task _journalAppend = Task.CompletedTask;
    private Exception? _journalFailure;
    private int _checkpointEpoch;

    public ArtistMetadataAutomationCoordinator(
        ArtistMetadataCacheRefreshService cacheRefresh,
        ArtistMetadataUpdaterService targetUpdate,
        UserPreferencesStore preferences,
        IWebHostEnvironment environment,
        ILogger<ArtistMetadataAutomationCoordinator> logger,
        LibraryArtistMetadataQueueService? artistQueue = null)
    {
        _artistQueue = artistQueue;
        _cacheRefresh = cacheRefresh;
        _targetUpdate = targetUpdate;
        _preferences = preferences;
        _logger = logger;
        _statePath = Path.Join(AppDataPaths.GetDataRoot(environment), "library-artist-images", "metadata-automation-state.json");
        _outcomeJournalPath = Path.Join(AppDataPaths.GetDataRoot(environment), "library-artist-images", "metadata-automation-outcomes.ndjson");
        _legacyStatePath = Path.Join(AppDataPaths.GetDataRoot(environment), "library-artist-images", "spotify", "metadata-updater-state.json");
    }

    public ArtistMetadataAutomationStatus GetStatus()
    {
        lock (_statusLock)
        {
            if (_status.RunState == "paused") return _status;
            var target = _targetUpdate.GetStatus();
            // Preparation clears the worker snapshot. Retain the same run's recovered progress
            // until the worker has rebuilt its counters from the journal.
            if (_status.RunOperation == "target-update" && _status.RunState != "idle"
                && target.TotalArtists == 0 && _status.TargetUpdate.TotalArtists > 0)
            {
                target = _status.TargetUpdate with
                {
                    Running = _status.RunState == "running",
                    Phase = target.Running ? target.Phase : "Preparing metadata update"
                };
            }
            else if (!target.Running && _status.RunState == "idle"
                && _status.TargetUpdate.CompletedAtUtc > (target.CompletedAtUtc ?? DateTimeOffset.MinValue))
            {
                target = _status.TargetUpdate;
            }
            if (_status.RunOperation == "target-update" && _checkpoint is { } run)
                target = target with { StartedAtUtc = run.StartedAtUtc };
            return _status with { TargetUpdate = target };
        }
    }

    public async Task<bool> PauseAsync(string operation, string runId, CancellationToken cancellationToken)
    {
        if (!await _transitionGate.WaitAsync(0, cancellationToken)) return false;
        try
        {
            if (_activeOperation is not { IsCompleted: false })
            {
                // A retained run waiting for its next retry attempt is paused in place: its unresolved
                // work and journal are kept, and only the automatic retry window stops.
                return await PauseRetainedRunAsync(operation, runId, cancellationToken);
            }

            Task? active;
            CancellationTokenSource? cts;
            await _stateWriteGate.WaitAsync(cancellationToken);
            try
            {
                var run = _checkpoint;
                if (!MatchesRun(run, operation, runId) || run!.Paused
                    || _userCancelCts?.IsCancellationRequested == true || _shutdownRequested
                    || _shutdownToken.IsCancellationRequested
                    || _activeOperation is not { IsCompleted: false }) return false;
                run.Paused = true;
                try
                {
                    var state = await LoadStateAsync(cancellationToken, persistMigration: false);
                    state.ActiveRun = SnapshotRun(run, preserveLegacy: _outcomeJournal is null);
                    await WriteStateAsync(state, cancellationToken);
                }
                catch
                {
                    run.Paused = false;
                    throw;
                }
                lock (_statusLock)
                {
                    _status = _status with { RunState = "pausing" };
                    active = _activeOperation;
                    cts = _activeCts;
                }
                TryCancel(cts);
            }
            finally { _stateWriteGate.Release(); }

            // Once pause intent is durable, the request disconnecting must not abandon the drain.
            if (active is not null) await active;
            await FlushCheckpointAsync(requireDurable: true);
            var retained = await LoadStateAsync(CancellationToken.None);
            return MatchesRun(retained.ActiveRun, operation, runId) && retained.ActiveRun!.Paused;
        }
        finally { _transitionGate.Release(); }
    }

    /// <summary>
    /// Pauses a retained run that is not currently executing. Nothing is running, so this only has to
    /// stop the automatic retry window while keeping the unresolved work for an explicit Resume.
    /// </summary>
    private async Task<bool> PauseRetainedRunAsync(string operation, string runId, CancellationToken cancellationToken)
    {
        var state = await LoadStateAsync(cancellationToken);
        var run = state.ActiveRun;
        if (!MatchesRun(run, operation, runId) || run!.Paused) return false;
        run.Paused = true;
        run.NextAttemptUtc = null;
        state.ActiveRun = SnapshotRun(run);
        await SaveStateAsync(state, cancellationToken);
        RestorePausedStatus(run);
        return true;
    }

    public Task<bool> ResumeAsync(string operation, string runId, CancellationToken cancellationToken)
        => ResumeAsync(operation, runId, cancellationToken, ResumeRunAsync);

    internal async Task<bool> ResumeAsync(string operation, string runId, CancellationToken cancellationToken,
        Func<ArtistMetadataActiveRun, CancellationToken, Task<bool>> enqueue)
    {
        if (!await _transitionGate.WaitAsync(0, cancellationToken)) return false;
        try
        {
            var state = await LoadStateAsync(cancellationToken);
            var run = state.ActiveRun;
            if (!MatchesRun(run, operation, runId) || !run!.Paused
                || _activeOperation is { IsCompleted: false }) return false;
            // An explicit resume starts a new retry window: attempts already spent do not carry over,
            // but the work already satisfied is still suppressed by the recorded outcomes.
            var retryWindowWasSet = run.TargetAttempts.Count > 0 || run.NextAttemptUtc is not null
                || run.PendingNotice is not null || run.Disposition is not null;
            ResetRetryWindow(run);
            if (retryWindowWasSet)
            {
                // The reset is durable before the operation gate is asked to accept the run, so an
                // interrupted enqueue cannot leave an exhausted attempt window behind.
                state.ActiveRun = run;
                await SaveStateAsync(state, cancellationToken);
            }

            lock (_statusLock) _status = _status with { RunState = "resuming" };
            // Keep the durable pause until the operation gate has accepted the same run.
            try
            {
                var accepted = await enqueue(run, cancellationToken);
                if (!accepted) RestorePausedStatus(run);
                return accepted;
            }
            catch
            {
                run.Paused = true;
                state.ActiveRun = run;
                await SaveStateAsync(state, CancellationToken.None);
                RestorePausedStatus(run);
                throw;
            }
        }
        finally { _transitionGate.Release(); }
    }

    public async Task<bool> CancelAsync(CancellationToken cancellationToken)
    {
        if (!await _transitionGate.WaitAsync(0, cancellationToken)) return false;
        try
        {
            var state = await LoadStateAsync(cancellationToken);
            // A retained run is cancellable whether it is paused or waiting for its next retry
            // attempt: either way the user is discarding the outstanding work deliberately.
            if (state.ActiveRun is not null && _activeOperation is not { IsCompleted: false })
            {
                var operation = state.ActiveRun.Operation;
                state.ActiveRun = null;
                await SaveStateAsync(state, cancellationToken);
                DeleteOutcomeJournal();
                lock (_statusLock) _status = _status with
                {
                    ActiveOperation = null, RunId = null, RunOperation = null, RunState = "idle", ResumeNotice = null,
                    RunDisposition = null, RunNextAttemptUtc = null, RunPendingNotice = null,
                    CacheRefresh = operation == "cache-refresh" ? _status.CacheRefresh with
                    { Phase = "Cache refresh cancelled", Message = "The cache refresh was cancelled.", CompletedAtUtc = DateTimeOffset.UtcNow }
                        : _status.CacheRefresh,
                    TargetUpdate = operation == "target-update" ? _status.TargetUpdate with
                    { Phase = "Metadata update cancelled", Message = "The metadata update was cancelled.", CompletedAtUtc = DateTimeOffset.UtcNow }
                        : _status.TargetUpdate
                };
                UpdateScheduleStatus(state, await _preferences.LoadAsync(), DateTimeOffset.UtcNow);
                return true;
            }
            return Cancel();
        }
        finally { _transitionGate.Release(); }
    }

    private static bool MatchesRun(ArtistMetadataActiveRun? run, string operation, string runId)
        => !string.IsNullOrWhiteSpace(runId) && run is not null
            && string.Equals(run.RunId, runId, StringComparison.Ordinal)
            && string.Equals(run.Operation, operation, StringComparison.Ordinal);

    /// <summary>
    /// Clears the retry bookkeeping of a retained run. Resume calls this so the attempt window starts
    /// over without discarding the work already recorded as satisfied.
    /// </summary>
    private static void ResetRetryWindow(ArtistMetadataActiveRun run)
    {
        run.TargetAttempts.Clear();
        run.CacheAttempts.Clear();
        run.NextAttemptUtc = null;
        run.PendingNotice = null;
        run.Disposition = null;
    }

    /// <summary>
    /// The notice shown for a retained run that is waiting for its next attempt, including the
    /// actionable reason recorded by the run that retained it.
    /// </summary>
    private static string DescribePendingRetryNotice(ArtistMetadataActiveRun run, int processed)
    {
        var operation = run.Operation.Replace('-', ' ');
        var retryAt = run.NextAttemptUtc is { } due ? $" at {due:HH:mm} (UTC)" : string.Empty;
        var notice = $"Retrying {operation} automatically{retryAt} ({processed} artist(s) already done).";
        return string.IsNullOrWhiteSpace(run.PendingNotice) ? notice : $"{notice} {run.PendingNotice}";
    }

    private void RestorePausedStatus(ArtistMetadataActiveRun run) => RestoreRetainedRunStatus(run, paused: true);

    /// <summary>
    /// Restores the visible state of a retained run (paused by the user, waiting for its next attempt,
    /// or waiting for the user after an exhausted window) from durable evidence, so a restart does not
    /// lose the reason the run is being kept.
    /// </summary>
    private void RestoreRetainedRunStatus(ArtistMetadataActiveRun run, bool paused)
    {
        var outcomes = BuildResumeContext(run)?.Outcomes ?? Array.Empty<ArtistRunOutcomeRecord>();
        var processed = outcomes.Count;
        var succeeded = outcomes.Count(o => o.Outcome == ArtistRunOutcomes.Succeeded);
        var failed = outcomes.Count(o => o.Outcome == ArtistRunOutcomes.Failed);
        var summaries = outcomes.SelectMany(o => o.TargetResults)
            .Where(result => !string.IsNullOrWhiteSpace(result.Target))
            .GroupBy(result => result.Target, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => new ArtistMetadataUpdaterService.TargetAccumulator
            {
                Updated = group.Count(result => result.Outcome == ArtistTargetOutcome.Updated),
                Unchanged = group.Count(result => result.Outcome == ArtistTargetOutcome.Unchanged),
                NotFound = group.Count(result => result.Outcome == ArtistTargetOutcome.NotFound),
                NotConfigured = group.Count(result => result.Outcome == ArtistTargetOutcome.NotConfigured),
                Failed = group.Count(result => result.Outcome == ArtistTargetOutcome.Failed)
            }, StringComparer.OrdinalIgnoreCase);
        foreach (var result in outcomes.SelectMany(o => o.TargetResults)
            .Where(result => summaries.ContainsKey(result.Target)))
            summaries[result.Target].Limitations.UnionWith(result.LimitationList);
        foreach (var target in run.TargetRequest?.Targets ?? new List<string>())
            summaries.TryAdd(target, new ArtistMetadataUpdaterService.TargetAccumulator());
        var operationLabel = run.Operation.Replace('-', ' ');
        var needsAttention = string.Equals(run.Disposition, ArtistMetadataRunDispositions.NeedsAttention, StringComparison.Ordinal);
        var disposition = paused
            ? needsAttention ? ArtistMetadataRunDispositions.NeedsAttention : ArtistMetadataRunDispositions.Paused
            : run.Disposition ?? ArtistMetadataRunDispositions.PendingRetry;
        var notice = paused
            ? string.IsNullOrWhiteSpace(run.PendingNotice)
                ? $"Paused {operationLabel} ({processed} artist(s) already done)."
                : $"Paused {operationLabel} ({processed} artist(s) already done). {run.PendingNotice}"
            : run.NextAttemptUtc is null
                ? $"Waiting to retry {operationLabel} ({processed} artist(s) already done)."
                : DescribePendingRetryNotice(run, processed);
        lock (_statusLock)
        {
            _status = _status with
            {
                ActiveOperation = null, RunId = run.RunId, RunOperation = run.Operation,
                RunState = paused ? "paused" : "pending-retry",
                RunDisposition = disposition, RunNextAttemptUtc = run.NextAttemptUtc, RunPendingNotice = run.PendingNotice,
                ResumeNotice = notice,
                CacheRefresh = run.Operation == "cache-refresh"
                    ? new ArtistMetadataCacheStatus(false, run.Automatic, "Cache refresh paused", null,
                        processed, run.TargetArtistIds.Count, null, run.StartedAtUtc, null, succeeded, failed)
                    : _status.CacheRefresh,
                TargetUpdate = run.Operation == "target-update"
                    ? new MetadataUpdaterStatusSnapshot(false, "Metadata update paused", null, run.StartedAtUtc, null,
                        run.TargetArtistIds.Count, processed, succeeded, failed,
                        outcomes.Count(o => o.Outcome == ArtistRunOutcomes.Skipped), null)
                    {
                        PartialArtists = outcomes.Count(o => o.Outcome == ArtistRunOutcomes.Partial),
                        NoMetadataArtists = outcomes.Count(o => o.Outcome == ArtistRunOutcomes.NoMetadata),
                        ResumedArtists = processed,
                        SkipReasons = outcomes.Where(o => o.Outcome == ArtistRunOutcomes.Skipped)
                            .GroupBy(o => o.Reason ?? ArtistRunSkipReasons.NotDue, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
                        Targets = ArtistMetadataUpdaterService.BuildTargetSummaries(summaries)
                    }
                    : _status.TargetUpdate
            };
        }
    }

    public bool Cancel()
    {
        CancellationTokenSource? userCts;
        bool running;
        lock (_statusLock)
        {
            userCts = _userCancelCts;
            running = _activeOperation is { IsCompleted: false }
                || !string.IsNullOrWhiteSpace(_status.ActiveOperation)
                || _status.CacheRefresh.Running;
        }

        running = running || _targetUpdate.GetStatus().Running;
        var requested = TryCancel(userCts);
        requested = _targetUpdate.Cancel() || requested;
        return requested || running;
    }

    private static bool TryCancel(CancellationTokenSource? cts)
    {
        if (cts is null)
        {
            return false;
        }

        try
        {
            if (!cts.IsCancellationRequested)
            {
                cts.Cancel();
            }

            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    internal static ArtistMetadataAutomationStatus FinalizeOperationStatus(
        ArtistMetadataAutomationStatus status,
        string operation,
        bool interruptedByShutdown,
        DateTimeOffset completedAtUtc)
    {
        var finalized = status with { ActiveOperation = null };
        if (!string.Equals(operation, "cache-refresh", StringComparison.Ordinal)
            || !status.CacheRefresh.Running)
        {
            return finalized;
        }

        return finalized with
        {
            CacheRefresh = status.CacheRefresh with
            {
                Running = false,
                Phase = interruptedByShutdown ? "Cache refresh interrupted" : "Cache refresh cancelled",
                Message = interruptedByShutdown
                    ? "The cache refresh was interrupted and will resume after restart."
                    : "The cache refresh was cancelled.",
                CurrentArtist = null,
                CompletedAtUtc = completedAtUtc
            }
        };
    }

    public async Task<(bool Queued, string? RunId)> EnqueueArtistCacheRefreshAsync(long artistId, CancellationToken cancellationToken,
        ArtistCacheRefreshScope scope = ArtistCacheRefreshScope.Full, bool automatic = false)
    {
        var preferences = await _preferences.LoadAsync();
        var request = BuildCacheRequest(preferences) with { ArtistId = artistId, FolderId = null, Scope = scope, ForceProviderRefresh = !automatic };
        if (automatic && await _cacheRefresh.IsCompleteAsync(request, cancellationToken)) return (false, null);
        if (_artistQueue is null) throw new InvalidOperationException("Artist cache queue is unavailable.");
        return await _artistQueue.EnqueueRefreshAsync(request, cancellationToken);
    }

    public ArtistMetadataAutomationStatus? GetArtistCacheRefreshStatus(string? runId, long? artistId = null)
        => _artistQueue?.GetRefreshStatus(runId, artistId);

    public Task<bool> EnqueueCacheRefreshAsync(ArtistMetadataCacheRefreshRequest request, CancellationToken cancellationToken)
        => EnqueueAsync(
            "cache-refresh",
            async token =>
            {
                await RunCacheRefreshAsync(request with { ForceProviderRefresh = true }, automatic: false, token);
                return true;
            },
            cancellationToken,
            resuming: null,
            cacheRequest: request with { ForceProviderRefresh = true });

    public Task<bool> EnqueueTargetUpdateAsync(MetadataUpdaterRunRequest request, CancellationToken cancellationToken)
        => EnqueueAsync(
            "target-update",
            token => RunTargetUpdateAsync(request, automatic: false, null, token),
            cancellationToken,
            resuming: null,
            targetRequest: request);

    /// <summary>
    /// BackgroundService only awaits ExecuteAsync, which returns as soon as a run is scheduled. The
    /// run itself is tracked separately, so shutdown must wait for it to unwind and flush its
    /// checkpoint; otherwise the tail of the durable record is lost on a hard stop.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _shutdownRequested = true;
        var stopping = base.StopAsync(cancellationToken);
        Task? active;
        lock (_statusLock)
        {
            active = _activeOperation;
            TryCancel(_activeCts);
        }

        if (active is { IsCompleted: false })
        {
            try
            {
                await active.WaitAsync(ShutdownDrainTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning(
                    "Artist metadata run did not finish within {DrainSeconds}s of shutdown; flushing checkpoint and continuing.",
                    ShutdownDrainTimeout.TotalSeconds);
            }
            catch (OperationCanceledException) when (!active.IsCompleted)
            {
                _logger.LogWarning("Artist metadata shutdown drain was cancelled; flushing checkpoint and continuing.");
            }
        }

        try
        {
            await FlushCheckpointAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Artist metadata checkpoint flush during shutdown failed.");
        }

        await stopping;
    }

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

    private Task ResumeInterruptedRunAsync(CancellationToken cancellationToken)
        => RecoverRunAsync(cancellationToken, ResumeRunAsync);

    internal async Task RecoverRunAsync(CancellationToken cancellationToken,
        Func<ArtistMetadataActiveRun, CancellationToken, Task<bool>> enqueue)
    {
        var state = await LoadStateAsync(cancellationToken);
        var run = state.ActiveRun;
        if (run is null)
        {
            return;
        }

        UpdateScheduleStatus(state, await _preferences.LoadAsync(), DateTimeOffset.UtcNow);
        if (run.Paused)
        {
            RestorePausedStatus(run);
            return;
        }
        if (run.NextAttemptUtc is { } nextAttempt && nextAttempt > DateTimeOffset.UtcNow)
        {
            // A retained run waits for its retry window instead of being re-enqueued on every poll
            // tick, so a failing target is not hammered between attempts.
            RestoreRetainedRunStatus(run, paused: false);
            return;
        }

        await enqueue(run, cancellationToken);
    }

    private async Task<bool> ResumeRunAsync(ArtistMetadataActiveRun run, CancellationToken cancellationToken)
    {
        var preferences = await _preferences.LoadAsync();
        if (string.Equals(run.Operation, "cache-refresh", StringComparison.Ordinal))
        {
            var cacheRequest = run.CacheRequest ?? BuildCacheRequest(preferences);
            return await EnqueueAsync("cache-refresh", async token =>
            {
                await RunCacheRefreshAsync(cacheRequest, run.Automatic, token);
                return true;
            }, cancellationToken, resuming: run);
        }
        var targetRequest = run.TargetRequest ?? BuildTargetRequest(preferences);
        return await EnqueueAsync("target-update",
            token => RunTargetUpdateAsync(targetRequest, run.Automatic, run, token),
            cancellationToken, resuming: run);
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
        var deepRefreshDue = cacheDue
            && IsDue(state.LastDeepRefreshUtc, preferences.MetadataDeepRefreshIntervalDays, now);
        UpdateScheduleStatus(state, preferences, now);
        if (!cacheDue && !updateDue)
        {
            return;
        }

        if (cacheDue)
        {
            var cacheRequest = deepRefreshDue
                ? BuildCacheRequest(preferences) with { ForceProviderRefresh = true }
                : BuildCacheRequest(preferences);
            if (await EnqueueAsync(
                    "cache-refresh",
                    async token =>
                    {
                        await RunCacheRefreshAsync(cacheRequest, automatic: true, token);
                        return true;
                    },
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
                    token => RunTargetUpdateAsync(targetRequest, automatic: true, null, token),
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

            var retained = await LoadStateAsync(cancellationToken);
            if (resuming is null && retained.ActiveRun is not null)
            {
                _operationGate.Release();
                return false;
            }

            lock (_statusLock)
            {
                _status = _status with { ActiveOperation = operation };
                _userCancelCts = new CancellationTokenSource();
                _activeCts = CancellationTokenSource.CreateLinkedTokenSource(_userCancelCts.Token, _shutdownToken);
            }
            Interlocked.Increment(ref _checkpointEpoch);
            var isNewRun = resuming is null;
            if (isNewRun && operation == "target-update")
                lock (_statusLock) _status = _status with { TargetUpdate = MetadataUpdaterStatusSnapshot.Idle() };
            _checkpoint = resuming ?? new ArtistMetadataActiveRun
            {
                Operation = operation,
                Automatic = automatic,
                StartedAtUtc = DateTimeOffset.UtcNow,
                CacheRequest = cacheRequest,
                TargetRequest = targetRequest,
                RunId = Guid.NewGuid().ToString("N")
            };
            if (string.IsNullOrWhiteSpace(_checkpoint.RunId))
            {
                // State written before the journal existed has no run id; give it one so the journal
                // it is migrated into cannot collide with an unrelated run's.
                _checkpoint.RunId = Guid.NewGuid().ToString("N");
            }

            _checkpoint.Paused = false;
            await PersistCheckpointAsync(requireDurable: true);
            lock (_statusLock) _status = _status with
            { RunId = _checkpoint.RunId, RunOperation = operation, RunState = "running", ResumeNotice = null };
            var journal = await OpenOutcomeJournalAsync(_checkpoint, isNewRun, cancellationToken);
            lock (_checkpointLock)
            {
                _outcomeJournal = journal;
                _journalAppend = Task.CompletedTask;
                _journalFailure = null;
                _checkpoint.Outcomes = null;
                _checkpoint.CompletedArtistIds = null;
            }

            _activeOperation = RunManualOperationAsync(operation, run, _activeCts);
            return true;
        }
        catch
        {
            _activeCts?.Dispose();
            _userCancelCts?.Dispose();
            _activeCts = null;
            _userCancelCts = null;
            _checkpoint = null;
            lock (_statusLock) _status = _status with { ActiveOperation = null };
            _operationGate.Release();
            throw;
        }
    }

    private async Task RunManualOperationAsync(
        string operation,
        Func<CancellationToken, Task<bool>> run,
        CancellationTokenSource cts)
    {
        try
        {
            await PersistCheckpointAsync();
            bool completed;
            try { completed = await run(cts.Token); }
            catch (OperationCanceledException ex) when (!cts.IsCancellationRequested)
            {
                throw new TimeoutException("An artist metadata request ended unexpectedly without cancelling the run.", ex);
            }
            if (!completed)
            {
                // The target updater catches cancellation and returns false, so a shutdown
                // interrupt never reaches the catch below. Keep that run for resume.
                // Any other incomplete result (user cancel, updater already running) must
                // not stamp a successful sweep clock.
                if ((_shutdownRequested || _shutdownToken.IsCancellationRequested || _checkpoint?.Paused == true)
                    && _userCancelCts?.IsCancellationRequested != true)
                {
                    _logger.LogInformation(
                        "Artist metadata {Operation} was interrupted by shutdown; keeping the run for resume ({Completed} artist(s) done).",
                        operation,
                        CountRecordedOutcomes());
                    await FlushCheckpointAsync();
                    await PersistCheckpointAsync();
                }
                else
                {
                    if (_userCancelCts?.IsCancellationRequested != true && !cts.IsCancellationRequested)
                        throw new InvalidOperationException("The artist metadata operation did not complete; unfinished work was retained.");
                    await ClearActiveRunWithoutStampingAsync();
                }

                return;
            }

            var deepRefresh = _checkpoint?.CacheRequest?.ForceProviderRefresh == true;
            var finishedRun = _checkpoint;
            // A failed append must retain the frozen run, never become an empty successful sweep.
            if (operation == "cache-refresh") await FlushCheckpointAsync(requireDurable: true);
            await StopCheckpointWritesAsync();
            var state = await LoadStateAsync(CancellationToken.None);
            if (operation == "cache-refresh" && finishedRun is not null
                && RetainUnresolvedCacheWork(finishedRun, ReadRunOutcomes(finishedRun), DateTimeOffset.UtcNow))
            {
                state.ActiveRun = SnapshotRun(finishedRun);
                await SaveStateAsync(state, CancellationToken.None);
            }
            else if (operation == "cache-refresh")
            {
                state.LastCacheRefreshUtc = DateTimeOffset.UtcNow;
                if (deepRefresh)
                {
                    state.LastDeepRefreshUtc = DateTimeOffset.UtcNow;
                }
                state.ActiveRun = null;
                await SaveStateAsync(state, CancellationToken.None);
                // The run finished, so its outcome journal is no longer needed. Leaving it would let it
                // accumulate; a stale one is discarded by the run id header regardless.
                DeleteOutcomeJournal();
            }
            else if (finishedRun is not null && RetainUnresolvedTargetWork(finishedRun, DateTimeOffset.UtcNow) is { Retain: true } decision)
            {
                // The sweep ended with unresolved target/field work. Keep the run and its journal so the
                // work is retried at the poll interval, and do not stamp the success clock for it.
                state.ActiveRun = SnapshotRun(finishedRun);
                await SaveStateAsync(state, CancellationToken.None);
                _logger.LogInformation(
                    "Artist metadata {Operation} retained {Unresolved} unresolved artist/target pair(s). {Notice}",
                    operation,
                    decision.UnresolvedPairs,
                    decision.Notice);
            }
            else
            {
                state.LastTargetUpdateUtc = DateTimeOffset.UtcNow;
                state.ActiveRun = null;
                await SaveStateAsync(state, CancellationToken.None);
                DeleteOutcomeJournal();
            }
            var preferences = await _preferences.LoadAsync();
            UpdateScheduleStatus(state, preferences, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Distinguish app shutdown from a user cancel: shutdown must KEEP the run
            // so it resumes on the next start; only an explicit user cancel clears it.
            if ((_shutdownRequested || _shutdownToken.IsCancellationRequested || _checkpoint?.Paused == true)
                    && _userCancelCts?.IsCancellationRequested != true)
            {
                _logger.LogInformation(
                    "Artist metadata {Operation} was interrupted by shutdown; keeping the run for resume ({Completed} artist(s) done).",
                    operation,
                    CountRecordedOutcomes());
                await FlushCheckpointAsync();
                await PersistCheckpointAsync();
            }
            else
            {
                _logger.LogInformation("Artist metadata {Operation} was cancelled.", operation);
                await ClearActiveRunWithoutStampingAsync();
            }
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex) || ex is OperationCanceledException)
        {
            _logger.LogWarning(ex, "Artist metadata {Operation} failed.", operation);
            if (_userCancelCts?.IsCancellationRequested == true)
            {
                // Provider/final-save cleanup may throw after user cancellation.
                // The user's stop instruction takes precedence over resuming that failure.
                await ClearActiveRunWithoutStampingAsync();
                return;
            }
            if (ex is DeezSpoTag.Integrations.TargetLookupException { Target: not null, UnresolvedArtists: not null } audit)
            {
                lock (_checkpointLock)
                {
                    if (_checkpoint is { } auditRun)
                    {
                        auditRun.UnresolvedAuditArtists[audit.Target] = new Dictionary<long, string>(audit.UnresolvedArtists);
                        auditRun.PendingNotice = audit.Message;
                        if (audit.UnresolvedArtists.Values.Any(cause =>
                            ArtistPendingWorkCategories.NeedsUserAction(ArtistPendingWorkCategories.ClassifyFailure(cause))))
                        {
                            auditRun.Paused = true;
                            auditRun.Disposition = ArtistMetadataRunDispositions.NeedsAttention;
                        }
                    }
                }
            }
            RecordOperationFailure(operation, ex.Message);
            await FlushCheckpointAsync();

            if (_checkpoint?.Paused == true)
            {
                await PersistCheckpointAsync(requireDurable: true);
                return;
            }

            // A run that keeps failing at preparation time would otherwise be retried on every poll
            // tick indefinitely. Count the failures and abandon the run once the cap is reached.
            var failures = 0;
            lock (_checkpointLock)
            {
                if (_checkpoint is { } poisoned)
                {
                    poisoned.ConsecutiveFailures++;
                    failures = poisoned.ConsecutiveFailures;
                    if (operation == "cache-refresh")
                    {
                        poisoned.Disposition = ArtistMetadataRunDispositions.PendingRetry;
                        poisoned.NextAttemptUtc = DateTimeOffset.UtcNow + PollInterval;
                        poisoned.PendingNotice = "Refresh Cache could not save completion evidence; unfinished work was retained.";
                    }
                }
            }

            if (failures >= MaxConsecutiveRunFailures
                && (operation == "cache-refresh" || ex is DeezSpoTag.Integrations.TargetLookupException { UnresolvedArtists: not null }
                    || ex is TimeoutException or OperationCanceledException))
            {
                lock (_checkpointLock)
                {
                    if (_checkpoint is { } auditRun)
                    {
                        auditRun.Paused = true;
                        auditRun.Disposition = ArtistMetadataRunDispositions.NeedsAttention;
                        auditRun.PendingNotice ??= ex.Message;
                    }
                }
                await PersistCheckpointAsync(requireDurable: true);
                return;
            }

            if (failures >= MaxConsecutiveRunFailures)
            {
                _logger.LogError(
                    "Artist metadata {Operation} failed {FailureCount} times in a row; abandoning the run instead of retrying.",
                    operation,
                    failures);
                await ClearActiveRunWithoutStampingAsync();
                return;
            }

            try
            {
                await PersistCheckpointAsync(requireDurable: true);
            }
            catch (Exception persistenceError) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(persistenceError))
            {
                _logger.LogWarning(persistenceError, "Artist metadata failure checkpoint could not be saved.");
            }
            Interlocked.Increment(ref _checkpointEpoch);
        }
        finally
        {
            ArtistMetadataAutomationState? retained = null;
            try
            {
                await FlushCheckpointAsync();
                await StopCheckpointWritesAsync();
                retained = await LoadStateAsync(CancellationToken.None);
            }
            catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
            {
                _logger.LogWarning(ex, "Artist metadata cleanup could not read or flush persisted state.");
            }
            finally
            {
                CancellationTokenSource? userCts;
                lock (_statusLock)
                {
                    userCts = _userCancelCts;
                    _userCancelCts = null;
                    _activeCts = null;
                    _status = FinalizeOperationStatus(_status, operation,
                        _shutdownRequested || _shutdownToken.IsCancellationRequested, DateTimeOffset.UtcNow);
                    _status = _status with { RunState = "idle" };
                }
                cts.Dispose();
                if (userCts is not null && !ReferenceEquals(userCts, cts)) userCts.Dispose();
                _operationGate.Release();
            }
            if (retained?.ActiveRun is { } retainedRun)
            {
                if (retainedRun.Paused)
                {
                    RestorePausedStatus(retainedRun);
                }
                else
                {
                    RestoreRetainedRunStatus(retainedRun, paused: false);
                }
            }
        }
    }

    private void RecordOperationFailure(string operation, string message)
    {
        lock (_statusLock)
        {
            if (operation == "cache-refresh")
            {
                _status = _status with
                {
                    CacheRefresh = _status.CacheRefresh with
                    {
                        Running = false,
                        Phase = "Cache refresh failed",
                        Message = message,
                        CurrentArtist = null,
                        CompletedAtUtc = DateTimeOffset.UtcNow
                    }
                };
                return;
            }

            // Target status is owned by ArtistMetadataUpdaterService and merged in GetStatus.
            _status = _status with
            {
                TargetUpdate = _status.TargetUpdate with
                {
                    Running = false,
                    Phase = "Metadata update failed",
                    Message = message,
                    CurrentArtist = null,
                    CompletedAtUtc = DateTimeOffset.UtcNow
                }
            };
        }
    }

    /// <summary>Attempts allowed per unresolved artist/target pair, including the initial sweep.</summary>
    internal const int MaxAttemptsPerUnresolvedTarget = 3;

    /// <summary>Result of judging whether a finished sweep is settled or must be retained.</summary>
    internal readonly record struct RetainedRunDecision(
        bool Retain,
        int UnresolvedPairs,
        int ExhaustedPairs,
        bool NeedsUserAction,
        string? Notice);

    /// <summary>
    /// Judges a finished target sweep against the durable per-target evidence it recorded. A sweep is
    /// settled only when no artist still owes a target write; otherwise the run keeps its journal and is
    /// retried at the poll interval, and the success clock is not stamped for it.
    /// </summary>
    private static bool RetainUnresolvedCacheWork(ArtistMetadataActiveRun run,
        IReadOnlyList<ArtistRunOutcomeRecord> outcomes, DateTimeOffset nowUtc)
    {
        var recorded = outcomes.Select(row => row.ArtistId).ToHashSet();
        var missing = (run.TargetArtistIds ?? []).Where(id => !recorded.Contains(id))
            .Select(id => new ArtistRunOutcomeRecord(id, ArtistRunOutcomes.Failed,
                CacheWork: [new ArtistCacheWorkOutcome("local", "artist", ArtistCacheWorkStatus.Failed, ReasonCode: "missing-outcome")]));
        var pending = outcomes.Concat(missing).SelectMany(row => (row.CacheWork ??
            [new ArtistCacheWorkOutcome("local", "artist", ArtistCacheWorkStatus.Failed, ReasonCode: "unknown-stage-evidence")])
            .Where(work => !work.IsComplete).Select(work => (row.ArtistId, Work: work))).ToList();
        if (pending.Count == 0) return false;
        var attempts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in pending)
        {
            var key = $"{entry.ArtistId}:{entry.Work.Provider}:{entry.Work.Stage}";
            attempts[key] = run.CacheAttempts.GetValueOrDefault(key) + 1;
        }
        run.CacheAttempts = attempts;
        var needsAttention = pending.Any(item => item.Work.Status == ArtistCacheWorkStatus.AuthenticationRequired)
            || attempts.Values.Any(attempt => attempt >= MaxAttemptsPerUnresolvedTarget);
        var retryDue = pending.Where(item => item.Work.RetryAtUtc.HasValue)
            .Select(item => item.Work.RetryAtUtc!.Value).DefaultIfEmpty(nowUtc + PollInterval).Max();
        run.NextAttemptUtc = needsAttention ? null : (retryDue > nowUtc + PollInterval ? retryDue : nowUtc + PollInterval);
        run.Paused = needsAttention;
        run.Disposition = needsAttention ? ArtistMetadataRunDispositions.NeedsAttention : ArtistMetadataRunDispositions.PendingRetry;
        run.PendingNotice = needsAttention
            ? $"Refresh Cache retained {pending.Count} unfinished source update(s). Check provider access, then Resume."
            : $"Refresh Cache retained {pending.Count} unfinished source update(s) for retry.";
        return true;
    }

    private RetainedRunDecision RetainUnresolvedTargetWork(ArtistMetadataActiveRun run, DateTimeOffset nowUtc)
    {
        var pairs = ArtistMetadataPendingWork.Collect(ReadRunOutcomes(run), run.TargetAttempts);
        if (pairs.Count == 0)
        {
            return new RetainedRunDecision(false, 0, 0, false, null);
        }

        var attempts = new Dictionary<string, int>(StringComparer.Ordinal);
        var needsUserAction = false;
        var exhausted = 0;
        foreach (var pair in pairs)
        {
            // This sweep was an attempt against every pair that is still unresolved.
            var used = pair.Attempts + 1;
            attempts[ArtistMetadataPendingWork.Key(pair.ArtistId, pair.Target)] = used;
            if (ArtistPendingWorkCategories.NeedsUserAction(pair.Category))
            {
                needsUserAction = true;
            }

            if (used >= MaxAttemptsPerUnresolvedTarget)
            {
                exhausted++;
            }
        }

        run.TargetAttempts = attempts;
        run.NextAttemptUtc = needsUserAction || exhausted > 0 ? null : nowUtc + PollInterval;
        run.PendingNotice = BuildPendingNotice(pairs.Count, exhausted, needsUserAction);
        run.Disposition = needsUserAction || exhausted > 0
            ? ArtistMetadataRunDispositions.NeedsAttention
            : ArtistMetadataRunDispositions.PendingRetry;
        if (needsUserAction || exhausted > 0)
        {
            run.Paused = true;
        }

        return new RetainedRunDecision(true, pairs.Count, exhausted, needsUserAction, run.PendingNotice);
    }

    private static string BuildPendingNotice(int unresolvedPairs, int exhaustedPairs, bool needsUserAction)
    {
        var scope = unresolvedPairs == 1 ? "1 artist/target pair" : $"{unresolvedPairs} artist/target pairs";
        if (needsUserAction)
        {
            return $"Update Servers retained {scope} with a configuration or authentication failure. Fix the server settings, then Resume.";
        }

        if (exhaustedPairs > 0)
        {
            return $"Update Servers retained {scope} after {MaxAttemptsPerUnresolvedTarget} attempts. Resume to start a new retry window.";
        }

        return $"Update Servers retained {scope} for another attempt.";
    }

    private static ArtistMetadataActiveRun SnapshotRun(ArtistMetadataActiveRun checkpoint, bool preserveLegacy = false) => new()
    {
        Operation = checkpoint.Operation, Automatic = checkpoint.Automatic, Paused = checkpoint.Paused,
        StartedAtUtc = checkpoint.StartedAtUtc, CacheRequest = checkpoint.CacheRequest,
        TargetRequest = checkpoint.TargetRequest, RunId = checkpoint.RunId,
        TargetArtistIds = checkpoint.TargetArtistIds.ToList(), ConsecutiveFailures = checkpoint.ConsecutiveFailures,
        TargetAttempts = new Dictionary<string, int>(checkpoint.TargetAttempts, StringComparer.Ordinal),
        CacheAttempts = new Dictionary<string, int>(checkpoint.CacheAttempts, StringComparer.Ordinal),
        NextAttemptUtc = checkpoint.NextAttemptUtc, PendingNotice = checkpoint.PendingNotice,
        Disposition = checkpoint.Disposition,
        UnresolvedAuditArtists = checkpoint.UnresolvedAuditArtists.ToDictionary(pair => pair.Key, pair => new Dictionary<long, string>(pair.Value), StringComparer.OrdinalIgnoreCase),
        Outcomes = preserveLegacy ? checkpoint.Outcomes?.ToList() : null,
        CompletedArtistIds = preserveLegacy ? checkpoint.CompletedArtistIds?.ToList() : null
    };

    private async Task PersistCheckpointAsync(bool requireDurable = false)
    {
        // Checkpoint persistence is a durable write that must complete even when the
        // surrounding run is being cancelled: it is exactly what preserves the resume
        // position across a shutdown. Waiting on the write gate is therefore deliberately
        // not tied to a run token, which is spelled out here rather than left implicit.
        await _stateWriteGate.WaitAsync(CancellationToken.None);
        try
        {
            var epoch = Volatile.Read(ref _checkpointEpoch);
            ArtistMetadataActiveRun? snapshot;
            lock (_checkpointLock) snapshot = _checkpoint is null ? null : SnapshotRun(_checkpoint, preserveLegacy: _outcomeJournal is null);
            if (snapshot is null) return;
            var state = await LoadStateAsync(CancellationToken.None, persistMigration: false);
            if (Volatile.Read(ref _checkpointEpoch) != epoch) return;
            state.ActiveRun = snapshot;
            await WriteStateAsync(state, CancellationToken.None);
        }
        catch (Exception ex) when (!requireDurable && ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Artist metadata checkpoint save failed.");
        }
        finally { _stateWriteGate.Release(); }
    }

    /// <summary>
    /// Opens the outcome journal for the run about to start. A new run truncates it; a resumed run
    /// appends to it and recovers the outcomes already recorded.
    /// </summary>
    private async Task<ArtistMetadataOutcomeJournal> OpenOutcomeJournalAsync(
        ArtistMetadataActiveRun run,
        bool isNewRun,
        CancellationToken cancellationToken)
    {
        var seed = isNewRun ? null : BuildLegacySeed(run);
        var journal = await ArtistMetadataOutcomeJournal.OpenAsync(
            _outcomeJournalPath,
            run.RunId,
            truncate: isNewRun,
            seed,
            cancellationToken);
        if (journal.RecoveredCount > 0)
        {
            _logger.LogInformation(
                "Artist metadata {Operation} recovered {Recovered} recorded outcome(s) from the journal.",
                run.Operation,
                journal.RecoveredCount);
        }

        return journal;
    }

    /// <summary>
    /// State written before the journal existed carries its outcomes inline (or, before that, as bare
    /// visited ids). Those are converted once and seeded into the journal so the rest of the resume
    /// path only has to understand the journal.
    /// </summary>
    private static IReadOnlyList<ArtistRunOutcomeRecord>? BuildLegacySeed(ArtistMetadataActiveRun run)
    {
        if (run.Outcomes is { Count: > 0 })
        {
            return run.Outcomes;
        }

        return run.CompletedArtistIds is { Count: > 0 }
            ? MigrateVisitedIdsToOutcomes(run.CompletedArtistIds)
            : null;
    }

    private async Task StopCheckpointWritesAsync()
    {
        Interlocked.Increment(ref _checkpointEpoch);
        ArtistMetadataOutcomeJournal? journal;
        lock (_checkpointLock)
        {
            _checkpoint = null;
            journal = _outcomeJournal;
            _outcomeJournal = null;
        }

        await FlushCheckpointAsync();
        if (journal is not null)
        {
            await journal.DisposeAsync();
        }
    }

    private async Task ClearActiveRunWithoutStampingAsync()
    {
        await StopCheckpointWritesAsync();
        try
        {
            DeleteOutcomeJournal();
            var state = await LoadStateAsync(CancellationToken.None);
            state.ActiveRun = null;
            await SaveStateAsync(state, CancellationToken.None);
            var preferences = await _preferences.LoadAsync();
            UpdateScheduleStatus(state, preferences, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Artist metadata active-run clear failed.");
        }
    }

    private void DeleteOutcomeJournal()
    {
        try
        {
            if (File.Exists(_outcomeJournalPath))
            {
                File.Delete(_outcomeJournalPath);
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Artist metadata outcome journal delete failed.");
        }
    }

    /// <summary>
    /// Waits for queued journal appends and flushes them to the OS. The state file no longer changes
    /// per artist, so this is the only durability barrier a shutdown needs.
    /// </summary>
    private async Task FlushCheckpointAsync(bool requireDurable = false)
    {
        Task pending;
        ArtistMetadataOutcomeJournal? journal;
        lock (_checkpointLock)
        {
            pending = _journalAppend;
            journal = _outcomeJournal;
        }

        try
        {
            await pending;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Interlocked.CompareExchange(ref _journalFailure, ex, null);
            _logger.LogDebug(ex, "Artist metadata outcome journal append failed.");
        }

        if (journal is not null)
        {
            try
            {
                await journal.FlushAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.CompareExchange(ref _journalFailure, ex, null);
                _logger.LogDebug(ex, "Artist metadata outcome journal flush failed.");
            }
        }
        if (requireDurable && _journalFailure is { } failure)
            throw new IOException("The artist metadata outcome journal could not be saved durably.", failure);
    }

    private async Task<ArtistMetadataCacheRefreshResult> RunCacheRefreshAsync(
        ArtistMetadataCacheRefreshRequest request,
        bool automatic,
        CancellationToken cancellationToken)
    {
        var resume = BuildResumeContext(_checkpoint);
        var outcomes = resume?.Outcomes ?? Array.Empty<ArtistRunOutcomeRecord>();
        UpdateCacheStatus(new ArtistMetadataCacheStatus(true, automatic, "Refreshing artist metadata cache", null,
            outcomes.Count, resume?.TargetArtistIds?.Count ?? 0, null, _checkpoint?.StartedAtUtc ?? DateTimeOffset.UtcNow, null,
            outcomes.Count(o => o.Outcome is ArtistRunOutcomes.Succeeded or ArtistRunOutcomes.Partial),
            outcomes.Count(o => o.Outcome == ArtistRunOutcomes.Failed)));
        var progress = new Progress<ArtistMetadataOperationProgress>(UpdateCacheProgress);
        var result = await _cacheRefresh.RefreshAsync(
            outcomes.Count > 0 ? request with { ForceProviderRefresh = false } : request,
            progress,
            resumedRun: resume,
            outcomeSink: NoteArtistOutcome,
            cancellationToken,
            targetSink: PersistRunTargetsAsync);
        UpdateCacheStatus(GetStatus().CacheRefresh with
        {
            Running = false,
            Phase = result.Error is null ? "Cache refresh completed" : "Cache refresh failed",
            Message = result.Error ?? $"{result.Succeeded} succeeded, {result.Failed} failed.",
            ProcessedArtists = result.Succeeded + result.Failed,
            TotalArtists = result.Total,
            SuccessfulArtists = result.Succeeded,
            FailedArtists = result.Failed,
            CurrentArtist = null,
            CompletedAtUtc = DateTimeOffset.UtcNow
        });
        if (_checkpoint is { } run)
            await _cacheRefresh.SaveRunResultAsync(run.RunId, request.ArtistId, result, cancellationToken);
        return result;
    }

    private async Task<bool> RunTargetUpdateAsync(
        MetadataUpdaterRunRequest request,
        bool automatic,
        ArtistMetadataActiveRun? resuming,
        CancellationToken cancellationToken)
    {
        // Provider collection belongs to Refresh Cache. Target updates publish the existing
        // caches, including popular songs, without starting an unreported provider sweep.
        var resume = BuildResumeContext(resuming);
        lock (_statusLock)
        {
            _status = _status with { ActiveOperation = "target-update" };
        }

        // progress drives the UI only. The checkpoint is written through the journal rather than by
        // rewriting the state file, and the resolved artist set is published once so a resume reuses it.
        var progress = new Progress<ArtistMetadataOperationProgress>(static _ => { });
        return await _targetUpdate.RunAndWaitAsync(
            request,
            automatic,
            progress,
            resume,
            RecordArtistOutcomeAsync,
            NoteRunTargets,
            cancellationToken);
    }

    /// <summary>
    /// Records the artist set the run resolved to, and persists it immediately. This has to be durable
    /// before the first artist is processed: a crash mid-run must resume against the same targets, and
    /// the state file is small enough that writing it once more costs nothing.
    /// </summary>
    private async Task PersistRunTargetsAsync(IReadOnlyList<long> targetArtistIds)
    {
        lock (_checkpointLock)
        {
            if (_checkpoint is not { } checkpoint) return;
            if (checkpoint.TargetArtistIds.Count == 0)
                checkpoint.TargetArtistIds = targetArtistIds.ToList();
        }
        await PersistCheckpointAsync(requireDurable: true);
    }

    private Task NoteRunTargets(IReadOnlyList<long> targetArtistIds)
        => PersistRunTargetsAsync(targetArtistIds);

    private async Task RecordArtistOutcomeAsync(ArtistRunOutcomeRecord record)
    {
        NoteArtistOutcome(record);
        await FlushCheckpointAsync(requireDurable: true);
    }

    /// <summary>
    /// Builds the resume context from persisted state. State written before per-artist outcomes
    /// existed is migrated by treating every visited id as a success, which is the only defensible
    /// reading of the old shape and keeps the counters internally consistent.
    /// </summary>
    /// <summary>
    /// Builds the resume context from the outcome journal. The state file no longer carries outcomes, so
    /// a resumed run reads them back from the journal; legacy state without a journal falls back to the
    /// inline records or the bare visited-id list, both of which are read as successes.
    /// </summary>
    private ArtistRunResumeContext? BuildResumeContext(ArtistMetadataActiveRun? resuming)
    {
        if (resuming is null)
        {
            return null;
        }

        var outcomes = ArtistMetadataOutcomeJournal.Read(_outcomeJournalPath, resuming.RunId);
        if (outcomes.Count == 0)
        {
            outcomes = BuildLegacySeed(resuming) ?? Array.Empty<ArtistRunOutcomeRecord>();
        }

        if (outcomes.Count == 0 && resuming.TargetArtistIds.Count == 0)
        {
            return null;
        }

        return new ArtistRunResumeContext(outcomes, resuming.TargetArtistIds);
    }

    /// <summary>
    /// How many artists already have a recorded outcome for the run in flight. Reads the journal, so
    /// the figure reflects what is actually durable rather than an in-memory list.
    /// </summary>
    private int CountRecordedOutcomes(ArtistMetadataActiveRun? run = null)
    {
        var active = run ?? _checkpoint;
        return ReadRunOutcomes(active).Count;
    }

    /// <summary>
    /// Reads the durable per-artist outcomes recorded for a run. The state file no longer carries
    /// outcomes, so this is the only evidence a retained run can be judged on.
    /// </summary>
    private IReadOnlyList<ArtistRunOutcomeRecord> ReadRunOutcomes(ArtistMetadataActiveRun? run)
    {
        if (run is null || string.IsNullOrWhiteSpace(run.RunId))
        {
            return Array.Empty<ArtistRunOutcomeRecord>();
        }

        return ArtistMetadataOutcomeJournal.Read(_outcomeJournalPath, run.RunId);
    }

    private static IReadOnlyList<ArtistRunOutcomeRecord> MigrateVisitedIdsToOutcomes(List<long> completedArtistIds)    {
        if (completedArtistIds.Count == 0)
        {
            return Array.Empty<ArtistRunOutcomeRecord>();
        }

        return completedArtistIds
            .Where(static artistId => artistId > 0)
            .Distinct()
            .Select(static artistId => new ArtistRunOutcomeRecord(artistId, ArtistRunOutcomes.Succeeded))
            .ToList();
    }

    /// <summary>
    /// Records a finished artist by appending one line to the outcome journal. Called synchronously from
    /// the run loop so the record is queued in order and cannot lag behind the work already done, and
    /// appending keeps the cost independent of how many artists the run has already processed.
    /// </summary>
    private void NoteArtistOutcome(ArtistRunOutcomeRecord record)
    {
        if (record.ArtistId <= 0)
        {
            return;
        }

        lock (_checkpointLock)
        {
            if (_outcomeJournal is not { } journal)
            {
                return;
            }

            // Carry the record in the continuation state so it is appended exactly once, in order.
            _journalAppend = _journalAppend.ContinueWith(
                static (previous, state) =>
                {
                    var (coordinator, journal, pending) = ((ArtistMetadataAutomationCoordinator, ArtistMetadataOutcomeJournal, ArtistRunOutcomeRecord))state!;
                    return coordinator.AppendOutcomeAsync(previous, journal, pending);
                },
                (this, journal, record),
                CancellationToken.None,
                TaskContinuationOptions.RunContinuationsAsynchronously,
                TaskScheduler.Default).Unwrap();
        }
    }

    private async Task AppendOutcomeAsync(
        Task previous,
        ArtistMetadataOutcomeJournal journal,
        ArtistRunOutcomeRecord record)
    {
        try
        {
            await previous;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The previous append already logged; keep the chain alive.
        }

        try
        {
            await journal.AppendAsync(record, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Ordinary recovery retries a missing record; Pause must not acknowledge lost outcomes.
            Interlocked.CompareExchange(ref _journalFailure, ex, null);
        }
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
                NextTargetUpdateUtc = NextDue(state.LastTargetUpdateUtc, preferences.MetadataTargetUpdateIntervalDays, now),
                ResumeNotice = state.ActiveRun is null
                    ? null
                    : state.ActiveRun.Paused
                        ? string.IsNullOrWhiteSpace(state.ActiveRun.PendingNotice)
                            ? $"Paused {state.ActiveRun.Operation.Replace('-', ' ')} ({CountRecordedOutcomes(state.ActiveRun)} artist(s) already done)."
                            : $"Paused {state.ActiveRun.Operation.Replace('-', ' ')} ({CountRecordedOutcomes(state.ActiveRun)} artist(s) already done). {state.ActiveRun.PendingNotice}"
                        : state.ActiveRun.NextAttemptUtc is null
                            ? $"Interrupted {state.ActiveRun.Operation.Replace('-', ' ')} will resume where it stopped ({CountRecordedOutcomes(state.ActiveRun)} artist(s) already done)."
                            : DescribePendingRetryNotice(state.ActiveRun, CountRecordedOutcomes(state.ActiveRun)),
                RunDisposition = state.ActiveRun is null
                    ? null
                    : state.ActiveRun.Paused
                        ? string.Equals(state.ActiveRun.Disposition, ArtistMetadataRunDispositions.NeedsAttention, StringComparison.Ordinal)
                            ? ArtistMetadataRunDispositions.NeedsAttention
                            : ArtistMetadataRunDispositions.Paused
                        : state.ActiveRun.Disposition ?? ArtistMetadataRunDispositions.PendingRetry,
                RunNextAttemptUtc = state.ActiveRun?.NextAttemptUtc,
                RunPendingNotice = state.ActiveRun?.PendingNotice
            };
        }
    }

    private static ArtistMetadataCacheRefreshRequest BuildCacheRequest(UserPreferencesDto preferences)
        => new(
            null,
            ParseFolderId(preferences.MetadataUpdaterFolderId),
            preferences.MetadataUpdaterSource,
            preferences.MetadataUpdaterIncludePopularSongs,
            preferences.MetadataUpdaterIncludeDiscography,
            ForceProviderRefresh: false,
            OcrTextArtBlockingEnabled: preferences.MetadataUpdaterOcrTextArtBlocking);

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
            IncludeDiscography = preferences.MetadataUpdaterIncludeDiscography,
            MissingArtistArtworkOnly = preferences.MetadataUpdaterMissingArtistArtworkOnly,
            OcrTextArtBlockingEnabled = preferences.MetadataUpdaterOcrTextArtBlocking,
            SaveArtistFolderImage = preferences.MetadataUpdaterSaveArtistFolderImage,
            IncludeAllArtists = true,
            Force = true
        };

    private static long? ParseFolderId(string? value) => long.TryParse(value, out var id) && id > 0 ? id : null;
    private static bool IsDue(DateTimeOffset? lastRun, int intervalDays, DateTimeOffset now)
        => intervalDays > 0 && (!lastRun.HasValue || now - lastRun.Value >= TimeSpan.FromDays(intervalDays));
    private static DateTimeOffset? NextDue(DateTimeOffset? lastRun, int intervalDays, DateTimeOffset now)
        => intervalDays <= 0 ? null : lastRun?.AddDays(intervalDays) ?? now;

    private async Task<ArtistMetadataAutomationState> LoadStateAsync(CancellationToken cancellationToken, bool persistMigration = true)
    {
        if (!File.Exists(_statePath))
        {
            return await MigrateLegacyStateAsync(cancellationToken, persistMigration);
        }
        await using var stream = File.OpenRead(_statePath);
        return await JsonSerializer.DeserializeAsync<ArtistMetadataAutomationState>(stream, cancellationToken: cancellationToken)
            ?? new ArtistMetadataAutomationState();
    }

    private async Task<ArtistMetadataAutomationState> MigrateLegacyStateAsync(CancellationToken cancellationToken, bool persistMigration)
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
                foreach (var timestamp in new[] { "lastPushedAtUtc", "updatedAtUtc" }
                    .Select(property => artist.TryGetProperty(property, out var value)
                        && value.ValueKind == JsonValueKind.String
                        && DateTimeOffset.TryParse(value.GetString(), out var parsed)
                        ? (DateTimeOffset?)parsed
                        : null)
                    .Where(timestamp => timestamp.HasValue
                        && (!latest.HasValue || timestamp.Value > latest.Value)))
                {
                    latest = timestamp;
                }
            }
            state.LastCacheRefreshUtc = latest;
            state.LastTargetUpdateUtc = latest;
        }

        if (persistMigration) await SaveStateAsync(state, cancellationToken);
        return state;
    }

    private async Task SaveStateAsync(ArtistMetadataAutomationState state, CancellationToken cancellationToken)
    {
        await _stateWriteGate.WaitAsync(cancellationToken);
        try { await WriteStateAsync(state, cancellationToken); }
        finally { _stateWriteGate.Release(); }
    }

    private async Task WriteStateAsync(ArtistMetadataAutomationState state, CancellationToken cancellationToken)
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
    public int Version { get; set; } = ArtistMetadataAutomationStateVersions.Current;
    public DateTimeOffset? LastCacheRefreshUtc { get; set; }
    public DateTimeOffset? LastTargetUpdateUtc { get; set; }
    public DateTimeOffset? LastDeepRefreshUtc { get; set; }
    public ArtistMetadataActiveRun? ActiveRun { get; set; }
}

public static class ArtistMetadataAutomationStateVersions
{
    /// <summary>Original shape: a bare list of visited artist ids.</summary>
    public const int VisitedIdsOnly = 3;

    /// <summary>Per-artist outcome records plus a frozen target set.</summary>
    public const int Current = 4;
}

public sealed class ArtistMetadataActiveRun
{
    public string Operation { get; set; } = string.Empty;
    public bool Automatic { get; set; }
    public bool Paused { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public ArtistMetadataCacheRefreshRequest? CacheRequest { get; set; }
    public MetadataUpdaterRunRequest? TargetRequest { get; set; }

    /// <summary>
    /// Identifies the outcome journal belonging to this run, so a journal left behind by an abandoned
    /// run is discarded instead of being merged into an unrelated one.
    /// </summary>
    public string RunId { get; set; } = string.Empty;

    /// <summary>Artist ids resolved once when the run started, so a resume reuses the same targets.</summary>
    public List<long> TargetArtistIds { get; set; } = new();

    /// <summary>Consecutive run-level failures, so a poisoned run stops auto-retrying forever.</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>
    /// Attempts already spent per unresolved artist/target pair, keyed by
    /// <see cref="ArtistMetadataPendingWork.Key"/>. A pair that becomes satisfied is dropped, and an
    /// explicit Resume clears the counters so it starts a new retry window.
    /// </summary>
    public Dictionary<string, int> TargetAttempts { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> CacheAttempts { get; set; } = new(StringComparer.Ordinal);

    /// <summary>When the retained run's next automatic attempt is due. Null when nothing is pending.</summary>
    public DateTimeOffset? NextAttemptUtc { get; set; }

    /// <summary>Why the run is retained, so the controls can state the actionable reason after restart.</summary>
    public string? PendingNotice { get; set; }

    public Dictionary<string, Dictionary<long, string>> UnresolvedAuditArtists { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One of <see cref="ArtistMetadataRunDispositions"/> for a retained run.</summary>
    public string? Disposition { get; set; }

    /// <summary>
    /// Visited artist ids from state written before per-artist outcomes existed. Read for migration
    /// only; superseded by <see cref="ArtistMetadataOutcomeJournal"/> and never written.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<long>? CompletedArtistIds { get; set; }

    /// <summary>
    /// Per-artist outcomes from an early v4 state file. Read for migration only; the journal is the
    /// source of truth and keeping the list out of the state file is what makes appends O(1).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<ArtistRunOutcomeRecord>? Outcomes { get; set; }
}

/// <summary>Terminal/non-terminal classification of a single artist's result within a run.</summary>
public static class ArtistRunOutcomes
{
    public const string Succeeded = "succeeded";
    public const string Partial = "partial";
    public const string Skipped = "skipped";
    public const string NoMetadata = "noMetadata";
    public const string Failed = "failed";

    /// <summary>Skip reasons that must not be retried because retrying cannot change the result.</summary>
    public static bool IsTerminalSkip(string? reason)
        => string.IsNullOrWhiteSpace(reason)
           || string.Equals(reason, ArtistRunSkipReasons.NotDue, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the artist should not be revisited on resume. Successes and partial successes are
    /// terminal once they have nothing outstanding; <c>notDue</c> skips are terminal because the
    /// interval has not elapsed. Failures and missing metadata stay retryable, and any record that
    /// still carries unresolved target work is retryable regardless of its artist-level label.
    /// </summary>
    public static bool IsTerminal(ArtistRunOutcomeRecord record)
    {
        if (record.HasUnresolvedWork)
        {
            return false;
        }

        return record.Outcome switch
        {
            Succeeded => true,
            Partial => true,
            Skipped => IsTerminalSkip(record.Reason),
            _ => false
        };
    }

    public static IReadOnlySet<long> BuildResumeSkipSet(IEnumerable<ArtistRunOutcomeRecord>? outcomes)
    {
        if (outcomes is null)
        {
            return new HashSet<long>();
        }

        var skip = new HashSet<long>();
        foreach (var artistId in outcomes
            .Where(record => record.ArtistId > 0 && IsTerminal(record))
            .Select(record => record.ArtistId))
        {
            skip.Add(artistId);
        }

        return skip;
    }
}

public static class ArtistRunSkipReasons
{
    public const string NotDue = "notDue";
    public const string SyncBlocked = "syncBlocked";
    public const string ScanOnly = "scanOnly";
    public const string ArtistRowMissing = "artistRowMissing";
    public const string Timeout = "timeout";
}

/// <summary>Immutable content selected for a target-update attempt and its retries.</summary>
public sealed record ArtistPushPayload(string? AvatarPath, string? AvatarHash,
    string? BackgroundPath, string? BackgroundHash, string? Biography,
    int NextAvatarIndex, int NextBackgroundIndex);

/// <summary>What one media server was actually asked to do for one artist.</summary>
public sealed record ArtistRunOutcomeRecord(
    long ArtistId,
    string Outcome,
    string? Reason = null,
    IReadOnlyList<ArtistTargetResult>? Targets = null,
    MetadataUpdaterTrackedArtist? TrackingState = null,
    IReadOnlyList<ArtistTargetPendingWork>? PendingWork = null,
    ArtistPushPayload? Payload = null,
    IReadOnlyList<ArtistCacheWorkOutcome>? CacheWork = null)
{
    /// <summary>Never-null view over <see cref="Targets"/> for consumers walking the graph.</summary>
    public IReadOnlyList<ArtistTargetResult> TargetResults => Targets ?? Array.Empty<ArtistTargetResult>();

    /// <summary>Never-null view over the unresolved target work recorded for this artist.</summary>
    public IReadOnlyList<ArtistTargetPendingWork> PendingWorkList => PendingWork ?? Array.Empty<ArtistTargetPendingWork>();

    /// <summary>
    /// True when this artist still owes a server write after its last attempt. Only typed outcomes
    /// carry this, so legacy records without per-target evidence never suppress or invent a retry.
    /// </summary>
    public bool HasUnresolvedWork => PendingWorkList.Count > 0 || CacheWork?.Any(work => !work.IsComplete) == true;
}

/// <summary>
/// Dispositions a target-update sweep can end in. A retained run keeps its checkpoint and journal so
/// the Activities controls can explain and recover it after a restart.
/// </summary>
public static class ArtistMetadataRunDispositions
{
    /// <summary>The sweep finished with no unresolved required target work.</summary>
    public const string Completed = "completed";

    /// <summary>Unresolved target/field work is retained and retried at the poll interval.</summary>
    public const string PendingRetry = "pending-retry";

    /// <summary>The attempt window is spent or the user has to act; Resume starts a new window.</summary>
    public const string NeedsAttention = "needs-attention";

    /// <summary>Paused by the user.</summary>
    public const string Paused = "paused";

    /// <summary>Cancelled by the user; nothing is retried.</summary>
    public const string Cancelled = "cancelled";
}

/// <summary>Why an artist/target pair still has work after an attempt.</summary>
public static class ArtistPendingWorkCategories
{
    /// <summary>A transient server error; another attempt can plausibly succeed.</summary>
    public const string Transient = "transient";

    /// <summary>Credentials or endpoint configuration need the user before more network attempts.</summary>
    public const string Authentication = "authentication";

    /// <summary>The server accepted a request but the local result could not be confirmed.</summary>
    public const string Uncertain = "uncertain";

    /// <summary>Preparation could not prove whether the target has the artwork.</summary>
    public const string Unknown = "unknown";

    /// <summary>The target is selected but not configured.</summary>
    public const string NotConfigured = "not-configured";

    /// <summary>True when an automatic retry is worth an attempt.</summary>
    public static bool IsRetryable(string? category)
        => category is Transient or Uncertain or Unknown;

    /// <summary>True when the category needs the user before another network attempt.</summary>
    public static bool NeedsUserAction(string? category)
        => category is Authentication or NotConfigured;

    /// <summary>
    /// Classifies a failed target write into a retry category. Authentication and credential
    /// failures must not consume repeated automatic attempts, so they are recognised explicitly
    /// rather than retried three times.
    /// </summary>
    public static string ClassifyFailure(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return Transient;
        }

        foreach (var marker in AuthenticationMarkers)
        {
            if (error.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return Authentication;
            }
        }

        return Transient;
    }

    private static readonly string[] AuthenticationMarkers =
    [
        "401", "403", "unauthor", "forbidden", "authentication", "credential", "api key", "apikey",
        "invalid token", "token expired", "access denied", "not permitted"
    ];
}

/// <summary>
/// Unresolved work for one artist/target pair, carried in the outcome journal so a later attempt knows
/// what is outstanding and how many attempts it has already had.
/// </summary>
public sealed record ArtistTargetPendingWork(
    string Target,
    IReadOnlyList<string> Fields,
    string Category,
    string? LastError = null)
{
    /// <summary>Never-null view over the unresolved fields.</summary>
    public IReadOnlyList<string> FieldList => Fields ?? Array.Empty<string>();
}

/// <summary>Aggregates the unresolved artist/target work recorded in a run's outcome journal.</summary>
public static class ArtistMetadataPendingWork
{
    /// <summary>One unresolved pair with the attempts already spent on it.</summary>
    public readonly record struct PendingPair(
        long ArtistId,
        string Target,
        IReadOnlyList<string> Fields,
        string Category,
        int Attempts);

    /// <summary>Stable attempt-counter key for one artist/target pair.</summary>
    public static string Key(long artistId, string target)
        => $"{artistId}|{target.Trim().ToLowerInvariant()}";

    /// <summary>
    /// Reads the surviving unresolved work from the latest outcome of each artist. A pair drops out as
    /// soon as its recorded fields are satisfied, so a satisfied retry cannot keep a counter alive.
    /// </summary>
    public static IReadOnlyList<PendingPair> Collect(
        IEnumerable<ArtistRunOutcomeRecord>? outcomes,
        IReadOnlyDictionary<string, int>? priorAttempts)
    {
        if (outcomes is null)
        {
            return Array.Empty<PendingPair>();
        }

        var merged = new Dictionary<string, (long ArtistId, string Target, List<string> Fields, string Category)>(StringComparer.Ordinal);
        foreach (var record in outcomes)
        {
            foreach (var work in record.PendingWorkList)
            {
                if (string.IsNullOrWhiteSpace(work.Target) || work.FieldList.Count == 0)
                {
                    continue;
                }

                var key = Key(record.ArtistId, work.Target);
                if (merged.TryGetValue(key, out var existing))
                {
                    foreach (var field in work.FieldList)
                    {
                        if (!existing.Fields.Contains(field, StringComparer.OrdinalIgnoreCase))
                        {
                            existing.Fields.Add(field);
                        }
                    }

                    continue;
                }

                merged[key] = (record.ArtistId, work.Target.Trim(), work.FieldList.ToList(), work.Category);
            }
        }

        var pairs = new List<PendingPair>(merged.Count);
        foreach (var (key, value) in merged)
        {
            var attempts = priorAttempts is not null && priorAttempts.TryGetValue(key, out var used) ? used : 0;
            pairs.Add(new PendingPair(value.ArtistId, value.Target, value.Fields, value.Category, attempts));
        }

        return pairs;
    }
}

public sealed record ArtistMetadataAutomationStatus(
    string? ActiveOperation,
    ArtistMetadataCacheStatus CacheRefresh,
    MetadataUpdaterStatusSnapshot TargetUpdate,
    DateTimeOffset? LastCacheRefreshUtc,
    DateTimeOffset? LastTargetUpdateUtc,
    DateTimeOffset? NextCacheRefreshUtc,
    DateTimeOffset? NextTargetUpdateUtc,
    string? ResumeNotice = null,
    string? RunId = null,
    string? RunOperation = null,
    string RunState = "idle",
    string? RunDisposition = null,
    DateTimeOffset? RunNextAttemptUtc = null,
    string? RunPendingNotice = null)
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
