using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Diagnostics.CodeAnalysis;

namespace DeezSpoTag.Web.Services;

[ExcludeFromCodeCoverage]
public sealed class DownloadQueuePreResolutionService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
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
            if (IsProviderRateLimit(result.Error))
            {
                QueuePreResolutionPayload.ApplyFailed(resolvedPayload, "Pre-resolution deferred by provider rate limit.", DateTimeOffset.UtcNow);
                await _queueRepository.TryUpdateQueuedPayloadIfCurrentAsync(
                    item.QueueUuid,
                    resolvingPayloadJson,
                    resolvedPayload.ToJsonString(),
                    status: "queued",
                    error: null,
                    cancellationToken: cancellationToken);
                return;
            }

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
                string.IsNullOrWhiteSpace(result.Error) ? "queued" : "failed",
                result.Error,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (IsProviderRateLimit(ex))
            {
                _logger.LogWarning(
                    ex,
                    "Queue pre-resolution deferred by provider rate limit for {QueueUuid} ({Artist} - {Title}).",
                    item.QueueUuid,
                    item.ArtistName,
                    item.TrackTitle);

                var deferredPayload = QueuePreResolutionPayload.ParseOrEmpty(resolvingPayloadJson);
                QueuePreResolutionPayload.ApplyFailed(deferredPayload, "Pre-resolution deferred by provider rate limit.", DateTimeOffset.UtcNow);
                await _queueRepository.TryUpdateQueuedPayloadIfCurrentAsync(
                    item.QueueUuid,
                    resolvingPayloadJson,
                    deferredPayload.ToJsonString(),
                    status: "queued",
                    error: null,
                    cancellationToken: CancellationToken.None);
                return;
            }

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
                status: "failed",
                error: ex.Message,
                cancellationToken: CancellationToken.None);
        }
    }

    private static bool IsProviderRateLimit(Exception exception)
    {
        if (exception is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests })
        {
            return true;
        }

        return exception.Message.Contains("429", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProviderRateLimit(string? message)
    {
        return !string.IsNullOrWhiteSpace(message)
            && (message.Contains("429", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase));
    }
}
