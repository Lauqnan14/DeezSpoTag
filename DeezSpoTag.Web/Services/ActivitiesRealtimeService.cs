using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace DeezSpoTag.Web.Services;

public sealed class ActivitiesRealtimeService
{
    private readonly IHubContext<ActivitiesHub> _hubContext;
    private readonly ILogger<ActivitiesRealtimeService> _logger;

    public ActivitiesRealtimeService(
        IHubContext<ActivitiesHub> hubContext,
        ILogger<ActivitiesRealtimeService> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public void PublishAutoTagRunChanged(AutoTagRunSummary summary)
    {
        if (string.IsNullOrWhiteSpace(summary.Id))
        {
            return;
        }

        _ = PublishAsync("autotagRunChanged", new
        {
            runId = summary.Id,
            date = AutoTagService.GetRunDateToken(summary.StartedAt),
            status = summary.Status,
            startedAt = summary.StartedAt,
            finishedAt = summary.FinishedAt,
            progress = summary.Progress
        });
    }

    public void PublishWatchlistHistoryChanged(WatchlistHistoryDto entry)
    {
        if (entry.Id <= 0)
        {
            return;
        }

        _ = PublishAsync("watchlistHistoryChanged", new
        {
            entry
        });
    }

    private async Task PublishAsync(string eventName, object payload)
    {
        try
        {
            await _hubContext.Clients.All.SendAsync(eventName, payload);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to broadcast activities event {EventName}.", eventName);
        }
    }
}
