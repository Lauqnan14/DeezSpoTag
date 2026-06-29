using DeezSpoTag.Integrations.Qobuz;
using DeezSpoTag.Services.Download.Qobuz;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Runtime;

namespace DeezSpoTag.Web.Services;

public sealed class QobuzPublicProviderHealthService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SchedulerPollInterval = TimeSpan.FromMinutes(1);
    private const string JobKey = "qobuz-public-provider-health";
    private readonly IQobuzDownloadService _qobuzDownloadService;
    private readonly IQobuzPublicProviderRegistry _providerRegistry;
    private readonly LibraryRepository _repository;
    private readonly BackgroundWorkCoordinator _workCoordinator;
    private readonly ILogger<QobuzPublicProviderHealthService> _logger;

    public QobuzPublicProviderHealthService(
        IQobuzDownloadService qobuzDownloadService,
        IQobuzPublicProviderRegistry providerRegistry,
        LibraryRepository repository,
        BackgroundWorkCoordinator workCoordinator,
        ILogger<QobuzPublicProviderHealthService> logger)
    {
        _qobuzDownloadService = qobuzDownloadService;
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
