using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Queue;

public sealed class DownloadQueueRecoveryService
{
    private const string FailedStatus = "failed";
    private readonly DownloadQueueRepository _queueRepository;
    private readonly DownloadCancellationRegistry _cancellationRegistry;
    private readonly DownloadQueueRecoveryRuntime _runtime;
    private readonly ILogger<DownloadQueueRecoveryService> _logger;

    public DownloadQueueRecoveryService(
        DownloadQueueRepository queueRepository,
        DownloadCancellationRegistry cancellationRegistry,
        DownloadQueueRecoveryRuntime runtime,
        ILogger<DownloadQueueRecoveryService> logger)
    {
        _queueRepository = queueRepository;
        _cancellationRegistry = cancellationRegistry;
        _runtime = runtime;
        _logger = logger;
    }

    public async Task RecoverStaleRunningTasksAsync(CancellationToken cancellationToken)
    {
        await RecoverRunningItemsOlderThanAsync(
            DownloadQueueRecoveryPolicy.RunningStallThreshold,
            recoverOrphanedOnly: false,
            cancellationToken);

        await RecoverRunningItemsOlderThanAsync(
            DownloadQueueRecoveryPolicy.OrphanedRunningThreshold,
            recoverOrphanedOnly: true,
            cancellationToken);
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
