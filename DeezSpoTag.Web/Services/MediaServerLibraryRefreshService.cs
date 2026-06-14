using DeezSpoTag.Integrations.Jellyfin;
using DeezSpoTag.Integrations.Plex;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed class MediaServerLibraryRefreshService
{
    private const int PlexTrackPageSize = 500;
    private const int RefreshAttemptCount = 3;
    private const string PlexService = "plex";
    private const string JellyfinService = "jellyfin";
    private const string NoneService = "none";
    private static readonly TimeSpan RefreshRetryDelay = TimeSpan.FromSeconds(1);

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

    public async Task<MediaServerRefreshSummary> RefreshConfiguredServersAsync(
        CancellationToken cancellationToken)
    {
        var state = await _authService.LoadAsync();
        var configuredServers = 0;
        var refreshedServers = 0;
        var failures = new List<string>();

        if (HasPlexConfiguration(state.Plex))
        {
            configuredServers++;
            if (await RefreshPlexAsync(state.Plex, cancellationToken))
            {
                refreshedServers++;
            }
            else
            {
                failures.Add(PlexService);
            }
        }

        if (HasJellyfinConfiguration(state.Jellyfin))
        {
            configuredServers++;
            if (await RefreshJellyfinAsync(state.Jellyfin, cancellationToken))
            {
                refreshedServers++;
            }
            else
            {
                failures.Add(JellyfinService);
            }
        }

        return new MediaServerRefreshSummary(configuredServers, refreshedServers, failures);
    }

    private async Task<bool> RefreshPlexAsync(PlexAuth? plex, CancellationToken cancellationToken)
    {
        if (!HasPlexConfiguration(plex))
        {
            return false;
        }

        var configuredPlex = plex!;
        var sections = await GetPlexLibrarySectionsWithRetryAsync(configuredPlex, cancellationToken);
        var musicSections = sections
            .Where(section => string.Equals(section.Type, "artist", StringComparison.OrdinalIgnoreCase))
            .Where(section => !section.Title.Contains("audiobook", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (musicSections.Count == 0)
        {
            _logger.LogWarning("Plex library refresh skipped because no music library sections were found.");
            return false;
        }

        var allSectionsRefreshed = true;
        foreach (var section in musicSections)
        {
            var refreshed = await RetryRefreshAsync(
                () => _plexApiClient.RefreshLibraryAsync(
                    configuredPlex.Url!,
                    configuredPlex.Token!,
                    section.Key,
                    cancellationToken),
                PlexService,
                section.Key,
                cancellationToken);
            if (!refreshed)
            {
                allSectionsRefreshed = false;
                _logger.LogWarning(
                    "Plex library refresh request failed for section {SectionKey} ({SectionTitle}).",
                    section.Key,
                    section.Title);
            }
        }

        try
        {
            await UpdatePlexTrackMetadataIndexAsync(configuredPlex, musicSections, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Plex library refresh was requested, but the local Plex track metadata index could not be updated.");
        }

        return allSectionsRefreshed;
    }

    private async Task<List<PlexLibrarySection>> GetPlexLibrarySectionsWithRetryAsync(
        PlexAuth plex,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= RefreshAttemptCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sections = await _plexApiClient.GetLibrarySectionsAsync(
                plex.Url!,
                plex.Token!,
                cancellationToken);
            if (sections.Count > 0)
            {
                return sections;
            }

            if (attempt < RefreshAttemptCount)
            {
                _logger.LogWarning(
                    "Plex library section discovery returned no sections on attempt {Attempt}/{AttemptCount}; retrying.",
                    attempt,
                    RefreshAttemptCount);
                await Task.Delay(RefreshRetryDelay, cancellationToken);
            }
        }

        return [];
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

    private async Task<bool> RefreshJellyfinAsync(JellyfinAuth? jellyfin, CancellationToken cancellationToken)
    {
        if (!HasJellyfinConfiguration(jellyfin))
        {
            return false;
        }

        var refreshed = await RetryRefreshAsync(
            () => _jellyfinApiClient.RefreshLibraryAsync(
                jellyfin!.Url!,
                jellyfin.ApiKey!,
                cancellationToken),
            JellyfinService,
            sectionKey: null,
            cancellationToken);
        if (!refreshed)
        {
            _logger.LogWarning("Jellyfin library refresh request failed.");
        }

        return refreshed;
    }

    private async Task<bool> RetryRefreshAsync(
        Func<Task<bool>> refresh,
        string service,
        string? sectionKey,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= RefreshAttemptCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await refresh())
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "{Service} library refresh attempt {Attempt}/{AttemptCount} failed for section {SectionKey}.",
                    service,
                    attempt,
                    RefreshAttemptCount,
                    sectionKey ?? "all");
            }

            if (attempt < RefreshAttemptCount)
            {
                await Task.Delay(RefreshRetryDelay, cancellationToken);
            }
        }

        return false;
    }

    private static bool HasPlexConfiguration(PlexAuth? plex) =>
        plex != null
        && !string.IsNullOrWhiteSpace(plex.Url)
        && !string.IsNullOrWhiteSpace(plex.Token);

    private static bool HasJellyfinConfiguration(JellyfinAuth? jellyfin) =>
        jellyfin != null
        && !string.IsNullOrWhiteSpace(jellyfin.Url)
        && !string.IsNullOrWhiteSpace(jellyfin.ApiKey);
}

public sealed record MediaServerRefreshSummary(
    int ConfiguredServerCount,
    int RefreshedServerCount,
    IReadOnlyList<string> FailedServers)
{
    public bool IsComplete => ConfiguredServerCount == RefreshedServerCount;
}
