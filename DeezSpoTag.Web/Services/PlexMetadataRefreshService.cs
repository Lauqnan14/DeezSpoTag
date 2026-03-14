using DeezSpoTag.Integrations.Plex;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed class PlexMetadataRefreshService : BackgroundService
{
    private readonly PlexApiClient _plexApiClient;
    private readonly PlatformAuthService _authService;
    private readonly LibraryRepository _libraryRepository;
    private readonly ILogger<PlexMetadataRefreshService> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromHours(6);

    public PlexMetadataRefreshService(
        PlexApiClient plexApiClient,
        PlatformAuthService authService,
        LibraryRepository libraryRepository,
        ILogger<PlexMetadataRefreshService> logger)
    {
        _plexApiClient = plexApiClient;
        _authService = authService;
        _libraryRepository = libraryRepository;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshRecentMetadataAsync(stoppingToken);
            }
            catch (OperationCanceledException ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Plex metadata refresh timed out; will retry on next interval.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Plex metadata refresh failed.");
            }

            try
            {
                await Task.Delay(_interval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
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
