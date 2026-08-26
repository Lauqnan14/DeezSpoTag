using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using System.Text.Json;

namespace DeezSpoTag.Web.Services;

public sealed class WatchlistPostDownloadSyncService : IWatchlistPostDownloadSyncNotifier
{
    private static readonly TimeSpan ProcessingLease = TimeSpan.FromMinutes(15);
    private const int TargetSyncClaimBatchSize = 1;
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromMinutes(10);
    // Backoff is 15 * 2^(attempt-1) capped at MaximumRetryDelay (10 min from attempt 7 on), so 10
    // attempts is roughly 1-1.5h of accumulated retrying -- long enough to ride out a transient
    // blip (target server restart, brief network issue) but short enough that a structurally
    // unfixable failure (one track that will never verify, an oversized batch call) stops
    // consuming a retry slot forever and instead surfaces as "blocked" for manual attention.
    internal const int MaxSyncAttempts = 10;
    // Mirrors WatchlistRunCoordinator's per-source circuit breaker (SourceCircuitFailureThreshold
    // / SourceCircuitCooldownSeconds), but keyed by target media server instead of playlist
    // source: with up to 45 playlists x 3 targets, a single down/flaky target server would
    // otherwise mean dozens of independent jobs all discovering that and retrying on their own
    // schedules. A shared circuit lets them all back off together as one unit.
    private const int TargetCircuitFailureThreshold = 5;
    private const int TargetCircuitCooldownSeconds = 300;
    private const string PlaylistJobTrackId = "playlist";
    private const string ArtworkJobTrackIdPrefix = "artwork:";
    private readonly WatchlistRunSignal _coordinatorSignal;
    private readonly IServiceProvider _serviceProvider;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly ILogger<WatchlistPostDownloadSyncService> _logger;
    private readonly string _leaseOwner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
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

    public async ValueTask RequestPlaylistSyncAsync(
        string source,
        string playlistId,
        CancellationToken cancellationToken = default)
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
            "playlist",
            source,
            playlistId,
            cancellationToken);
        if (accepted)
        {
            _coordinatorSignal.Request(WatchlistWakeReason.Reconciliation);
        }
    }

    public async Task ProcessFinalizationWorkAsync(
        CancellationToken cancellationToken,
        Func<bool>? shouldStop = null)
    {
        try
        {
            await RepairMissingFinalizationOutboxAsync(cancellationToken);
            await ProcessFinalizationOutboxAsync(cancellationToken, shouldStop);
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

    public async Task<int> ProcessTargetSyncWorkAsync(
        TargetSyncBudget budget,
        CancellationToken cancellationToken)
    {
        var processed = 0;
        try
        {
            await RepairIncompleteJobsIfNeededAsync(cancellationToken);
            if (!IsWatchlistEnabled())
            {
                return processed;
            }

            using var scope = _serviceProvider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<LibraryRepository>();
            if (!repository.IsConfigured)
            {
                return processed;
            }

            var excludedJobIds = new List<long>();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (budget.ShouldStop?.Invoke() == true)
                {
                    break;
                }
                var jobs = await repository.ClaimDueWatchlistSyncJobsAsync(
                    TargetSyncClaimBatchSize,
                    ProcessingLease,
                    _leaseOwner,
                    budget.PlaylistFilter?.Source,
                    budget.PlaylistFilter?.PlaylistId,
                    budget.Kind,
                    excludedJobIds,
                    cancellationToken);
                if (jobs.Count == 0)
                {
                    break;
                }

                foreach (var job in jobs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    excludedJobIds.Add(job.Id);
                    await ProcessClaimedJobAsync(
                        repository,
                        job,
                        cancellationToken);
                    processed++;
                    if (budget.OnProgress != null)
                    {
                        await budget.OnProgress(cancellationToken);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Watchlist coordinator target sync phase failed; the next coordinator cycle will retry.");
        }

        return processed;
    }

    private async Task ProcessFinalizationOutboxAsync(
        CancellationToken cancellationToken,
        Func<bool>? shouldStop)
    {
        while (true)
        {
            if (shouldStop?.Invoke() == true)
            {
                return;
            }
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
        var leaseRenewal = RenewLeaseAsync(repository, job, leaseRenewalCancellation.Token);
        try
        {
            var targetCircuit = await repository.GetWatchlistTargetCircuitStateAsync(job.TargetService, cancellationToken);
            if (IsTargetCircuitOpen(targetCircuit))
            {
                // Defer without counting it as an attempt against this job -- the target itself
                // is the problem, not this specific track/playlist, so it shouldn't burn down
                // this job's own MaxSyncAttempts budget while the whole target is unavailable.
                await repository.RetryWatchlistSyncJobAsync(
                    job.Id,
                    _leaseOwner,
                    job.AttemptCount,
                    targetCircuit!.OpenUntilUtc ?? DateTimeOffset.UtcNow.AddSeconds(TargetCircuitCooldownSeconds),
                    $"{FormatTargetServiceLabel(job.TargetService)} sync is temporarily paused after repeated {FormatCircuitFailureClass(targetCircuit.Reason)} failures.",
                    cancellationToken);
                return;
            }

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
                cancellationToken);
            switch (outcome.Kind)
            {
                case SyncAttemptOutcomeKind.Completed:
                    await ResetTargetCircuitAsync(repository, job.TargetService, targetCircuit, cancellationToken);
                    var completed = IsPlaylistJob(job.TrackId)
                        ? await repository.CompleteWatchlistPlaylistSyncJobAsync(
                            job,
                            _leaseOwner,
                            outcome.AppliedKind ?? WatchlistAppliedKind.Full,
                            outcome.MembershipHash,
                            outcome.SourcePlaylistId,
                            cancellationToken)
                        : await repository.CompleteWatchlistSyncJobAsync(job.Id, _leaseOwner, cancellationToken);
                    if (completed)
                    {
                        if (IsPlaylistJob(job.TrackId)
                            && outcome.AppliedKind is WatchlistAppliedKind.Partial
                                or WatchlistAppliedKind.WaitingForSeed)
                        {
                            await repository.EnqueueMembershipJobsForResolvedUnsyncedIdentitiesAsync(
                                job.Source,
                                job.PlaylistId,
                                job.TargetService,
                                job.SnapshotId ?? string.Empty,
                                cancellationToken);
                        }
                    }
                    return;
                case SyncAttemptOutcomeKind.Obsolete:
                    await repository.DeleteObsoleteWatchlistSyncJobAsync(job, _leaseOwner, cancellationToken);
                    return;
                case SyncAttemptOutcomeKind.Blocked:
                    await repository.BlockWatchlistSyncJobAsync(job.Id, _leaseOwner, outcome.Message, cancellationToken);
                    return;
            }
            if (!IsArtworkJob(job.TrackId)
                && ShouldIncrementTargetCircuit(outcome.FailureClass))
            {
                await RecordTargetCircuitFailureAsync(
                    repository,
                    job.TargetService,
                    targetCircuit,
                    outcome.FailureClass,
                    outcome.Message,
                    cancellationToken);
            }

            var deferWithoutBurningAttempts = outcome.FailureClass is SyncFailureClass.IdentityMiss
                or SyncFailureClass.None;
            var attempt = deferWithoutBurningAttempts
                ? job.AttemptCount
                : job.AttemptCount + 1;
            if (attempt >= MaxSyncAttempts)
            {
                await repository.BlockWatchlistSyncJobAsync(
                    job.Id,
                    _leaseOwner,
                    $"Gave up after {attempt} attempts: {outcome.Message}",
                    cancellationToken);
                _logger.LogWarning(
                    "Watchlist target sync job {JobId} ({Source}:{PlaylistId} track={TrackId} target={TargetService}) blocked after {Attempt} attempts. Last error: {LastError}",
                    job.Id,
                    job.Source,
                    job.PlaylistId,
                    job.TrackId,
                    job.TargetService,
                    attempt,
                    outcome.Message);
                return;
            }

            var retryDelay = deferWithoutBurningAttempts
                ? TimeSpan.FromMinutes(2)
                : TimeSpan.FromSeconds(Math.Min(MaximumRetryDelay.TotalSeconds, 15 * Math.Pow(2, Math.Min(Math.Max(attempt, 1) - 1, 6))));
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
            var unexpectedAttempt = job.AttemptCount + 1;
            if (unexpectedAttempt >= MaxSyncAttempts)
            {
                _logger.LogWarning(
                    ex,
                    "Watchlist target sync job {JobId} blocked after {Attempt} unexpected failures.",
                    job.Id,
                    unexpectedAttempt);
                await repository.BlockWatchlistSyncJobAsync(
                    job.Id,
                    _leaseOwner,
                    $"Gave up after {unexpectedAttempt} attempts: {ex.Message}",
                    cancellationToken);
                return;
            }

            _logger.LogWarning(
                ex,
                "Watchlist target sync job {JobId} failed unexpectedly; returning it to durable retry.",
                job.Id);
            await repository.RetryWatchlistSyncJobAsync(
                job.Id,
                _leaseOwner,
                unexpectedAttempt,
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

    private async Task RenewLeaseAsync(LibraryRepository repository, WatchlistSyncJobDto job, CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromTicks(ProcessingLease.Ticks / 3);
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(interval, cancellationToken);
            if (!await repository.RenewWatchlistSyncJobLeaseAsync(job.Id, _leaseOwner, ProcessingLease, cancellationToken))
            {
                return;
            }

            if (IsPlaylistJob(job.TrackId))
            {
                await repository.TouchPlaylistWatchHeartbeatAsync(
                    job.Source,
                    job.PlaylistId,
                    TimeSpan.FromMinutes(45),
                    cancellationToken);
            }
        }
    }

    private async Task<SyncAttemptOutcome> TrySyncOnceAsync(
        SyncRequest request,
        int attempt,
        CancellationToken cancellationToken)
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

            if (IsArtworkJob(request.TrackId))
            {
                var revision = request.TrackId[ArtworkJobTrackIdPrefix.Length..].Trim();
                var playlistVisualService = scope.ServiceProvider.GetRequiredService<PlaylistVisualService>();
                var activeRevision = playlistVisualService.GetTargetArtworkRevision(
                    playlist.Source,
                    playlist.SourceId,
                    request.TargetService);
                if (string.IsNullOrWhiteSpace(activeRevision))
                {
                    var refreshedPlaylist = await scope.ServiceProvider.GetRequiredService<PlaylistWatchReconciler>()
                        .RefreshPlaylistMetadataOnlyAsync(playlist, cancellationToken, forceArtworkRefresh: true);
                    playlist = refreshedPlaylist;
                    activeRevision = playlistVisualService.GetTargetArtworkRevision(
                        playlist.Source,
                        playlist.SourceId,
                        request.TargetService);
                }

                if (string.IsNullOrWhiteSpace(activeRevision)
                    || !string.Equals(activeRevision, revision, StringComparison.OrdinalIgnoreCase))
                {
                    return SyncAttemptOutcome.Obsolete("A newer playlist artwork revision is active.");
                }

                if (await repository.IsPlaylistWatchArtworkRevisionAppliedAsync(
                        playlist.Source,
                        playlist.SourceId,
                        request.TargetService,
                        revision,
                        cancellationToken))
                {
                    return SyncAttemptOutcome.Completed("Playlist artwork revision is already applied.");
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
                LogSyncCompleted(request, attempt, syncResult.SyncedTracks);
                return SyncAttemptOutcome.Completed(
                    syncResult.Message,
                    MapAppliedKind(syncResult, request.TargetService),
                    BuildMembershipHash(syncResult),
                    playlist.SourceId);
            }

            var terminalFailure = IsTerminalSyncFailure(syncResult);
            LogSyncNotReady(request, attempt, syncResult.Message);
            var failureClass = ClassifySyncFailureClass(syncResult);
            return terminalFailure
                ? SyncAttemptOutcome.Blocked(syncResult.Message, failureClass)
                : SyncAttemptOutcome.Retry(syncResult.Message, failureClass);
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

        return syncResult.Kind == PlaylistSyncResultKind.Blocked
            || PlaylistSyncResult.IsBlockedConfigMessage(syncResult.Message);
    }

    internal static SyncFailureClass ClassifySyncFailureClass(PlaylistSyncResult syncResult)
    {
        if (syncResult.Success)
        {
            return SyncFailureClass.None;
        }

        if (syncResult.Kind == PlaylistSyncResultKind.Blocked)
        {
            return SyncFailureClass.Config;
        }

        if (syncResult.Kind == PlaylistSyncResultKind.WriteLag)
        {
            return SyncFailureClass.IdentityMiss;
        }

        return ClassifyRetryFailureClass(syncResult.Message);
    }

    internal static SyncFailureClass ClassifyRetryFailureClass(string? message)
    {
        var text = message ?? string.Empty;
        var lower = text.ToLowerInvariant();
        if (lower.Contains("region_blocked", StringComparison.Ordinal)
            || lower.Contains("geo_restricted", StringComparison.Ordinal)
            || lower.Contains("not available in your country", StringComparison.Ordinal))
        {
            return SyncFailureClass.IdentityMiss;
        }

        if (lower.Contains("is not configured.", StringComparison.Ordinal)
            || lower.Contains("unauthorized", StringComparison.Ordinal)
            || (lower.Contains("forbidden", StringComparison.Ordinal) && !lower.Contains("region_blocked", StringComparison.Ordinal))
            || lower.Contains("401", StringComparison.Ordinal)
            || lower.Contains("403", StringComparison.Ordinal))
        {
            return SyncFailureClass.Auth;
        }

        if (PlaylistSyncResult.IsNoTargetMatchesMessage(text)
            || PlaylistSyncResult.IsLibraryEmptyMessage(text)
            || lower.Contains("verification is incomplete", StringComparison.Ordinal)
            || lower.Contains("source tracks:", StringComparison.Ordinal)
            || lower.Contains("waiting for", StringComparison.Ordinal) && lower.Contains("index finalized", StringComparison.Ordinal))
        {
            return SyncFailureClass.IdentityMiss;
        }

        if (lower.Contains("waiting for the durable playlist reconciliation", StringComparison.Ordinal)
            || PlaylistSyncResult.IsSourceLoadMessage(text))
        {
            return SyncFailureClass.None;
        }

        if (lower.Contains("reorder", StringComparison.Ordinal)
            && (lower.Contains("not supported", StringComparison.Ordinal)
                || lower.Contains("unsupported", StringComparison.Ordinal)))
        {
            return SyncFailureClass.ReorderUnsupported;
        }

        return SyncFailureClass.Transport;
    }

    private static bool ShouldIncrementTargetCircuit(SyncFailureClass failureClass)
        => failureClass is SyncFailureClass.Transport or SyncFailureClass.Auth;

    private static WatchlistAppliedKind MapAppliedKind(PlaylistSyncResult result, string targetService)
    {
        if (result.Kind == PlaylistSyncResultKind.IdentityGap)
        {
            if (string.Equals(targetService, "plex", StringComparison.OrdinalIgnoreCase)
                && result.TargetMatches == 0)
            {
                return WatchlistAppliedKind.WaitingForSeed;
            }

            return WatchlistAppliedKind.Partial;
        }

        return WatchlistAppliedKind.Full;
    }

    private static string? BuildMembershipHash(PlaylistSyncResult result)
    {
        var payload = string.Join(
            "\u001F",
            result.PlaylistId ?? string.Empty,
            result.TargetMatches.ToString(System.Globalization.CultureInfo.InvariantCulture),
            result.SyncedTracks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            result.Kind.ToString());
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload)));
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

    private static bool IsTargetCircuitOpen(WatchlistTargetCircuitStateDto? circuitState)
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

    private static bool IsArtworkJob(string? trackId)
        => trackId?.StartsWith(ArtworkJobTrackIdPrefix, StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsPlaylistJob(string? trackId)
        => string.Equals(trackId, PlaylistJobTrackId, StringComparison.OrdinalIgnoreCase);

    private static string FormatCircuitFailureClass(string? reason)
    {
        var text = reason ?? string.Empty;
        if (text.Contains("Source tracks:", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Target matches:", StringComparison.OrdinalIgnoreCase)
            || text.Contains("verification is incomplete", StringComparison.OrdinalIgnoreCase))
        {
            return "transport";
        }

        var parts = text.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2
            && Enum.TryParse<SyncFailureClass>(parts[1], ignoreCase: true, out var parsed)
            && parsed is SyncFailureClass.Transport or SyncFailureClass.Auth)
        {
            return parsed.ToString().ToLowerInvariant();
        }

        return "transport";
    }

    private async Task RecordTargetCircuitFailureAsync(
        LibraryRepository repository,
        string targetService,
        WatchlistTargetCircuitStateDto? existing,
        SyncFailureClass failureClass,
        string? reason,
        CancellationToken cancellationToken)
    {
        var failureCount = Math.Max(0, existing?.FailureCount ?? 0) + 1;
        var isOpen = failureCount >= TargetCircuitFailureThreshold;
        var openUntilUtc = isOpen
            ? DateTimeOffset.UtcNow.AddSeconds(TargetCircuitCooldownSeconds)
            : existing?.OpenUntilUtc;
        var fingerprint = $"{NormalizeTargetService(targetService)}:{failureClass}:0";

        await repository.UpsertWatchlistTargetCircuitStateAsync(
            new LibraryRepository.WatchlistTargetCircuitStateUpsertInput(
                targetService,
                isOpen,
                openUntilUtc,
                fingerprint,
                failureCount),
            cancellationToken);

        if (isOpen && existing?.IsOpen != true && _logger.IsEnabled(LogLevel.Warning))
        {
            _logger.LogWarning(
                "Watchlist target sync circuit opened for {TargetService} after {FailureCount} failures; pausing sync jobs against it for {CooldownSeconds}s. Last error: {LastError}",
                FormatTargetServiceLabel(targetService),
                failureCount,
                TargetCircuitCooldownSeconds,
                reason);
        }
    }

    private static async Task ResetTargetCircuitAsync(
        LibraryRepository repository,
        string targetService,
        WatchlistTargetCircuitStateDto? existing,
        CancellationToken cancellationToken)
    {
        if (existing == null || (existing.FailureCount <= 0 && !existing.IsOpen))
        {
            // Already clean -- avoid a write on every single successful sync.
            return;
        }

        await repository.UpsertWatchlistTargetCircuitStateAsync(
            new LibraryRepository.WatchlistTargetCircuitStateUpsertInput(
                targetService,
                IsOpen: false,
                OpenUntilUtc: null,
                Reason: null,
                FailureCount: 0),
            cancellationToken);
    }

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

    private sealed record SyncAttemptOutcome(
        SyncAttemptOutcomeKind Kind,
        string Message,
        WatchlistAppliedKind? AppliedKind = null,
        string? MembershipHash = null,
        string? SourcePlaylistId = null,
        SyncFailureClass FailureClass = SyncFailureClass.None)
    {
        public static SyncAttemptOutcome Completed(string message)
            => Completed(message, WatchlistAppliedKind.Full, null, null);

        public static SyncAttemptOutcome Completed(
            string message,
            WatchlistAppliedKind appliedKind,
            string? membershipHash,
            string? sourcePlaylistId)
            => new(
                SyncAttemptOutcomeKind.Completed,
                message,
                appliedKind,
                membershipHash,
                sourcePlaylistId);

        public static SyncAttemptOutcome Retry(string message, SyncFailureClass failureClass = SyncFailureClass.None)
            => new(SyncAttemptOutcomeKind.Retry, message, FailureClass: failureClass);

        public static SyncAttemptOutcome Obsolete(string message) => new(SyncAttemptOutcomeKind.Obsolete, message);

        public static SyncAttemptOutcome Blocked(string message, SyncFailureClass failureClass = SyncFailureClass.Config)
            => new(SyncAttemptOutcomeKind.Blocked, message, FailureClass: failureClass);
    }
}

public sealed record TargetSyncBudget(
    (string Source, string PlaylistId)? PlaylistFilter,
    WatchlistSyncJobKind Kind,
    Func<CancellationToken, Task>? OnProgress = null,
    Func<bool>? ShouldStop = null)
{
    public static TargetSyncBudget DrainAll(
        WatchlistSyncJobKind kind,
        (string Source, string PlaylistId)? playlistFilter = null,
        Func<CancellationToken, Task>? onProgress = null,
        Func<bool>? shouldStop = null)
        => new(
            PlaylistFilter: playlistFilter,
            Kind: kind,
            OnProgress: onProgress,
            ShouldStop: shouldStop);
}

public enum SyncFailureClass
{
    None,
    Config,
    Auth,
    Transport,
    IdentityMiss,
    ReorderUnsupported
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
    MissingTracksQueued,
    DuplicateSharedTrackLinked,
    WatchlistDisabled,
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
