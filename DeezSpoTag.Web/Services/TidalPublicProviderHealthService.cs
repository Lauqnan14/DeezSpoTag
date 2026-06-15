using DeezSpoTag.Integrations.Tidal;
using DeezSpoTag.Services.Download.Tidal;

namespace DeezSpoTag.Web.Services;

public sealed class TidalPublicProviderHealthService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);
    private readonly TidalDownloadService _downloadService;
    private readonly ITidalPublicProviderRegistry _providerRegistry;
    private readonly ILogger<TidalPublicProviderHealthService> _logger;

    public TidalPublicProviderHealthService(
        TidalDownloadService downloadService,
        ITidalPublicProviderRegistry providerRegistry,
        ILogger<TidalPublicProviderHealthService> logger)
    {
        _downloadService = downloadService;
        _providerRegistry = providerRegistry;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if ((await _providerRegistry.GetProvidersAsync(stoppingToken)).Any(static provider => provider.Enabled))
                {
                    await _downloadService.CheckPublicProvidersAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Periodic Tidal public provider health check failed.");
            }
            await Task.Delay(CheckInterval, stoppingToken);
        }
    }
}
