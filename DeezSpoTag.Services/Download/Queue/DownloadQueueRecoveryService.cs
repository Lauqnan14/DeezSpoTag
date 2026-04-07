using System.Text.Json;
using System.Text.Json.Nodes;
using DeezSpoTag.Services.Download.Amazon;
using DeezSpoTag.Services.Download.Apple;
using DeezSpoTag.Services.Download.Deezer;
using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Download.Qobuz;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Tidal;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Queue;

public sealed class DownloadQueueRecoveryService
{
    private const string FailedStatus = "failed";
    private const string InQueueStatus = "inQueue";
    private readonly DownloadQueueRepository _queueRepository;
    private readonly DownloadCancellationRegistry _cancellationRegistry;
    private readonly DownloadRetryScheduler _retryScheduler;
    private readonly EngineFallbackCoordinator _fallbackCoordinator;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly IActivityLogWriter _activityLog;
    private readonly IDeezSpoTagListener _listener;
    private readonly ILogger<DownloadQueueRecoveryService> _logger;

    public DownloadQueueRecoveryService(
        DownloadQueueRepository queueRepository,
        DownloadCancellationRegistry cancellationRegistry,
        DownloadRetryScheduler retryScheduler,
        EngineFallbackCoordinator fallbackCoordinator,
        DeezSpoTagSettingsService settingsService,
        IActivityLogWriter activityLog,
        IDeezSpoTagListener listener,
        ILogger<DownloadQueueRecoveryService> logger)
    {
        _queueRepository = queueRepository;
        _cancellationRegistry = cancellationRegistry;
        _retryScheduler = retryScheduler;
        _fallbackCoordinator = fallbackCoordinator;
        _settingsService = settingsService;
        _activityLog = activityLog;
        _listener = listener;
        _logger = logger;
    }

    public async Task RecoverStaleRunningTasksAsync(CancellationToken cancellationToken)
    {
        var staleItems = await _queueRepository.GetRunningTasksOlderThanAsync(
            DownloadQueueRecoveryPolicy.RunningStallThreshold,
            cancellationToken);

        foreach (var item in staleItems)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(item.QueueUuid)
                || !await _queueRepository.TryClaimStaleRunningAsync(
                    item.QueueUuid,
                    DownloadQueueRecoveryPolicy.RunningStallThreshold,
                    cancellationToken))
            {
                continue;
            }

            if (_cancellationRegistry.IsActive(item.QueueUuid))
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
        _activityLog.Warn($"Download stalled: {item.QueueUuid} engine={engine} progress={item.Progress ?? 0:0.#}");
        _cancellationRegistry.Cancel(item.QueueUuid);
        _listener.Send("updateQueue", new
        {
            uuid = item.QueueUuid,
            error = message
        });
        return Task.CompletedTask;
    }

    private async Task RecoverOrphanedItemAsync(DownloadQueueItem item, CancellationToken cancellationToken)
    {
        var normalizedItem = await NormalizeFallbackPayloadAsync(item, cancellationToken);
        var engine = NormalizeEngineName(normalizedItem.Engine);

        switch (engine)
        {
            case "qobuz":
                await RecoverOrphanedItemAsync(
                    normalizedItem,
                    payloadJson => QueueHelperUtils.DeserializeQueueItem<QobuzQueueItem>(payloadJson),
                    cancellationToken);
                break;
            case "apple":
                await RecoverOrphanedItemAsync(
                    normalizedItem,
                    payloadJson => QueueHelperUtils.DeserializeQueueItem<AppleQueueItem>(payloadJson),
                    cancellationToken);
                break;
            case "tidal":
                await RecoverOrphanedItemAsync(
                    normalizedItem,
                    payloadJson => QueueHelperUtils.DeserializeQueueItem<TidalQueueItem>(payloadJson),
                    cancellationToken);
                break;
            case "amazon":
                await RecoverOrphanedItemAsync(
                    normalizedItem,
                    payloadJson => QueueHelperUtils.DeserializeQueueItem<AmazonQueueItem>(payloadJson),
                    cancellationToken);
                break;
            default:
                await RecoverOrphanedItemAsync(
                    normalizedItem with { Engine = "deezer" },
                    payloadJson => QueueHelperUtils.DeserializeQueueItem<DeezerQueueItem>(payloadJson),
                    cancellationToken);
                break;
        }
    }

    private async Task RecoverOrphanedItemAsync<TPayload>(
        DownloadQueueItem item,
        Func<string?, TPayload?> deserialize,
        CancellationToken cancellationToken)
        where TPayload : EngineQueueItemBase
    {
        var payload = deserialize(item.PayloadJson);
        var engine = NormalizeEngineName(item.Engine);
        var recoveryMessage = DownloadQueueRecoveryPolicy.BuildRecoveryFailureMessage(engine);
        if (payload == null)
        {
            await MarkFailedAndRetryAsync(item.QueueUuid, engine, "Invalid payload during queue recovery.");
            return;
        }

        if (string.IsNullOrWhiteSpace(payload.Engine))
        {
            payload.Engine = engine;
        }

        var currentEngine = NormalizeEngineName(payload.Engine);
        var advanced = await _fallbackCoordinator.TryAdvanceAsync(
            item.QueueUuid,
            currentEngine,
            payload,
            cancellationToken);
        if (advanced)
        {
            _logger.LogWarning(
                "Recovered orphaned running queue item {QueueUuid}: {OldEngine} -> {NewEngine}",
                item.QueueUuid,
                currentEngine,
                payload.Engine);
            _activityLog.Warn($"Recovered stale running item: {item.QueueUuid} {currentEngine} -> {payload.Engine}");
            _listener.Send("updateQueue", new
            {
                uuid = item.QueueUuid,
                status = InQueueStatus,
                progress = 0,
                downloaded = 0,
                failed = 0,
                error = default(string),
                engine = payload.Engine
            });
            return;
        }

        await MarkFailedAndRetryAsync(item.QueueUuid, currentEngine, recoveryMessage);
    }

    private async Task MarkFailedAndRetryAsync(string queueUuid, string engine, string message)
    {
        await _queueRepository.UpdateStatusAsync(
            queueUuid,
            FailedStatus,
            message,
            cancellationToken: CancellationToken.None);
        _listener.Send("updateQueue", new
        {
            uuid = queueUuid,
            status = FailedStatus,
            error = message
        });
        _activityLog.Error($"Queue recovery failed (engine={engine}): {queueUuid} {message}");
        _retryScheduler.ScheduleRetry(queueUuid, engine, message);
    }

    private async Task<DownloadQueueItem> NormalizeFallbackPayloadAsync(
        DownloadQueueItem item,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.PayloadJson))
        {
            return item;
        }

        JsonObject? payloadObj;
        try
        {
            payloadObj = JsonNode.Parse(item.PayloadJson) as JsonObject;
        }
        catch (JsonException)
        {
            return item;
        }

        if (payloadObj == null)
        {
            return item;
        }

        var settings = _settingsService.LoadSettings();
        var state = FallbackPayloadNormalizer.ResolveCanonicalState(item, settings, payloadObj);
        var changed = FallbackPayloadNormalizer.ApplyCanonicalState(payloadObj, state, resetIndexAndHistory: false);
        if (!changed)
        {
            return item;
        }

        var normalizedPayload = payloadObj.ToJsonString();
        await _queueRepository.UpdatePayloadAsync(item.QueueUuid, normalizedPayload, cancellationToken);
        return item with { PayloadJson = normalizedPayload };
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
