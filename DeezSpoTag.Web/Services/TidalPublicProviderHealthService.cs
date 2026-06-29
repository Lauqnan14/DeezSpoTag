using DeezSpoTag.Integrations.Tidal;
using DeezSpoTag.Services.Download.Tidal;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Runtime;

namespace DeezSpoTag.Web.Services;

public sealed class TidalPublicProviderHealthService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SchedulerPollInterval = TimeSpan.FromMinutes(1);
    private const string JobKey = "tidal-public-provider-health";
    private readonly TidalDownloadService _downloadService;
    private readonly ITidalPublicProviderRegistry _providerRegistry;
    private readonly LibraryRepository _repository;
    private readonly BackgroundWorkCoordinator _workCoordinator;
    private readonly ILogger<TidalPublicProviderHealthService> _logger;

    public TidalPublicProviderHealthService(
        TidalDownloadService downloadService,
        ITidalPublicProviderRegistry providerRegistry,
        LibraryRepository repository,
        BackgroundWorkCoordinator workCoordinator,
        ILogger<TidalPublicProviderHealthService> logger)
    {
        _downloadService = downloadService;
        _providerRegistry = providerRegistry;
        _repository = repository;
        _workCoordinator = workCoordinator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (await _repository.TryClaimBackgroundJobAsync(JobKey, CheckInterval, DateTimeOffset.UtcNow, stoppingToken))
            {
                await _workCoordinator.RunHeavyWorkAsync(CheckEnabledProvidersAsync, stoppingToken);
                await _repository.CompleteBackgroundJobAsync(JobKey, CheckInterval, DateTimeOffset.UtcNow, stoppingToken);
            }
            await Task.Delay(SchedulerPollInterval, stoppingToken);
        }
    }

    private async Task CheckEnabledProvidersAsync(CancellationToken cancellationToken)
    {
        try
        {
            if ((await _providerRegistry.GetProvidersAsync(cancellationToken)).Any(static provider => provider.Enabled))
            {
                await _downloadService.CheckPublicProvidersAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Periodic Tidal public provider health check failed.");
        }
    }
}
