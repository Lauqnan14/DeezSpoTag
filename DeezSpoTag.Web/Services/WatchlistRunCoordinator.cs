using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Runtime;
using DeezSpoTag.Services.Download.Queue;

namespace DeezSpoTag.Web.Services;

public enum WatchlistTriggerKind
{
    All,
    Artist,
    Playlist
}

public enum WatchlistTriggerStatus
{
    Accepted,
    Coalesced,
    Disabled
}

public sealed record WatchlistTriggerRequest(
    WatchlistTriggerKind Kind,
    string Source,
    string Identifier);

public sealed record WatchlistTriggerResult(WatchlistTriggerStatus Status)
{
    public bool Scheduled => Status is not WatchlistTriggerStatus.Disabled;
}

/// <summary>
/// Structured, platform-level snapshot progress for one cycle.
///
/// This exists so the UI can report one line per platform ("Spotify - 18/24 checked - 2 changed")
/// instead of one activity message per playlist head request. Per-playlist detail is deliberately
/// NOT summarised here: it stays available on each playlist card, in watchlist history, and in the
/// logs. Only actionable outcomes (failures, truncation) are surfaced individually.
/// </summary>
public sealed record WatchlistPlatformProgress(
    string Source,
    string Label,
    int TotalPlaylists,
    int HeadsChecked,
    int Unchanged,
    int Changed,
    int New,
    int RequiresExpansion,
    int Failed,
    int Completed = 0,
    int Incomplete = 0,
    long HeadPhaseMs = 0,
    long TotalMs = 0,
    DateTimeOffset? StartedUtc = null,
    DateTimeOffset? CompletedUtc = null)
{
    /// <summary>Human-readable one-line summary, e.g. "20 checked - 2 changed - 18 unchanged".</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string> { $"{HeadsChecked}/{TotalPlaylists} checked" };
            if (Changed > 0)
            {
                parts.Add($"{Changed} changed");
            }

            if (New > 0)
            {
                parts.Add($"{New} new");
            }

            if (RequiresExpansion > 0)
            {
                parts.Add($"{RequiresExpansion} need refresh");
            }

            if (Incomplete > 0)
            {
                parts.Add($"{Incomplete} incomplete");
            }

            if (Failed > 0)
            {
                parts.Add($"{Failed} failed");
            }

            return string.Join(" · ", parts);
        }
    }
}

public sealed record WatchlistRuntimeHealth(
    bool IsRunning,
    bool TriggerPending,
    DateTimeOffset? LastCycleStartedUtc,
    DateTimeOffset? LastCycleCompletedUtc,
    string? LastAdmissionBlockReason,
    int LastRecoveredClaimCount,
    int PendingReconciliationRequests = 0,
    IReadOnlyList<WatchlistPlatformProgress>? Platforms = null)
{
    /// <summary>Never null, so callers and serializers do not each need their own null handling.</summary>
    public IReadOnlyList<WatchlistPlatformProgress> PlatformProgress => Platforms ?? [];
}

public sealed record WatchlistRuntimeResetResult(
    LibraryRepository.WatchlistRuntimeCleanupResult Cleanup,
    WatchlistTriggerStatus TriggerStatus);

public readonly record struct WatchlistBatchGate(
    bool HasActiveDownloads,
    DateTimeOffset? LastBatchCompletedUtc,
    DateTimeOffset? NextRunUtc)
{
    public bool CanRun(DateTimeOffset nowUtc)
        => !HasActiveDownloads && (!NextRunUtc.HasValue || NextRunUtc.Value <= nowUtc);
}

public sealed class WatchlistRunCoordinator : BackgroundService
{
    private const string ArtistKind = "artist";
    private const string PlaylistKind = "playlist";
    private const string PlaylistWatchType = "playlist";
    private const string ArtistWatchType = "artist";
    private const int SourceCircuitFailureThreshold = 2;
    private const int SourceCircuitCooldownSeconds = 300;
    /// <summary>
    /// Upper bound on how many target-sync jobs the cycle-end drain may claim in one cycle.
    /// The drain previously ran until the queue was empty, so a large backlog (or a target
    /// server answering slowly) made cycle teardown wait on unbounded work and stopped the
    /// coordinator scheduling altogether. Jobs past the cap are never claimed, so they stay
    /// durably queued and the next cycle resumes them -- losing nothing, and guaranteeing the
    /// cycle always ends. Deliberately a job count and not a wall-clock budget so the amount
    /// of work per cycle does not vary with machine speed or provider latency.
    /// </summary>
    internal const int MaxDrainJobsPerCycle = 25;
    private readonly IServiceProvider _serviceProvider;
    private readonly BackgroundWorkCoordinator _workCoordinator;
    private readonly WatchlistRunSignal _runSignal;
    private readonly ILogger<WatchlistRunCoordinator> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _itemLocks = new();
    private readonly ConcurrentDictionary<string, int> _consecutiveFailures = new();
    private readonly string _reconciliationLeaseOwner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private DateTimeOffset _lastDestinationRepairUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastIdentityIndexRefreshUtc = DateTimeOffset.MinValue;
    private readonly object _runtimeHealthGate = new();
    private readonly object _activeCycleGate = new();
    private readonly SemaphoreSlim _cycleGate = new(1, 1);
    private CancellationTokenSource? _activeCycleCancellation;
    private int _runtimeResetRequested;
    private WatchlistRuntimeHealth _runtimeHealth = new(false, false, null, null, null, 0);

    public WatchlistRunCoordinator(
        IServiceProvider serviceProvider,
        BackgroundWorkCoordinator workCoordinator,
        WatchlistRunSignal runSignal,
        ILogger<WatchlistRunCoordinator> logger)
    {
        _serviceProvider = serviceProvider;
        _workCoordinator = workCoordinator;
        _runSignal = runSignal;
        _logger = logger;
    }

    public WatchlistRunCoordinator(
        IServiceProvider serviceProvider,
        ILogger<WatchlistRunCoordinator> logger)
        : this(
            serviceProvider,
            new BackgroundWorkCoordinator(),
            new WatchlistRunSignal(),
            logger)
    {
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Playlist watch service started.");
        var pathfinder = _serviceProvider.GetService<SpotifyPathfinderMetadataClient>();
        if (pathfinder is not null)
        {
            pathfinder.AuthenticationRecovered += HandleSpotifyAuthenticationRecoveredAsync;
        }

        try
        {
            await _workCoordinator.WaitForStartupGraceAsync(stoppingToken);
            await RecoverCoordinatorStateAsync(stoppingToken);
            UpdateRuntimeHealth(health => health with { IsRunning = false });

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await WaitForFullRunDeadlineAsync(stoppingToken);
                    await ExecuteRunAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            if (pathfinder is not null)
            {
                pathfinder.AuthenticationRecovered -= HandleSpotifyAuthenticationRecoveredAsync;
            }
        }

        _logger.LogInformation("Playlist watch service stopped.");
    }

    private async Task HandleSpotifyAuthenticationRecoveredAsync(
        SpotifyPathfinderMetadataClient.PathfinderAuthRecovery recovery,
        CancellationToken cancellationToken)
    {
        if (!IsWatchlistEnabled())
        {
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<LibraryRepository>();
        if (!repository.IsConfigured)
        {
            return;
        }

        var reconciler = scope.ServiceProvider.GetRequiredService<PlaylistWatchReconciler>();
        await repository.UpsertWatchlistSourceCircuitStateAsync(
            new LibraryRepository.WatchlistSourceCircuitStateUpsertInput(
                PlaylistWatchType,
                "spotify",
                IsOpen: false,
                OpenUntilUtc: null,
                Reason: null,
                Fingerprint: null,
                FailureCount: 0),
            cancellationToken);

        var recovered = 0;
        var playlists = await repository.GetPlaylistWatchlistAsync(cancellationToken);
        foreach (var playlist in playlists.Where(IsRecoverableSpotifyAuthenticationFailure))
        {
            await reconciler.UpdatePlaylistStateAsync(
                playlist.Source,
                playlist.SourceId,
                playlist.TrackCount,
                playlist.SnapshotId,
                WatchlistPlaylistState.Pending,
                lastRunMessage: null,
                nextAttemptUtc: null,
                consecutiveFailures: 0,
                cancellationToken: cancellationToken,
                touchLastChecked: false);
            ResetPlaylistRuntimeState(playlist.Source, playlist.SourceId);
            await repository.EnqueueWatchlistReconciliationRequestAsync(
                PlaylistKind,
                playlist.Source,
                playlist.SourceId,
                cancellationToken);
            recovered++;
        }

        if (recovered > 0)
        {
            _logger.LogInformation(
                "Spotify authentication recovered; cleared {PlaylistCount} stale playlist auth states and requested reconciliation. Previous code={FailureCode}, incident={IncidentId}.",
                recovered,
                recovery.RecoveredFailureCode ?? "unknown",
                recovery.RecoveredIncidentId ?? "unknown");
            _runSignal.Request(WatchlistWakeReason.Reconciliation);
        }
    }

    internal static bool IsRecoverableSpotifyAuthenticationFailure(PlaylistWatchlistDto playlist)
    {
        if (!string.Equals(NormalizeSource(playlist.Source), "spotify", StringComparison.Ordinal))
        {
            return false;
        }

        var status = (playlist.LastRunStatus ?? string.Empty).Trim().ToLowerInvariant();
        if (status is not ("source_failure" or "circuit_open" or "backoff"))
        {
            return false;
        }

        return (playlist.LastRunMessage ?? string.Empty).Contains("spotify_auth_", StringComparison.OrdinalIgnoreCase);
    }

    private TimeSpan GetWatchInterval()
    {
        using var scope = _serviceProvider.CreateScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<DeezSpoTagSettingsService>();
        return GetWatchInterval(settingsService.LoadSettings());
    }

    private static TimeSpan GetWatchInterval(DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings)
    {
        var seconds = settings.WatchPollIntervalSeconds;
        if (seconds < 1)
        {
            seconds = 1;
        }
        return TimeSpan.FromSeconds(seconds);
    }

    private async Task WaitForFullRunDeadlineAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var scope = _serviceProvider.CreateScope();
            var repository = scope.ServiceProvider.GetService<LibraryRepository>();
            if (repository == null || !repository.IsConfigured)
            {
                await Task.Delay(GetWatchInterval(), cancellationToken);
                return;
            }

            var scheduler = await repository.GetWatchlistSchedulerStateAsync(PlaylistWatchType, cancellationToken);
            var gate = await EvaluateBatchGateAsync(repository, scheduler?.CycleStartedUtc, cancellationToken);
            if (gate.HasActiveDownloads)
            {
                if (scheduler is not null
                    && !string.Equals(scheduler.CycleStatus, "waiting_downloads", StringComparison.OrdinalIgnoreCase))
                {
                    await repository.UpdateWatchlistCycleStateAsync(
                        PlaylistWatchType,
                        "waiting_downloads",
                        scheduler.CycleStartedUtc,
                        cycleCompletedUtc: null,
                        nextCycleUtc: null,
                        cancellationToken);
                }

                await ProcessWaitSignalAsync(
                    DateTimeOffset.UtcNow.AddSeconds(15),
                    TimeSpan.FromSeconds(15),
                    cancellationToken);
                continue;
            }

            if (scheduler is not null
                && string.Equals(scheduler.CycleStatus, "waiting_downloads", StringComparison.OrdinalIgnoreCase))
            {
                var completedUtc = gate.LastBatchCompletedUtc ?? DateTimeOffset.UtcNow;
                var nextRunUtc = completedUtc + GetWatchInterval();
                await repository.UpdateWatchlistCycleStateAsync(
                    PlaylistWatchType,
                    "completed",
                    scheduler.CycleStartedUtc,
                    completedUtc,
                    nextRunUtc,
                    cancellationToken);
                scheduler = await repository.GetWatchlistSchedulerStateAsync(PlaylistWatchType, cancellationToken);
            }

            if (scheduler is null
                || string.Equals(scheduler.CycleStatus, "running", StringComparison.OrdinalIgnoreCase)
                || !scheduler.NextCycleUtc.HasValue
                || scheduler.NextCycleUtc.Value <= DateTimeOffset.UtcNow)
            {
                return;
            }

            var deadlineUtc = scheduler.NextCycleUtc.Value;
            var nextTargetSyncUtc = await repository.GetNextWatchlistSyncJobDueUtcAsync(cancellationToken);
            var wakeUtc = nextTargetSyncUtc.HasValue && nextTargetSyncUtc.Value < deadlineUtc
                ? nextTargetSyncUtc.Value
                : deadlineUtc;
            await ProcessWaitSignalAsync(
                deadlineUtc,
                Max(TimeSpan.Zero, wakeUtc - DateTimeOffset.UtcNow),
                cancellationToken,
                wakeUtc < deadlineUtc ? WatchlistWakeReason.TargetSync : WatchlistWakeReason.ScheduledRefresh);
        }
    }

    private async Task ProcessWaitSignalAsync(
        DateTimeOffset workDeadlineUtc,
        TimeSpan delay,
        CancellationToken cancellationToken,
        WatchlistWakeReason timeoutReason = WatchlistWakeReason.Finalization)
    {
        var reason = await _runSignal.WaitAsync(delay, cancellationToken, timeoutReason);
        if (reason.HasFlag(WatchlistWakeReason.Finalization)
            || reason.HasFlag(WatchlistWakeReason.TargetSync))
        {
            await ProcessCountdownTargetWorkAsync(workDeadlineUtc, cancellationToken);
        }
    }

    private async Task<WatchlistBatchGate> EvaluateBatchGateAsync(
        LibraryRepository repository,
        DateTimeOffset? batchStartedUtc,
        CancellationToken cancellationToken)
    {
        var claims = await repository.GetAllPlaylistWatchDownloadClaimsAsync(status: null, cancellationToken);
        var queueRepository = _serviceProvider.GetRequiredService<DownloadQueueRepository>();
        var queueItems = await queueRepository.GetTasksAsync(cancellationToken: cancellationToken);
        return EvaluateBatchGate(claims, queueItems, batchStartedUtc, DateTimeOffset.UtcNow, GetWatchInterval());
    }

    internal static WatchlistBatchGate EvaluateBatchGate(
        IReadOnlyList<PlaylistWatchDownloadClaimDto> claims,
        IReadOnlyList<DownloadQueueItem> queueItems,
        DateTimeOffset? batchStartedUtc,
        DateTimeOffset nowUtc,
        TimeSpan watchInterval)
    {
        var queueByUuid = queueItems.ToDictionary(static item => item.QueueUuid, StringComparer.OrdinalIgnoreCase);
        var active = claims.Any(claim =>
            queueByUuid.TryGetValue(claim.QueueUuid, out var item)
            && WatchlistEngine.IsPendingWatchClaimStillOwnedByQueue(item, nowUtc));
        if (active)
        {
            return new WatchlistBatchGate(true, null, null);
        }

        if (!batchStartedUtc.HasValue)
        {
            return new WatchlistBatchGate(false, null, null);
        }

        var completionCandidates = new List<DateTimeOffset>();
        foreach (var claim in claims)
        {
            if (queueByUuid.TryGetValue(claim.QueueUuid, out var item)
                && item.CreatedAt >= batchStartedUtc.Value)
            {
                completionCandidates.Add(item.UpdatedAt);
            }
            else if (claim.UpdatedAt >= batchStartedUtc.Value)
            {
                completionCandidates.Add(claim.UpdatedAt);
            }
        }

        if (completionCandidates.Count == 0)
        {
            return new WatchlistBatchGate(false, null, null);
        }

        var completedUtc = completionCandidates.Max();
        return new WatchlistBatchGate(false, completedUtc, completedUtc + watchInterval);
    }

    private static TimeSpan Max(TimeSpan left, TimeSpan right)
        => left >= right ? left : right;

    private async Task ProcessCountdownTargetWorkAsync(
        DateTimeOffset fullRunDeadlineUtc,
        CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<LibraryRepository>();
        if (!repository.IsConfigured || !IsWatchlistEnabled())
        {
            return;
        }

        var coordinatorWork = scope.ServiceProvider.GetService<WatchlistPostDownloadSyncService>();
        if (coordinatorWork is null)
        {
            return;
        }

        await coordinatorWork.ProcessFinalizationWorkAsync(
            cancellationToken,
            shouldStop: () => DateTimeOffset.UtcNow >= fullRunDeadlineUtc);
        await coordinatorWork.ProcessTargetSyncWorkAsync(
            TargetSyncBudget.DrainBounded(
                WatchlistSyncJobKind.All,
                MaxDrainJobsPerCycle,
                shouldStop: () => DateTimeOffset.UtcNow >= fullRunDeadlineUtc),
            cancellationToken);
    }

    private static async Task RunActiveCycleTargetWorkAsync(
        WatchlistPostDownloadSyncService coordinatorWork,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await coordinatorWork.ProcessFinalizationWorkAsync(
                cancellationToken,
                shouldStop: () => cancellationToken.IsCancellationRequested);
            var processed = await coordinatorWork.ProcessTargetSyncWorkAsync(
                TargetSyncBudget.DrainAll(
                    WatchlistSyncJobKind.All,
                    shouldStop: () => cancellationToken.IsCancellationRequested),
                cancellationToken);
            if (processed == 0)
            {
                // Idle backoff: an empty drain is the common case during long cycles, and polling
                // claims every second only adds SQLite chatter. Jobs enqueued mid-cycle (for
                // example by inline membership work) wait at most this long before draining.
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
            }
        }
    }

    public Task<WatchlistTriggerResult> TriggerRunOnceAsync(CancellationToken cancellationToken = default)
        => TriggerAsync(new WatchlistTriggerRequest(WatchlistTriggerKind.All, string.Empty, string.Empty), cancellationToken);

    public async Task<WatchlistTriggerResult> StartEnabledWatchlistAsync(CancellationToken cancellationToken = default)
    {
        if (!IsWatchlistEnabled())
        {
            return new WatchlistTriggerResult(WatchlistTriggerStatus.Disabled);
        }

        await _cycleGate.WaitAsync(cancellationToken);
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<LibraryRepository>();
            if (!repository.IsConfigured)
            {
                return new WatchlistTriggerResult(WatchlistTriggerStatus.Disabled);
            }

            var requestAccepted = await repository.EnqueueWatchlistReconciliationRequestAsync(
                "all",
                string.Empty,
                string.Empty,
                cancellationToken);
            await repository.UpdateWatchlistCycleStateAsync(
                PlaylistWatchType,
                "due",
                cycleStartedUtc: null,
                cycleCompletedUtc: null,
                nextCycleUtc: DateTimeOffset.UtcNow,
                cancellationToken);
            _runSignal.Request(WatchlistWakeReason.Reset);
            return new WatchlistTriggerResult(
                requestAccepted ? WatchlistTriggerStatus.Accepted : WatchlistTriggerStatus.Coalesced);
        }
        finally
        {
            _cycleGate.Release();
        }
    }

    public Task<WatchlistTriggerResult> TriggerArtistOnceAsync(long artistId, CancellationToken cancellationToken = default)
        => TriggerAsync(
            new WatchlistTriggerRequest(WatchlistTriggerKind.Artist, ArtistKind, artistId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            cancellationToken);

    public Task<WatchlistTriggerResult> TriggerPlaylistOnceAsync(
        string source,
        string sourceId,
        CancellationToken cancellationToken = default)
        => TriggerAsync(new WatchlistTriggerRequest(WatchlistTriggerKind.Playlist, source, sourceId), cancellationToken);

    public async Task<WatchlistTriggerResult> TriggerAsync(
        WatchlistTriggerRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!IsWatchlistEnabled())
        {
            return new WatchlistTriggerResult(WatchlistTriggerStatus.Disabled);
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<LibraryRepository>();
        if (!repository.IsConfigured)
        {
            return new WatchlistTriggerResult(WatchlistTriggerStatus.Disabled);
        }
        var requestAccepted = await repository.EnqueueWatchlistReconciliationRequestAsync(
            request.Kind switch
            {
                WatchlistTriggerKind.Playlist => PlaylistKind,
                WatchlistTriggerKind.Artist => ArtistKind,
                _ => "all"
            },
            request.Source,
            request.Identifier,
            cancellationToken);
        _runSignal.Request(WatchlistWakeReason.Reconciliation);
        return new WatchlistTriggerResult(
            requestAccepted ? WatchlistTriggerStatus.Accepted : WatchlistTriggerStatus.Coalesced);
    }

    private bool IsWatchlistEnabled()
    {
        using var scope = _serviceProvider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<DeezSpoTagSettingsService>().LoadSettings().WatchEnabled;
    }

    public void ResetPlaylistRuntimeState(string source, string sourceId)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(sourceId))
        {
            return;
        }

        var normalizedSource = NormalizeSource(source);
        var normalizedSourceId = sourceId.Trim();
        var key = $"playlist:{normalizedSource}:{normalizedSourceId}";
        _consecutiveFailures.TryRemove(key, out _);
    }

    public WatchlistRuntimeHealth GetRuntimeHealth()
    {
        lock (_runtimeHealthGate)
        {
            return _runtimeHealth with { TriggerPending = _runSignal.IsPending };
        }
    }

    public void ResetPlaylistRuntimeStateForAll(IReadOnlyCollection<PlaylistWatchlistDto> playlists)
    {
        if (playlists == null || playlists.Count == 0)
        {
            return;
        }

        foreach (var playlist in playlists)
        {
            if (playlist == null)
            {
                continue;
            }

            ResetPlaylistRuntimeState(playlist.Source, playlist.SourceId);
        }
    }

    public async Task ResetSchedulerStateAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<LibraryRepository>();
        if (!repository.IsConfigured)
        {
            return;
        }

        await SaveSchedulerStateAsync(
            repository,
            activeSource: null,
            activeSourceId: null,
            activeStartedUtc: null,
            lastProgressUtc: DateTimeOffset.UtcNow,
            cancellationToken);
    }

    public async Task ResetSourceCircuitAsync(string source, CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<LibraryRepository>();
        if (!repository.IsConfigured)
        {
            return;
        }

        await repository.UpsertWatchlistSourceCircuitStateAsync(
            new LibraryRepository.WatchlistSourceCircuitStateUpsertInput(
                PlaylistWatchType,
                NormalizeSource(source),
                IsOpen: false,
                OpenUntilUtc: null,
                Reason: null,
                Fingerprint: null,
                FailureCount: 0),
            cancellationToken);
    }

    public async Task<WatchlistRuntimeResetResult> ResetRuntimeAsync(CancellationToken cancellationToken)
        => await ResetRuntimeCoreAsync(
            preserveDownloadFinalization: false,
            restartWhenEnabled: true,
            cancellationToken);

    public async Task DisableWatchlistAsync(CancellationToken cancellationToken)
        => _ = await ResetRuntimeCoreAsync(
            preserveDownloadFinalization: true,
            restartWhenEnabled: false,
            cancellationToken);

    private async Task<WatchlistRuntimeResetResult> ResetRuntimeCoreAsync(
        bool preserveDownloadFinalization,
        bool restartWhenEnabled,
        CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _runtimeResetRequested, 1);
        lock (_activeCycleGate)
        {
            _activeCycleCancellation?.Cancel();
        }
        _runSignal.Request(WatchlistWakeReason.Reset);

        var acquired = false;
        try
        {
            await _cycleGate.WaitAsync(cancellationToken);
            acquired = true;
            using var scope = _serviceProvider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<LibraryRepository>();
            var cleanup = preserveDownloadFinalization
                ? await repository.ClearDisabledWatchlistRuntimeAsync(cancellationToken)
                : await repository.ClearWatchlistRuntimeAsync(cancellationToken);

            _consecutiveFailures.Clear();
            _lastDestinationRepairUtc = DateTimeOffset.MinValue;
            _lastIdentityIndexRefreshUtc = DateTimeOffset.MinValue;
            UpdateRuntimeHealth(_ => new WatchlistRuntimeHealth(false, false, null, DateTimeOffset.UtcNow, null, 0));

            var triggerStatus = WatchlistTriggerStatus.Disabled;
            if (restartWhenEnabled && IsWatchlistEnabled())
            {
                var accepted = await repository.EnqueueWatchlistReconciliationRequestAsync(
                    "all",
                    string.Empty,
                    string.Empty,
                    cancellationToken);
                await repository.UpdateWatchlistCycleStateAsync(
                    PlaylistWatchType,
                    "due",
                    cycleStartedUtc: null,
                    cycleCompletedUtc: null,
                    nextCycleUtc: DateTimeOffset.UtcNow,
                    cancellationToken);
                triggerStatus = accepted ? WatchlistTriggerStatus.Accepted : WatchlistTriggerStatus.Coalesced;
            }

            return new WatchlistRuntimeResetResult(cleanup, triggerStatus);
        }
        finally
        {
            Interlocked.Exchange(ref _runtimeResetRequested, 0);
            if (acquired)
            {
                _cycleGate.Release();
            }
        }
    }

    [SuppressMessage("Major Code Smell", "S3776", Justification = "Watch cycle entrypoint intentionally centralizes lock, failure handling, and lifecycle semantics.")]
    private Task RunOnceAsync(CancellationToken stoppingToken)
        => ExecuteRunAsync(stoppingToken);

    private async Task ExecuteRunAsync(CancellationToken stoppingToken)
    {
        await _cycleGate.WaitAsync(stoppingToken);
        using var cycleCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        lock (_activeCycleGate)
        {
            _activeCycleCancellation = cycleCancellation;
        }
        if (Volatile.Read(ref _runtimeResetRequested) != 0)
        {
            cycleCancellation.Cancel();
        }
        UpdateRuntimeHealth(health => health with
        {
            IsRunning = true,
            LastCycleStartedUtc = DateTimeOffset.UtcNow,
            LastAdmissionBlockReason = null,
            LastRecoveredClaimCount = 0
        });
        var cycleStartedUtc = DateTimeOffset.UtcNow;
        var cycleCompleted = false;
        try
        {
            using (var scope = _serviceProvider.CreateScope())
            {
                var repository = scope.ServiceProvider.GetRequiredService<LibraryRepository>();
                if (repository.IsConfigured)
                {
                    await repository.UpdateWatchlistCycleStateAsync(
                        PlaylistWatchType,
                        "running",
                        cycleStartedUtc,
                        cycleCompletedUtc: null,
                        nextCycleUtc: null,
                        cycleCancellation.Token);
                }
            }
            await RunOneWatchCycleAsync(cycleCancellation.Token);
            cycleCompleted = true;
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Watchlist cycle canceled for runtime reset.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Playlist watch run failed.");
        }
        finally
        {
            var cycleCompletedUtc = DateTimeOffset.UtcNow;
            if (!stoppingToken.IsCancellationRequested
                && Volatile.Read(ref _runtimeResetRequested) == 0
                && IsWatchlistEnabled())
            {
                using var scope = _serviceProvider.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<LibraryRepository>();
                if (repository.IsConfigured)
                {
                    var batchGate = await EvaluateBatchGateAsync(repository, cycleStartedUtc, stoppingToken);
                    var completionAnchorUtc = batchGate.LastBatchCompletedUtc ?? cycleCompletedUtc;
                    var nextCycleUtc = batchGate.LastBatchCompletedUtc.HasValue
                        ? completionAnchorUtc + GetWatchInterval()
                        : cycleCompletedUtc + GetWatchInterval();
                    await repository.UpdateWatchlistCycleStateAsync(
                        PlaylistWatchType,
                        batchGate.HasActiveDownloads
                            ? "waiting_downloads"
                            : cycleCompleted ? "completed" : "failed",
                        cycleStartedUtc,
                        batchGate.HasActiveDownloads ? null : completionAnchorUtc,
                        batchGate.HasActiveDownloads ? null : nextCycleUtc,
                        stoppingToken);
                }
            }
            lock (_activeCycleGate)
            {
                if (ReferenceEquals(_activeCycleCancellation, cycleCancellation))
                {
                    _activeCycleCancellation = null;
                }
            }
            UpdateRuntimeHealth(health => health with
            {
                IsRunning = false,
                LastCycleCompletedUtc = cycleCompletedUtc
            });
            _cycleGate.Release();
        }
    }

    private async Task RunOneWatchCycleAsync(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<DeezSpoTagSettingsService>();
        var settings = settingsService.LoadSettings();
        if (!settings.WatchEnabled)
        {
            var disabledRepository = scope.ServiceProvider.GetRequiredService<LibraryRepository>();
            if (disabledRepository.IsConfigured)
            {
                var finalizationWork = scope.ServiceProvider.GetService<WatchlistPostDownloadSyncService>();
                if (finalizationWork is not null)
                {
                    await finalizationWork.ProcessFinalizationWorkAsync(stoppingToken);
                }
                var cleanup = await disabledRepository.ClearDisabledWatchlistRuntimeAsync(stoppingToken);
                _consecutiveFailures.Clear();
                // Schedule the next cycle even while disabled: the end-of-cycle scheduling in
                // ExecuteRunAsync is gated on the watchlist being enabled, and without a
                // next_cycle_utc the wait phase returns immediately — the coordinator would
                // otherwise busy-loop cleanup every few milliseconds for as long as the
                // watchlist stays disabled.
                await disabledRepository.UpdateWatchlistCycleStateAsync(
                    PlaylistWatchType,
                    "completed",
                    cycleStartedUtc: null,
                    cycleCompletedUtc: DateTimeOffset.UtcNow,
                    nextCycleUtc: DateTimeOffset.UtcNow + GetWatchInterval(settings),
                    stoppingToken);
                if (_logger.IsEnabled(LogLevel.Information)
                    && (cleanup.ReconciliationRequestsDeleted > 0
                        || cleanup.SyncJobsDeleted > 0
                        || cleanup.FinalizationOutboxDeleted > 0
                        || cleanup.ClaimsDeleted > 0
                        || cleanup.SchedulerRowsDeleted > 0
                        || cleanup.SourceCircuitsDeleted > 0
                        || cleanup.TargetCircuitsDeleted > 0
                        || cleanup.PlaylistStatesDeleted > 0
                        || cleanup.ArtistStatesDeleted > 0))
                {
                    _logger.LogInformation(
                        "Watchlist disabled cleanup applied: reconciliationRequests={ReconciliationRequests}, syncJobs={SyncJobs}, finalizationOutbox={FinalizationOutbox}, claims={Claims}, schedulerRows={SchedulerRows}, sourceCircuits={SourceCircuits}, targetCircuits={TargetCircuits}, playlistStates={PlaylistStates}, artistStates={ArtistStates}.",
                        cleanup.ReconciliationRequestsDeleted,
                        cleanup.SyncJobsDeleted,
                        cleanup.FinalizationOutboxDeleted,
                        cleanup.ClaimsDeleted,
                        cleanup.SchedulerRowsDeleted,
                        cleanup.SourceCircuitsDeleted,
                        cleanup.TargetCircuitsDeleted,
                        cleanup.PlaylistStatesDeleted,
                        cleanup.ArtistStatesDeleted);
                }
            }
            _logger.LogDebug("Watchlist disabled in settings.");
            return;
        }

        var queueAdmission = scope.ServiceProvider.GetRequiredService<WatchlistQueueAdmissionService>();
        var repository = scope.ServiceProvider.GetRequiredService<LibraryRepository>();
        if (!repository.IsConfigured)
        {
            _logger.LogDebug("Watchlist skipped - library DB not configured.");
            return;
        }
        await RecoverCoordinatorStateAsync(stoppingToken);

        var pendingRequestCount = await repository.GetWatchlistReconciliationRequestCountAsync(stoppingToken);
        UpdateRuntimeHealth(health => health with { PendingReconciliationRequests = pendingRequestCount });

        var reconciliationRequests = await repository.ClaimDueWatchlistReconciliationRequestsAsync(
            1000,
            TimeSpan.FromMinutes(15),
            _reconciliationLeaseOwner,
            stoppingToken);
        using var leaseRenewalCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var leaseRenewal = RenewReconciliationLeasesAsync(repository, leaseRenewalCancellation.Token);
        var queueRepository = scope.ServiceProvider.GetRequiredService<DownloadQueueRepository>();
        var orchestrationService = scope.ServiceProvider.GetService<DownloadOrchestrationService>();
        var queueGate = orchestrationService is null
            ? await queueAdmission.EvaluateQueueGateAsync(queueRepository, stoppingToken)
            : await queueAdmission.EvaluateQueueGateAsync(queueRepository, orchestrationService, stoppingToken);
        var queueAdmissionToken = 0L;
        if (queueGate.Allowed)
        {
            UpdateRuntimeHealth(health => health with { LastAdmissionBlockReason = null });
            queueAdmissionToken = queueAdmission.BeginRun(Math.Max(1, settings.WatchMaxItemsPerRun));
        }
        else
        {
            UpdateRuntimeHealth(health => health with { LastAdmissionBlockReason = queueGate.Message });
        }

        using var activeTargetWorkCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var targetSyncWork = scope.ServiceProvider.GetService<WatchlistPostDownloadSyncService>();
        var activeTargetWork = targetSyncWork is null
            ? Task.CompletedTask
            : RunActiveCycleTargetWorkAsync(targetSyncWork, activeTargetWorkCancellation.Token);
        try
        {
            await RunWatchCycleCoreAsync(
                scope.ServiceProvider,
                settings,
                reconciliationRequests,
                queueGate.Allowed,
                stoppingToken);
        }
        finally
        {
            activeTargetWorkCancellation.Cancel();
            try
            {
                await activeTargetWork;
            }
            catch (OperationCanceledException) when (activeTargetWorkCancellation.IsCancellationRequested)
            {
            }
            leaseRenewalCancellation.Cancel();
            try
            {
                await leaseRenewal;
            }
            catch (OperationCanceledException) when (leaseRenewalCancellation.IsCancellationRequested)
            {
            }
            if (queueAdmissionToken != 0)
            {
                queueAdmission.EndRun(queueAdmissionToken);
            }
        }
    }

    private async Task RecoverCoordinatorStateAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<LibraryRepository>();
        if (!repository.IsConfigured)
        {
            return;
        }

        var smoothSyncRecoveryApplied = await repository.ApplyWatchlistSmoothSyncRecoveryAsync(cancellationToken);
        if (smoothSyncRecoveryApplied)
        {
            ResetPlaylistRuntimeStateForAll(await repository.GetPlaylistWatchlistAsync(cancellationToken));
            await repository.EnqueueWatchlistReconciliationRequestAsync("all", null, null, cancellationToken);
            _logger.LogWarning("Applied Watchlist smooth-sync recovery to clear stale backoff, identity jobs, and identity circuits.");
        }

        var membershipCatchUpJobs = await repository.EnqueueMembershipCatchUpForIncompletePlaylistsAsync(cancellationToken);
        if (membershipCatchUpJobs > 0)
        {
            _logger.LogInformation(
                "Enqueued {JobCount} Watchlist membership catch-up job(s) for library tracks still missing from a target playlist.",
                membershipCatchUpJobs);
            _runSignal.Request(WatchlistWakeReason.TargetSync);
        }

        var staleWorkRecovered = await repository.RecoverStaleWatchlistWorkAsync(cancellationToken);
        var expiredTargetCircuitsClosed = await repository.CloseExpiredWatchlistTargetCircuitsAsync(cancellationToken);
        var expiredTargetJobsRecovered = await repository.RepairWatchlistSyncBacklogAsync(
            WatchlistPostDownloadSyncService.MaxSyncAttempts,
            cancellationToken);
        var drift = await repository.DetectWatchlistStateDriftAsync(
            WatchlistPostDownloadSyncService.MaxSyncAttempts,
            cancellationToken);
        if (drift.HasDrift && _logger.IsEnabled(LogLevel.Warning))
        {
            _logger.LogWarning(
                "Watchlist state drift detected: appliedWithoutMembership={AppliedWithoutMembership}, membershipWithoutApplied={MembershipWithoutApplied}, orphanedMembership={OrphanedMembership}, membershipForUnconfiguredTarget={MembershipForUnconfiguredTarget}, blockedBelowAttemptCap={BlockedBelowAttemptCap}",
                drift.AppliedWithoutMembership,
                drift.MembershipWithoutApplied,
                drift.OrphanedMembership,
                drift.MembershipForUnconfiguredTarget,
                drift.BlockedBelowAttemptCap);
        }
        var recoveredClaims = await scope.ServiceProvider
            .GetRequiredService<PlaylistWatchReconciler>()
            .RecoverInvalidPendingWatchClaimsAsync(cancellationToken);
        UpdateRuntimeHealth(health => health with
        {
            LastRecoveredClaimCount = recoveredClaims
        });

        if (staleWorkRecovered > 0)
        {
            _logger.LogWarning(
                "Recovered {Count} Watchlist items whose persisted execution deadlines expired.",
                staleWorkRecovered);
        }
        if (expiredTargetJobsRecovered > 0)
        {
            _logger.LogInformation(
                "Recovered {Count} expired or obsolete Watchlist target synchronization job(s).",
                expiredTargetJobsRecovered);
        }
        if (expiredTargetCircuitsClosed > 0)
        {
            _logger.LogInformation(
                "Closed {Count} expired Watchlist target synchronization circuit(s).",
                expiredTargetCircuitsClosed);
        }

        if (smoothSyncRecoveryApplied || staleWorkRecovered > 0 || expiredTargetJobsRecovered > 0 || expiredTargetCircuitsClosed > 0 || recoveredClaims > 0)
        {
            _runSignal.Request(WatchlistWakeReason.TargetSync | WatchlistWakeReason.Reconciliation);
        }
    }

    private async Task RepairLegacyApplePlaylistStorefrontsAsync(
        LibraryRepository repository,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
    {
        var storefront = NormalizeAppleStorefront(settings.AppleMusic?.Storefront);
        if (storefront is null)
        {
            return;
        }

        var repairedSourceIds = await repository.BackfillLegacyApplePlaylistStorefrontAsync(
            storefront,
            cancellationToken);
        if (repairedSourceIds.Count == 0)
        {
            return;
        }

        await repository.UpsertWatchlistSourceCircuitStateAsync(
            new LibraryRepository.WatchlistSourceCircuitStateUpsertInput(
                PlaylistWatchType,
                "apple",
                IsOpen: false,
                OpenUntilUtc: null,
                Reason: null,
                Fingerprint: null,
                FailureCount: 0),
            cancellationToken);

        foreach (var sourceId in repairedSourceIds)
        {
            var key = $"playlist:apple:{sourceId}";
            _consecutiveFailures.TryRemove(key, out _);
            await repository.EnqueueWatchlistReconciliationRequestAsync(
                PlaylistKind,
                "apple",
                sourceId,
                cancellationToken);
        }

        _logger.LogInformation(
            "Repaired persisted Apple storefront for {PlaylistCount} legacy monitored playlist(s).",
            repairedSourceIds.Count);
    }

    private static string? NormalizeAppleStorefront(string? storefront)
    {
        var normalized = (storefront ?? string.Empty).Trim().ToLowerInvariant();
        return normalized.Length is >= 2 and <= 5
               && normalized.All(character => char.IsAsciiLetter(character) || character == '-')
            ? normalized
            : null;
    }

    private async Task RenewReconciliationLeasesAsync(
        LibraryRepository repository,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
            if (await repository.RenewClaimedWatchlistReconciliationRequestsAsync(
                    _reconciliationLeaseOwner,
                    TimeSpan.FromMinutes(15),
                    cancellationToken) == 0)
            {
                return;
            }
        }
    }

    private void UpdateRuntimeHealth(Func<WatchlistRuntimeHealth, WatchlistRuntimeHealth> update)
    {
        lock (_runtimeHealthGate)
        {
            _runtimeHealth = update(_runtimeHealth);
        }
    }

    private async Task<PlaylistRunResult> RunWatchCycleCoreAsync(
        IServiceProvider serviceProvider,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        IReadOnlyList<WatchlistReconciliationRequestDto> reconciliationRequests,
        bool queueAdmissionAllowed,
        CancellationToken stoppingToken)
    {
        ThrowIfWatchlistStopped(stoppingToken);
        var repository = serviceProvider.GetRequiredService<LibraryRepository>();
        if (!repository.IsConfigured)
        {
            _logger.LogDebug("Watchlist skipped - library DB not configured.");
            return PlaylistRunResult.Empty;
        }

        var profileResolutionService = serviceProvider.GetRequiredService<AutoTagProfileResolutionService>();
        await TryRepairWatchlistDestinationIntegrityAsync(repository, profileResolutionService, stoppingToken);
        ThrowIfWatchlistStopped(stoppingToken);
        await RepairLegacyApplePlaylistStorefrontsAsync(repository, settings, stoppingToken);
        await RefreshWatchlistIdentityIndexAsync(
            serviceProvider,
            profileResolutionService,
            stoppingToken);
        ThrowIfWatchlistStopped(stoppingToken);
        var playlistItems = await FilterEligibleWatchItemsAsync(
            BuildPlaylistWatchItems(await repository.GetPlaylistWatchlistAsync(stoppingToken)),
            repository,
            stoppingToken);
        var artistItems = await FilterEligibleWatchItemsAsync(
            BuildArtistWatchItems(await repository.GetWatchlistAsync(stoppingToken)),
            repository,
            stoppingToken);
        var allItems = BuildCombinedWatchItems(playlistItems, artistItems);
        if (allItems.Count == 0)
        {
            CleanupStaleState(Array.Empty<WatchItem>());
            await repository.CompleteClaimedWatchlistReconciliationRequestsAsync(
                reconciliationRequests,
                _reconciliationLeaseOwner,
                stoppingToken);
            var remainingWhenEmpty = await repository.GetWatchlistReconciliationRequestCountAsync(stoppingToken);
            UpdateRuntimeHealth(health => health with { PendingReconciliationRequests = remainingWhenEmpty });
            return PlaylistRunResult.Empty;
        }

        CleanupStaleState(allItems);
        var playlistRunResult = await ProcessPlaylistWatchItemsAsync(
            playlistItems,
            settings,
            repository,
            serviceProvider,
            stoppingToken);
        ThrowIfWatchlistStopped(stoppingToken);
        if (playlistRunResult.AbortedRun)
        {
            return playlistRunResult;
        }

        ThrowIfWatchlistStopped(stoppingToken);
        var processedArtistIds = await ProcessArtistWatchItemsAsync(
            artistItems,
            playlistItems,
            settings,
            serviceProvider,
            stoppingToken);
        if (queueAdmissionAllowed)
        {
            ThrowIfWatchlistStopped(stoppingToken);
            var reconciler = serviceProvider.GetRequiredService<PlaylistWatchReconciler>();
            await repository.ReconcilePlaylistWatchMissingTracksWithLibraryAsync(stoppingToken);
            var playlists = playlistItems
                .Select(static item => item.Playlist)
                .Where(static playlist => playlist is not null)
                .Select(static playlist => playlist!)
                .ToList();
            // Admission waits for the missing table to fill the run quota, except when every
            // monitored playlist and artist has already been visited and still cannot fill it.
            // Playlist and artist rows share this pass so a below-quota remainder is judged
            // against the full ledger, not dumped after playlists and before artist discovery.
            await reconciler.AdmitDueMissingTracksFromLedgerAsync(playlists, stoppingToken);
        }
        var completedRequests = reconciliationRequests.Where(request =>
                request.Kind == "all"
                    ? playlistRunResult.ProcessedKeys.Count == playlistItems.Count
                      && processedArtistIds.Count == artistItems.Count
                    : request.Kind == PlaylistKind
                    ? !playlistItems.Any(item => string.Equals(
                          item.Key,
                          $"playlist:{NormalizeSource(request.Source)}:{request.Identifier}",
                          StringComparison.Ordinal))
                      || playlistRunResult.ProcessedKeys.Contains($"playlist:{NormalizeSource(request.Source)}:{request.Identifier}")
                    : request.Kind != ArtistKind
                      || !long.TryParse(request.Identifier, out var artistId)
                      || !artistItems.Any(item => item.Artist?.ArtistId == artistId)
                      || processedArtistIds.Contains(artistId)).ToList();
        await repository.CompleteClaimedWatchlistReconciliationRequestsAsync(
            completedRequests,
            _reconciliationLeaseOwner,
            stoppingToken);
        var retryRequests = reconciliationRequests.Except(completedRequests).ToList();
        await repository.RetryClaimedWatchlistReconciliationRequestsAsync(
            retryRequests,
            _reconciliationLeaseOwner,
            "Reconciliation did not reach a successful terminal outcome.",
            stoppingToken);
        var remainingRequests = await repository.GetWatchlistReconciliationRequestCountAsync(stoppingToken);
        UpdateRuntimeHealth(health => health with { PendingReconciliationRequests = remainingRequests });
        return playlistRunResult;
    }

    private void ThrowIfWatchlistStopped(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsWatchlistEnabled())
        {
            throw new OperationCanceledException("Watchlist was disabled.", cancellationToken);
        }
    }

    private async Task RefreshWatchlistIdentityIndexAsync(
        IServiceProvider serviceProvider,
        AutoTagProfileResolutionService profileResolutionService,
        CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow - _lastIdentityIndexRefreshUtc < TimeSpan.FromMinutes(10))
        {
            return;
        }

        var ingestionService = serviceProvider.GetService<KnownLibraryFileIngestionService>();
        var folderIds = await ResolveWatchlistDestinationFolderIdsAsync(
            profileResolutionService,
            cancellationToken);
        if (ingestionService is not null && folderIds.Count > 0)
        {
            var repository = serviceProvider.GetRequiredService<LibraryRepository>();
            var folders = (await repository.GetFoldersAsync(cancellationToken))
                .Where(folder => folder.Enabled && folderIds.Contains(folder.Id) && Directory.Exists(folder.RootPath))
                .ToList();
            var missingFilesByFolder = new Dictionary<long, List<string>>();
            foreach (var folder in folders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var indexedFiles = await repository.GetLocalScanFileStatesAsync(folder.Id, cancellationToken);
                try
                {
                    var missingFiles = Directory
                        .EnumerateFiles(folder.RootPath, "*", SearchOption.AllDirectories)
                        .Where(KnownLibraryFilePathSet.IsExistingAudioFile)
                        .Select(Path.GetFullPath)
                        .Where(path => !indexedFiles.ContainsKey(path))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (missingFiles.Count > 0)
                    {
                        missingFilesByFolder[folder.Id] = missingFiles;
                    }
                }
                catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
                {
                    _logger.LogWarning(ex, "Watchlist identity index inspection failed for folder {FolderId}.", folder.Id);
                }
            }

            if (missingFilesByFolder.Count > 0)
            {
                await ingestionService.IngestAndVerifyAsync(missingFilesByFolder, cancellationToken);
            }
        }

        _lastIdentityIndexRefreshUtc = DateTimeOffset.UtcNow;
    }

    private async Task TryRepairWatchlistDestinationIntegrityAsync(
        LibraryRepository repository,
        AutoTagProfileResolutionService profileResolutionService,
        CancellationToken stoppingToken)
    {
        if ((DateTimeOffset.UtcNow - _lastDestinationRepairUtc) < TimeSpan.FromSeconds(1))
        {
            return;
        }

        var validFolderIds = await ResolveWatchlistDestinationFolderIdsAsync(profileResolutionService, stoppingToken);
        var repairResult = await repository.RepairWatchlistDestinationEligibilityAsync(validFolderIds, stoppingToken);
        _lastDestinationRepairUtc = DateTimeOffset.UtcNow;
        if ((repairResult.PlaylistPreferencesUpdated <= 0 && repairResult.ArtistPreferencesUpdated <= 0)
            || !_logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        _logger.LogInformation(
            "Watchlist destination integrity repair applied: playlistPreferencesUpdated={PlaylistUpdated}, artistPreferencesUpdated={ArtistUpdated}",
            repairResult.PlaylistPreferencesUpdated,
            repairResult.ArtistPreferencesUpdated);
    }

    private static async Task<HashSet<long>> ResolveWatchlistDestinationFolderIdsAsync(
        AutoTagProfileResolutionService profileResolutionService,
        CancellationToken cancellationToken)
        => await WatchlistDestinationFolderResolver.GetValidFolderIdsAsync(profileResolutionService, cancellationToken);

    private static List<WatchItem> BuildCombinedWatchItems(
        IReadOnlyList<WatchItem> playlistItems,
        IReadOnlyList<WatchItem> artistItems)
    {
        var items = new List<WatchItem>(playlistItems.Count + artistItems.Count);
        items.AddRange(playlistItems);
        items.AddRange(artistItems);
        return items;
    }

    [SuppressMessage("Major Code Smell", "S3776", Justification = "Playlist watch scheduler loop preserves queue budget, backoff, and circuit-breaker behavior in one control flow.")]
    /// <summary>
    /// Phase 1 of the platform-oriented workflow. Fetches every monitored playlist head grouped by
    /// platform, with bounded per-provider concurrency and per-playlist failure isolation. Returns a
    /// map keyed by "source:sourceId" so the serial reconciliation loop can reuse each head instead
    /// of re-fetching it one playlist at a time.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, PlaylistHeadSnapshot>> DiscoverPlatformHeadsAsync(
        IServiceProvider serviceProvider,
        LibraryRepository repository,
        IReadOnlyList<PlaylistWatchlistDto> playlists,
        CancellationToken cancellationToken)
    {
        var emptyHeads = new Dictionary<string, PlaylistHeadSnapshot>(StringComparer.OrdinalIgnoreCase);
        if (playlists.Count == 0)
        {
            return emptyHeads;
        }

        var platformCoordinator = serviceProvider.GetService<PlaylistPlatformSnapshotCoordinator>();
        if (platformCoordinator is null)
        {
            // Discovery is an optimisation of ordering and concurrency, never a requirement: with no
            // coordinator registered, reconciliation simply fetches its own heads as it always has.
            return emptyHeads;
        }

        IReadOnlyList<PlaylistHeadDiscovery> discoveries;
        try
        {
            discoveries = await platformCoordinator.DiscoverHeadsAsync(
                playlists,
                async (source, watchType, _) =>
                {
                    var state = await repository.GetWatchlistSourceCircuitStateAsync(watchType, source, cancellationToken);
                    return state is not null && IsCircuitOpen(state);
                },
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            // A discovery-phase failure must not abort the run: fall back to per-playlist heads.
            _logger.LogWarning(
                ex,
                "Playlist platform head discovery failed; falling back to per-playlist head fetches for {PlaylistCount} playlist(s).",
                playlists.Count);
            return new Dictionary<string, PlaylistHeadSnapshot>(StringComparer.OrdinalIgnoreCase);
        }

        var heads = new Dictionary<string, PlaylistHeadSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var discovery in discoveries)
        {
            // Failed discoveries are deliberately not mapped: the playlist then fetches its own head
            // during reconciliation, which preserves the existing per-playlist failure handling.
            if (discovery.ChangeState == PlaylistHeadChangeState.Failed
                || discovery.Head is null
                || discovery.Head.IsFailed)
            {
                continue;
            }

            heads[BuildWatchKey(discovery.Source, discovery.SourceId)] = discovery.Head;
        }

        PublishPlatformProgress(BuildPlatformProgress(discoveries));
        return heads;
    }

    /// <summary>
    /// Collapses per-playlist discovery results into one progress entry per platform so the UI can
    /// report platform-level status instead of a message per playlist.
    /// </summary>
    private static IReadOnlyList<WatchlistPlatformProgress> BuildPlatformProgress(
        IReadOnlyList<PlaylistHeadDiscovery> discoveries)
        => discoveries
            .GroupBy(static discovery => discovery.Source, StringComparer.OrdinalIgnoreCase)
            .Select(group => new WatchlistPlatformProgress(
                group.Key,
                PlatformLabel(group.Key),
                group.Count(),
                group.Count(),
                group.Count(static d => d.ChangeState == PlaylistHeadChangeState.Unchanged),
                group.Count(static d => d.ChangeState == PlaylistHeadChangeState.Changed),
                group.Count(static d => d.ChangeState == PlaylistHeadChangeState.New),
                group.Count(static d => d.ChangeState == PlaylistHeadChangeState.RequiresExpansion),
                group.Count(static d => d.ChangeState == PlaylistHeadChangeState.Failed),
                Completed: 0,
                Incomplete: 0,
                HeadPhaseMs: group.Sum(static d => d.DurationMs),
                TotalMs: group.Sum(static d => d.DurationMs),
                StartedUtc: DateTimeOffset.UtcNow,
                CompletedUtc: null))
            .OrderBy(static progress => progress.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

    internal static string PlatformLabel(string source)
        => (source ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "spotify" => "Spotify",
            "deezer" => "Deezer",
            "apple" => "Apple Music",
            "tidal" => "Tidal",
            "qobuz" => "Qobuz",
            "boomplay" => "Boomplay",
            "smarttracklist" => "Smart Tracklists",
            "recommendations" => "Recommendations",
            _ => string.IsNullOrWhiteSpace(source) ? "Unknown" : source.Trim()
        };

    private void PublishPlatformProgress(IReadOnlyList<WatchlistPlatformProgress> platforms)
    {
        UpdateRuntimeHealth(health => health with { Platforms = platforms });

        // One realtime event per platform, never one per playlist. Clients that only need the
        // latest state can ignore intermediate updates entirely.
        try
        {
            _serviceProvider.GetService<ActivitiesRealtimeService>()
                ?.PublishWatchlistPlatformProgressChanged(platforms);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Progress reporting must never be able to fail a watch run.
            _logger.LogDebug(ex, "Publishing Watchlist platform progress failed.");
        }
    }

    /// <summary>
    /// Advances one platform's completed/incomplete counters as its playlists finish reconciling,
    /// keeping the reported progress in step with the serial loop.
    /// </summary>
    private void RecordPlatformOutcome(string source, bool succeeded, bool incomplete)
    {
        lock (_runtimeHealthGate)
        {
            var current = _runtimeHealth.Platforms;
            if (current is null || current.Count == 0)
            {
                return;
            }

            var updated = new List<WatchlistPlatformProgress>(current.Count);
            var matched = false;
            foreach (var platform in current)
            {
                if (!string.Equals(platform.Source, source, StringComparison.OrdinalIgnoreCase))
                {
                    updated.Add(platform);
                    continue;
                }

                matched = true;
                updated.Add(platform with
                {
                    Completed = platform.Completed + (succeeded ? 1 : 0),
                    Incomplete = platform.Incomplete + (incomplete ? 1 : 0),
                    CompletedUtc = DateTimeOffset.UtcNow,
                    TotalMs = platform.HeadPhaseMs
                });
            }

            if (matched)
            {
                _runtimeHealth = _runtimeHealth with { Platforms = updated };
            }
        }
    }

    private static string BuildWatchKey(string source, string? sourceId)
        => $"{(source ?? string.Empty).Trim()}:{(sourceId ?? string.Empty).Trim()}";

    private async Task<PlaylistRunResult> ProcessPlaylistWatchItemsAsync(
        IReadOnlyList<WatchItem> playlistItems,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        LibraryRepository repository,
        IServiceProvider serviceProvider,
        CancellationToken stoppingToken)
    {
        var runStartedUtc = DateTimeOffset.UtcNow;
        if (playlistItems.Count == 0)
        {
            return PlaylistRunResult.Empty;
        }

        var processedKeys = new HashSet<string>(StringComparer.Ordinal);
        var processed = 0;
        var succeeded = 0;
        var failed = 0;
        var skippedByLockBusy = 0;
        var reconciler = serviceProvider.GetRequiredService<PlaylistWatchReconciler>();
        var playlists = playlistItems
            .Select(static item => item.Playlist)
            .Where(static playlist => playlist is not null)
            .Select(static playlist => playlist!)
            .ToList();

        // Phase 1: platform-oriented head discovery. All playlist heads for a platform are fetched
        // together under bounded per-provider concurrency before any reconciliation runs. This is
        // read-only; every mutation below still happens serially, in the existing priority order.
        var prefetchedHeads = await DiscoverPlatformHeadsAsync(
            serviceProvider,
            repository,
            playlists,
            stoppingToken);

        foreach (var activeItem in playlistItems)
        {
            stoppingToken.ThrowIfCancellationRequested();
            var sourceCircuit = await repository.GetWatchlistSourceCircuitStateAsync(
                PlaylistWatchType,
                activeItem.Source,
                stoppingToken);
            if (sourceCircuit is { } openCircuit && IsCircuitOpen(openCircuit))
            {
                var openUntilUtc = openCircuit.OpenUntilUtc;
                await PersistPlaylistSchedulerStateAsync(
                    activeItem,
                    serviceProvider,
                    WatchlistPlaylistState.CircuitOpen,
                    string.IsNullOrWhiteSpace(openCircuit.Reason) ? "Source circuit breaker open." : openCircuit.Reason,
                    openUntilUtc,
                    _consecutiveFailures.TryGetValue(activeItem.Key, out var circuitFailures) ? circuitFailures : 0,
                    stoppingToken);
                await PersistPlaylistProgressAsync(repository, activeItem, stoppingToken);
                processedKeys.Add(activeItem.Key);
                processed++;
                failed++;
                continue;
            }

            await TouchPlaylistHeartbeatAsync(repository, activeItem, stoppingToken);
            var execution = await TryProcessItemAsync(
                activeItem,
                settings,
                serviceProvider,
                stoppingToken,
                prefetchedHeads.GetValueOrDefault(BuildWatchKey(activeItem.Source, activeItem.Playlist?.SourceId)));
            await TouchPlaylistHeartbeatAsync(repository, activeItem, stoppingToken);
            await PersistPlaylistProgressAsync(repository, activeItem, stoppingToken);
            await reconciler.AdmitDueMissingTracksWhenQuotaReadyAsync(playlists, stoppingToken);
            if (execution.Outcome == WatchItemRunOutcome.LockBusy)
            {
                await PersistPlaylistSchedulerStateAsync(
                    activeItem,
                    serviceProvider,
                    WatchlistPlaylistState.Pending,
                    "Playlist reconciliation is already running.",
                    null,
                    0,
                    stoppingToken);
                skippedByLockBusy++;
                processedKeys.Add(activeItem.Key);
                processed++;
                failed++;
                continue;
            }

            processed++;
            processedKeys.Add(activeItem.Key);
            if (execution.Outcome == WatchItemRunOutcome.Success)
            {
                succeeded++;
            }
            else
            {
                failed++;
            }

            // Fold this playlist's outcome into its platform's progress line. Truncation and
            // failure stay visible here; everything else stays in per-playlist state and history.
            RecordPlatformOutcome(
                activeItem.Source,
                execution.Outcome == WatchItemRunOutcome.Success,
                execution.PlaylistResult?.QueueStopReason is { } stopReason
                && stopReason.Equals(WatchQueueStopReason.Incomplete.ToString(), StringComparison.OrdinalIgnoreCase));

            var playlistResult = execution.PlaylistResult;
            if (playlistResult is { } systemicFailureResult && ShouldRecordSystemicFailure(systemicFailureResult))
            {
                await OpenSourceCircuitAsync(
                    repository,
                    activeItem.Source,
                    systemicFailureResult.FailureFingerprint,
                    systemicFailureResult.FailureMessage,
                    stoppingToken);
                continue;
            }

            if (execution.Outcome == WatchItemRunOutcome.Failure && execution.SystemicFailure)
            {
                await OpenSourceCircuitAsync(
                    repository,
                    activeItem.Source,
                    fingerprint: "hosted_service_exception",
                    reason: execution.FailureMessage,
                    stoppingToken);
                continue;
            }

            await repository.UpsertWatchlistSourceCircuitStateAsync(
                new LibraryRepository.WatchlistSourceCircuitStateUpsertInput(
                    PlaylistWatchType,
                    activeItem.Source,
                    IsOpen: false,
                    OpenUntilUtc: null,
                    Reason: null,
                    Fingerprint: null,
                    FailureCount: 0),
                stoppingToken);

            if (execution.SnapshotExpanded
                && settings.WatchDelayBetweenPlaylistsSeconds > 0)
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(settings.WatchDelayBetweenPlaylistsSeconds),
                    stoppingToken);
            }
        }

        var elapsedMs = (DateTimeOffset.UtcNow - runStartedUtc).TotalMilliseconds;
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Watchlist playlist run summary: total={TotalItems}, processed={Processed}, ok={Succeeded}, failed={Failed}, skipLock={SkippedLock}, elapsedMs={ElapsedMs:0}",
                playlistItems.Count,
                processed,
                succeeded,
                failed,
                skippedByLockBusy,
                elapsedMs);
        }

        return new PlaylistRunResult(AbortedRun: false, processedKeys);
    }

    private static async Task PersistPlaylistProgressAsync(
        LibraryRepository repository,
        WatchItem item,
        CancellationToken cancellationToken)
    {
        await SaveSchedulerStateAsync(
            repository,
            activeSource: null,
            activeSourceId: null,
            activeStartedUtc: null,
            lastProgressUtc: DateTimeOffset.UtcNow,
            cancellationToken);
    }

    private static async Task TouchPlaylistHeartbeatAsync(
        LibraryRepository repository,
        WatchItem item,
        CancellationToken cancellationToken)
    {
        if (item.Playlist == null)
        {
            return;
        }

        await repository.TouchPlaylistWatchHeartbeatAsync(
            item.Playlist.Source,
            item.Playlist.SourceId,
            TimeSpan.FromMinutes(45),
            cancellationToken);
    }

    private async Task<IReadOnlySet<long>> ProcessArtistWatchItemsAsync(
        IReadOnlyList<WatchItem> artistItems,
        IReadOnlyList<WatchItem> playlistItems,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        IServiceProvider serviceProvider,
        CancellationToken stoppingToken)
    {
        if (artistItems.Count == 0)
        {
            return new HashSet<long>();
        }

        var processedArtistIds = new HashSet<long>();
        var reconciler = serviceProvider.GetRequiredService<PlaylistWatchReconciler>();
        var playlists = playlistItems
            .Select(static item => item.Playlist)
            .Where(static playlist => playlist is not null)
            .Select(static playlist => playlist!)
            .ToList();
        foreach (var item in artistItems)
        {
            stoppingToken.ThrowIfCancellationRequested();
            var artistRepository = serviceProvider.GetService<LibraryRepository>();
            if (artistRepository?.IsConfigured == true)
            {
                var artistCircuit = await artistRepository.GetWatchlistSourceCircuitStateAsync(
                    ArtistWatchType,
                    item.Source,
                    stoppingToken);
                if (artistCircuit is { } openArtistCircuit && IsCircuitOpen(openArtistCircuit))
                {
                    await PersistArtistRunStateAsync(
                        item,
                        serviceProvider,
                        "circuit_open",
                        string.IsNullOrWhiteSpace(openArtistCircuit.Reason)
                            ? "Source circuit breaker open."
                            : openArtistCircuit.Reason,
                        openArtistCircuit.OpenUntilUtc,
                        openArtistCircuit.FailureCount,
                        "circuit_open",
                        null,
                        stoppingToken);
                    if (item.Artist is not null)
                    {
                        processedArtistIds.Add(item.Artist.ArtistId);
                    }
                    continue;
                }
            }

            var execution = await TryProcessItemAsync(item, settings, serviceProvider, stoppingToken);
            if (item.Artist != null)
            {
                processedArtistIds.Add(item.Artist.ArtistId);
            }
            await reconciler.AdmitDueMissingTracksWhenQuotaReadyAsync(playlists, stoppingToken);

            if (execution.Outcome == WatchItemRunOutcome.Failure
                && execution.SystemicFailure
                && artistRepository?.IsConfigured == true)
            {
                await OpenSourceCircuitAsync(
                    artistRepository,
                    item.Source,
                    fingerprint: "artist_watch_systemic_failure",
                    reason: execution.FailureMessage,
                    stoppingToken,
                    ArtistWatchType);
            }
        }

        return processedArtistIds;
    }

    private async Task<WatchItemExecutionOutcome> TryProcessItemAsync(
        WatchItem item,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        IServiceProvider serviceProvider,
        CancellationToken stoppingToken,
        PlaylistHeadSnapshot? prefetchedHead = null)
    {
        var itemLock = _itemLocks.GetOrAdd(item.Key, _ => new SemaphoreSlim(1, 1));
        if (!await itemLock.WaitAsync(0, stoppingToken))
        {
            return new WatchItemExecutionOutcome(WatchItemRunOutcome.LockBusy, null, false, null, false);
        }

        var startedUtc = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await PersistArtistRunStateAsync(
                item,
                serviceProvider,
                "processing",
                null,
                null,
                _consecutiveFailures.GetValueOrDefault(item.Key),
                "reconciling",
                null,
                stoppingToken);
            var playlistResult = await RunItemAsync(item, serviceProvider, stoppingToken, prefetchedHead);
            _consecutiveFailures.TryRemove(item.Key, out _);
            await PersistArtistRunStateAsync(
                item,
                serviceProvider,
                "completed",
                null,
                null,
                0,
                "completed",
                null,
                stoppingToken);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Watchlist item succeeded: key={WatchItemKey}, kind={Kind}, source={Source}, elapsedMs={ElapsedMs:0}",
                    item.Key,
                    item.Kind,
                    item.Source,
                    stopwatch.Elapsed.TotalMilliseconds);
            }
            return new WatchItemExecutionOutcome(
                WatchItemRunOutcome.Success,
                playlistResult,
                false,
                null,
                playlistResult?.SnapshotExpanded == true);
        }
        catch (OperationCanceledException ex) when (!stoppingToken.IsCancellationRequested)
        {
            return await RecordItemFailureAsync(item, settings, serviceProvider, startedUtc, stopwatch, ex, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await RecordItemFailureAsync(item, settings, serviceProvider, startedUtc, stopwatch, ex, stoppingToken);
        }
        finally
        {
            itemLock.Release();
        }
    }

    private async Task<WatchItemExecutionOutcome> RecordItemFailureAsync(
        WatchItem item,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        IServiceProvider serviceProvider,
        DateTimeOffset startedUtc,
        Stopwatch stopwatch,
        Exception ex,
        CancellationToken cancellationToken)
    {
        var failures = _consecutiveFailures.AddOrUpdate(item.Key, 1, static (_, current) => Math.Min(current + 1, 12));
        var baseDelaySeconds = item.Kind == ArtistKind
            ? Math.Max(1, settings.WatchDelayBetweenArtistsSeconds)
            : Math.Max(1, settings.WatchDelayBetweenPlaylistsSeconds);
        var backoffSeconds = Math.Min(
            600,
            baseDelaySeconds * (int)Math.Pow(2, Math.Min(failures - 1, 6)));
        var nextRunUtc = startedUtc.AddSeconds(backoffSeconds);
        await PersistPlaylistSchedulerStateAsync(item, serviceProvider, WatchlistPlaylistState.Backoff, ex.Message, nextRunUtc, failures, cancellationToken);
        await PersistArtistRunStateAsync(
            item,
            serviceProvider,
            "backoff",
            ex.Message,
            nextRunUtc,
            failures,
            "backoff",
            null,
            cancellationToken);
        if (ShouldEmitBackoffWarning(failures))
        {
            _logger.LogWarning(
                ex,
                "Watchlist item failed: key={WatchItemKey}, kind={Kind}, source={Source}, failures={Failures}, backoffSeconds={BackoffSeconds}, nextRunUtc={NextRunUtc}, elapsedMs={ElapsedMs:0}",
                item.Key,
                item.Kind,
                item.Source,
                failures,
                backoffSeconds,
                nextRunUtc,
                stopwatch.Elapsed.TotalMilliseconds);
        }
        else
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Watchlist item still failing under backoff threshold: key={WatchItemKey}, kind={Kind}, source={Source}, failures={Failures}, backoffSeconds={BackoffSeconds}, nextRunUtc={NextRunUtc}, elapsedMs={ElapsedMs:0}",
                    item.Key,
                    item.Kind,
                    item.Source,
                    failures,
                    backoffSeconds,
                    nextRunUtc,
                    stopwatch.Elapsed.TotalMilliseconds);
            }
        }
        return new WatchItemExecutionOutcome(
            WatchItemRunOutcome.Failure,
            null,
            IsLikelySystemicFailure(ex.Message),
            ex.Message,
            SnapshotExpanded: false);
    }

    private static async Task PersistPlaylistSchedulerStateAsync(
        WatchItem item,
        IServiceProvider serviceProvider,
        WatchlistPlaylistState status,
        string? message,
        DateTimeOffset? nextAttemptUtc,
        int? consecutiveFailures,
        CancellationToken cancellationToken)
    {
        if (item.Kind != PlaylistKind || item.Playlist is null)
        {
            return;
        }

        var repository = serviceProvider.GetService<LibraryRepository>();
        var playlistReconciler = serviceProvider.GetService<PlaylistWatchReconciler>();
        if (repository == null || playlistReconciler == null || !repository.IsConfigured)
        {
            return;
        }

        var state = await repository.GetPlaylistWatchStateAsync(item.Playlist.Source, item.Playlist.SourceId, cancellationToken);
        if (state != null
            && string.Equals(state.LastRunStatus, WatchlistStateService.ToPersistedStatus(status), StringComparison.Ordinal)
            && string.Equals(state.LastRunMessage, message, StringComparison.Ordinal)
            && state.ConsecutiveFailures == consecutiveFailures
            && Nullable.Equals(state.NextAttemptUtc, nextAttemptUtc))
        {
            return;
        }

        await playlistReconciler.UpdatePlaylistStateAsync(
            item.Playlist.Source,
            item.Playlist.SourceId,
            state?.TrackCount ?? item.Playlist.TrackCount,
            state?.SnapshotId ?? item.Playlist.SnapshotId,
            status,
            message,
            nextAttemptUtc,
            consecutiveFailures,
            cancellationToken,
            touchLastChecked: true);
    }

    private static async Task PersistArtistRunStateAsync(
        WatchItem item,
        IServiceProvider serviceProvider,
        string status,
        string? message,
        DateTimeOffset? nextAttemptUtc,
        int consecutiveFailures,
        string phase,
        DateTimeOffset? deadlineUtc,
        CancellationToken cancellationToken)
    {
        if (item.Kind != ArtistKind || item.Artist is null)
        {
            return;
        }

        var repository = serviceProvider.GetService<LibraryRepository>();
        if (repository?.IsConfigured != true)
        {
            return;
        }

        await repository.UpdateArtistWatchRunStateAsync(
            item.Artist.ArtistId,
            status,
            message,
            nextAttemptUtc,
            consecutiveFailures,
            phase,
            deadlineUtc,
            cancellationToken);
    }

    private static List<WatchItem> BuildPlaylistWatchItems(IReadOnlyList<PlaylistWatchlistDto> playlists)
    {
        var items = new List<WatchItem>(playlists.Count);
        foreach (var playlist in playlists)
        {
            if (playlist == null || string.IsNullOrWhiteSpace(playlist.SourceId))
            {
                continue;
            }
            var key = $"playlist:{playlist.Source}:{playlist.SourceId}";
            items.Add(new WatchItem(PlaylistKind, key, NormalizeSource(playlist.Source), playlist, null));
        }

        return items;
    }

    private static List<WatchItem> BuildArtistWatchItems(IReadOnlyList<WatchlistArtistDto> artists)
    {
        var items = new List<WatchItem>(artists.Count);
        foreach (var artist in artists)
        {
            var key = $"artist:{artist.ArtistId}";
            items.Add(new WatchItem(ArtistKind, key, ArtistKind, null, artist));
        }

        return items;
    }

    private static async Task<List<WatchItem>> FilterEligibleWatchItemsAsync(
        IReadOnlyList<WatchItem> items,
        LibraryRepository repository,
        CancellationToken cancellationToken)
    {
        var eligible = new List<WatchItem>(items.Count);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PlaylistWatchPreferenceDto? playlistPreference = null;
            if (item.Playlist is not null)
            {
                playlistPreference = await repository.GetPlaylistWatchPreferenceAsync(
                    NormalizeSource(item.Playlist.Source),
                    item.Playlist.SourceId,
                    cancellationToken);
            }

            if (HasExplicitDestination(item, playlistPreference))
            {
                eligible.Add(item);
            }
        }

        return eligible;
    }

    private static bool HasExplicitDestination(
        WatchItem item,
        PlaylistWatchPreferenceDto? playlistPreference)
        => item.Playlist is not null
            ? playlistPreference?.DestinationFolderId is > 0
            : item.Artist?.DestinationFolderId is > 0;

    private static string NormalizeStatus(string? status)
        => string.IsNullOrWhiteSpace(status) ? string.Empty : status.Trim().ToLowerInvariant();

    private static async Task SaveSchedulerStateAsync(
        LibraryRepository repository,
        string? activeSource,
        string? activeSourceId,
        DateTimeOffset? activeStartedUtc,
        DateTimeOffset? lastProgressUtc,
        CancellationToken cancellationToken)
    {
        await repository.UpsertWatchlistSchedulerStateAsync(
            new LibraryRepository.WatchlistSchedulerStateUpsertInput(
                PlaylistWatchType,
                activeSource,
                activeSourceId,
                activeStartedUtc,
                lastProgressUtc),
            cancellationToken);
    }

    private static bool IsCircuitOpen(WatchlistSourceCircuitStateDto? circuitState)
    {
        if (circuitState is not { IsOpen: true })
        {
            return false;
        }

        if (!circuitState.OpenUntilUtc.HasValue)
        {
            return true;
        }

        return DateTimeOffset.UtcNow < circuitState.OpenUntilUtc.Value;
    }

    private static async Task OpenSourceCircuitAsync(
        LibraryRepository repository,
        string source,
        string? fingerprint,
        string? reason,
        CancellationToken cancellationToken,
        string watchType = PlaylistWatchType)
    {
        var existing = await repository.GetWatchlistSourceCircuitStateAsync(watchType, source, cancellationToken);
        if (IsSpotifyAuthenticationIncidentFingerprint(fingerprint)
            && string.Equals(existing?.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return;
        }
        var failureCount = Math.Max(0, existing?.FailureCount ?? 0) + 1;
        var isOpen = failureCount >= SourceCircuitFailureThreshold;
        var openUntilUtc = isOpen
            ? DateTimeOffset.UtcNow.AddSeconds(SourceCircuitCooldownSeconds)
            : existing?.OpenUntilUtc;

        await repository.UpsertWatchlistSourceCircuitStateAsync(
            new LibraryRepository.WatchlistSourceCircuitStateUpsertInput(
                watchType,
                source,
                isOpen,
                openUntilUtc,
                string.IsNullOrWhiteSpace(reason) ? "Systemic source failure." : reason,
                fingerprint,
                failureCount),
            cancellationToken);
    }

    private static bool IsLikelySystemicFailure(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var normalized = message.Trim().ToLowerInvariant();
        return normalized.Contains("captcha", StringComparison.Ordinal)
               || normalized.Contains("forbidden", StringComparison.Ordinal)
               || normalized.Contains("unauthorized", StringComparison.Ordinal)
               || normalized.Contains("login required", StringComparison.Ordinal)
               || normalized.Contains("http 401", StringComparison.Ordinal)
               || normalized.Contains("http 403", StringComparison.Ordinal)
               || normalized.Contains("http 429", StringComparison.Ordinal)
               || normalized.Contains("rate limit", StringComparison.Ordinal)
               || normalized.Contains("too many requests", StringComparison.Ordinal)
               || normalized.Contains("timeout", StringComparison.Ordinal)
               || normalized.Contains("timed out", StringComparison.Ordinal)
               || normalized.Contains("service unavailable", StringComparison.Ordinal)
               || normalized.Contains("gateway timeout", StringComparison.Ordinal)
               || normalized.Contains("http 500", StringComparison.Ordinal)
               || normalized.Contains("http 502", StringComparison.Ordinal)
               || normalized.Contains("http 503", StringComparison.Ordinal)
               || normalized.Contains("http 504", StringComparison.Ordinal);
    }

    internal static bool ShouldRecordSystemicFailure(PlaylistReconciliationResult result)
        => result.SystemicFailures > 0 && result.FailureIsIncidentOrigin;

    private static bool IsSpotifyAuthenticationIncidentFingerprint(string? fingerprint)
        => !string.IsNullOrWhiteSpace(fingerprint)
           && fingerprint.StartsWith("spotify_auth_", StringComparison.OrdinalIgnoreCase)
           && fingerprint.Contains(':', StringComparison.Ordinal);

    private void CleanupStaleState(IReadOnlyList<WatchItem> items)
    {
        var activeKeys = new HashSet<string>(items.Select(static item => item.Key), StringComparer.Ordinal);
        CleanupDictionary(_itemLocks, activeKeys, static semaphore =>
        {
            semaphore.Dispose();
            return true;
        });
        CleanupDictionary(_consecutiveFailures, activeKeys, static _ => true);
    }

    private static void CleanupDictionary<TValue>(
        ConcurrentDictionary<string, TValue> dictionary,
        HashSet<string> activeKeys,
        Func<TValue, bool> onRemoved)
    {
        foreach (var key in dictionary.Keys)
        {
            if (activeKeys.Contains(key))
            {
                continue;
            }

            if (dictionary.TryRemove(key, out var removedValue))
            {
                _ = onRemoved(removedValue);
            }
        }
    }

    private static async Task<PlaylistReconciliationResult?> RunItemAsync(
        WatchItem item,
        IServiceProvider serviceProvider,
        CancellationToken stoppingToken,
        PlaylistHeadSnapshot? prefetchedHead = null)
    {
        if (item.Kind == PlaylistKind && item.Playlist != null)
        {
            var watcher = serviceProvider.GetRequiredService<PlaylistWatchReconciler>();
            return await watcher.ReconcilePlaylistAsync(item.Playlist, stoppingToken, false, prefetchedHead);
        }

        if (item.Kind == ArtistKind && item.Artist != null)
        {
            var watcher = serviceProvider.GetRequiredService<ArtistWatchService>();
            await watcher.CheckArtistWatchItemAsync(item.Artist, stoppingToken);
        }

        return null;
    }

    private sealed record WatchItem(
        string Kind,
        string Key,
        string Source,
        PlaylistWatchlistDto? Playlist,
        WatchlistArtistDto? Artist);
    private sealed record PlaylistRunResult(
        bool AbortedRun,
        IReadOnlySet<string> ProcessedKeys)
    {
        public static PlaylistRunResult Empty { get; } = new(
            false,
            new HashSet<string>(StringComparer.Ordinal));
    }

    private sealed record WatchItemExecutionOutcome(
        WatchItemRunOutcome Outcome,
        PlaylistReconciliationResult? PlaylistResult,
        bool SystemicFailure,
        string? FailureMessage,
        bool SnapshotExpanded = false);

    private enum WatchItemRunOutcome
    {
        Success,
        Failure,
        LockBusy
    }

    private static string NormalizeSource(string? source)
        => string.IsNullOrWhiteSpace(source) ? "unknown" : source.Trim().ToLowerInvariant();

    internal static bool ShouldEmitBackoffWarning(int failures)
    {
        if (failures <= 2)
        {
            return true;
        }

        // Keep warnings on exponential milestones while reducing repetitive noise.
        return (failures & (failures - 1)) == 0;
    }
}
