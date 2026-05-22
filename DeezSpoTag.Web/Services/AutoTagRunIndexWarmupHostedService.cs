using Microsoft.Extensions.Hosting;

namespace DeezSpoTag.Web.Services;

public sealed class AutoTagRunIndexWarmupHostedService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AutoTagRunIndexWarmupHostedService> _logger;

    public AutoTagRunIndexWarmupHostedService(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<AutoTagRunIndexWarmupHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
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
            if (!_configuration.GetValue("AutoTag:RunIndexWarmupOnStartup", false))
            {
                return Task.CompletedTask;
            }

            _serviceProvider.GetRequiredService<AutoTagService>().WarmRunIndexIfMissing();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "AutoTag run index warmup failed.");
        }

        return Task.CompletedTask;
    }
}
