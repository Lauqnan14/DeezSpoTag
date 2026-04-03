using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Queue;

public sealed class DownloadRetryScheduler
{
    private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.Ordinal);
    private readonly DownloadQueueRepository _queueRepository;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly IActivityLogWriter _activityLog;
    private readonly IDeezSpoTagListener _listener;
    private readonly ILogger<DownloadRetryScheduler> _logger;
    private readonly DownloadCancellationRegistry _cancellationRegistry;
    private readonly Func<bool>? _isAutoTagRunning;

    public DownloadRetryScheduler(
        DownloadQueueRepository queueRepository,
        DeezSpoTagSettingsService settingsService,
        IActivityLogWriter activityLog,
        IDeezSpoTagListener listener,
        ILogger<DownloadRetryScheduler> logger,
        DownloadCancellationRegistry cancellationRegistry,
        Func<bool>? isAutoTagRunning = null)
    {
        _queueRepository = queueRepository;
        _settingsService = settingsService;
        _activityLog = activityLog;
        _listener = listener;
        _logger = logger;
        _cancellationRegistry = cancellationRegistry;
        _isAutoTagRunning = isAutoTagRunning;
    }

    public void ScheduleRetry(string queueUuid, string engine, string reason)
    {
        if (string.IsNullOrWhiteSpace(queueUuid))
        {
            return;
        }

        if (!TryCreateRetrySchedule(queueUuid, engine, reason, out var attempt, out var delaySeconds))
        {
            return;
        }

        _ = Task.Run(() => ExecuteScheduledRetryAsync(queueUuid, engine, attempt, delaySeconds));
    }

    private bool TryCreateRetrySchedule(string queueUuid, string engine, string reason, out int attempt, out int delaySeconds)
    {
        var settings = _settingsService.LoadSettings();
        attempt = _attempts.AddOrUpdate(queueUuid, 1, (_, current) => current + 1);
        var maxRetries = settings.MaxRetries;
        if (maxRetries <= 0 || attempt > maxRetries)
        {
            _activityLog.Warn($"Auto-retry stopped (engine={engine} attempt={attempt} max={maxRetries}): {queueUuid} {reason}");
            _attempts.TryRemove(queueUuid, out _);
            delaySeconds = 0;
            return false;
        }

        delaySeconds = Math.Max(0, settings.RetryDelaySeconds + (settings.RetryDelayIncrease * (attempt - 1)));
        _activityLog.Warn($"Auto-retry scheduled (engine={engine} attempt={attempt} delay={delaySeconds}s): {queueUuid} {reason}");
        return true;
    }

    private async Task ExecuteScheduledRetryAsync(string queueUuid, string engine, int attempt, int delaySeconds)
    {
        try
        {
            if (delaySeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
            }

            await WaitForAutoTagIdleAsync();
            if (WasRetryUserCanceled(queueUuid))
            {
                return;
            }

            var item = await _queueRepository.GetByUuidAsync(queueUuid);
            if (item == null || !IsRetryableStatus(item.Status))
            {
                return;
            }

            if (WasRetryUserCanceled(queueUuid))
            {
                return;
            }

            await ResetFallbackStateAsync(item);
            await _queueRepository.RequeueAsync(queueUuid);
            _activityLog.Info($"Auto-retry queued (engine={engine} attempt={attempt}): {queueUuid}");
            NotifyRetryQueued(queueUuid);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to auto-retry {QueueUuid}", queueUuid);
            _activityLog.Error($"Auto-retry failed (engine={engine}): {queueUuid} {ex.Message}");
        }
    }

    private async Task WaitForAutoTagIdleAsync()
    {
        while (_isAutoTagRunning?.Invoke() == true)
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
    }

    private bool WasRetryUserCanceled(string queueUuid)
    {
        if (!_cancellationRegistry.WasUserCanceled(queueUuid))
        {
            return false;
        }

        _activityLog.Info($"Auto-retry skipped (user canceled): {queueUuid}");
        return true;
    }

    private static bool IsRetryableStatus(string? status)
        => (status ?? string.Empty) is "failed" or "cancelled" or "canceled";

    private void NotifyRetryQueued(string queueUuid)
    {
        _listener.Send("updateQueue", new
        {
            uuid = queueUuid,
            status = "inQueue",
            progress = 0,
            downloaded = 0,
            failed = 0,
            error = default(string)
        });
    }

    public void Clear(string queueUuid)
    {
        if (string.IsNullOrWhiteSpace(queueUuid))
        {
            return;
        }

        _attempts.TryRemove(queueUuid, out _);
    }

    private async Task ResetFallbackStateAsync(DownloadQueueItem item)
    {
        if (string.IsNullOrWhiteSpace(item.PayloadJson))
        {
            return;
        }

        try
        {
            var settings = _settingsService.LoadSettings();
            var payloadNode = JsonNode.Parse(item.PayloadJson);
            if (payloadNode is not JsonObject payloadObj)
            {
                return;
            }

            var state = FallbackPayloadNormalizer.ResolveCanonicalState(item, settings, payloadObj);
            var changed = FallbackPayloadNormalizer.ApplyCanonicalState(payloadObj, state, resetIndexAndHistory: true);
            if (changed)
            {
                await _queueRepository.UpdatePayloadAsync(item.QueueUuid, payloadObj.ToJsonString());
            }

            await _queueRepository.UpdateEngineAsync(item.QueueUuid, state.FirstStep.Source);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to reset fallback state for {QueueUuid}", item.QueueUuid);
        }
    }
}
