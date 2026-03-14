using DeezSpoTag.Services.Download.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace DeezSpoTag.Web.Services;

public sealed class DownloadIntentBackgroundService : BackgroundService
{
    private readonly IDownloadIntentBackgroundQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DownloadIntentBackgroundService> _logger;

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
            try
            {
                await service.EnqueueAsync(intent, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Background intent enqueue failed for {SourceUrl}", intent.SourceUrl);
            }
        }
    }
}
