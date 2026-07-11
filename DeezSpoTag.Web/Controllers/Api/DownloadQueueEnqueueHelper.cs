using System.Text.Json;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Core.Utils;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Web.Controllers.Api;

internal static class DownloadQueueEnqueueHelper
{
    private const string DuplicateReasonCode = "queue_duplicate";
    private const string DuplicateQueueMessage = "Skipped: matching track is already in queue.";
    private const string QueuedStatus = "queued";

    public static Func<TPayload, int, CancellationToken, Task<EnqueueOutcome>> CreateDedupEnqueueDelegate<TPayload>(
        DownloadQueueRepository queueRepository,
        DownloadDedupeService dedupeService,
        DeezSpoTagSettingsService settingsService,
        IServiceProvider serviceProvider)
        where TPayload : EngineQueueItemBase
    {
        return (payload, redownloadCooldownMinutes, cancellationToken) => EnqueueWithDedupAsync(
            payload,
            redownloadCooldownMinutes,
            queueRepository,
            dedupeService,
            settingsService,
            serviceProvider,
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
        DownloadDedupeService dedupeService,
        DeezSpoTagSettingsService settingsService,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
        where TPayload : EngineQueueItemBase
    {
        var durationMs = ResolveDurationMs(payload);
        var requestedLocalQualityRank = ResolveRequestedLocalQualityRank(payload);
        var finalOutputPath = await ResolveExpectedFinalOutputPathAsync(
            payload,
            settingsService,
            serviceProvider,
            cancellationToken);
        var dedupeDecision = await dedupeService.CheckAsync(
            DownloadDedupeService.FromQueuePayload(
                payload,
                durationMs,
                requestedLocalQualityRank,
                finalOutputPath: finalOutputPath),
            cancellationToken);
        if (!dedupeDecision.Allowed)
        {
            return EnqueueOutcome.Skipped(
                dedupeDecision.ReasonCode ?? DuplicateReasonCode,
                dedupeDecision.Message ?? DuplicateQueueMessage,
                dedupeDecision.QueueUuid);
        }

        return await EnqueueNewItemAsync(payload, durationMs, queueRepository, cancellationToken);
    }

    private static int? ResolveDurationMs<TPayload>(TPayload payload)
        where TPayload : EngineQueueItemBase
        => payload.DurationSeconds > 0 ? payload.DurationSeconds * 1000 : (int?)null;

    private static int? ResolveRequestedLocalQualityRank<TPayload>(TPayload payload)
        where TPayload : EngineQueueItemBase
    {
        if (int.TryParse(payload.Quality, out var numericQuality))
        {
            return MediaQualityInference.MapRequestedNumericQualityToLocalRank(numericQuality);
        }

        return MediaQualityInference.InferLocalQualityRankFromText(
            payload.Quality,
            "ATMOS",
            treatPodcastAsVideo: false);
    }

    private static async Task<string?> ResolveExpectedFinalOutputPathAsync<TPayload>(
        TPayload payload,
        DeezSpoTagSettingsService settingsService,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
        where TPayload : EngineQueueItemBase
    {
        if (payload.DestinationFolderId == null)
        {
            return null;
        }

        var settings = CloneSettings(settingsService.LoadSettings());
        using var scope = serviceProvider.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IDownloadTagSettingsResolver>();
        var loggerFactory = scope.ServiceProvider.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger("DownloadQueueEnqueueHelper")
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        await DownloadEngineSettingsHelper.ResolveAndApplyProfileAsync(
            resolver,
            settings,
            payload.DestinationFolderId,
            logger,
            cancellationToken,
            new DownloadEngineSettingsHelper.ProfileResolutionOptions(CurrentEngine: payload.Engine));

        var pathProcessor = scope.ServiceProvider.GetRequiredService<EnhancedPathTemplateProcessor>();
        var context = EngineAudioPostDownloadHelper.BuildTrackContext(
            payload,
            settings,
            pathProcessor,
            payload.Engine,
            ResolvePayloadSourceId(payload));

        return !string.IsNullOrWhiteSpace(context.PathResult.WritePath)
            ? DownloadPathResolver.ResolveIoPath(context.PathResult.WritePath)
            : Path.Join(
                DownloadPathResolver.ResolveIoPath(context.PathResult.FilePath),
                context.PathResult.Filename);
    }

    private static DeezSpoTagSettings CloneSettings(DeezSpoTagSettings settings)
        => JsonSerializer.Deserialize<DeezSpoTagSettings>(JsonSerializer.Serialize(settings)) ?? settings;

    private static string? ResolvePayloadSourceId<TPayload>(TPayload payload)
        where TPayload : EngineQueueItemBase
        => payload.Engine?.Trim().ToLowerInvariant() switch
        {
            "deezer" => payload.DeezerId,
            "spotify" => payload.SpotifyId,
            "apple" => payload.AppleId,
            "qobuz" => payload.QobuzId,
            "tidal" => payload.TidalId,
            "amazon" => payload.AmazonId,
            _ => null
        };

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
            Status: QueuedStatus,
            PayloadJson: json,
            Progress: 0,
            Downloaded: 0,
            Failed: 0,
            Error: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

        var insertId = await queueRepository.EnqueueAsync(item, cancellationToken);
        if (!insertId.HasValue || insertId.Value <= 0)
        {
            return EnqueueOutcome.Skipped(DuplicateReasonCode, DuplicateQueueMessage);
        }

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

    public static EnqueueOutcome Skipped(string reasonCode, string message, string? queueUuid)
        => new(false, true, reasonCode, message, queueUuid);
}
