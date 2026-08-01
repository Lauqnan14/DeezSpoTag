using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Shared;

internal static class EngineQueueProcessorHelper
{
    internal readonly record struct ProcessorDeps(
        DownloadQueueRepository QueueRepository,
        DownloadCancellationRegistry CancellationRegistry,
        DeezSpoTagSettingsService SettingsService,
        IDeezSpoTagListener Listener,
        DownloadRetryScheduler RetryScheduler,
        IServiceProvider ServiceProvider,
        EngineFallbackCoordinator FallbackCoordinator,
        IActivityLogWriter ActivityLog,
        IDownloadTagSettingsResolver TagSettingsResolver,
        IFolderConversionSettingsOverlay FolderConversionSettingsOverlay,
        ILogger Logger);

    internal readonly record struct ProcessorCallbacks<TPayload>(
        Func<TPayload, string> ResolveSourceId,
        Func<TPayload, DeezSpoTagSettings, object> BuildRequest,
        Action<object, EngineAudioPostDownloadHelper.EngineTrackContext> ApplyContextToRequest,
        Func<TPayload, object, DeezSpoTagSettings, Func<double, double, Task>?, CancellationToken, Task<string>> DownloadAsync,
        Func<TPayload, CancellationToken, Task>? PreparePayloadAsync,
        Func<object, string> BuildStartLogMessage,
        Func<TPayload, string?> ResolveFinishTitle,
        Func<TPayload, Dictionary<string, object>> ToQueuePayload,
        Func<TPayload, string, CancellationToken, Task<string>>? AcceptAcquiredAudioAsync = null,
        Func<TPayload, string, CancellationToken, Task>? RejectAcquiredAudioAsync = null)
        where TPayload : EngineQueueItemBase;

    private readonly record struct PrefetchContext(
        string QueueUuid,
        EngineAudioPostDownloadHelper.EngineTrackContext Context,
        EngineQueueItemBase Payload,
        DeezSpoTagSettings Settings,
        string ExpectedOutputPath,
        IServiceProvider ServiceProvider,
        IDeezSpoTagListener Listener,
        IActivityLogWriter ActivityLog,
        ILogger Logger,
        string EngineName);

    private readonly record struct ExecutionState(
        object Request,
        EngineAudioPostDownloadHelper.EngineTrackContext? Context,
        Func<double, double, Task> ProgressReporter);

    private readonly record struct QueueWorkContext<TPayload>(
        DownloadQueueItem Item,
        TPayload Payload,
        string EngineName,
        ProcessorDeps Deps,
        ProcessorCallbacks<TPayload> Callbacks,
        DeezSpoTagSettings Settings,
        CancellationToken ItemToken)
        where TPayload : EngineQueueItemBase;

    public static async Task ProcessQueueItemAsync<TPayload>(
        DownloadQueueItem next,
        string engineName,
        ProcessorDeps deps,
        ProcessorCallbacks<TPayload> callbacks,
        CancellationToken stoppingToken)
        where TPayload : EngineQueueItemBase
    {
        var settings = deps.SettingsService.LoadSettings();
        var originalDownloadLocation = settings.DownloadLocation;
        TPayload? payload = null;

        using var itemCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        deps.CancellationRegistry.Register(next.QueueUuid, itemCts);
        var itemToken = itemCts.Token;

        try
        {
            payload = await InitializePayloadAsync(next, engineName, deps, callbacks, settings, itemToken);
            if (payload == null)
            {
                return;
            }

            var workContext = new QueueWorkContext<TPayload>(
                next,
                payload,
                engineName,
                deps,
                callbacks,
                settings,
                itemToken);
            var executionState = await PrepareExecutionStateAsync(
                next,
                workContext,
                itemToken);
            await ExecutePipelineAsync(
                workContext,
                executionState,
                itemToken);
        }
        catch (OperationCanceledException ex) when (itemToken.IsCancellationRequested)
        {
            if (payload != null)
            {
                var workContext = new QueueWorkContext<TPayload>(
                    next,
                    payload,
                    engineName,
                    deps,
                    callbacks,
                    settings,
                    itemToken);
                await HandleCanceledProcessingAsync(workContext, ex, stoppingToken);
            }
            else
            {
                await HandleFailedProcessingAsync(next, engineName, deps, callbacks, payload, ex, stoppingToken);
            }
        }
        catch (OperationCanceledException ex) when (!itemToken.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            var timeoutException = new TimeoutException(
                $"{engineName} operation timed out or was canceled by an external provider.",
                ex);
            await HandleFailedProcessingAsync(next, engineName, deps, callbacks, payload, timeoutException, stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await HandleFailedProcessingAsync(next, engineName, deps, callbacks, payload, ex, stoppingToken);
        }
        finally
        {
            settings.DownloadLocation = originalDownloadLocation;
            deps.CancellationRegistry.Remove(next.QueueUuid);
        }
    }

    private static async Task<TPayload?> InitializePayloadAsync<TPayload>(
        DownloadQueueItem next,
        string engineName,
        ProcessorDeps deps,
        ProcessorCallbacks<TPayload> callbacks,
        DeezSpoTagSettings settings,
        CancellationToken itemToken)
        where TPayload : EngineQueueItemBase
    {
        var initializeContext = new EngineAudioPostDownloadHelper.InitializeQueueItemContext<TPayload>(
            deps.QueueRepository,
            deps.RetryScheduler,
            deps.ActivityLog,
            deps.TagSettingsResolver,
            deps.FolderConversionSettingsOverlay,
            deps.Listener,
            deps.FallbackCoordinator.TryAdvanceAsync,
            callbacks.ToQueuePayload,
            settings,
            engineName,
            deps.Logger);
        return await EngineAudioPostDownloadHelper.InitializeQueueItemAsync(
            next,
            next.PayloadJson,
            QueueHelperUtils.DeserializeQueueItem<TPayload>,
            initializeContext,
            itemToken);
    }

    private static async Task<ExecutionState> PrepareExecutionStateAsync<TPayload>(
        DownloadQueueItem next,
        QueueWorkContext<TPayload> workContext,
        CancellationToken itemToken)
        where TPayload : EngineQueueItemBase
    {
        if (workContext.Callbacks.PreparePayloadAsync is not null)
        {
            await workContext.Callbacks.PreparePayloadAsync(workContext.Payload, itemToken);
        }

        var context = await BuildTrackContextOrNullAsync(workContext);
        var request = workContext.Callbacks.BuildRequest(workContext.Payload, workContext.Settings);
        if (context != null)
        {
            workContext.Callbacks.ApplyContextToRequest(request, context);
            await QueueHelperUtils.PersistExpectedStagingPathAsync(
                workContext.Deps.QueueRepository,
                next.QueueUuid,
                workContext.Payload,
                ResolveExpectedOutputPath(context),
                itemToken);
        }

        var progressReporter = QueueHelperUtils.CreateProgressReporter(
            workContext.Deps.QueueRepository,
            workContext.Deps.Listener,
            next.QueueUuid,
            workContext.Deps.Logger,
            "Failed to report progress for {QueueUuid}",
            itemToken);
        workContext.Deps.ActivityLog.Info(workContext.Callbacks.BuildStartLogMessage(request));
        await QueuePrefetchIfNeededAsync(workContext, context);

        return new ExecutionState(request, context, progressReporter);
    }

    private static async Task ExecutePipelineAsync<TPayload>(
        QueueWorkContext<TPayload> workContext,
        ExecutionState executionState,
        CancellationToken itemToken)
        where TPayload : EngineQueueItemBase
    {
        string outputPath;
        if (!DownloadLifecycleCheckpoint.TryResume(workContext.Payload, out outputPath))
        {
            await QueueHelperUtils.UpdatePayloadAsync(
                workContext.Deps.QueueRepository,
                workContext.Item.QueueUuid,
                workContext.Payload,
                itemToken);
            outputPath = await workContext.Callbacks.DownloadAsync(
                workContext.Payload,
                executionState.Request,
                workContext.Settings,
                executionState.ProgressReporter,
                itemToken);
            try
            {
                await DeliveredAudioQualityGuard.EnsurePlanStepSatisfiedAsync(
                    workContext.Payload,
                    outputPath,
                    workContext.Item.QueueUuid,
                    workContext.Deps.QueueRepository,
                    workContext.Deps.Listener,
                    itemToken);
            }
            catch (DeliveredAudioQualityBelowPlanStepException)
            {
                if (workContext.Callbacks.RejectAcquiredAudioAsync is not null)
                {
                    await workContext.Callbacks.RejectAcquiredAudioAsync(
                        workContext.Payload,
                        outputPath,
                        CancellationToken.None);
                }
                throw;
            }
            await DownloadLifecycleCheckpoint.PersistAcquiredAsync(
                workContext.Deps.QueueRepository,
                workContext.Item.QueueUuid,
                workContext.Payload,
                outputPath,
                itemToken);
        }

        if (workContext.Callbacks.AcceptAcquiredAudioAsync is not null)
        {
            var acceptedPath = await workContext.Callbacks.AcceptAcquiredAudioAsync(
                workContext.Payload,
                outputPath,
                itemToken);
            if (!string.Equals(acceptedPath, outputPath, StringComparison.Ordinal))
            {
                outputPath = acceptedPath;
                await DownloadLifecycleCheckpoint.PersistAcquiredAsync(
                    workContext.Deps.QueueRepository,
                    workContext.Item.QueueUuid,
                    workContext.Payload,
                    outputPath,
                    itemToken);
            }
        }

        try
        {
            outputPath = await ApplyPostDownloadSettingsAsync(
                workContext,
                outputPath,
                executionState.Context,
                itemToken);
            await CompleteProcessingAsync(workContext, outputPath);
        }
        catch (DownloadFinalizationException ex)
        {
            await DownloadLifecycleCheckpoint.PersistFinalizationFailureAsync(
                workContext.Deps.QueueRepository,
                workContext.Deps.RetryScheduler,
                workContext.Deps.Listener,
                workContext.Item.QueueUuid,
                workContext.EngineName,
                workContext.Payload,
                ex,
                CancellationToken.None);
            return;
        }
    }

    private static async Task<EngineAudioPostDownloadHelper.EngineTrackContext?> BuildTrackContextOrNullAsync<TPayload>(
        QueueWorkContext<TPayload> workContext)
        where TPayload : EngineQueueItemBase
    {
        using var scope = workContext.Deps.ServiceProvider.CreateScope();
        var pathProcessor = scope.ServiceProvider.GetRequiredService<EnhancedPathTemplateProcessor>();
        var context = BuildTrackContext(
            workContext.Payload,
            workContext.Settings,
            pathProcessor,
            workContext.EngineName,
            workContext.Callbacks.ResolveSourceId(workContext.Payload));
        var resolvedSource = await EngineAudioPostDownloadHelper.ResolveProfileDownloadTagSourceAsync(
            workContext.Deps.TagSettingsResolver,
            workContext.Payload.DestinationFolderId,
            workContext.Settings,
            workContext.EngineName,
            workContext.Deps.Logger,
            workContext.ItemToken);
        var applied = await EngineAudioPostDownloadHelper.ApplyProfileMetadataOverrideAsync(
            new EngineAudioPostDownloadHelper.ProfileMetadataOverrideRequest(
                context.Track,
                workContext.Payload,
                workContext.Settings,
                workContext.Deps.ServiceProvider,
                workContext.EngineName,
                resolvedSource,
                workContext.Deps.Logger,
                workContext.ItemToken));
        return applied
            ? EngineAudioPostDownloadHelper.BuildTrackContextFromTrack(
                context.Track,
                workContext.Payload,
                workContext.Settings,
                pathProcessor)
            : context;
    }

    private static string ResolveExpectedOutputPath(EngineAudioPostDownloadHelper.EngineTrackContext context)
    {
        return !string.IsNullOrWhiteSpace(context.PathResult.WritePath)
            ? context.PathResult.WritePath
            : Path.Join(context.PathResult.FilePath, context.PathResult.Filename);
    }

    private static async Task QueuePrefetchIfNeededAsync<TPayload>(
        QueueWorkContext<TPayload> workContext,
        EngineAudioPostDownloadHelper.EngineTrackContext? context)
        where TPayload : EngineQueueItemBase
    {
        if (context == null)
        {
            return;
        }

        var expectedOutputPath = !string.IsNullOrWhiteSpace(context.PathResult.WritePath)
            ? DownloadPathResolver.ResolveIoPath(context.PathResult.WritePath)
            : Path.Join(
                DownloadPathResolver.ResolveIoPath(context.PathResult.FilePath),
                context.PathResult.Filename);
        var prefetchRequest = CreatePrefetchRequest(
            new PrefetchContext(
                workContext.Item.QueueUuid,
                context,
                workContext.Payload,
                workContext.Settings,
                expectedOutputPath,
                workContext.Deps.ServiceProvider,
                workContext.Deps.Listener,
                workContext.Deps.ActivityLog,
                workContext.Deps.Logger,
                workContext.EngineName));
        await EngineAudioPostDownloadHelper.QueueParallelPostDownloadPrefetchAsync(prefetchRequest, workContext.ItemToken);
    }

    private static async Task<string> ApplyPostDownloadSettingsAsync<TPayload>(
        QueueWorkContext<TPayload> workContext,
        string outputPath,
        EngineAudioPostDownloadHelper.EngineTrackContext? context,
        CancellationToken itemToken)
        where TPayload : EngineQueueItemBase
    {
        if (context == null)
        {
            return outputPath;
        }

        using var scope = workContext.Deps.ServiceProvider.CreateScope();
        var postDownloadRequest = new EngineAudioPostDownloadHelper.PostDownloadSettingsRequest(
            workContext.Item.QueueUuid,
            context,
            workContext.Payload,
            outputPath,
            workContext.Settings,
            scope.ServiceProvider,
            workContext.EngineName,
            workContext.Deps.Logger,
            AppleCoverLookupIdOverride: ResolveAppleArtworkOverride(workContext.Payload),
            AnimatedArtworkAppleIdOverride: ResolveAppleArtworkOverride(workContext.Payload));
        return await EngineAudioPostDownloadHelper.ApplyPostDownloadSettingsAsync(postDownloadRequest, itemToken);
    }

    private static async Task CompleteProcessingAsync<TPayload>(
        QueueWorkContext<TPayload> workContext,
        string outputPath)
        where TPayload : EngineQueueItemBase
    {
        var finalSize = QueueHelperUtils.TryGetFileSizeMb(outputPath);
        if (finalSize <= 0 || !QueueHelperUtils.OutputExists(outputPath))
        {
            throw new DownloadFinalizationException(
                DownloadFinalizationStage.FinalVerification,
                "Audio downloaded; final verification will be retried.",
                new InvalidOperationException($"Downloaded file missing or empty: {outputPath}"));
        }

        await EngineAudioPostDownloadHelper.AwaitRemainingPrefetchAsync(
            workContext.Item.QueueUuid,
            workContext.ItemToken);

        ActualDownloadQualityLabel.ApplyTo(workContext.Payload, outputPath);
        DownloadLifecycleCheckpoint.MarkCompleted(workContext.Payload);
        FallbackAttemptRecorder.RecordCurrent(
            workContext.Payload,
            "completed",
            "none",
            BuildCompletedQualityDetail(workContext.Payload));
        await QueueHelperUtils.UpdateFinalDestinationPayloadAsync(
            new QueueHelperUtils.UpdateFinalDestinationPayloadRequest<TPayload>(
                workContext.Deps.QueueRepository,
                workContext.Item.QueueUuid,
                workContext.Payload,
                outputPath,
                finalSize,
                workContext.Payload.Size,
                workContext.Payload.Files,
                new QueueHelperUtils.FinalDestinationMutators<TPayload>(
                    item => item.FinalDestinations,
                    (item, value) => item.FinalDestinations = value,
                    new QueueHelperUtils.PayloadUpdateMutators<TPayload>(
                        (item, value) => item.FilePath = value,
                        (item, value) => item.TotalSize = value,
                        (item, value) => item.Progress = value,
                        (item, value) => item.Downloaded = value))),
            workContext.ItemToken);
        await workContext.Deps.QueueRepository.UpdateStatusAsync(
            workContext.Item.QueueUuid,
            "completed",
            downloaded: 1,
            progress: 100,
            cancellationToken: workContext.ItemToken);
        await EngineAudioPostDownloadHelper.UpdateWatchlistTrackStatusAsync(
            workContext.Payload,
            "completed",
            workContext.Deps.ServiceProvider,
            workContext.ItemToken,
            workContext.Item.QueueUuid);
        var completedEngine = string.IsNullOrWhiteSpace(workContext.Payload.Engine)
            ? workContext.EngineName
            : workContext.Payload.Engine;
        workContext.Deps.ServiceProvider
            .GetService<IDownloadApiHealthTracker>()
            ?.ReportSuccess(completedEngine);
        await workContext.Deps.RetryScheduler.ClearAsync(workContext.Item.QueueUuid, workContext.ItemToken);
        workContext.Deps.Listener.Send("updateQueue", new
        {
            uuid = workContext.Item.QueueUuid,
            status = "completed",
            progress = 100,
            downloaded = 1,
            failed = 0,
            engine = workContext.Payload.Engine,
            quality = workContext.Payload.Quality,
            requestedQuality = workContext.Payload.RequestedQuality,
            deliveredQuality = workContext.Payload.DeliveredQuality,
            autoIndex = workContext.Payload.AutoIndex,
            fallbackPlan = workContext.Payload.FallbackPlan,
            fallbackHistory = workContext.Payload.FallbackHistory
        });
        workContext.Deps.Listener.SendFinishDownload(
            workContext.Item.QueueUuid,
            workContext.Callbacks.ResolveFinishTitle(workContext.Payload) ?? string.Empty);
    }

    private static string BuildCompletedQualityDetail(EngineQueueItemBase payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.DeliveredQuality)
            && !string.Equals(payload.DeliveredQuality, payload.Quality, StringComparison.OrdinalIgnoreCase))
        {
            return $"Source delivered {payload.DeliveredQuality}; final output is {payload.Quality}.";
        }

        return $"Delivered {payload.Quality}.";
    }

    private static async Task HandleCanceledProcessingAsync<TPayload>(
        QueueWorkContext<TPayload> workContext,
        OperationCanceledException exception,
        CancellationToken stoppingToken)
        where TPayload : EngineQueueItemBase
    {
        if (workContext.Deps.CancellationRegistry.WasTimedOut(workContext.Item.QueueUuid))
        {
            var timeoutException = new TimeoutException(
                DownloadQueueRecoveryPolicy.BuildStallTimeoutMessage(workContext.EngineName),
                exception);
            await HandleFailedProcessingAsync(
                workContext.Item,
                workContext.EngineName,
                workContext.Deps,
                workContext.Callbacks,
                workContext.Payload,
                timeoutException,
                stoppingToken);
            return;
        }

        var cancellationContext = new EngineAudioPostDownloadHelper.CancellationHandlingContext(
            workContext.Deps.QueueRepository,
            workContext.Deps.CancellationRegistry,
            workContext.Deps.Listener,
            workContext.Deps.RetryScheduler,
            workContext.EngineName,
            workContext.Deps.ServiceProvider);
        await EngineAudioPostDownloadHelper.HandleCancellationAsync(
            workContext.Item.QueueUuid,
            workContext.Payload,
            cancellationContext,
            workContext.ItemToken);
    }

    private static async Task HandleFailedProcessingAsync<TPayload>(
        DownloadQueueItem next,
        string engineName,
        ProcessorDeps deps,
        ProcessorCallbacks<TPayload> callbacks,
        TPayload? payload,
        Exception exception,
        CancellationToken stoppingToken)
        where TPayload : EngineQueueItemBase
    {
        var failureContext = new EngineAudioPostDownloadHelper.FailureHandlingContext<TPayload>(
            deps.QueueRepository,
            deps.ActivityLog,
            deps.Listener,
            deps.RetryScheduler,
            deps.ServiceProvider,
            deps.FallbackCoordinator.TryAdvanceAsync,
            callbacks.ToQueuePayload,
            engineName,
            deps.Logger);
        await EngineAudioPostDownloadHelper.HandleFailureAsync(
            exception,
            next.QueueUuid,
            payload,
            failureContext,
            stoppingToken);
    }

    private static EngineAudioPostDownloadHelper.EngineTrackContext BuildTrackContext(
        EngineQueueItemBase payload,
        DeezSpoTagSettings settings,
        EnhancedPathTemplateProcessor pathProcessor,
        string source,
        string? sourceId)
    {
        var sharedContext = EngineAudioPostDownloadHelper.BuildTrackContext(
            payload,
            settings,
            pathProcessor,
            source,
            sourceId);
        return new EngineAudioPostDownloadHelper.EngineTrackContext(
            sharedContext.Track,
            sharedContext.PathResult,
            sharedContext.OutputDir,
            sharedContext.FilenameFormat);
    }

    private static EngineAudioPostDownloadHelper.PrefetchRequest CreatePrefetchRequest(PrefetchContext context)
    {
        var scheduler = context.ServiceProvider.GetRequiredService<IPostDownloadTaskScheduler>();
        var lyricsService = context.ServiceProvider.GetRequiredService<LyricsService>();
        var queueRepository = context.ServiceProvider.GetRequiredService<DownloadQueueRepository>();
        return new EngineAudioPostDownloadHelper.PrefetchRequest(
            context.QueueUuid,
            context.Context,
            context.Payload,
            context.Settings,
            context.ExpectedOutputPath,
            scheduler,
            lyricsService,
            queueRepository,
            context.Listener,
            context.ActivityLog,
            context.Logger,
            context.EngineName,
            AppleCoverLookupIdOverride: ResolveAppleArtworkOverride(context.Payload),
            AnimatedArtworkAppleIdOverride: ResolveAppleArtworkOverride(context.Payload));
    }

    private static string? ResolveAppleArtworkOverride(EngineQueueItemBase payload)
        => string.IsNullOrWhiteSpace(payload.AppleId) ? null : payload.AppleId;
}
