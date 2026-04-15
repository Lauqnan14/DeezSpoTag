using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

/// <summary>
/// Background worker that assigns completed analysis tracks to mood buckets.
/// Runs every 30 seconds, processing 50 tracks per batch.
/// </summary>
public sealed class MoodBucketBackgroundService : BackgroundService
{
    private const int BatchSize = 50;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly MoodBucketService _moodBucketService;
    private readonly LibraryRepository _repository;
    private readonly ILogger<MoodBucketBackgroundService> _logger;

    public MoodBucketBackgroundService(
        MoodBucketService moodBucketService,
        LibraryRepository repository,
        ILogger<MoodBucketBackgroundService> logger)
    {
        _moodBucketService = moodBucketService;
        _repository = repository;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var trackIds = await _repository.GetUnbucketedAnalyzedTrackIdsAsync(BatchSize, stoppingToken);
                if (trackIds.Count > 0)
                {
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
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "MoodBucket worker error");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
