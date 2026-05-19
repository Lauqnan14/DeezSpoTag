using Microsoft.Extensions.Hosting;

namespace DeezSpoTag.Web.Services;

public sealed class AutoTagRunIndexWarmupHostedService : BackgroundService
{
    private readonly AutoTagService _autoTagService;
    private readonly ILogger<AutoTagRunIndexWarmupHostedService> _logger;

    public AutoTagRunIndexWarmupHostedService(
        AutoTagService autoTagService,
        ILogger<AutoTagRunIndexWarmupHostedService> logger)
    {
        _autoTagService = autoTagService;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (stoppingToken.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        try
        {
            _autoTagService.WarmRunIndexIfMissing();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "AutoTag run index warmup failed.");
        }

        return Task.CompletedTask;
    }
}
