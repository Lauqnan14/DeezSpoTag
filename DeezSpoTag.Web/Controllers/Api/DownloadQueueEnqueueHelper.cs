using System.Text.Json;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Models;

namespace DeezSpoTag.Web.Controllers.Api;

internal static class DownloadQueueEnqueueHelper
{
    public static Func<TPayload, int, CancellationToken, Task<EnqueueOutcome>> CreateDedupEnqueueDelegate<TPayload>(
        DownloadQueueRepository queueRepository,
        IDeezSpoTagListener listener,
        ILogger logger)
        where TPayload : EngineQueueItemBase
    {
        return (payload, redownloadCooldownMinutes, cancellationToken) => EnqueueWithDedupAsync(
            payload,
            redownloadCooldownMinutes,
            queueRepository,
            listener,
            logger,
            cancellationToken);
    }

    public static Action<TPayload> CreateQueueAddedNotifier<TPayload>(
        IDeezSpoTagListener listener,
        Func<TPayload, object> payloadMapper)
        where TPayload : class
    {
        return payload => listener.SendAddedToQueue(payloadMapper(payload));
    }

    public static async Task<EnqueueOutcome> EnqueueWithDedupAsync<TPayload>(
        TPayload payload,
        int redownloadCooldownMinutes,
        DownloadQueueRepository queueRepository,
        IDeezSpoTagListener listener,
        ILogger logger,
        CancellationToken cancellationToken)
        where TPayload : EngineQueueItemBase
    {
        var durationMs = ResolveDurationMs(payload);
        var duplicateRequest = BuildDuplicateLookupRequest(payload, durationMs, redownloadCooldownMinutes);
        if (await queueRepository.ExistsDuplicateAsync(duplicateRequest, cancellationToken))
        {
            return await HandleDuplicateLookupMatchAsync(payload, queueRepository, listener, logger, cancellationToken);
        }

        var existing = await queueRepository.GetByMetadataAsync(
            payload.Engine,
            payload.Artist,
            payload.Title,
            payload.ContentType,
            payload.DestinationFolderId,
            cancellationToken);
        if (existing is not null)
        {
            return await HandleExistingQueueEntryAsync(payload, existing, queueRepository, cancellationToken);
        }

        return await EnqueueNewItemAsync(payload, durationMs, queueRepository, cancellationToken);
    }

    private static DuplicateLookupRequest BuildDuplicateLookupRequest<TPayload>(
        TPayload payload,
        int? durationMs,
        int redownloadCooldownMinutes)
        where TPayload : EngineQueueItemBase
    {
        return new DuplicateLookupRequest
        {
            Isrc = payload.Isrc,
            DeezerTrackId = payload.DeezerId,
            SpotifyTrackId = payload.SpotifyId,
            AppleTrackId = payload.AppleId,
            ArtistName = payload.Artist,
            TrackTitle = payload.Title,
            DurationMs = durationMs,
            DestinationFolderId = payload.DestinationFolderId,
            ContentType = payload.ContentType,
            RedownloadCooldownMinutes = redownloadCooldownMinutes
        };
    }

    private static int? ResolveDurationMs<TPayload>(TPayload payload)
        where TPayload : EngineQueueItemBase
        => payload.DurationSeconds > 0 ? payload.DurationSeconds * 1000 : (int?)null;

    private static async Task<EnqueueOutcome> HandleDuplicateLookupMatchAsync<TPayload>(
        TPayload payload,
        DownloadQueueRepository queueRepository,
        IDeezSpoTagListener listener,
        ILogger logger,
        CancellationToken cancellationToken)
        where TPayload : EngineQueueItemBase
    {
        var duplicate = await queueRepository.GetByMetadataAsync(
            payload.Engine,
            payload.Artist,
            payload.Title,
            payload.ContentType,
            payload.DestinationFolderId,
            cancellationToken);
        if (duplicate is null)
        {
            logger.LogWarning(
                "Skip enqueue (engine={Engine} reason=duplicate): {Artist} - {Title}",
                payload.Engine,
                payload.Artist,
                payload.Title);
            return EnqueueOutcome.Skipped("queue_duplicate", "Skipped: matching track is already in queue.");
        }

        return await HandleDuplicateStatusAsync(payload, duplicate, queueRepository, listener, logger, cancellationToken);
    }

    private static async Task<EnqueueOutcome> HandleDuplicateStatusAsync<TPayload>(
        TPayload payload,
        DownloadQueueItem duplicate,
        DownloadQueueRepository queueRepository,
        IDeezSpoTagListener listener,
        ILogger logger,
        CancellationToken cancellationToken)
        where TPayload : EngineQueueItemBase
    {
        var duplicateStatus = duplicate.Status ?? string.Empty;
        if (IsRetryableQueueStatus(duplicateStatus))
        {
            await queueRepository.RequeueAsync(duplicate.QueueUuid, cancellationToken);
            logger.LogInformation("Duplicate triggered retry (engine={Engine}): {QueueUuid}", payload.Engine, duplicate.QueueUuid);
            listener.Send("updateQueue", new
            {
                uuid = duplicate.QueueUuid,
                status = "inQueue",
                progress = 0,
                downloaded = 0,
                failed = 0,
                error = default(string)
            });
            return EnqueueOutcome.Queued("queue_requeued", "Duplicate triggered retry", duplicate.QueueUuid);
        }

        if (IsCompletedStatus(duplicateStatus))
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Skip enqueue (engine={Engine} reason=recently_downloaded): {Artist} - {Title}",
                    payload.Engine,
                    payload.Artist,
                    payload.Title);
            }
            return EnqueueOutcome.Skipped("queue_recently_downloaded", "Skipped: track was downloaded recently.");
        }

        logger.LogWarning(
            "Skip enqueue (engine={Engine} reason=duplicate): {Artist} - {Title}",
            payload.Engine,
            payload.Artist,
            payload.Title);
        return EnqueueOutcome.Skipped("queue_duplicate", "Skipped: matching track is already in queue.");
    }

    private static bool IsRetryableQueueStatus(string status)
        => status is "failed" or "canceled" or "cancelled";

    private static bool IsCompletedStatus(string status)
        => status.Equals("completed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("complete", StringComparison.OrdinalIgnoreCase);

    private static async Task<EnqueueOutcome> HandleExistingQueueEntryAsync<TPayload>(
        TPayload payload,
        DownloadQueueItem existing,
        DownloadQueueRepository queueRepository,
        CancellationToken cancellationToken)
        where TPayload : EngineQueueItemBase
    {
        var existingStatus = existing.Status ?? string.Empty;
        if (IsQueuedQueueStatus(existingStatus))
        {
            return EnqueueOutcome.Skipped("queue_duplicate", "Skipped: matching track is already in queue.");
        }

        if (IsCompletedStatus(existingStatus))
        {
            return EnqueueOutcome.Skipped("queue_recently_downloaded", "Skipped: track was downloaded recently.");
        }

        payload.Id = existing.QueueUuid;
        var payloadJson = JsonSerializer.Serialize(payload);
        await queueRepository.UpdateEngineAsync(existing.QueueUuid, payload.Engine, cancellationToken);
        await queueRepository.UpdateQueueMetadataAsync(
            existing.QueueUuid,
            null,
            payload.ContentType,
            payload.DestinationFolderId ?? existing.DestinationFolderId,
            cancellationToken);
        await queueRepository.UpdatePayloadAsync(existing.QueueUuid, payloadJson, cancellationToken);
        await queueRepository.UpdateStatusAsync(
            existing.QueueUuid,
            "queued",
            error: null,
            downloaded: 0,
            failed: 0,
            progress: 0,
            cancellationToken: cancellationToken);
        return EnqueueOutcome.Queued("queue_requeued", "Duplicate triggered retry", existing.QueueUuid);
    }

    private static bool IsQueuedQueueStatus(string status)
        => status is "queued" or "running" or "paused";

    private static async Task<EnqueueOutcome> EnqueueNewItemAsync<TPayload>(
        TPayload payload,
        int? durationMs,
        DownloadQueueRepository queueRepository,
        CancellationToken cancellationToken)
        where TPayload : EngineQueueItemBase
    {
        var json = JsonSerializer.Serialize(payload);
        var item = new DownloadQueueItem(
            Id: 0,
            QueueUuid: payload.Id,
            Engine: payload.Engine,
            ArtistName: payload.Artist,
            TrackTitle: payload.Title,
            Isrc: payload.Isrc,
            DeezerTrackId: payload.DeezerId,
            DeezerAlbumId: null,
            DeezerArtistId: null,
            SpotifyTrackId: payload.SpotifyId,
            SpotifyAlbumId: null,
            SpotifyArtistId: null,
            AppleTrackId: payload.AppleId,
            AppleAlbumId: null,
            AppleArtistId: null,
            DurationMs: durationMs,
            DestinationFolderId: payload.DestinationFolderId,
            QualityRank: null,
            QueueOrder: null,
            ContentType: payload.ContentType,
            Status: "queued",
            PayloadJson: json,
            Progress: 0,
            Downloaded: 0,
            Failed: 0,
            Error: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

        await queueRepository.EnqueueAsync(item, cancellationToken);
        return EnqueueOutcome.Queued();
    }
}

public readonly record struct EnqueueOutcome(
    bool Success,
    bool AlreadyQueued,
    string? ReasonCode,
    string? Message,
    string? QueueUuid)
{
    public static EnqueueOutcome Queued(string? reasonCode = null, string? message = null, string? queueUuid = null)
        => new(true, false, reasonCode, message, queueUuid);

    public static EnqueueOutcome Skipped(string reasonCode, string message)
        => new(false, true, reasonCode, message, null);
}
