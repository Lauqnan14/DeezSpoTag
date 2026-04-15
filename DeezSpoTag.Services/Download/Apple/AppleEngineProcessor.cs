using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net.Sockets;
using DeezerClient = DeezSpoTag.Integrations.Deezer.DeezerClient;

namespace DeezSpoTag.Services.Download.Apple;

public sealed class AppleEngineProcessor : IQueueEngineProcessor
{
    private const string EngineName = "apple";
    private const string AppleProvider = "apple";
    private const string DeezerProvider = "deezer";
    private const string SpotifyProvider = "spotify";
    private const string FailedStatus = "failed";
    private const string CompletedStatus = "completed";
    private const string NoLyricsStatus = "no-lyrics";
    private const string RunningStatus = "running";
    private const string PausedStatus = "paused";
    private const string CanceledStatus = "canceled";
    private const string CancelledStatus = "cancelled";
    private const string InvalidPayloadMessage = "Invalid payload";
    private const string UpdateQueueEvent = "updateQueue";
    private const string DefaultLanguage = "en-US";
    private const string AttributesKey = "attributes";
    private const string UnknownValue = "unknown";
    private const string PlaylistType = "playlist";
    private const string FetchingState = "fetching";
    private const string SkippedState = "skipped";
    private const string AtmosKeyword = "atmos";
    private const string AacKeyword = "aac";
    private const string AlacKeyword = "alac";
    private const string AacLcType = "aac-lc";
    private readonly DownloadQueueRepository _queueRepository;
    private readonly DownloadCancellationRegistry _cancellationRegistry;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly IDeezSpoTagListener _deezspotagListener;
    private readonly DownloadRetryScheduler _retryScheduler;
    private readonly IAppleDownloadService _downloadService;
    private readonly IAppleWrapperStatusProvider _wrapperStatusProvider;
    private readonly IServiceProvider _serviceProvider;
    private readonly EngineFallbackCoordinator _fallbackCoordinator;
    private readonly IActivityLogWriter _activityLog;
    private readonly Utils.LyricsService _lyricsService;
    private readonly IPostDownloadTaskScheduler _postDownloadTaskScheduler;
    private readonly IDownloadTagSettingsResolver _tagSettingsResolver;
    private readonly IFolderConversionSettingsOverlay _folderConversionSettingsOverlay;
    private readonly ILogger<AppleEngineProcessor> _logger;

    private sealed record CoverFallbackServices(
        AppleMusicCatalogService? AppleCatalog,
        DeezerClient? DeezerClient,
        ISpotifyIdResolver? SpotifyIdResolver,
        ISpotifyArtworkResolver? SpotifyArtworkResolver,
        IHttpClientFactory? HttpClientFactory);

    private sealed class PrefetchWorkContext
    {
        public required string QueueUuid { get; init; }
        public required EngineAudioPostDownloadHelper.EngineTrackContext Context { get; init; }
        public required AppleQueueItem Payload { get; init; }
        public required DeezSpoTagSettings Settings { get; init; }
        public required string ExpectedOutputPath { get; init; }
        public required string ExpectedBaseName { get; init; }
        public required string FileDir { get; init; }
        public required string CoverPath { get; init; }
        public required string ArtistPath { get; init; }
        public required string ExtrasPath { get; init; }
        public required bool AllowPlaylistCover { get; init; }
        public required bool ShouldFetchArtwork { get; init; }
        public required bool ShouldFetchLyrics { get; init; }
    }

    private sealed class ArtworkPrefetchContext
    {
        public required PrefetchWorkContext Work { get; init; }
        public required string? CoverUrl { get; init; }
        public required bool IsAppleCover { get; init; }
        public required bool PreferMaxQualityCover { get; init; }
        public required int AppleArtworkSize { get; init; }
        public required ImageDownloader ImageDownloader { get; init; }
        public required EnhancedPathTemplateProcessor PathProcessor { get; init; }
        public required AppleMusicCatalogService? AppleCatalog { get; init; }
        public required DeezerClient? DeezerClient { get; init; }
        public required ISpotifyArtworkResolver? SpotifyArtworkResolver { get; init; }
        public required IHttpClientFactory? HttpClientFactory { get; init; }
        public required Func<string> GetArtworkStatus { get; init; }
        public required Func<string> GetLyricsStatus { get; init; }
        public required Action<string> SetArtworkStatus { get; init; }
    }

    private sealed class LyricsPrefetchContext
    {
        public required PrefetchWorkContext Work { get; init; }
        public required Func<string> GetArtworkStatus { get; init; }
        public required Func<string> GetLyricsStatus { get; init; }
        public required Action<string> SetLyricsStatus { get; init; }
        public required Action<string> SetLyricsType { get; init; }
        public required Func<string> GetLyricsType { get; init; }
    }

    private sealed record CoverSaveContext(
        DeezSpoTagSettings Settings,
        string CoverUrl,
        string CoverPath,
        string CoverName,
        bool IsAppleCover,
        bool PreferMaxQualityCover,
        int AppleArtworkSize,
        ImageDownloader ImageDownloader);

    private sealed record ArtistPrefetchSaveContext(
        DeezSpoTagSettings Settings,
        AppleQueueItem Payload,
        Track Track,
        string ArtistPath,
        int AppleArtworkSize,
        bool PreferMaxQualityCover,
        ImageDownloader ImageDownloader,
        EnhancedPathTemplateProcessor PathProcessor,
        AppleMusicCatalogService? AppleCatalog,
        DeezerClient? DeezerClient,
        ISpotifyArtworkResolver? SpotifyArtworkResolver,
        IHttpClientFactory? HttpClientFactory);

    private sealed class QueueInitializationContext
    {
        public required AppleQueueItem Payload { get; init; }
        public required DeezSpoTagSettings Settings { get; init; }
        public required bool VideoPayload { get; init; }
        public required string? OriginalDownloadLocation { get; init; }
        public string? VideoDestinationRoot { get; init; }
    }

    private sealed class DownloadRequestContext
    {
        public required AppleDownloadRequest Request { get; init; }
        public EngineAudioPostDownloadHelper.EngineTrackContext? TrackContext { get; init; }
    }

    public AppleEngineProcessor(
        IServiceProvider serviceProvider,
        ILogger<AppleEngineProcessor> logger)
    {
        _queueRepository = serviceProvider.GetRequiredService<DownloadQueueRepository>();
        _cancellationRegistry = serviceProvider.GetRequiredService<DownloadCancellationRegistry>();
        _settingsService = serviceProvider.GetRequiredService<DeezSpoTagSettingsService>();
        _deezspotagListener = serviceProvider.GetRequiredService<IDeezSpoTagListener>();
        _retryScheduler = serviceProvider.GetRequiredService<DownloadRetryScheduler>();
        _downloadService = serviceProvider.GetRequiredService<IAppleDownloadService>();
        _wrapperStatusProvider = serviceProvider.GetRequiredService<IAppleWrapperStatusProvider>();
        _serviceProvider = serviceProvider;
        _fallbackCoordinator = serviceProvider.GetRequiredService<EngineFallbackCoordinator>();
        _activityLog = serviceProvider.GetRequiredService<IActivityLogWriter>();
        _lyricsService = serviceProvider.GetRequiredService<Utils.LyricsService>();
        _postDownloadTaskScheduler = serviceProvider.GetRequiredService<IPostDownloadTaskScheduler>();
        _tagSettingsResolver = serviceProvider.GetRequiredService<IDownloadTagSettingsResolver>();
        _folderConversionSettingsOverlay = serviceProvider.GetRequiredService<IFolderConversionSettingsOverlay>();
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
        _ = _settingsService.LoadSettings();
        QueueInitializationContext? queueContext = null;

        using var itemCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _cancellationRegistry.Register(next.QueueUuid, itemCts);
        var itemToken = itemCts.Token;

        try
        {
            queueContext = await ExecuteQueueItemPipelineAsync(next, stoppingToken, itemToken);
        }
        catch (OperationCanceledException ex) when (itemToken.IsCancellationRequested)
        {
            await HandleItemCanceledAsync(next, queueContext, ex, stoppingToken);
        }
        catch (OperationCanceledException ex) when (!itemToken.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            var timeoutException = new TimeoutException(
                $"{EngineName} operation timed out or was canceled by an external provider.",
                ex);
            _logger.LogError(ex, "Apple download timed out for {QueueUuid}", next.QueueUuid);
            await HandleQueueItemFailureAsync(next, queueContext, timeoutException.Message, stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Apple download failed for {QueueUuid}", next.QueueUuid);
            await HandleQueueItemFailureAsync(next, queueContext, ex.Message, stoppingToken);
        }
        finally
        {
            RestoreOriginalDownloadLocation(queueContext);
            _cancellationRegistry.Remove(next.QueueUuid);
        }
    }

    private async Task<QueueInitializationContext?> ExecuteQueueItemPipelineAsync(
        DownloadQueueItem next,
        CancellationToken stoppingToken,
        CancellationToken itemToken)
    {
        var queueContext = await InitializeQueueItemAsync(next, itemToken);
        if (queueContext == null)
        {
            return null;
        }

        var requestContext = await BuildDownloadRequestContextAsync(next, queueContext, itemToken);
        if (!await EnsureWrapperAvailabilityAsync(next, queueContext, requestContext.Request, stoppingToken, itemToken))
        {
            return queueContext;
        }

        await QueuePrefetchIfNeededAsync(next.QueueUuid, queueContext, requestContext.TrackContext);
        var result = await ExecuteDownloadWithFallbackAsync(next.QueueUuid, queueContext.Payload, requestContext.Request, itemToken);
        if (!result.Success)
        {
            await HandleDownloadFailureAsync(next, queueContext.Payload, result.Message, stoppingToken, itemToken);
            return queueContext;
        }

        ApplyDownloadQualityMetadata(queueContext.Payload, result, next.QueueUuid);
        var outputPath = await ApplyPostDownloadSettingsSafelyAsync(
            next.QueueUuid,
            queueContext,
            requestContext.TrackContext,
            result.OutputPath,
            itemToken);
        if (!await PersistOutputMetadataIfPresentAsync(next.QueueUuid, queueContext.Payload, outputPath, itemToken))
        {
            const string verificationError = "Downloaded file missing or empty after transfer.";
            _logger.LogWarning("Apple download verification failed for {QueueUuid}: {OutputPath}", next.QueueUuid, outputPath);
            await HandleDownloadFailureAsync(next, queueContext.Payload, verificationError, stoppingToken, itemToken);
            return queueContext;
        }

        await MarkQueueItemCompletedAsync(next.QueueUuid, queueContext.Payload, itemToken);
        return queueContext;
    }

    private async Task HandleItemCanceledAsync(
        DownloadQueueItem next,
        QueueInitializationContext? queueContext,
        OperationCanceledException exception,
        CancellationToken stoppingToken)
    {
        if (_cancellationRegistry.WasTimedOut(next.QueueUuid))
        {
            var timeoutException = new TimeoutException(
                DownloadQueueRecoveryPolicy.BuildStallTimeoutMessage(EngineName),
                exception);
            await HandleQueueItemFailureAsync(next, queueContext, timeoutException.Message, stoppingToken);
            return;
        }

        await HandleCanceledQueueItemAsync(next.QueueUuid);
    }

    private async Task HandleQueueItemFailureAsync(
        DownloadQueueItem next,
        QueueInitializationContext? queueContext,
        string message,
        CancellationToken stoppingToken)
    {
        if (queueContext != null)
        {
            await HandleDownloadFailureAsync(next, queueContext.Payload, message, stoppingToken, CancellationToken.None);
            return;
        }

        await _queueRepository.UpdateStatusAsync(next.QueueUuid, FailedStatus, message, cancellationToken: CancellationToken.None);
        ScheduleRetryIfEligible(next.QueueUuid, message);
    }

    private static void RestoreOriginalDownloadLocation(QueueInitializationContext? queueContext)
    {
        if (queueContext?.OriginalDownloadLocation != null)
        {
            queueContext.Settings.DownloadLocation = queueContext.OriginalDownloadLocation;
        }
    }

    private async Task<QueueInitializationContext?> InitializeQueueItemAsync(
        DownloadQueueItem next,
        CancellationToken itemToken)
    {
        var payload = AppleQueueItemHelpers.DeserializeQueueItem(next.PayloadJson);
        if (payload == null)
        {
            await _queueRepository.UpdateStatusAsync(next.QueueUuid, FailedStatus, InvalidPayloadMessage, cancellationToken: itemToken);
            ScheduleRetryIfEligible(next.QueueUuid, "invalid payload");
            return null;
        }

        var isVideoPayload = IsVideoPayload(payload);
        _deezspotagListener.SendStartDownload(next.QueueUuid);
        _deezspotagListener.Send(UpdateQueueEvent, new
        {
            uuid = next.QueueUuid,
            progress = payload.Progress,
            downloaded = payload.Downloaded,
            failed = payload.Failed
        });
        await _queueRepository.UpdateStatusAsync(next.QueueUuid, RunningStatus, progress: payload.Progress, cancellationToken: itemToken);

        var settings = _settingsService.LoadSettings();
        var originalDownloadLocation = settings.DownloadLocation;
        await DownloadEngineSettingsHelper.ResolveAndApplyProfileAsync(
            _tagSettingsResolver,
            settings,
            payload.DestinationFolderId,
            _logger,
            itemToken,
            new DownloadEngineSettingsHelper.ProfileResolutionOptions(
                CurrentEngine: EngineName,
                RequireProfile: !isVideoPayload));
        await _folderConversionSettingsOverlay.ApplyAsync(settings, payload.DestinationFolderId, itemToken);
        DownloadEngineSettingsHelper.ApplyQualityBucketToSettings(settings, payload.QualityBucket);

        var videoDestinationRoot = isVideoPayload
            ? await ResolveVideoDestinationRootAsync(payload.DestinationFolderId, itemToken)
            : null;
        if (isVideoPayload
            && payload.DestinationFolderId.HasValue
            && string.IsNullOrWhiteSpace(videoDestinationRoot))
        {
            const string message = "Destination folder not found or disabled for video download";
            await _queueRepository.UpdateStatusAsync(next.QueueUuid, FailedStatus, message, cancellationToken: itemToken);
            _deezspotagListener.Send(UpdateQueueEvent, new { uuid = next.QueueUuid, status = FailedStatus, failed = true, error = message });
            return null;
        }

        await ResolveAndPersistStorefrontAppleIdAsync(next.QueueUuid, payload, settings, itemToken);
        if (isVideoPayload)
        {
            await TryPopulateVideoMetadataAsync(payload, next.QueueUuid, itemToken);
        }

        return new QueueInitializationContext
        {
            Payload = payload,
            Settings = settings,
            VideoPayload = isVideoPayload,
            VideoDestinationRoot = videoDestinationRoot,
            OriginalDownloadLocation = originalDownloadLocation
        };
    }

    private async Task ResolveAndPersistStorefrontAppleIdAsync(
        string queueUuid,
        AppleQueueItem payload,
        DeezSpoTagSettings settings,
        CancellationToken itemToken)
    {
        var resolvedAppleId = await ResolveAppleIdForStorefrontAsync(payload, settings, itemToken);
        if (string.IsNullOrWhiteSpace(resolvedAppleId)
            || string.Equals(payload.AppleId, resolvedAppleId, StringComparison.Ordinal))
        {
            return;
        }

        payload.AppleId = resolvedAppleId;
        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        await _queueRepository.UpdatePayloadAsync(queueUuid, json, itemToken);
    }

    private async Task<DownloadRequestContext> BuildDownloadRequestContextAsync(
        DownloadQueueItem next,
        QueueInitializationContext queueContext,
        CancellationToken itemToken)
    {
        var progressReporter = AppleQueueItemHelpers.CreateProgressReporter(
            _queueRepository,
            _deezspotagListener,
            next.QueueUuid,
            queueContext.Payload,
            _logger,
            itemToken);
        var trackContext = await BuildTrackContextAsync(next.QueueUuid, queueContext.Payload, queueContext.Settings, itemToken);
        var request = AppleRequestBuilder.BuildRequest(queueContext.Payload, queueContext.Settings, progressReporter);
        await TryPopulateAuthorizationTokenAsync(next.QueueUuid, request, itemToken);
        if (trackContext != null && !queueContext.VideoPayload)
        {
            request.OutputDir = trackContext.OutputDir;
            request.FilenameFormat = trackContext.FilenameFormat;
        }
        if (queueContext.VideoPayload && !string.IsNullOrWhiteSpace(queueContext.VideoDestinationRoot))
        {
            request.VideoOutputRoot = DownloadPathResolver.ResolveIoPath(queueContext.VideoDestinationRoot);
        }

        return new DownloadRequestContext
        {
            Request = request,
            TrackContext = trackContext
        };
    }

    private async Task<EngineAudioPostDownloadHelper.EngineTrackContext?> BuildTrackContextAsync(
        string queueUuid,
        AppleQueueItem payload,
        DeezSpoTagSettings settings,
        CancellationToken itemToken)
    {
        var appleId = ResolveAppleId(payload);
        if (string.IsNullOrWhiteSpace(appleId) && !string.IsNullOrWhiteSpace(payload.Isrc))
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var catalog = scope.ServiceProvider.GetRequiredService<AppleMusicCatalogService>();
                using var isrcDoc = await catalog.GetSongByIsrcAsync(payload.Isrc, settings.AppleMusic.Storefront, DefaultLanguage, itemToken, settings.AppleMusic.MediaUserToken);
                var resolved = TryExtractAppleIdFromCatalog(isrcDoc);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    appleId = resolved;
                    payload.AppleId = resolved;
                    _logger.LogInformation("Apple ID resolved via ISRC for {QueueUuid}: {AppleId}", queueUuid, resolved);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Apple ISRC lookup failed for {QueueUuid}", queueUuid);
            }
        }

        if (!string.IsNullOrWhiteSpace(appleId) && string.IsNullOrWhiteSpace(payload.AppleId))
        {
            payload.AppleId = appleId;
            var json = System.Text.Json.JsonSerializer.Serialize(payload);
            await _queueRepository.UpdatePayloadAsync(queueUuid, json, itemToken);
        }

        using var buildScope = _serviceProvider.CreateScope();
        var pathProcessor = buildScope.ServiceProvider.GetRequiredService<EnhancedPathTemplateProcessor>();
        return BuildTrackContext(payload, settings, pathProcessor, appleId);
    }

    private async Task TryPopulateAuthorizationTokenAsync(
        string queueUuid,
        AppleDownloadRequest request,
        CancellationToken itemToken)
    {
        if (!string.IsNullOrWhiteSpace(request.AuthorizationToken))
        {
            return;
        }

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var catalog = scope.ServiceProvider.GetRequiredService<AppleMusicCatalogService>();
            request.AuthorizationToken = await catalog.GetAuthorizationTokenAsync(itemToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to resolve Apple dev token before download for {QueueUuid}.", queueUuid);
        }
    }

    private async Task<bool> EnsureWrapperAvailabilityAsync(
        DownloadQueueItem next,
        QueueInitializationContext queueContext,
        AppleDownloadRequest request,
        CancellationToken stoppingToken,
        CancellationToken itemToken)
    {
        if (!IsWrapperRequired(request))
        {
            return true;
        }

        if (!AreWrapperStreamPortsReachable(request, out var wrapperPortReason))
        {
            var allowAudioFallback = !(request.IsVideo || queueContext.VideoPayload);
            if (allowAudioFallback && CanFallbackToAacStereo(request) && ShouldUseInEngineAppleAacFallback(queueContext.Payload))
            {
                ApplyAacStereoFallback(request);
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "Apple wrapper stream ports unavailable; falling back to AAC stereo for {QueueUuid}. Reason: {Reason}",
                        next.QueueUuid,
                        wrapperPortReason);
                }
                _deezspotagListener.SendDownloadWarn(
                    next.QueueUuid,
                    new { message = "Wrapper stream ports unavailable, falling back to AAC stereo." },
                    "wrapper_stream_fallback",
                    wrapperPortReason);
                return true;
            }

            await HandleDownloadFailureAsync(next, queueContext.Payload, wrapperPortReason, stoppingToken, itemToken, quality: null);
            return false;
        }

        var wrapperStatus = _wrapperStatusProvider.GetStatus();
        if (wrapperStatus.WrapperReady)
        {
            return true;
        }

        var isVideoWrapperCheck = request.IsVideo || queueContext.VideoPayload;
        if (!isVideoWrapperCheck && CanFallbackToAacStereo(request) && ShouldUseInEngineAppleAacFallback(queueContext.Payload))
        {
            ApplyAacStereoFallback(request);
            _logger.LogInformation(
                "Apple wrapper unavailable; falling back to AAC stereo for {QueueUuid}.",
                next.QueueUuid);
            _deezspotagListener.SendDownloadWarn(
                next.QueueUuid,
                new { message = "Wrapper offline, falling back to AAC stereo." },
                "wrapper_fallback",
                "Start the Apple wrapper to restore ALAC/Atmos downloads.");
            return true;
        }

        var reason = wrapperStatus.NeedsTwoFactor
            ? "Apple wrapper requires 2FA verification."
            : wrapperStatus.Message;
        await HandleDownloadFailureAsync(next, queueContext.Payload, reason, stoppingToken, itemToken, quality: null);
        return false;
    }

    private static bool AreWrapperStreamPortsReachable(AppleDownloadRequest request, out string reason)
    {
        var decryptEndpoint = request.DecryptM3u8Port?.Trim() ?? string.Empty;
        var m3u8Endpoint = request.GetM3u8Port?.Trim() ?? string.Empty;
        var decryptReady = IsEndpointReachable(decryptEndpoint, TimeSpan.FromSeconds(2));
        var m3u8Ready = IsEndpointReachable(m3u8Endpoint, TimeSpan.FromSeconds(2));
        if (decryptReady && m3u8Ready)
        {
            reason = string.Empty;
            return true;
        }

        reason = $"Apple wrapper stream ports unavailable (decrypt={decryptEndpoint}, m3u8={m3u8Endpoint}).";
        return false;
    }

    private static bool IsEndpointReachable(string endpoint, TimeSpan timeout)
    {
        if (!TryParseEndpoint(endpoint, out var host, out var port))
        {
            return false;
        }

        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync(host, port);
            if (!task.Wait(timeout))
            {
                return false;
            }

            return client.Connected;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private static bool TryParseEndpoint(string endpoint, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return false;
        }

        var parts = endpoint.Split(':', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || !int.TryParse(parts[1], out port))
        {
            return false;
        }

        host = parts[0];
        return port is > 0 and <= 65535;
    }

    private async Task QueuePrefetchIfNeededAsync(
        string queueUuid,
        QueueInitializationContext queueContext,
        EngineAudioPostDownloadHelper.EngineTrackContext? trackContext)
    {
        _activityLog.Info($"Download start: {queueUuid} engine=apple");
        if (trackContext == null || queueContext.VideoPayload)
        {
            return;
        }

        var expectedOutputPath = !string.IsNullOrWhiteSpace(trackContext.PathResult.WritePath)
            ? DownloadPathResolver.ResolveIoPath(trackContext.PathResult.WritePath)
            : Path.Join(
                DownloadPathResolver.ResolveIoPath(trackContext.PathResult.FilePath),
                trackContext.PathResult.Filename);
        await QueueParallelPostDownloadPrefetchAsync(
            queueUuid,
            trackContext,
            queueContext.Payload,
            queueContext.Settings,
            expectedOutputPath);
    }

    private async Task<AppleDownloadResult> ExecuteDownloadWithFallbackAsync(
        string queueUuid,
        AppleQueueItem payload,
        AppleDownloadRequest request,
        CancellationToken itemToken)
    {
        var result = await _downloadService.DownloadAsync(request, itemToken);
        if (!result.Success && CanFallbackToAacStereo(request) && ShouldUseInEngineAppleAacFallback(payload))
        {
            _logger.LogWarning("Apple download failed for {QueueUuid}, retrying with AAC stereo. Error: {Message}", queueUuid, result.Message);
            ApplyAacStereoFallback(request);
            result = await _downloadService.DownloadAsync(request, itemToken);
        }

        return result;
    }

    private async Task HandleDownloadFailureAsync(
        DownloadQueueItem next,
        AppleQueueItem payload,
        string reason,
        CancellationToken stoppingToken,
        CancellationToken itemToken,
        string? quality = null)
    {
        if (string.IsNullOrWhiteSpace(quality))
        {
            _activityLog.Warn($"Download failed (engine=apple): {next.QueueUuid} {reason}");
        }
        else
        {
            _activityLog.Warn($"Download failed (engine=apple quality={quality}): {next.QueueUuid} {reason}");
        }

        if (await TryAdvanceFallbackAsync(next, payload, stoppingToken))
        {
            return;
        }

        payload.ErrorMessage = reason;
        payload.Status = AppleDownloadStatus.Failed;
        await _queueRepository.UpdateStatusAsync(next.QueueUuid, FailedStatus, reason, cancellationToken: itemToken);
        _deezspotagListener.Send(UpdateQueueEvent, payload.ToQueuePayload());
        ScheduleRetryIfEligible(next.QueueUuid, reason);
    }

    private async Task<bool> TryAdvanceFallbackAsync(
        DownloadQueueItem next,
        AppleQueueItem payload,
        CancellationToken stoppingToken)
    {
        var advanced = await _fallbackCoordinator.TryAdvanceAsync(
            next.QueueUuid,
            next.Engine,
            payload,
            stoppingToken);
        if (!advanced)
        {
            return false;
        }

        _activityLog.Info($"Fallback advanced: {next.QueueUuid} -> {payload.Engine} (auto_index={payload.AutoIndex})");
        if (!payload.FallbackQueuedExternally)
        {
            _deezspotagListener.SendAddedToQueue(payload.ToQueuePayload());
        }

        return true;
    }

    private void ApplyDownloadQualityMetadata(AppleQueueItem payload, AppleDownloadResult result, string queueUuid)
    {
        if (result.IsVideo)
        {
            payload.Quality = "Video";
            payload.VideoResolution = string.IsNullOrWhiteSpace(result.VideoResolutionTier) ? payload.VideoResolution : result.VideoResolutionTier;
            payload.VideoHdr = result.VideoHdr;
            payload.VideoAudioProfile = string.IsNullOrWhiteSpace(result.VideoAudioProfile) ? payload.VideoAudioProfile : result.VideoAudioProfile;
            _logger.LogInformation(
                "Apple video profile: resolution={Resolution} hdr={Hdr} audio={Audio} for {QueueUuid}",
                string.IsNullOrWhiteSpace(payload.VideoResolution) ? UnknownValue : payload.VideoResolution,
                payload.VideoHdr,
                string.IsNullOrWhiteSpace(payload.VideoAudioProfile) ? UnknownValue : payload.VideoAudioProfile,
                queueUuid);
            return;
        }

        if (string.IsNullOrWhiteSpace(result.QualityLabel))
        {
            return;
        }

        payload.Quality = result.QualityLabel;
        _logger.LogInformation("Apple download quality: {Quality} for {QueueUuid}", result.QualityLabel, queueUuid);
    }

    private async Task<string> ApplyPostDownloadSettingsSafelyAsync(
        string queueUuid,
        QueueInitializationContext queueContext,
        EngineAudioPostDownloadHelper.EngineTrackContext? trackContext,
        string outputPath,
        CancellationToken itemToken)
    {
        if (trackContext != null && !queueContext.VideoPayload)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                return await ApplyPostDownloadSettingsAsync(
                    trackContext,
                    queueContext.Payload,
                    outputPath,
                    queueContext.Settings,
                    scope.ServiceProvider,
                    itemToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Apple post-download settings failed for {QueueUuid}", queueUuid);
                return outputPath;
            }
        }

        if (!queueContext.VideoPayload)
        {
            return outputPath;
        }

        try
        {
            using var scope = _serviceProvider.CreateScope();
            return await ApplyVideoPostDownloadSettingsAsync(
                queueContext.Payload,
                outputPath,
                queueContext.Settings,
                scope.ServiceProvider,
                itemToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Apple video post-download settings failed for {QueueUuid}", queueUuid);
            return outputPath;
        }
    }

    private async Task<bool> PersistOutputMetadataIfPresentAsync(
        string queueUuid,
        AppleQueueItem payload,
        string outputPath,
        CancellationToken itemToken)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return false;
        }

        var finalSize = AppleQueueItemHelpers.TryGetFileSizeMb(outputPath);
        if (finalSize <= 0 || !AppleQueueItemHelpers.OutputExists(outputPath))
        {
            return false;
        }

        await AppleQueueItemHelpers.UpdateQueuePayloadAsync(_queueRepository, queueUuid, payload, outputPath, finalSize, itemToken);
        return true;
    }

    private async Task MarkQueueItemCompletedAsync(
        string queueUuid,
        AppleQueueItem payload,
        CancellationToken itemToken)
    {
        payload.Progress = 100;
        payload.Downloaded = Math.Max(payload.Size, 1);
        payload.Status = AppleDownloadStatus.Completed;
        await _queueRepository.UpdateStatusAsync(queueUuid, CompletedStatus, downloaded: 1, progress: 100, cancellationToken: itemToken);
        _deezspotagListener.Send(UpdateQueueEvent, payload.ToQueuePayload());
        _retryScheduler.Clear(queueUuid);
    }

    private async Task HandleCanceledQueueItemAsync(string queueUuid)
    {
        var current = await _queueRepository.GetByUuidAsync(queueUuid, CancellationToken.None);
        var status = current?.Status ?? CancelledStatus;
        if (status is CompletedStatus or FailedStatus)
        {
            return;
        }

        if (_cancellationRegistry.WasUserPaused(queueUuid))
        {
            await _queueRepository.UpdateStatusAsync(queueUuid, PausedStatus, cancellationToken: CancellationToken.None);
            _deezspotagListener.Send(UpdateQueueEvent, new { uuid = queueUuid, status = PausedStatus });
            return;
        }

        if (_cancellationRegistry.WasUserCanceled(queueUuid))
        {
            await _queueRepository.UpdateStatusAsync(queueUuid, CanceledStatus, cancellationToken: CancellationToken.None);
            _deezspotagListener.Send(UpdateQueueEvent, new { uuid = queueUuid, status = CanceledStatus });
            return;
        }

        await _queueRepository.UpdateStatusAsync(queueUuid, CancelledStatus, "Cancelled", cancellationToken: CancellationToken.None);
        ScheduleRetryIfEligible(queueUuid, CancelledStatus);
    }

    private void ScheduleRetryIfEligible(string queueUuid, string? reason)
    {
        if (!ShouldScheduleRetry(reason))
        {
            _activityLog.Info($"Auto-retry skipped (engine=apple): {queueUuid} {reason}");
            return;
        }

        _retryScheduler.ScheduleRetry(queueUuid, EngineName, reason ?? string.Empty);
    }

    private static bool ShouldScheduleRetry(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return true;
        }

        var normalized = reason.Trim().ToLowerInvariant();
        if (normalized.Contains("invalid payload", StringComparison.Ordinal))
        {
            return false;
        }

        if (normalized.Contains("apple video key acquisition failed", StringComparison.Ordinal)
            || normalized.Contains("widevine key acquisition failed", StringComparison.Ordinal))
        {
            return false;
        }

        if (normalized.Contains("apple video mux completed without an audio track", StringComparison.Ordinal)
            || normalized.Contains("apple video mux failed", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static EngineAudioPostDownloadHelper.EngineTrackContext BuildTrackContext(
        AppleQueueItem payload,
        DeezSpoTagSettings settings,
        EnhancedPathTemplateProcessor pathProcessor,
        string? appleId)
    {
        return EngineAudioPostDownloadHelper.BuildTrackContext(
            payload,
            settings,
            pathProcessor,
            AppleProvider,
            appleId,
            static queueItem => queueItem.CollectionType?.ToLowerInvariant() switch
            {
                "artist" => "artist",
                PlaylistType => PlaylistType,
                "album" => "album",
                _ => "track"
            },
            static (track, queueItem) =>
            {
                if (queueItem is AppleQueueItem { HasAppleDigitalMaster: true })
                {
                    track.Urls["apple_digital_master"] = "1";
                }
            });
    }

    private async Task<string?> ResolveVideoDestinationRootAsync(long? destinationFolderId, CancellationToken cancellationToken)
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

    private async Task<string?> ResolveAppleIdForStorefrontAsync(
        AppleQueueItem payload,
        DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
    {
        var appleId = ResolveAppleId(payload);
        if (string.IsNullOrWhiteSpace(appleId))
        {
            return appleId;
        }

        var storefront = string.IsNullOrWhiteSpace(settings.AppleMusic?.Storefront) ? "us" : settings.AppleMusic!.Storefront;
        var mediaUserToken = settings.AppleMusic?.MediaUserToken;
        var isVideo = AppleVideoClassifier.IsVideo(payload.SourceUrl, payload.CollectionType, payload.ContentType);

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var catalog = scope.ServiceProvider.GetRequiredService<AppleMusicCatalogService>();
            if (isVideo)
            {
                using var doc = await catalog.GetMusicVideoAsync(appleId, storefront, DefaultLanguage, cancellationToken);
                var resolved = TryExtractAppleIdFromCatalog(doc);
                return string.IsNullOrWhiteSpace(resolved) ? appleId : resolved;
            }

            using (var doc = await catalog.GetSongAsync(appleId, storefront, DefaultLanguage, cancellationToken, mediaUserToken))
            {
                var resolved = TryExtractAppleIdFromCatalog(doc);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    return resolved;
                }
            }

            if (!string.IsNullOrWhiteSpace(payload.Isrc))
            {
                using var isrcDoc = await catalog.GetSongByIsrcAsync(payload.Isrc, storefront, DefaultLanguage, cancellationToken, mediaUserToken);
                var resolved = TryExtractAppleIdFromCatalog(isrcDoc);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    return resolved;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            return appleId;
        }

        return appleId;
    }

    private static string? TryExtractAppleIdFromCatalog(System.Text.Json.JsonDocument? doc)
    {
        if (doc == null)
        {
            return null;
        }

        var root = doc.RootElement;
        if (root.TryGetProperty("data", out var dataArr)
            && dataArr.ValueKind == System.Text.Json.JsonValueKind.Array
            && dataArr.GetArrayLength() > 0)
        {
            return dataArr[0].TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        }

        if (root.TryGetProperty("results", out var results) && results.ValueKind == System.Text.Json.JsonValueKind.Object &&
            results.TryGetProperty("songs", out var songs) && songs.ValueKind == System.Text.Json.JsonValueKind.Object &&
            songs.TryGetProperty("data", out var songData) && songData.ValueKind == System.Text.Json.JsonValueKind.Array &&
            songData.GetArrayLength() > 0)
        {
            return songData[0].TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        }

        return null;
    }

    private static bool IsWrapperRequired(AppleDownloadRequest request)
    {
        if (request.IsVideo || AppleVideoClassifier.IsVideoUrl(request.ServiceUrl))
        {
            return false;
        }

        if (!request.GetM3u8FromDevice)
        {
            return false;
        }

        var profile = request.PreferredProfile?.ToLowerInvariant() ?? string.Empty;
        if (profile.Contains(AlacKeyword, StringComparison.OrdinalIgnoreCase) || profile.Contains(AtmosKeyword, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var aacType = request.AacType?.ToLowerInvariant() ?? string.Empty;
        return aacType.Contains("binaural", StringComparison.OrdinalIgnoreCase)
            || aacType.Contains("downmix", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVideoPayload(AppleQueueItem payload)
    {
        if (payload == null)
        {
            return false;
        }

        return AppleVideoClassifier.IsVideo(payload.SourceUrl, payload.CollectionType, payload.ContentType);
    }

    private static bool CanFallbackToAacStereo(AppleDownloadRequest request)
    {
        var profile = request.PreferredProfile?.ToLowerInvariant() ?? string.Empty;
        if (profile.Contains(AtmosKeyword, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (request.IsVideo || AppleVideoClassifier.IsVideoUrl(request.ServiceUrl))
        {
            return false;
        }

        return profile.Contains(AacKeyword, StringComparison.OrdinalIgnoreCase)
               || profile.Contains(AlacKeyword, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldUseInEngineAppleAacFallback(AppleQueueItem payload)
    {
        // Preserve global AUTO fallback order when multiple engines are present.
        return EngineFallbackPlanPolicy.ShouldUseInEngineFallback(payload, EngineName);
    }

    private static void ApplyAacStereoFallback(AppleDownloadRequest request)
    {
        request.PreferredProfile = "AAC";
        request.AacType = AacLcType;
        request.GetM3u8FromDevice = false;
    }

    private async Task<string> ApplyPostDownloadSettingsAsync(
        EngineAudioPostDownloadHelper.EngineTrackContext context,
        AppleQueueItem payload,
        string outputPath,
        DeezSpoTagSettings settings,
        IServiceProvider scope,
        CancellationToken cancellationToken)
    {
        var imageDownloader = scope.GetRequiredService<ImageDownloader>();
        var audioTagger = scope.GetRequiredService<AudioTagger>();
        var spotifyArtworkResolver = scope.GetService<ISpotifyArtworkResolver>();
        var spotifyIdResolver = scope.GetService<ISpotifyIdResolver>();
        var httpClientFactory = scope.GetService<IHttpClientFactory>();
        var appleCatalog = scope.GetService<AppleMusicCatalogService>();
        var deezerClient = scope.GetService<DeezerClient>();
        var services = new CoverFallbackServices(appleCatalog, deezerClient, spotifyIdResolver, spotifyArtworkResolver, httpClientFactory);

        var isPlaylist = string.Equals(payload.CollectionType, PlaylistType, StringComparison.OrdinalIgnoreCase);
        var allowPlaylistCover = !isPlaylist || settings.DlAlbumcoverForPlaylist;
        var coverUrl = await ResolveCoverUrlWithFallbackAsync(
            payload,
            settings,
            services,
            cancellationToken);

        var isAppleCover = IsAppleArtworkUrl(coverUrl);

        if (allowPlaylistCover && settings.Tags?.Cover == true && !string.IsNullOrWhiteSpace(coverUrl) && context.Track.Album != null)
        {
            var embedSize = settings.EmbedMaxQualityCover ? settings.LocalArtworkSize : settings.EmbeddedArtworkSize;
            var embedUrl = coverUrl;
            string extension;
            if (isAppleCover)
            {
                extension = $".{AppleQueueHelpers.GetAppleArtworkExtension(coverUrl, AppleQueueHelpers.GetAppleArtworkFormat(settings))}";
            }
            else if (embedUrl.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                extension = ".png";
            }
            else
            {
                extension = ".jpg";
            }
            var embedPath = Path.Join(Path.GetTempPath(), $"apple-embed-{Guid.NewGuid():N}{extension}");
            string? downloaded;
            if (isAppleCover)
            {
                downloaded = await AppleQueueHelpers.DownloadAppleArtworkAsync(
                    imageDownloader,
                    new AppleQueueHelpers.AppleArtworkDownloadRequest
                    {
                        RawUrl = embedUrl,
                        OutputPath = embedPath,
                        Settings = settings,
                        Size = embedSize,
                        Overwrite = settings.OverwriteFile,
                        PreferMaxQuality = true,
                        Logger = _logger
                    },
                    cancellationToken);
            }
            else
            {
                var sizedUrl = ApplyArtworkSize(embedUrl, embedSize);
                downloaded = await imageDownloader.DownloadImageAsync(
                    sizedUrl,
                    embedPath,
                    settings.OverwriteFile,
                    true,
                    cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(downloaded))
            {
                context.Track.Album.EmbeddedCoverPath = downloaded;
            }
        }

        try
        {
            await audioTagger.TagTrackAsync(outputPath, context.Track, settings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Apple tagging failed for {Path}", outputPath);
        }

        UpdateAudioPayloadFiles(payload, context.PathResult, outputPath);
        return outputPath;
    }

    private async Task QueueParallelPostDownloadPrefetchAsync(
        string queueUuid,
        EngineAudioPostDownloadHelper.EngineTrackContext context,
        AppleQueueItem payload,
        DeezSpoTagSettings settings,
        string expectedOutputPath)
    {
        var isPlaylist = string.Equals(payload.CollectionType, PlaylistType, StringComparison.OrdinalIgnoreCase);
        var allowPlaylistCover = !isPlaylist || settings.DlAlbumcoverForPlaylist;
        var shouldFetchArtwork = (allowPlaylistCover && settings.SaveArtwork)
            || (allowPlaylistCover && settings.SaveAnimatedArtwork)
            || settings.SaveArtworkArtist;
        var shouldFetchLyrics = ShouldSaveLyrics(settings);
        var shouldQueue = shouldFetchArtwork || shouldFetchLyrics;
        if (!shouldQueue)
        {
            return;
        }

        var prefetchPaths = EngineAudioPostDownloadHelper.BuildPrefetchPathContext(queueUuid, context, expectedOutputPath);

        QueuePrefetchStatusHelper.Send(
            _deezspotagListener,
            prefetchPaths.QueueUuid,
            shouldFetchArtwork ? FetchingState : SkippedState,
            shouldFetchLyrics ? FetchingState : SkippedState);

        var work = new PrefetchWorkContext
        {
            QueueUuid = prefetchPaths.QueueUuid,
            Context = context,
            Payload = payload,
            Settings = settings,
            ExpectedOutputPath = expectedOutputPath,
            ExpectedBaseName = prefetchPaths.ExpectedBaseName,
            FileDir = prefetchPaths.FileDir,
            CoverPath = prefetchPaths.CoverPath,
            ArtistPath = prefetchPaths.ArtistPath,
            ExtrasPath = prefetchPaths.ExtrasPath,
            AllowPlaylistCover = allowPlaylistCover,
            ShouldFetchArtwork = shouldFetchArtwork,
            ShouldFetchLyrics = shouldFetchLyrics
        };

        await _postDownloadTaskScheduler.EnqueueAsync(
            prefetchPaths.QueueUuid,
            Engine,
            (provider, token) => RunParallelPostDownloadPrefetchWorkAsync(provider, work, token),
            CancellationToken.None);
    }

    private async Task RunParallelPostDownloadPrefetchWorkAsync(
        IServiceProvider provider,
        PrefetchWorkContext work,
        CancellationToken token)
    {
        var imageDownloader = provider.GetRequiredService<ImageDownloader>();
        var pathProcessor = provider.GetRequiredService<EnhancedPathTemplateProcessor>();
        var spotifyArtworkResolver = provider.GetService<ISpotifyArtworkResolver>();
        var spotifyIdResolver = provider.GetService<ISpotifyIdResolver>();
        var httpClientFactory = provider.GetService<IHttpClientFactory>();
        var appleCatalog = provider.GetService<AppleMusicCatalogService>();
        var deezerClient = provider.GetService<DeezerClient>();
        var services = new CoverFallbackServices(appleCatalog, deezerClient, spotifyIdResolver, spotifyArtworkResolver, httpClientFactory);
        var preferMaxQualityCover = work.Settings.EmbedMaxQualityCover;
        var appleArtworkSize = AppleQueueHelpers.GetAppleArtworkSize(work.Settings);
        var artworkStatus = work.ShouldFetchArtwork ? FetchingState : SkippedState;
        var lyricsStatus = work.ShouldFetchLyrics ? FetchingState : SkippedState;
        var lyricsType = string.Empty;

        var coverUrl = await ResolveCoverUrlWithFallbackAsync(
            work.Payload,
            work.Settings,
            services,
            token);
        var isAppleCover = IsAppleArtworkUrl(coverUrl);

        var artworkTask = work.ShouldFetchArtwork
            ? Task.Run(
                () => RunArtworkPrefetchAsync(
                    new ArtworkPrefetchContext
                    {
                        Work = work,
                        CoverUrl = coverUrl,
                        IsAppleCover = isAppleCover,
                        PreferMaxQualityCover = preferMaxQualityCover,
                        AppleArtworkSize = appleArtworkSize,
                        ImageDownloader = imageDownloader,
                        PathProcessor = pathProcessor,
                        AppleCatalog = appleCatalog,
                        DeezerClient = deezerClient,
                        SpotifyArtworkResolver = spotifyArtworkResolver,
                        HttpClientFactory = httpClientFactory,
                        GetArtworkStatus = () => artworkStatus,
                        GetLyricsStatus = () => lyricsStatus,
                        SetArtworkStatus = status => artworkStatus = status
                    },
                    token),
                token)
            : Task.CompletedTask;

        var lyricsTask = work.ShouldFetchLyrics
            ? Task.Run(
                () => RunLyricsPrefetchAsync(
                    new LyricsPrefetchContext
                    {
                        Work = work,
                        GetArtworkStatus = () => artworkStatus,
                        GetLyricsStatus = () => lyricsStatus,
                        SetLyricsStatus = status => lyricsStatus = status,
                        SetLyricsType = type => lyricsType = type,
                        GetLyricsType = () => lyricsType
                    },
                    token),
                token)
            : Task.CompletedTask;

        await Task.WhenAll(artworkTask, lyricsTask);
    }

    private async Task RunArtworkPrefetchAsync(ArtworkPrefetchContext context, CancellationToken token)
    {
        try
        {
            var coverName = context.PathProcessor.GenerateAlbumName(
                context.Work.Settings.CoverImageTemplate,
                context.Work.Context.Track.Album,
                context.Work.Settings,
                context.Work.Context.Track.Playlist);

            if (context.Work.AllowPlaylistCover && context.Work.Settings.SaveArtwork && !string.IsNullOrWhiteSpace(context.CoverUrl))
            {
                Directory.CreateDirectory(context.Work.CoverPath);
                await SavePrefetchCoverArtworkAsync(
                    new CoverSaveContext(
                        context.Work.Settings,
                        context.CoverUrl,
                        context.Work.CoverPath,
                        coverName,
                        context.IsAppleCover,
                        context.PreferMaxQualityCover,
                        context.AppleArtworkSize,
                        context.ImageDownloader),
                    token);
            }

            if (context.Work.AllowPlaylistCover && context.Work.Settings.SaveAnimatedArtwork && context.AppleCatalog != null && context.HttpClientFactory != null)
            {
                await SaveAnimatedPrefetchArtworkAsync(context.Work.Settings, context.Work.Payload, context.Work.CoverPath, coverName, context.AppleCatalog, context.HttpClientFactory, token);
            }

            if (context.Work.Settings.SaveArtworkArtist)
            {
                await SaveArtistPrefetchArtworkAsync(
                    new ArtistPrefetchSaveContext(
                        context.Work.Settings,
                        context.Work.Payload,
                        context.Work.Context.Track,
                        context.Work.ArtistPath,
                        context.AppleArtworkSize,
                        context.PreferMaxQualityCover,
                        context.ImageDownloader,
                        context.PathProcessor,
                        context.AppleCatalog,
                        context.DeezerClient,
                        context.SpotifyArtworkResolver,
                        context.HttpClientFactory),
                    token);
            }

            context.SetArtworkStatus(CompletedStatus);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.SetArtworkStatus(FailedStatus);
            _logger.LogWarning(ex, "Apple artwork prefetch failed for {Path}", context.Work.ExpectedOutputPath);
        }
        finally
        {
            QueuePrefetchStatusHelper.Send(_deezspotagListener, context.Work.QueueUuid, context.GetArtworkStatus(), context.GetLyricsStatus());
        }
    }

    private async Task RunLyricsPrefetchAsync(LyricsPrefetchContext context, CancellationToken token)
    {
        try
        {
            Directory.CreateDirectory(context.Work.FileDir);
            var paths = (
                FilePath: context.Work.FileDir,
                Filename: context.Work.ExpectedBaseName,
                ExtrasPath: context.Work.ExtrasPath,
                CoverPath: context.Work.CoverPath,
                ArtistPath: context.Work.ArtistPath
            );
            var lyrics = await _lyricsService.ResolveLyricsAsync(context.Work.Context.Track, context.Work.Settings, token);
            var resolvedType = LyricsPrefetchTypeHelper.ResolveFromLyrics(lyrics);
            if (!string.IsNullOrWhiteSpace(resolvedType))
            {
                context.SetLyricsType(resolvedType);
                QueuePrefetchStatusHelper.Send(_deezspotagListener, context.Work.QueueUuid, context.GetArtworkStatus(), FetchingState, resolvedType);
            }

            if (lyrics != null && lyrics.IsLoaded())
            {
                await _lyricsService.SaveLyricsAsync(lyrics, context.Work.Context.Track, paths, context.Work.Settings, token);
                var savedLyricsType = LyricsPrefetchTypeHelper.ResolveSavedLyricsType(context.Work.FileDir, context.Work.ExpectedBaseName);
                if (!string.IsNullOrWhiteSpace(savedLyricsType))
                {
                    context.SetLyricsType(savedLyricsType);
                }
            }

            context.SetLyricsStatus(string.IsNullOrWhiteSpace(context.GetLyricsType()) ? NoLyricsStatus : CompletedStatus);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.SetLyricsStatus(FailedStatus);
            _logger.LogWarning(ex, "Apple lyrics download failed for {Path}", context.Work.ExpectedOutputPath);
        }
        finally
        {
            QueuePrefetchStatusHelper.Send(_deezspotagListener, context.Work.QueueUuid, context.GetArtworkStatus(), context.GetLyricsStatus(), context.GetLyricsType());
        }
    }

    private async Task SavePrefetchCoverArtworkAsync(CoverSaveContext context, CancellationToken token)
    {
        if (context.IsAppleCover)
        {
            foreach (var format in AppleQueueHelpers.GetArtworkOutputFormats(context.Settings))
            {
                var targetPath = Path.Join(context.CoverPath, $"{context.CoverName}.{format}");
                await AppleQueueHelpers.DownloadAppleArtworkAsync(
                    context.ImageDownloader,
                    new AppleQueueHelpers.AppleArtworkDownloadRequest
                    {
                        RawUrl = context.CoverUrl,
                        OutputPath = targetPath,
                        Settings = context.Settings,
                        Size = context.AppleArtworkSize,
                        Overwrite = context.Settings.OverwriteFile,
                        PreferMaxQuality = context.PreferMaxQualityCover,
                        Logger = _logger
                    },
                    token);
            }
            return;
        }

        var formats = (context.Settings.LocalArtworkFormat ?? "jpg")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var format in formats)
        {
            var ext = format.Equals("png", StringComparison.OrdinalIgnoreCase) ? "png" : "jpg";
            var targetPath = Path.Join(context.CoverPath, $"{context.CoverName}.{ext}");
            await context.ImageDownloader.DownloadImageAsync(
                context.CoverUrl,
                targetPath,
                context.Settings.OverwriteFile,
                context.PreferMaxQualityCover,
                token);
        }
    }

    private async Task SaveAnimatedPrefetchArtworkAsync(
        DeezSpoTagSettings settings,
        AppleQueueItem payload,
        string coverPath,
        string coverName,
        AppleMusicCatalogService appleCatalog,
        IHttpClientFactory httpClientFactory,
        CancellationToken token)
    {
        var storefront = string.IsNullOrWhiteSpace(settings.AppleMusic?.Storefront) ? "us" : settings.AppleMusic!.Storefront;
        var appleId = ResolveAppleId(payload);
        var savedAnimated = await AppleQueueHelpers.SaveAnimatedArtworkAsync(
            appleCatalog,
            httpClientFactory,
            new AppleQueueHelpers.AnimatedArtworkSaveRequest
            {
                AppleId = appleId,
                Title = payload.Title,
                Artist = payload.Artist,
                Album = payload.Album,
                BaseFileName = coverName,
                Storefront = storefront,
                MaxResolution = settings.Video.AppleMusicVideoMaxResolution,
                OutputDir = coverPath,
                Logger = _logger,
                CollectionType = payload.CollectionType,
                CollectionId = appleId
            },
            token);
        if (savedAnimated)
        {
            _activityLog.Info($"Animated artwork saved: {coverPath}");
        }
    }

    private async Task SaveArtistPrefetchArtworkAsync(ArtistPrefetchSaveContext context, CancellationToken token)
    {
        var artistImageUrl = await DownloadEngineArtworkHelper.ResolveArtistImageUrlAsync(
            new DownloadEngineArtworkHelper.ArtistImageResolveRequest(
                context.AppleCatalog,
                context.HttpClientFactory,
                context.Settings,
                context.DeezerClient,
                context.SpotifyArtworkResolver,
                context.Payload.AppleId,
                context.Payload.DeezerId,
                context.Payload.SpotifyId,
                context.Payload.Artist,
                _logger),
            token);

        if (string.IsNullOrWhiteSpace(artistImageUrl))
        {
            return;
        }

        await DownloadEngineArtworkHelper.SaveArtistArtworkAsync(
            new DownloadEngineArtworkHelper.SaveArtistArtworkRequest(
                context.ImageDownloader,
                context.PathProcessor,
                context.ArtistPath,
                artistImageUrl,
                context.Settings,
                context.Track,
                context.AppleArtworkSize,
                context.PreferMaxQualityCover,
                _logger,
                true),
            token);
    }

    private static void UpdateAudioPayloadFiles(AppleQueueItem payload, PathGenerationResult pathResult, string outputPath)
    {
        var result = QueuePayloadFileHelper.BuildAudioFiles(pathResult, outputPath);
        payload.Files = result.Files;
        payload.LyricsStatus = result.LyricsStatus;
    }

    private static string ApplyArtworkSize(string url, int size)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        var sizeStr = $"{size}x{size}";
        if (url.Contains("{w}x{h}", StringComparison.OrdinalIgnoreCase))
        {
            return url.Replace("{w}x{h}", sizeStr, StringComparison.OrdinalIgnoreCase);
        }

        if (url.Contains("{w}", StringComparison.OrdinalIgnoreCase) || url.Contains("{h}", StringComparison.OrdinalIgnoreCase))
        {
            return url
                .Replace("{w}", size.ToString(), StringComparison.OrdinalIgnoreCase)
                .Replace("{h}", size.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        return url;
    }

    private static bool IsAppleArtworkUrl(string? url)
        => !string.IsNullOrWhiteSpace(url)
           && url.Contains("mzstatic.com", StringComparison.OrdinalIgnoreCase);

    private async Task<string?> ResolveCoverUrlWithFallbackAsync(
        AppleQueueItem payload,
        DeezSpoTagSettings settings,
        CoverFallbackServices services,
        CancellationToken cancellationToken)
    {
        foreach (var fallback in ArtworkFallbackHelper.ResolveOrder(settings))
        {
            var coverUrl = fallback switch
            {
                AppleProvider => await TryResolveAppleCoverUrlAsync(payload, settings, services.AppleCatalog, services.HttpClientFactory, cancellationToken),
                DeezerProvider => await ArtworkFallbackHelper.TryResolveDeezerCoverAsync(
                    services.DeezerClient,
                    payload.DeezerId,
                    settings.LocalArtworkSize,
                    _logger,
                    cancellationToken,
                    payload.Album),
                SpotifyProvider => await ArtworkFallbackHelper.TryResolveSpotifyCoverAsync(
                    services.SpotifyIdResolver,
                    services.SpotifyArtworkResolver,
                    payload.Title,
                    payload.Artist,
                    payload.Album,
                    payload.Isrc,
                    cancellationToken),
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(coverUrl))
            {
                return coverUrl;
            }
        }

        return null;
    }

    private async Task<string?> TryResolveAppleCoverUrlAsync(
        AppleQueueItem payload,
        DeezSpoTagSettings settings,
        AppleMusicCatalogService? appleCatalog,
        IHttpClientFactory? httpClientFactory,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(payload.Cover) && IsAppleArtworkUrl(payload.Cover))
        {
            return payload.Cover;
        }

        var appleId = ResolveAppleId(payload);
        return await ArtworkFallbackHelper.TryResolveAppleCoverAsync(
            appleCatalog,
            httpClientFactory,
            new ArtworkFallbackHelper.AppleCoverLookupRequest(
                settings,
                appleId,
                payload.Title,
                payload.Artist,
                payload.Album),
            _logger,
            cancellationToken);
    }

    private static async Task<string> ApplyVideoPostDownloadSettingsAsync(
        AppleQueueItem payload,
        string outputPath,
        DeezSpoTagSettings settings,
        IServiceProvider scope,
        CancellationToken cancellationToken)
    {
        var downloadMoveService = scope.GetRequiredService<DownloadMoveService>();
        var outputDir = Path.GetDirectoryName(outputPath) ?? string.Empty;
        var displayPath = DownloadPathResolver.NormalizeDisplayPath(outputPath);

        payload.Files = new List<Dictionary<string, object>>
        {
            new()
            {
                ["path"] = displayPath,
                ["albumPath"] = DownloadPathResolver.NormalizeDisplayPath(outputDir),
                ["artistPath"] = DownloadPathResolver.NormalizeDisplayPath(outputDir)
            }
        };

        var moveObject = BuildVideoMoveObject(payload, outputDir, outputPath);
        var moveResult = await downloadMoveService.MoveToLibraryAsync(
            moveObject,
            settings,
            Array.Empty<string>(),
            cancellationToken);
        if (moveResult?.MovedPaths.TryGetValue(DownloadPathResolver.NormalizeDisplayPath(outputPath), out var movedPath) == true)
        {
            outputPath = movedPath;
            payload.Files[0]["path"] = movedPath;
        }

        return outputPath;
    }

    private async Task TryPopulateVideoMetadataAsync(AppleQueueItem payload, string queueUuid, CancellationToken cancellationToken)
    {
        if (!NeedsVideoMetadataHydration(payload))
        {
            return;
        }

        var settings = _settingsService.LoadSettings();
        var storefront = string.IsNullOrWhiteSpace(settings.AppleMusic.Storefront) ? "us" : settings.AppleMusic.Storefront;
        var appleId = ResolveAppleId(payload);
        if (string.IsNullOrWhiteSpace(appleId))
        {
            return;
        }

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var catalog = scope.ServiceProvider.GetRequiredService<AppleMusicCatalogService>();
            using var doc = await catalog.GetMusicVideoAsync(appleId, storefront, DefaultLanguage, cancellationToken);
            if (!TryGetVideoAttributes(doc.RootElement, out var attrs))
            {
                return;
            }

            ApplyVideoMetadata(payload, attrs);

            var json = System.Text.Json.JsonSerializer.Serialize(payload);
            await _queueRepository.UpdatePayloadAsync(queueUuid, json, cancellationToken);
            _deezspotagListener.Send("updateQueue", payload.ToQueuePayload());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Apple MV metadata lookup failed for {QueueUuid}", queueUuid);
        }
    }

    private static bool NeedsVideoMetadataHydration(AppleQueueItem payload)
    {
        return string.IsNullOrWhiteSpace(payload.Title)
               || string.IsNullOrWhiteSpace(payload.Artist)
               || string.IsNullOrWhiteSpace(payload.Cover);
    }

    private static void ApplyVideoMetadata(AppleQueueItem payload, AppleCatalogVideoAttributes attrs)
    {
        payload.Title = string.IsNullOrWhiteSpace(payload.Title) ? attrs.Name : payload.Title;
        payload.Artist = string.IsNullOrWhiteSpace(payload.Artist) ? attrs.ArtistName : payload.Artist;
        payload.Album = string.IsNullOrWhiteSpace(payload.Album) ? attrs.AlbumName : payload.Album;
        payload.AlbumArtist = string.IsNullOrWhiteSpace(payload.AlbumArtist) ? attrs.ArtistName : payload.AlbumArtist;
        payload.Cover = string.IsNullOrWhiteSpace(payload.Cover) ? attrs.ArtworkUrl : payload.Cover;
        payload.Isrc = string.IsNullOrWhiteSpace(payload.Isrc) ? attrs.Isrc : payload.Isrc;
        payload.ReleaseDate = string.IsNullOrWhiteSpace(payload.ReleaseDate) ? attrs.ReleaseDate : payload.ReleaseDate;
        if (payload.DurationSeconds == 0 && attrs.DurationSeconds > 0)
        {
            payload.DurationSeconds = attrs.DurationSeconds;
        }

        if (string.IsNullOrWhiteSpace(payload.VideoResolution) && attrs.Has4K)
        {
            payload.VideoResolution = "4K";
        }

        if (attrs.HasHdr)
        {
            payload.VideoHdr = true;
        }

        if (string.IsNullOrWhiteSpace(payload.CollectionType))
        {
            payload.CollectionType = "music-video";
        }
    }

    private static bool TryGetVideoAttributes(System.Text.Json.JsonElement root, out AppleCatalogVideoAttributes attrs)
    {
        return AppleCatalogVideoAttributeParser.TryParse(root, AttributesKey, out attrs);
    }

    private static string? ResolveAppleId(AppleQueueItem payload)
        => AppleIdParser.Resolve(payload.AppleId, payload.SourceUrl);

    private static DeezSpoTagSingle BuildVideoMoveObject(AppleQueueItem payload, string outputDir, string outputPath)
    {
        var normalizedDir = DownloadPathResolver.NormalizeDisplayPath(outputDir);

        return new DeezSpoTagSingle
        {
            UUID = payload.Id,
            Type = "track",
            Id = payload.Id,
            Title = payload.Title,
            Artist = payload.Artist,
            Cover = payload.Cover,
            Size = Math.Max(payload.Size, 1),
            ExtrasPath = normalizedDir,
            DestinationFolderId = payload.DestinationFolderId,
            Files = new List<Dictionary<string, object>>
            {
                new()
                {
                    ["path"] = DownloadPathResolver.NormalizeDisplayPath(outputPath),
                    ["albumPath"] = normalizedDir,
                    ["artistPath"] = normalizedDir
                }
            }
        };
    }

    private static bool ShouldSaveLyrics(DeezSpoTagSettings settings)
    {
        return LyricsSettingsPolicy.CanFetchLyrics(settings);
    }

}
