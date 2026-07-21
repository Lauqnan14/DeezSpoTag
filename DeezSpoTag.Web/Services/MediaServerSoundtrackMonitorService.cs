namespace DeezSpoTag.Web.Services;

public sealed class MediaServerSoundtrackMonitorService : BackgroundService
{
    private static readonly TimeSpan WeeklyRefreshInterval = TimeSpan.FromDays(7);
    private static readonly TimeSpan NewItemProbeInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SchedulerPollInterval = TimeSpan.FromMinutes(15);
    private const string WeeklyJobKey = "media-server-soundtrack-sync";
    private const string NewItemJobKey = "media-server-soundtrack-new-items";
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
            if (await _repository.TryClaimBackgroundJobAsync(WeeklyJobKey, WeeklyRefreshInterval, DateTimeOffset.UtcNow, stoppingToken))
            {
                await _workCoordinator.RunHeavyWorkAsync(RunWeeklySyncIterationAsync, stoppingToken);
                await _repository.CompleteBackgroundJobAsync(WeeklyJobKey, WeeklyRefreshInterval, DateTimeOffset.UtcNow, stoppingToken);
            }
            if (await _repository.TryClaimBackgroundJobAsync(NewItemJobKey, NewItemProbeInterval, DateTimeOffset.UtcNow, stoppingToken))
            {
                await _workCoordinator.RunHeavyWorkAsync(RunNewItemDetectionIterationAsync, stoppingToken);
                await _repository.CompleteBackgroundJobAsync(NewItemJobKey, NewItemProbeInterval, DateTimeOffset.UtcNow, stoppingToken);
            }
            await Task.Delay(SchedulerPollInterval, stoppingToken);
        }
    }

    private async Task RunWeeklySyncIterationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _service.RunWeeklyBackgroundSyncAsync(cancellationToken);
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

    private async Task RunNewItemDetectionIterationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _service.DetectAndResolveNewItemsAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "New soundtrack item detection timed out.");
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(ex, "New soundtrack item detection failed.");
        }
    }
}
