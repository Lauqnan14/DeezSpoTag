using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics.CodeAnalysis;

namespace DeezSpoTag.Web.Services;

[ExcludeFromCodeCoverage]
public sealed class DownloadQueuePreResolutionService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private readonly DownloadQueueRepository _queueRepository;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DownloadQueuePreResolutionService> _logger;

    public DownloadQueuePreResolutionService(
        DownloadQueueRepository queueRepository,
        DeezSpoTagSettingsService settingsService,
        IServiceScopeFactory scopeFactory,
        ILogger<DownloadQueuePreResolutionService> logger)
    {
        _queueRepository = queueRepository;
        _settingsService = settingsService;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ResolveOneLookaheadItemAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
            {
                _logger.LogWarning(ex, "Queue pre-resolution pass failed.");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task ResolveOneLookaheadItemAsync(CancellationToken cancellationToken)
    {
        var settings = _settingsService.LoadSettings();
        if (!settings.EnableQueuePreResolution)
        {
            return;
        }

        var windowSize = Math.Clamp(settings.QueuePreResolutionWindow, 1, 25);
        var retryDelay = TimeSpan.FromMinutes(Math.Clamp(settings.QueuePreResolutionRetryMinutes, 1, 60));
        var now = DateTimeOffset.UtcNow;
        var tasks = await _queueRepository.GetTasksAsync(cancellationToken: cancellationToken);
        var candidate = QueuePreResolutionPlanner.SelectNext(
            tasks,
            settings.QueueOrder,
            windowSize,
            retryDelay,
            now);
        if (candidate == null)
        {
            return;
        }

        await ResolveCandidateAsync(candidate, now, cancellationToken);
    }

    private async Task ResolveCandidateAsync(
        DownloadQueueItem item,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var originalPayloadJson = item.PayloadJson ?? string.Empty;
        var resolvingPayload = QueuePreResolutionPayload.ParseOrEmpty(originalPayloadJson);
        QueuePreResolutionPayload.MarkResolving(resolvingPayload, startedAt);
        var resolvingPayloadJson = resolvingPayload.ToJsonString();
        var claimed = await _queueRepository.TryUpdateQueuedPayloadIfCurrentAsync(
            item.QueueUuid,
            item.PayloadJson,
            resolvingPayloadJson,
            cancellationToken: cancellationToken);
        if (!claimed)
        {
            return;
        }

        var resolvingItem = item with { PayloadJson = resolvingPayloadJson };
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var resolver = scope.ServiceProvider.GetRequiredService<DownloadIntentService>();
            var result = await resolver.ResolveQueuedPayloadAsync(resolvingItem, cancellationToken);
            var resolvedPayload = QueuePreResolutionPayload.ParseOrEmpty(resolvingPayloadJson);
            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                QueuePreResolutionPayload.ApplyFailed(resolvedPayload, result.Error!, DateTimeOffset.UtcNow);
            }
            else
            {
                QueuePreResolutionPayload.ApplyResolved(resolvedPayload, result, DateTimeOffset.UtcNow);
            }

            await _queueRepository.TryUpdateQueuedPayloadIfCurrentAsync(
                item.QueueUuid,
                resolvingPayloadJson,
                resolvedPayload.ToJsonString(),
                result.Engine,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Queue pre-resolution failed for {QueueUuid} ({Artist} - {Title}).",
                item.QueueUuid,
                item.ArtistName,
                item.TrackTitle);
            var failedPayload = QueuePreResolutionPayload.ParseOrEmpty(resolvingPayloadJson);
            QueuePreResolutionPayload.ApplyFailed(failedPayload, ex.Message, DateTimeOffset.UtcNow);
            await _queueRepository.TryUpdateQueuedPayloadIfCurrentAsync(
                item.QueueUuid,
                resolvingPayloadJson,
                failedPayload.ToJsonString(),
                cancellationToken: CancellationToken.None);
        }
    }
}
