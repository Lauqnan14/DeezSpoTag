namespace DeezSpoTag.Web.Services;

public sealed class MediaServerSoundtrackMonitorService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(10);
    private readonly MediaServerSoundtrackService _service;
    private readonly ILogger<MediaServerSoundtrackMonitorService> _logger;

    public MediaServerSoundtrackMonitorService(
        MediaServerSoundtrackService service,
        ILogger<MediaServerSoundtrackMonitorService> logger)
    {
        _service = service;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunSyncIterationAsync(stoppingToken);

            try
            {
                await Task.Delay(RefreshInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunSyncIterationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _service.RefreshDiscoveredLibrariesAsync(cancellationToken);
            await _service.SyncPersistentMediaCacheAsync(fullRefresh: false, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "Soundtrack monitor refresh timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Soundtrack monitor refresh failed.");
        }
    }
}
