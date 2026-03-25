namespace DeezSpoTag.Web.Services;

public sealed class MediaServerSoundtrackMonitorService : BackgroundService
{
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
            await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _service.RefreshDiscoveredLibrariesAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Soundtrack monitor refresh failed.");
            }

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
}
