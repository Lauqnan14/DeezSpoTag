using System.Text.Json;
using System.Text.Json.Nodes;
using DeezSpoTag.Core.Security;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed class ManualUnavailableRetryService : BackgroundService
{
    private const int BatchSize = 10;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromDays(7);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ManualUnavailableRetryService> _logger;

    public ManualUnavailableRetryService(
        IServiceScopeFactory scopeFactory,
        ILogger<ManualUnavailableRetryService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessDueRetriesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
            {
                _logger.LogWarning(ex, "Manual unavailable retry sweep failed.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task ProcessDueRetriesAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<LibraryRepository>();
        if (!repository.IsConfigured)
        {
            return;
        }

        var dueTracks = await repository.GetDueManualUnavailableTracksAsync(
            DateTimeOffset.UtcNow,
            BatchSize,
            cancellationToken);
        if (dueTracks.Count == 0)
        {
            return;
        }

        var intentService = scope.ServiceProvider.GetRequiredService<DownloadIntentService>();
        var app = scope.ServiceProvider.GetRequiredService<DeezSpoTagApp>();
        foreach (var track in dueTracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RetryTrackAsync(repository, intentService, app, track, cancellationToken);
        }
    }

    private async Task RetryTrackAsync(
        LibraryRepository repository,
        DownloadIntentService intentService,
        DeezSpoTagApp app,
        ManualUnavailableTrackDto track,
        CancellationToken cancellationToken)
    {
        // The original queue row is still in the table at a failed/unavailable status, so a fresh
        // enqueue is rejected as a queue duplicate. Requeue that row in place instead, which also
        // resets the persisted fallback plan and the exhausted retry-attempt counter.
        if (await TryRequeueExistingRowAsync(repository, app, track, cancellationToken))
        {
            return;
        }

        var intent = BuildIntent(track);
        try
        {
            var result = await intentService.EnqueueManualAsync(intent, cancellationToken);
            await RecordFreshEnqueueOutcomeAsync(repository, track, result, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Manual unavailable retry failed for {Title} by {Artist}.",
                LogSanitizer.OneLine(track.Title),
                LogSanitizer.OneLine(track.Artist));
            await repository.ScheduleManualUnavailableTrackRetryAsync(
                track.Id,
                DateTimeOffset.UtcNow.Add(RetryDelay),
                ex.Message,
                CancellationToken.None);
        }
    }

    /// <summary>
    /// Books the re-check outcome for a fresh enqueue started because the original queue row no
    /// longer exists. The record must keep monitoring until a later re-check reaches a terminal
    /// conclusion on the new row, and it must do so by following the NEW row's uuid: the next
    /// re-check observes whether the new download completed, failed or was cancelled. When the
    /// rebind fails (the new uuid is already monitored, or nothing was queued) the record stays
    /// rescheduled on its current identity for the next re-check.
    /// </summary>
    private async Task RecordFreshEnqueueOutcomeAsync(
        LibraryRepository repository,
        ManualUnavailableTrackDto track,
        DownloadIntentResult result,
        CancellationToken cancellationToken)
    {
        var nextRetryAtUtc = DateTimeOffset.UtcNow.Add(RetryDelay);
        if (result.Queued.Count > 0)
        {
            var newQueueUuid = FirstNonEmpty(result.Queued[0], result.RelatedQueueUuids.FirstOrDefault());
            if (!string.IsNullOrWhiteSpace(newQueueUuid)
                && await repository.RebindManualUnavailableTrackQueueAsync(
                    track.Id,
                    newQueueUuid,
                    nextRetryAtUtc,
                    "Fresh queue row enqueued; the next re-check reviews the attempt outcome.",
                    cancellationToken))
            {
                _logger.LogInformation(
                    "Fresh enqueue for {Title} by {Artist} created {QueueUuid}; monitoring rebound to it.",
                    LogSanitizer.OneLine(track.Title),
                    LogSanitizer.OneLine(track.Artist),
                    LogSanitizer.OneLine(newQueueUuid));
                return;
            }

            // Keep the record scheduled even when the rebind could not land: deleting it here
            // would abandon the track if the fresh attempt fails again.
            _logger.LogWarning(
                "Fresh enqueue for {Title} by {Artist} could not rebind monitoring to {QueueUuid}; the record stays on its current uuid.",
                LogSanitizer.OneLine(track.Title),
                LogSanitizer.OneLine(track.Artist),
                LogSanitizer.OneLine(FirstNonEmpty(result.Queued[0], result.RelatedQueueUuids.FirstOrDefault()) ?? "(none)"));
        }

        var reason = result.Queued.Count > 0
            ? "Fresh queue row enqueued; the next re-check reviews the attempt outcome."
            : FirstNonEmpty(result.Message, result.SkipReasons.FirstOrDefault(), "Track still unavailable from enabled sources.");
        await repository.ScheduleManualUnavailableTrackRetryAsync(
            track.Id,
            nextRetryAtUtc,
            reason,
            cancellationToken);
        _logger.LogInformation(
            "Fresh enqueue outcome for {Title} by {Artist}: record stays scheduled ({Reason}).",
            LogSanitizer.OneLine(track.Title),
            LogSanitizer.OneLine(track.Artist),
            LogSanitizer.OneLine(reason));
    }

    /// <summary>
    /// Returns true when the tracked row was handled here, so the caller must not fall through to
    /// a fresh enqueue. Only a missing queue row falls through: while the row still exists, a
    /// fresh enqueue is rejected as a queue duplicate. A user-cancelled row is retired instead of
    /// revived, because <see cref="DeezSpoTagApp.RetryDownloadAsync"/> requeues with a manual origin
    /// and would clear the cancellation.
    /// </summary>
    private async Task<bool> TryRequeueExistingRowAsync(
        LibraryRepository repository,
        DeezSpoTagApp app,
        ManualUnavailableTrackDto track,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(track.QueueUuid))
        {
            return false;
        }

        try
        {
            var existing = await app.GetQueueItemAsync(track.QueueUuid, cancellationToken);
            if (existing == null)
            {
                return false;
            }

            if (IsCanceledStatus(existing.Status))
            {
                await repository.DeleteManualUnavailableTrackAsync(track.Id, cancellationToken);
                _logger.LogInformation(
                    "Retired unavailable retry for {Title} by {Artist}; the original download was cancelled.",
                    LogSanitizer.OneLine(track.Title),
                    LogSanitizer.OneLine(track.Artist));
                return true;
            }

            var decision = ClassifyRowForRecheck(existing.Status);
            if (decision == RecheckDecision.RetireObtained)
            {
                // The track was obtained: monitoring concluded and the record retires.
                await repository.DeleteManualUnavailableTrackAsync(track.Id, cancellationToken);
                _logger.LogInformation(
                    "Retired unavailable retry for {Title} by {Artist}; the original download completed.",
                    LogSanitizer.OneLine(track.Title),
                    LogSanitizer.OneLine(track.Artist));
                return true;
            }

            if (decision == RecheckDecision.WaitActive)
            {
                // The row is live (queued/running): resetting it in place would discard the live
                // attempt's progress and could double-process it. Wait for the outcome instead.
                await repository.ScheduleManualUnavailableTrackRetryAsync(
                    track.Id,
                    DateTimeOffset.UtcNow.Add(RetryDelay),
                    "Waiting for the active download to conclude before the next re-check.",
                    cancellationToken);
                _logger.LogInformation(
                    "Unavailable retry for {Title} by {Artist} waits on the active download; record rescheduled.",
                    LogSanitizer.OneLine(track.Title),
                    LogSanitizer.OneLine(track.Artist));
                return true;
            }

            // Automatic re-check: pass the Monitoring origin so the requeue SQL refuses to
            // revive a row the user cancelled (a manual origin would clear the cancellation
            // if a cancel lands between the status check above and this retry).
            if (await app.RetryDownloadAsync(track.QueueUuid, cancellationToken, origin: QueueRequeueOrigin.Monitoring))
            {
                // Keep the record scheduled: if this re-attempt fails again, the next re-check
                // must still fire. The record retires only when a re-check reaches a terminal
                // conclusion (obtained or cancelled), never immediately after queueing a retry.
                await repository.ScheduleManualUnavailableTrackRetryAsync(
                    track.Id,
                    DateTimeOffset.UtcNow.Add(RetryDelay),
                    "Requeued from the original queue row; the next re-check reviews the retry outcome.",
                    cancellationToken);
                _logger.LogInformation(
                    "Unavailable retry requeued {Title} by {Artist} from the original queue row; monitoring continues.",
                    LogSanitizer.OneLine(track.Title),
                    LogSanitizer.OneLine(track.Artist));
                return true;
            }

            // The requeue was rejected (for example a cancel landed between the status read and
            // the requeue). The row still exists, so a fresh enqueue would be rejected as a queue
            // duplicate: reschedule and let the next re-check review the row again.
            await repository.ScheduleManualUnavailableTrackRetryAsync(
                track.Id,
                DateTimeOffset.UtcNow.Add(RetryDelay),
                "Requeue was not accepted; the next re-check reviews the row again.",
                cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed re-check must not abandon the track. The queue row still exists here, so a
            // fresh enqueue would be rejected as a duplicate: reschedule and keep monitoring.
            _logger.LogWarning(
                ex,
                "In-place requeue failed for {Title} by {Artist}; the next re-check will retry.",
                LogSanitizer.OneLine(track.Title),
                LogSanitizer.OneLine(track.Artist));
            try
            {
                await repository.ScheduleManualUnavailableTrackRetryAsync(
                    track.Id,
                    DateTimeOffset.UtcNow.Add(RetryDelay),
                    ex.Message,
                    CancellationToken.None);
            }
            catch (Exception scheduleEx) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(scheduleEx))
            {
                _logger.LogWarning(
                    scheduleEx,
                    "Could not reschedule the unavailable retry for {Title} by {Artist}.",
                    LogSanitizer.OneLine(track.Title),
                    LogSanitizer.OneLine(track.Artist));
            }

            return true;
        }
    }

    private enum RecheckDecision
    {
        RetireObtained,
        Requeue,
        WaitActive
    }

    private static RecheckDecision ClassifyRowForRecheck(string? status)
    {
        var normalized = (status ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized is "failed" or "unavailable" or "skipped")
        {
            return RecheckDecision.Requeue;
        }

        if (normalized is "completed" or "complete" or "canceled" or "cancelled")
        {
            return RecheckDecision.RetireObtained;
        }

        return RecheckDecision.WaitActive;
    }

    private static bool IsCanceledStatus(string? status)
        => (status ?? string.Empty).Trim().ToLowerInvariant() is "canceled" or "cancelled";

    internal static DownloadIntent BuildIntent(ManualUnavailableTrackDto track)
    {
        // The normalised columns are the repaired, authoritative copy. The payload is only a fallback,
        // for a record written before those columns existed.
        var payload = ParsePayload(track.PayloadJson);
        return new DownloadIntent
        {
            SourceService = FirstNonEmpty(track.SourceService, ReadString(payload, "SourceService", "sourceService"), track.Engine) ?? string.Empty,
            SourceUrl = FirstNonEmpty(track.SourceUrl, ReadString(payload, "SourceUrl", "sourceUrl", "Url", "url")) ?? string.Empty,
            PreferredEngine = FirstNonEmpty(track.Engine, ReadString(payload, "PreferredEngine", "preferredEngine", "Engine", "engine")) ?? string.Empty,
            SpotifyId = FirstNonEmpty(track.SpotifyId, ReadString(payload, "SpotifyId", "spotifyId", "spotifyTrackId")) ?? string.Empty,
            DeezerId = FirstNonEmpty(track.DeezerId, ReadString(payload, "DeezerId", "deezerId", "deezerTrackId")) ?? string.Empty,
            AppleId = FirstNonEmpty(track.AppleId, ReadString(payload, "AppleId", "appleId", "appleTrackId")) ?? string.Empty,
            QobuzId = FirstNonEmpty(track.QobuzId, ReadString(payload, "QobuzId", "qobuzId", "qobuzTrackId")) ?? string.Empty,
            TidalId = FirstNonEmpty(track.TidalId, ReadString(payload, "TidalId", "tidalId", "tidalTrackId")) ?? string.Empty,
            AmazonId = FirstNonEmpty(track.AmazonId, ReadString(payload, "AmazonId", "amazonId", "amazonTrackId")) ?? string.Empty,
            Isrc = FirstNonEmpty(track.Isrc, ReadString(payload, "Isrc", "isrc")) ?? string.Empty,
            Title = FirstNonEmpty(track.Title, ReadString(payload, "Title", "title")) ?? string.Empty,
            Artist = FirstNonEmpty(track.Artist, ReadString(payload, "Artist", "artist")) ?? string.Empty,
            Album = FirstNonEmpty(track.Album, ReadString(payload, "Album", "album", "CollectionName", "collectionName")) ?? string.Empty,
            AlbumArtist = FirstNonEmpty(track.AlbumArtist, ReadString(payload, "AlbumArtist", "albumArtist")) ?? string.Empty,
            // Never the "Unavailable Tracks" playlist artwork. Cover feeds the post-download artwork
            // pipeline, so a placeholder would be written into the file's tags as if it were real.
            Cover = ResolveRetryCover(track.CoverUrl, payload),
            Quality = FirstNonEmpty(track.Quality, ReadString(payload, "Quality", "quality")) ?? string.Empty,
            ContentType = FirstNonEmpty(track.ContentType, ReadString(payload, "ContentType", "contentType"), "music") ?? "music",
            DestinationFolderId = track.DestinationFolderId ?? ReadInt64(payload, "DestinationFolderId", "destinationFolderId"),
            DurationMs = FirstPositive(
                track.DurationMs,
                ReadPositiveInt32(payload, "DurationMs", "durationMs"),
                ReadDurationSecondsAsMilliseconds(payload)) ?? 0,
            TrackNumber = FirstPositive(
                track.TrackNumber,
                ReadPositiveInt32(payload, "TrackNumber", "trackNumber", "SpotifyTrackNumber", "spotifyTrackNumber")) ?? 0,
            TrackTotal = FirstPositive(
                track.TrackTotal,
                ReadPositiveInt32(payload, "TrackTotal", "trackTotal", "SpotifyTotalTracks", "spotifyTotalTracks")) ?? 0,
            DiscNumber = FirstPositive(
                track.DiscNumber,
                ReadPositiveInt32(payload, "DiscNumber", "discNumber", "SpotifyDiscNumber", "spotifyDiscNumber")) ?? 0,
            DiscTotal = FirstPositive(
                track.DiscTotal,
                ReadPositiveInt32(payload, "DiscTotal", "discTotal")) ?? 0,
            ReleaseDate = FirstNonEmpty(track.ReleaseDate, ReadString(payload, "ReleaseDate", "releaseDate", "release_date")) ?? string.Empty,
            // Absent stays absent: a null here is not the same claim as false.
            Explicit = FirstNullable(track.Explicit, ReadBoolean(payload, "Explicit", "explicit", "explicit_lyrics"))
        };
    }

    private static JsonObject ParsePayload(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(payloadJson) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static string? ReadString(JsonObject payload, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (payload[key] is not JsonNode node)
            {
                continue;
            }

            var value = node.ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// The record's own artwork, falling back to the legacy payload. The queue writes a PLACEHOLDER
    /// into "cover" when it found no artwork (<c>QueuePayloadBuilder.DefaultCoverPath</c>), so a
    /// plain key lookup can return a path that is not a cover. Promoting one would tag the file with
    /// the "Unavailable Tracks" playlist image, so the placeholder is skipped and a real cover
    /// further down the key list still wins.
    /// </summary>
    private static string ResolveRetryCover(string? recordCover, JsonObject payload)
    {
        var cover = FirstNonEmpty(recordCover);
        if (cover is not null && !IsCoverPlaceholder(cover))
        {
            return cover;
        }

        foreach (var value in new[] { "Cover", "cover", "CoverUrl", "coverUrl", "albumCover", "AlbumCover" }
                     .Select(key => ReadString(payload, key))
                     .Where(value => value is not null && !IsCoverPlaceholder(value))
                     .Select(value => value!))
        {
            return value;
        }

        return string.Empty;
    }

    /// <summary>
    /// True for the two paths the queue uses to mean "no artwork".
    /// </summary>
    private static bool IsCoverPlaceholder(string? value)
        => QueuePayloadJsonParser.IsCoverPlaceholder(value);

    private static int? ReadInt32(JsonObject payload, params string[] keys)
        => int.TryParse(ReadString(payload, keys), out var value) ? value : null;

    private static int? ReadPositiveInt32(JsonObject payload, params string[] keys)
    {
        var value = ReadInt32(payload, keys);
        return value is > 0 ? value : null;
    }

    /// <summary>
    /// Legacy payloads carried seconds under their own key. Bounded before multiplying so a bad
    /// payload cannot wrap into a negative duration.
    /// </summary>
    private static int? ReadDurationSecondsAsMilliseconds(JsonObject payload)
    {
        var seconds = ReadPositiveInt32(payload, "DurationSeconds", "durationSeconds");
        return seconds is > 0 && seconds <= int.MaxValue / 1000 ? seconds.Value * 1000 : null;
    }

    /// <summary>
    /// Accepts a JSON boolean, 0/1, or a boolean string in any casing. Returns null when the payload
    /// says nothing.
    /// </summary>
    private static bool? ReadBoolean(JsonObject payload, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (payload[key] is not JsonNode node)
            {
                continue;
            }

            if (node.GetValueKind() == JsonValueKind.True)
            {
                return true;
            }

            if (node.GetValueKind() == JsonValueKind.False)
            {
                return false;
            }

            if (node.GetValueKind() is not (JsonValueKind.String or JsonValueKind.Number))
            {
                continue;
            }

            var value = node.ToString().Trim();
            if (value == "1")
            {
                return true;
            }

            if (value == "0")
            {
                return false;
            }

            if (bool.TryParse(value, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static long? ReadInt64(JsonObject payload, params string[] keys)
        => long.TryParse(ReadString(payload, keys), out var value) ? value : null;

    private static int? FirstPositive(params int?[] values)
    {
        foreach (var value in values.Where(static value => value is > 0))
        {
            return value;
        }

        return null;
    }

    private static bool? FirstNullable(params bool?[] values)
    {
        foreach (var value in values.Where(static value => value.HasValue))
        {
            return value;
        }

        return null;
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!.Trim())
            .FirstOrDefault();
}
