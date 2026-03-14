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
            payload = await EngineAudioPostDownloadHelper.InitializeQueueItemAsync(
                next,
                next.PayloadJson,
                QueueHelperUtils.DeserializeQueueItem<TPayload>,
                initializeContext,
                itemToken);
            if (payload == null)
            {
                return;
            }

            if (callbacks.PreparePayloadAsync is not null)
            {
                await callbacks.PreparePayloadAsync(payload, itemToken);
            }

            EngineAudioPostDownloadHelper.EngineTrackContext? context = null;
            using (var scope = deps.ServiceProvider.CreateScope())
            {
                var pathProcessor = scope.ServiceProvider.GetRequiredService<EnhancedPathTemplateProcessor>();
                context = BuildTrackContext(payload, settings, pathProcessor, engineName, callbacks.ResolveSourceId(payload));
            }

            var request = callbacks.BuildRequest(payload, settings);
            if (context != null)
            {
                callbacks.ApplyContextToRequest(request, context);
            }

            var progressReporter = QueueHelperUtils.CreateProgressReporter(
                deps.QueueRepository,
                deps.Listener,
                next.QueueUuid,
                deps.Logger,
                "Failed to report progress for {QueueUuid}",
                itemToken);

            deps.ActivityLog.Info(callbacks.BuildStartLogMessage(request));
            if (context != null)
            {
                    var expectedOutputPath = !string.IsNullOrWhiteSpace(context.PathResult.WritePath)
                        ? DownloadPathResolver.ResolveIoPath(context.PathResult.WritePath)
                        : Path.Join(
                            DownloadPathResolver.ResolveIoPath(context.PathResult.FilePath),
                            context.PathResult.Filename);
                var prefetchRequest = CreatePrefetchRequest(
                    new PrefetchContext(
                        next.QueueUuid,
                        context,
                        payload,
                        settings,
                        expectedOutputPath,
                        deps.ServiceProvider,
                        deps.Listener,
                        deps.ActivityLog,
                        deps.Logger,
                        engineName));
                await EngineAudioPostDownloadHelper.QueueParallelPostDownloadPrefetchAsync(prefetchRequest, itemToken);
            }

            var outputPath = await callbacks.DownloadAsync(payload, request, settings, progressReporter, itemToken);

            if (context != null)
            {
                try
                {
                    using var scope = deps.ServiceProvider.CreateScope();
                    var postDownloadRequest = new EngineAudioPostDownloadHelper.PostDownloadSettingsRequest(
                        context,
                        payload,
                        outputPath,
                        settings,
                        scope.ServiceProvider,
                        engineName,
                        deps.Logger);
                    outputPath = await EngineAudioPostDownloadHelper.ApplyPostDownloadSettingsAsync(
                        postDownloadRequest,
                        itemToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    deps.Logger.LogWarning(ex, "{Engine} post-download settings failed for {QueueUuid}", engineName, next.QueueUuid);
                }
            }

            var finalSize = QueueHelperUtils.TryGetFileSizeMb(outputPath);
            if (finalSize <= 0 || !QueueHelperUtils.OutputExists(outputPath))
            {
                throw new InvalidOperationException($"Downloaded file missing or empty: {outputPath}");
            }

            await deps.QueueRepository.UpdateStatusAsync(next.QueueUuid, "completed", downloaded: 1, progress: 100, cancellationToken: itemToken);
            await QueueHelperUtils.UpdateFinalDestinationPayloadAsync(
                new QueueHelperUtils.UpdateFinalDestinationPayloadRequest<TPayload>(
                    deps.QueueRepository,
                    next.QueueUuid,
                    payload,
                    outputPath,
                    finalSize,
                    payload.Size,
                    payload.Files,
                    new QueueHelperUtils.FinalDestinationMutators<TPayload>(
                        item => item.FinalDestinations,
                        (item, value) => item.FinalDestinations = value,
                        new QueueHelperUtils.PayloadUpdateMutators<TPayload>(
                            (item, value) => item.FilePath = value,
                            (item, value) => item.TotalSize = value,
                            (item, value) => item.Progress = value,
                            (item, value) => item.Downloaded = value))),
                itemToken);
            await EngineAudioPostDownloadHelper.UpdateWatchlistTrackStatusAsync(
                payload,
                "completed",
                deps.ServiceProvider,
                itemToken);
            deps.RetryScheduler.Clear(next.QueueUuid);

            deps.Listener.Send("updateQueue", new
            {
                uuid = next.QueueUuid,
                progress = 100,
                downloaded = 1,
                failed = 0,
                engine = payload.Engine
            });
            deps.Listener.SendFinishDownload(next.QueueUuid, callbacks.ResolveFinishTitle(payload) ?? string.Empty);
        }
        catch (OperationCanceledException) when (itemToken.IsCancellationRequested)
        {
            var cancellationContext = new EngineAudioPostDownloadHelper.CancellationHandlingContext(
                deps.QueueRepository,
                deps.CancellationRegistry,
                deps.Listener,
                deps.RetryScheduler,
                engineName,
                deps.ServiceProvider);
            await EngineAudioPostDownloadHelper.HandleCancellationAsync(
                next.QueueUuid,
                payload,
                cancellationContext,
                itemToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
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
                ex,
                next.QueueUuid,
                payload,
                failureContext,
                stoppingToken);
        }
        finally
        {
            settings.DownloadLocation = originalDownloadLocation;
            deps.CancellationRegistry.Remove(next.QueueUuid);
        }
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
