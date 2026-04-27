using DeezSpoTag.Web.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace DeezSpoTag.Web.Services;

public sealed class CrossDeviceSyncService
{
    private readonly IHubContext<CrossDeviceSyncHub> _hubContext;
    private readonly ILogger<CrossDeviceSyncService> _logger;

    public CrossDeviceSyncService(
        IHubContext<CrossDeviceSyncHub> hubContext,
        ILogger<CrossDeviceSyncService> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task PublishTracklistUpdatedAsync(
        string tracklistType,
        string tracklistId,
        int trackCount,
        string payloadHash,
        string? sourceClientId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tracklistType) || string.IsNullOrWhiteSpace(tracklistId))
        {
            return;
        }

        var payload = new TracklistUpdatedEvent(
            TracklistType: tracklistType,
            TracklistId: tracklistId,
            TrackCount: trackCount,
            PayloadHash: payloadHash,
            UpdatedUtc: DateTimeOffset.UtcNow,
            SourceClientId: string.IsNullOrWhiteSpace(sourceClientId) ? null : sourceClientId.Trim());

        try
        {
            await _hubContext.Clients.All.SendAsync("tracklistUpdated", payload, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to broadcast cross-device tracklist update.");
        }
    }
}

public sealed record TracklistUpdatedEvent(
    string TracklistType,
    string TracklistId,
    int TrackCount,
    string PayloadHash,
    DateTimeOffset UpdatedUtc,
    string? SourceClientId);
