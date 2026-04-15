using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Apple;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Metadata.Qobuz;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.IO;
using TagLib;
using DeezerClient = DeezSpoTag.Integrations.Deezer.DeezerClient;

namespace DeezSpoTag.Services.Download.Qobuz;

public sealed class QobuzEngineProcessor : IQueueEngineProcessor
{
    private const string EngineName = "qobuz";
    private const string FailedStatus = "failed";
    private const string CompletedStatus = "completed";
    private const string CancelledStatus = "cancelled";
    private const string RunningStatus = "running";
    private const string InvalidPayloadMessage = "Invalid payload";
    private const string UpdateQueueEvent = "updateQueue";
    private const string TrackType = "track";
    private readonly DownloadQueueRepository _queueRepository;
    private readonly DownloadCancellationRegistry _cancellationRegistry;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly IDeezSpoTagListener _deezspotagListener;
    private readonly DownloadRetryScheduler _retryScheduler;
    private readonly IQobuzDownloadService _qobuzDownloader;
    private readonly IServiceProvider _serviceProvider;
    private readonly EngineFallbackCoordinator _fallbackCoordinator;
    private readonly SongLinkResolver _songLinkResolver;
    private readonly IActivityLogWriter _activityLog;
    private readonly Utils.LyricsService _lyricsService;
    private readonly IPostDownloadTaskScheduler _postDownloadTaskScheduler;
    private readonly IFolderConversionSettingsOverlay _folderConversionSettingsOverlay;
    private readonly IDownloadTagSettingsResolver _tagSettingsResolver;
    private readonly QobuzTrackResolver _qobuzTrackResolver;
    private readonly ILogger<QobuzEngineProcessor> _logger;

    public QobuzEngineProcessor(
        IServiceProvider serviceProvider,
        IQobuzDownloadService qobuzDownloader,
        ILogger<QobuzEngineProcessor> logger)
    {
        _queueRepository = serviceProvider.GetRequiredService<DownloadQueueRepository>();
        _cancellationRegistry = serviceProvider.GetRequiredService<DownloadCancellationRegistry>();
        _settingsService = serviceProvider.GetRequiredService<DeezSpoTagSettingsService>();
        _deezspotagListener = serviceProvider.GetRequiredService<IDeezSpoTagListener>();
        _retryScheduler = serviceProvider.GetRequiredService<DownloadRetryScheduler>();
        _qobuzDownloader = qobuzDownloader;
        _serviceProvider = serviceProvider;
        _fallbackCoordinator = serviceProvider.GetRequiredService<EngineFallbackCoordinator>();
        _songLinkResolver = serviceProvider.GetRequiredService<SongLinkResolver>();
        _activityLog = serviceProvider.GetRequiredService<IActivityLogWriter>();
        _lyricsService = serviceProvider.GetRequiredService<Utils.LyricsService>();
        _postDownloadTaskScheduler = serviceProvider.GetRequiredService<IPostDownloadTaskScheduler>();
        _folderConversionSettingsOverlay = serviceProvider.GetRequiredService<IFolderConversionSettingsOverlay>();
        _tagSettingsResolver = serviceProvider.GetRequiredService<IDownloadTagSettingsResolver>();
        _qobuzTrackResolver = serviceProvider.GetRequiredService<QobuzTrackResolver>();
        _logger = logger;
    }

    public string Engine => EngineName;

    Task IQueueEngineProcessor.ProcessQueueItemAsync(
        DownloadQueueItem item,
        DeezSpoTag.Services.Download.Deezer.IDeezerQueueContext context,
        CancellationToken cancellationToken) =>
        ProcessQueueItemAsync(item, cancellationToken);

    public async Task ProcessQueueItemAsync(DownloadQueueItem next, CancellationToken stoppingToken)
    {
        var settings = _settingsService.LoadSettings();
        QobuzQueueItem? payload = null;

        using var itemCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _cancellationRegistry.Register(next.QueueUuid, itemCts);
        var itemToken = itemCts.Token;

        try
        {
            payload = await DeserializeAndStartAsync(next, settings, itemToken);
            if (payload == null)
            {
                return;
            }

            await ExecuteDownloadPipelineAsync(next, payload, settings, itemToken);
        }
        catch (OperationCanceledException ex) when (itemToken.IsCancellationRequested)
        {
            if (_cancellationRegistry.WasTimedOut(next.QueueUuid))
            {
                var timeoutException = new TimeoutException(
                    DownloadQueueRecoveryPolicy.BuildStallTimeoutMessage(EngineName),
                    ex);
                await HandleFailureAsync(next, payload, timeoutException, stoppingToken);
                return;
            }

            await HandleCancellationAsync(next.QueueUuid, payload);
        }
        catch (OperationCanceledException ex) when (!itemToken.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            var timeoutException = new TimeoutException(
                $"{EngineName} operation timed out or was canceled by an external provider.",
                ex);
            await HandleFailureAsync(next, payload, timeoutException, stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await HandleFailureAsync(next, payload, ex, stoppingToken);
        }
        finally
        {
            _cancellationRegistry.Remove(next.QueueUuid);
        }
    }

    private async Task ExecuteDownloadPipelineAsync(
        DownloadQueueItem next,
        QobuzQueueItem payload,
        DeezSpoTagSettings settings,
        CancellationToken itemToken)
    {
        var context = BuildTrackContext(payload, settings);
        var request = BuildRequest(payload, settings, context);
        payload.QobuzRequestedQuality = request.Quality;
        var progressReporter = CreateProgressReporter(next.QueueUuid, itemToken);
        _activityLog.Info($"Download start: {next.QueueUuid} engine={EngineName} quality={request.Quality}");

        var explicitQobuzTrackId = ExtractQobuzTrackId(payload.SourceUrl) ?? ExtractQobuzTrackId(payload.Url);
        if (explicitQobuzTrackId.HasValue && string.IsNullOrWhiteSpace(payload.QobuzId))
        {
            payload.QobuzId = explicitQobuzTrackId.Value.ToString();
        }

        var resolvedIsrc = await ResolveIsrcAsync(payload, itemToken);
        await ResolveAndPersistPreferredTrackAsync(next.QueueUuid, payload, resolvedIsrc, itemToken);
        payload.QobuzResolvedQuality = request.Quality;
        payload.Quality = request.Quality;
        await QueueHelperUtils.UpdatePayloadAsync(_queueRepository, next.QueueUuid, payload, cancellationToken: itemToken);
        request.SelectedQualityCallback = selectedQuality =>
            SyncResolvedQualityAsync(next.QueueUuid, payload, selectedQuality, itemToken);

        var sourceSelection = ResolveQobuzSource(payload);
        await QueuePrefetchAsync(next.QueueUuid, context, payload, settings);

        var outputPath = await DownloadWithFallbackAsync(
            payload,
            request,
            settings,
            resolvedIsrc,
            sourceSelection,
            progressReporter,
            itemToken);
        outputPath = await TryApplyPostDownloadSettingsAsync(next.QueueUuid, context, payload, outputPath, settings, itemToken);
        var actualQuality = TryInferActualQobuzQuality(outputPath);
        payload.QobuzActualQuality = actualQuality ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(actualQuality)
            && !string.Equals(actualQuality, request.Quality, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Qobuz output quality differs from resolved quality for {QueueUuid}: resolved={Resolved} actual={Actual} file={FilePath}",
                next.QueueUuid,
                request.Quality,
                actualQuality,
                outputPath);
        }

        await SyncResolvedQualityAsync(next.QueueUuid, payload, request.Quality, itemToken);

        await CompleteDownloadAsync(next.QueueUuid, payload, outputPath, itemToken);
    }

    private async Task SyncResolvedQualityAsync(
        string queueUuid,
        QobuzQueueItem payload,
        string? selectedQuality,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(selectedQuality))
        {
            return;
        }

        var resolvedQuality = selectedQuality.Trim();
        var qualityChanged = !string.Equals(payload.Quality, resolvedQuality, StringComparison.OrdinalIgnoreCase);
        var resolvedChanged = !string.Equals(payload.QobuzResolvedQuality, resolvedQuality, StringComparison.OrdinalIgnoreCase);
        if (!qualityChanged && !resolvedChanged)
        {
            return;
        }

        payload.Quality = resolvedQuality;
        payload.QobuzResolvedQuality = resolvedQuality;
        await QueueHelperUtils.UpdatePayloadAsync(_queueRepository, queueUuid, payload, cancellationToken: cancellationToken);
        _deezspotagListener.Send(UpdateQueueEvent, new
        {
            uuid = queueUuid,
            quality = payload.Quality,
            engine = payload.Engine,
            qobuzRequestedQuality = payload.QobuzRequestedQuality,
            qobuzResolvedQuality = payload.QobuzResolvedQuality,
            qobuzActualQuality = payload.QobuzActualQuality
        });
    }

    private async Task<QobuzQueueItem?> DeserializeAndStartAsync(
        DownloadQueueItem next,
        DeezSpoTagSettings settings,
        CancellationToken itemToken)
    {
        var payload = QueueHelperUtils.DeserializeQueueItem<QobuzQueueItem>(next.PayloadJson);
        if (payload == null)
        {
            await _queueRepository.UpdateStatusAsync(next.QueueUuid, FailedStatus, InvalidPayloadMessage, cancellationToken: itemToken);
            _retryScheduler.ScheduleRetry(next.QueueUuid, EngineName, "invalid payload");
            return null;
        }

        await DownloadEngineSettingsHelper.ResolveAndApplyProfileAsync(
            _tagSettingsResolver,
            settings,
            payload.DestinationFolderId,
            _logger,
            itemToken,
            new DownloadEngineSettingsHelper.ProfileResolutionOptions(
                CurrentEngine: EngineName,
                WrapResolutionExceptions: false));
        await _folderConversionSettingsOverlay.ApplyAsync(settings, payload.DestinationFolderId, itemToken);

        _deezspotagListener.SendStartDownload(next.QueueUuid);
        _deezspotagListener.Send(UpdateQueueEvent, new
        {
            uuid = next.QueueUuid,
            progress = payload.Progress,
            downloaded = payload.Downloaded,
            failed = payload.Failed
        });

        await _queueRepository.UpdateStatusAsync(next.QueueUuid, RunningStatus, progress: payload.Progress, cancellationToken: itemToken);
        return payload;
    }

    private EngineAudioPostDownloadHelper.EngineTrackContext BuildTrackContext(
        QobuzQueueItem payload,
        DeezSpoTagSettings settings)
    {
        using var scope = _serviceProvider.CreateScope();
        var pathProcessor = scope.ServiceProvider.GetRequiredService<EnhancedPathTemplateProcessor>();
        return BuildTrackContext(payload, settings, pathProcessor);
    }

    private static QobuzDownloadRequest BuildRequest(
        QobuzQueueItem payload,
        DeezSpoTagSettings settings,
        EngineAudioPostDownloadHelper.EngineTrackContext context)
    {
        var request = QobuzRequestBuilder.BuildRequest(payload, settings);
        request.OutputDir = context.OutputDir;
        request.FilenameFormat = context.FilenameFormat;
        return request;
    }

    private Func<double, double, Task> CreateProgressReporter(string queueUuid, CancellationToken cancellationToken)
    {
        return QueueHelperUtils.CreateProgressReporter(
            _queueRepository,
            _deezspotagListener,
            queueUuid,
            _logger,
            "Failed to report progress for {QueueUuid}",
            cancellationToken);
    }

    private async Task<QobuzTrackResolution?> ResolveAndPersistPreferredTrackAsync(
        string queueUuid,
        QobuzQueueItem payload,
        string? resolvedIsrc,
        CancellationToken cancellationToken)
    {
        var sourceSelection = ResolveQobuzSource(payload);
        if (!string.IsNullOrWhiteSpace(payload.QobuzId) && sourceSelection.HasTrackUrl)
        {
            payload.QobuzResolutionSource = "direct_url";
            payload.QobuzResolutionScore = null;
            await QueueHelperUtils.UpdatePayloadAsync(_queueRepository, queueUuid, payload, cancellationToken: cancellationToken);
            return null;
        }

        var resolvedTrack = await ResolvePreferredQobuzTrackAsync(payload, resolvedIsrc, cancellationToken);
        if (resolvedTrack == null)
        {
            return null;
        }

        payload.QobuzId = resolvedTrack.Track.Id.ToString();
        payload.QobuzResolutionSource = resolvedTrack.Source;
        payload.QobuzResolutionScore = resolvedTrack.Score;
        payload.SourceUrl = $"https://play.qobuz.com/track/{resolvedTrack.Track.Id}";
        await QueueHelperUtils.UpdatePayloadAsync(_queueRepository, queueUuid, payload, cancellationToken: cancellationToken);
        return resolvedTrack;
    }

    private static QobuzSourceSelection ResolveQobuzSource(QobuzQueueItem payload)
    {
        if (HasQobuzTrackUrl(payload.SourceUrl))
        {
            return new QobuzSourceSelection(payload.SourceUrl ?? string.Empty, true);
        }

        if (HasQobuzTrackUrl(payload.Url))
        {
            return new QobuzSourceSelection(payload.Url ?? string.Empty, true);
        }

        return new QobuzSourceSelection(string.Empty, false);
    }

    private async Task QueuePrefetchAsync(
        string queueUuid,
        EngineAudioPostDownloadHelper.EngineTrackContext context,
        QobuzQueueItem payload,
        DeezSpoTagSettings settings)
    {
        var expectedOutputPath = !string.IsNullOrWhiteSpace(context.PathResult.WritePath)
            ? DownloadPathResolver.ResolveIoPath(context.PathResult.WritePath)
            : Path.Join(
                DownloadPathResolver.ResolveIoPath(context.PathResult.FilePath),
                context.PathResult.Filename);
        await QueueParallelPostDownloadPrefetchAsync(queueUuid, context, payload, settings, expectedOutputPath);
    }

    private async Task<string> DownloadWithFallbackAsync(
        QobuzQueueItem payload,
        QobuzDownloadRequest request,
        DeezSpoTagSettings settings,
        string? resolvedIsrc,
        QobuzSourceSelection sourceSelection,
        Func<double, double, Task> progressReporter,
        CancellationToken cancellationToken)
    {
        var context = new DownloadWithQualityContext(
            payload,
            request,
            settings,
            resolvedIsrc,
            sourceSelection,
            progressReporter,
            cancellationToken);

        try
        {
            return await DownloadWithQualityAsync(context, request.Quality);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
                                   && settings.FallbackBitrate
                                   && ShouldUseInEngineQualityFallback(payload))
        {
            var fallbackQuality = EngineQualityFallback.GetNextLowerQuality(EngineName, request.Quality);
            if (string.IsNullOrWhiteSpace(fallbackQuality))
            {
                throw;
            }

            _logger.LogWarning(ex, "Qobuz download failed at quality {Quality}, retrying at {Fallback}", request.Quality, fallbackQuality);
            request.Quality = fallbackQuality;
            payload.Quality = fallbackQuality;
            return await DownloadWithQualityAsync(context, fallbackQuality);
        }
    }

    private static bool ShouldUseInEngineQualityFallback(QobuzQueueItem payload)
    {
        // If the queue item is in a multi-engine fallback plan (e.g. qobuz -> tidal -> apple),
        // do not do in-engine quality step-down first; let the global coordinator preserve source order.
        return EngineFallbackPlanPolicy.ShouldUseInEngineFallback(payload, EngineName);
    }

    private async Task<string> DownloadWithQualityAsync(DownloadWithQualityContext context, string quality)
    {
        var requestPayload = BuildDownloadPayload(
            context.Payload,
            context.Request,
            context.Settings,
            quality,
            context.ResolvedIsrc,
            context.SourceSelection,
            context.ProgressReporter);
        if (!string.IsNullOrWhiteSpace(context.ResolvedIsrc))
        {
            return await _qobuzDownloader.DownloadByIsrcAsync(requestPayload, context.CancellationToken);
        }

        if (context.SourceSelection.HasTrackUrl || !string.IsNullOrWhiteSpace(context.Payload.SourceUrl))
        {
            return await _qobuzDownloader.DownloadByUrlAsync(requestPayload, context.CancellationToken);
        }

        throw new InvalidOperationException("Qobuz download requires a valid source URL or ISRC.");
    }

    private sealed record DownloadWithQualityContext(
        QobuzQueueItem Payload,
        QobuzDownloadRequest Request,
        DeezSpoTagSettings Settings,
        string? ResolvedIsrc,
        QobuzSourceSelection SourceSelection,
        Func<double, double, Task> ProgressReporter,
        CancellationToken CancellationToken);

    private static QobuzDownloadRequest BuildDownloadPayload(
        QobuzQueueItem payload,
        QobuzDownloadRequest request,
        DeezSpoTagSettings settings,
        string quality,
        string? resolvedIsrc,
        QobuzSourceSelection sourceSelection,
        Func<double, double, Task> progressReporter)
    {
        return new QobuzDownloadRequest
        {
            Isrc = resolvedIsrc ?? string.Empty,
            TrackUrl = sourceSelection.HasTrackUrl ? sourceSelection.TrackUrl : payload.SourceUrl,
            OutputDir = request.OutputDir,
            Quality = quality,
            FilenameFormat = request.FilenameFormat,
            IncludeTrackNumber = request.IncludeTrackNumber,
            Position = request.Position,
            TrackName = request.TrackName,
            ArtistName = request.ArtistName,
            AlbumName = request.AlbumName,
            AlbumArtist = request.AlbumArtist,
            ReleaseDate = request.ReleaseDate,
            UseAlbumTrackNumber = request.UseAlbumTrackNumber,
            CoverUrl = request.CoverUrl,
            EmbedMaxQualityCover = settings.EmbedMaxQualityCover,
            DurationSeconds = request.DurationSeconds,
            SpotifyTrackNumber = request.SpotifyTrackNumber,
            SpotifyDiscNumber = request.SpotifyDiscNumber,
            SpotifyTotalTracks = request.SpotifyTotalTracks,
            AllowQualityFallback = request.AllowQualityFallback,
            SelectedQualityCallback = request.SelectedQualityCallback,
            TagSettings = settings.Tags,
            ProgressCallback = progressReporter
        };
    }

    private async Task<string> TryApplyPostDownloadSettingsAsync(
        string queueUuid,
        EngineAudioPostDownloadHelper.EngineTrackContext context,
        QobuzQueueItem payload,
        string outputPath,
        DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            return await ApplyPostDownloadSettingsAsync(
                context,
                payload,
                outputPath,
                settings,
                scope.ServiceProvider,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Qobuz post-download settings failed for {QueueUuid}", queueUuid);
            return outputPath;
        }
    }

    private async Task CompleteDownloadAsync(
        string queueUuid,
        QobuzQueueItem payload,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var finalSize = QueueHelperUtils.TryGetFileSizeMb(outputPath);
        if (finalSize <= 0 || !QueueHelperUtils.OutputExists(outputPath))
        {
            throw new InvalidOperationException($"Downloaded file missing or empty: {outputPath}");
        }

        await _queueRepository.UpdateStatusAsync(queueUuid, CompletedStatus, downloaded: 1, progress: 100, cancellationToken: cancellationToken);
        await QueueHelperUtils.UpdatePayloadAsync(
            new QueueHelperUtils.UpdatePayloadRequest<QobuzQueueItem>(
                _queueRepository,
                queueUuid,
                payload,
                outputPath,
                finalSize,
                payload.Size,
                new QueueHelperUtils.PayloadUpdateMutators<QobuzQueueItem>(
                    (item, value) => item.FilePath = value,
                    (item, value) => item.TotalSize = value,
                    (item, value) => item.Progress = value,
                    (item, value) => item.Downloaded = value)),
            cancellationToken);
        await EngineAudioPostDownloadHelper.UpdateWatchlistTrackStatusAsync(payload, CompletedStatus, _serviceProvider, cancellationToken);
        _retryScheduler.Clear(queueUuid);

        _deezspotagListener.Send(UpdateQueueEvent, new
        {
            uuid = queueUuid,
            progress = 100,
            downloaded = 1,
            failed = 0,
            engine = payload.Engine
        });
        _deezspotagListener.SendFinishDownload(queueUuid, payload.Title);
    }

    private async Task HandleCancellationAsync(string queueUuid, QobuzQueueItem? payload)
    {
        var current = await _queueRepository.GetByUuidAsync(queueUuid, CancellationToken.None);
        var status = current?.Status ?? CancelledStatus;
        if (status is CompletedStatus or FailedStatus)
        {
            return;
        }

        await _queueRepository.UpdateStatusAsync(queueUuid, CancelledStatus, "Cancelled", cancellationToken: CancellationToken.None);
        if (payload != null)
        {
            await EngineAudioPostDownloadHelper.UpdateWatchlistTrackStatusAsync(payload, CancelledStatus, _serviceProvider, CancellationToken.None);
        }

        _retryScheduler.ScheduleRetry(queueUuid, EngineName, CancelledStatus);
    }

    private async Task HandleFailureAsync(
        DownloadQueueItem next,
        QobuzQueueItem? payload,
        Exception ex,
        CancellationToken stoppingToken)
    {
        _logger.LogError(ex, "Qobuz download failed for {QueueUuid}", next.QueueUuid);
        if (payload != null && !stoppingToken.IsCancellationRequested)
        {
            var quality = string.IsNullOrWhiteSpace(payload.Quality) ? "unknown" : payload.Quality;
            _activityLog.Warn($"Download failed (engine={EngineName} quality={quality}): {next.QueueUuid} {ex.Message}");
            var advanced = await _fallbackCoordinator.TryAdvanceAsync(
                next.QueueUuid,
                next.Engine,
                payload,
                stoppingToken);
            if (advanced)
            {
                _activityLog.Info($"Fallback advanced: {next.QueueUuid} -> {payload.Engine} (auto_index={payload.AutoIndex})");
                if (!payload.FallbackQueuedExternally)
                {
                    _deezspotagListener.SendAddedToQueue(payload.ToQueuePayload());
                }

                return;
            }
        }

        await _queueRepository.UpdateStatusAsync(next.QueueUuid, FailedStatus, ex.Message, cancellationToken: CancellationToken.None);
        if (payload != null)
        {
            await EngineAudioPostDownloadHelper.UpdateWatchlistTrackStatusAsync(payload, FailedStatus, _serviceProvider, CancellationToken.None);
        }

        _activityLog.Error($"Download failed (engine={EngineName}): {next.QueueUuid} {ex.Message}");
        _retryScheduler.ScheduleRetry(next.QueueUuid, EngineName, ex.Message);
    }

    private readonly record struct QobuzSourceSelection(string TrackUrl, bool HasTrackUrl);

    private async Task<string?> ResolveIsrcAsync(QobuzQueueItem payload, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(payload.Isrc) && IsrcValidator.IsValid(payload.Isrc))
        {
            return payload.Isrc;
        }

        var userCountry = _settingsService.LoadSettings().DeezerCountry;
        if (!string.IsNullOrWhiteSpace(payload.DeezerId))
        {
            var deezerUrl = $"https://www.deezer.com/track/{payload.DeezerId}";
            var deezerLink = await _songLinkResolver.ResolveByUrlAsync(deezerUrl, userCountry, cancellationToken);
            if (!string.IsNullOrWhiteSpace(deezerLink?.Isrc) && IsrcValidator.IsValid(deezerLink.Isrc))
            {
                return deezerLink.Isrc;
            }
        }

        if (!string.IsNullOrWhiteSpace(payload.SpotifyId))
        {
            var spotifyLink = await _songLinkResolver.ResolveSpotifyTrackAsync(payload.SpotifyId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(spotifyLink?.Isrc) && IsrcValidator.IsValid(spotifyLink.Isrc))
            {
                return spotifyLink.Isrc;
            }
        }

        return null;
    }

    private async Task<QobuzTrackResolution?> ResolvePreferredQobuzTrackAsync(
        QobuzQueueItem payload,
        string? resolvedIsrc,
        CancellationToken cancellationToken)
    {
        var resolution = await _qobuzTrackResolver.ResolveTrackAsync(
            resolvedIsrc,
            payload.Title,
            payload.Artist,
            payload.Album,
            payload.DurationSeconds > 0 ? payload.DurationSeconds * 1000 : null,
            cancellationToken);

        if (resolution == null)
        {
            return null;
        }

        var existingTrackId = ExtractQobuzTrackId(payload.SourceUrl) ?? ExtractQobuzTrackId(payload.Url);
        if (existingTrackId.HasValue && existingTrackId.Value != resolution.Track.Id)
        {
            _logger.LogInformation(
                "Qobuz resolution corrected track for {QueueUuid}: existing={ExistingTrackId} resolved={ResolvedTrackId} source={Source} score={Score}",
                payload.Id,
                existingTrackId.Value,
                resolution.Track.Id,
                resolution.Source,
                resolution.Score);
        }

        return resolution;
    }

    private static bool HasQobuzTrackUrl(string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl)
            || !Uri.TryCreate(sourceUrl, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (!parsed.Host.Contains("qobuz.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return parsed.AbsolutePath.Contains("/track/", StringComparison.OrdinalIgnoreCase);
    }

    private static int? ExtractQobuzTrackId(string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl)
            || !Uri.TryCreate(sourceUrl, UriKind.Absolute, out var parsed)
            || !parsed.Host.Contains("qobuz.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var segments = parsed.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals(TrackType, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(segments[i + 1], out var trackId))
            {
                return trackId;
            }
        }

        return null;
    }

    private static EngineAudioPostDownloadHelper.EngineTrackContext BuildTrackContext(
        QobuzQueueItem payload,
        DeezSpoTagSettings settings,
        EnhancedPathTemplateProcessor pathProcessor)
    {
        return EngineAudioPostDownloadHelper.BuildTrackContext(
            payload,
            settings,
            pathProcessor,
            EngineName,
            !string.IsNullOrWhiteSpace(payload.QobuzId) ? payload.QobuzId : payload.SpotifyId,
            downloadTypeResolver: null,
            configureTrack: static (track, item) =>
            {
                if (item is not QobuzQueueItem qobuzPayload)
                {
                    return;
                }

                track.QobuzId = qobuzPayload.QobuzId;
                if (!string.IsNullOrWhiteSpace(qobuzPayload.Url))
                {
                    track.Urls["source_url_fallback"] = qobuzPayload.Url;
                }
            });
    }

    private async Task QueueParallelPostDownloadPrefetchAsync(
        string queueUuid,
        EngineAudioPostDownloadHelper.EngineTrackContext context,
        QobuzQueueItem payload,
        DeezSpoTagSettings settings,
        string expectedOutputPath)
    {
        var request = new EngineAudioPostDownloadHelper.PrefetchRequest(
            queueUuid,
            context,
            payload,
            settings,
            expectedOutputPath,
            _postDownloadTaskScheduler,
            _lyricsService,
            _deezspotagListener,
            _activityLog,
            _logger,
            Engine,
            AppleCoverLookupIdOverride: null,
            AnimatedArtworkAppleIdOverride: null);
        await EngineAudioPostDownloadHelper.QueueParallelPostDownloadPrefetchAsync(
            request,
            CancellationToken.None);
    }

    private async Task<string> ApplyPostDownloadSettingsAsync(
        EngineAudioPostDownloadHelper.EngineTrackContext context,
        QobuzQueueItem payload,
        string outputPath,
        DeezSpoTagSettings settings,
        IServiceProvider scope,
        CancellationToken cancellationToken)
    {
        var request = new EngineAudioPostDownloadHelper.PostDownloadSettingsRequest(
            context,
            payload,
            outputPath,
            settings,
            scope,
            Engine,
            _logger,
            AppleCoverLookupIdOverride: null,
            AnimatedArtworkAppleIdOverride: null);
        return await EngineAudioPostDownloadHelper.ApplyPostDownloadSettingsAsync(
            request,
            cancellationToken);
    }

    private string? TryInferActualQobuzQuality(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath))
        {
            return null;
        }

        try
        {
            using var mediaFile = TagLib.File.Create(filePath);
            var bitsPerSample = mediaFile.Properties.BitsPerSample;
            if (bitsPerSample <= 0)
            {
                return null;
            }

            var sampleRate = mediaFile.Properties.AudioSampleRate;
            if (bitsPerSample >= 24)
            {
                return sampleRate >= 96000 ? "27" : "7";
            }

            return "6";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to infer Qobuz output quality from file {FilePath}", filePath);
            }
            return null;
        }
    }

}
