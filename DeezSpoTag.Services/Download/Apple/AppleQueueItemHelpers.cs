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
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AppleQueueItem>(payloadJson);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            return null;
        }
    }

    public static async Task UpdateQueuePayloadAsync(
        DownloadQueueRepository queueRepository,
        string queueUuid,
        AppleQueueItem payload,
        string filePath,
        double fileSizeMb,
        CancellationToken cancellationToken)
    {
        payload.FilePath = DownloadPathResolver.NormalizeDisplayPath(filePath);
        payload.FinalDestinations = FinalDestinationTracker.EnsureMap(payload.FinalDestinations);
        FinalDestinationTracker.SeedIdentityEntries(payload.FinalDestinations, payload.FilePath, payload.Files);
        payload.TotalSize = fileSizeMb;
        payload.Progress = 100;
        payload.Downloaded = Math.Max(payload.Size, 1);
        var json = JsonSerializer.Serialize(payload);
        var finalDestinationsJson = FinalDestinationTracker.Serialize(payload.FinalDestinations);
        await queueRepository.UpdateFinalDestinationsAsync(queueUuid, finalDestinationsJson, json, cancellationToken);
    }

    public static double TryGetFileSizeMb(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return 0;
        }

        var ioPath = DownloadPathResolver.ResolveIoPath(filePath);
        if (!File.Exists(ioPath))
        {
            return 0;
        }

        try
        {
            var info = new FileInfo(ioPath);
            return info.Length / 1024d / 1024d;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            return 0;
        }
    }

    public static bool OutputExists(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        var ioPath = DownloadPathResolver.ResolveIoPath(filePath);
        return File.Exists(ioPath);
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
                logger.LogDebug(ex, "Failed to report progress for {QueueUuid}", queueUuid);
            }
        };
    }
}
