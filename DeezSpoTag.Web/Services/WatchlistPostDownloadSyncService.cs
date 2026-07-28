using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using System.Text.Json;

namespace DeezSpoTag.Web.Services;

public sealed class WatchlistPostDownloadSyncService : IWatchlistPostDownloadSyncNotifier
{
    private static readonly TimeSpan ProcessingLease = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan TargetOperationTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromMinutes(10);
    private readonly WatchlistRunSignal _coordinatorSignal;
    private readonly IServiceProvider _serviceProvider;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly ILogger<WatchlistPostDownloadSyncService> _logger;
    private readonly string _leaseOwner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
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

    public async ValueTask RequestAllPlaylistSyncAsync(CancellationToken cancellationToken = default)
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

        var accepted = await repository.EnqueueWatchlistReconciliationRequestAsync(
            "all",
            source: null,
            identifier: null,
            cancellationToken);
        if (accepted)
        {
            _coordinatorSignal.Request(WatchlistWakeReason.Reconciliation);
        }
    }

    public async Task ProcessFinalizationWorkAsync(
        int finalizationLimit,
        CancellationToken cancellationToken)
    {
        try
        {
            await RepairMissingFinalizationOutboxAsync(cancellationToken);
            await ProcessFinalizationOutboxAsync(finalizationLimit, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Watchlist coordinator finalization phase failed; the next coordinator cycle will retry.");
        }
    }

    public async Task ProcessTargetSyncWorkAsync(
        int syncJobLimit,
        CancellationToken cancellationToken)
    {
        try
        {
            await RepairIncompleteJobsIfNeededAsync(cancellationToken);
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

            var jobs = await repository.ClaimDueWatchlistSyncJobsAsync(
                Math.Clamp(syncJobLimit, 1, 100),
                ProcessingLease,
                _leaseOwner,
                cancellationToken: cancellationToken);
            await Task.WhenAll(jobs.Select(job =>
                ProcessClaimedJobAsync(repository, job, cancellationToken)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Watchlist coordinator target sync phase failed; the next coordinator cycle will retry.");
        }
    }

    private async Task ProcessFinalizationOutboxAsync(int limit, CancellationToken cancellationToken)
    {
        for (var processed = 0; processed < limit; processed++)
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
                var item = await queueRepository.GetByUuidAsync(work.QueueUuid, cancellationToken)
                    ?? BuildOutboxQueueItem(work.QueueUuid, work.PayloadJson);

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

    private static DeezSpoTag.Services.Download.Queue.DownloadQueueItem BuildOutboxQueueItem(
        string queueUuid,
        string? payloadJson)
    {
        string ReadString(JsonElement root, params string[] names)
        {
            foreach (var name in names)
            {
                if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString() ?? string.Empty;
                }
            }
            return string.Empty;
        }

        long? destinationFolderId = null;
        var artist = string.Empty;
        var title = string.Empty;
        var isrc = string.Empty;
        var spotifyId = string.Empty;
        var durationMs = (int?)null;
        if (!string.IsNullOrWhiteSpace(payloadJson))
        {
            try
            {
                using var document = JsonDocument.Parse(payloadJson);
                var root = document.RootElement;
                artist = ReadString(root, "Artist", "artist");
                title = ReadString(root, "Title", "title", "trackTitle");
                isrc = ReadString(root, "Isrc", "isrc", "ISRC");
                spotifyId = ReadString(root, "SpotifyId", "spotifyId", "spotifyTrackId");
                if (root.TryGetProperty("DestinationFolderId", out var folder)
                    || root.TryGetProperty("destinationFolderId", out folder))
                {
                    destinationFolderId = folder.ValueKind == JsonValueKind.Number && folder.TryGetInt64(out var parsed)
                        ? parsed
                        : null;
                }
                if (root.TryGetProperty("DurationMs", out var duration)
                    || root.TryGetProperty("durationMs", out duration))
                {
                    durationMs = duration.ValueKind == JsonValueKind.Number && duration.TryGetInt32(out var parsed)
                        ? parsed
                        : null;
                }
            }
            catch (JsonException)
            {
                // Claims and finalized paths remain sufficient for durable replay.
            }
        }

        return new DeezSpoTag.Services.Download.Queue.DownloadQueueItem(
            Id: 0,
            QueueUuid: queueUuid,
            Engine: string.Empty,
            ArtistName: artist,
            TrackTitle: title,
            Isrc: isrc,
            DeezerTrackId: null,
            DeezerAlbumId: null,
            DeezerArtistId: null,
            SpotifyTrackId: spotifyId,
            SpotifyAlbumId: null,
            SpotifyArtistId: null,
            AppleTrackId: null,
            AppleAlbumId: null,
            AppleArtistId: null,
            DurationMs: durationMs,
            DestinationFolderId: destinationFolderId,
            QualityRank: null,
            QueueOrder: null,
            ContentType: null,
            Status: "completed",
            PayloadJson: payloadJson,
            Progress: 100,
            Downloaded: 1,
            Failed: 0,
            Error: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);
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
        _lastRepairAttemptUtc = DateTimeOffset.UtcNow;
        var repairedBacklog = await repository.RepairWatchlistSyncBacklogAsync(cancellationToken);
        if (repairedBacklog > 0)
        {
            _logger.LogInformation(
                "Repaired {Count} expired or obsolete Watchlist target synchronization job(s).",
                repairedBacklog);
        }
        var counts = await repository.GetWatchlistSyncJobStatusCountsAsync(cancellationToken);
        if (counts.RepairRequired <= 0)
        {
            return;
        }

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
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationCancellation.CancelAfter(TargetOperationTimeout);
        using var leaseRenewalCancellation = CancellationTokenSource.CreateLinkedTokenSource(operationCancellation.Token);
        var leaseRenewal = RenewLeaseAsync(repository, job.Id, leaseRenewalCancellation.Token);
        try
        {
            var request = new SyncRequest(
                job.Id,
                job.Source,
                job.PlaylistId,
                job.TrackId,
                job.TargetService,
                job.AttemptCount);
            var outcome = await TrySyncOnceAsync(
                request,
                job.AttemptCount + 1,
                operationCancellation.Token);
            switch (outcome.Kind)
            {
                case SyncAttemptOutcomeKind.Completed:
                    var completed = string.Equals(job.TrackId, "playlist", StringComparison.OrdinalIgnoreCase)
                        ? await repository.CompleteWatchlistPlaylistSyncJobAsync(job, _leaseOwner, cancellationToken)
                        : await repository.CompleteWatchlistSyncJobAsync(job.Id, _leaseOwner, cancellationToken);
                    if (completed)
                    {
                        await ResumePlaylistReconciliationAfterInitialSyncAsync(repository, job, cancellationToken);
                    }
                    return;
                case SyncAttemptOutcomeKind.Obsolete:
                    if (await repository.DeleteObsoleteWatchlistSyncJobAsync(job, _leaseOwner, cancellationToken))
                    {
                        await ResumePlaylistReconciliationAfterInitialSyncAsync(repository, job, cancellationToken);
                    }
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
        catch (OperationCanceledException ex) when (operationCancellation.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Watchlist target sync job {JobId} for {Target} was cancelled by the target operation; returning only that target to durable retry.",
                job.Id,
                job.TargetService);
            await repository.RetryWatchlistSyncJobAsync(
                job.Id,
                _leaseOwner,
                job.AttemptCount + 1,
                DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1),
                $"{FormatTargetServiceLabel(job.TargetService)} target operation exceeded {TargetOperationTimeout.TotalMinutes:0} minutes.",
                cancellationToken);
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

    private async Task ResumePlaylistReconciliationAfterInitialSyncAsync(
        LibraryRepository repository,
        WatchlistSyncJobDto completedJob,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(completedJob.TrackId, "playlist", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var remainingInitialJobs = (await repository.GetWatchlistSyncJobsAsync(
                completedJob.Source,
                completedJob.PlaylistId,
                cancellationToken))
            .Any(static job => string.Equals(job.TrackId, "playlist", StringComparison.OrdinalIgnoreCase));
        if (remainingInitialJobs)
        {
            return;
        }

        var state = await repository.GetPlaylistWatchStateAsync(
            completedJob.Source,
            completedJob.PlaylistId,
            cancellationToken);
        if (state == null
            || !string.Equals(
                state.LastRunStatus,
                WatchlistStateService.ToPersistedStatus(WatchlistPlaylistState.WaitingForTargetSync),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await repository.EnqueueWatchlistReconciliationRequestAsync(
            "playlist",
            completedJob.Source,
            completedJob.PlaylistId,
            cancellationToken);
        _coordinatorSignal.Request(WatchlistWakeReason.Reconciliation);
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
        }
    }

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
                    "Completing obsolete Watchlist sync job {JobId}; target {Target} is not configured for {Source}:{PlaylistId}.",
                    request.JobId,
                    request.TargetService,
                    request.Source,
                    request.PlaylistId);
                return SyncAttemptOutcome.Obsolete("Target server is no longer selected.");
            }

            if (request.TrackId.StartsWith("artwork:", StringComparison.OrdinalIgnoreCase))
            {
                var revision = request.TrackId["artwork:".Length..].Trim();
                var activeRevision = scope.ServiceProvider.GetRequiredService<PlaylistVisualService>()
                    .GetActiveArtworkRevision(playlist.Source, playlist.SourceId);
                if (string.IsNullOrWhiteSpace(activeRevision)
                    || !string.Equals(activeRevision, revision, StringComparison.OrdinalIgnoreCase))
                {
                    return SyncAttemptOutcome.Obsolete("A newer playlist artwork revision is active.");
                }

                var artworkResult = await scope.ServiceProvider.GetRequiredService<PlaylistSyncService>()
                    .SyncPlaylistArtworkToTargetAsync(
                        playlist,
                        preference,
                        request.TargetService,
                        cancellationToken);
                await repository.SetPlaylistWatchArtworkTargetStateAsync(
                    playlist.Source,
                    playlist.SourceId,
                    request.TargetService,
                    revision,
                    artworkResult.Success,
                    artworkResult.Success ? null : artworkResult.Message,
                    cancellationToken);
                return artworkResult.Success
                    ? SyncAttemptOutcome.Completed(artworkResult.Message)
                    : SyncAttemptOutcome.Retry(artworkResult.Message);
            }

            if (await repository.HasWatchlistReconciliationRequestAsync(
                    "playlist",
                    playlist.Source,
                    playlist.SourceId,
                    cancellationToken))
            {
                _coordinatorSignal.Request(WatchlistWakeReason.Reconciliation);
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
                _coordinatorSignal.Request(WatchlistWakeReason.Reconciliation);
                return SyncAttemptOutcome.Retry("Playlist candidate cache is unavailable; reconciliation was requested.");
            }

            var syncResult = await scope.ServiceProvider.GetRequiredService<PlaylistSyncService>()
                .SyncAvailablePlaylistTracksAsync(
                playlist,
                preference,
                candidates,
                request.TargetService,
                force: false,
                cancellationToken);

            if (syncResult.Success)
            {
                await AddPlaylistSyncHistoryAsync(
                    scope.ServiceProvider,
                    playlist,
                    WatchlistHistoryStatus.MediaSyncCompleted,
                    cancellationToken);
                LogSyncCompleted(request, attempt, syncResult.SyncedTracks);
                return SyncAttemptOutcome.Completed(syncResult.Message);
            }

            var terminalFailure = IsTerminalSyncFailure(syncResult);
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
                "Watchlist playlist sync attempt {Attempt} failed for {Source}:{PlaylistId}.",
                attempt,
                request.Source,
                request.PlaylistId);
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

    private static bool IsConfiguredTarget(PlaylistWatchPreferenceDto preference, string targetService)
    {
        var normalizedTarget = NormalizeTargetService(targetService);
        if (normalizedTarget is not ("plex" or "jellyfin" or "navidrome"))
        {
            return false;
        }

        var targets = preference.SyncTargets is { Count: > 0 }
            ? preference.SyncTargets
            : [preference.Service ?? string.Empty];
        return targets.Any(target => string.Equals(
            NormalizeTargetService(target),
            normalizedTarget,
            StringComparison.OrdinalIgnoreCase));
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

    private void LogPlaylistMissing(SyncRequest request)
    {
        _logger.LogWarning(
            "Watchlist playlist sync skipped because playlist no longer exists: {Source}:{PlaylistId}.",
            request.Source,
            request.PlaylistId);
    }

    private void LogSyncCompleted(SyncRequest request, int attempt, int syncedTracks)
    {
        if (!_logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        _logger.LogInformation(
            "Watchlist playlist sync completed for {Source}:{PlaylistId} (attempt {Attempt}, syncedTracks={SyncedTracks}).",
            request.Source,
            request.PlaylistId,
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
            "Watchlist playlist sync not ready for {Source}:{PlaylistId} (attempt {Attempt}): {Message}",
            request.Source,
            request.PlaylistId,
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

    private static string NormalizeTargetService(string? target)
        => (target ?? string.Empty).Trim().ToLowerInvariant();

    private static string FormatTargetServiceLabel(string target)
        => NormalizeTargetService(target) switch
        {
            "plex" => "Plex",
            "jellyfin" => "Jellyfin",
            "navidrome" => "Navidrome",
            _ => target
        };

    private sealed record SyncRequest(
        long JobId,
        string Source,
        string PlaylistId,
        string TrackId,
        string TargetService,
        int AttemptCount);

    private enum SyncAttemptOutcomeKind
    {
        Completed,
        Retry,
        Obsolete,
        Blocked
    }

    private sealed record SyncAttemptOutcome(SyncAttemptOutcomeKind Kind, string Message)
    {
        public static SyncAttemptOutcome Completed(string message) => new(SyncAttemptOutcomeKind.Completed, message);
        public static SyncAttemptOutcome Retry(string message) => new(SyncAttemptOutcomeKind.Retry, message);
        public static SyncAttemptOutcome Obsolete(string message) => new(SyncAttemptOutcomeKind.Obsolete, message);
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
