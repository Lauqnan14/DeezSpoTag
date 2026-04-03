using System.Text.RegularExpressions;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Download.Amazon;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Services.Download.Tidal;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Web.Controllers.Api;

internal static class EngineDownloadControllerCommon
{
    internal readonly record struct BatchEnqueueResult(IReadOnlyList<string> Queued, int Skipped)
    {
        public bool Success => Queued.Count > 0;
    }

    internal readonly record struct QueueTrackSeed(
        string? QueueOrigin,
        string? SourceUrl,
        string? CollectionName,
        string? CollectionType,
        string? Title,
        string? Artist,
        string? Album,
        string? AlbumArtist,
        string? Isrc,
        string? Cover,
        string? ReleaseDate,
        string? SpotifyId,
        int DurationSeconds,
        int DurationMs,
        int Position,
        int SpotifyTrackNumber,
        int SpotifyDiscNumber,
        int SpotifyTotalTracks,
        bool UseAlbumTrackNumber);

    internal class EnginePayloadPreparationContext
    {
        public required string Quality { get; init; }
        public long? DestinationFolderId { get; init; }
        public required DeezSpoTagSettings Settings { get; init; }
        public required ISpotifyIdResolver SpotifyIdResolver { get; init; }
        public required ILogger Logger { get; init; }
        public required TimeSpan RegexTimeout { get; init; }
    }

    internal sealed class TidalPayloadPreparationContext : EnginePayloadPreparationContext
    {
        public required Func<string, string?> NormalizeSourceUrl { get; init; }
        public required Func<string, string?> ExtractTrackId { get; init; }
    }

    internal abstract class SpotifyLinkedPayloadMapping<TTrack, TPayload>
        where TPayload : EngineQueueItemBase
    {
        public required string EngineName { get; init; }
        public bool IncludeDeezer { get; init; } = true;
        public required Func<TTrack, string?> GetSourceUrl { get; init; }
        public required Action<TTrack, string> SetSourceUrl { get; init; }
        public required Func<TTrack, string?> GetSpotifyId { get; init; }
        public required Action<TTrack, string> SetSpotifyId { get; init; }
        public required Func<TTrack, string?> GetTitle { get; init; }
        public required Func<TTrack, string?> GetArtist { get; init; }
        public required Func<TTrack, string?> GetAlbum { get; init; }
        public required Func<TTrack, string?> GetIsrc { get; init; }
        public required Func<string, string?> NormalizeSourceUrl { get; init; }
        public required Func<string, TimeSpan, string?> ExtractEngineId { get; init; }
        public required Action<TTrack, string> SetEngineId { get; init; }
        public required Func<TTrack, QueueTrackSeed> CreateTrackSeed { get; init; }
        public required Func<TPayload> CreatePayload { get; init; }
        public required Action<TPayload, TTrack> PopulateEngineFields { get; init; }
    }

    internal sealed class SpotifyLinkedPayloadSpec<TTrack, TPayload>
        : SpotifyLinkedPayloadMapping<TTrack, TPayload>
        where TPayload : EngineQueueItemBase
    {
    }

    internal sealed class SpotifyLinkedPayloadContext<TTrack, TPayload>
        : SpotifyLinkedPayloadMapping<TTrack, TPayload>
        where TPayload : EngineQueueItemBase
    {
        public required string Quality { get; init; }
        public long? DestinationFolderId { get; init; }
        public required DeezSpoTagSettings Settings { get; init; }
        public required ISpotifyIdResolver SpotifyIdResolver { get; init; }
        public required ILogger Logger { get; init; }
        public required TimeSpan RegexTimeout { get; init; }
    }

    internal sealed class BatchEnqueueContext<TTrack, TPayload>
        where TPayload : EngineQueueItemBase
    {
        public required string EngineLabel { get; init; }
        public required string EmptyTracksError { get; init; }
        public required DownloadOrchestrationService OrchestrationService { get; init; }
        public required DeezSpoTagSettingsService SettingsService { get; init; }
        public required LibraryRepository LibraryRepository { get; init; }
        public required ILogger Logger { get; init; }
        public required Func<DeezSpoTagSettings, IActionResult?> ValidateSettings { get; init; }
        public required Func<TTrack, DeezSpoTagSettings, CancellationToken, Task<TPayload?>> PreparePayloadAsync { get; init; }
        public required Func<TPayload, int, CancellationToken, Task<bool>> EnqueueAsync { get; init; }
        public required Action<TPayload> OnQueued { get; init; }
    }

    public static string? TryExtractSpotifyId(string? sourceUrl, TimeSpan regexTimeout)
        => EngineLinkParser.TryExtractSpotifyTrackId(sourceUrl, regexTimeout);

    public static string ResolveContentType(string? quality)
    {
        if (!string.IsNullOrWhiteSpace(quality)
            && quality.Contains("atmos", StringComparison.OrdinalIgnoreCase))
        {
            return DownloadContentTypes.Atmos;
        }

        return DownloadContentTypes.Stereo;
    }

    public static int ResolveDurationSeconds(int durationSeconds, int durationMs)
    {
        if (durationSeconds > 0)
        {
            return durationSeconds;
        }

        return durationMs > 0 ? (int)Math.Round(durationMs / 1000d) : 0;
    }

    public static (List<string> AutoSources, int AutoIndex, string ResolvedQuality) ResolveAutoSourceState(
        DeezSpoTagSettings settings,
        bool includeDeezer,
        string engine,
        string quality)
    {
        var autoSources = DownloadSourceOrder.ResolveQualityAutoSources(
            settings,
            includeDeezer: includeDeezer,
            targetQuality: quality);
        var (resolvedIndex, resolvedQuality) = DownloadSourceOrder.ResolveInitialAutoStep(autoSources, engine, quality);
        var autoIndex = autoSources.Count == 0 ? -1 : Math.Max(0, resolvedIndex);
        return (autoSources, autoIndex, string.IsNullOrWhiteSpace(resolvedQuality) ? quality : resolvedQuality);
    }

    public static void PopulateSharedQueueFields(
        EngineQueueItemBase payload,
        QueueTrackSeed track,
        string quality,
        long? destinationFolderId,
        List<string> autoSources,
        int autoIndex,
        string? contentType = null)
    {
        payload.Id = Guid.NewGuid().ToString("N");
        payload.QueueOrigin = track.QueueOrigin ?? "tracklist";
        payload.SourceUrl = track.SourceUrl ?? string.Empty;
        payload.CollectionName = track.CollectionName ?? string.Empty;
        payload.CollectionType = track.CollectionType ?? string.Empty;
        payload.Title = track.Title ?? string.Empty;
        payload.Artist = track.Artist ?? string.Empty;
        payload.Album = track.Album ?? string.Empty;
        payload.AlbumArtist = track.AlbumArtist ?? track.Artist ?? string.Empty;
        payload.Isrc = track.Isrc ?? string.Empty;
        payload.Cover = track.Cover ?? string.Empty;
        payload.AutoSources = autoSources;
        payload.AutoIndex = autoIndex;
        payload.FallbackPlan = autoSources
            .Select((source, index) =>
            {
                var step = DownloadSourceOrder.DecodeAutoSource(source);
                var engine = string.IsNullOrWhiteSpace(step.Source) ? string.Empty : step.Source;
                return new FallbackPlanStep(
                    StepId: $"step-{index}",
                    Engine: engine,
                    Quality: step.Quality,
                    RequiredInputs: Array.Empty<string>(),
                    ResolutionStrategy: "direct_url");
            })
            .Where(step => !string.IsNullOrWhiteSpace(step.Engine))
            .ToList();
        payload.ReleaseDate = track.ReleaseDate ?? string.Empty;
        payload.DurationSeconds = ResolveDurationSeconds(track.DurationSeconds, track.DurationMs);
        payload.Position = track.Position;
        payload.Quality = quality;
        payload.SpotifyId = track.SpotifyId ?? string.Empty;
        payload.SpotifyTrackNumber = track.SpotifyTrackNumber;
        payload.SpotifyDiscNumber = track.SpotifyDiscNumber;
        payload.SpotifyTotalTracks = track.SpotifyTotalTracks;
        payload.UseAlbumTrackNumber = track.UseAlbumTrackNumber;
        payload.DestinationFolderId = destinationFolderId;
        payload.ContentType = contentType ?? string.Empty;
        payload.Size = 1;
    }

    public static QueueTrackSeed CreateQueueTrackSeed(EngineDownloadTrackDtoBase track)
    {
        return new QueueTrackSeed(
            track.QueueOrigin,
            track.SourceUrl,
            track.CollectionName,
            track.CollectionType,
            track.Title,
            track.Artist,
            track.Album,
            track.AlbumArtist,
            track.Isrc,
            track.Cover,
            track.ReleaseDate,
            track.SpotifyId,
            track.DurationSeconds,
            track.DurationMs,
            track.Position,
            track.SpotifyTrackNumber,
            track.SpotifyDiscNumber,
            track.SpotifyTotalTracks,
            track.UseAlbumTrackNumber);
    }

    public static async Task<BatchEnqueueResult> EnqueueBatchAsync<TTrack, TPayload>(
        IEnumerable<TTrack> tracks,
        Func<TTrack, CancellationToken, Task<TPayload?>> preparePayloadAsync,
        Func<TPayload, CancellationToken, Task<bool>> enqueueAsync,
        Action<TPayload> onQueued,
        CancellationToken cancellationToken)
        where TPayload : EngineQueueItemBase
    {
        var queued = new List<string>();
        var skipped = 0;

        foreach (var track in tracks)
        {
            var payload = await preparePayloadAsync(track, cancellationToken);
            if (payload is null)
            {
                skipped++;
                continue;
            }

            if (await enqueueAsync(payload, cancellationToken))
            {
                queued.Add(payload.Id);
                onQueued(payload);
                continue;
            }

            skipped++;
        }

        return new BatchEnqueueResult(queued, skipped);
    }

    public static async Task<TPayload?> PrepareSpotifyLinkedPayloadAsync<TTrack, TPayload>(
        TTrack track,
        SpotifyLinkedPayloadContext<TTrack, TPayload> context,
        CancellationToken cancellationToken)
        where TPayload : EngineQueueItemBase
    {
        if (string.IsNullOrWhiteSpace(context.GetSourceUrl(track))
            && string.IsNullOrWhiteSpace(context.GetSpotifyId(track)))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(context.GetSpotifyId(track)))
        {
            var resolvedSpotifyId = TryExtractSpotifyId(context.GetSourceUrl(track), context.RegexTimeout)
                ?? await context.SpotifyIdResolver.ResolveTrackIdAsync(
                    context.GetTitle(track) ?? string.Empty,
                    context.GetArtist(track) ?? string.Empty,
                    context.GetAlbum(track),
                    context.GetIsrc(track),
                    cancellationToken);
            if (!string.IsNullOrWhiteSpace(resolvedSpotifyId))
            {
                context.SetSpotifyId(track, resolvedSpotifyId);
            }
        }

        var sourceUrl = context.GetSourceUrl(track);
        if (!string.IsNullOrWhiteSpace(sourceUrl))
        {
            var normalized = context.NormalizeSourceUrl(sourceUrl);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                context.Logger.LogWarning("Skipping {Engine} enqueue: invalid sourceUrl {SourceUrl}", context.EngineName, sourceUrl);
                return null;
            }

            context.SetSourceUrl(track, normalized);
            var extractedEngineId = context.ExtractEngineId(normalized, context.RegexTimeout);
            if (!string.IsNullOrWhiteSpace(extractedEngineId))
            {
                context.SetEngineId(track, extractedEngineId);
            }
        }

        var (autoSources, autoIndex, resolvedQuality) = ResolveAutoSourceState(
            context.Settings,
            context.IncludeDeezer,
            context.EngineName.ToLowerInvariant(),
            context.Quality);
        var payload = context.CreatePayload();
        context.PopulateEngineFields(payload, track);
        PopulateSharedQueueFields(
            payload,
            context.CreateTrackSeed(track),
            resolvedQuality,
            context.DestinationFolderId,
            autoSources,
            autoIndex,
            ResolveContentType(context.Quality));

        return payload;
    }

    public static Task<AmazonQueueItem?> PrepareAmazonPayloadAsync(
        AmazonDownloadTrackDto track,
        EnginePayloadPreparationContext context,
        CancellationToken cancellationToken)
    {
        return PrepareSpotifyLinkedPayloadAsync(
            track,
            CreateSpotifyLinkedPayloadContext(
                context,
                new SpotifyLinkedPayloadSpec<AmazonDownloadTrackDto, AmazonQueueItem>
                {
                    EngineName = "Amazon",
                    GetSourceUrl = static item => item.SourceUrl,
                    SetSourceUrl = static (item, value) => item.SourceUrl = value,
                    GetSpotifyId = static item => item.SpotifyId,
                    SetSpotifyId = static (item, value) => item.SpotifyId = value,
                    GetTitle = static item => item.Title,
                    GetArtist = static item => item.Artist,
                    GetAlbum = static item => item.Album,
                    GetIsrc = static item => item.Isrc,
                    NormalizeSourceUrl = EngineLinkParser.TryNormalizeAmazonUrl,
                    ExtractEngineId = EngineLinkParser.TryExtractAmazonTrackId,
                    SetEngineId = static (item, value) => item.AmazonId = value,
                    CreateTrackSeed = CreateQueueTrackSeed,
                    CreatePayload = static () => new AmazonQueueItem(),
                    PopulateEngineFields = static (payload, item) =>
                    {
                        payload.AmazonId = item.AmazonId ?? string.Empty;
                        payload.AppleId = item.AppleId ?? string.Empty;
                    }
                }),
            cancellationToken);
    }

    public static Task<TidalQueueItem?> PrepareTidalPayloadAsync(
        TidalDownloadTrackDto track,
        TidalPayloadPreparationContext context,
        CancellationToken cancellationToken)
    {
        return PrepareSpotifyLinkedPayloadAsync(
            track,
            CreateSpotifyLinkedPayloadContext(
                context,
                new SpotifyLinkedPayloadSpec<TidalDownloadTrackDto, TidalQueueItem>
                {
                    EngineName = "Tidal",
                    GetSourceUrl = static item => item.SourceUrl,
                    SetSourceUrl = static (item, value) => item.SourceUrl = value,
                    GetSpotifyId = static item => item.SpotifyId,
                    SetSpotifyId = static (item, value) => item.SpotifyId = value,
                    GetTitle = static item => item.Title,
                    GetArtist = static item => item.Artist,
                    GetAlbum = static item => item.Album,
                    GetIsrc = static item => item.Isrc,
                    NormalizeSourceUrl = context.NormalizeSourceUrl,
                    ExtractEngineId = (value, _) => context.ExtractTrackId(value),
                    SetEngineId = static (item, value) => item.TidalId = value,
                    CreateTrackSeed = CreateQueueTrackSeed,
                    CreatePayload = static () => new TidalQueueItem(),
                    PopulateEngineFields = static (payload, item) =>
                    {
                        payload.TidalId = item.TidalId ?? string.Empty;
                        payload.AppleId = item.AppleId ?? string.Empty;
                    }
                }),
            cancellationToken);
    }

    private static SpotifyLinkedPayloadContext<TTrack, TPayload> CreateSpotifyLinkedPayloadContext<TTrack, TPayload>(
        EnginePayloadPreparationContext context,
        SpotifyLinkedPayloadSpec<TTrack, TPayload> spec)
        where TPayload : EngineQueueItemBase
    {
        return new SpotifyLinkedPayloadContext<TTrack, TPayload>
        {
            Quality = context.Quality,
            DestinationFolderId = context.DestinationFolderId,
            Settings = context.Settings,
            SpotifyIdResolver = context.SpotifyIdResolver,
            Logger = context.Logger,
            RegexTimeout = context.RegexTimeout,
            EngineName = spec.EngineName,
            IncludeDeezer = spec.IncludeDeezer,
            GetSourceUrl = spec.GetSourceUrl,
            SetSourceUrl = spec.SetSourceUrl,
            GetSpotifyId = spec.GetSpotifyId,
            SetSpotifyId = spec.SetSpotifyId,
            GetTitle = spec.GetTitle,
            GetArtist = spec.GetArtist,
            GetAlbum = spec.GetAlbum,
            GetIsrc = spec.GetIsrc,
            NormalizeSourceUrl = spec.NormalizeSourceUrl,
            ExtractEngineId = spec.ExtractEngineId,
            SetEngineId = spec.SetEngineId,
            CreateTrackSeed = spec.CreateTrackSeed,
            CreatePayload = spec.CreatePayload,
            PopulateEngineFields = spec.PopulateEngineFields
        };
    }

    public static async Task<IActionResult> HandleBatchEnqueueAsync<TTrack, TPayload>(
        ControllerBase controller,
        IReadOnlyCollection<TTrack>? tracks,
        long? destinationFolderId,
        BatchEnqueueContext<TTrack, TPayload> context)
        where TPayload : EngineQueueItemBase
    {
        var cancellationToken = controller.HttpContext.RequestAborted;
        var downloadGate = await context.OrchestrationService.EvaluateDownloadGateAsync(cancellationToken);
        if (!downloadGate.Allowed)
        {
            return new ObjectResult(new
            {
                error = string.IsNullOrWhiteSpace(downloadGate.Message)
                    ? "Downloads paused while AutoTag is running."
                    : downloadGate.Message
            })
            {
                StatusCode = 409
            };
        }

        if (tracks == null || tracks.Count == 0)
        {
            return new BadRequestObjectResult(new { error = context.EmptyTracksError });
        }

        var settings = context.SettingsService.LoadSettings();
        var destinationCheck = await DownloadDestinationGuard.ValidateAsync(
            destinationFolderId,
            settings.DownloadLocation,
            context.LibraryRepository,
            cancellationToken);
        if (!destinationCheck.Ok)
        {
            return new BadRequestObjectResult(new
            {
                error = destinationCheck.Error ?? "Destination folder is required."
            });
        }

        var validationResult = context.ValidateSettings(settings);
        if (validationResult != null)
        {
            return validationResult;
        }

        var result = await EnqueueBatchAsync(
            tracks,
            (track, ct) => context.PreparePayloadAsync(track, settings, ct),
            (payload, ct) => context.EnqueueAsync(payload, settings.RedownloadCooldownMinutes, ct),
            context.OnQueued,
            cancellationToken);

        context.Logger.LogInformation(
            "{Engine} download enqueue complete: queued {Queued} skipped {Skipped}",
            context.EngineLabel,
            result.Queued.Count,
            result.Skipped);

        return new OkObjectResult(BuildBatchResponse(context.EngineLabel, result));
    }

    public static object BuildBatchResponse(string engineLabel, BatchEnqueueResult result)
    {
        return new
        {
            success = result.Success,
            queued = result.Queued,
            skipped = result.Skipped,
            message = BuildBatchMessage(engineLabel, result)
        };
    }

    private static string BuildBatchMessage(string engineLabel, BatchEnqueueResult result)
    {
        if (!result.Success)
        {
            return "Nothing queued";
        }

        var skippedSuffix = result.Skipped > 0 ? $", skipped {result.Skipped}" : string.Empty;
        return $"Queued {result.Queued.Count} {engineLabel} item(s){skippedSuffix}";
    }
}
