using DeezSpoTag.Core.Models.Settings;
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
        Func<TPayload, Dictionary<string, object>> ToQueuePayload)
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
        var outputPath = await workContext.Callbacks.DownloadAsync(
            workContext.Payload,
            executionState.Request,
            workContext.Settings,
            executionState.ProgressReporter,
            itemToken);
        outputPath = await ApplyPostDownloadSettingsSafelyAsync(
            workContext,
            outputPath,
            executionState.Context,
            itemToken);
        await CompleteProcessingAsync(workContext, outputPath);
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

    private static async Task<string> ApplyPostDownloadSettingsSafelyAsync<TPayload>(
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
            context,
            workContext.Payload,
            outputPath,
            workContext.Settings,
            scope.ServiceProvider,
            workContext.EngineName,
            workContext.Deps.Logger);
        return await ApplyPostDownloadSettingsWithFallbackAsync(
            workContext.EngineName,
            workContext.Item.QueueUuid,
            outputPath,
            workContext.Deps.Logger,
            () => EngineAudioPostDownloadHelper.ApplyPostDownloadSettingsAsync(postDownloadRequest, itemToken));
    }

    internal static async Task<string> ApplyPostDownloadSettingsWithFallbackAsync(
        string engineName,
        string queueUuid,
        string outputPath,
        ILogger logger,
        Func<Task<string>> applySettingsAsync)
    {
        try
        {
            return await applySettingsAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"{engineName} post-download settings failed for {queueUuid}; failing queue item.",
                ex);
        }
    }

    private static async Task CompleteProcessingAsync<TPayload>(
        QueueWorkContext<TPayload> workContext,
        string outputPath)
        where TPayload : EngineQueueItemBase
    {
        var finalSize = QueueHelperUtils.TryGetFileSizeMb(outputPath);
        if (finalSize <= 0 || !QueueHelperUtils.OutputExists(outputPath))
        {
            throw new InvalidOperationException($"Downloaded file missing or empty: {outputPath}");
        }

        var prefetchFailure = await EngineAudioPostDownloadHelper.EnsureArtworkPrefetchCompletedAsync(
            workContext.Item.QueueUuid,
            outputPath,
            workContext.ItemToken);
        if (!string.IsNullOrWhiteSpace(prefetchFailure))
        {
            workContext.Deps.Logger.LogWarning(
                "{Engine} sidecar prefetch failed for {QueueUuid}: {Reason}",
                workContext.EngineName,
                workContext.Item.QueueUuid,
                prefetchFailure);
            workContext.Deps.ActivityLog.Warn(
                $"Sidecar prefetch failed (engine={workContext.EngineName}): {workContext.Item.QueueUuid} {prefetchFailure}");
        }
        await workContext.Deps.QueueRepository.UpdateStatusAsync(
            workContext.Item.QueueUuid,
            "completed",
            downloaded: 1,
            progress: 100,
            cancellationToken: workContext.ItemToken);
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
        await EngineAudioPostDownloadHelper.UpdateWatchlistTrackStatusAsync(
            workContext.Payload,
            "completed",
            workContext.Deps.ServiceProvider,
            workContext.ItemToken);
        workContext.Deps.RetryScheduler.Clear(workContext.Item.QueueUuid);
        workContext.Deps.Listener.Send("updateQueue", new
        {
            uuid = workContext.Item.QueueUuid,
            progress = 100,
            downloaded = 1,
            failed = 0,
            engine = workContext.Payload.Engine
        });
        workContext.Deps.Listener.SendFinishDownload(
            workContext.Item.QueueUuid,
            workContext.Callbacks.ResolveFinishTitle(workContext.Payload) ?? string.Empty);
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
        return new EngineAudioPostDownloadHelper.PrefetchRequest(
            context.QueueUuid,
            context.Context,
            context.Payload,
            context.Settings,
            context.ExpectedOutputPath,
            scheduler,
            lyricsService,
            context.Listener,
            context.ActivityLog,
            context.Logger,
            context.EngineName);
    }
}
