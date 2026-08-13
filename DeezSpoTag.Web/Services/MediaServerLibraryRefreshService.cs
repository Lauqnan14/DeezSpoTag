using DeezSpoTag.Integrations.Jellyfin;
using DeezSpoTag.Integrations.Navidrome;
using DeezSpoTag.Integrations.Plex;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed class MediaServerLibraryRefreshService
{
    private const int PlexTrackPageSize = 500;
    private const int RefreshAttemptCount = 3;
    private const string PlexService = "plex";
    private const string JellyfinService = "jellyfin";
    private const string NavidromeService = "navidrome";
    private const string NoneService = "none";
    private static readonly TimeSpan RefreshRetryDelay = TimeSpan.FromSeconds(1);

    private readonly PlatformAuthService _authService;
    private readonly PlexApiClient _plexApiClient;
    private readonly JellyfinApiClient _jellyfinApiClient;
    private readonly NavidromeApiClient _navidromeApiClient;
    private readonly LibraryRepository _libraryRepository;
    private readonly ILogger<MediaServerLibraryRefreshService> _logger;

    public MediaServerLibraryRefreshService(
        PlatformAuthService authService,
        PlexApiClient plexApiClient,
        JellyfinApiClient jellyfinApiClient,
        NavidromeApiClient navidromeApiClient,
        LibraryRepository libraryRepository,
        ILogger<MediaServerLibraryRefreshService> logger)
    {
        _authService = authService;
        _plexApiClient = plexApiClient;
        _jellyfinApiClient = jellyfinApiClient;
        _navidromeApiClient = navidromeApiClient;
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

        if (normalizedService == NavidromeService)
        {
            await RefreshNavidromeAsync(state.Navidrome, cancellationToken);
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

    public Task<MediaServerRefreshSummary> RefreshConfiguredServersAsync(
        CancellationToken cancellationToken)
        => RefreshConfiguredServersAsync(cancellationToken, updateTrackIndex: true);

    public async Task<MediaServerRefreshSummary> RefreshConfiguredServersAsync(
        CancellationToken cancellationToken,
        bool updateTrackIndex)
    {
        var state = await _authService.LoadAsync();
        var configuredServers = 0;
        var refreshedServers = 0;
        var failures = new List<string>();

        if (HasPlexConfiguration(state.Plex))
        {
            configuredServers++;
            if (await RefreshPlexAsync(state.Plex, updateTrackIndex, cancellationToken))
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
            if (await RefreshJellyfinAsync(state.Jellyfin, updateTrackIndex, cancellationToken))
            {
                refreshedServers++;
            }
            else
            {
                failures.Add(JellyfinService);
            }
        }

        if (HasNavidromeConfiguration(state.Navidrome))
        {
            configuredServers++;
            if (await RefreshNavidromeAsync(state.Navidrome, cancellationToken))
            {
                refreshedServers++;
            }
            else
            {
                failures.Add(NavidromeService);
            }
        }

        return new MediaServerRefreshSummary(configuredServers, refreshedServers, failures);
    }

    public async Task<IReadOnlyList<string>> GetConfiguredServicesAsync()
    {
        var state = await _authService.LoadAsync();
        var services = new List<string>(3);
        if (HasPlexConfiguration(state.Plex))
        {
            services.Add(PlexService);
        }
        if (HasJellyfinConfiguration(state.Jellyfin))
        {
            services.Add(JellyfinService);
        }
        if (HasNavidromeConfiguration(state.Navidrome))
        {
            services.Add(NavidromeService);
        }
        return services;
    }

    public async Task<bool> RequestLibraryRefreshAsync(
        string service,
        CancellationToken cancellationToken)
    {
        var state = await _authService.LoadAsync();
        return service.Trim().ToLowerInvariant() switch
        {
            PlexService => await RefreshPlexAsync(state.Plex, updateTrackIndex: false, cancellationToken: cancellationToken),
            JellyfinService => await RefreshJellyfinAsync(state.Jellyfin, updateTrackIndex: false, cancellationToken: cancellationToken),
            NavidromeService => await RefreshNavidromeAsync(state.Navidrome, cancellationToken),
            _ => false
        };
    }

    private Task<bool> RefreshPlexAsync(PlexAuth? plex, CancellationToken cancellationToken)
        => RefreshPlexAsync(plex, updateTrackIndex: true, cancellationToken: cancellationToken);

    private async Task<bool> RefreshPlexAsync(
        PlexAuth? plex,
        bool updateTrackIndex,
        CancellationToken cancellationToken)
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
            if (updateTrackIndex)
            {
                await UpdatePlexTrackMetadataIndexAsync(configuredPlex, musicSections, cancellationToken);
            }
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
                await _libraryRepository.UpsertMediaServerTrackMetadataAsync(
                    tracks
                        .Where(track => filePathMap.ContainsKey(track.FilePath))
                        .Select(track => new MediaServerTrackMetadataUpsertDto(
                            filePathMap[track.FilePath],
                            PlexService,
                            track.RatingKey,
                            track.FilePath,
                            now))
                        .ToList(),
                    cancellationToken);
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

    private Task<bool> RefreshJellyfinAsync(JellyfinAuth? jellyfin, CancellationToken cancellationToken)
        => RefreshJellyfinAsync(jellyfin, updateTrackIndex: true, cancellationToken: cancellationToken);

    private async Task<bool> RefreshJellyfinAsync(
        JellyfinAuth? jellyfin,
        bool updateTrackIndex,
        CancellationToken cancellationToken)
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
            return false;
        }

        try
        {
            if (updateTrackIndex)
            {
                await UpdateJellyfinTrackMetadataIndexAsync(jellyfin!, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Jellyfin library refresh was requested, but the local Jellyfin track metadata index could not be updated.");
        }

        return refreshed;
    }

    private async Task UpdateJellyfinTrackMetadataIndexAsync(
        JellyfinAuth jellyfin,
        CancellationToken cancellationToken)
    {
        if (!_libraryRepository.IsConfigured)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(jellyfin.UserId))
        {
            _logger.LogWarning("Jellyfin track metadata index skipped because Jellyfin user id is missing.");
            return;
        }

        var libraries = await _jellyfinApiClient.GetLibrariesAsync(
            jellyfin.Url!,
            jellyfin.ApiKey!,
            cancellationToken);
        var musicLibraries = libraries
            .Where(static library => string.Equals(library.CollectionType, "music", StringComparison.OrdinalIgnoreCase))
            .Where(static library => !string.IsNullOrWhiteSpace(library.Id))
            .ToList();
        if (musicLibraries.Count == 0)
        {
            _logger.LogWarning("Jellyfin track metadata index skipped because no music libraries were found.");
            return;
        }

        var mappedCount = 0;
        var seenItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in musicLibraries)
        {
            var offset = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await _jellyfinApiClient.GetAudioTracksAsync(
                    jellyfin.Url!,
                    jellyfin.ApiKey!,
                    jellyfin.UserId!,
                    library.Id,
                    offset,
                    PlexTrackPageSize,
                    cancellationToken);
                if (page.Count == 0)
                {
                    break;
                }

                var tracks = page
                    .Where(track => !string.IsNullOrWhiteSpace(track.Id)
                                    && !string.IsNullOrWhiteSpace(track.FilePath)
                                    && seenItemIds.Add(track.Id))
                    .ToList();
                var filePathMap = await _libraryRepository.GetTrackIdsByFilePathsAsync(
                    tracks.Select(static track => track.FilePath!).ToList(),
                    cancellationToken);
                var now = DateTimeOffset.UtcNow;
                var upserts = tracks
                    .Where(track => !string.IsNullOrWhiteSpace(track.FilePath)
                                    && filePathMap.ContainsKey(track.FilePath!))
                    .Select(track => new MediaServerTrackMetadataUpsertDto(
                        filePathMap[track.FilePath!],
                        JellyfinService,
                        track.Id,
                        track.FilePath,
                        now))
                    .ToList();
                await _libraryRepository.UpsertMediaServerTrackMetadataAsync(upserts, cancellationToken);
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
            _logger.LogInformation("Jellyfin track metadata index updated: mappedTracks={MappedTracks}.", mappedCount);
        }
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

    private async Task<bool> RefreshNavidromeAsync(NavidromeAuth? navidrome, CancellationToken cancellationToken)
    {
        if (!HasNavidromeConfiguration(navidrome))
        {
            return false;
        }

        return await RetryRefreshAsync(
            () => _navidromeApiClient.StartScanAsync(
                navidrome!.Url!,
                navidrome.Username!,
                navidrome.Password!,
                cancellationToken),
            NavidromeService,
            sectionKey: null,
            cancellationToken);
    }

    private static bool HasPlexConfiguration(PlexAuth? plex) =>
        plex != null
        && !string.IsNullOrWhiteSpace(plex.Url)
        && !string.IsNullOrWhiteSpace(plex.Token);

    private static bool HasJellyfinConfiguration(JellyfinAuth? jellyfin) =>
        jellyfin != null
        && !string.IsNullOrWhiteSpace(jellyfin.Url)
        && !string.IsNullOrWhiteSpace(jellyfin.ApiKey);

    private static bool HasNavidromeConfiguration(NavidromeAuth? navidrome) =>
        navidrome != null
        && !string.IsNullOrWhiteSpace(navidrome.Url)
        && !string.IsNullOrWhiteSpace(navidrome.Username)
        && !string.IsNullOrWhiteSpace(navidrome.Password);
}

public sealed record MediaServerRefreshSummary(
    int ConfiguredServerCount,
    int RefreshedServerCount,
    IReadOnlyList<string> FailedServers)
{
    public bool IsComplete => ConfiguredServerCount == RefreshedServerCount;
}
