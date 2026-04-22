using System.Diagnostics;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Globalization;
using System.Text.RegularExpressions;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Core.Models.Download;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Services.Downloader;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Fallback;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CoreTrack = DeezSpoTag.Core.Models.Track;
using CoreArtist = DeezSpoTag.Core.Models.Artist;
using CoreAlbum = DeezSpoTag.Core.Models.Album;

namespace DeezSpoTag.Services.Download.Deezer;

public interface IDeezerQueueContext
{
    bool IsUserPaused(string uuid);
    Task UpdateWatchlistTrackStatusAsync(string payloadJson, string status, CancellationToken cancellationToken);
}

public sealed partial class DeezerEngineProcessor : IQueueEngineProcessor
{
    private const string EngineName = "deezer";
    private const string FailedStatus = "failed";
    private const string CompletedStatus = "completed";
    private const string RunningStatus = "running";
    private const string PausedStatus = "paused";
    private const string CanceledStatus = "canceled";
    private const string UpdateQueueEvent = "updateQueue";
    private const string InvalidPayloadMessage = "Invalid payload";
    private const string DeezerLoginRequiredMessage = "Deezer login required";
    private const string TrackType = "track";
    private const string EpisodeCollectionType = "episode";
    private const string DeezerSource = "deezer";
    private const string SpotifySource = "spotify";
    private const string HearThisBrowserUserAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
    private const string HearThisDownloadPathSegment = "/download/";
    private const string HearThisListenPathSegment = "/listen.mp3";
    private const string HearThisStreamPathSegment = "/stream.mp3";
    private const int EpisodeParallelRangeDownloads = 6;
    private const long EpisodeParallelRangeMinimumBytes = 24L * 1024L * 1024L;
    private const long EpisodeRangeChunkBytes = 8L * 1024L * 1024L;
    private const int EpisodeRangeHardLimit = 96;
    private const int EpisodeStreamBufferBytes = 512 * 1024;
    private static readonly TimeSpan EpisodeReadStallTimeout = TimeSpan.FromSeconds(20);
    private const int EpisodeGatewayCacheMaxEntries = 2048;
    private static readonly TimeSpan EpisodeGatewayCacheTtl = TimeSpan.FromMinutes(20);
    private static readonly ConcurrentDictionary<string, CachedEpisodeGatewayObject> EpisodeGatewayCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> EpisodeGatewayCacheGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly DownloadQueueRepository _queueRepository;
    private readonly DownloadCancellationRegistry _cancellationRegistry;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly IDeezSpoTagListener _listener;
    private readonly DownloadRetryScheduler _retryScheduler;
    private readonly EngineFallbackCoordinator _fallbackCoordinator;
    private readonly IServiceProvider _serviceProvider;
    private readonly IActivityLogWriter _activityLog;
    private readonly DeezSpoTag.Services.Download.Utils.LyricsService _lyricsService;
    private readonly IPostDownloadTaskScheduler _postDownloadTaskScheduler;
    private readonly DeezerClient _deezerClient;
    private readonly AuthenticatedDeezerService _authenticatedDeezerService;
    private readonly TrackDownloader _trackDownloader;
    private readonly IDownloadTagSettingsResolver _tagSettingsResolver;
    private readonly IFolderConversionSettingsOverlay _folderConversionSettingsOverlay;
    private readonly ILogger<DeezerEngineProcessor> _logger;

    public DeezerEngineProcessor(
        IServiceProvider serviceProvider,
        ILogger<DeezerEngineProcessor> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _queueRepository = serviceProvider.GetRequiredService<DownloadQueueRepository>();
        _cancellationRegistry = serviceProvider.GetRequiredService<DownloadCancellationRegistry>();
        _settingsService = serviceProvider.GetRequiredService<DeezSpoTagSettingsService>();
        _listener = serviceProvider.GetRequiredService<IDeezSpoTagListener>();
        _retryScheduler = serviceProvider.GetRequiredService<DownloadRetryScheduler>();
        _fallbackCoordinator = serviceProvider.GetRequiredService<EngineFallbackCoordinator>();
        _activityLog = serviceProvider.GetRequiredService<IActivityLogWriter>();
        _lyricsService = serviceProvider.GetRequiredService<DeezSpoTag.Services.Download.Utils.LyricsService>();
        _postDownloadTaskScheduler = serviceProvider.GetRequiredService<IPostDownloadTaskScheduler>();
        _deezerClient = serviceProvider.GetRequiredService<DeezerClient>();
        _authenticatedDeezerService = serviceProvider.GetRequiredService<AuthenticatedDeezerService>();
        _trackDownloader = serviceProvider.GetRequiredService<TrackDownloader>();
        _tagSettingsResolver = serviceProvider.GetRequiredService<IDownloadTagSettingsResolver>();
        _folderConversionSettingsOverlay = serviceProvider.GetRequiredService<IFolderConversionSettingsOverlay>();
    }

    public string Engine => EngineName;

    Task IQueueEngineProcessor.ProcessQueueItemAsync(
        DownloadQueueItem item,
        IDeezerQueueContext context,
        CancellationToken cancellationToken) =>
        ProcessQueueItemAsync(item, context, cancellationToken);

    public async Task ProcessQueueItemAsync(
        DownloadQueueItem nextItem,
        IDeezerQueueContext context,
        CancellationToken cancellationToken)
    {
        var currentUUID = nextItem.QueueUuid;
        var payloadJson = nextItem.PayloadJson ?? string.Empty;
        if (await HandleMissingPayloadAsync(currentUUID, payloadJson, cancellationToken))
        {
            return;
        }

        var settings = _settingsService.LoadSettings();
        DeezerQueueItem? payload = null;

        using var itemCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cancellationRegistry.Register(currentUUID, itemCts);
        var itemToken = itemCts.Token;

        try
        {
            payload = QueueHelperUtils.DeserializeQueueItem<DeezerQueueItem>(payloadJson);
            if (payload == null)
            {
                await FailQueueWithRetryAsync(currentUUID, InvalidPayloadMessage, "invalid payload", notify: false, itemToken);
                return;
            }

            var completedSuccessfully = await ExecuteQueueItemCoreAsync(
                nextItem,
                context,
                settings,
                currentUUID,
                payloadJson,
                payload,
                itemToken);
            if (completedSuccessfully)
            {
                _listener.SendFinishDownload(currentUUID, payload.Title ?? string.Empty);
                DeezSpoTagSpeedTracker.Clear(currentUUID);
            }
        }
        catch (OperationCanceledException ex)
        {
            if (_cancellationRegistry.WasTimedOut(currentUUID))
            {
                var timeoutException = new TimeoutException(
                    DownloadQueueRecoveryPolicy.BuildStallTimeoutMessage(EngineName),
                    ex);
                await HandleProcessingExceptionAsync(timeoutException, currentUUID, nextItem.Engine, payload, cancellationToken);
                return;
            }

            await HandleCancellationAsync(currentUUID, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await HandleProcessingExceptionAsync(ex, currentUUID, nextItem.Engine, payload, cancellationToken);
        }
        finally
        {
            _cancellationRegistry.Remove(currentUUID);
        }
    }

    private async Task<bool> HandleMissingPayloadAsync(string queueUuid, string payloadJson, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(payloadJson))
        {
            return false;
        }

        _logger.LogWarning("Queue item {UUID} missing payload, marking as failed", queueUuid);
        _activityLog.Warn($"Download failed (engine=deezer reason=missing_payload): {queueUuid}");
        await FailQueueWithRetryAsync(queueUuid, "Missing queue payload", "missing payload", notify: false, cancellationToken);
        return true;
    }

    private async Task<bool> ExecuteQueueItemCoreAsync(
        DownloadQueueItem nextItem,
        IDeezerQueueContext context,
        DeezSpoTagSettings settings,
        string queueUuid,
        string payloadJson,
        DeezerQueueItem payload,
        CancellationToken cancellationToken)
    {
        var isEpisodePayload = IsEpisodePayload(payload);
        payload.Engine = EngineName;
        if (await HandleAtmosOnlyGuardAsync(queueUuid, nextItem.Engine, payload, cancellationToken))
        {
            return false;
        }

        var resolvedDownloadTagSource = await DownloadEngineSettingsHelper.ResolveAndApplyProfileAsync(
            _tagSettingsResolver,
            settings,
            payload.DestinationFolderId,
            _logger,
            cancellationToken,
            new DownloadEngineSettingsHelper.ProfileResolutionOptions(
                CurrentEngine: EngineName,
                RequireProfile: !isEpisodePayload));
        await _folderConversionSettingsOverlay.ApplyAsync(settings, payload.DestinationFolderId, cancellationToken);
        DownloadEngineSettingsHelper.ApplyQualityBucketToSettings(settings, payload.QualityBucket);

        _listener.SendStartDownload(queueUuid);
        _listener.Send(UpdateQueueEvent, new
        {
            uuid = queueUuid,
            progress = payload.Progress,
            downloaded = payload.Downloaded,
            failed = payload.Failed
        });

        await _queueRepository.UpdateStatusAsync(queueUuid, RunningStatus, progress: payload.Progress, cancellationToken: cancellationToken);

        var completedSuccessfully = isEpisodePayload
            ? await ProcessEpisodePayloadAsync(payload, settings, queueUuid, cancellationToken)
            : await ProcessTrackPayloadAsync(
                new TrackPayloadProcessingRequest(
                    nextItem,
                    context,
                    payloadJson,
                    payload,
                    settings,
                    queueUuid,
                    resolvedDownloadTagSource),
                cancellationToken);
        if (!completedSuccessfully)
        {
            return false;
        }

        if (!context.IsUserPaused(queueUuid))
        {
            return true;
        }

        await _queueRepository.UpdateStatusAsync(queueUuid, PausedStatus, cancellationToken: cancellationToken);
        _listener.Send(UpdateQueueEvent, new { uuid = queueUuid, status = PausedStatus });
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Download {UUID} paused by user request", queueUuid);        }
        return false;
    }

    private async Task HandleCancellationAsync(string queueUuid, CancellationToken cancellationToken)
    {
        EngineAudioPostDownloadHelper.ClearPrefetchState(queueUuid);
        if (_cancellationRegistry.WasUserPaused(queueUuid))
        {
            await _queueRepository.UpdateStatusAsync(queueUuid, PausedStatus, cancellationToken: cancellationToken);
            _listener.Send(UpdateQueueEvent, new { uuid = queueUuid, status = PausedStatus });
            return;
        }

        if (_cancellationRegistry.WasUserCanceled(queueUuid))
        {
            await _queueRepository.UpdateStatusAsync(queueUuid, CanceledStatus, cancellationToken: cancellationToken);
            _listener.Send(UpdateQueueEvent, new { uuid = queueUuid, status = CanceledStatus });
            return;
        }

        await _queueRepository.UpdateStatusAsync(queueUuid, CanceledStatus, cancellationToken: cancellationToken);
        _listener.Send(UpdateQueueEvent, new { uuid = queueUuid, status = CanceledStatus });
    }

    private async Task HandleProcessingExceptionAsync(
        Exception ex,
        string queueUuid,
        string currentEngine,
        DeezerQueueItem? payload,
        CancellationToken cancellationToken)
    {
        EngineAudioPostDownloadHelper.ClearPrefetchState(queueUuid);
        _logger.LogError(ex, "Error during Deezer download of {UUID}", queueUuid);
        var isEpisodePayload = payload != null && IsEpisodePayload(payload);
        var fallbackAdvanced = payload != null
            && !isEpisodePayload
            && await TryFallbackAsync(queueUuid, currentEngine, payload, cancellationToken);

        if (!fallbackAdvanced)
        {
            await FailQueueAsync(queueUuid, ex.Message, notify: true, cancellationToken);
            if (!isEpisodePayload)
            {
                _retryScheduler.ScheduleRetry(queueUuid, EngineName, ex.Message);
            }
        }
    }

    private async Task<bool> HandleAtmosOnlyGuardAsync(
        string queueUuid,
        string currentEngine,
        DeezerQueueItem payload,
        CancellationToken cancellationToken)
    {
        if (!DownloadEngineSettingsHelper.IsAtmosOnlyPayload(payload.ContentType, payload.Quality))
        {
            return false;
        }

        const string message = "Atmos payload must be processed by Apple engine.";
        _activityLog.Warn($"Atmos guard blocked non-Apple processing: {queueUuid} engine={EngineName}");
        if (await TryFallbackAsync(queueUuid, currentEngine, payload, cancellationToken))
        {
            return true;
        }

        await FailQueueWithRetryAsync(queueUuid, message, message, notify: false, cancellationToken);
        return true;
    }

    private async Task<bool> ProcessEpisodePayloadAsync(
        DeezerQueueItem payload,
        DeezSpoTagSettings settings,
        string queueUuid,
        CancellationToken cancellationToken)
    {
        var authenticated = await _authenticatedDeezerService.EnsureAuthenticatedAsync();
        if (!authenticated)
        {
            await FailQueueAsync(queueUuid, DeezerLoginRequiredMessage, notify: true, cancellationToken);
            return false;
        }

        return await ProcessEpisodeAsync(payload, settings, queueUuid, cancellationToken);
    }

    private sealed record TrackPayloadProcessingRequest(
        DownloadQueueItem QueueItem,
        IDeezerQueueContext Context,
        string PayloadJson,
        DeezerQueueItem Payload,
        DeezSpoTagSettings Settings,
        string QueueUuid,
        string? ResolvedDownloadTagSource);

    private sealed record TrackDownloadPreparation(
        CoreTrack Track,
        SingleDownloadObject DownloadObject);

    private async Task<bool> ProcessTrackPayloadAsync(
        TrackPayloadProcessingRequest request,
        CancellationToken cancellationToken)
    {
        if (!await EnsureAuthenticatedForTrackDownloadAsync(request, cancellationToken))
        {
            return false;
        }

        var track = await ResolveTrackForDownloadAsync(request, cancellationToken);
        if (track == null)
        {
            return false;
        }

        var preparation = BuildTrackDownloadPreparation(request, track);
        _activityLog.Info($"Download start: {request.QueueUuid} engine=deezer bitrate={preparation.Track.Bitrate}");

        var result = await ExecuteTrackDownloadAsync(request, preparation, cancellationToken);

        if (string.IsNullOrWhiteSpace(result.Path))
        {
            await HandleTrackDownloadFailureAsync(request, result, cancellationToken);
            return false;
        }

        await FinalizeSuccessfulTrackDownloadAsync(request, preparation.Track, result, cancellationToken);
        return true;
    }

    private async Task<bool> EnsureAuthenticatedForTrackDownloadAsync(
        TrackPayloadProcessingRequest request,
        CancellationToken cancellationToken)
    {
        if (await _authenticatedDeezerService.EnsureAuthenticatedAsync())
        {
            return true;
        }

        if (await TryFallbackAsync(request.QueueUuid, request.QueueItem.Engine, request.Payload, cancellationToken))
        {
            return false;
        }

        await FailQueueWithRetryAsync(
            request.QueueUuid,
            DeezerLoginRequiredMessage,
            "not logged in",
            notify: false,
            cancellationToken);
        return false;
    }

    private async Task<CoreTrack?> ResolveTrackForDownloadAsync(
        TrackPayloadProcessingRequest request,
        CancellationToken cancellationToken)
    {
        var track = await BuildTrackAsync(request.Payload, request.ResolvedDownloadTagSource);
        if (track != null)
        {
            return track;
        }

        if (await TryFallbackAsync(request.QueueUuid, request.QueueItem.Engine, request.Payload, cancellationToken))
        {
            return null;
        }

        await FailQueueWithRetryAsync(
            request.QueueUuid,
            "Track metadata unavailable",
            "metadata missing",
            notify: false,
            cancellationToken);
        return null;
    }

    private static TrackDownloadPreparation BuildTrackDownloadPreparation(
        TrackPayloadProcessingRequest request,
        CoreTrack track)
    {
        var requestedBitrate = ResolveRequestedBitrate(request.Payload);
        var resolvedBitrate = DownloadSourceOrder.ResolveDeezerBitrate(request.Settings, requestedBitrate);
        track.Bitrate = resolvedBitrate;
        var resolvedTagSource = ResolveTagSource(request.Payload.SourceService, request.ResolvedDownloadTagSource);
        track.Source = resolvedTagSource;
        track.SourceId = ResolveTagSourceId(request.Payload, resolvedTagSource);
        track.ApplySettings(request.Settings);

        var downloadObject = new SingleDownloadObject
        {
            Uuid = request.QueueUuid,
            Title = string.IsNullOrWhiteSpace(request.Payload.Title) ? (track.Title ?? string.Empty) : request.Payload.Title,
            Bitrate = resolvedBitrate,
            Track = track,
            Album = track.Album,
            Artist = track.MainArtist,
            Size = 1,
            Type = TrackType,
            Single = new DownloadSingle
            {
                Track = track,
                Album = track.Album
            }
        };

        return new TrackDownloadPreparation(track, downloadObject);
    }

    private Task<TrackDownloadResult> ExecuteTrackDownloadAsync(
        TrackPayloadProcessingRequest request,
        TrackDownloadPreparation preparation,
        CancellationToken cancellationToken)
    {
        return _trackDownloader.DownloadTrackAsync(new TrackDownloader.TrackDownloadRequest(
            Track: preparation.Track,
            Album: preparation.Track.Album,
            Playlist: null,
            DownloadObject: preparation.DownloadObject,
            Settings: request.Settings,
            Listener: new DownloadListenerAdapter(_listener),
            EnableDeferredSidecarTasks: false,
            AllowInEngineBitrateFallback: ShouldUseInEngineQualityFallback(request.Payload),
            CancellationToken: cancellationToken));
    }

    private async Task HandleTrackDownloadFailureAsync(
        TrackPayloadProcessingRequest request,
        TrackDownloadResult result,
        CancellationToken cancellationToken)
    {
        var message = string.IsNullOrWhiteSpace(result.Error?.Message) ? "Download failed" : result.Error.Message;
        if (await TryFallbackAsync(request.QueueUuid, request.QueueItem.Engine, request.Payload, cancellationToken))
        {
            return;
        }

        await FailQueueWithRetryAsync(request.QueueUuid, message, message, notify: false, cancellationToken);
    }

    private async Task FinalizeSuccessfulTrackDownloadAsync(
        TrackPayloadProcessingRequest request,
        CoreTrack track,
        TrackDownloadResult result,
        CancellationToken cancellationToken)
    {
        await SyncEffectiveQualityAsync(request.QueueUuid, request.Payload, track.Bitrate, cancellationToken);
        await UpdateTrackPayloadFilesAsync(request, track, result, cancellationToken);
        await UpdateCompletedTrackQueueStateAsync(request, result.Path!, cancellationToken);
    }

    private async Task UpdateTrackPayloadFilesAsync(
        TrackPayloadProcessingRequest request,
        CoreTrack track,
        TrackDownloadResult result,
        CancellationToken cancellationToken)
    {
        if (result.GeneratedPathResult == null)
        {
            UpdatePayloadFiles(request.Payload, result.Path!);
            return;
        }

        var trackContext = new EngineAudioPostDownloadHelper.EngineTrackContext(
            track,
            result.GeneratedPathResult,
            DownloadPathResolver.ResolveIoPath(result.GeneratedPathResult.FilePath),
            $"literal:{result.GeneratedPathResult.Filename}");
        var expectedOutputPath = DownloadPathResolver.ResolveIoPath(result.Path!);
        await EngineAudioPostDownloadHelper.QueueParallelPostDownloadPrefetchAsync(
            new EngineAudioPostDownloadHelper.PrefetchRequest(
                request.QueueUuid,
                trackContext,
                request.Payload,
                request.Settings,
                expectedOutputPath,
                _postDownloadTaskScheduler,
                _lyricsService,
                _listener,
                _activityLog,
                _logger,
                EngineName),
            cancellationToken);

        await LogTrackPrefetchFailureIfAnyAsync(request.QueueUuid, cancellationToken);
        EngineAudioPostDownloadHelper.UpdateAudioPayloadFiles(request.Payload, result.GeneratedPathResult, result.Path!);
    }

    private async Task LogTrackPrefetchFailureIfAnyAsync(string queueUuid, CancellationToken cancellationToken)
    {
        var prefetchFailure = await EngineAudioPostDownloadHelper.EnsureArtworkPrefetchCompletedAsync(queueUuid, cancellationToken);
        if (string.IsNullOrWhiteSpace(prefetchFailure))
        {
            return;
        }

        _logger.LogWarning(
            "Deezer sidecar prefetch failed for {QueueUuid}: {Reason}",
            queueUuid,
            prefetchFailure);
        _activityLog.Warn($"Sidecar prefetch failed (engine={EngineName}): {queueUuid} {prefetchFailure}");
    }

    private async Task UpdateCompletedTrackQueueStateAsync(
        TrackPayloadProcessingRequest request,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var sizeMb = QueueHelperUtils.TryGetFileSizeMb(outputPath);
        await QueueHelperUtils.UpdateFinalDestinationPayloadAsync(
            new QueueHelperUtils.UpdateFinalDestinationPayloadRequest<DeezerQueueItem>(
                _queueRepository,
                request.QueueUuid,
                request.Payload,
                outputPath,
                sizeMb,
                request.Payload.Size,
                request.Payload.Files,
                new QueueHelperUtils.FinalDestinationMutators<DeezerQueueItem>(
                    item => item.FinalDestinations,
                    (item, value) => item.FinalDestinations = value,
                    new QueueHelperUtils.PayloadUpdateMutators<DeezerQueueItem>(
                        (item, value) => item.FilePath = value,
                        (item, value) => item.TotalSize = value,
                        (item, value) => item.Progress = value,
                        (item, value) => item.Downloaded = value))),
            cancellationToken);

        await _queueRepository.UpdateStatusAsync(request.QueueUuid, CompletedStatus, cancellationToken: cancellationToken);
        await request.Context.UpdateWatchlistTrackStatusAsync(request.PayloadJson, CompletedStatus, cancellationToken);
    }

    private async Task FailQueueAsync(
        string queueUuid,
        string message,
        bool notify,
        CancellationToken cancellationToken)
    {
        await _queueRepository.UpdateStatusAsync(queueUuid, FailedStatus, message, cancellationToken: cancellationToken);
        if (notify)
        {
            _listener.Send(UpdateQueueEvent, new { uuid = queueUuid, status = FailedStatus, failed = true, error = message });
        }
    }

    private async Task FailQueueWithRetryAsync(
        string queueUuid,
        string message,
        string retryReason,
        bool notify,
        CancellationToken cancellationToken)
    {
        await FailQueueAsync(queueUuid, message, notify, cancellationToken);
        _retryScheduler.ScheduleRetry(queueUuid, EngineName, retryReason);
    }

    private async Task<bool> TryFallbackAsync(string queueUuid, string engine, DeezerQueueItem payload, CancellationToken cancellationToken)
    {
        try
        {
            var advanced = await _fallbackCoordinator.TryAdvanceAsync(queueUuid, engine, payload, cancellationToken);
            if (advanced)
            {
                _activityLog.Info($"Fallback advanced: {queueUuid} -> {payload.Engine} (auto_index={payload.AutoIndex})");
                if (!payload.FallbackQueuedExternally)
                {
                    _listener.SendAddedToQueue(payload.ToQueuePayload());
                }
            }
            return advanced;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Fallback advance failed for {QueueUuid}", queueUuid);
            return false;
        }
    }

    private static bool IsEpisodePayload(DeezerQueueItem payload)
    {
        if (string.Equals(payload.ContentType, DownloadContentTypes.Podcast, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(payload.CollectionType, EpisodeCollectionType, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(payload.SourceUrl)
               && payload.SourceUrl.Contains("/episode/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldUseInEngineQualityFallback(DeezerQueueItem payload)
    {
        // Preserve global AUTO fallback order for multi-engine plans.
        return EngineFallbackPlanPolicy.ShouldUseInEngineFallback(payload, EngineName);
    }

    private async Task<CoreTrack?> BuildTrackAsync(DeezerQueueItem payload, string? downloadTagSource)
    {
        if (string.IsNullOrWhiteSpace(payload.DeezerId))
        {
            return null;
        }

        var apiTrack = await _deezerClient.GetTrackAsync(payload.DeezerId);
        if (apiTrack == null)
        {
            return null;
        }

        var track = new CoreTrack
        {
            Id = apiTrack.Id.ToString()
        };
        track.ParseTrack(apiTrack);
        await track.ParseData(_deezerClient, track.Id, apiTrack, apiTrack.Album, null, true);
        ApplyTrackUrlsFromPayload(track, payload);

        if (ShouldPreferPayloadMetadata(downloadTagSource))
        {
            ApplyPayloadMetadataOverrides(track, payload);
        }

        return track;
    }

    private static void ApplyTrackUrlsFromPayload(CoreTrack track, DeezerQueueItem payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.DeezerId))
        {
            track.Urls["deezer_track_id"] = payload.DeezerId;
            track.Urls[DeezerSource] = $"https://www.deezer.com/track/{payload.DeezerId}";
        }

        if (!string.IsNullOrWhiteSpace(payload.AppleId))
        {
            track.Urls["apple_track_id"] = payload.AppleId;
            track.Urls["apple_id"] = payload.AppleId;
            track.Urls["apple"] = $"https://music.apple.com/us/song/{payload.AppleId}?i={payload.AppleId}";
        }
        if (!string.IsNullOrWhiteSpace(payload.SourceUrl))
        {
            track.Urls["source_url"] = payload.SourceUrl;
        }

        if (!string.IsNullOrWhiteSpace(payload.Url))
        {
            track.Urls["source_url_fallback"] = payload.Url;
        }
    }

    private static bool ShouldPreferPayloadMetadata(string? downloadTagSource)
    {
        return string.Equals(downloadTagSource?.Trim(), SpotifySource, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveTagSource(string? payloadSourceService, string? resolvedDownloadTagSource)
    {
        return DownloadTagSourceHelper.NormalizeResolvedDownloadTagSource(resolvedDownloadTagSource)
            ?? DownloadTagSourceHelper.NormalizeResolvedDownloadTagSource(payloadSourceService)
            ?? DeezerSource;
    }

    private static string ResolveTagSourceId(DeezerQueueItem payload, string resolvedTagSource)
    {
        if (string.Equals(resolvedTagSource, SpotifySource, StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(payload.SpotifyId)
                ? payload.SpotifyId
                : string.Empty;
        }

        return payload.DeezerId;
    }

    private static void ApplyPayloadMetadataOverrides(CoreTrack track, DeezerQueueItem payload)
    {
        ApplyTrackTitleOverride(track, payload);
        var primaryArtistName = ResolvePrimaryArtistName(payload, track);
        ApplyPrimaryArtist(track, primaryArtistName);
        var albumArtistName = ResolveAlbumArtistName(payload, primaryArtistName);
        EnsureAlbum(track, payload, albumArtistName);
        ApplyAlbumArtist(track, albumArtistName);
        ApplyTrackMetadataOverrides(track, payload);
        ApplyReleaseDateOverride(track, payload.ReleaseDate);
        ApplyContributorOverrides(track, payload);
        ApplyAlbumMetadataOverrides(track.Album, payload);
        ApplyPayloadSourceUrls(track, payload);
    }

    private static void ApplyTrackTitleOverride(CoreTrack track, DeezerQueueItem payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.Title))
        {
            track.Title = payload.Title;
        }
    }

    private static void ApplyTrackMetadataOverrides(CoreTrack track, DeezerQueueItem payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.Isrc))
        {
            track.ISRC = payload.Isrc;
        }

        if (payload.TrackNumber > 0)
        {
            track.TrackNumber = payload.TrackNumber;
        }

        if (payload.DiscNumber > 0)
        {
            track.DiscNumber = payload.DiscNumber;
        }

        if (payload.Position > 0 && (!track.Position.HasValue || track.Position.Value <= 0))
        {
            track.Position = payload.Position;
        }

        if (payload.Explicit.HasValue)
        {
            track.Explicit = payload.Explicit.Value;
        }

        if (!string.IsNullOrWhiteSpace(payload.Copyright))
        {
            track.Copyright = payload.Copyright;
        }
    }

    private static void ApplyReleaseDateOverride(CoreTrack track, string? releaseDate)
    {
        if (string.IsNullOrWhiteSpace(releaseDate))
        {
            return;
        }

        var parsedDate = CustomDate.FromString(releaseDate);
        if (string.IsNullOrWhiteSpace(parsedDate.Year))
        {
            return;
        }

        track.Date = parsedDate;
        if (track.Album != null)
        {
            track.Album.Date = parsedDate;
        }
    }

    private static void ApplyContributorOverrides(CoreTrack track, DeezerQueueItem payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.Composer))
        {
            track.Contributors["composer"] = new List<string> { payload.Composer };
        }
    }

    private static void ApplyAlbumMetadataOverrides(CoreAlbum? album, DeezerQueueItem payload)
    {
        if (album == null)
        {
            return;
        }

        if (payload.TrackTotal > 0)
        {
            album.TrackTotal = payload.TrackTotal;
        }

        if (payload.DiscTotal > 0)
        {
            album.DiscTotal = payload.DiscTotal;
        }

        if (!string.IsNullOrWhiteSpace(payload.Label))
        {
            album.Label = payload.Label;
        }

        if (!string.IsNullOrWhiteSpace(payload.Barcode))
        {
            album.Barcode = payload.Barcode;
        }

        if (payload.Genres.Count > 0)
        {
            album.Genre = payload.Genres
                .Where(static genre => !string.IsNullOrWhiteSpace(genre))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (!string.IsNullOrWhiteSpace(payload.Copyright))
        {
            album.Copyright = payload.Copyright;
        }

        if (payload.Explicit.HasValue)
        {
            album.Explicit = payload.Explicit.Value;
        }
    }

    private static void ApplyPayloadSourceUrls(CoreTrack track, DeezerQueueItem payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.SourceUrl))
        {
            track.DownloadURL = payload.SourceUrl;
            track.Urls["source_url"] = payload.SourceUrl;
            return;
        }

        if (!string.IsNullOrWhiteSpace(payload.Url))
        {
            track.Urls["source_url_fallback"] = payload.Url;
        }
    }

    private static string ResolvePrimaryArtistName(DeezerQueueItem payload, CoreTrack track)
    {
        if (!string.IsNullOrWhiteSpace(payload.Artist))
        {
            return payload.Artist;
        }

        if (!string.IsNullOrWhiteSpace(track.MainArtist?.Name))
        {
            return track.MainArtist.Name;
        }

        return "Unknown Artist";
    }

    private static string ResolveAlbumArtistName(DeezerQueueItem payload, string primaryArtistName)
    {
        return string.IsNullOrWhiteSpace(payload.AlbumArtist)
            ? primaryArtistName
            : payload.AlbumArtist;
    }

    private static void ApplyPrimaryArtist(CoreTrack track, string primaryArtistName)
    {
        track.MainArtist ??= new CoreArtist("0", primaryArtistName);
        track.MainArtist.Name = primaryArtistName;

        if (!track.Artist.TryGetValue("Main", out var mainArtists))
        {
            mainArtists = new List<string>();
            track.Artist["Main"] = mainArtists;
        }

        mainArtists.Clear();
        mainArtists.Add(primaryArtistName);

        track.Artists = new List<string> { primaryArtistName };
    }

    private static void EnsureAlbum(CoreTrack track, DeezerQueueItem payload, string albumArtistName)
    {
        if (track.Album == null)
        {
            var albumTitle = string.IsNullOrWhiteSpace(payload.Album) ? "Unknown Album" : payload.Album;
            track.Album = new CoreAlbum(payload.DeezerAlbumId ?? "0", albumTitle)
            {
                MainArtist = new CoreArtist("0", albumArtistName),
                RootArtist = new CoreArtist("0", albumArtistName)
            };
        }
        else if (!string.IsNullOrWhiteSpace(payload.Album))
        {
            track.Album.Title = payload.Album;
        }
    }

    private static void ApplyAlbumArtist(CoreTrack track, string albumArtistName)
    {
        if (track.Album == null)
        {
            return;
        }

        track.Album.MainArtist ??= new CoreArtist("0", albumArtistName);
        track.Album.RootArtist ??= new CoreArtist("0", albumArtistName);
        track.Album.MainArtist.Name = albumArtistName;
        track.Album.RootArtist.Name = albumArtistName;

        if (!track.Album.Artist.TryGetValue("Main", out var mainArtists))
        {
            mainArtists = new List<string>();
            track.Album.Artist["Main"] = mainArtists;
        }

        mainArtists.Clear();
        mainArtists.Add(albumArtistName);
        track.Album.Artists = new List<string> { albumArtistName };
    }

    private async Task<bool> ProcessEpisodeAsync(
        DeezerQueueItem payload,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        string queueUuid,
        CancellationToken cancellationToken)
    {
        var episodeId = ResolveEpisodeId(payload);
        var trackApi = await FetchEpisodeMetadataAsync(episodeId, cancellationToken);
        var streamUrl = await ResolveEpisodeStreamUrlForPayloadAsync(payload, trackApi, episodeId, cancellationToken);
        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            await FailQueueAsync(queueUuid, "Episode stream URL missing", notify: true, cancellationToken);
            return false;
        }

        var destinationRoot = await ResolveEpisodeDestinationRootForPayloadAsync(payload, settings, queueUuid, cancellationToken);
        if (string.IsNullOrWhiteSpace(destinationRoot))
        {
            return false;
        }

        var outputPath = await DownloadEpisodeFileAsync(
            BuildEpisodeTrackFromApi(trackApi, payload, settings),
            episodeId,
            streamUrl,
            destinationRoot,
            settings,
            queueUuid,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return false;
        }

        await FinalizeEpisodePayloadAsync(queueUuid, payload, outputPath, cancellationToken);
        return true;
    }

    private static string? ResolveEpisodeId(DeezerQueueItem payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.DeezerId))
        {
            return payload.DeezerId;
        }

        return ExtractEpisodeId(payload.SourceUrl);
    }

    private async Task<string?> ResolveEpisodeStreamUrlForPayloadAsync(
        DeezerQueueItem payload,
        Dictionary<string, object> trackApi,
        string? episodeId,
        CancellationToken cancellationToken)
    {
        var streamUrl = IsUsableEpisodeStreamUrl(payload.SourceUrl)
            ? payload.SourceUrl
            : ResolveEpisodeStreamUrlFromMetadata(trackApi);
        if (!string.IsNullOrWhiteSpace(streamUrl) && !IsDeezerEpisodePage(streamUrl))
        {
            return streamUrl;
        }

        if (string.IsNullOrWhiteSpace(episodeId))
        {
            return null;
        }

        var showId = ResolveEpisodeShowId(trackApi, payload);
        if (trackApi.Count > 0)
        {
            if (!string.IsNullOrWhiteSpace(showId))
            {
                // Metadata was already fetched from gateway in this execution path.
                // Skip a duplicate gateway episode request and resolve directly via show page.
                return await ResolveEpisodeStreamUrlFromShowAsync(showId, episodeId);
            }

            var gatewayStream = await ResolveEpisodeStreamUrlFromGatewayAsync(episodeId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(gatewayStream))
            {
                return gatewayStream;
            }

            return null;
        }

        return await ResolveEpisodeStreamUrlAsync(episodeId, showId, cancellationToken);
    }

    private async Task<string?> ResolveEpisodeDestinationRootForPayloadAsync(
        DeezerQueueItem payload,
        DeezSpoTagSettings settings,
        string queueUuid,
        CancellationToken cancellationToken)
    {
        var destinationRoot = await ResolveEpisodeDestinationRootAsync(payload.DestinationFolderId, cancellationToken);
        if (payload.DestinationFolderId.HasValue && string.IsNullOrWhiteSpace(destinationRoot))
        {
            await FailQueueAsync(queueUuid, "Destination folder not found or disabled for podcast download", notify: true, cancellationToken);
            return null;
        }

        destinationRoot ??= settings.Podcast?.DownloadLocation;
        destinationRoot ??= settings.DownloadLocation;
        destinationRoot = DownloadPathResolver.ResolveIoPath(destinationRoot ?? string.Empty);
        destinationRoot = string.IsNullOrWhiteSpace(destinationRoot) ? "." : destinationRoot;
        Directory.CreateDirectory(destinationRoot);
        return destinationRoot;
    }

    private async Task<string?> DownloadEpisodeFileAsync(
        CoreTrack track,
        string? episodeId,
        string streamUrl,
        string destinationRoot,
        DeezSpoTagSettings settings,
        string queueUuid,
        CancellationToken cancellationToken)
    {
        var httpClientFactory = _serviceProvider.GetRequiredService<IHttpClientFactory>();
        var downloadClient = httpClientFactory.CreateClient("DeezSpoTagDownload");
        using var response = await OpenEpisodeInitialResponseAsync(
            downloadClient,
            streamUrl,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var finalStreamUrl = response.RequestMessage?.RequestUri?.ToString() ?? streamUrl;
        var preferredStreamUrl = finalStreamUrl;
        var extension = GetEpisodeExtension(response.Content.Headers.ContentType?.MediaType, streamUrl, finalStreamUrl);
        var outputPath = await ResolveEpisodeOutputPathAsync(
            track,
            episodeId,
            destinationRoot,
            extension,
            settings,
            cancellationToken);

        var progressReporter = QueueHelperUtils.CreateProgressReporter(
            _queueRepository,
            _listener,
            queueUuid,
            _logger,
            "Failed to update Deezer progress for {QueueUuid}",
            cancellationToken);

        var totalBytes = response.Content.Headers.ContentLength.GetValueOrDefault();
        var rangeSupported = IsEpisodeParallelRangeSupported(response, totalBytes);
        if (rangeSupported
            && await TryDownloadEpisodeWithParallelRangesAsync(
                downloadClient,
                preferredStreamUrl,
                outputPath,
                totalBytes,
                progressReporter,
                queueUuid,
                cancellationToken)
            && IsNonEmptyFile(outputPath))
        {
            return outputPath;
        }

        var reuseInitialResponse = ShouldReuseInitialEpisodeResponse(rangeSupported, preferredStreamUrl, finalStreamUrl);
        if (!reuseInitialResponse)
        {
            response.Dispose();
        }

        using var fallbackResponse = reuseInitialResponse
            ? response
            : await OpenEpisodeResponseWithHostFallbackAsync(
                downloadClient,
                preferredStreamUrl,
                finalStreamUrl,
                cancellationToken);
        fallbackResponse.EnsureSuccessStatusCode();
        await CopyEpisodeResponseToFileAsync(fallbackResponse, outputPath, progressReporter, cancellationToken);

        if (IsNonEmptyFile(outputPath))
        {
            return outputPath;
        }

        await FailQueueAsync(queueUuid, "Episode download failed", notify: true, cancellationToken);
        return null;
    }

    private static bool ShouldReuseInitialEpisodeResponse(bool rangeSupported, string preferredStreamUrl, string finalStreamUrl) =>
        !rangeSupported && string.Equals(preferredStreamUrl, finalStreamUrl, StringComparison.OrdinalIgnoreCase);

    private static bool IsNonEmptyFile(string outputPath) =>
        File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;

    private static async Task CopyEpisodeResponseToFileAsync(
        HttpResponseMessage response,
        string outputPath,
        Func<double, double, Task> progressReporter,
        CancellationToken cancellationToken)
    {
        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var fileStream = new FileStream(
            outputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            EpisodeStreamBufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        await progressReporter(0, 0);
        var fallbackTotalBytes = response.Content.Headers.ContentLength.GetValueOrDefault();
        var totalRead = 0L;
        var speedWindowBytes = 0L;
        var lastReportAt = DateTimeOffset.UtcNow;

        var buffer = ArrayPool<byte>.Shared.Rent(EpisodeStreamBufferBytes);
        try
        {
            while (true)
            {
                var readTask = contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).AsTask();
                var read = await readTask.WaitAsync(EpisodeReadStallTimeout, cancellationToken);
                if (read <= 0)
                {
                    break;
                }

                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                totalRead += read;
                speedWindowBytes += read;
                if (fallbackTotalBytes > 0)
                {
                    var progressState = await ReportEpisodeCopyProgressAsync(
                        progressReporter,
                        fallbackTotalBytes,
                        totalRead,
                        speedWindowBytes,
                        lastReportAt);
                    speedWindowBytes = progressState.SpeedWindowBytes;
                    lastReportAt = progressState.LastReportAt;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (totalRead > 0)
        {
            await progressReporter(100, 0);
        }
    }

    private static async Task<(long SpeedWindowBytes, DateTimeOffset LastReportAt)> ReportEpisodeCopyProgressAsync(
        Func<double, double, Task> progressReporter,
        long fallbackTotalBytes,
        long totalRead,
        long speedWindowBytes,
        DateTimeOffset lastReportAt)
    {
        var now = DateTimeOffset.UtcNow;
        if (totalRead != fallbackTotalBytes && (now - lastReportAt).TotalSeconds < 1)
        {
            return (speedWindowBytes, lastReportAt);
        }

        var elapsedSeconds = Math.Max((now - lastReportAt).TotalSeconds, 0.001d);
        var speedMbps = ((speedWindowBytes * 8d) / 1024d / 1024d) / elapsedSeconds;
        var progress = totalRead * 100d / fallbackTotalBytes;
        await progressReporter(progress, speedMbps);
        return (0, now);
    }

    private async Task<HttpResponseMessage> OpenEpisodeInitialResponseAsync(
        HttpClient client,
        string streamUrl,
        CancellationToken cancellationToken)
    {
        var preferredUrl = await ResolvePreferredEpisodeStreamUrlAsync(client, streamUrl, cancellationToken);
        if (string.Equals(preferredUrl, streamUrl, StringComparison.OrdinalIgnoreCase))
        {
            return await SendEpisodeRequestAsync(client, streamUrl, cancellationToken);
        }

        try
        {
            var preferredResponse = await SendEpisodeRequestAsync(client, preferredUrl, cancellationToken);
            if (preferredResponse.IsSuccessStatusCode)
            {
                return preferredResponse;
            }

            preferredResponse.Dispose();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Preferred HearThis download URL failed, falling back to gateway stream URL.");
            }
        }

        return await SendEpisodeRequestAsync(client, streamUrl, cancellationToken);
    }

    private async Task<string> ResolvePreferredEpisodeStreamUrlAsync(
        HttpClient client,
        string streamUrl,
        CancellationToken cancellationToken)
    {
        var hearThisDownloadUrl = await TryResolveHearThisDownloadUrlAsync(client, streamUrl, cancellationToken);
        if (!string.IsNullOrWhiteSpace(hearThisDownloadUrl))
        {
            return hearThisDownloadUrl;
        }

        return streamUrl;
    }

    private async Task<string?> TryResolveHearThisDownloadUrlAsync(
        HttpClient client,
        string streamUrl,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(streamUrl)
            || !Uri.TryCreate(streamUrl, UriKind.Absolute, out var streamUri)
            || !streamUri.Host.EndsWith(".hearthis.at", StringComparison.OrdinalIgnoreCase)
            || !IsHearThisTrackStreamPath(streamUri.AbsolutePath))
        {
            return null;
        }

        if (streamUri.AbsolutePath.Contains(HearThisDownloadPathSegment, StringComparison.OrdinalIgnoreCase))
        {
            return streamUrl;
        }

        var trackPageUri = BuildHearThisTrackPageUri(streamUri);
        if (trackPageUri == null)
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, trackPageUri);
            request.Headers.Referrer = trackPageUri;
            request.Headers.TryAddWithoutValidation("User-Agent", HearThisBrowserUserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            var downloadUrl = ExtractHearThisDownloadUrl(html, trackPageUri);
            if (!string.IsNullOrWhiteSpace(downloadUrl))
            {
                return downloadUrl;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to resolve HearThis download URL from episode stream page.");
            }
        }

        return null;
    }

    private static Uri? BuildHearThisTrackPageUri(Uri streamUri)
    {
        if (streamUri.AbsolutePath.Contains(HearThisDownloadPathSegment, StringComparison.OrdinalIgnoreCase))
        {
            var downloadIndex = streamUri.AbsolutePath.LastIndexOf(HearThisDownloadPathSegment, StringComparison.OrdinalIgnoreCase);
            if (downloadIndex <= 0)
            {
                return null;
            }

            var downloadTrackPath = streamUri.AbsolutePath[..downloadIndex];
            if (string.IsNullOrWhiteSpace(downloadTrackPath))
            {
                return null;
            }

            var downloadBuilder = new UriBuilder(streamUri)
            {
                Path = downloadTrackPath,
                Query = string.Empty
            };
            return downloadBuilder.Uri;
        }

        var marker = ResolveHearThisStreamPathMarker(streamUri.AbsolutePath);
        if (marker == null)
        {
            return null;
        }

        var markerIndex = streamUri.AbsolutePath.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex <= 0)
        {
            return null;
        }

        var trackPath = streamUri.AbsolutePath[..markerIndex];
        if (string.IsNullOrWhiteSpace(trackPath))
        {
            return null;
        }

        var builder = new UriBuilder(streamUri)
        {
            Path = trackPath,
            Query = string.Empty
        };
        return builder.Uri;
    }

    private static bool IsHearThisTrackStreamPath(string absolutePath)
    {
        return absolutePath.EndsWith(HearThisListenPathSegment, StringComparison.OrdinalIgnoreCase)
               || absolutePath.EndsWith(HearThisStreamPathSegment, StringComparison.OrdinalIgnoreCase)
               || absolutePath.Contains(HearThisDownloadPathSegment, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveHearThisStreamPathMarker(string absolutePath)
    {
        if (absolutePath.EndsWith(HearThisListenPathSegment, StringComparison.OrdinalIgnoreCase))
        {
            return HearThisListenPathSegment;
        }

        if (absolutePath.EndsWith(HearThisStreamPathSegment, StringComparison.OrdinalIgnoreCase))
        {
            return HearThisStreamPathSegment;
        }

        return null;
    }

    private static async Task<HttpResponseMessage> SendEpisodeRequestAsync(
        HttpClient client,
        string url,
        CancellationToken cancellationToken)
    {
        using var request = CreateEpisodeRequest(url, range: null);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static HttpRequestMessage CreateEpisodeRequest(string url, RangeHeaderValue? range)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (range != null)
        {
            request.Headers.Range = range;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !uri.Host.EndsWith(".hearthis.at", StringComparison.OrdinalIgnoreCase))
        {
            return request;
        }

        request.Headers.TryAddWithoutValidation("User-Agent", HearThisBrowserUserAgent);
        if (uri.AbsolutePath.Contains(HearThisDownloadPathSegment, StringComparison.OrdinalIgnoreCase))
        {
            var referer = BuildHearThisTrackPageUri(uri);
            if (referer != null)
            {
                request.Headers.Referrer = referer;
            }
        }

        return request;
    }

    private static string? ExtractHearThisDownloadUrl(string html, Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var hrefMatch = HearThisDownloadHrefRegex().Match(html);
        if (hrefMatch.Success)
        {
            return NormalizeHearThisUrl(hrefMatch.Groups["url"].Value, baseUri);
        }

        var escapedMatch = HearThisEscapedDownloadUrlRegex().Match(html);
        if (escapedMatch.Success)
        {
            return NormalizeHearThisUrl(escapedMatch.Value.Replace("\\/", "/", StringComparison.Ordinal), baseUri);
        }

        return null;
    }

    private static string? NormalizeHearThisUrl(string rawUrl, Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return null;
        }

        var candidate = WebUtility.HtmlDecode(rawUrl.Trim());
        if (candidate.StartsWith("//", StringComparison.Ordinal))
        {
            candidate = $"{baseUri.Scheme}:{candidate}";
        }

        if (Uri.TryCreate(candidate, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri.ToString();
        }

        if (Uri.TryCreate(baseUri, candidate, out var relativeUri))
        {
            return relativeUri.ToString();
        }

        return null;
    }

    private async Task<HttpResponseMessage> OpenEpisodeResponseWithHostFallbackAsync(
        HttpClient client,
        string preferredUrl,
        string originalUrl,
        CancellationToken cancellationToken)
    {
        if (string.Equals(preferredUrl, originalUrl, StringComparison.OrdinalIgnoreCase))
        {
            return await SendEpisodeRequestAsync(client, preferredUrl, cancellationToken);
        }

        try
        {
            var preferredResponse = await SendEpisodeRequestAsync(client, preferredUrl, cancellationToken);
            if (preferredResponse.IsSuccessStatusCode)
            {
                return preferredResponse;
            }

            preferredResponse.Dispose();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Preferred HearThis host failed, falling back to original stream URL.");
            }
        }

        return await SendEpisodeRequestAsync(client, originalUrl, cancellationToken);
    }

    private static bool IsEpisodeParallelRangeSupported(HttpResponseMessage response, long totalBytes)
    {
        if (totalBytes < EpisodeParallelRangeMinimumBytes)
        {
            return false;
        }

        return response.Headers.AcceptRanges
            .Any(range => string.Equals(range, "bytes", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<bool> TryDownloadEpisodeWithParallelRangesAsync(
        HttpClient client,
        string streamUrl,
        string outputPath,
        long totalBytes,
        Func<double, double, Task> progressReporter,
        string queueUuid,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(streamUrl, UriKind.Absolute, out _))
        {
            return false;
        }

        try
        {
            await progressReporter(0, 0);
            var ranges = BuildEpisodeRanges(totalBytes, EpisodeParallelRangeDownloads);
            await PreallocateEpisodeDownloadFileAsync(outputPath, totalBytes, cancellationToken);
            using var writeHandle = OpenEpisodeWriteHandle(outputPath);
            var counters = new EpisodeRangeTransferCounters();
            using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var progressLoop = StartEpisodeRangeProgressLoopAsync(
                progressReporter,
                totalBytes,
                counters,
                progressCts.Token);

            try
            {
                await DownloadEpisodeRangesAsync(client, streamUrl, ranges, writeHandle, counters, cancellationToken);
            }
            finally
            {
                await StopEpisodeRangeProgressLoopAsync(progressCts, progressLoop);
            }

            if (counters.TotalRead < totalBytes)
            {
                throw new IOException($"Episode range download incomplete for {queueUuid}: read={counters.TotalRead} expected={totalBytes}");
            }

            await progressReporter(100, 0);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Episode range download fallback for queue {QueueUuid}", queueUuid);
            }

            try
            {
                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }
            }
            catch
            {
                // Best-effort cleanup before fallback to single-stream mode.
            }

            return false;
        }
    }

    private static async Task PreallocateEpisodeDownloadFileAsync(string outputPath, long totalBytes, CancellationToken cancellationToken)
    {
        await using var preallocateStream = new FileStream(
            outputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            EpisodeStreamBufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        preallocateStream.SetLength(totalBytes);
        await preallocateStream.FlushAsync(cancellationToken);
    }

    private static Microsoft.Win32.SafeHandles.SafeFileHandle OpenEpisodeWriteHandle(string outputPath) =>
        File.OpenHandle(
            outputPath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.None,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static Task StartEpisodeRangeProgressLoopAsync(
        Func<double, double, Task> progressReporter,
        long totalBytes,
        EpisodeRangeTransferCounters counters,
        CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            var lastReportedProgress = 0d;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var read = Interlocked.Read(ref counters.TotalRead);
                if (read <= 0)
                {
                    continue;
                }

                var windowBytes = Interlocked.Exchange(ref counters.SpeedWindowBytes, 0);
                var progress = read * 100d / totalBytes;
                if (progress <= lastReportedProgress)
                {
                    continue;
                }

                var speedMbps = (windowBytes * 8d) / 1024d / 1024d;
                await progressReporter(progress, speedMbps);
                lastReportedProgress = progress;
            }
        }, CancellationToken.None);

    private static async Task StopEpisodeRangeProgressLoopAsync(CancellationTokenSource progressCts, Task progressLoop)
    {
        await progressCts.CancelAsync();
        try
        {
            await progressLoop;
        }
        catch (OperationCanceledException)
        {
            // Expected when shutting down progress loop.
        }
    }

    private static async Task DownloadEpisodeRangesAsync(
        HttpClient client,
        string streamUrl,
        List<(long Start, long End)> ranges,
        Microsoft.Win32.SafeHandles.SafeFileHandle writeHandle,
        EpisodeRangeTransferCounters counters,
        CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(ranges, new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Min(EpisodeParallelRangeDownloads, ranges.Count)
        }, async (range, token) =>
        {
            await DownloadEpisodeRangeAsync(client, streamUrl, range, writeHandle, counters, token);
        });
    }

    private static async Task DownloadEpisodeRangeAsync(
        HttpClient client,
        string streamUrl,
        (long Start, long End) range,
        Microsoft.Win32.SafeHandles.SafeFileHandle writeHandle,
        EpisodeRangeTransferCounters counters,
        CancellationToken cancellationToken)
    {
        using var request = CreateEpisodeRequest(streamUrl, new RangeHeaderValue(range.Start, range.End));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            throw new InvalidOperationException("Episode source ignored range request.");
        }

        response.EnsureSuccessStatusCode();
        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = ArrayPool<byte>.Shared.Rent(EpisodeStreamBufferBytes);
        try
        {
            var offset = range.Start;
            while (true)
            {
                var readTask = contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).AsTask();
                var read = await readTask.WaitAsync(EpisodeReadStallTimeout, cancellationToken);
                if (read <= 0)
                {
                    break;
                }

                await RandomAccess.WriteAsync(
                    writeHandle,
                    buffer.AsMemory(0, read),
                    offset,
                    cancellationToken);
                offset += read;
                Interlocked.Add(ref counters.TotalRead, read);
                Interlocked.Add(ref counters.SpeedWindowBytes, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static List<(long Start, long End)> BuildEpisodeRanges(long totalBytes, int parallelism)
    {
        var estimatedCount = (int)Math.Ceiling(totalBytes / (double)EpisodeRangeChunkBytes);
        var rangeCount = Math.Clamp(
            estimatedCount,
            Math.Max(1, parallelism),
            EpisodeRangeHardLimit);
        var ranges = new List<(long Start, long End)>(rangeCount);
        var chunkSize = (long)Math.Ceiling(totalBytes / (double)rangeCount);
        var start = 0L;
        for (var index = 0; index < rangeCount; index++)
        {
            var end = Math.Min(totalBytes - 1, start + chunkSize - 1);
            ranges.Add((start, end));
            if (end >= totalBytes - 1)
            {
                break;
            }

            start = end + 1;
        }

        return ranges;
    }

    private async Task<string> ResolveEpisodeOutputPathAsync(
        CoreTrack track,
        string? episodeId,
        string destinationRoot,
        string extension,
        DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var originalDownloadLocation = settings.DownloadLocation;
        settings.DownloadLocation = destinationRoot;
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var pathProcessor = scope.ServiceProvider.GetRequiredService<EnhancedPathTemplateProcessor>();
            var pathResult = pathProcessor.GeneratePaths(track, EpisodeCollectionType, settings);
            var outputDirectory = Path.GetFullPath(pathResult.FilePath);
            Directory.CreateDirectory(outputDirectory);
            return Path.Join(outputDirectory, EnsureEpisodeFilenameExtension(pathResult.Filename, extension));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to generate templated podcast path; falling back to flat output for episode {EpisodeId}", episodeId);
            Directory.CreateDirectory(destinationRoot);
            var fallbackFilename = BuildEpisodeFilename(track, episodeId, settings.IllegalCharacterReplacer, extension);
            return Path.Join(destinationRoot, fallbackFilename);
        }
        finally
        {
            settings.DownloadLocation = originalDownloadLocation;
        }
    }

    private async Task FinalizeEpisodePayloadAsync(
        string queueUuid,
        DeezerQueueItem payload,
        string outputPath,
        CancellationToken cancellationToken)
    {
        UpdatePayloadFiles(payload, outputPath);
        var sizeMb = QueueHelperUtils.TryGetFileSizeMb(outputPath);
        await QueueHelperUtils.UpdateFinalDestinationPayloadAsync(
            new QueueHelperUtils.UpdateFinalDestinationPayloadRequest<DeezerQueueItem>(
                _queueRepository,
                queueUuid,
                payload,
                outputPath,
                sizeMb,
                payload.Size,
                payload.Files,
                new QueueHelperUtils.FinalDestinationMutators<DeezerQueueItem>(
                    item => item.FinalDestinations,
                    (item, value) => item.FinalDestinations = value,
                    new QueueHelperUtils.PayloadUpdateMutators<DeezerQueueItem>(
                        (item, value) => item.FilePath = value,
                        (item, value) => item.TotalSize = value,
                        (item, value) => item.Progress = value,
                        (item, value) => item.Downloaded = value))),
            cancellationToken);
        await _queueRepository.UpdateStatusAsync(queueUuid, CompletedStatus, downloaded: 1, progress: 100, cancellationToken: cancellationToken);
    }

    private static string? ExtractEpisodeId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var parts = url.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var index = Array.FindIndex(parts, part => string.Equals(part, EpisodeCollectionType, StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && index + 1 < parts.Length)
        {
            return parts[index + 1];
        }

        return null;
    }

    private async Task<string?> ResolveEpisodeStreamUrlAsync(string? episodeId, string? showId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(episodeId))
        {
            return null;
        }

        var effectiveShowId = showId;
        var gatewayEpisodeStream = await ResolveEpisodeStreamUrlFromGatewayAsync(episodeId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(gatewayEpisodeStream))
        {
            return gatewayEpisodeStream;
        }

        return await ResolveEpisodeStreamUrlFromShowAsync(effectiveShowId, episodeId);
    }

    private Task<string?> ResolveEpisodeStreamUrlFromShowAsync(string? showId, string episodeId)
        => DeezerDownloadSharedHelpers.ResolveEpisodeStreamUrlFromShowAsync(
            _serviceProvider,
            showId,
            episodeId,
            (ex, id) =>
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "Failed to resolve episode stream URL via show page for {EpisodeId}", id);
                }
            });

    private async Task<string?> ResolveEpisodeStreamUrlFromGatewayAsync(string episodeId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var episode = await GetEpisodeGatewayObjectAsync(episodeId);
            var streamUrl = episode?.Value<string>("EPISODE_DIRECT_STREAM_URL")
                            ?? episode?.Value<string>("EPISODE_URL");
            return IsDeezerEpisodePage(streamUrl ?? string.Empty) ? null : streamUrl;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to resolve episode stream URL via gateway for {EpisodeId}", episodeId);            }
            return null;
        }
    }

    private async Task<Dictionary<string, object>> FetchEpisodeMetadataAsync(string? episodeId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(episodeId))
        {
            return new Dictionary<string, object>();
        }

        try
        {
            var episode = await GetEpisodeGatewayObjectAsync(episodeId);
            if (episode == null)
            {
                return new Dictionary<string, object>();
            }

            var gatewayMetadata = episode.ToObject<Dictionary<string, object>>() ?? new Dictionary<string, object>();
            if (gatewayMetadata.Count > 0)
            {
                return gatewayMetadata;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to resolve episode metadata via gateway for {EpisodeId}", episodeId);            }
        }

        return new Dictionary<string, object>();
    }

    private async Task<Newtonsoft.Json.Linq.JObject?> GetEpisodeGatewayObjectAsync(string episodeId)
    {
        var cacheKey = episodeId.Trim();
        if (TryGetCachedEpisodeGatewayObject(cacheKey, out var cachedEpisode))
        {
            return cachedEpisode;
        }

        var gate = EpisodeGatewayCacheGates.GetOrAdd(cacheKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (TryGetCachedEpisodeGatewayObject(cacheKey, out cachedEpisode))
            {
                return cachedEpisode;
            }

            using var scope = _serviceProvider.CreateScope();
            var gatewayService = scope.ServiceProvider.GetRequiredService<DeezerGatewayService>();
            var page = await gatewayService.GetEpisodePageAsync(cacheKey).ConfigureAwait(false);
            var results = page["results"] as Newtonsoft.Json.Linq.JObject ?? page;
            var episode = results["EPISODE"] as Newtonsoft.Json.Linq.JObject
                          ?? results[EpisodeCollectionType] as Newtonsoft.Json.Linq.JObject
                          ?? results;
            if (episode == null)
            {
                return null;
            }

            EpisodeGatewayCache[cacheKey] = new CachedEpisodeGatewayObject(
                DateTimeOffset.UtcNow,
                (Newtonsoft.Json.Linq.JObject)episode.DeepClone());
            PruneEpisodeGatewayCacheIfNeeded();

            return (Newtonsoft.Json.Linq.JObject)episode.DeepClone();
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool TryGetCachedEpisodeGatewayObject(string cacheKey, out Newtonsoft.Json.Linq.JObject? episode)
    {
        episode = null;
        if (!EpisodeGatewayCache.TryGetValue(cacheKey, out var cached))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - cached.CachedAtUtc > EpisodeGatewayCacheTtl)
        {
            EpisodeGatewayCache.TryRemove(cacheKey, out _);
            return false;
        }

        episode = (Newtonsoft.Json.Linq.JObject)cached.Payload.DeepClone();
        return true;
    }

    private static void PruneEpisodeGatewayCacheIfNeeded()
    {
        if (EpisodeGatewayCache.Count <= EpisodeGatewayCacheMaxEntries)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var entry in EpisodeGatewayCache)
        {
            if (now - entry.Value.CachedAtUtc > EpisodeGatewayCacheTtl)
            {
                EpisodeGatewayCache.TryRemove(entry.Key, out _);
            }
        }

        if (EpisodeGatewayCache.Count <= EpisodeGatewayCacheMaxEntries)
        {
            return;
        }

        var overflow = EpisodeGatewayCache.Count - EpisodeGatewayCacheMaxEntries;
        if (overflow <= 0)
        {
            return;
        }

        foreach (var entry in EpisodeGatewayCache
                     .OrderBy(item => item.Value.CachedAtUtc)
                     .Take(overflow))
        {
            EpisodeGatewayCache.TryRemove(entry.Key, out _);
        }
    }

    private static string? ResolveEpisodeStreamUrlFromMetadata(Dictionary<string, object> trackApi)
    {
        var direct = GetDictString(trackApi, "direct_stream_url")
                     ?? GetDictString(trackApi, "EPISODE_DIRECT_STREAM_URL")
                     ?? GetDictString(trackApi, "direct_url")
                     ?? GetDictString(trackApi, "url")
                     ?? GetDictString(trackApi, "episode_url")
                     ?? GetDictString(trackApi, "EPISODE_URL");

        return IsDeezerEpisodePage(direct ?? string.Empty) ? null : direct;
    }

    private static string? ResolveEpisodeShowId(Dictionary<string, object> trackApi, DeezerQueueItem payload)
    {
        var nestedShow = GetDictObject(trackApi, "show");
        var nestedShowUpper = GetDictObject(trackApi, "SHOW");
        var candidates = new[]
        {
            GetDictString(trackApi, "show_id"),
            GetDictString(trackApi, "SHOW_ID"),
            GetDictString(nestedShow, "id"),
            GetDictString(nestedShow, "SHOW_ID"),
            GetDictString(nestedShowUpper, "id"),
            payload.DeezerAlbumId,
            payload.DeezerArtistId
        };

        return candidates
            .FirstOrDefault(static candidate => !string.IsNullOrWhiteSpace(candidate) && long.TryParse(candidate, out _));
    }

    private static CoreTrack BuildEpisodeTrackFromApi(
        Dictionary<string, object> trackApi,
        DeezerQueueItem payload,
        DeezSpoTagSettings settings)
    {
        var title = ResolveFirstNonEmpty(
            GetDictString(trackApi, "title"),
            GetDictString(trackApi, "EPISODE_TITLE"),
            payload.Title,
            "Unknown Episode")!;
        var duration = GetDictInt(trackApi, "duration");
        if (duration <= 0)
        {
            duration = GetDictInt(trackApi, "DURATION");
        }

        var trackNumber = GetDictInt(trackApi, "episode_number", 1);
        if (trackNumber <= 0)
        {
            trackNumber = GetDictInt(trackApi, "EPISODE_NUMBER", 1);
        }

        var artistDict = GetDictObject(trackApi, "artist");
        var showDict = GetDictObject(trackApi, "show");
        var showDictUpper = GetDictObject(trackApi, "SHOW");
        var showId = ResolveFirstNonEmpty(
            GetDictString(trackApi, "show_id"),
            GetDictString(trackApi, "SHOW_ID"),
            GetDictString(showDict, "id"),
            GetDictString(showDictUpper, "id"),
            GetDictString(artistDict, "id"),
            payload.DeezerAlbumId,
            payload.DeezerArtistId,
            "0")!;
        var showTitle = ResolveFirstNonEmpty(
            GetDictString(trackApi, "show_title"),
            GetDictString(trackApi, "SHOW_TITLE"),
            GetDictString(trackApi, "SHOW_NAME"),
            GetDictString(showDict, "title"),
            GetDictString(showDictUpper, "title"),
            GetDictString(showDict, "name"),
            GetDictString(showDictUpper, "name"),
            GetDictString(artistDict, "name"),
            payload.Album,
            payload.Artist,
            "Unknown Show")!;
        var showArtMd5 = ResolveFirstNonEmpty(
            GetDictString(trackApi, "show_art_md5"),
            GetDictString(trackApi, "SHOW_ART_MD5"),
            GetDictString(showDict, "md5_image"),
            GetDictString(showDictUpper, "md5_image"));
        if (string.IsNullOrWhiteSpace(showArtMd5))
        {
            var showImageUrl = ResolveFirstNonEmpty(
                GetDictString(trackApi, "SHOW_PICTURE"),
                GetDictString(showDict, "picture"),
                GetDictString(showDict, "picture_big"),
                GetDictString(showDictUpper, "picture"),
                GetDictString(showDictUpper, "picture_big"));
            showArtMd5 = DeezerImageUrlParser.ExtractImageMd5(showImageUrl, "talk");
        }

        var artist = new CoreArtist(showId, showTitle, "Main");
        var album = new CoreAlbum(showId, showTitle)
        {
            MainArtist = artist,
            RootArtist = artist,
            TrackTotal = 1,
            DiscTotal = 1,
            Pic = string.IsNullOrWhiteSpace(showArtMd5) ? new Picture("", "talk") : new Picture(showArtMd5, "talk")
        };
        album.Artist["Main"] = new List<string> { showTitle };
        album.Artists = new List<string> { showTitle };

        var track = new CoreTrack
        {
            Id = GetDictString(trackApi, "id") ?? payload.DeezerId,
            Title = title,
            Duration = duration,
            MainArtist = artist,
            Album = album,
            TrackNumber = trackNumber,
            DiscNumber = 1,
            DiskNumber = 1
        };

        track.Artist["Main"] = new List<string> { showTitle };
        track.Artists = new List<string> { showTitle };
        ApplyEpisodeReleaseDate(track, trackApi);
        track.ApplySettings(settings);
        return track;
    }

    private static void ApplyEpisodeReleaseDate(CoreTrack track, Dictionary<string, object> trackApi)
    {
        var releaseValue = ResolveFirstNonEmpty(
            GetDictString(trackApi, "release_date"),
            GetDictString(trackApi, "EPISODE_PUBLISHED_TIMESTAMP"));
        if (string.IsNullOrWhiteSpace(releaseValue))
        {
            return;
        }

        if (long.TryParse(releaseValue, out var unixRaw) && unixRaw > 0)
        {
            var unixSeconds = unixRaw > 9_999_999_999 ? unixRaw / 1000 : unixRaw;
            try
            {
                var parsedUnix = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
                track.Date.Day = parsedUnix.Day.ToString("D2");
                track.Date.Month = parsedUnix.Month.ToString("D2");
                track.Date.Year = parsedUnix.Year.ToString();
                track.Date.FixDayMonth();
                return;
            }
            catch (ArgumentOutOfRangeException)
            {
                // Fall through to regular date parsing.
            }
        }

        if (!DateTimeOffset.TryParse(
            releaseValue,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
            out var parsed))
        {
            return;
        }

        var parsedUtc = parsed.UtcDateTime;
        track.Date.Day = parsedUtc.Day.ToString("D2");
        track.Date.Month = parsedUtc.Month.ToString("D2");
        track.Date.Year = parsedUtc.Year.ToString();
        track.Date.FixDayMonth();
    }

    private static int GetDictInt(Dictionary<string, object> dict, string key, int fallback = 0)
    {
        if (!dict.TryGetValue(key, out var value) || value == null)
        {
            return fallback;
        }

        return value switch
        {
            int intValue => intValue,
            long longValue => longValue is < int.MinValue or > int.MaxValue ? fallback : (int)longValue,
            JsonElement element when element.ValueKind == JsonValueKind.Number => element.TryGetInt32(out var intVal) ? intVal : fallback,
            JsonElement element when element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out var parsed) => parsed,
            _ => int.TryParse(value.ToString(), out var parsed) ? parsed : fallback
        };
    }

    private static Dictionary<string, object> GetDictObject(Dictionary<string, object> dict, string key)
        => DeezerDownloadSharedHelpers.GetDictObject(dict, key);

    private static string? GetDictString(Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var value) || value == null)
        {
            return null;
        }

        return value switch
        {
            string str => str,
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
            _ => value.ToString()
        };
    }

    private static bool IsDeezerEpisodePage(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Host.Contains("deezer.com", StringComparison.OrdinalIgnoreCase)
               && uri.AbsolutePath.Contains("/episode", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUsableEpisodeStreamUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            return false;
        }

        return !IsDeezerEpisodePage(url);
    }

    private async Task<string?> ResolveEpisodeDestinationRootAsync(long? destinationFolderId, CancellationToken cancellationToken)
    {
        if (!destinationFolderId.HasValue)
        {
            return null;
        }

        using var scope = _serviceProvider.CreateScope();
        var libraryRepository = scope.ServiceProvider.GetService<LibraryRepository>();
        if (libraryRepository == null || !libraryRepository.IsConfigured)
        {
            return null;
        }

        var folders = await libraryRepository.GetFoldersAsync(cancellationToken);
        var explicitFolder = folders.FirstOrDefault(folder => folder.Id == destinationFolderId.Value && folder.Enabled);
        return explicitFolder?.RootPath;
    }

    private static string GetEpisodeExtension(string? contentType, string streamUrl, string? finalStreamUrl = null)
    {
        return ResolveExtensionFromContentType(contentType)
               ?? ResolveExtensionFromUrl(finalStreamUrl)
               ?? ResolveExtensionFromUrl(streamUrl)
               ?? ".mp3";
    }

    private static string? ResolveExtensionFromContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        if (contentType.Contains("audio/mp4", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("audio/m4a", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("audio/aac", StringComparison.OrdinalIgnoreCase))
        {
            return ".m4a";
        }

        if (contentType.Contains("audio/mpeg", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("audio/mp3", StringComparison.OrdinalIgnoreCase))
        {
            return ".mp3";
        }

        return null;
    }

    private static string? ResolveExtensionFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (url.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase)
            || url.Contains(".m4a?", StringComparison.OrdinalIgnoreCase))
        {
            return ".m4a";
        }

        if (url.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
            || url.Contains(".mp3?", StringComparison.OrdinalIgnoreCase))
        {
            return ".mp3";
        }

        return null;
    }

    private static string BuildEpisodeFilename(CoreTrack track, string? episodeId, string? illegalCharacterReplacer, string extension)
    {
        var replacement = string.IsNullOrWhiteSpace(illegalCharacterReplacer) ? "_" : illegalCharacterReplacer;
        var fallback = string.IsNullOrWhiteSpace(episodeId) ? EpisodeCollectionType : $"{EpisodeCollectionType}-{episodeId}";
        var baseName = string.IsNullOrWhiteSpace(track.Title) ? fallback : track.Title;
        var sanitized = CjkFilenameSanitizer.SanitizeSegment(
            baseName,
            fallback: fallback,
            replacement: replacement,
            collapseWhitespace: true,
            trimTrailingDotsAndSpaces: true,
            maxLength: 180);

        return sanitized.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? sanitized
            : $"{sanitized}{extension}";
    }

    private static string EnsureEpisodeFilenameExtension(string filename, string extension)
    {
        if (string.IsNullOrWhiteSpace(filename))
        {
            return $"episode{extension}";
        }

        return filename.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? filename
            : $"{filename}{extension}";
    }

    private static string? ResolveFirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private async Task SyncEffectiveQualityAsync(
        string queueUuid,
        DeezerQueueItem payload,
        int effectiveBitrate,
        CancellationToken cancellationToken)
    {
        if (effectiveBitrate <= 0)
        {
            return;
        }

        var effectiveQuality = effectiveBitrate.ToString();
        var bitrateChanged = payload.Bitrate != effectiveBitrate;
        if (!bitrateChanged
            && string.Equals(payload.Quality, effectiveQuality, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        payload.Bitrate = effectiveBitrate;
        var qualitySynced = await EngineQueueQualitySyncHelper.SyncQualityAsync(
            _queueRepository,
            _listener,
            queueUuid,
            payload,
            effectiveQuality,
            cancellationToken);
        if (!qualitySynced)
        {
            await QueueHelperUtils.UpdatePayloadAsync(_queueRepository, queueUuid, payload, cancellationToken);
        }
    }

    private static int ResolveRequestedBitrate(DeezerQueueItem payload)
    {
        if (payload.Bitrate > 0)
        {
            return payload.Bitrate;
        }

        return int.TryParse(payload.Quality, out var parsedQuality) && parsedQuality > 0
            ? parsedQuality
            : 0;
    }

    private sealed record CachedEpisodeGatewayObject(DateTimeOffset CachedAtUtc, Newtonsoft.Json.Linq.JObject Payload);
    private sealed class EpisodeRangeTransferCounters
    {
        public long TotalRead;
        public long SpeedWindowBytes;
    }

    [GeneratedRegex("href\\s*=\\s*[\"'](?<url>[^\"']*/download/\\?[^\"'<>\r\n]+)[\"']", RegexOptions.IgnoreCase)]
    private static partial Regex HearThisDownloadHrefRegex();

    [GeneratedRegex("https?:\\\\/\\\\/[^\"'\\s]+/download/\\?[^\"'\\s]+", RegexOptions.IgnoreCase)]
    private static partial Regex HearThisEscapedDownloadUrlRegex();

    private static void UpdatePayloadFiles(DeezerQueueItem payload, string outputPath)
    {
        payload.Files = QueuePayloadFileHelper.BuildSingleOutputFile(outputPath);
    }
}
