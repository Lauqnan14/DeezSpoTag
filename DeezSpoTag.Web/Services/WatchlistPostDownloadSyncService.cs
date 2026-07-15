using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;

namespace DeezSpoTag.Web.Services;

public sealed record WatchlistPostDownloadSyncHealth(
    bool IsRunning,
    bool IsProcessing,
    DateTimeOffset? LastHeartbeatUtc,
    DateTimeOffset? LastCycleCompletedUtc,
    DateTimeOffset? LastSuccessfulJobUtc,
    long? CurrentJobId,
    string? CurrentTarget,
    int ConsecutiveFailures,
    string? LastError);

public sealed class WatchlistPostDownloadSyncService : BackgroundService, IWatchlistPostDownloadSyncNotifier
{
    private const string PlaylistRefreshTrackId = "__playlist_refresh__";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProcessingLease = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromMinutes(10);
    private readonly WatchlistRunSignal _wakeSignal = new();
    private readonly WatchlistRunSignal _coordinatorSignal;
    private readonly IServiceProvider _serviceProvider;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly ILogger<WatchlistPostDownloadSyncService> _logger;
    private readonly string _leaseOwner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private readonly object _healthGate = new();
    private WatchlistPostDownloadSyncHealth _health = new(false, false, null, null, null, null, null, 0, null);
    private DateTimeOffset _lastRepairAttemptUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastOutboxRepairUtc = DateTimeOffset.MinValue;

    public WatchlistPostDownloadSyncService(
        IServiceProvider serviceProvider,
        DeezSpoTagSettingsService settingsService,
        WatchlistRunSignal coordinatorSignal,
        ILogger<WatchlistPostDownloadSyncService> logger)
    {
        _serviceProvider = serviceProvider;
        _settingsService = settingsService;
        _coordinatorSignal = coordinatorSignal;
        _logger = logger;
    }

    public async ValueTask NotifyFinalizedAsync(
        string source,
        string playlistId,
        string trackId,
        string queueUuid,
        long? destinationFolderId,
        IReadOnlyList<string>? finalFilePaths = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source)
            || string.IsNullOrWhiteSpace(playlistId)
            || string.IsNullOrWhiteSpace(trackId))
        {
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<LibraryRepository>();
        var paths = NormalizeChangedFilePaths(finalFilePaths);
        var jobs = await repository.EnqueueWatchlistSyncJobAsync(
            source,
            playlistId,
            trackId,
            destinationFolderId,
            paths,
            queueUuid,
            cancellationToken);
        if (jobs.Count > 0)
        {
            SignalWorker();
        }
    }

    public Task ResumePendingJobsAsync(CancellationToken cancellationToken = default)
    {
        if (!IsWatchlistEnabled())
        {
            return Task.CompletedTask;
        }

        cancellationToken.ThrowIfCancellationRequested();
        SignalWorker();
        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        UpdateHealth(health => health with { IsRunning = true, LastHeartbeatUtc = DateTimeOffset.UtcNow });
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                UpdateHealth(health => health with { LastHeartbeatUtc = DateTimeOffset.UtcNow });
                if (IsWatchlistEnabled())
                {
                    await ProcessDueJobsAsync(stoppingToken);
                }

                UpdateHealth(health => health with
                {
                    LastHeartbeatUtc = DateTimeOffset.UtcNow,
                    LastCycleCompletedUtc = DateTimeOffset.UtcNow,
                    ConsecutiveFailures = 0
                });
                await WaitForWakeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Watchlist target-sync worker cycle failed; the worker will remain active and retry.");
                UpdateHealth(health => health with
                {
                    LastHeartbeatUtc = DateTimeOffset.UtcNow,
                    LastCycleCompletedUtc = DateTimeOffset.UtcNow,
                    ConsecutiveFailures = health.ConsecutiveFailures + 1,
                    LastError = ex.Message,
                    IsProcessing = false,
                    CurrentJobId = null,
                    CurrentTarget = null
                });
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        UpdateHealth(health => health with { IsRunning = false, IsProcessing = false, LastHeartbeatUtc = DateTimeOffset.UtcNow });
    }

    public WatchlistPostDownloadSyncHealth GetRuntimeHealth()
    {
        lock (_healthGate)
        {
            return _health;
        }
    }

    private void UpdateHealth(Func<WatchlistPostDownloadSyncHealth, WatchlistPostDownloadSyncHealth> update)
    {
        lock (_healthGate)
        {
            _health = update(_health);
        }
    }

    private async Task ProcessDueJobsAsync(CancellationToken cancellationToken)
    {
        await RepairMissingFinalizationOutboxAsync(cancellationToken);
        await ProcessFinalizationOutboxAsync(cancellationToken);
        await RepairIncompleteJobsIfNeededAsync(cancellationToken);
        for (var processed = 0; processed < 100 && IsWatchlistEnabled(); processed++)
        {
            using var scope = _serviceProvider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<LibraryRepository>();
            if (!repository.IsConfigured)
            {
                return;
            }

            var jobs = await repository.ClaimDueWatchlistSyncJobsAsync(1, ProcessingLease, _leaseOwner, cancellationToken);
            var job = jobs.FirstOrDefault();
            if (job == null)
            {
                return;
            }

            UpdateHealth(health => health with
            {
                IsProcessing = true,
                LastHeartbeatUtc = DateTimeOffset.UtcNow,
                CurrentJobId = job.Id,
                CurrentTarget = job.TargetService
            });
            await ProcessClaimedJobAsync(repository, job, cancellationToken);
            UpdateHealth(health => health with
            {
                IsProcessing = false,
                LastHeartbeatUtc = DateTimeOffset.UtcNow,
                CurrentJobId = null,
                CurrentTarget = null
            });
        }
    }

    private async Task ProcessFinalizationOutboxAsync(CancellationToken cancellationToken)
    {
        for (var processed = 0; processed < 100; processed++)
        {
            using var scope = _serviceProvider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<LibraryRepository>();
            var outbox = await repository.ClaimDueWatchlistFinalizationOutboxAsync(
                1,
                ProcessingLease,
                _leaseOwner,
                cancellationToken);
            var work = outbox.FirstOrDefault();
            if (work == null)
            {
                return;
            }

            try
            {
                var queueRepository = scope.ServiceProvider.GetRequiredService<DeezSpoTag.Services.Download.Queue.DownloadQueueRepository>();
                var item = await queueRepository.GetByUuidAsync(work.QueueUuid, cancellationToken);
                if (item == null)
                {
                    await repository.RetryWatchlistFinalizationOutboxAsync(
                        work.Id,
                        _leaseOwner,
                        work.AttemptCount + 1,
                        DateTimeOffset.UtcNow.AddMinutes(1),
                        "Queue item is not currently available.",
                        cancellationToken);
                    continue;
                }

                var sent = await scope.ServiceProvider.GetRequiredService<WatchlistFinalizationService>()
                    .NotifyQueueItemFinalizedAsync(
                        item,
                        work.PayloadJson ?? item.PayloadJson,
                        work.FinalFilePaths,
                        cancellationToken);
                if (sent > 0)
                {
                    await repository.CompleteWatchlistFinalizationOutboxAsync(work.Id, _leaseOwner, cancellationToken);
                    continue;
                }

                await repository.RetryWatchlistFinalizationOutboxAsync(
                    work.Id,
                    _leaseOwner,
                    work.AttemptCount + 1,
                    DateTimeOffset.UtcNow.AddMinutes(1),
                    "Finalized files or Watchlist ownership are not verifiable yet.",
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
            {
                await repository.RetryWatchlistFinalizationOutboxAsync(
                    work.Id,
                    _leaseOwner,
                    work.AttemptCount + 1,
                    DateTimeOffset.UtcNow.AddMinutes(1),
                    ex.Message,
                    cancellationToken);
            }
        }
    }

    private async Task RepairMissingFinalizationOutboxAsync(CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow - _lastOutboxRepairUtc < TimeSpan.FromMinutes(5))
        {
            return;
        }
        _lastOutboxRepairUtc = DateTimeOffset.UtcNow;

        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<LibraryRepository>();
        var queueRepository = scope.ServiceProvider.GetRequiredService<DeezSpoTag.Services.Download.Queue.DownloadQueueRepository>();
        var completedItems = (await queueRepository.GetTasksAsync(cancellationToken: cancellationToken))
            .Where(item => string.Equals(item.Status, "completed", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(item.Status, "complete", StringComparison.OrdinalIgnoreCase));
        foreach (var item in completedItems)
        {
            var claims = await repository.GetPlaylistWatchDownloadClaimsAsync(item.QueueUuid, status: null, cancellationToken);
            if (claims.Count == 0 && !WatchlistFinalizationService.PayloadHasWatchlistContext(item.PayloadJson))
            {
                continue;
            }
            var paths = DeezSpoTag.Services.Download.Queue.DownloadQueueRepository.GetExistingMaterializedFilePaths(item);
            if (paths.Count == 0)
            {
                continue;
            }
            await repository.UpsertWatchlistFinalizationOutboxAsync(
                item.QueueUuid,
                item.PayloadJson,
                paths,
                cancellationToken);
        }
        await repository.DeleteCompletedWatchlistFinalizationOutboxOlderThanAsync(
            DateTimeOffset.UtcNow.AddDays(-30),
            cancellationToken);
    }

    private async Task RepairIncompleteJobsIfNeededAsync(CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow - _lastRepairAttemptUtc < TimeSpan.FromMinutes(5))
        {
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<LibraryRepository>();
        if (!repository.IsConfigured)
        {
            return;
        }
        var counts = await repository.GetWatchlistSyncJobStatusCountsAsync(cancellationToken);
        if (counts.RepairRequired <= 0)
        {
            return;
        }

        _lastRepairAttemptUtc = DateTimeOffset.UtcNow;
        var playlists = await repository.GetPlaylistWatchlistAsync(cancellationToken);
        var repairService = scope.ServiceProvider.GetRequiredService<WatchlistFinalizationService>();
        await repairService.RepairPlaylistsAsync(playlists, cancellationToken);
    }

    private bool IsWatchlistEnabled()
    {
        var settings = _settingsService.LoadSettings();
        return settings.WatchEnabled;
    }

    private async Task ProcessClaimedJobAsync(
        LibraryRepository repository,
        WatchlistSyncJobDto job,
        CancellationToken cancellationToken)
    {
        using var leaseRenewalCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var leaseRenewal = RenewLeaseAsync(repository, job.Id, leaseRenewalCancellation.Token);
        try
        {
            var request = new SyncRequest(
                job.Id,
                job.Source,
                job.PlaylistId,
                job.TrackId,
                job.TargetService,
                job.DestinationFolderId,
                job.FinalFilePaths,
                job.AttemptCount);
            var outcome = await TrySyncOnceAsync(request, job.AttemptCount + 1, cancellationToken);
            switch (outcome.Kind)
            {
                case SyncAttemptOutcomeKind.Completed:
                    if (await repository.CompleteWatchlistSyncJobAsync(job.Id, _leaseOwner, cancellationToken))
                    {
                        UpdateHealth(health => health with
                        {
                            LastSuccessfulJobUtc = DateTimeOffset.UtcNow,
                            LastError = null
                        });
                    }
                    return;
                case SyncAttemptOutcomeKind.Obsolete:
                    await repository.DeleteObsoleteWatchlistSyncJobAsync(job, _leaseOwner, cancellationToken);
                    return;
                case SyncAttemptOutcomeKind.RepairRequired:
                    await repository.MarkWatchlistSyncJobRepairRequiredAsync(
                        job.Id,
                        _leaseOwner,
                        outcome.Message,
                        cancellationToken);
                    return;
                case SyncAttemptOutcomeKind.Blocked:
                    await repository.BlockWatchlistSyncJobAsync(job.Id, _leaseOwner, outcome.Message, cancellationToken);
                    return;
            }
            var attempt = job.AttemptCount + 1;
            var retryDelay = TimeSpan.FromSeconds(Math.Min(MaximumRetryDelay.TotalSeconds, 15 * Math.Pow(2, Math.Min(attempt - 1, 6))));
            await repository.RetryWatchlistSyncJobAsync(
                job.Id,
                _leaseOwner,
                attempt,
                DateTimeOffset.UtcNow + retryDelay,
                outcome.Message,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Watchlist target sync job {JobId} failed unexpectedly; returning it to durable retry.",
                job.Id);
            await repository.RetryWatchlistSyncJobAsync(
                job.Id,
                _leaseOwner,
                job.AttemptCount + 1,
                DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1),
                ex.Message,
                cancellationToken);
        }
        finally
        {
            leaseRenewalCancellation.Cancel();
            try
            {
                await leaseRenewal;
            }
            catch (OperationCanceledException) when (leaseRenewalCancellation.IsCancellationRequested)
            {
                // Expected once the claimed job leaves processing.
            }
        }
    }

    private async Task RenewLeaseAsync(LibraryRepository repository, long jobId, CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromTicks(ProcessingLease.Ticks / 3);
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(interval, cancellationToken);
            if (!await repository.RenewWatchlistSyncJobLeaseAsync(jobId, _leaseOwner, ProcessingLease, cancellationToken))
            {
                return;
            }
            UpdateHealth(health => health with { LastHeartbeatUtc = DateTimeOffset.UtcNow });
        }
    }

    private void SignalWorker()
        => _wakeSignal.Request();

    private Task WaitForWakeAsync(CancellationToken cancellationToken)
        => _wakeSignal.WaitAsync(PollInterval, cancellationToken);

    private async Task<SyncAttemptOutcome> TrySyncOnceAsync(SyncRequest request, int attempt, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<LibraryRepository>();
            if (!repository.IsConfigured)
            {
                return SyncAttemptOutcome.Retry("Library database is not configured.");
            }

            var playlist = await FindPlaylistAsync(repository, request, cancellationToken);
            if (playlist == null)
            {
                LogPlaylistMissing(request);
                return SyncAttemptOutcome.Obsolete("The monitored playlist no longer exists.");
            }

            var preference = await repository.GetPlaylistWatchPreferenceAsync(
                playlist.Source,
                playlist.SourceId,
                cancellationToken);
            if (preference == null || !IsConfiguredTarget(preference, request.TargetService))
            {
                _logger.LogInformation(
                    "Completing obsolete Watchlist sync job {JobId}; target {TargetService} is no longer configured for {Source}:{PlaylistId}.",
                    request.JobId,
                    request.TargetService,
                    request.Source,
                    request.PlaylistId);
                return SyncAttemptOutcome.Obsolete("The synchronization target is no longer configured.");
            }
            var effectiveRequest = ResolveEffectiveRequest(request, preference);
            var isPlaylistRefresh = string.Equals(request.TrackId, PlaylistRefreshTrackId, StringComparison.Ordinal);

            if (await repository.HasWatchlistReconciliationRequestAsync(
                    "playlist",
                    playlist.Source,
                    playlist.SourceId,
                    cancellationToken))
            {
                _coordinatorSignal.Request();
                return SyncAttemptOutcome.Retry("Waiting for the durable playlist reconciliation request to complete.");
            }

            var watcher = scope.ServiceProvider.GetRequiredService<PlaylistWatchReconciler>();
            var candidates = await watcher.GetCachedPlaylistTrackCandidatesAsync(
                playlist.Source,
                playlist.SourceId,
                cancellationToken);
            if (candidates.Count == 0)
            {
                await repository.EnqueueWatchlistReconciliationRequestAsync(
                    "playlist",
                    playlist.Source,
                    playlist.SourceId,
                    cancellationToken);
                _coordinatorSignal.Request();
                return SyncAttemptOutcome.Retry("Playlist candidate cache is unavailable; reconciliation was requested.");
            }
            if (!isPlaylistRefresh && !candidates.Any(candidate => string.Equals(
                    candidate.TrackSourceId,
                    request.TrackId,
                    StringComparison.OrdinalIgnoreCase)))
            {
                return SyncAttemptOutcome.Obsolete("The track is no longer present in the monitored source playlist.");
            }

            if (!isPlaylistRefresh
                && effectiveRequest.DestinationFolderId.HasValue
                && effectiveRequest.ChangedFilePaths.Count == 0)
            {
                var repairService = scope.ServiceProvider.GetService<WatchlistFinalizationService>();
                var repaired = repairService == null
                    ? 0
                    : await repairService.RepairPlaylistAsync(playlist, cancellationToken);
                return repaired > 0
                    ? SyncAttemptOutcome.Completed("Finalization repair recreated the target synchronization job.")
                    : SyncAttemptOutcome.RepairRequired("Finalized download has no recoverable destination file paths.");
            }

            if (!isPlaylistRefresh
                && !await VerifyLocalLibraryIngestionAsync(scope.ServiceProvider, effectiveRequest, cancellationToken))
            {
                return SyncAttemptOutcome.Retry("Finalized files are not visible in the local library yet.");
            }

            await RefreshMediaServerAsync(scope.ServiceProvider, request.TargetService, cancellationToken);
            var syncResult = await scope.ServiceProvider.GetRequiredService<PlaylistSyncService>()
                .SyncAvailablePlaylistTracksToTargetAsync(
                playlist,
                preference,
                request.TargetService,
                candidates,
                force: false,
                cancellationToken);

            if ((isPlaylistRefresh && syncResult.Success)
                || (!isPlaylistRefresh
                    && await IsFinalizedTrackSyncedAsync(repository, playlist, request.TrackId, request.TargetService, cancellationToken)))
            {
                await TransitionPlaylistStateAsync(
                    scope.ServiceProvider,
                    playlist,
                    WatchlistPlaylistState.MediaSyncCompleted,
                    "Monitored playlist synchronization completed.",
                    cancellationToken);
                await AddPlaylistSyncHistoryAsync(
                    scope.ServiceProvider,
                    playlist,
                    WatchlistHistoryStatus.MediaSyncCompleted,
                    cancellationToken);
                LogSyncCompleted(request, attempt, syncResult.SyncedTracks);
                return SyncAttemptOutcome.Completed(syncResult.Message);
            }

            var terminalFailure = IsTerminalSyncFailure(syncResult);
            await TransitionPlaylistStateAsync(
                scope.ServiceProvider,
                playlist,
                terminalFailure ? WatchlistPlaylistState.MediaSyncBlocked : WatchlistPlaylistState.MediaSyncWaiting,
                syncResult.Message,
                cancellationToken);
            await AddPlaylistSyncHistoryAsync(
                scope.ServiceProvider,
                playlist,
                terminalFailure ? WatchlistHistoryStatus.MediaSyncBlocked : WatchlistHistoryStatus.MediaSyncWaiting,
                cancellationToken);
            LogSyncNotReady(request, attempt, syncResult.Message);
            return terminalFailure
                ? SyncAttemptOutcome.Blocked(syncResult.Message)
                : SyncAttemptOutcome.Retry(syncResult.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(
                ex,
                "Watchlist playlist sync attempt {Attempt} failed for {Source}:{PlaylistId} after finalized track {TrackId}.",
                attempt,
                request.Source,
                request.PlaylistId,
                request.TrackId);
            return SyncAttemptOutcome.Retry(ex.Message);
        }
    }

    private static bool IsTerminalSyncFailure(PlaylistSyncResult syncResult)
    {
        if (syncResult.Success)
        {
            return false;
        }

        return string.Equals(syncResult.Message, "Playlist not available.", StringComparison.OrdinalIgnoreCase)
            || string.Equals(syncResult.Message, "No target server selected.", StringComparison.OrdinalIgnoreCase)
            || string.Equals(syncResult.Message, "Playlist sync target is disabled.", StringComparison.OrdinalIgnoreCase)
            || string.Equals(syncResult.Message, "Unsupported playlist sync target.", StringComparison.OrdinalIgnoreCase)
            || string.Equals(syncResult.Message, "No eligible tracks after blocked/ignored filtering.", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> IsFinalizedTrackSyncedAsync(
        LibraryRepository repository,
        PlaylistWatchlistDto playlist,
        string trackId,
        string targetService,
        CancellationToken cancellationToken)
        => await repository.IsPlaylistWatchTrackSyncedToTargetAsync(
            playlist.Source,
            playlist.SourceId,
            trackId,
            targetService,
            cancellationToken);

    private static bool IsConfiguredTarget(PlaylistWatchPreferenceDto preference, string targetService)
    {
        var targets = preference.SyncTargets is { Count: > 0 }
            ? preference.SyncTargets
            : [preference.Service ?? string.Empty];
        return targets.Any(target => string.Equals(target, targetService, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task AddPlaylistSyncHistoryAsync(
        IServiceProvider serviceProvider,
        PlaylistWatchlistDto playlist,
        WatchlistHistoryStatus status,
        CancellationToken cancellationToken)
    {
        await serviceProvider.GetRequiredService<WatchlistHistoryService>().RecordAsync(
            new WatchlistHistoryWrite(
                playlist.Source,
                "playlist",
                playlist.SourceId,
                WatchlistHistoryService.PlaylistItemKey(playlist.Source, playlist.SourceId),
                playlist.Name,
                "playlist",
                playlist.TrackCount ?? 0,
                status,
                null),
            cancellationToken);
    }

    private static Task TransitionPlaylistStateAsync(
        IServiceProvider serviceProvider,
        PlaylistWatchlistDto playlist,
        WatchlistPlaylistState state,
        string? message,
        CancellationToken cancellationToken)
        => serviceProvider.GetRequiredService<WatchlistStateService>().TransitionPlaylistAsync(
            new WatchlistPlaylistStateTransition(
                playlist.Source,
                playlist.SourceId,
                state,
                message,
                playlist.TrackCount,
                playlist.SnapshotId,
                NextAttemptUtc: null,
                ConsecutiveFailures: state == WatchlistPlaylistState.MediaSyncBlocked ? 1 : 0,
                TouchLastChecked: false),
            cancellationToken);

    private void LogPlaylistMissing(SyncRequest request)
    {
        _logger.LogWarning(
            "Watchlist playlist sync skipped because playlist no longer exists: {Source}:{PlaylistId}.",
            request.Source,
            request.PlaylistId);
    }

    private SyncRequest ResolveEffectiveRequest(SyncRequest request, PlaylistWatchPreferenceDto? preference)
    {
        var preferenceDestinationFolderId = preference?.DestinationFolderId;
        if (request.DestinationFolderId.HasValue || !preferenceDestinationFolderId.HasValue)
        {
            return request;
        }

        var destinationFolderId = preferenceDestinationFolderId.Value;
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Watchlist playlist sync resolved destination folder {DestinationFolderId} from playlist preference for {Source}:{PlaylistId}.",
                destinationFolderId,
                request.Source,
                request.PlaylistId);
        }

        return request with { DestinationFolderId = destinationFolderId };
    }

    private void LogSyncCompleted(SyncRequest request, int attempt, int syncedTracks)
    {
        if (!_logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        _logger.LogInformation(
            "Watchlist playlist sync completed for {Source}:{PlaylistId} after finalized track {TrackId} (attempt {Attempt}, syncedTracks={SyncedTracks}).",
            request.Source,
            request.PlaylistId,
            request.TrackId,
            attempt,
            syncedTracks);
    }

    private void LogSyncNotReady(SyncRequest request, int attempt, string message)
    {
        if (!_logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        _logger.LogInformation(
            "Watchlist playlist sync not ready for {Source}:{PlaylistId} after finalized track {TrackId} (attempt {Attempt}): {Message}",
            request.Source,
            request.PlaylistId,
            request.TrackId,
            attempt,
            message);
    }

    private static async Task<PlaylistWatchlistDto?> FindPlaylistAsync(
        LibraryRepository repository,
        SyncRequest request,
        CancellationToken cancellationToken)
    {
        var items = await repository.GetPlaylistWatchlistAsync(cancellationToken);
        return items.FirstOrDefault(item =>
            string.Equals(item.Source, request.Source, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.SourceId, request.PlaylistId, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<bool> VerifyLocalLibraryIngestionAsync(
        IServiceProvider services,
        SyncRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.DestinationFolderId.HasValue)
        {
            return true;
        }

        var ingestionService = services.GetService<KnownLibraryFileIngestionService>();
        if (ingestionService == null)
        {
            return true;
        }

        if (request.ChangedFilePaths.Count > 0)
        {
            var ingestion = await ingestionService.VerifyAsync(
                new Dictionary<long, List<string>>
                {
                    [request.DestinationFolderId.Value] = request.ChangedFilePaths.ToList()
                },
                cancellationToken);
            return ingestion.IsComplete;
        }

        // Missing final paths are a notifier bug. The sync must be driven by real destination files.
        var configStore = services.GetService<LibraryConfigStore>();
        configStore?.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "warning",
            $"Watchlist playlist direct library ingestion skipped because no final file paths were provided for {request.Source}:{request.PlaylistId}:{request.TrackId} (destinationFolderId={request.DestinationFolderId})."));
        return false;
    }

    private static List<string> NormalizeChangedFilePaths(IReadOnlyList<string>? changedFilePaths)
    {
        if (changedFilePaths is null || changedFilePaths.Count == 0)
        {
            return new List<string>();
        }

        return changedFilePaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task RefreshMediaServerAsync(
        IServiceProvider services,
        string targetService,
        CancellationToken cancellationToken)
    {
        var refreshService = services.GetRequiredService<MediaServerLibraryRefreshService>();
        await refreshService.RefreshAsync(targetService, cancellationToken);
    }

    private sealed record SyncRequest(
        long JobId,
        string Source,
        string PlaylistId,
        string TrackId,
        string TargetService,
        long? DestinationFolderId,
        IReadOnlyList<string> ChangedFilePaths,
        int AttemptCount);

    private enum SyncAttemptOutcomeKind
    {
        Completed,
        Retry,
        Obsolete,
        RepairRequired,
        Blocked
    }

    private sealed record SyncAttemptOutcome(SyncAttemptOutcomeKind Kind, string Message)
    {
        public static SyncAttemptOutcome Completed(string message) => new(SyncAttemptOutcomeKind.Completed, message);
        public static SyncAttemptOutcome Retry(string message) => new(SyncAttemptOutcomeKind.Retry, message);
        public static SyncAttemptOutcome Obsolete(string message) => new(SyncAttemptOutcomeKind.Obsolete, message);
        public static SyncAttemptOutcome RepairRequired(string message) => new(SyncAttemptOutcomeKind.RepairRequired, message);
        public static SyncAttemptOutcome Blocked(string message) => new(SyncAttemptOutcomeKind.Blocked, message);
    }
}

public enum WatchlistHistoryStatus
{
    Queued,
    Completed,
    Failed,
    Unavailable,
    Deferred,
    MetadataRefreshed,
    SourceUpdated,
    MediaSyncSkippedSyncServiceUnavailable,
    MediaSyncCompleted,
    MediaSyncWaiting,
    MediaSyncBlocked,
    MissingTracksQueued,
    DuplicateSharedTrackLinked,
    WatchlistDisabled,
    MediaSyncDeferredQueueActive,
    QueueBudgetReached,
    TrackQueueDeferred,
    SourceFailure,
    SkippedAlreadyAvailable,
    SkippedAlreadyQueued,
    StaleClaimRecovered,
    SkippedBlocked,
    SkippedUnavailableRecheckWindow
}

public sealed record WatchlistHistoryWrite(
    string Source,
    string WatchType,
    string SourceId,
    string ItemKey,
    string Name,
    string CollectionType,
    int TrackCount,
    WatchlistHistoryStatus Status,
    string? ArtistName);

public sealed class WatchlistHistoryService
{
    private readonly LibraryRepository _repository;
    private readonly ActivitiesRealtimeService? _activitiesRealtime;
    private DateTimeOffset _lastPrunedUtc = DateTimeOffset.MinValue;

    public WatchlistHistoryService(
        LibraryRepository repository,
        ActivitiesRealtimeService? activitiesRealtime)
    {
        _repository = repository;
        _activitiesRealtime = activitiesRealtime;
    }

    public async Task<WatchlistHistoryDto?> RecordAsync(
        WatchlistHistoryWrite write,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(write.ItemKey))
        {
            throw new ArgumentException("A stable Watchlist item key is required.", nameof(write));
        }

        WatchlistHistoryDto? entry;
        try
        {
            entry = await _repository.AddWatchlistHistoryAsync(
                new WatchlistHistoryInsert(
                    write.Source,
                    write.WatchType,
                    write.SourceId,
                    write.Name,
                    write.CollectionType,
                    Math.Max(0, write.TrackCount),
                    ToPersistedStatus(write.Status),
                    write.ArtistName,
                    write.ItemKey),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            // History is an audit projection. It must never become a transaction boundary for
            // source reconciliation, queue ownership, or post-download synchronization.
            return null;
        }
        try
        {
            if (entry != null)
            {
                _activitiesRealtime?.PublishWatchlistHistoryChanged(entry);
            }

            if (DateTimeOffset.UtcNow - _lastPrunedUtc >= TimeSpan.FromHours(24))
            {
                await _repository.PruneWatchlistHistoryAsync(
                    DateTimeOffset.UtcNow.AddDays(-90),
                    maximumRows: 50_000,
                    cancellationToken);
                _lastPrunedUtc = DateTimeOffset.UtcNow;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            // The history row was already persisted; projection notification and retention are
            // also best-effort and cannot make the calling Watchlist transaction fail.
        }

        return entry;
    }

    public static string PlaylistItemKey(string source, string sourceId)
        => $"playlist:{source.Trim().ToLowerInvariant()}:{sourceId.Trim()}";

    public static string ArtistItemKey(long artistId)
        => $"artist:{artistId}";

    public static string ToPersistedStatus(WatchlistHistoryStatus status)
    {
        var value = status.ToString();
        var builder = new System.Text.StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsUpper(character) && index > 0)
            {
                builder.Append('_');
            }
            builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }
}
