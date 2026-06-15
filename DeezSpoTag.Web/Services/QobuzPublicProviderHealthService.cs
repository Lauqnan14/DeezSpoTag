using DeezSpoTag.Integrations.Qobuz;
using DeezSpoTag.Services.Download.Qobuz;

namespace DeezSpoTag.Web.Services;

public sealed class QobuzPublicProviderHealthService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);
    private readonly IQobuzDownloadService _qobuzDownloadService;
    private readonly IQobuzPublicProviderRegistry _providerRegistry;
    private readonly ILogger<QobuzPublicProviderHealthService> _logger;

    public QobuzPublicProviderHealthService(
        IQobuzDownloadService qobuzDownloadService,
        IQobuzPublicProviderRegistry providerRegistry,
        ILogger<QobuzPublicProviderHealthService> logger)
    {
        _qobuzDownloadService = qobuzDownloadService;
        _providerRegistry = providerRegistry;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(InitialDelay, stoppingToken);
        using var timer = new PeriodicTimer(CheckInterval);

        do
        {
            await CheckEnabledProvidersAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CheckEnabledProvidersAsync(CancellationToken cancellationToken)
    {
        try
        {
            var providers = await _providerRegistry.GetProvidersAsync(cancellationToken);
            if (!providers.Any(provider => provider.Enabled))
            {
                return;
            }

            await _qobuzDownloadService.CheckPublicProvidersAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Periodic Qobuz public provider health check failed.");
        }
    }
}
