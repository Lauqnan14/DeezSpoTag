namespace DeezSpoTag.Web.Services;

public sealed class MediaServerSoundtrackMonitorService : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan SchedulerPollInterval = TimeSpan.FromMinutes(15);
    private const string JobKey = "media-server-soundtrack-sync";
    private readonly MediaServerSoundtrackService _service;
    private readonly DeezSpoTag.Services.Library.LibraryRepository _repository;
    private readonly DeezSpoTag.Services.Runtime.BackgroundWorkCoordinator _workCoordinator;
    private readonly ILogger<MediaServerSoundtrackMonitorService> _logger;

    public MediaServerSoundtrackMonitorService(
        MediaServerSoundtrackService service,
        DeezSpoTag.Services.Library.LibraryRepository repository,
        DeezSpoTag.Services.Runtime.BackgroundWorkCoordinator workCoordinator,
        ILogger<MediaServerSoundtrackMonitorService> logger)
    {
        _service = service;
        _repository = repository;
        _workCoordinator = workCoordinator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (await _repository.TryClaimBackgroundJobAsync(JobKey, RefreshInterval, DateTimeOffset.UtcNow, stoppingToken))
            {
                await _workCoordinator.RunHeavyWorkAsync(RunSyncIterationAsync, stoppingToken);
                await _repository.CompleteBackgroundJobAsync(JobKey, RefreshInterval, DateTimeOffset.UtcNow, stoppingToken);
            }
            await Task.Delay(SchedulerPollInterval, stoppingToken);
        }
    }

    private async Task RunSyncIterationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _service.RunScheduledBackgroundSyncAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "Soundtrack monitor run timed out.");
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(ex, "Soundtrack monitor run failed.");
        }
    }
}
