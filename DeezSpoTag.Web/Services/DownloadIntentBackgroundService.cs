using DeezSpoTag.Services.Download.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace DeezSpoTag.Web.Services;

public sealed class DownloadIntentBackgroundService : BackgroundService
{
    private const int MaxThrottleRetries = 4;
    private static readonly TimeSpan BaseThrottleDelay = TimeSpan.FromSeconds(8);

    private readonly IDownloadIntentBackgroundQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DownloadIntentBackgroundService> _logger;
    private readonly Dictionary<string, int> _retryCounts = new(StringComparer.OrdinalIgnoreCase);

    public DownloadIntentBackgroundService(
        IDownloadIntentBackgroundQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<DownloadIntentBackgroundService> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var intent in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            using var scope = _scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<DownloadIntentService>();
            var retryKey = BuildRetryKey(intent);
            try
            {
                await service.EnqueueManualAsync(intent, stoppingToken);
                _retryCounts.Remove(retryKey);
            }
            catch (HttpRequestException ex) when (IsRateLimit(ex))
            {
                var nextRetry = _retryCounts.TryGetValue(retryKey, out var currentRetry) ? currentRetry + 1 : 1;
                if (nextRetry > MaxThrottleRetries)
                {
                    _retryCounts.Remove(retryKey);
                    _logger.LogWarning(
                        ex,
                        "Background intent enqueue dropped after throttle retries for {SourceUrl}",
                        DeezSpoTag.Core.Security.LogSanitizer.OneLine(intent.SourceUrl));
                    continue;
                }

                _retryCounts[retryKey] = nextRetry;
                var delay = TimeSpan.FromSeconds(BaseThrottleDelay.TotalSeconds * nextRetry);
                _logger.LogWarning(
                    ex,
                    "Background intent enqueue throttled for {SourceUrl}; retry {Retry}/{MaxRetry} in {DelaySeconds}s",
                    DeezSpoTag.Core.Security.LogSanitizer.OneLine(intent.SourceUrl),
                    nextRetry,
                    MaxThrottleRetries,
                    (int)delay.TotalSeconds);

                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                _queue.Enqueue(intent);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _retryCounts.Remove(retryKey);
                _logger.LogWarning(ex, "Background intent enqueue failed for {SourceUrl}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(intent.SourceUrl));
            }
        }
    }

    private static bool IsRateLimit(HttpRequestException ex)
        => ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests;

    private static string BuildRetryKey(DownloadIntent intent)
    {
        var sourceUrl = string.IsNullOrWhiteSpace(intent.SourceUrl) ? "-" : intent.SourceUrl.Trim();
        var sourceService = string.IsNullOrWhiteSpace(intent.SourceService) ? "-" : intent.SourceService.Trim();
        var title = string.IsNullOrWhiteSpace(intent.Title) ? "-" : intent.Title.Trim();
        var artist = string.IsNullOrWhiteSpace(intent.Artist) ? "-" : intent.Artist.Trim();
        return $"{sourceService}|{sourceUrl}|{title}|{artist}";
    }
}
