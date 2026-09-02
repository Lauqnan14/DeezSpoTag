using Microsoft.Extensions.Logging;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Settings;

namespace DeezSpoTag.Services.Download.Queue;

public sealed class DownloadQueueRecoveryService
{
    private const string FailedStatus = "failed";
    private int _startupPipelineDemoteDone;
    private readonly DownloadQueueRepository _queueRepository;
    private readonly DownloadCancellationRegistry _cancellationRegistry;
    private readonly DownloadQueueRecoveryRuntime _runtime;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly ILogger<DownloadQueueRecoveryService> _logger;

    public DownloadQueueRecoveryService(
        DownloadQueueRepository queueRepository,
        DownloadCancellationRegistry cancellationRegistry,
        DownloadQueueRecoveryRuntime runtime,
        DeezSpoTagSettingsService settingsService,
        ILogger<DownloadQueueRecoveryService> logger)
    {
        _queueRepository = queueRepository;
        _cancellationRegistry = cancellationRegistry;
        _runtime = runtime;
        _settingsService = settingsService;
        _logger = logger;
    }

    public async Task RecoverStaleRunningTasksAsync(CancellationToken cancellationToken)
    {
        var demoteRunningPipelineStates = Interlocked.Exchange(ref _startupPipelineDemoteDone, 1) == 0;
        await RecoverPendingPostDownloadWorkAsync(demoteRunningPipelineStates, cancellationToken);

        await RecoverRunningItemsOlderThanAsync(
            DownloadQueueRecoveryPolicy.RunningStallThreshold,
            recoverOrphanedOnly: false,
            cancellationToken);

        await RecoverRunningItemsOlderThanAsync(
            DownloadQueueRecoveryPolicy.OrphanedRunningThreshold,
            recoverOrphanedOnly: true,
            cancellationToken);

        await RecoverStalledAcquisitionsAsync(cancellationToken);
    }

    public Task RecoverPendingPostDownloadWorkAsync(CancellationToken cancellationToken)
        => RecoverPendingPostDownloadWorkAsync(demoteRunningPipelineStates: false, cancellationToken);

    public async Task RecoverPendingPostDownloadWorkAsync(
        bool demoteRunningPipelineStates,
        CancellationToken cancellationToken)
    {
        if (demoteRunningPipelineStates)
        {
            await _queueRepository.RecoverExpiredPostDownloadPipelineStatesAsync(cancellationToken);
        }

        if (!TryResolveDownloadRoot(out var downloadRoot))
        {
            return;
        }

        await RecoverPendingPostDownloadWorkAsync(downloadRoot, cancellationToken);
    }

    public async Task RecoverPendingPostDownloadWorkAsync(string downloadRoot, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(downloadRoot))
        {
            return;
        }
        var candidates = await _queueRepository.GetPostDownloadRecoveryCandidatesAsync(cancellationToken);
        foreach (var item in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await TryPromoteToPendingPostDownloadAsync(item, downloadRoot, cancellationToken);
        }
    }

    private async Task<bool> TryPromoteToPendingPostDownloadAsync(
        DownloadQueueItem item,
        string downloadRoot,
        CancellationToken cancellationToken)
    {
        if (!ShouldPromoteToPendingPostDownload(item, downloadRoot, out var destinationFolderId)
            || string.IsNullOrWhiteSpace(item.QueueUuid))
        {
            return false;
        }

        await _runtime.RetryScheduler.ClearAsync(item.QueueUuid, cancellationToken);
        await _queueRepository.PromoteToPendingPostDownloadAsync(
            item.QueueUuid,
            destinationFolderId,
            cancellationToken);

        if (item.DestinationFolderId != destinationFolderId
            && QueuePayloadFileHelper.TryRewriteDestinationFolderId(
                item.PayloadJson ?? string.Empty,
                destinationFolderId,
                out var rewrittenPayload))
        {
            await _queueRepository.UpdatePayloadAsync(item.QueueUuid, rewrittenPayload, cancellationToken);
        }

        _logger.LogInformation(
            "Recovered acquired download {QueueUuid} as completed pending post-download work for destination {DestinationFolderId}.",
            item.QueueUuid,
            destinationFolderId);
        _runtime.ActivityLog.Info(
            $"Download recovered after restart: {item.QueueUuid} pending enrichment/move for destination {destinationFolderId}.");
        _runtime.Listener.Send("updateQueue", new
        {
            uuid = item.QueueUuid,
            status = "completed",
            error = default(string)
        });
        return true;
    }

    private static bool ShouldPromoteToPendingPostDownload(
        DownloadQueueItem item,
        string downloadRoot,
        out long destinationFolderId)
    {
        destinationFolderId = 0;
        if (DownloadStagingFileOwnership.ReadDestinationFolderId(item) is not long destination
            || destination <= 0)
        {
            return false;
        }

        if (!DownloadStagingFileOwnership.HasOwnedStagingAudio(item, downloadRoot))
        {
            return false;
        }

        destinationFolderId = destination;
        var status = item.Status?.Trim().ToLowerInvariant();
        if (status is "completed" or "complete")
        {
            var enrichment = item.EnrichmentStatus?.Trim().ToLowerInvariant();
            var move = item.FinalizationStatus?.Trim().ToLowerInvariant();
            var enrichmentNeedsReset = enrichment is "running" or "blocked";
            var moveNeedsReset = move is "running" or "failed" or "blocked";
            return enrichmentNeedsReset
                   || moveNeedsReset
                   || item.DestinationFolderId != destination;
        }

        if (status is not "running" and not "downloading" and not "failed" and not "retrying")
        {
            return false;
        }

        return DownloadStagingFileOwnership.HasAcquiredAudio(item);
    }

    private bool TryResolveDownloadRoot(out string downloadRoot)
    {
        downloadRoot = string.Empty;
        string configuredPath;
        try
        {
            configuredPath = _settingsService.LoadSettings().DownloadLocation?.Trim() ?? string.Empty;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Post-download recovery skipped because download location could not be loaded.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return false;
        }

        var ioPath = DownloadPathResolver.ResolveIoPath(configuredPath);
        if (string.IsNullOrWhiteSpace(ioPath) || !Directory.Exists(ioPath))
        {
            return false;
        }

        try
        {
            downloadRoot = Path.GetFullPath(ioPath);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private async Task RecoverStalledAcquisitionsAsync(CancellationToken cancellationToken)
    {
        var candidates = await _queueRepository.GetRunningTasksOlderThanAsync(
            DownloadQueueRecoveryPolicy.AcquisitionStageLease,
            cancellationToken);
        foreach (var item in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(item.QueueUuid)
                || !TryReadStalledAcquisition(item, out var provider, out var stage))
            {
                continue;
            }

            if (!await _queueRepository.TryClaimStaleRunningAsync(
                    item.QueueUuid,
                    DownloadQueueRecoveryPolicy.AcquisitionStageLease,
                    cancellationToken))
            {
                continue;
            }

            var engine = NormalizeEngineName(item.Engine);
            var message = DownloadQueueRecoveryPolicy.BuildAcquisitionStallMessage(engine, provider, stage);
            _logger.LogWarning(
                "Recovering stalled acquisition {QueueUuid}: engine={Engine} provider={Provider} stage={Stage} with no audio transfer since {UpdatedAt}",
                item.QueueUuid,
                engine,
                provider,
                stage,
                item.UpdatedAt);
            if (_cancellationRegistry.IsActive(item.QueueUuid) && _cancellationRegistry.MarkTimedOut(item.QueueUuid))
            {
                _cancellationRegistry.Cancel(item.QueueUuid);
                _runtime.ActivityLog.Warn($"Download acquisition stalled (engine={engine}): {item.QueueUuid} {message}");
                _runtime.Listener.Send("updateQueue", new
                {
                    uuid = item.QueueUuid,
                    error = message
                });
                continue;
            }

            await MarkFailedAndRetryAsync(item.QueueUuid, engine, message);
        }
    }

    private static bool TryReadStalledAcquisition(DownloadQueueItem item, out string? provider, out string? stage)
    {
        provider = null;
        stage = null;
        if (string.IsNullOrWhiteSpace(item.PayloadJson))
        {
            return false;
        }

        try
        {
            var snapshot = System.Text.Json.JsonSerializer.Deserialize<AcquisitionSnapshot>(item.PayloadJson);
            if (snapshot is null
                || snapshot.AudioAcquired
                || snapshot.Progress > 0
                || snapshot.Downloaded > 0
                || snapshot.TotalSize > 0
                || !string.IsNullOrWhiteSpace(snapshot.FilePath)
                || string.IsNullOrWhiteSpace(snapshot.AcquisitionStage)
                || !snapshot.AcquisitionStageUpdatedUtc.HasValue)
            {
                return false;
            }

            if (string.Equals(
                    snapshot.AcquisitionStage,
                    Shared.DownloadAcquisitionStages.DownloadingAudio,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (DateTimeOffset.UtcNow - snapshot.AcquisitionStageUpdatedUtc.Value
                <= DownloadQueueRecoveryPolicy.AcquisitionStageLease)
            {
                return false;
            }

            provider = snapshot.AcquisitionProvider;
            stage = snapshot.AcquisitionStage;
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private sealed record AcquisitionSnapshot
    {
        public bool AudioAcquired { get; init; }
        public double Progress { get; init; }
        public int Downloaded { get; init; }
        public double TotalSize { get; init; }
        public string? FilePath { get; init; }
        public string? AcquisitionStage { get; init; }
        public string? AcquisitionProvider { get; init; }
        public DateTimeOffset? AcquisitionStageUpdatedUtc { get; init; }
    }

    private async Task RecoverRunningItemsOlderThanAsync(
        TimeSpan age,
        bool recoverOrphanedOnly,
        CancellationToken cancellationToken)
    {
        var candidates = await _queueRepository.GetRunningTasksOlderThanAsync(age, cancellationToken);
        foreach (var item in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(item.QueueUuid))
            {
                continue;
            }

            var isActive = _cancellationRegistry.IsActive(item.QueueUuid);
            if (recoverOrphanedOnly && isActive)
            {
                continue;
            }

            if (!await _queueRepository.TryClaimStaleRunningAsync(item.QueueUuid, age, cancellationToken))
            {
                continue;
            }

            if (isActive)
            {
                await CancelTimedOutActiveItemAsync(item);
                continue;
            }

            await RecoverOrphanedItemAsync(item, cancellationToken);
        }
    }

    private Task CancelTimedOutActiveItemAsync(DownloadQueueItem item)
    {
        if (!_cancellationRegistry.MarkTimedOut(item.QueueUuid))
        {
            return Task.CompletedTask;
        }

        var engine = NormalizeEngineName(item.Engine);
        var message = DownloadQueueRecoveryPolicy.BuildStallTimeoutMessage(engine);
        _logger.LogWarning(
            "Cancelling stalled active queue item {QueueUuid} for engine {Engine} after no progress updates since {UpdatedAt}",
            item.QueueUuid,
            engine,
            item.UpdatedAt);
        _runtime.ActivityLog.Warn($"Download stalled: {item.QueueUuid} engine={engine} progress={item.Progress ?? 0:0.#}");
        _cancellationRegistry.Cancel(item.QueueUuid);
        _runtime.Listener.Send("updateQueue", new
        {
            uuid = item.QueueUuid,
            error = message
        });
        return Task.CompletedTask;
    }

    private async Task RecoverOrphanedItemAsync(DownloadQueueItem item, CancellationToken cancellationToken)
    {
        if (TryResolveDownloadRoot(out var downloadRoot)
            && await TryPromoteToPendingPostDownloadAsync(item, downloadRoot, cancellationToken))
        {
            return;
        }

        var engine = NormalizeEngineName(item.Engine);
        var recoveryMessage = DownloadQueueRecoveryPolicy.BuildRecoveryFailureMessage(engine);
        await MarkFailedAndRetryAsync(item.QueueUuid, engine, recoveryMessage);
    }

    private async Task MarkFailedAndRetryAsync(string queueUuid, string engine, string message)
    {
        await _queueRepository.UpdateStatusAsync(
            queueUuid,
            FailedStatus,
            message,
            cancellationToken: CancellationToken.None);
        _runtime.Listener.Send("updateQueue", new
        {
            uuid = queueUuid,
            status = FailedStatus,
            error = message
        });
        _runtime.ActivityLog.Error($"Queue recovery failed (engine={engine}): {queueUuid} {message}");
        await _runtime.RetryScheduler.ScheduleRetryAsync(queueUuid, engine, message, CancellationToken.None);
    }

    private static string NormalizeEngineName(string? engine)
    {
        if (string.IsNullOrWhiteSpace(engine))
        {
            return "deezer";
        }

        var normalized = engine.Trim().ToLowerInvariant();
        return normalized == "deezspotag" ? "deezer" : normalized;
    }
}
