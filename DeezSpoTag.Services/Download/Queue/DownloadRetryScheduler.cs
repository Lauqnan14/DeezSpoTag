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
    private sealed record PendingRetryRequest(string QueueUuid, string Engine, int Attempt, string Reason, DateTimeOffset RequestedAtUtc);

    private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingRetryRequest> _pendingRetries = new(StringComparer.Ordinal);
    private readonly DownloadQueueRepository _queueRepository;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly IActivityLogWriter _activityLog;
    private readonly IDeezSpoTagListener _listener;
    private readonly ILogger<DownloadRetryScheduler> _logger;
    private readonly DownloadCancellationRegistry _cancellationRegistry;
    private readonly Action? _onRetryQueued;

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
        _logger = logger;
        _cancellationRegistry = cancellationRegistry;
        _onRetryQueued = onRetryQueued;
    }

    public bool HasPendingRetries => !_pendingRetries.IsEmpty;

    public void ScheduleRetry(string queueUuid, string engine, string reason)
    {
        if (string.IsNullOrWhiteSpace(queueUuid))
        {
            return;
        }

        if (!TryCreateRetrySchedule(queueUuid, engine, reason, out var pending))
        {
            return;
        }

        _pendingRetries[queueUuid] = pending;
        _onRetryQueued?.Invoke();
    }

    public async Task<bool> RunRetrySweepAsync(CancellationToken cancellationToken = default)
    {
        if (_pendingRetries.IsEmpty)
        {
            return false;
        }

        var requeuedAny = false;
        var retryRequests = _pendingRetries.Values
            .OrderBy(request => request.RequestedAtUtc)
            .ThenBy(request => request.QueueUuid, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var request in retryRequests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_pendingRetries.ContainsKey(request.QueueUuid))
            {
                continue;
            }

            if (WasRetryUserCanceled(request.QueueUuid))
            {
                _pendingRetries.TryRemove(request.QueueUuid, out _);
                _attempts.TryRemove(request.QueueUuid, out _);
                continue;
            }

            var item = await _queueRepository.GetByUuidAsync(request.QueueUuid, cancellationToken);
            if (item == null)
            {
                _pendingRetries.TryRemove(request.QueueUuid, out _);
                _attempts.TryRemove(request.QueueUuid, out _);
                continue;
            }

            if (!IsRetryableStatus(item.Status))
            {
                if (ShouldClearRetryState(item.Status))
                {
                    _attempts.TryRemove(request.QueueUuid, out _);
                }

                _pendingRetries.TryRemove(request.QueueUuid, out _);
                continue;
            }

            var requeued = await _queueRepository.RequeueAsync(
                request.QueueUuid,
                QueueRequeueOrigin.AutoRetry,
                cancellationToken: cancellationToken);
            _pendingRetries.TryRemove(request.QueueUuid, out _);
            if (!requeued)
            {
                continue;
            }

            _activityLog.Info($"Auto-retry queued (engine={request.Engine} attempt={request.Attempt}): {request.QueueUuid}");
            NotifyRetryQueued(request.QueueUuid);
            requeuedAny = true;
        }

        return requeuedAny;
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
        => (status ?? string.Empty) is "failed";

    private static bool ShouldClearRetryState(string? status)
    {
        var normalized = (status ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "completed" or "complete" or "canceled" or "cancelled";
    }

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
        _pendingRetries.TryRemove(queueUuid, out _);
    }

    private bool TryCreateRetrySchedule(string queueUuid, string engine, string reason, out PendingRetryRequest pending)
    {
        var settings = _settingsService.LoadSettings();
        var attempt = _attempts.AddOrUpdate(queueUuid, 1, (_, current) => current + 1);
        var maxRetries = Math.Clamp(settings.MaxRetries, 0, 1);
        if (maxRetries <= 0 || attempt > maxRetries)
        {
            _activityLog.Warn($"Auto-retry stopped (engine={engine} attempt={attempt} maxAutoSweepRetries={maxRetries}): {queueUuid} {reason}");
            _attempts.TryRemove(queueUuid, out _);
            pending = default!;
            return false;
        }

        pending = new PendingRetryRequest(
            queueUuid,
            string.IsNullOrWhiteSpace(engine) ? "unknown" : engine,
            attempt,
            reason,
            DateTimeOffset.UtcNow);
        _activityLog.Warn($"Auto-retry scheduled for queue-drain sweep (engine={engine} attempt={attempt}): {queueUuid} {reason}");
        return true;
    }

}
