using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public enum WatchQueueStopReason
{
    None,
    WatchlistDisabled,
    DownloadGate,
    RunBudget,
    TrackDeferred,
    SystemicFailure,
    Completed,
    TrackFailures
}

public enum PlaylistReconciliationMode
{
    SyncAndQueue,
    SyncOnly
}

public readonly record struct WatchlistQueueAdmissionDecision(
    bool Allowed,
    WatchQueueStopReason Reason,
    DateTimeOffset? RetryAtUtc,
    bool AdvanceToNextItem,
    string? Message)
{
    public static WatchlistQueueAdmissionDecision Allow()
        => new(true, WatchQueueStopReason.None, null, false, null);
}

public enum WatchlistPlaylistState
{
    Pending,
    HeadFetching,
    Unchanged,
    Expanding,
    DeltaDetected,
    Reconciling,
    Syncing,
    Queued,
    Completed,
    Unavailable,
    Failed,
    Backoff,
    CircuitOpen,
    WatchlistDisabled,
    MediaSyncDeferredQueueActive,
    QueueBudgetReached,
    TrackQueueDeferred,
    SourceFailure,
    Deferred,
    SyncConfigurationError,
    WaitingForDownloads,
    WaitingForTargetSync,
    MediaSyncCompleted,
    MediaSyncWaiting,
    MediaSyncBlocked,
    MetadataRefreshed,
    ConfigurationRequired
}

public sealed record WatchlistPlaylistStateTransition(
    string Source,
    string SourceId,
    WatchlistPlaylistState State,
    string? Message,
    int? TrackCount,
    string? SnapshotId,
    DateTimeOffset? NextAttemptUtc,
    int? ConsecutiveFailures,
    bool TouchLastChecked = true);

public sealed class WatchlistStateService
{
    private readonly LibraryRepository _repository;

    public WatchlistStateService(LibraryRepository repository)
    {
        _repository = repository;
    }

    public async Task TransitionPlaylistAsync(
        WatchlistPlaylistStateTransition transition,
        CancellationToken cancellationToken)
    {
        var state = await _repository.GetPlaylistWatchStateAsync(
            transition.Source,
            transition.SourceId,
            cancellationToken);
        await _repository.UpsertPlaylistWatchStateAsync(
            new LibraryRepository.PlaylistWatchStateUpsertInput(
                transition.Source,
                transition.SourceId,
                NormalizeSnapshotId(transition.SnapshotId) ?? state?.SnapshotId,
                transition.TrackCount ?? state?.TrackCount,
                state?.BatchNextOffset,
                state?.BatchProcessingSnapshotId,
                transition.TouchLastChecked ? DateTimeOffset.UtcNow : state?.LastCheckedUtc,
                ToPersistedStatus(transition.State),
                transition.Message,
                transition.NextAttemptUtc,
                transition.ConsecutiveFailures ?? state?.ConsecutiveFailures,
                CurrentPhase: ToPersistedStatus(transition.State),
                CurrentTrackIndex: state?.CurrentTrackIndex,
                CurrentTrackTotal: transition.TrackCount ?? state?.CurrentTrackTotal,
                HeartbeatUtc: DateTimeOffset.UtcNow,
                DeadlineUtc: transition.State is WatchlistPlaylistState.Completed
                    or WatchlistPlaylistState.Failed
                    or WatchlistPlaylistState.Backoff
                    or WatchlistPlaylistState.SourceFailure
                    ? null
                    : state?.DeadlineUtc ?? DateTimeOffset.UtcNow.AddMinutes(15)),
            cancellationToken);
    }

    public static WatchlistPlaylistState Parse(string status)
        => status.Trim().ToLowerInvariant() switch
        {
            "pending" => WatchlistPlaylistState.Pending,
            "head_fetching" => WatchlistPlaylistState.HeadFetching,
            "unchanged" => WatchlistPlaylistState.Unchanged,
            "expanding" => WatchlistPlaylistState.Expanding,
            "delta_detected" => WatchlistPlaylistState.DeltaDetected,
            "reconciling" => WatchlistPlaylistState.Reconciling,
            "syncing" => WatchlistPlaylistState.Syncing,
            "queued" => WatchlistPlaylistState.Queued,
            "completed" => WatchlistPlaylistState.Completed,
            "unavailable" => WatchlistPlaylistState.Unavailable,
            "failed" => WatchlistPlaylistState.Failed,
            "backoff" => WatchlistPlaylistState.Backoff,
            "circuit_open" => WatchlistPlaylistState.CircuitOpen,
            "watchlist_disabled" => WatchlistPlaylistState.WatchlistDisabled,
            "media_sync_deferred_queue_active" => WatchlistPlaylistState.MediaSyncDeferredQueueActive,
            "queue_budget_reached" => WatchlistPlaylistState.QueueBudgetReached,
            "track_queue_deferred" => WatchlistPlaylistState.TrackQueueDeferred,
            "source_failure" => WatchlistPlaylistState.SourceFailure,
            "deferred" => WatchlistPlaylistState.Deferred,
            "sync_configuration_error" => WatchlistPlaylistState.SyncConfigurationError,
            "waiting_for_downloads" => WatchlistPlaylistState.WaitingForDownloads,
            "waiting_for_target_sync" => WatchlistPlaylistState.WaitingForTargetSync,
            "media_sync_completed" => WatchlistPlaylistState.MediaSyncCompleted,
            "media_sync_waiting" => WatchlistPlaylistState.MediaSyncWaiting,
            "media_sync_blocked" => WatchlistPlaylistState.MediaSyncBlocked,
            "metadata_refreshed" => WatchlistPlaylistState.MetadataRefreshed,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown Watchlist playlist state.")
        };

    public static string ToPersistedStatus(WatchlistPlaylistState state)
        => state switch
        {
            WatchlistPlaylistState.HeadFetching => "head_fetching",
            WatchlistPlaylistState.DeltaDetected => "delta_detected",
            WatchlistPlaylistState.CircuitOpen => "circuit_open",
            WatchlistPlaylistState.WatchlistDisabled => "watchlist_disabled",
            WatchlistPlaylistState.MediaSyncDeferredQueueActive => "media_sync_deferred_queue_active",
            WatchlistPlaylistState.QueueBudgetReached => "queue_budget_reached",
            WatchlistPlaylistState.TrackQueueDeferred => "track_queue_deferred",
            WatchlistPlaylistState.SourceFailure => "source_failure",
            WatchlistPlaylistState.SyncConfigurationError => "sync_configuration_error",
            WatchlistPlaylistState.WaitingForDownloads => "waiting_for_downloads",
            WatchlistPlaylistState.WaitingForTargetSync => "waiting_for_target_sync",
            WatchlistPlaylistState.MediaSyncCompleted => "media_sync_completed",
            WatchlistPlaylistState.MediaSyncWaiting => "media_sync_waiting",
            WatchlistPlaylistState.MediaSyncBlocked => "media_sync_blocked",
            WatchlistPlaylistState.MetadataRefreshed => "metadata_refreshed",
            _ => ToSnakeCase(state.ToString())
        };

    private static string ToSnakeCase(string value)
    {
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

    private static string? NormalizeSnapshotId(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class WatchlistQueueAdmissionService
{
    private readonly object _gate = new();
    private readonly AsyncLocal<long> _executionGeneration = new();
    private long _generation;
    private long _activeGeneration;
    private int _limit;
    private int _remaining;

    public async Task<WatchlistQueueAdmissionDecision> EvaluateDownloadGateAsync(
        DownloadOrchestrationService orchestrationService,
        CancellationToken cancellationToken)
    {
        var gate = await orchestrationService.EvaluateDownloadGateAsync(cancellationToken);
        return gate.Allowed
            ? WatchlistQueueAdmissionDecision.Allow()
            : new WatchlistQueueAdmissionDecision(
                false,
                WatchQueueStopReason.DownloadGate,
                null,
                true,
                gate.Message);
    }

    public async Task<WatchlistQueueAdmissionDecision> EvaluateQueueGateAsync(
        DownloadQueueRepository queueRepository,
        DownloadOrchestrationService orchestrationService,
        CancellationToken cancellationToken)
    {
        var repositoryGate = await EvaluateQueueGateAsync(queueRepository, cancellationToken);
        if (!repositoryGate.Allowed)
        {
            return repositoryGate;
        }

        return await EvaluateDownloadGateAsync(orchestrationService, cancellationToken);
    }

    public async Task<WatchlistQueueAdmissionDecision> EvaluateQueueGateAsync(
        DownloadQueueRepository queueRepository,
        CancellationToken cancellationToken)
    {
        if (await queueRepository.HasActiveDownloadPipelineAsync(cancellationToken))
        {
            return new WatchlistQueueAdmissionDecision(
                false,
                WatchQueueStopReason.DownloadGate,
                null,
                true,
                "Waiting for active downloads, moves, or enrichment to finish.");
        }

        return WatchlistQueueAdmissionDecision.Allow();
    }

    public WatchlistQueueAdmissionDecision TryAdmitTrack(int queueItemCount = 1)
    {
        if (TryReserve(queueItemCount))
        {
            return WatchlistQueueAdmissionDecision.Allow();
        }

        return new WatchlistQueueAdmissionDecision(
            false,
            WatchQueueStopReason.RunBudget,
            null,
            false,
            "Watchlist run queue budget reached.");
    }

    public long BeginRun(int queueBudget)
    {
        lock (_gate)
        {
            _generation++;
            _activeGeneration = _generation;
            _executionGeneration.Value = _activeGeneration;
            _limit = Math.Max(0, queueBudget);
            _remaining = _limit;
            return _activeGeneration;
        }
    }

    public long BeginRunIfInactive(int queueBudget)
    {
        lock (_gate)
        {
            if (_activeGeneration != 0)
            {
                return 0;
            }

            _generation++;
            _activeGeneration = _generation;
            _executionGeneration.Value = _activeGeneration;
            _limit = Math.Max(0, queueBudget);
            _remaining = _limit;
            return _activeGeneration;
        }
    }

    public void EndRun(long token)
    {
        lock (_gate)
        {
            if (_activeGeneration != token)
            {
                return;
            }

            _activeGeneration = 0;
            _executionGeneration.Value = 0;
            _limit = 0;
            _remaining = 0;
        }
    }

    public int GetRemaining()
    {
        lock (_gate)
        {
            return _activeGeneration == 0 || _executionGeneration.Value != _activeGeneration
                ? 0
                : _remaining;
        }
    }

    public bool TryReserve(int queueItemCount)
    {
        if (queueItemCount <= 0)
        {
            return false;
        }

        lock (_gate)
        {
            if (_activeGeneration == 0
                || _executionGeneration.Value != _activeGeneration
                || _remaining < queueItemCount)
            {
                return false;
            }

            _remaining -= queueItemCount;
            return true;
        }
    }

    public void Release(int queueItemCount)
    {
        if (queueItemCount <= 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_activeGeneration == 0 || _executionGeneration.Value != _activeGeneration)
            {
                return;
            }

            _remaining = Math.Min(_limit, _remaining + queueItemCount);
        }
    }
}
