using System.Collections.Concurrent;
using System.Text.Json;
using DeezSpoTag.Core.Models.Deezer;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Integrations.Jellyfin;
using DeezSpoTag.Integrations.Plex;

namespace DeezSpoTag.Web.Services;

public sealed class MediaServerSoundtrackService
{
    private const int DefaultItemLimit = 120;
    private static readonly TimeSpan SoundtrackCacheTtl = TimeSpan.FromHours(12);

    private readonly PlatformAuthService _platformAuthService;
    private readonly PlexApiClient _plexApiClient;
    private readonly JellyfinApiClient _jellyfinApiClient;
    private readonly DeezerClient _deezerClient;
    private readonly MediaServerSoundtrackStore _store;
    private readonly ILogger<MediaServerSoundtrackService> _logger;
    private readonly ConcurrentDictionary<string, (DateTimeOffset CachedAtUtc, MediaServerSoundtrackMatchDto Match)> _soundtrackCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _soundtrackWarmupInFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _soundtrackWarmupGate = new(3, 3);

    private sealed class TvEpisodeFetchResult
    {
        public string ShowId { get; set; } = string.Empty;

        public string ShowTitle { get; set; } = string.Empty;

        public string? ShowImageUrl { get; set; }

        public List<MediaServerTvShowSeasonItem> Seasons { get; set; } = new();

        public List<MediaServerTvShowEpisodeItem> Episodes { get; set; } = new();
    }

    public MediaServerSoundtrackService(
        PlatformAuthService platformAuthService,
        PlexApiClient plexApiClient,
        JellyfinApiClient jellyfinApiClient,
        DeezerClient deezerClient,
        MediaServerSoundtrackStore store,
        ILogger<MediaServerSoundtrackService> logger)
    {
        _platformAuthService = platformAuthService;
        _plexApiClient = plexApiClient;
        _jellyfinApiClient = jellyfinApiClient;
        _deezerClient = deezerClient;
        _store = store;
        _logger = logger;
    }

    public async Task<MediaServerSoundtrackConfigurationDto> GetConfigurationAsync(bool refreshDiscovery, CancellationToken cancellationToken)
    {
        if (refreshDiscovery)
        {
            await RefreshDiscoveredLibrariesAsync(cancellationToken);
        }

        var auth = await _platformAuthService.LoadAsync();
        var settings = await _store.LoadAsync(cancellationToken);
        return BuildConfigurationDto(settings, auth, await DiscoverLibrariesAsync(auth, cancellationToken));
    }

    public async Task<MediaServerSoundtrackConfigurationDto> SaveConfigurationAsync(
        MediaServerSoundtrackConfigurationUpdateRequest request,
        CancellationToken cancellationToken)
    {
        request ??= new MediaServerSoundtrackConfigurationUpdateRequest();
        await _store.UpdateAsync(settings =>
        {
            var changed = false;
            foreach (var serverUpdate in request.Servers)
            {
                var serverType = NormalizeServerType(serverUpdate?.ServerType);
                if (string.IsNullOrWhiteSpace(serverType))
                {
                    continue;
                }

                var server = GetOrCreateServer(settings, serverType);
                if (serverUpdate?.AutoIncludeNewLibraries.HasValue == true
                    && server.AutoIncludeNewLibraries != serverUpdate.AutoIncludeNewLibraries.Value)
                {
                    server.AutoIncludeNewLibraries = serverUpdate.AutoIncludeNewLibraries.Value;
                    changed = true;
                }

                foreach (var libraryUpdate in serverUpdate?.Libraries ?? Enumerable.Empty<MediaServerSoundtrackLibraryPreferenceUpdateDto>())
                {
                    var libraryId = NormalizeText(libraryUpdate?.LibraryId);
                    if (string.IsNullOrWhiteSpace(libraryId))
                    {
                        continue;
                    }

                    if (!server.Libraries.TryGetValue(libraryId, out var library))
                    {
                        library = new MediaServerSoundtrackLibrarySettings
                        {
                            LibraryId = libraryId,
                            Name = libraryId,
                            Category = MediaServerSoundtrackConstants.MovieCategory,
                            Enabled = true,
                            Ignored = false,
                            FirstDiscoveredUtc = DateTimeOffset.UtcNow,
                            LastSeenUtc = DateTimeOffset.UtcNow
                        };
                        server.Libraries[libraryId] = library;
                        changed = true;
                    }

                    if (libraryUpdate?.Enabled.HasValue == true && library.Enabled != libraryUpdate.Enabled.Value)
                    {
                        library.Enabled = libraryUpdate.Enabled.Value;
                        changed = true;
                    }

                    if (libraryUpdate?.Ignored.HasValue == true && library.Ignored != libraryUpdate.Ignored.Value)
                    {
                        library.Ignored = libraryUpdate.Ignored.Value;
                        changed = true;
                    }

                    library.UserConfigured = true;
                }
            }

            return changed;
        }, cancellationToken);

        return await GetConfigurationAsync(refreshDiscovery: false, cancellationToken);
    }

    public async Task<MediaServerSoundtrackConfigurationDto> RefreshDiscoveredLibrariesAsync(CancellationToken cancellationToken)
    {
        var auth = await _platformAuthService.LoadAsync();
        var discovered = await DiscoverLibrariesAsync(auth, cancellationToken);
        var now = DateTimeOffset.UtcNow;

        await _store.UpdateAsync(settings => MergeDiscoveredLibraries(settings, discovered, now), cancellationToken);
        var reloaded = await _store.LoadAsync(cancellationToken);
        return BuildConfigurationDto(reloaded, auth, discovered);
    }

    public async Task<MediaServerSoundtrackItemsResponseDto> GetItemsAsync(
        string? category,
        string? serverType,
        string? libraryId,
        int? offset,
        int? limit,
        CancellationToken cancellationToken)
    {
        var normalizedCategory = NormalizeCategory(category);
        var normalizedLibraryId = NormalizeText(libraryId);
        var itemLimit = Math.Clamp(limit.GetValueOrDefault(DefaultItemLimit), 1, 300);
        var itemOffset = Math.Max(offset.GetValueOrDefault(0), 0);
        var hasSpecificLibrary = !string.IsNullOrWhiteSpace(normalizedLibraryId);

        var auth = await _platformAuthService.LoadAsync();
        var settings = await _store.LoadAsync(cancellationToken);

        var targets = ResolveTargetLibraries(settings, auth, normalizedCategory, serverType, normalizedLibraryId);
        if (targets.Count == 0)
        {
            return new MediaServerSoundtrackItemsResponseDto
            {
                Category = normalizedCategory,
                Total = 0,
                Items = new List<MediaServerSoundtrackItemDto>()
            };
        }

        var items = new List<MediaServerContentItem>();
        foreach (var target in targets)
        {
            var fetchOffset = hasSpecificLibrary ? itemOffset : 0;
            var fetched = await FetchLibraryItemsAsync(auth, target, fetchOffset, itemLimit, cancellationToken);
            if (fetched.Count == 0)
            {
                continue;
            }

            items.AddRange(fetched);
            if (items.Count >= itemLimit)
            {
                break;
            }
        }

        items = items
            .OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Year ?? 0)
            .Take(itemLimit)
            .ToList();

        var resultItems = new List<MediaServerSoundtrackItemDto>(items.Count);
        foreach (var item in items)
        {
            var match = ResolveSoundtrackFromCacheOrQueue(item);
            resultItems.Add(new MediaServerSoundtrackItemDto
            {
                ServerType = item.ServerType,
                ServerLabel = GetServerDisplayName(item.ServerType),
                LibraryId = item.LibraryId,
                LibraryName = item.LibraryName,
                Category = item.Category,
                ItemId = item.ItemId,
                Title = item.Title,
                Year = item.Year,
                ImageUrl = item.ImageUrl,
                Soundtrack = match
            });
        }

        return new MediaServerSoundtrackItemsResponseDto
        {
            Category = normalizedCategory,
            Total = resultItems.Count,
            Items = resultItems
        };
    }

    public async Task<MediaServerTvShowEpisodesResponseDto> GetEpisodesAsync(
        string? serverType,
        string? libraryId,
        string? showId,
        string? seasonId,
        int? limit,
        CancellationToken cancellationToken)
    {
        var normalizedShowId = NormalizeText(showId);
        if (string.IsNullOrWhiteSpace(normalizedShowId))
        {
            return new MediaServerTvShowEpisodesResponseDto();
        }

        var episodeLimit = Math.Clamp(limit.GetValueOrDefault(500), 1, 2000);
        var auth = await _platformAuthService.LoadAsync();
        var settings = await _store.LoadAsync(cancellationToken);
        var targets = ResolveTargetLibraries(
            settings,
            auth,
            MediaServerSoundtrackConstants.TvShowCategory,
            serverType,
            libraryId);
        if (targets.Count == 0)
        {
            return new MediaServerTvShowEpisodesResponseDto();
        }

        var target = targets[0];
        var allShows = await FetchLibraryItemsAsync(auth, target, 0, 2000, cancellationToken);
        var show = allShows.FirstOrDefault(item => string.Equals(item.ItemId, normalizedShowId, StringComparison.OrdinalIgnoreCase));
        if (show is null)
        {
            return new MediaServerTvShowEpisodesResponseDto
            {
                ServerType = NormalizeServerType(target.ServerType),
                ServerLabel = GetServerDisplayName(target.ServerType),
                LibraryId = target.LibraryId,
                LibraryName = target.LibraryName
            };
        }

        var fetched = await FetchTvEpisodesForShowAsync(auth, target, show, cancellationToken);
        var normalizedSeasonId = NormalizeText(seasonId);
        var filteredEpisodes = fetched.Episodes
            .Where(item => string.IsNullOrWhiteSpace(normalizedSeasonId)
                || string.Equals(item.SeasonId, normalizedSeasonId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.SeasonNumber ?? int.MaxValue)
            .ThenBy(item => item.EpisodeNumber ?? int.MaxValue)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .Take(episodeLimit)
            .ToList();

        var soundtrack = ResolveSoundtrackFromCacheOrQueue(show);
        var seasons = fetched.Seasons
            .Select(season => new MediaServerTvShowSeasonDto
            {
                SeasonId = season.SeasonId,
                Title = season.Title,
                SeasonNumber = season.SeasonNumber,
                ImageUrl = season.ImageUrl,
                EpisodeCount = fetched.Episodes.Count(episode =>
                    string.Equals(episode.SeasonId, season.SeasonId, StringComparison.OrdinalIgnoreCase))
            })
            .OrderBy(season => season.SeasonNumber ?? int.MaxValue)
            .ThenBy(season => season.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new MediaServerTvShowEpisodesResponseDto
        {
            ServerType = NormalizeServerType(target.ServerType),
            ServerLabel = GetServerDisplayName(target.ServerType),
            LibraryId = target.LibraryId,
            LibraryName = target.LibraryName,
            ShowId = show.ItemId,
            ShowTitle = show.Title,
            ShowImageUrl = show.ImageUrl,
            SelectedSeasonId = string.IsNullOrWhiteSpace(normalizedSeasonId) ? null : normalizedSeasonId,
            TotalEpisodes = filteredEpisodes.Count,
            Seasons = seasons,
            Episodes = filteredEpisodes.Select(episode => new MediaServerTvShowEpisodeDto
            {
                EpisodeId = episode.EpisodeId,
                SeasonId = episode.SeasonId,
                SeasonTitle = episode.SeasonTitle,
                SeasonNumber = episode.SeasonNumber,
                EpisodeNumber = episode.EpisodeNumber,
                Title = episode.Title,
                Year = episode.Year,
                ImageUrl = episode.ImageUrl,
                Soundtrack = soundtrack
            }).ToList()
        };
    }

    private async Task<List<MediaServerLibraryDescriptor>> DiscoverLibrariesAsync(PlatformAuthState auth, CancellationToken cancellationToken)
    {
        var discovered = new List<MediaServerLibraryDescriptor>();
        discovered.AddRange(await DiscoverPlexLibrariesAsync(auth.Plex, cancellationToken));
        discovered.AddRange(await DiscoverJellyfinLibrariesAsync(auth.Jellyfin, cancellationToken));
        return discovered;
    }

    private async Task<List<MediaServerLibraryDescriptor>> DiscoverPlexLibrariesAsync(PlexAuth? plex, CancellationToken cancellationToken)
    {
        if (!HasCredentials(plex?.Url, plex?.Token))
        {
            return new List<MediaServerLibraryDescriptor>();
        }

        try
        {
            var sections = await _plexApiClient.GetLibrarySectionsAsync(plex!.Url!, plex.Token!, cancellationToken);
            return sections
                .Select(section => new MediaServerLibraryDescriptor
                {
                    ServerType = MediaServerSoundtrackConstants.PlexServer,
                    LibraryId = NormalizeText(section.Key),
                    Name = NormalizeText(section.Title),
                    Category = MapPlexCategory(section.Type),
                    Connected = true
                })
                .Where(section => !string.IsNullOrWhiteSpace(section.LibraryId))
                .Where(section => section.Category == MediaServerSoundtrackConstants.MovieCategory || section.Category == MediaServerSoundtrackConstants.TvShowCategory)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to discover Plex soundtrack libraries.");
            return new List<MediaServerLibraryDescriptor>();
        }
    }

    private async Task<List<MediaServerLibraryDescriptor>> DiscoverJellyfinLibrariesAsync(JellyfinAuth? jellyfin, CancellationToken cancellationToken)
    {
        if (!HasCredentials(jellyfin?.Url, jellyfin?.ApiKey))
        {
            return new List<MediaServerLibraryDescriptor>();
        }

        try
        {
            var libraries = await _jellyfinApiClient.GetLibrariesAsync(jellyfin!.Url!, jellyfin.ApiKey!, cancellationToken);
            return libraries
                .Select(library => new MediaServerLibraryDescriptor
                {
                    ServerType = MediaServerSoundtrackConstants.JellyfinServer,
                    LibraryId = NormalizeText(library.Id),
                    Name = NormalizeText(library.Name),
                    Category = MapJellyfinCategory(library.CollectionType),
                    Connected = true
                })
                .Where(library => !string.IsNullOrWhiteSpace(library.LibraryId))
                .Where(library => library.Category == MediaServerSoundtrackConstants.MovieCategory || library.Category == MediaServerSoundtrackConstants.TvShowCategory)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to discover Jellyfin soundtrack libraries.");
            return new List<MediaServerLibraryDescriptor>();
        }
    }

    private static bool MergeDiscoveredLibraries(
        MediaServerSoundtrackSettings settings,
        IReadOnlyList<MediaServerLibraryDescriptor> discovered,
        DateTimeOffset nowUtc)
    {
        var changed = false;

        foreach (var group in discovered.GroupBy(item => NormalizeServerType(item.ServerType), StringComparer.OrdinalIgnoreCase))
        {
            var serverType = NormalizeServerType(group.Key);
            if (string.IsNullOrWhiteSpace(serverType))
            {
                continue;
            }

            var server = GetOrCreateServer(settings, serverType);
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var library in group)
            {
                var libraryId = NormalizeText(library.LibraryId);
                if (string.IsNullOrWhiteSpace(libraryId))
                {
                    continue;
                }

                seenIds.Add(libraryId);
                if (!server.Libraries.TryGetValue(libraryId, out var existing))
                {
                    existing = new MediaServerSoundtrackLibrarySettings
                    {
                        LibraryId = libraryId,
                        Name = NormalizeText(library.Name),
                        Category = NormalizeCategory(library.Category),
                        Enabled = server.AutoIncludeNewLibraries,
                        Ignored = false,
                        FirstDiscoveredUtc = nowUtc,
                        LastSeenUtc = nowUtc,
                        UserConfigured = false
                    };
                    server.Libraries[libraryId] = existing;
                    changed = true;
                    continue;
                }

                var normalizedName = NormalizeText(library.Name);
                var normalizedCategory = NormalizeCategory(library.Category);

                if (!string.Equals(existing.Name, normalizedName, StringComparison.Ordinal))
                {
                    existing.Name = normalizedName;
                    changed = true;
                }

                if (!string.Equals(existing.Category, normalizedCategory, StringComparison.Ordinal))
                {
                    existing.Category = normalizedCategory;
                    changed = true;
                }

                if (existing.LastSeenUtc != nowUtc)
                {
                    existing.LastSeenUtc = nowUtc;
                    changed = true;
                }
            }

            foreach (var staleLibrary in server.Libraries.Values.Where(entry => !seenIds.Contains(entry.LibraryId)))
            {
                if (staleLibrary.LastSeenUtc != null)
                {
                    staleLibrary.LastSeenUtc = null;
                    changed = true;
                }
            }
        }

        return changed;
    }

    private MediaServerSoundtrackConfigurationDto BuildConfigurationDto(
        MediaServerSoundtrackSettings settings,
        PlatformAuthState auth,
        IReadOnlyList<MediaServerLibraryDescriptor> discovered)
    {
        var discoveredLookup = discovered
            .GroupBy(item => NormalizeServerType(item.ServerType), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.ToDictionary(item => NormalizeText(item.LibraryId), item => item, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

        var serverTypes = new[]
        {
            MediaServerSoundtrackConstants.PlexServer,
            MediaServerSoundtrackConstants.JellyfinServer
        };

        var servers = new List<MediaServerSoundtrackServerDto>();
        foreach (var serverType in serverTypes)
        {
            var serverSettings = settings.Servers.TryGetValue(serverType, out var found)
                ? found
                : new MediaServerSoundtrackServerSettings();
            var connected = IsServerConnected(serverType, auth);
            var discoveredById = discoveredLookup.TryGetValue(serverType, out var lookup)
                ? lookup
                : new Dictionary<string, MediaServerLibraryDescriptor>(StringComparer.OrdinalIgnoreCase);

            var libraryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in serverSettings.Libraries.Keys)
            {
                libraryIds.Add(NormalizeText(id));
            }
            foreach (var id in discoveredById.Keys)
            {
                libraryIds.Add(NormalizeText(id));
            }

            var libraries = new List<MediaServerSoundtrackLibraryDto>();
            foreach (var id in libraryIds.Where(id => !string.IsNullOrWhiteSpace(id)).OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
            {
                serverSettings.Libraries.TryGetValue(id, out var saved);
                discoveredById.TryGetValue(id, out var live);

                if (live == null && saved?.LastSeenUtc == null)
                {
                    continue;
                }

                var category = NormalizeCategory(saved?.Category ?? live?.Category);
                var name = NormalizeText(saved?.Name);
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = NormalizeText(live?.Name);
                }

                libraries.Add(new MediaServerSoundtrackLibraryDto
                {
                    LibraryId = id,
                    Name = name,
                    Category = category,
                    CategoryLabel = GetCategoryLabel(category),
                    Enabled = saved?.Enabled ?? serverSettings.AutoIncludeNewLibraries,
                    Ignored = saved?.Ignored ?? false,
                    Connected = live != null,
                    FirstDiscoveredUtc = saved?.FirstDiscoveredUtc,
                    LastSeenUtc = saved?.LastSeenUtc
                });
            }

            servers.Add(new MediaServerSoundtrackServerDto
            {
                ServerType = serverType,
                DisplayName = GetServerDisplayName(serverType),
                Connected = connected,
                AutoIncludeNewLibraries = serverSettings.AutoIncludeNewLibraries,
                Libraries = libraries
            });
        }

        return new MediaServerSoundtrackConfigurationDto
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Servers = servers
        };
    }

    private List<(string ServerType, string LibraryId, string LibraryName)> ResolveTargetLibraries(
        MediaServerSoundtrackSettings settings,
        PlatformAuthState auth,
        string category,
        string? serverType,
        string? libraryId)
    {
        var normalizedServer = NormalizeServerType(serverType);
        var normalizedLibraryId = NormalizeText(libraryId);

        var targets = new List<(string ServerType, string LibraryId, string LibraryName)>();
        foreach (var (storedServerType, serverSettings) in settings.Servers)
        {
            if (!string.IsNullOrWhiteSpace(normalizedServer)
                && !string.Equals(storedServerType, normalizedServer, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!IsServerConnected(storedServerType, auth))
            {
                continue;
            }

            foreach (var (storedLibraryId, storedLibrary) in serverSettings.Libraries)
            {
                if (!string.IsNullOrWhiteSpace(normalizedLibraryId)
                    && !string.Equals(storedLibraryId, normalizedLibraryId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.Equals(NormalizeCategory(storedLibrary.Category), category, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!storedLibrary.Enabled || storedLibrary.Ignored)
                {
                    continue;
                }

                targets.Add((storedServerType, storedLibraryId, storedLibrary.Name));
            }
        }

        return targets;
    }

    private async Task<List<MediaServerContentItem>> FetchLibraryItemsAsync(
        PlatformAuthState auth,
        (string ServerType, string LibraryId, string LibraryName) target,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        if (string.Equals(target.ServerType, MediaServerSoundtrackConstants.PlexServer, StringComparison.OrdinalIgnoreCase))
        {
            return await FetchPlexItemsAsync(auth.Plex, target.LibraryId, target.LibraryName, offset, limit, cancellationToken);
        }

        if (string.Equals(target.ServerType, MediaServerSoundtrackConstants.JellyfinServer, StringComparison.OrdinalIgnoreCase))
        {
            return await FetchJellyfinItemsAsync(auth.Jellyfin, target.LibraryId, target.LibraryName, offset, limit, cancellationToken);
        }

        return new List<MediaServerContentItem>();
    }

    private async Task<TvEpisodeFetchResult> FetchTvEpisodesForShowAsync(
        PlatformAuthState auth,
        (string ServerType, string LibraryId, string LibraryName) target,
        MediaServerContentItem show,
        CancellationToken cancellationToken)
    {
        if (string.Equals(target.ServerType, MediaServerSoundtrackConstants.PlexServer, StringComparison.OrdinalIgnoreCase))
        {
            return await FetchPlexEpisodesAsync(auth.Plex, show, cancellationToken);
        }

        if (string.Equals(target.ServerType, MediaServerSoundtrackConstants.JellyfinServer, StringComparison.OrdinalIgnoreCase))
        {
            return await FetchJellyfinEpisodesAsync(auth.Jellyfin, show, cancellationToken);
        }

        return new TvEpisodeFetchResult
        {
            ShowId = show.ItemId,
            ShowTitle = show.Title,
            ShowImageUrl = show.ImageUrl
        };
    }

    private async Task<List<MediaServerContentItem>> FetchPlexItemsAsync(
        PlexAuth? plex,
        string libraryId,
        string libraryName,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        if (!HasCredentials(plex?.Url, plex?.Token))
        {
            return new List<MediaServerContentItem>();
        }

        var items = await _plexApiClient.GetLibraryMediaItemsAsync(plex!.Url!, plex.Token!, libraryId, offset, limit, cancellationToken);
        return items
            .Select(item => new MediaServerContentItem
            {
                ServerType = MediaServerSoundtrackConstants.PlexServer,
                LibraryId = libraryId,
                LibraryName = libraryName,
                Category = MapPlexCategory(item.Type),
                ItemId = item.Id,
                Title = NormalizeText(item.Title),
                Year = item.Year,
                ImageUrl = item.ImageUrl
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Category))
            .Where(item => !string.IsNullOrWhiteSpace(item.ItemId) && !string.IsNullOrWhiteSpace(item.Title))
            .ToList();
    }

    private async Task<List<MediaServerContentItem>> FetchJellyfinItemsAsync(
        JellyfinAuth? jellyfin,
        string libraryId,
        string libraryName,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        if (!HasCredentials(jellyfin?.Url, jellyfin?.ApiKey))
        {
            return new List<MediaServerContentItem>();
        }

        var userId = NormalizeText(jellyfin!.UserId);
        if (string.IsNullOrWhiteSpace(userId))
        {
            var currentUser = await _jellyfinApiClient.GetCurrentUserAsync(jellyfin.Url!, jellyfin.ApiKey!, cancellationToken);
            userId = NormalizeText(currentUser?.Id);
        }

        if (string.IsNullOrWhiteSpace(userId))
        {
            return new List<MediaServerContentItem>();
        }

        var items = await _jellyfinApiClient.GetLibraryItemsAsync(jellyfin.Url!, jellyfin.ApiKey!, userId, libraryId, offset, limit, cancellationToken);
        return items
            .Select(item => new MediaServerContentItem
            {
                ServerType = MediaServerSoundtrackConstants.JellyfinServer,
                LibraryId = libraryId,
                LibraryName = libraryName,
                Category = MapJellyfinItemCategory(item.Type),
                ItemId = NormalizeText(item.Id),
                Title = NormalizeText(item.Name),
                Year = item.ProductionYear,
                ImageUrl = BuildJellyfinImageUrl(jellyfin.Url!, jellyfin.ApiKey!, item.Id, item.ImageTags, episodePreferred: false)
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Category))
            .Where(item => !string.IsNullOrWhiteSpace(item.ItemId) && !string.IsNullOrWhiteSpace(item.Title))
            .ToList();
    }

    private async Task<TvEpisodeFetchResult> FetchPlexEpisodesAsync(
        PlexAuth? plex,
        MediaServerContentItem show,
        CancellationToken cancellationToken)
    {
        if (!HasCredentials(plex?.Url, plex?.Token))
        {
            return new TvEpisodeFetchResult
            {
                ShowId = show.ItemId,
                ShowTitle = show.Title,
                ShowImageUrl = show.ImageUrl
            };
        }

        var result = new TvEpisodeFetchResult
        {
            ShowId = show.ItemId,
            ShowTitle = show.Title,
            ShowImageUrl = show.ImageUrl
        };

        var seasons = await _plexApiClient.GetShowSeasonsAsync(plex!.Url!, plex.Token!, show.ItemId, cancellationToken);
        foreach (var season in seasons)
        {
            if (string.IsNullOrWhiteSpace(season.Id))
            {
                continue;
            }

            var seasonTitle = NormalizeText(season.Title);
            if (string.IsNullOrWhiteSpace(seasonTitle))
            {
                seasonTitle = season.SeasonNumber.HasValue ? $"Season {season.SeasonNumber.Value}" : "Season";
            }

            var seasonId = NormalizeText(season.Id);
            result.Seasons.Add(new MediaServerTvShowSeasonItem
            {
                SeasonId = seasonId,
                Title = seasonTitle,
                SeasonNumber = season.SeasonNumber,
                ImageUrl = season.ImageUrl
            });

            var episodes = await _plexApiClient.GetSeasonEpisodesAsync(plex.Url!, plex.Token!, seasonId, cancellationToken);
            result.Episodes.AddRange(episodes
                .Where(episode => !string.IsNullOrWhiteSpace(episode.Id))
                .Select(episode => new MediaServerTvShowEpisodeItem
                {
                    EpisodeId = NormalizeText(episode.Id),
                    SeasonId = seasonId,
                    SeasonTitle = seasonTitle,
                    SeasonNumber = season.SeasonNumber ?? episode.SeasonNumber,
                    EpisodeNumber = episode.EpisodeNumber,
                    Title = NormalizeText(episode.Title),
                    Year = episode.Year,
                    ImageUrl = episode.ImageUrl
                })
                .Where(episode => !string.IsNullOrWhiteSpace(episode.Title)));
        }

        return result;
    }

    private async Task<TvEpisodeFetchResult> FetchJellyfinEpisodesAsync(
        JellyfinAuth? jellyfin,
        MediaServerContentItem show,
        CancellationToken cancellationToken)
    {
        if (!HasCredentials(jellyfin?.Url, jellyfin?.ApiKey))
        {
            return new TvEpisodeFetchResult
            {
                ShowId = show.ItemId,
                ShowTitle = show.Title,
                ShowImageUrl = show.ImageUrl
            };
        }

        var userId = NormalizeText(jellyfin!.UserId);
        if (string.IsNullOrWhiteSpace(userId))
        {
            var currentUser = await _jellyfinApiClient.GetCurrentUserAsync(jellyfin.Url!, jellyfin.ApiKey!, cancellationToken);
            userId = NormalizeText(currentUser?.Id);
        }

        var result = new TvEpisodeFetchResult
        {
            ShowId = show.ItemId,
            ShowTitle = show.Title,
            ShowImageUrl = show.ImageUrl
        };

        if (string.IsNullOrWhiteSpace(userId))
        {
            return result;
        }

        var seasons = await _jellyfinApiClient.GetShowSeasonsAsync(jellyfin.Url!, jellyfin.ApiKey!, userId, show.ItemId, cancellationToken);
        foreach (var season in seasons)
        {
            var seasonId = NormalizeText(season.Id);
            if (string.IsNullOrWhiteSpace(seasonId))
            {
                continue;
            }

            var seasonNumber = season.IndexNumber ?? season.ParentIndexNumber;
            var seasonTitle = NormalizeText(season.Name);
            if (string.IsNullOrWhiteSpace(seasonTitle))
            {
                seasonTitle = seasonNumber.HasValue ? $"Season {seasonNumber.Value}" : "Season";
            }

            result.Seasons.Add(new MediaServerTvShowSeasonItem
            {
                SeasonId = seasonId,
                Title = seasonTitle,
                SeasonNumber = seasonNumber,
                ImageUrl = BuildJellyfinImageUrl(jellyfin.Url!, jellyfin.ApiKey!, season.Id, season.ImageTags, episodePreferred: false)
            });

            var episodes = await _jellyfinApiClient.GetSeasonEpisodesAsync(jellyfin.Url!, jellyfin.ApiKey!, userId, seasonId, cancellationToken);
            result.Episodes.AddRange(episodes
                .Where(episode => !string.IsNullOrWhiteSpace(episode.Id))
                .Select(episode => new MediaServerTvShowEpisodeItem
                {
                    EpisodeId = NormalizeText(episode.Id),
                    SeasonId = seasonId,
                    SeasonTitle = seasonTitle,
                    SeasonNumber = episode.ParentIndexNumber ?? seasonNumber,
                    EpisodeNumber = episode.IndexNumber,
                    Title = NormalizeText(episode.Name),
                    Year = episode.ProductionYear,
                    ImageUrl = BuildJellyfinImageUrl(
                        jellyfin.Url!,
                        jellyfin.ApiKey!,
                        episode.Id,
                        episode.ImageTags,
                        episodePreferred: true)
                })
                .Where(episode => !string.IsNullOrWhiteSpace(episode.Title)));
        }

        return result;
    }

    private MediaServerSoundtrackMatchDto ResolveSoundtrackFromCacheOrQueue(MediaServerContentItem item)
    {
        var cacheKey = BuildSoundtrackCacheKey(item);
        if (TryGetFreshSoundtrack(cacheKey, out var match))
        {
            return match;
        }

        QueueSoundtrackWarmup(item, cacheKey);
        return CreateFallbackSearchMatch(BuildSoundtrackQuery(item.Title));
    }

    private bool TryGetFreshSoundtrack(string cacheKey, out MediaServerSoundtrackMatchDto match)
    {
        if (_soundtrackCache.TryGetValue(cacheKey, out var cached)
            && DateTimeOffset.UtcNow - cached.CachedAtUtc <= SoundtrackCacheTtl)
        {
            match = cached.Match;
            return true;
        }

        match = default!;
        return false;
    }

    private void QueueSoundtrackWarmup(MediaServerContentItem item, string cacheKey)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return;
        }

        if (!_soundtrackWarmupInFlight.TryAdd(cacheKey, 0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await WarmSoundtrackAsync(item, cacheKey);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Soundtrack warmup failed for {Title}", item.Title);
            }
            finally
            {
                _soundtrackWarmupInFlight.TryRemove(cacheKey, out _);
            }
        });
    }

    private async Task WarmSoundtrackAsync(MediaServerContentItem item, string cacheKey)
    {
        await _soundtrackWarmupGate.WaitAsync();
        try
        {
            if (TryGetFreshSoundtrack(cacheKey, out _))
            {
                return;
            }

            var resolved = await ResolveSoundtrackDirectAsync(item, CancellationToken.None);
            _soundtrackCache[cacheKey] = (DateTimeOffset.UtcNow, resolved);
        }
        finally
        {
            _soundtrackWarmupGate.Release();
        }
    }

    private async Task<MediaServerSoundtrackMatchDto> ResolveSoundtrackDirectAsync(MediaServerContentItem item, CancellationToken cancellationToken)
    {
        var query = BuildSoundtrackQuery(item.Title);
        var defaultMatch = CreateFallbackSearchMatch(query);

        try
        {
            var albumResult = await _deezerClient.SearchAlbumAsync(query, new ApiOptions { Limit = 3 });
            var playlistResult = await _deezerClient.SearchPlaylistAsync(query, new ApiOptions { Limit = 3 });

            var albumMatch = SelectBestAlbumMatch(albumResult, item.Title, query);
            var playlistMatch = SelectBestPlaylistMatch(playlistResult, item.Title, query);
            return ChooseBestMatch(defaultMatch, albumMatch, playlistMatch);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed resolving soundtrack for {Title}", item.Title);
            return defaultMatch;
        }
    }

    private static MediaServerSoundtrackMatchDto ChooseBestMatch(
        MediaServerSoundtrackMatchDto fallback,
        MediaServerSoundtrackMatchDto? album,
        MediaServerSoundtrackMatchDto? playlist)
    {
        var best = fallback;
        if (album != null && album.Score > best.Score)
        {
            best = album;
        }

        if (playlist != null && playlist.Score > best.Score)
        {
            best = playlist;
        }

        return best;
    }

    private static MediaServerSoundtrackMatchDto? SelectBestAlbumMatch(DeezerSearchResult? result, string title, string query)
    {
        if (result?.Data == null || result.Data.Length == 0)
        {
            return null;
        }

        MediaServerSoundtrackMatchDto? best = null;
        foreach (var item in result.Data)
        {
            if (item is not JsonElement json)
            {
                continue;
            }

            var id = GetString(json, "id");
            var albumTitle = GetString(json, "title");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(albumTitle))
            {
                continue;
            }

            var artistName = GetNestedString(json, "artist", "name");
            var score = ComputeMatchScore(title, albumTitle);
            var model = new MediaServerSoundtrackMatchDto
            {
                Kind = "album",
                DeezerId = id,
                Title = albumTitle,
                Subtitle = artistName,
                Url = $"https://www.deezer.com/album/{Uri.EscapeDataString(id)}",
                CoverUrl = GetString(json, "cover_medium") ?? GetString(json, "cover") ?? BuildDeezerSearchUrl(query),
                Score = score
            };

            if (best == null || model.Score > best.Score)
            {
                best = model;
            }
        }

        return best;
    }

    private static MediaServerSoundtrackMatchDto? SelectBestPlaylistMatch(DeezerSearchResult? result, string title, string query)
    {
        if (result?.Data == null || result.Data.Length == 0)
        {
            return null;
        }

        MediaServerSoundtrackMatchDto? best = null;
        foreach (var item in result.Data)
        {
            if (item is not JsonElement json)
            {
                continue;
            }

            var id = GetString(json, "id");
            var playlistTitle = GetString(json, "title");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(playlistTitle))
            {
                continue;
            }

            var creator = GetNestedString(json, "user", "name");
            var score = ComputeMatchScore(title, playlistTitle) - 2;
            var model = new MediaServerSoundtrackMatchDto
            {
                Kind = "playlist",
                DeezerId = id,
                Title = playlistTitle,
                Subtitle = creator,
                Url = $"https://www.deezer.com/playlist/{Uri.EscapeDataString(id)}",
                CoverUrl = GetString(json, "picture_medium") ?? GetString(json, "picture") ?? BuildDeezerSearchUrl(query),
                Score = score
            };

            if (best == null || model.Score > best.Score)
            {
                best = model;
            }
        }

        return best;
    }

    private static int ComputeMatchScore(string targetTitle, string candidateTitle)
    {
        var normalizedTarget = NormalizeMatchingText(targetTitle);
        var normalizedCandidate = NormalizeMatchingText(candidateTitle);

        var score = 20;
        if (normalizedCandidate.Contains(normalizedTarget, StringComparison.OrdinalIgnoreCase)
            || normalizedTarget.Contains(normalizedCandidate, StringComparison.OrdinalIgnoreCase))
        {
            score += 35;
        }

        if (normalizedCandidate.Contains("soundtrack", StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.Contains("ost", StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.Contains("original motion picture", StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.Contains("original television", StringComparison.OrdinalIgnoreCase))
        {
            score += 20;
        }

        return score;
    }

    private static string NormalizeMatchingText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Concat(value
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch) ? ch : ' '))
            .Replace("  ", " ", StringComparison.Ordinal)
            .Trim();
    }

    private static MediaServerSoundtrackMatchDto CreateFallbackSearchMatch(string query)
        => new()
        {
            Kind = "search",
            DeezerId = null,
            Title = "Search on Deezer",
            Subtitle = null,
            Url = BuildDeezerSearchUrl(query),
            CoverUrl = null,
            Score = 1
        };

    private static string BuildDeezerSearchUrl(string query)
        => $"https://www.deezer.com/search/{Uri.EscapeDataString(query)}";

    private static string BuildSoundtrackQuery(string title)
        => string.IsNullOrWhiteSpace(title) ? "soundtrack" : $"{title} soundtrack";

    private static string BuildSoundtrackCacheKey(MediaServerContentItem item)
        => $"{NormalizeServerType(item.ServerType)}::{NormalizeText(item.LibraryId)}::{NormalizeText(item.ItemId)}";

    private static string BuildJellyfinImageUrl(
        string serverUrl,
        string apiKey,
        string? itemId,
        IReadOnlyDictionary<string, string>? imageTags,
        bool episodePreferred)
    {
        var normalizedId = NormalizeText(itemId);
        if (string.IsNullOrWhiteSpace(normalizedId))
        {
            return string.Empty;
        }

        var imageType = ResolveJellyfinImageType(imageTags, episodePreferred, out var imageTag);
        var normalizedServerUrl = serverUrl.TrimEnd('/');
        var token = Uri.EscapeDataString(apiKey);
        var suffix = string.IsNullOrWhiteSpace(imageTag)
            ? string.Empty
            : $"&tag={Uri.EscapeDataString(imageTag)}";
        return $"{normalizedServerUrl}/Items/{Uri.EscapeDataString(normalizedId)}/Images/{imageType}?maxHeight=420&quality=90&api_key={token}{suffix}";
    }

    private static string ResolveJellyfinImageType(
        IReadOnlyDictionary<string, string>? imageTags,
        bool episodePreferred,
        out string? selectedTag)
    {
        selectedTag = null;
        if (imageTags == null || imageTags.Count == 0)
        {
            return "Primary";
        }

        if (episodePreferred
            && imageTags.TryGetValue("Thumb", out var thumbTag)
            && !string.IsNullOrWhiteSpace(thumbTag))
        {
            selectedTag = thumbTag;
            return "Thumb";
        }

        if (imageTags.TryGetValue("Primary", out var primaryTag)
            && !string.IsNullOrWhiteSpace(primaryTag))
        {
            selectedTag = primaryTag;
            return "Primary";
        }

        if (imageTags.TryGetValue("Thumb", out thumbTag)
            && !string.IsNullOrWhiteSpace(thumbTag))
        {
            selectedTag = thumbTag;
            return "Thumb";
        }

        selectedTag = null;
        return "Primary";
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null
        };
    }

    private static string? GetNestedString(JsonElement element, string objectName, string propertyName)
    {
        if (!element.TryGetProperty(objectName, out var nested) || nested.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return GetString(nested, propertyName);
    }

    private static bool HasCredentials(string? url, string? token)
        => !string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(token);

    private static bool IsServerConnected(string serverType, PlatformAuthState auth)
    {
        var normalized = NormalizeServerType(serverType);
        if (normalized == MediaServerSoundtrackConstants.PlexServer)
        {
            return HasCredentials(auth.Plex?.Url, auth.Plex?.Token);
        }

        if (normalized == MediaServerSoundtrackConstants.JellyfinServer)
        {
            return HasCredentials(auth.Jellyfin?.Url, auth.Jellyfin?.ApiKey);
        }

        return false;
    }

    private static MediaServerSoundtrackServerSettings GetOrCreateServer(MediaServerSoundtrackSettings settings, string serverType)
    {
        if (!settings.Servers.TryGetValue(serverType, out var server))
        {
            server = new MediaServerSoundtrackServerSettings();
            settings.Servers[serverType] = server;
        }

        server.Libraries ??= new Dictionary<string, MediaServerSoundtrackLibrarySettings>(StringComparer.OrdinalIgnoreCase);
        return server;
    }

    private static string NormalizeServerType(string? serverType)
    {
        var normalized = NormalizeText(serverType).ToLowerInvariant();
        return normalized switch
        {
            MediaServerSoundtrackConstants.PlexServer => MediaServerSoundtrackConstants.PlexServer,
            MediaServerSoundtrackConstants.JellyfinServer => MediaServerSoundtrackConstants.JellyfinServer,
            _ => string.Empty
        };
    }

    private static string NormalizeCategory(string? category)
    {
        var normalized = NormalizeText(category).ToLowerInvariant();
        return normalized switch
        {
            "tvshow" => MediaServerSoundtrackConstants.TvShowCategory,
            "tv_show" => MediaServerSoundtrackConstants.TvShowCategory,
            "show" => MediaServerSoundtrackConstants.TvShowCategory,
            "series" => MediaServerSoundtrackConstants.TvShowCategory,
            _ => MediaServerSoundtrackConstants.MovieCategory
        };
    }

    private static string MapPlexCategory(string? plexType)
    {
        var normalized = NormalizeText(plexType).ToLowerInvariant();
        return normalized switch
        {
            "movie" => MediaServerSoundtrackConstants.MovieCategory,
            "show" => MediaServerSoundtrackConstants.TvShowCategory,
            "series" => MediaServerSoundtrackConstants.TvShowCategory,
            "tvshow" => MediaServerSoundtrackConstants.TvShowCategory,
            "tvshows" => MediaServerSoundtrackConstants.TvShowCategory,
            _ => string.Empty
        };
    }

    private static string MapJellyfinCategory(string? jellyfinCollectionType)
    {
        var normalized = NormalizeText(jellyfinCollectionType).ToLowerInvariant();
        return normalized switch
        {
            "movie" => MediaServerSoundtrackConstants.MovieCategory,
            "movies" => MediaServerSoundtrackConstants.MovieCategory,
            "tvshows" => MediaServerSoundtrackConstants.TvShowCategory,
            "tvshow" => MediaServerSoundtrackConstants.TvShowCategory,
            "series" => MediaServerSoundtrackConstants.TvShowCategory,
            _ => string.Empty
        };
    }

    private static string MapJellyfinItemCategory(string? jellyfinType)
    {
        var normalized = NormalizeText(jellyfinType).ToLowerInvariant();
        return normalized switch
        {
            "series" => MediaServerSoundtrackConstants.TvShowCategory,
            "movie" => MediaServerSoundtrackConstants.MovieCategory,
            _ => string.Empty
        };
    }

    private static string NormalizeText(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string GetCategoryLabel(string category)
        => string.Equals(category, MediaServerSoundtrackConstants.TvShowCategory, StringComparison.Ordinal)
            ? "TV Shows"
            : "Movies";

    private static string GetServerDisplayName(string serverType)
        => string.Equals(serverType, MediaServerSoundtrackConstants.JellyfinServer, StringComparison.OrdinalIgnoreCase)
            ? "Jellyfin"
            : "Plex";
}
