using DeezSpoTag.Services.Download.Queue;

namespace DeezSpoTag.Services.Download.Shared;

public static class DownloadAcquisitionStageWriter
{
    public static async Task RecordAsync(
        DownloadQueueRepository repository,
        string queueUuid,
        EngineQueueItemBase payload,
        string stage,
        string provider,
        CancellationToken cancellationToken)
    {
        payload.AcquisitionStage = stage;
        payload.AcquisitionProvider = provider;
        payload.AcquisitionStageUpdatedUtc = DateTimeOffset.UtcNow;
        payload.AcquisitionFailureReason = string.Empty;
        await QueueHelperUtils.UpdatePayloadAsync(repository, queueUuid, payload, cancellationToken: cancellationToken);
    }

    public static void RecordFailure(EngineQueueItemBase payload, string reason)
    {
        payload.AcquisitionFailureReason = reason;
        payload.AcquisitionStageUpdatedUtc = DateTimeOffset.UtcNow;
    }

    public static bool IsStalledAcquisition(EngineQueueItemBase payload, TimeSpan lease)
    {
        if (payload.AudioAcquired
            || payload.Progress > 0
            || payload.Downloaded > 0
            || payload.TotalSize > 0
            || !string.IsNullOrWhiteSpace(payload.FilePath))
        {
            return false;
        }

        var updated = payload.AcquisitionStageUpdatedUtc;
        return updated.HasValue && DateTimeOffset.UtcNow - updated.Value > lease;
    }
}
