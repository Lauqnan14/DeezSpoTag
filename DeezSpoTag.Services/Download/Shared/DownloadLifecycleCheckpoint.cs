using System.Text.Json;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Utils;

namespace DeezSpoTag.Services.Download.Shared;

internal static class DownloadLifecycleCheckpoint
{
    private const string PendingStage = "pending";
    private const string CompletedStage = "completed";

    public static bool TryResume(EngineQueueItemBase payload, out string audioPath)
    {
        audioPath = string.Empty;
        if (!payload.AudioAcquired || string.IsNullOrWhiteSpace(payload.AcquiredAudioPath))
        {
            return false;
        }

        var ioPath = DownloadPathResolver.ResolveIoPath(payload.AcquiredAudioPath);
        try
        {
            var file = new FileInfo(ioPath);
            if (!file.Exists || file.Length <= 0)
            {
                ClearAcquisition(payload);
                return false;
            }

            var validation = DeliveredAudioQualityGuard.Validate(payload, ioPath);
            if (!validation.Success)
            {
                ClearAcquisition(payload);
                return false;
            }

            audioPath = ioPath;
            payload.AcquiredFileSizeBytes = file.Length;
            payload.AcquiredDeliveredQuality = validation.DeliveredQuality;
            payload.FinalizationStage = PendingStage;
            return true;
        }
        catch (IOException)
        {
            ClearAcquisition(payload);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            ClearAcquisition(payload);
            return false;
        }
    }

    public static bool TryAdoptExistingAudio(EngineQueueItemBase payload)
    {
        if (payload.AudioAcquired || string.IsNullOrWhiteSpace(payload.FilePath))
        {
            return false;
        }

        return TryAdoptExistingAudioAtPath(payload, payload.FilePath);
    }

    public static bool TryAdoptExistingAudioAtPath(EngineQueueItemBase payload, string audioPath)
    {
        if (payload.AudioAcquired || string.IsNullOrWhiteSpace(audioPath))
        {
            return false;
        }

        var ioPath = DownloadPathResolver.ResolveIoPath(audioPath);
        try
        {
            var file = new FileInfo(ioPath);
            if (!file.Exists || file.Length <= 0)
            {
                return false;
            }

            var validation = DeliveredAudioQualityGuard.Validate(payload, ioPath);
            if (!validation.Success)
            {
                return false;
            }

            payload.RequestedQuality = validation.RequestedQuality;
            payload.DeliveredQuality = validation.DeliveredQuality;
            payload.AudioAcquired = true;
            payload.AcquiredAudioPath = DownloadPathResolver.NormalizeDisplayPath(ioPath);
            payload.AcquiredRequestedQuality = validation.RequestedQuality;
            payload.AcquiredDeliveredQuality = validation.DeliveredQuality;
            payload.AcquiredEngine = payload.Engine;
            payload.AcquiredFileSizeBytes = file.Length;
            payload.FinalizationStage = PendingStage;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static async Task PersistAcquiredAsync(
        DownloadQueueRepository repository,
        string queueUuid,
        EngineQueueItemBase payload,
        string audioPath,
        CancellationToken cancellationToken)
    {
        var ioPath = DownloadPathResolver.ResolveIoPath(audioPath);
        var file = new FileInfo(ioPath);
        if (!file.Exists || file.Length <= 0)
        {
            throw new InvalidOperationException($"Downloaded file missing or empty: {audioPath}");
        }

        payload.AudioAcquired = true;
        payload.AcquiredAudioPath = DownloadPathResolver.NormalizeDisplayPath(audioPath);
        payload.AcquiredRequestedQuality = payload.RequestedQuality;
        payload.AcquiredDeliveredQuality = payload.DeliveredQuality;
        payload.AcquiredEngine = payload.Engine;
        payload.AcquiredFileSizeBytes = file.Length;
        payload.FinalizationStage = PendingStage;
        payload.FinalizationError = string.Empty;
        payload.FinalizationInnerError = string.Empty;
        payload.FinalizationRetryAtUtc = null;
        await repository.UpdatePayloadAsync(queueUuid, JsonSerializer.Serialize(payload), cancellationToken);
    }

    public static async Task PersistFinalizationFailureAsync(
        DownloadQueueRepository repository,
        DownloadRetryScheduler retryScheduler,
        IDeezSpoTagListener listener,
        string queueUuid,
        string engine,
        EngineQueueItemBase payload,
        DownloadFinalizationException failure,
        CancellationToken cancellationToken)
    {
        var retryAt = DateTimeOffset.UtcNow.AddSeconds(15);
        payload.FinalizationStage = failure.Stage;
        payload.FinalizationError = failure.OriginalMessage;
        payload.FinalizationInnerError = failure.InnerException?.Message ?? string.Empty;
        payload.FinalizationRetryAtUtc = retryAt;
        if (string.Equals(failure.Stage, DownloadFinalizationStage.ArtworkPrefetch, StringComparison.Ordinal))
        {
            payload.PrefetchedArtworkError = failure.OriginalMessage;
        }

        await repository.UpdatePayloadAsync(queueUuid, JsonSerializer.Serialize(payload), cancellationToken);
        var scheduled = await retryScheduler.ScheduleRetryAsync(queueUuid, engine, failure.UserMessage, cancellationToken);
        if (!scheduled)
        {
            await repository.UpdateStatusAsync(
                queueUuid,
                "failed",
                failure.UserMessage,
                cancellationToken: cancellationToken);
        }
        listener.Send("updateQueue", new
        {
            uuid = queueUuid,
            status = scheduled ? "retry_waiting" : "failed",
            error = failure.UserMessage,
            audioAcquired = true,
            finalizationStage = failure.Stage,
            finalizationRetryAtUtc = retryAt
        });
    }

    public static void MarkCompleted(EngineQueueItemBase payload)
    {
        payload.FinalizationStage = CompletedStage;
        payload.FinalizationError = string.Empty;
        payload.FinalizationInnerError = string.Empty;
        payload.FinalizationRetryAtUtc = null;
    }

    public static void ClearAcquisition(EngineQueueItemBase payload)
    {
        payload.AudioAcquired = false;
        payload.AcquiredAudioPath = string.Empty;
        payload.AcquiredRequestedQuality = string.Empty;
        payload.AcquiredDeliveredQuality = string.Empty;
        payload.AcquiredEngine = string.Empty;
        payload.AcquiredFileSizeBytes = 0;
        payload.FinalizationStage = string.Empty;
        payload.FinalizationError = string.Empty;
        payload.FinalizationInnerError = string.Empty;
        payload.FinalizationRetryAtUtc = null;
    }
}

internal static class DownloadFinalizationStage
{
    public const string ArtworkPrefetch = "artwork_prefetch";
    public const string TagWriting = "tag_writing";
    public const string ArtworkEmbedding = "artwork_embedding";
    public const string FinalVerification = "final_verification";
}

internal sealed class DownloadFinalizationException : InvalidOperationException
{
    public DownloadFinalizationException(string stage, string userMessage, Exception cause)
        : base(cause.Message, cause)
    {
        Stage = stage;
        UserMessage = userMessage;
        OriginalMessage = cause.Message;
    }

    public string Stage { get; }
    public string UserMessage { get; }
    public string OriginalMessage { get; }
}
