using DeezSpoTag.Integrations.Plex;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed class PlexMetadataRefreshService : BackgroundService
{
    private readonly PlexApiClient _plexApiClient;
    private readonly PlatformAuthService _authService;
    private readonly LibraryRepository _libraryRepository;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PlexMetadataRefreshService> _logger;
    private readonly DeezSpoTag.Services.Runtime.BackgroundWorkCoordinator _workCoordinator;
    private readonly TimeSpan _interval = TimeSpan.FromHours(6);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(15);
    private const string JobKey = "plex-metadata-refresh";

    public PlexMetadataRefreshService(
        PlexApiClient plexApiClient,
        PlatformAuthService authService,
        LibraryRepository libraryRepository,
        DeezSpoTag.Services.Runtime.BackgroundWorkCoordinator workCoordinator,
        IConfiguration configuration,
        ILogger<PlexMetadataRefreshService> logger)
    {
        _plexApiClient = plexApiClient;
        _authService = authService;
        _libraryRepository = libraryRepository;
        _workCoordinator = workCoordinator;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!BackgroundAutomationPolicy.IsEnabled(_configuration, "PlexMetadataRefresh"))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            if (await _libraryRepository.TryClaimBackgroundJobAsync(JobKey, _interval, now, stoppingToken))
            {
                await RunClaimedRefreshAsync(stoppingToken);
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunClaimedRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _workCoordinator.RunHeavyWorkAsync(RefreshRecentMetadataAsync, cancellationToken);
            await _libraryRepository.CompleteBackgroundJobAsync(JobKey, _interval, DateTimeOffset.UtcNow, cancellationToken);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Plex metadata refresh timed out; will retry later.");
            await _libraryRepository.FailBackgroundJobAsync(JobKey, TimeSpan.FromMinutes(30), DateTimeOffset.UtcNow, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Plex metadata refresh failed.");
            await _libraryRepository.FailBackgroundJobAsync(JobKey, TimeSpan.FromMinutes(30), DateTimeOffset.UtcNow, CancellationToken.None);
        }
    }

    private async Task RefreshRecentMetadataAsync(CancellationToken cancellationToken)
    {
        var auth = await _authService.LoadAsync();
        var plex = auth.Plex;
        if (plex is null || string.IsNullOrWhiteSpace(plex.Url) || string.IsNullOrWhiteSpace(plex.Token))
        {
            _logger.LogInformation("Plex auth missing; skipping metadata refresh.");
            return;
        }

        var history = await _plexApiClient.GetHistoryAsync(plex.Url, plex.Token, cancellationToken);
        var ratingKeys = history
            .Select(item => item.RatingKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct()
            .Take(200)
            .ToList();
        if (ratingKeys.Count == 0)
        {
            return;
        }

        var trackMap = await _libraryRepository.GetTrackIdsByPlexRatingKeysAsync(ratingKeys, cancellationToken);
        foreach (var entry in trackMap)
        {
            var metadata = await _plexApiClient.GetTrackMetadataAsync(
                plex.Url,
                plex.Token,
                entry.Key,
                cancellationToken);
            if (metadata is null)
            {
                continue;
            }

            await _libraryRepository.UpsertPlexTrackMetadataAsync(
                new PlexTrackMetadataDto(
                    entry.Value,
                    metadata.RatingKey,
                    metadata.UserRating,
                    metadata.Genres,
                    metadata.Moods,
                    DateTimeOffset.UtcNow),
                cancellationToken);
        }
    }
}
