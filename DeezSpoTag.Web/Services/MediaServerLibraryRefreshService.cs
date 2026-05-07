using DeezSpoTag.Integrations.Jellyfin;
using DeezSpoTag.Integrations.Plex;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed class MediaServerLibraryRefreshService
{
    private const int PlexTrackPageSize = 500;
    private const string PlexService = "plex";
    private const string JellyfinService = "jellyfin";
    private const string NoneService = "none";

    private readonly PlatformAuthService _authService;
    private readonly PlexApiClient _plexApiClient;
    private readonly JellyfinApiClient _jellyfinApiClient;
    private readonly LibraryRepository _libraryRepository;
    private readonly ILogger<MediaServerLibraryRefreshService> _logger;

    public MediaServerLibraryRefreshService(
        PlatformAuthService authService,
        PlexApiClient plexApiClient,
        JellyfinApiClient jellyfinApiClient,
        LibraryRepository libraryRepository,
        ILogger<MediaServerLibraryRefreshService> logger)
    {
        _authService = authService;
        _plexApiClient = plexApiClient;
        _jellyfinApiClient = jellyfinApiClient;
        _libraryRepository = libraryRepository;
        _logger = logger;
    }

    public async Task RefreshAsync(string? service, CancellationToken cancellationToken)
    {
        var normalizedService = (service ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedService == NoneService)
        {
            return;
        }

        var state = await _authService.LoadAsync();
        if (normalizedService == JellyfinService)
        {
            await RefreshJellyfinAsync(state.Jellyfin, cancellationToken);
            return;
        }

        if (normalizedService == PlexService)
        {
            await RefreshPlexAsync(state.Plex, cancellationToken);
            return;
        }

        if (state.Plex is { } plex
            && !string.IsNullOrWhiteSpace(plex.Url)
            && !string.IsNullOrWhiteSpace(plex.Token))
        {
            await RefreshPlexAsync(plex, cancellationToken);
            return;
        }

        await RefreshJellyfinAsync(state.Jellyfin, cancellationToken);
    }

    private async Task RefreshPlexAsync(PlexAuth? plex, CancellationToken cancellationToken)
    {
        if (plex == null
            || string.IsNullOrWhiteSpace(plex.Url)
            || string.IsNullOrWhiteSpace(plex.Token))
        {
            return;
        }

        var sections = await _plexApiClient.GetLibrarySectionsAsync(
            plex.Url,
            plex.Token,
            cancellationToken);
        var musicSections = sections
            .Where(section => string.Equals(section.Type, "artist", StringComparison.OrdinalIgnoreCase))
            .Where(section => !section.Title.Contains("audiobook", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var section in musicSections)
        {
            var refreshed = await _plexApiClient.RefreshLibraryAsync(
                plex.Url,
                plex.Token,
                section.Key,
                cancellationToken);
            if (!refreshed)
            {
                _logger.LogWarning(
                    "Plex library refresh request failed for section {SectionKey} ({SectionTitle}).",
                    section.Key,
                    section.Title);
            }
        }

        await UpdatePlexTrackMetadataIndexAsync(plex, musicSections, cancellationToken);
    }

    private async Task UpdatePlexTrackMetadataIndexAsync(
        PlexAuth plex,
        List<PlexLibrarySection> musicSections,
        CancellationToken cancellationToken)
    {
        if (!_libraryRepository.IsConfigured || musicSections.Count == 0)
        {
            return;
        }

        var mappedCount = 0;
        var seenRatingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in musicSections)
        {
            var offset = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await _plexApiClient.GetLibraryTracksAsync(
                    plex.Url!,
                    plex.Token!,
                    section.Key,
                    offset,
                    PlexTrackPageSize,
                    cancellationToken);
                if (page.Count == 0)
                {
                    break;
                }

                var tracks = page
                    .Where(track => !string.IsNullOrWhiteSpace(track.RatingKey)
                                    && !string.IsNullOrWhiteSpace(track.FilePath)
                                    && seenRatingKeys.Add(track.RatingKey))
                    .ToList();
                var filePathMap = await _libraryRepository.GetTrackIdsByFilePathsAsync(
                    tracks.Select(static track => track.FilePath).ToList(),
                    cancellationToken);
                var now = DateTimeOffset.UtcNow;
                var upserts = tracks
                    .Where(track => filePathMap.ContainsKey(track.FilePath))
                    .Select(track => new PlexTrackMetadataUpsertDto(
                        filePathMap[track.FilePath],
                        track.RatingKey,
                        now))
                    .ToList();
                await _libraryRepository.UpsertPlexTrackMetadataAsync(upserts, cancellationToken);
                mappedCount += upserts.Count;

                if (page.Count < PlexTrackPageSize)
                {
                    break;
                }

                offset += PlexTrackPageSize;
            }
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Plex track metadata index updated: mappedTracks={MappedTracks}.", mappedCount);
        }
    }

    private async Task RefreshJellyfinAsync(JellyfinAuth? jellyfin, CancellationToken cancellationToken)
    {
        if (jellyfin == null
            || string.IsNullOrWhiteSpace(jellyfin.Url)
            || string.IsNullOrWhiteSpace(jellyfin.ApiKey))
        {
            return;
        }

        var refreshed = await _jellyfinApiClient.RefreshLibraryAsync(
            jellyfin.Url,
            jellyfin.ApiKey,
            cancellationToken);
        if (!refreshed)
        {
            _logger.LogWarning("Jellyfin library refresh request failed.");
        }
    }
}
