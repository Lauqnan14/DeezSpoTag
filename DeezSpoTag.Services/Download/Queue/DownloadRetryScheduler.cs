using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Soulseek;
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
        CancellationToken cancellationToken = default,
        TimeSpan? minimumDelay = null,
        bool continueProviderRecovery = false)
    {
        if (string.IsNullOrWhiteSpace(queueUuid)
            || _cancellationRegistry.WasUserCanceled(queueUuid))
        {
            return false;
        }

        if (await IsManualSoulseekSelectionAsync(queueUuid, engine, cancellationToken))
        {
            await _queueRepository.ClearRetryScheduleAsync(queueUuid, resetAttempts: false, cancellationToken);
            _lastKnownPending = await _queueRepository.HasScheduledRetriesAsync(cancellationToken);
            return false;
        }

        var maxRetries = Math.Max(0, _settingsService.LoadSettings().MaxRetries);
        var scheduled = await _queueRepository.ScheduleRetryAsync(
            queueUuid,
            string.IsNullOrWhiteSpace(engine) ? "unknown" : engine,
            reason ?? string.Empty,
            maxRetries,
            cancellationToken, minimumDelay, continueProviderRecovery);
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
            if (await IsManualSoulseekSelectionAsync(queueUuid, "soulseek", cancellationToken))
            {
                var item = await _queueRepository.GetByUuidAsync(queueUuid, cancellationToken);
                await _queueRepository.ClearRetryScheduleAsync(queueUuid, resetAttempts: false, cancellationToken);
                await _queueRepository.UpdateStatusAsync(
                    queueUuid,
                    "failed",
                    item?.Error ?? "The selected Soulseek file failed.",
                    cancellationToken: cancellationToken);
                continue;
            }
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

    private async Task<bool> IsManualSoulseekSelectionAsync(
        string queueUuid,
        string engine,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(engine, SoulseekQueueItem.EngineId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var item = await _queueRepository.GetByUuidAsync(queueUuid, cancellationToken);
        return item is not null
            && SoulseekPinnedCandidatePolicy.IsManualSelection(
                QueuePreResolutionPayload.ParseOrEmpty(item.PayloadJson));
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

        // Finalization retries must keep the engine and quality of the acquired asset.
        if (payload == null || DeezSpoTagApp.HasRecoverableAcquiredAudio(payload))
        {
            return;
        }

        DownloadExecutionPlan.NormalizePersistedRetryPlan(payload, out var plan);
        if (plan.Count == 0) return;

        // A temporary provider failure must not pin every later retry to the last
        // exhausted step. Restart the saved plan, preserving its enabled engines
        // and quality order rather than rebuilding it from current settings.
        void Set(string canonical, string legacy, JsonNode? value)
        {
            payload[canonical] = value;
            payload.Remove(legacy);
        }
        Set("Engine", "engine", JsonValue.Create(plan[0].Engine));
        Set("SourceService", "sourceService", JsonValue.Create(plan[0].Engine));
        Set("Quality", "quality", JsonValue.Create(plan[0].Quality ?? string.Empty));
        Set("AutoIndex", "autoIndex", JsonValue.Create(0));
        Set("ResolvedAutoIndex", "resolvedAutoIndex", JsonValue.Create(0));
        Set("ResolutionStatus", "resolutionStatus", JsonValue.Create("pending"));
        Set("ResolvedAtUtc", "resolvedAtUtc", null);
        foreach (var field in new[] { "ResolvedEngine", "ResolvedQuality", "ResolvedSourceUrl", "ResolutionError", "RequestedQuality", "DeliveredQuality" })
        {
            Set(field, char.ToLowerInvariant(field[0]) + field[1..], JsonValue.Create(string.Empty));
        }
        Set("FallbackHistory", "fallbackHistory", new JsonArray());

        await _queueRepository.UpdatePayloadAndEngineAsync(
            queueUuid,
            plan[0].Engine,
            payload.ToJsonString(),
            cancellationToken);
        _activityLog.Info($"Restarted saved fallback plan before retry: {queueUuid}");
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
