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
        var state = new ProgressReporterState();

        return async (progress, speedMbps) =>
        {
            if (!state.TryBuildSnapshot(progress, speedMbps, DateTimeOffset.UtcNow, out var snapshot))
            {
                return;
            }

            try
            {
                payload.Progress = snapshot.Progress;
                payload.SegmentTotal = snapshot.SegmentTotal;
                payload.SegmentProgress = snapshot.SegmentCompleted;
                var json = JsonSerializer.Serialize(payload);
                await queueRepository.UpdateProgressAsync(queueUuid, snapshot.Progress, cancellationToken);
                await queueRepository.UpdatePayloadAsync(queueUuid, json, cancellationToken);
                listener.Send("updateQueue", new
                {
                    uuid = queueUuid,
                    progress = snapshot.Progress,
                    segmentProgress = snapshot.SegmentCompleted,
                    segmentTotal = snapshot.SegmentTotal
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

    private readonly record struct ProgressSnapshot(
        double Progress,
        int SegmentTotal,
        int SegmentCompleted);

    private sealed class ProgressReporterState
    {
        private const double MinProgressDelta = 0.25d;
        private readonly object _gate = new();
        private double _lastProgress = -1d;
        private DateTimeOffset _lastUpdate = DateTimeOffset.UtcNow;
        private int _lastSegmentTotal;
        private int _lastSegmentCompleted;

        public bool TryBuildSnapshot(double progress, double speedMbps, DateTimeOffset now, out ProgressSnapshot snapshot)
        {
            snapshot = default;
            var normalized = Math.Clamp(progress, 0, 100);
            var (segmentTotal, segmentCompleted) = ParseSegmentProgress(speedMbps);

            lock (_gate)
            {
                normalized = Math.Max(normalized, _lastProgress < 0 ? 0 : _lastProgress);
                var shouldEmitSnapshot = (now - _lastUpdate).TotalSeconds >= 1 && normalized > _lastProgress;
                if (!ShouldSendProgressUpdate(normalized, shouldEmitSnapshot))
                {
                    return false;
                }

                _lastProgress = normalized;
                _lastUpdate = now;
                if (segmentTotal > 0)
                {
                    _lastSegmentTotal = segmentTotal;
                    _lastSegmentCompleted = Math.Clamp(segmentCompleted, 0, segmentTotal);
                }

                snapshot = new ProgressSnapshot(normalized, _lastSegmentTotal, _lastSegmentCompleted);
                return true;
            }
        }

        private bool ShouldSendProgressUpdate(double normalized, bool shouldEmitSnapshot)
        {
            return _lastProgress < 0
                || normalized >= 100
                || normalized - _lastProgress >= MinProgressDelta
                || shouldEmitSnapshot;
        }

        private static (int SegmentTotal, int SegmentCompleted) ParseSegmentProgress(double speedMbps)
        {
            if (speedMbps < 100000)
            {
                return (0, 0);
            }

            var segmentTotal = (int)Math.Floor(speedMbps / 100000d);
            var segmentCompleted = (int)Math.Round(speedMbps - (segmentTotal * 100000d));
            return (segmentTotal, segmentCompleted);
        }
    }
}
