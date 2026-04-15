using System.Text.Json;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Utils;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Apple;

internal static class AppleQueueItemHelpers
{
    public static AppleQueueItem? DeserializeQueueItem(string? payloadJson)
    {
        return QueueHelperUtils.DeserializeQueueItem<AppleQueueItem>(payloadJson);
    }

    public static async Task UpdateQueuePayloadAsync(
        DownloadQueueRepository queueRepository,
        string queueUuid,
        AppleQueueItem payload,
        string filePath,
        double fileSizeMb,
        CancellationToken cancellationToken)
    {
        var request = new QueueHelperUtils.UpdateFinalDestinationPayloadRequest<AppleQueueItem>(
            QueueRepository: queueRepository,
            QueueUuid: queueUuid,
            Payload: payload,
            FilePath: filePath,
            FileSizeMb: fileSizeMb,
            ItemSize: payload.Size,
            Files: payload.Files,
            Mutators: new QueueHelperUtils.FinalDestinationMutators<AppleQueueItem>(
                GetFinalDestinations: static item => item.FinalDestinations,
                SetFinalDestinations: static (item, value) => item.FinalDestinations = value,
                PayloadMutators: new QueueHelperUtils.PayloadUpdateMutators<AppleQueueItem>(
                    SetFilePath: static (item, value) => item.FilePath = value,
                    SetTotalSize: static (item, value) => item.TotalSize = value,
                    SetProgress: static (item, value) => item.Progress = value,
                    SetDownloaded: static (item, value) => item.Downloaded = value)));

        await QueueHelperUtils.UpdateFinalDestinationPayloadAsync(request, cancellationToken);
    }

    public static double TryGetFileSizeMb(string filePath)
    {
        return QueueHelperUtils.TryGetFileSizeMb(filePath);
    }

    public static bool OutputExists(string filePath)
    {
        return QueueHelperUtils.OutputExists(filePath);
    }

    public static Func<double, double, Task> CreateProgressReporter(
        DownloadQueueRepository queueRepository,
        IDeezSpoTagListener listener,
        string queueUuid,
        AppleQueueItem payload,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var lastProgress = -1d;
        var lastUpdate = DateTimeOffset.UtcNow;
        var gate = new object();
        var lastSegmentTotal = 0;
        var lastSegmentCompleted = 0;

        return async (progress, speedMbps) =>
        {
            var normalized = Math.Clamp(progress, 0, 100);
            var now = DateTimeOffset.UtcNow;
            var shouldSend = false;
            var segmentTotal = 0;
            var segmentCompleted = 0;

            if (speedMbps >= 100000)
            {
                segmentTotal = (int)Math.Floor(speedMbps / 100000d);
                segmentCompleted = (int)Math.Round(speedMbps - (segmentTotal * 100000d));
            }

            lock (gate)
            {
                if (normalized >= 100 || normalized - lastProgress >= 1 || (now - lastUpdate).TotalSeconds >= 1)
                {
                    lastProgress = normalized;
                    lastUpdate = now;
                    shouldSend = true;
                    if (segmentTotal > 0)
                    {
                        lastSegmentTotal = segmentTotal;
                        lastSegmentCompleted = Math.Clamp(segmentCompleted, 0, segmentTotal);
                    }
                }
            }

            if (!shouldSend)
            {
                return;
            }

            try
            {
                payload.SegmentTotal = lastSegmentTotal;
                payload.SegmentProgress = lastSegmentCompleted;
                var json = JsonSerializer.Serialize(payload);
                await queueRepository.UpdateProgressAsync(queueUuid, normalized, cancellationToken);
                await queueRepository.UpdatePayloadAsync(queueUuid, json, cancellationToken);
                listener.Send("updateQueue", new
                {
                    uuid = queueUuid,
                    progress = normalized,
                    segmentProgress = lastSegmentCompleted,
                    segmentTotal = lastSegmentTotal
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug(ex, "Failed to report progress for {QueueUuid}", queueUuid);                }
            }
        };
    }
}
