using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeezSpoTag.Services.Download.Queue;

public sealed class DownloadRetryScheduler
{
    private readonly DownloadQueueRepository _queueRepository;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly IActivityLogWriter _activityLog;
    private readonly IDeezSpoTagListener _listener;
    private readonly DownloadCancellationRegistry _cancellationRegistry;
    private readonly Action? _onRetryQueued;
    private volatile bool _lastKnownPending;

    public DownloadRetryScheduler(
        DownloadQueueRepository queueRepository,
        DeezSpoTagSettingsService settingsService,
        IActivityLogWriter activityLog,
        IDeezSpoTagListener listener,
        ILogger<DownloadRetryScheduler> logger,
        DownloadCancellationRegistry cancellationRegistry,
        Action? onRetryQueued = null)
    {
        _queueRepository = queueRepository;
        _settingsService = settingsService;
        _activityLog = activityLog;
        _listener = listener;
        _cancellationRegistry = cancellationRegistry;
        _onRetryQueued = onRetryQueued;
        _ = logger;
    }

    public bool HasPendingRetries => _lastKnownPending;

    public async Task<bool> HasPendingRetriesAsync(CancellationToken cancellationToken = default)
    {
        _lastKnownPending = await _queueRepository.HasScheduledRetriesAsync(cancellationToken);
        return _lastKnownPending;
    }

    public async Task<bool> ScheduleRetryAsync(
        string queueUuid,
        string engine,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(queueUuid)
            || _cancellationRegistry.WasUserCanceled(queueUuid))
        {
            return false;
        }

        var maxRetries = Math.Max(0, _settingsService.LoadSettings().MaxRetries);
        var scheduled = await _queueRepository.ScheduleRetryAsync(
            queueUuid,
            string.IsNullOrWhiteSpace(engine) ? "unknown" : engine,
            reason ?? string.Empty,
            maxRetries,
            cancellationToken);
        _lastKnownPending = scheduled || await _queueRepository.HasScheduledRetriesAsync(cancellationToken);
        if (scheduled)
        {
            _activityLog.Warn($"Durable retry scheduled (engine={engine}): {queueUuid} {reason}");
            _onRetryQueued?.Invoke();
        }
        else
        {
            _activityLog.Warn($"Retry attempts exhausted (engine={engine} maxRetries={maxRetries}): {queueUuid} {reason}");
        }

        return scheduled;
    }

    public async Task<bool> RunRetrySweepAsync(CancellationToken cancellationToken = default)
    {
        var due = await _queueRepository.GetDueRetryQueueUuidsAsync(cancellationToken);
        var requeuedAny = false;
        var newestFirst = string.Equals(
            _settingsService.LoadSettings().QueueOrder,
            "recent",
            StringComparison.OrdinalIgnoreCase);
        foreach (var queueUuid in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_cancellationRegistry.WasUserCanceled(queueUuid))
            {
                await _queueRepository.ClearRetryScheduleAsync(queueUuid, resetAttempts: true, cancellationToken);
                continue;
            }

            await NormalizePersistedPlanAsync(queueUuid, cancellationToken);
            var requeued = await _queueRepository.RequeueAsync(
                queueUuid,
                QueueRequeueOrigin.AutoRetry,
                requeueToFront: false,
                newestFirst,
                cancellationToken);
            if (!requeued)
            {
                continue;
            }

            _activityLog.Info($"Durable retry queued after queue-drain gate: {queueUuid}");
            _listener.Send("updateQueue", new
            {
                uuid = queueUuid,
                status = "inQueue",
                progress = 0,
                downloaded = 0,
                failed = 0,
                error = default(string)
            });
            requeuedAny = true;
        }

        _lastKnownPending = await _queueRepository.HasScheduledRetriesAsync(cancellationToken);
        return requeuedAny;
    }

    private async Task NormalizePersistedPlanAsync(
        string queueUuid,
        CancellationToken cancellationToken)
    {
        var item = await _queueRepository.GetByUuidAsync(queueUuid, cancellationToken);
        if (string.IsNullOrWhiteSpace(item?.PayloadJson))
        {
            return;
        }

        JsonObject? payload;
        try
        {
            payload = JsonNode.Parse(item.PayloadJson) as JsonObject;
        }
        catch (JsonException)
        {
            return;
        }

        if (payload == null
            || !DownloadExecutionPlan.NormalizePersistedRetryPlan(payload, out var plan)
            || plan.Count == 0)
        {
            return;
        }

        await _queueRepository.UpdatePayloadAndEngineAsync(
            queueUuid,
            plan[0].Engine,
            payload.ToJsonString(),
            cancellationToken);
        _activityLog.Info($"Normalized persisted fallback plan before retry: {queueUuid}");
    }

    public async Task ClearAsync(
        string queueUuid,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(queueUuid))
        {
            return;
        }

        await _queueRepository.ClearRetryScheduleAsync(queueUuid, resetAttempts: true, cancellationToken);
        _lastKnownPending = await _queueRepository.HasScheduledRetriesAsync(cancellationToken);
    }
}
