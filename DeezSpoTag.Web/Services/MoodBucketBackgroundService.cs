using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

/// <summary>
/// One-time compatibility backfill for analyzed tracks created before mood assignment
/// became part of the analysis completion path.
/// </summary>
public sealed class MoodBucketBackgroundService : BackgroundService
{
    private const int BatchSize = 50;
    private readonly MoodBucketService _moodBucketService;
    private readonly LibraryRepository _repository;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MoodBucketBackgroundService> _logger;
    private readonly DeezSpoTag.Services.Runtime.BackgroundWorkCoordinator _workCoordinator;

    public MoodBucketBackgroundService(
        MoodBucketService moodBucketService,
        LibraryRepository repository,
        DeezSpoTag.Services.Runtime.BackgroundWorkCoordinator workCoordinator,
        IConfiguration configuration,
        ILogger<MoodBucketBackgroundService> logger)
    {
        _moodBucketService = moodBucketService;
        _repository = repository;
        _workCoordinator = workCoordinator;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!BackgroundAutomationPolicy.IsEnabled(_configuration, "MoodBucket"))
        {
            return;
        }

        await _workCoordinator.RunHeavyWorkAsync(ProcessBacklogAsync, stoppingToken);
    }

    private async Task ProcessBacklogAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await ProcessBatchAsync(stoppingToken) == 0)
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "MoodBucket backfill failed");
                return;
            }
        }
    }

    private async Task<int> ProcessBatchAsync(CancellationToken stoppingToken)
    {
        var trackIds = await _repository.GetUnbucketedAnalyzedTrackIdsAsync(BatchSize, stoppingToken);
        if (trackIds.Count == 0)
        {
            return 0;
        }

        var totalAssigned = 0;
        foreach (var trackId in trackIds)
        {
            var moods = await _moodBucketService.AssignTrackToMoodsAsync(trackId, stoppingToken);
            totalAssigned += moods.Count;
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "MoodBucket backfill: processed {Count} tracks, {Assigned} mood assignments",
                trackIds.Count, totalAssigned);
        }
        return trackIds.Count;
    }
}
