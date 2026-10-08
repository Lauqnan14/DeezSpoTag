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
            date = AutoTagService.GetRunDateToken(AutoTagService.GetRunHistoryTimestamp(summary)),
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

    /// <summary>
    /// Publishes one platform-level snapshot progress update for the whole set of playlists on that
    /// platform. This is deliberately a single event per platform per phase rather than one event per
    /// playlist, which is what removes the per-playlist chatter from the UI. Per-playlist detail is
    /// still available through watchlist history and the playlist cards.
    /// </summary>
    public void PublishWatchlistPlatformProgressChanged(IReadOnlyList<WatchlistPlatformProgress> platforms)
    {
        if (platforms is null || platforms.Count == 0)
        {
            return;
        }

        _ = PublishAsync("watchlistPlatformProgressChanged", new
        {
            platforms = platforms.Select(static platform => new
            {
                source = platform.Source,
                label = platform.Label,
                summary = platform.Summary,
                totalPlaylists = platform.TotalPlaylists,
                headsChecked = platform.HeadsChecked,
                unchanged = platform.Unchanged,
                changed = platform.Changed,
                @new = platform.New,
                requiresExpansion = platform.RequiresExpansion,
                failed = platform.Failed,
                completed = platform.Completed,
                incomplete = platform.Incomplete,
                headPhaseMs = platform.HeadPhaseMs,
                totalMs = platform.TotalMs,
                startedUtc = platform.StartedUtc,
                completedUtc = platform.CompletedUtc
            }).ToList()
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
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to broadcast activities event {EventName}.", eventName);
            }
        }
    }
}
