using System.Text.Json;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Utils;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Queue;

internal static class QueueHelperUtils
{
    public sealed record PayloadUpdateMutators<TQueueItem>(
        Action<TQueueItem, string> SetFilePath,
        Action<TQueueItem, double> SetTotalSize,
        Action<TQueueItem, double> SetProgress,
        Action<TQueueItem, int> SetDownloaded);

    public sealed record UpdatePayloadRequest<TQueueItem>(
        DownloadQueueRepository QueueRepository,
        string QueueUuid,
        TQueueItem Payload,
        string FilePath,
        double FileSizeMb,
        int ItemSize,
        PayloadUpdateMutators<TQueueItem> Mutators);

    public sealed record FinalDestinationMutators<TQueueItem>(
        Func<TQueueItem, Dictionary<string, string>> GetFinalDestinations,
        Action<TQueueItem, Dictionary<string, string>> SetFinalDestinations,
        PayloadUpdateMutators<TQueueItem> PayloadMutators);

    public sealed record UpdateFinalDestinationPayloadRequest<TQueueItem>(
        DownloadQueueRepository QueueRepository,
        string QueueUuid,
        TQueueItem Payload,
        string FilePath,
        double FileSizeMb,
        int ItemSize,
        List<Dictionary<string, object>> Files,
        FinalDestinationMutators<TQueueItem> Mutators);

    public static TQueueItem? DeserializeQueueItem<TQueueItem>(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<TQueueItem>(payloadJson);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return default;
        }
    }

    public static async Task UpdateFinalDestinationPayloadAsync<TQueueItem>(
        UpdateFinalDestinationPayloadRequest<TQueueItem> request,
        CancellationToken cancellationToken)
    {
        var normalizedPath = DownloadPathResolver.NormalizeDisplayPath(request.FilePath);
        request.Mutators.PayloadMutators.SetFilePath(request.Payload, normalizedPath);
        var finalDestinations = FinalDestinationTracker.EnsureMap(request.Mutators.GetFinalDestinations(request.Payload));
        request.Mutators.SetFinalDestinations(request.Payload, finalDestinations);
        FinalDestinationTracker.SeedIdentityEntries(finalDestinations, normalizedPath, request.Files);
        request.Mutators.PayloadMutators.SetTotalSize(request.Payload, request.FileSizeMb);
        request.Mutators.PayloadMutators.SetProgress(request.Payload, 100);
        request.Mutators.PayloadMutators.SetDownloaded(request.Payload, Math.Max(request.ItemSize, 1));
        var json = JsonSerializer.Serialize(request.Payload);
        var finalDestinationsJson = FinalDestinationTracker.Serialize(finalDestinations);
        await request.QueueRepository.UpdateFinalDestinationsAsync(request.QueueUuid, finalDestinationsJson, json, cancellationToken);
    }

    public static async Task UpdatePayloadAsync<TQueueItem>(
        UpdatePayloadRequest<TQueueItem> request,
        CancellationToken cancellationToken)
    {
        request.Mutators.SetFilePath(request.Payload, DownloadPathResolver.NormalizeDisplayPath(request.FilePath));
        request.Mutators.SetTotalSize(request.Payload, request.FileSizeMb);
        request.Mutators.SetProgress(request.Payload, 100);
        request.Mutators.SetDownloaded(request.Payload, Math.Max(request.ItemSize, 1));
        var json = JsonSerializer.Serialize(request.Payload);
        await request.QueueRepository.UpdatePayloadAsync(request.QueueUuid, json, cancellationToken);
    }

    public static async Task UpdatePayloadAsync<TQueueItem>(
        DownloadQueueRepository queueRepository,
        string queueUuid,
        TQueueItem payload,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload);
        await queueRepository.UpdatePayloadAsync(queueUuid, json, cancellationToken);
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
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
        ILogger logger,
        string logMessage,
        CancellationToken cancellationToken)
    {
        var lastProgress = -1d;
        var lastUpdate = DateTimeOffset.UtcNow;
        var gate = new object();

        return async (progress, speedMbps) =>
        {
            var normalized = Math.Clamp(progress, 0, 100);
            var now = DateTimeOffset.UtcNow;
            var shouldSend = false;

            lock (gate)
            {
                if (normalized >= 100 || normalized - lastProgress >= 1 || (now - lastUpdate).TotalSeconds >= 1)
                {
                    lastProgress = normalized;
                    lastUpdate = now;
                    shouldSend = true;
                }
            }

            if (!shouldSend)
            {
                return;
            }

            try
            {
                await queueRepository.UpdateProgressAsync(queueUuid, normalized, cancellationToken);
                listener.Send("updateQueue", new
                {
                    uuid = queueUuid,
                    progress = normalized
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug(ex, "{Context} (queue {QueueUuid})", logMessage, queueUuid);                }
            }
        };
    }
}
