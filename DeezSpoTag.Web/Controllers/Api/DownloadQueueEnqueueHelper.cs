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
        var durationMs = payload.DurationSeconds > 0 ? payload.DurationSeconds * 1000 : (int?)null;
        if (await queueRepository.ExistsDuplicateAsync(
                new DuplicateLookupRequest
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
                },
                cancellationToken))
        {
            var duplicate = await queueRepository.GetByMetadataAsync(payload.Engine, payload.Artist, payload.Title, payload.ContentType, cancellationToken);
            if (duplicate is not null)
            {
                var duplicateStatus = duplicate.Status ?? string.Empty;
                if (duplicateStatus is "failed" or "canceled" or "cancelled")
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

                if (duplicateStatus is "completed" or "complete")
                {
                    logger.LogInformation(
                        "Skip enqueue (engine={Engine} reason=recently_downloaded): {Artist} - {Title}",
                        payload.Engine,
                        payload.Artist,
                        payload.Title);
                    return EnqueueOutcome.Skipped("queue_recently_downloaded", "Skipped: track was downloaded recently.");
                }
            }

            logger.LogWarning(
                "Skip enqueue (engine={Engine} reason=duplicate): {Artist} - {Title}",
                payload.Engine,
                payload.Artist,
                payload.Title);
            return EnqueueOutcome.Skipped("queue_duplicate", "Skipped: matching track is already in queue.");
        }

        var existing = await queueRepository.GetByMetadataAsync(payload.Engine, payload.Artist, payload.Title, payload.ContentType, cancellationToken);
        if (existing is not null)
        {
            var existingStatus = existing.Status ?? string.Empty;
            if (existingStatus is "queued" or "running" or "paused" || IsCompletedStatus(existingStatus))
            {
                return IsCompletedStatus(existingStatus)
                    ? EnqueueOutcome.Skipped("queue_recently_downloaded", "Skipped: track was downloaded recently.")
                    : EnqueueOutcome.Skipped("queue_duplicate", "Skipped: matching track is already in queue.");
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

    private static bool IsCompletedStatus(string status)
    {
        return status.Equals("completed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("complete", StringComparison.OrdinalIgnoreCase);
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
