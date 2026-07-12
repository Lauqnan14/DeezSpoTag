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

namespace DeezSpoTag.Services.Download.Apple;

public sealed class AppleEngineProcessor : IQueueEngineProcessor
{
    private const string EngineName = "apple";
    private const string AppleProvider = "apple";
    private const string FailedStatus = "failed";
    private const string CompletedStatus = "completed";
    private const string PausedStatus = "paused";
    private const string CanceledStatus = "canceled";
    private const string CancelledStatus = "cancelled";
    private const string InvalidPayloadMessage = "Invalid payload";
    private const string UpdateQueueEvent = "updateQueue";
    private const string DefaultLanguage = "en-US";
    private const string AttributesKey = "attributes";
    private const string UnknownValue = "unknown";
    private const string PlaylistType = "playlist";
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
    private readonly AppleExternalToolRunner _toolRunner;
    private readonly IServiceProvider _serviceProvider;
    private readonly EngineFallbackCoordinator _fallbackCoordinator;
    private readonly IActivityLogWriter _activityLog;
    private readonly Utils.LyricsService _lyricsService;
    private readonly IPostDownloadTaskScheduler _postDownloadTaskScheduler;
    private readonly IDownloadTagSettingsResolver _tagSettingsResolver;
    private readonly IFolderConversionSettingsOverlay _folderConversionSettingsOverlay;
    private readonly ILogger<AppleEngineProcessor> _logger;
    private sealed class QueueInitializationContext
    {
        public required AppleQueueItem Payload { get; init; }
        public required DeezSpoTagSettings Settings { get; init; }
        public required bool VideoPayload { get; init; }
        public required string? OriginalDownloadLocation { get; init; }
        public string? ResolvedDownloadTagSource { get; init; }
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
        _toolRunner = serviceProvider.GetRequiredService<AppleExternalToolRunner>();
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
            _logger.LogWarning("Apple download verification failed for {QueueUuid}: {OutputPath}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(next.QueueUuid), DeezSpoTag.Core.Security.LogSanitizer.OneLine(outputPath));
            await HandleDownloadFailureAsync(next, queueContext.Payload, verificationError, stoppingToken, itemToken);
            return queueContext;
        }

        if (!await ValidateFinalAudioOutputAsync(next, queueContext.Payload, outputPath, stoppingToken, itemToken))
        {
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

        await EngineAudioPostDownloadHelper.CancelPrefetchAndWaitAsync(
            next.QueueUuid,
            TimeSpan.FromSeconds(15),
            CancellationToken.None);
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
        await QueueHelperUtils.SendRunningStartedAsync(
            _queueRepository,
            _deezspotagListener,
            next.QueueUuid,
            payload.Downloaded,
            payload.Failed,
            itemToken);

        var settings = _settingsService.LoadSettings();
        var originalDownloadLocation = settings.DownloadLocation;
        var resolvedDownloadTagSource = await DownloadEngineSettingsHelper.ResolveAndApplyProfileAsync(
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
            OriginalDownloadLocation = originalDownloadLocation,
            ResolvedDownloadTagSource = resolvedDownloadTagSource
        };
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
        var trackContext = await BuildTrackContextAsync(
            next.QueueUuid,
            queueContext.Payload,
            queueContext.Settings,
            queueContext.ResolvedDownloadTagSource,
            itemToken);
        var request = AppleRequestBuilder.BuildRequest(queueContext.Payload, queueContext.Settings, progressReporter);
        await TryPopulateAuthorizationTokenAsync(next.QueueUuid, request, itemToken);
        if (trackContext != null && !queueContext.VideoPayload)
        {
            request.OutputDir = trackContext.OutputDir;
            request.FilenameFormat = trackContext.FilenameFormat;
            await QueueHelperUtils.PersistExpectedStagingPathAsync(
                _queueRepository,
                next.QueueUuid,
                queueContext.Payload,
                ResolveExpectedOutputPath(trackContext),
                itemToken);
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
        string? resolvedDownloadTagSource,
        CancellationToken itemToken)
    {
        var appleId = ResolveAppleId(payload);

        using var buildScope = _serviceProvider.CreateScope();
        var pathProcessor = buildScope.ServiceProvider.GetRequiredService<EnhancedPathTemplateProcessor>();
        var context = BuildTrackContext(payload, settings, pathProcessor, appleId);
        var applied = await EngineAudioPostDownloadHelper.ApplyProfileMetadataOverrideAsync(
            new EngineAudioPostDownloadHelper.ProfileMetadataOverrideRequest(
                context.Track,
                payload,
                settings,
                _serviceProvider,
                EngineName,
                resolvedDownloadTagSource,
                _logger,
                itemToken));
        return applied
            ? EngineAudioPostDownloadHelper.BuildTrackContextFromTrack(
                context.Track,
                payload,
                settings,
                pathProcessor,
                ResolveAppleDownloadType)
            : context;
    }

    private static string ResolveExpectedOutputPath(EngineAudioPostDownloadHelper.EngineTrackContext context)
    {
        return !string.IsNullOrWhiteSpace(context.PathResult.WritePath)
            ? context.PathResult.WritePath
            : Path.Join(context.PathResult.FilePath, context.PathResult.Filename);
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
            if (TryApplyWrapperAacFallback(
                next,
                queueContext,
                request,
                wrapperPortReason,
                "wrapper_stream_fallback",
                "Wrapper stream ports unavailable, falling back to AAC stereo.",
                includeReasonInLog: true))
            {
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

        if (TryApplyWrapperAacFallback(
            next,
            queueContext,
            request,
            "Start the Apple wrapper to restore ALAC/Atmos downloads.",
            "wrapper_fallback",
            "Wrapper offline, falling back to AAC stereo.",
            includeReasonInLog: false))
        {
            return true;
        }

        var reason = wrapperStatus.NeedsTwoFactor
            ? "Apple wrapper requires 2FA verification."
            : wrapperStatus.Message;
        await HandleDownloadFailureAsync(next, queueContext.Payload, reason, stoppingToken, itemToken, quality: null);
        return false;
    }

    private bool TryApplyWrapperAacFallback(
        DownloadQueueItem next,
        QueueInitializationContext queueContext,
        AppleDownloadRequest request,
        string warningReason,
        string warningCode,
        string warningMessage,
        bool includeReasonInLog)
    {
        var isVideoRequest = request.IsVideo || queueContext.VideoPayload;
        if (isVideoRequest || !CanFallbackToAacStereo(request) || !ShouldUseInEngineAppleAacFallback(queueContext.Payload))
        {
            return false;
        }

        ApplyAacStereoFallback(request);
        if (_logger.IsEnabled(LogLevel.Information))
        {
            if (includeReasonInLog)
            {
                _logger.LogInformation(
                    "Apple wrapper stream ports unavailable; falling back to AAC stereo for {QueueUuid}. Reason: {Reason}",
                    next.QueueUuid,
                    warningReason);
            }
            else
            {
                _logger.LogInformation(
                    "Apple wrapper unavailable; falling back to AAC stereo for {QueueUuid}.",
                    next.QueueUuid);
            }
        }

        _deezspotagListener.SendDownloadWarn(
            next.QueueUuid,
            new { message = warningMessage },
            warningCode,
            warningReason);
        return true;
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

        await EngineAudioPostDownloadHelper.CancelPrefetchAndWaitAsync(
            next.QueueUuid,
            TimeSpan.FromSeconds(15),
            itemToken);
        if (!EngineAudioPostDownloadHelper.IsFinalDestinationDedupeBlock(reason))
        {
            await _queueRepository.UpdatePrefetchStateAsync(
                next.QueueUuid,
                "[]",
                string.Empty,
                FailedStatus,
                "Audio download failed before prefetched assets could be finalized.",
                itemToken);
        }
        payload.ErrorMessage = reason;
        payload.Status = AppleDownloadStatus.Failed;
        await _queueRepository.UpdateStatusAsync(next.QueueUuid, FailedStatus, reason, cancellationToken: itemToken);
        _deezspotagListener.Send(UpdateQueueEvent, payload.ToQueuePayload());
        if (!EngineAudioPostDownloadHelper.IsFinalDestinationDedupeBlock(reason))
        {
            ScheduleRetryIfEligible(next.QueueUuid, reason);
        }
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
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Apple video profile: resolution={Resolution} hdr={Hdr} audio={Audio} for {QueueUuid}",
                    string.IsNullOrWhiteSpace(payload.VideoResolution) ? UnknownValue : payload.VideoResolution,
                    payload.VideoHdr,
                    string.IsNullOrWhiteSpace(payload.VideoAudioProfile) ? UnknownValue : payload.VideoAudioProfile,
                    queueUuid);            }
            return;
        }

        if (string.IsNullOrWhiteSpace(result.QualityLabel))
        {
            return;
        }

        payload.Quality = result.QualityLabel;
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Apple download quality: {Quality} for {QueueUuid}", result.QualityLabel, queueUuid);        }
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
            using var scope = _serviceProvider.CreateScope();
            return await EngineQueueProcessorHelper.ApplyPostDownloadSettingsWithFallbackAsync(
                EngineName,
                queueUuid,
                outputPath,
                _logger,
                () => ApplyPostDownloadSettingsAsync(
                    trackContext,
                    queueContext.Payload,
                    outputPath,
                    queueContext.Settings,
                    scope.ServiceProvider,
                    itemToken));
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

    private async Task<bool> ValidateFinalAudioOutputAsync(
        DownloadQueueItem next,
        AppleQueueItem payload,
        string outputPath,
        CancellationToken stoppingToken,
        CancellationToken itemToken)
    {
        if (IsVideoPayload(payload))
        {
            return true;
        }

        var validation = await _toolRunner.ValidateDecodableAudioAsync(outputPath, itemToken);
        if (validation.Success)
        {
            validation = await AppleExternalToolRunner.ValidateExpectedDurationAsync(
                outputPath,
                payload.DurationSeconds,
                itemToken);
            if (validation.Success)
            {
                return true;
            }
        }

        _logger.LogWarning(
            "Apple final audio decode validation failed for {QueueUuid}: {OutputPath}. {Reason}",
            DeezSpoTag.Core.Security.LogSanitizer.OneLine(next.QueueUuid),
            DeezSpoTag.Core.Security.LogSanitizer.OneLine(outputPath),
            DeezSpoTag.Core.Security.LogSanitizer.OneLine(validation.Message));
        await HandleDownloadFailureAsync(next, payload, validation.Message, stoppingToken, itemToken);
        return false;
    }

    private async Task MarkQueueItemCompletedAsync(
        string queueUuid,
        AppleQueueItem payload,
        CancellationToken itemToken)
    {
        var prefetchFailure = await EnsureRequiredPrefetchCompletedAsync(queueUuid, itemToken);
        if (!string.IsNullOrWhiteSpace(prefetchFailure))
        {
            _logger.LogWarning(
                "Apple sidecar prefetch failed for {QueueUuid}: {Reason}",
                queueUuid,
                prefetchFailure);
            _activityLog.Warn($"Sidecar prefetch failed (engine={EngineName}): {queueUuid} {prefetchFailure}");
            throw new InvalidOperationException(
                $"{EngineName} required artwork prefetch failed for {queueUuid}: {prefetchFailure}");
        }
        payload.Progress = 100;
        payload.Downloaded = Math.Max(payload.Size, 1);
        payload.Status = AppleDownloadStatus.Completed;
        await _queueRepository.UpdateStatusAsync(queueUuid, CompletedStatus, downloaded: 1, progress: 100, cancellationToken: itemToken);
        _deezspotagListener.Send(UpdateQueueEvent, payload.ToQueuePayload());
        await EngineAudioPostDownloadHelper.UpdateWatchlistTrackStatusAsync(
            payload,
            CompletedStatus,
            _serviceProvider,
            itemToken,
            queueUuid);
        _retryScheduler.Clear(queueUuid);
    }

    private async Task HandleCanceledQueueItemAsync(string queueUuid)
    {
        ClearPrefetchGate(queueUuid);
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

    private async Task<string?> EnsureRequiredPrefetchCompletedAsync(string queueUuid, CancellationToken cancellationToken)
    {
        return await EngineAudioPostDownloadHelper.EnsureArtworkPrefetchCompletedAsync(queueUuid, cancellationToken: cancellationToken);
    }

    private static void ClearPrefetchGate(string queueUuid)
    {
        EngineAudioPostDownloadHelper.ClearPrefetchState(queueUuid);
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
            ResolveAppleDownloadType,
            ConfigureAppleTrack);
    }

    private static string ResolveAppleDownloadType(EngineQueueItemBase queueItem)
        => queueItem.CollectionType?.ToLowerInvariant() switch
        {
            "artist" => "artist",
            PlaylistType => PlaylistType,
            "album" => "album",
            _ => "track"
        };

    private static void ConfigureAppleTrack(Track track, EngineQueueItemBase queueItem)
    {
        if (queueItem is AppleQueueItem { HasAppleDigitalMaster: true })
        {
            track.Urls["apple_digital_master"] = "1";
        }
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
        return await EngineAudioPostDownloadHelper.ApplyPostDownloadSettingsAsync(
            new EngineAudioPostDownloadHelper.PostDownloadSettingsRequest(
                context,
                payload,
                outputPath,
                settings,
                scope,
                EngineName,
                _logger,
                payload.AppleId),
            cancellationToken);
    }

    private async Task QueueParallelPostDownloadPrefetchAsync(
        string queueUuid,
        EngineAudioPostDownloadHelper.EngineTrackContext context,
        AppleQueueItem payload,
        DeezSpoTagSettings settings,
        string expectedOutputPath)
    {
        await EngineAudioPostDownloadHelper.QueueParallelPostDownloadPrefetchAsync(
            new EngineAudioPostDownloadHelper.PrefetchRequest(
                queueUuid,
                context,
                payload,
                settings,
                expectedOutputPath,
                _postDownloadTaskScheduler,
                _lyricsService,
                _queueRepository,
                _deezspotagListener,
                _activityLog,
                _logger,
                Engine,
                payload.AppleId,
                payload.AppleId));
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
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Apple MV metadata lookup failed for {QueueUuid}", queueUuid);            }
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
