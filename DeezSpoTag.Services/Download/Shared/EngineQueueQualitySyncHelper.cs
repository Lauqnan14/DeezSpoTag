using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared.Models;

namespace DeezSpoTag.Services.Download.Shared;

internal static class EngineQueueQualitySyncHelper
{
    public static async Task<bool> SyncQualityAsync<TQueueItem>(
        DownloadQueueRepository queueRepository,
        IDeezSpoTagListener listener,
        string queueUuid,
        TQueueItem payload,
        string? effectiveQuality,
        CancellationToken cancellationToken)
        where TQueueItem : EngineQueueItemBase
    {
        if (string.IsNullOrWhiteSpace(effectiveQuality))
        {
            return false;
        }

        if (string.Equals(payload.Quality, effectiveQuality, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        payload.Quality = effectiveQuality;
        await QueueHelperUtils.UpdatePayloadAsync(queueRepository, queueUuid, payload, cancellationToken);
        listener.Send("updateQueue", new
        {
            uuid = queueUuid,
            quality = payload.Quality,
            engine = payload.Engine
        });
        return true;
    }
}
