using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeezSpoTag.Core.Models.Deezer;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Integrations.Jellyfin;
using DeezSpoTag.Integrations.Plex;
using MusicBrainzArtistCredit = DeezSpoTag.Web.Services.AutoTag.ArtistCredit;
using MusicBrainzClient = DeezSpoTag.Web.Services.AutoTag.MusicBrainzClient;
using MusicBrainzRecordingSearchResults = DeezSpoTag.Web.Services.AutoTag.RecordingSearchResults;
using MusicBrainzRelease = DeezSpoTag.Web.Services.AutoTag.ReleaseSmall;
using Newtonsoft.Json.Linq;

namespace DeezSpoTag.Web.Services;

public sealed class MediaServerSoundtrackService
{
    private const int DefaultItemLimit = 120;
    private static readonly TimeSpan SoundtrackCacheTtl = TimeSpan.FromHours(12);
    private static readonly Regex TitleBracketNoiseRegex = new(@"\[[^\]]*\]|\([^\)]*\)|\{[^\}]*\}", RegexOptions.Compiled);
    private static readonly Regex TitleWhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly string[] SoundtrackNoiseTokens =
    {
        "2160p", "1080p", "720p", "4k", "uhd", "hdr", "dv",
        "x264", "x265", "h264", "h265", "hevc",
        "webrip", "web-dl", "bluray", "brrip", "remux",
        "amzn", "nf", "ddp", "dts", "atmos", "truehd",
        "proper", "repack", "extended", "uncut"
    };
    private static readonly HashSet<string> SoundtrackStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "at", "by", "for", "from", "in", "of", "on", "or", "the", "to", "with",
        "original", "motion", "picture", "television", "series", "season", "movie", "film",
        "soundtrack", "score", "music", "ost"
    };

    private readonly PlatformAuthService _platformAuthService;
    private readonly PlexApiClient _plexApiClient;
    private readonly JellyfinApiClient _jellyfinApiClient;
    private readonly SpotifySearchService _spotifySearchService;
    private readonly SpotifyDeezerAlbumResolver _spotifyDeezerAlbumResolver;
    private readonly MusicBrainzClient _musicBrainzClient;
    private readonly DeezerClient _deezerClient;
    private readonly MediaServerSoundtrackStore _store;
    private readonly MediaServerSoundtrackCacheRepository _cacheRepository;
    private readonly ILogger<MediaServerSoundtrackService> _logger;
    private readonly ConcurrentDictionary<string, (DateTimeOffset CachedAtUtc, MediaServerSoundtrackMatchDto Match)> _soundtrackCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<MediaServerSoundtrackMatchDto>> _soundtrackResolutionInFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _backgroundSoundtrackPersistInFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _soundtrackWarmupGate = new(6, 6);
    private readonly SemaphoreSlim _mediaCacheSyncGate = new(1, 1);

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
        SpotifySearchService spotifySearchService,
        SpotifyDeezerAlbumResolver spotifyDeezerAlbumResolver,
        MusicBrainzClient musicBrainzClient,
        DeezerClient deezerClient,
        MediaServerSoundtrackStore store,
        MediaServerSoundtrackCacheRepository cacheRepository,
        ILogger<MediaServerSoundtrackService> logger)
    {
        _platformAuthService = platformAuthService;
        _plexApiClient = plexApiClient;
        _jellyfinApiClient = jellyfinApiClient;
        _spotifySearchService = spotifySearchService;
        _spotifyDeezerAlbumResolver = spotifyDeezerAlbumResolver;
        _musicBrainzClient = musicBrainzClient;
        _deezerClient = deezerClient;
        _store = store;
        _cacheRepository = cacheRepository;
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
        if (!refreshDiscovery)
        {
            // Read-only config loads must not trigger live media-server discovery.
            return BuildConfigurationDto(
                settings,
                auth,
                new List<MediaServerLibraryDescriptor>());
        }

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
        bool refreshFromServer,
        CancellationToken cancellationToken)
    {
        var normalizedCategory = NormalizeCategory(category);
        var normalizedServerType = NormalizeServerType(serverType);
        var normalizedLibraryId = NormalizeText(libraryId);
        var itemLimit = Math.Clamp(limit.GetValueOrDefault(DefaultItemLimit), 1, 300);
        var itemOffset = Math.Max(offset.GetValueOrDefault(0), 0);
        var persisted = await _cacheRepository.GetItemsAsync(
            normalizedCategory,
            normalizedServerType,
            normalizedLibraryId,
            itemOffset,
            itemLimit,
            cancellationToken);
        if (!refreshFromServer)
        {
            QueueBackgroundSoundtrackResolution(persisted);
            return new MediaServerSoundtrackItemsResponseDto
            {
                Category = normalizedCategory,
                Total = persisted.Count,
                Items = persisted
            };
        }

        var auth = await _platformAuthService.LoadAsync();
        var settings = await _store.LoadAsync(cancellationToken);

        var targets = ResolveTargetLibraries(settings, auth, normalizedCategory, normalizedServerType, normalizedLibraryId);
        if (targets.Count == 0)
        {
            return new MediaServerSoundtrackItemsResponseDto
            {
                Category = normalizedCategory,
                Total = persisted.Count,
                Items = persisted
            };
        }

        try
        {
            var items = new List<MediaServerContentItem>();
            foreach (var target in targets)
            {
                var fetchOffset = itemOffset;
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

            var persistedByCacheKey = new Dictionary<string, MediaServerSoundtrackItemDto>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in items.GroupBy(
                item => $"{NormalizeServerType(item.ServerType)}::{NormalizeText(item.LibraryId)}",
                StringComparer.OrdinalIgnoreCase))
            {
                var sample = group.FirstOrDefault();
                if (sample == null)
                {
                    continue;
                }

                var libraryScopedRows = await _cacheRepository.GetItemsByIdsAsync(
                    sample.ServerType,
                    sample.LibraryId,
                    group.Select(item => item.ItemId).ToArray(),
                    cancellationToken);

                foreach (var row in libraryScopedRows.Values)
                {
                    var cacheKey = BuildSoundtrackItemCacheKey(row.ServerType, row.LibraryId, row.ItemId);
                    if (!string.IsNullOrWhiteSpace(cacheKey))
                    {
                        persistedByCacheKey[cacheKey] = row;
                    }
                }
            }

            var resultItems = items
                .Select(item =>
                {
                    var cacheKey = BuildSoundtrackItemCacheKey(item.ServerType, item.LibraryId, item.ItemId);
                    persistedByCacheKey.TryGetValue(cacheKey, out var persistedRow);
                    return BuildSoundtrackItemDto(item, persistedRow);
                })
                .ToList();

            await ResolveAtLeastOneSoundtrackMatchForResponseAsync(resultItems, cancellationToken);
            await _cacheRepository.UpsertItemsAsync(resultItems, cancellationToken);
            QueueBackgroundSoundtrackResolution(resultItems);

            if (resultItems.Count == 0)
            {
                resultItems = persisted;
            }

            return new MediaServerSoundtrackItemsResponseDto
            {
                Category = normalizedCategory,
                Total = resultItems.Count,
                Items = resultItems
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed loading media-server soundtrack items from source; using persisted soundtrack rows.");
            return new MediaServerSoundtrackItemsResponseDto
            {
                Category = normalizedCategory,
                Total = persisted.Count,
                Items = persisted
            };
        }
    }

    public async Task<MediaServerSoundtrackItemDto?> ResolveItemSoundtrackAsync(
        MediaServerSoundtrackResolveRequest request,
        CancellationToken cancellationToken)
    {
        if (request == null)
        {
            return null;
        }

        var serverType = NormalizeServerType(request.ServerType);
        var libraryId = NormalizeText(request.LibraryId);
        var itemId = NormalizeText(request.ItemId);
        var title = NormalizeText(request.Title);
        if (string.IsNullOrWhiteSpace(serverType)
            || string.IsNullOrWhiteSpace(libraryId)
            || string.IsNullOrWhiteSpace(itemId)
            || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var item = new MediaServerContentItem
        {
            ServerType = serverType,
            LibraryId = libraryId,
            LibraryName = NormalizeText(request.LibraryName),
            Category = NormalizeCategory(request.Category),
            ItemId = itemId,
            Title = title,
            Year = request.Year,
            ImageUrl = NormalizeText(request.ImageUrl)
        };

        var match = await ResolveSoundtrackAsync(item, cancellationToken);
        var dto = new MediaServerSoundtrackItemDto
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
        };

        await _cacheRepository.UpsertItemsAsync(new[] { dto }, cancellationToken);
        return dto;
    }

    private MediaServerSoundtrackItemDto BuildSoundtrackItemDto(
        MediaServerContentItem item,
        MediaServerSoundtrackItemDto? persistedRow)
    {
        var soundtrack = persistedRow?.Soundtrack ?? CreateFallbackSearchMatch(BuildSoundtrackQueries(item.Title)[0]);

        return new MediaServerSoundtrackItemDto
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
            Soundtrack = soundtrack
        };
    }

    public async Task SyncPersistentMediaCacheAsync(CancellationToken cancellationToken)
    {
        if (!await _mediaCacheSyncGate.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            var auth = await _platformAuthService.LoadAsync();
            var settings = await _store.LoadAsync(cancellationToken);
            var targets = ResolveTargetLibraries(settings, auth, MediaServerSoundtrackConstants.MovieCategory, null, null)
                .Concat(ResolveTargetLibraries(settings, auth, MediaServerSoundtrackConstants.TvShowCategory, null, null))
                .GroupBy(
                    target => $"{NormalizeServerType(target.ServerType)}::{NormalizeText(target.LibraryId)}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await SyncLibraryMediaToPersistentCacheAsync(auth, target, cancellationToken);
            }
        }
        finally
        {
            _mediaCacheSyncGate.Release();
        }
    }

    public void TriggerPersistentMediaCacheSync()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await SyncPersistentMediaCacheAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Background soundtrack media cache sync failed.");
            }
        });
    }

    private static string BuildSoundtrackItemCacheKey(string? serverType, string? libraryId, string? itemId)
    {
        var normalizedServer = string.IsNullOrWhiteSpace(serverType) ? string.Empty : serverType.Trim().ToLowerInvariant();
        var normalizedLibrary = string.IsNullOrWhiteSpace(libraryId) ? string.Empty : libraryId.Trim();
        var normalizedItemId = string.IsNullOrWhiteSpace(itemId) ? string.Empty : itemId.Trim();
        return $"{normalizedServer}::{normalizedLibrary}::{normalizedItemId}";
    }

    private async Task SyncLibraryMediaToPersistentCacheAsync(
        PlatformAuthState auth,
        (string ServerType, string LibraryId, string LibraryName) target,
        CancellationToken cancellationToken)
    {
        const int batchSize = 150;
        var offset = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fetched = await FetchLibraryItemsAsync(auth, target, offset, batchSize, cancellationToken);
            if (fetched.Count == 0)
            {
                break;
            }

            var existingByItemId = await _cacheRepository.GetItemsByIdsAsync(
                target.ServerType,
                target.LibraryId,
                fetched.Select(item => item.ItemId).ToArray(),
                cancellationToken);

            var rows = fetched
                .Select(item =>
                {
                    existingByItemId.TryGetValue(NormalizeText(item.ItemId), out var persistedRow);
                    return BuildSoundtrackItemDto(item, persistedRow);
                })
                .ToList();

            await _cacheRepository.UpsertItemsAsync(rows, cancellationToken);
            QueueBackgroundSoundtrackResolution(rows);

            offset += fetched.Count;
            if (fetched.Count < batchSize)
            {
                break;
            }
        }
    }

    private void QueueBackgroundSoundtrackResolution(IReadOnlyCollection<MediaServerSoundtrackItemDto> rows)
    {
        if (rows == null || rows.Count == 0)
        {
            return;
        }

        var unresolved = rows
            .Where(ShouldResolveSoundtrackInBackground)
            .ToList();
        if (unresolved.Count == 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            foreach (var row in unresolved)
            {
                var cacheKey = BuildSoundtrackItemCacheKey(row.ServerType, row.LibraryId, row.ItemId);
                if (!_backgroundSoundtrackPersistInFlight.TryAdd(cacheKey, 0))
                {
                    continue;
                }

                try
                {
                    var sourceItem = new MediaServerContentItem
                    {
                        ServerType = row.ServerType,
                        LibraryId = row.LibraryId,
                        LibraryName = row.LibraryName,
                        Category = row.Category,
                        ItemId = row.ItemId,
                        Title = row.Title,
                        Year = row.Year,
                        ImageUrl = row.ImageUrl
                    };

                    var resolvedMatch = await ResolveSoundtrackAsync(sourceItem, CancellationToken.None);
                    if (AreMatchesEquivalent(row.Soundtrack, resolvedMatch))
                    {
                        continue;
                    }

                    var updated = new MediaServerSoundtrackItemDto
                    {
                        ServerType = row.ServerType,
                        ServerLabel = row.ServerLabel,
                        LibraryId = row.LibraryId,
                        LibraryName = row.LibraryName,
                        Category = row.Category,
                        ItemId = row.ItemId,
                        Title = row.Title,
                        Year = row.Year,
                        ImageUrl = row.ImageUrl,
                        Soundtrack = resolvedMatch
                    };

                    await _cacheRepository.UpsertItemsAsync(new[] { updated }, CancellationToken.None);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Background soundtrack persistence failed for {ServerType}/{LibraryId}/{ItemId}", row.ServerType, row.LibraryId, row.ItemId);
                }
                finally
                {
                    _backgroundSoundtrackPersistInFlight.TryRemove(cacheKey, out _);
                }
            }
        });
    }

    private static bool ShouldResolveSoundtrackInBackground(MediaServerSoundtrackItemDto item)
    {
        var kind = NormalizeText(item.Soundtrack?.Kind).ToLowerInvariant();
        var deezerId = NormalizeText(item.Soundtrack?.DeezerId);
        return string.IsNullOrWhiteSpace(deezerId) || string.Equals(kind, "search", StringComparison.Ordinal);
    }

    private async Task ResolveAtLeastOneSoundtrackMatchForResponseAsync(
        IReadOnlyList<MediaServerSoundtrackItemDto> rows,
        CancellationToken cancellationToken)
    {
        if (rows == null || rows.Count == 0 || rows.Any(HasResolvedSoundtrack))
        {
            return;
        }

        foreach (var row in rows.Take(12))
        {
            if (!ShouldResolveSoundtrackInBackground(row))
            {
                continue;
            }

            var sourceItem = new MediaServerContentItem
            {
                ServerType = row.ServerType,
                LibraryId = row.LibraryId,
                LibraryName = row.LibraryName,
                Category = row.Category,
                ItemId = row.ItemId,
                Title = row.Title,
                Year = row.Year,
                ImageUrl = row.ImageUrl
            };

            MediaServerSoundtrackMatchDto resolvedMatch;
            try
            {
                resolvedMatch = await ResolveSoundtrackAsync(sourceItem, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Foreground soundtrack resolve failed for {Title}", row.Title);
                continue;
            }

            if (!HasResolvedSoundtrack(resolvedMatch))
            {
                continue;
            }

            row.Soundtrack = resolvedMatch;
            return;
        }
    }

    private static bool HasResolvedSoundtrack(MediaServerSoundtrackItemDto row)
        => HasResolvedSoundtrack(row.Soundtrack);

    private static bool HasResolvedSoundtrack(MediaServerSoundtrackMatchDto? match)
    {
        if (match == null)
        {
            return false;
        }

        var kind = NormalizeText(match.Kind).ToLowerInvariant();
        var deezerId = NormalizeText(match.DeezerId);
        return !string.IsNullOrWhiteSpace(deezerId) && !string.Equals(kind, "search", StringComparison.Ordinal);
    }

    private static bool AreMatchesEquivalent(MediaServerSoundtrackMatchDto? left, MediaServerSoundtrackMatchDto? right)
    {
        if (left == null && right == null)
        {
            return true;
        }

        if (left == null || right == null)
        {
            return false;
        }

        return string.Equals(NormalizeText(left.Kind), NormalizeText(right.Kind), StringComparison.OrdinalIgnoreCase)
            && string.Equals(NormalizeText(left.DeezerId), NormalizeText(right.DeezerId), StringComparison.OrdinalIgnoreCase)
            && string.Equals(NormalizeText(left.Url), NormalizeText(right.Url), StringComparison.Ordinal)
            && string.Equals(NormalizeText(left.Title), NormalizeText(right.Title), StringComparison.Ordinal)
            && string.Equals(NormalizeText(left.Subtitle), NormalizeText(right.Subtitle), StringComparison.Ordinal)
            && Math.Abs(left.Score - right.Score) < 0.001;
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

        var soundtrack = await ResolveSoundtrackAsync(show, cancellationToken);
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

                // LastSeenUtc is cleared when discovery cannot see the library anymore.
                // Exclude these stale entries so soundtrack loads don't rescan removed/hidden libraries.
                if (storedLibrary.LastSeenUtc == null)
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

    private async Task<MediaServerSoundtrackMatchDto> ResolveSoundtrackAsync(MediaServerContentItem item, CancellationToken cancellationToken)
    {
        var cacheKey = BuildSoundtrackCacheKey(item);
        if (TryGetFreshSoundtrack(cacheKey, out var match))
        {
            return match;
        }

        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return CreateFallbackSearchMatch(BuildSoundtrackQueries(item.Title)[0]);
        }

        var resolutionTask = _soundtrackResolutionInFlight.GetOrAdd(cacheKey, cacheEntryKey =>
        {
            var created = ResolveAndCacheSoundtrackAsync(item, cacheEntryKey);
            _ = created.ContinueWith(
                completedTask => _soundtrackResolutionInFlight.TryRemove(cacheEntryKey, out _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return created;
        });

        return await resolutionTask.WaitAsync(cancellationToken);
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

    private async Task<MediaServerSoundtrackMatchDto> ResolveAndCacheSoundtrackAsync(MediaServerContentItem item, string cacheKey)
    {
        await _soundtrackWarmupGate.WaitAsync();
        try
        {
            if (TryGetFreshSoundtrack(cacheKey, out var cached))
            {
                return cached;
            }

            var resolved = await ResolveSoundtrackDirectAsync(item, CancellationToken.None);
            _soundtrackCache[cacheKey] = (DateTimeOffset.UtcNow, resolved);
            return resolved;
        }
        finally
        {
            _soundtrackWarmupGate.Release();
        }
    }

    private async Task<MediaServerSoundtrackMatchDto> ResolveSoundtrackDirectAsync(MediaServerContentItem item, CancellationToken cancellationToken)
    {
        var queries = BuildSoundtrackQueries(item.Title);
        var primaryQuery = queries[0];
        var defaultMatch = CreateFallbackSearchMatch(primaryQuery);

        try
        {
            var bestMatch = defaultMatch;
            var spotifyMatch = await TryResolveSpotifySoundtrackMatchAsync(item, queries, cancellationToken);
            bestMatch = SelectHigherScore(bestMatch, spotifyMatch);
            if (!string.IsNullOrWhiteSpace(bestMatch.DeezerId) && bestMatch.Score >= 55)
            {
                return bestMatch;
            }

            var musicBrainzCuratedMatch = await TryResolveMusicBrainzCuratedMatchAsync(item, queries, cancellationToken);
            bestMatch = SelectHigherScore(bestMatch, musicBrainzCuratedMatch);
            if (!string.IsNullOrWhiteSpace(bestMatch.DeezerId) && bestMatch.Score >= 55)
            {
                return bestMatch;
            }

            foreach (var query in queries)
            {
                var albumResult = await _deezerClient.SearchAlbumAsync(query, new ApiOptions { Limit = 5 });
                var playlistResult = await _deezerClient.SearchPlaylistAsync(query, new ApiOptions { Limit = 5 });
                var trackResult = await _deezerClient.SearchTrackAsync(query, new ApiOptions { Limit = 8 });

                var albumMatch = SelectBestAlbumMatch(albumResult, item.Title, query);
                var playlistMatch = SelectBestPlaylistMatch(playlistResult, item.Title, query);
                var trackAlbumMatch = SelectBestTrackAlbumMatch(trackResult, item.Title, query);
                bestMatch = ChooseBestMatch(bestMatch, albumMatch, playlistMatch, trackAlbumMatch);

                if (!string.IsNullOrWhiteSpace(bestMatch.DeezerId) && bestMatch.Score >= 55)
                {
                    break;
                }
            }

            return bestMatch;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed resolving soundtrack for {Title}", item.Title);
            return defaultMatch;
        }
    }

    private async Task<MediaServerSoundtrackMatchDto?> TryResolveSpotifySoundtrackMatchAsync(
        MediaServerContentItem item,
        IReadOnlyList<string> queries,
        CancellationToken cancellationToken)
    {
        MediaServerSoundtrackMatchDto? best = null;
        foreach (var query in queries)
        {
            SpotifySearchTypeResponse? spotifyAlbums;
            try
            {
                spotifyAlbums = await _spotifySearchService.SearchByTypeAsync(query, "album", 10, 0, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Spotify soundtrack candidate search failed for {Title}", item.Title);
                continue;
            }

            if (spotifyAlbums?.Items == null || spotifyAlbums.Items.Count == 0)
            {
                continue;
            }

            var albumCandidates = spotifyAlbums.Items
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Id) && !string.IsNullOrWhiteSpace(candidate.Name))
                .Where(candidate => IsSoundtrackCandidateCompatible(item.Title, candidate.Name))
                .OrderByDescending(candidate => ComputeMatchScore(item.Title, candidate.Name))
                .Take(4)
                .ToList();
            if (albumCandidates.Count == 0)
            {
                continue;
            }

            foreach (var candidate in albumCandidates)
            {
                var spotifyDirectMatch = BuildSpotifyDirectSoundtrackMatch(item, candidate);
                best = SelectHigherScoreNullable(best, spotifyDirectMatch);

                var deezerAlbumId = await TryResolveDeezerAlbumIdFromSpotifyCandidateAsync(candidate, cancellationToken);
                if (string.IsNullOrWhiteSpace(deezerAlbumId))
                {
                    var spotifyFallbackCandidate = await TryResolveCuratedCandidateViaDeezerAsync(
                        item,
                        candidate.Name,
                        ExtractSpotifyItemArtist(candidate.Subtitle),
                        scoreBonus: 12,
                        cancellationToken);
                    best = SelectHigherScoreNullable(best, spotifyFallbackCandidate);
                    continue;
                }

                var deezerAlbum = await TryGetDeezerAlbumAsync(deezerAlbumId);
                var deezerTitle = NormalizeText(deezerAlbum?.Title);
                if (string.IsNullOrWhiteSpace(deezerTitle))
                {
                    deezerTitle = candidate.Name;
                }

                var subtitle = NormalizeText(deezerAlbum?.Artist?.Name);
                if (string.IsNullOrWhiteSpace(subtitle))
                {
                    subtitle = ExtractSpotifyItemArtist(candidate.Subtitle);
                }

                var score = Math.Max(
                    ComputeMatchScore(item.Title, deezerTitle) + 12,
                    ComputeMatchScore(item.Title, candidate.Name) + 8);
                var match = new MediaServerSoundtrackMatchDto
                {
                    Kind = "album",
                    DeezerId = deezerAlbumId,
                    Title = deezerTitle,
                    Subtitle = string.IsNullOrWhiteSpace(subtitle) ? null : subtitle,
                    Url = $"https://www.deezer.com/album/{Uri.EscapeDataString(deezerAlbumId)}",
                    CoverUrl = FirstNonEmpty(
                        deezerAlbum?.CoverMedium,
                        deezerAlbum?.CoverBig,
                        deezerAlbum?.Cover,
                        candidate.ImageUrl),
                    Score = score
                };

                best = SelectHigherScoreNullable(best, match);
                if (best is { Score: >= 65 })
                {
                    return best;
                }
            }
        }

        return best;
    }

    private static MediaServerSoundtrackMatchDto? BuildSpotifyDirectSoundtrackMatch(
        MediaServerContentItem item,
        SpotifySearchItem candidate)
    {
        var spotifyUrl = BuildSpotifyAlbumUrl(candidate);
        var title = NormalizeText(candidate.Name);
        if (string.IsNullOrWhiteSpace(spotifyUrl) || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }
        if (!IsSoundtrackCandidateCompatible(item.Title, title))
        {
            return null;
        }

        var subtitle = ExtractSpotifyItemArtist(candidate.Subtitle);
        var score = Math.Max(ComputeMatchScore(item.Title, title) + 6, 26);
        return new MediaServerSoundtrackMatchDto
        {
            Kind = "spotify_album",
            DeezerId = null,
            Title = title,
            Subtitle = string.IsNullOrWhiteSpace(subtitle) ? null : subtitle,
            Url = spotifyUrl,
            CoverUrl = candidate.ImageUrl,
            Score = score
        };
    }

    private static string? BuildSpotifyAlbumUrl(SpotifySearchItem candidate)
    {
        var sourceUrl = NormalizeText(candidate.SourceUrl);
        if (!string.IsNullOrWhiteSpace(sourceUrl))
        {
            return sourceUrl;
        }

        var id = NormalizeText(candidate.Id);
        return string.IsNullOrWhiteSpace(id)
            ? null
            : $"https://open.spotify.com/album/{Uri.EscapeDataString(id)}";
    }

    private sealed class MusicBrainzSoundtrackCandidate
    {
        public string Title { get; init; } = string.Empty;

        public string? ArtistHint { get; init; }

        public int Score { get; init; }
    }

    private async Task<MediaServerSoundtrackMatchDto?> TryResolveMusicBrainzCuratedMatchAsync(
        MediaServerContentItem item,
        IReadOnlyList<string> queries,
        CancellationToken cancellationToken)
    {
        var curatedCandidates = new List<MusicBrainzSoundtrackCandidate>();
        foreach (var query in queries.Take(3))
        {
            MusicBrainzRecordingSearchResults? response;
            try
            {
                response = await _musicBrainzClient.SearchAsync(query, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "MusicBrainz soundtrack candidate search failed for {Title}", item.Title);
                continue;
            }

            foreach (var recording in response?.Recordings ?? Enumerable.Empty<DeezSpoTag.Web.Services.AutoTag.Recording>())
            {
                foreach (var release in recording.Releases ?? Enumerable.Empty<MusicBrainzRelease>())
                {
                    var releaseTitle = NormalizeText(release.Title);
                    if (string.IsNullOrWhiteSpace(releaseTitle))
                    {
                        continue;
                    }

                    var score = ComputeMusicBrainzCuratedScore(item.Title, recording.Title, release);
                    if (score < 45)
                    {
                        continue;
                    }

                    curatedCandidates.Add(new MusicBrainzSoundtrackCandidate
                    {
                        Title = releaseTitle,
                        ArtistHint = BuildArtistText(release.ArtistCredit) ?? BuildArtistText(recording.ArtistCredit),
                        Score = score
                    });

                    var groupTitle = NormalizeText(release.ReleaseGroup?.Title);
                    if (string.IsNullOrWhiteSpace(groupTitle)
                        || string.Equals(groupTitle, releaseTitle, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    curatedCandidates.Add(new MusicBrainzSoundtrackCandidate
                    {
                        Title = groupTitle,
                        ArtistHint = BuildArtistText(release.ArtistCredit) ?? BuildArtistText(recording.ArtistCredit),
                        Score = Math.Max(40, score - 3)
                    });
                }
            }
        }

        var dedupedCandidates = curatedCandidates
            .GroupBy(candidate => candidate.Title, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(candidate => candidate.Score).First())
            .OrderByDescending(candidate => candidate.Score)
            .Take(8)
            .ToList();

        MediaServerSoundtrackMatchDto? best = null;
        foreach (var candidate in dedupedCandidates)
        {
            var mapped = await TryResolveCuratedCandidateViaDeezerAsync(
                item,
                candidate.Title,
                candidate.ArtistHint,
                scoreBonus: Math.Min(18, candidate.Score / 4),
                cancellationToken);
            best = SelectHigherScoreNullable(best, mapped);
            if (best is { Score: >= 65 })
            {
                break;
            }
        }

        return best;
    }

    private async Task<MediaServerSoundtrackMatchDto?> TryResolveCuratedCandidateViaDeezerAsync(
        MediaServerContentItem item,
        string candidateTitle,
        string? artistHint,
        int scoreBonus,
        CancellationToken cancellationToken)
    {
        var normalizedTitle = NormalizeText(candidateTitle);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
        {
            return null;
        }

        try
        {
            var albumResult = await _deezerClient.SearchAlbumAsync(normalizedTitle, new ApiOptions { Limit = 6 });
            var playlistResult = await _deezerClient.SearchPlaylistAsync(normalizedTitle, new ApiOptions { Limit = 4 });
            var trackResult = await _deezerClient.SearchTrackAsync(normalizedTitle, new ApiOptions { Limit = 6 });

            var albumMatch = SelectBestAlbumMatch(albumResult, item.Title, normalizedTitle);
            var playlistMatch = SelectBestPlaylistMatch(playlistResult, item.Title, normalizedTitle);
            var trackAlbumMatch = SelectBestTrackAlbumMatch(trackResult, item.Title, normalizedTitle);
            var best = ChooseBestMatch(CreateFallbackSearchMatch(normalizedTitle), albumMatch, playlistMatch, trackAlbumMatch);
            if (string.IsNullOrWhiteSpace(best.DeezerId))
            {
                return null;
            }

            var artistBoost = ComputeArtistHintBoost(artistHint, best.Subtitle);
            best.Score = Math.Min(100, best.Score + Math.Max(0, scoreBonus) + artistBoost);
            if (string.IsNullOrWhiteSpace(best.Subtitle) && !string.IsNullOrWhiteSpace(artistHint))
            {
                best.Subtitle = artistHint.Trim();
            }

            return best;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Deezer mapping from curated soundtrack candidate failed for {Title}", candidateTitle);
            return null;
        }
    }

    private static int ComputeMusicBrainzCuratedScore(string mediaTitle, string recordingTitle, MusicBrainzRelease release)
    {
        var releaseTitle = NormalizeText(release.Title);
        var score = ComputeMatchScore(mediaTitle, releaseTitle);
        score = Math.Max(score, ComputeMatchScore(mediaTitle, recordingTitle));

        var primaryType = NormalizeText(release.ReleaseGroup?.PrimaryType);
        if (string.Equals(primaryType, "Album", StringComparison.OrdinalIgnoreCase))
        {
            score += 3;
        }

        var secondaryTypes = release.ReleaseGroup?.SecondaryTypes ?? new List<string>();
        if (secondaryTypes.Any(type => string.Equals(NormalizeText(type), "Soundtrack", StringComparison.OrdinalIgnoreCase)))
        {
            score += 18;
        }

        if (LooksLikeSoundtrackTitle(releaseTitle) || LooksLikeSoundtrackTitle(release.ReleaseGroup?.Title))
        {
            score += 14;
        }

        var status = NormalizeText(release.Status);
        if (string.Equals(status, "Official", StringComparison.OrdinalIgnoreCase))
        {
            score += 4;
        }

        return score;
    }

    private static int ComputeArtistHintBoost(string? artistHint, string? resolvedSubtitle)
    {
        var hint = NormalizeMatchingText(artistHint);
        var subtitle = NormalizeMatchingText(resolvedSubtitle);
        if (string.IsNullOrWhiteSpace(hint) || string.IsNullOrWhiteSpace(subtitle))
        {
            return 0;
        }

        if (subtitle.Contains(hint, StringComparison.OrdinalIgnoreCase)
            || hint.Contains(subtitle, StringComparison.OrdinalIgnoreCase))
        {
            return 8;
        }

        return 0;
    }

    private static bool LooksLikeSoundtrackTitle(string? title)
    {
        var normalized = NormalizeMatchingText(title);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return normalized.Contains("soundtrack", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("ost", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("score", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("original motion picture", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("original television", StringComparison.OrdinalIgnoreCase);
    }

    private static string? BuildArtistText(List<MusicBrainzArtistCredit>? credits)
    {
        if (credits == null || credits.Count == 0)
        {
            return null;
        }

        var names = credits
            .Select(credit => NormalizeText(credit?.Name))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return names.Length == 0 ? null : string.Join(", ", names);
    }

    private async Task<string?> TryResolveDeezerAlbumIdFromSpotifyCandidateAsync(
        SpotifySearchItem candidate,
        CancellationToken cancellationToken)
    {
        var metadata = new SpotifyUrlMetadata(
            Type: "album",
            Id: candidate.Id,
            Name: candidate.Name,
            SourceUrl: candidate.SourceUrl,
            ImageUrl: candidate.ImageUrl,
            Subtitle: ExtractSpotifyItemArtist(candidate.Subtitle),
            TotalTracks: null,
            DurationMs: null,
            TrackList: new List<SpotifyTrackSummary>(),
            AlbumList: new List<SpotifyAlbumSummary>(),
            OwnerName: null,
            Followers: null,
            SnapshotId: null);

        try
        {
            return await _spotifyDeezerAlbumResolver.ResolveAlbumIdAsync(metadata, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Spotify->Deezer soundtrack album resolution failed for Spotify album {AlbumName}", candidate.Name);
            return null;
        }
    }

    private async Task<ApiAlbum?> TryGetDeezerAlbumAsync(string albumId)
    {
        try
        {
            return await _deezerClient.GetAlbum(albumId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed loading Deezer album {AlbumId} for soundtrack hydration.", albumId);
            return null;
        }
    }

    private static string? ExtractSpotifyItemArtist(string? subtitle)
    {
        if (string.IsNullOrWhiteSpace(subtitle))
        {
            return null;
        }

        var raw = subtitle.Trim();
        var index = raw.IndexOf(" • ", StringComparison.Ordinal);
        if (index < 0)
        {
            return raw;
        }

        var artist = raw[..index].Trim();
        return string.IsNullOrWhiteSpace(artist) ? raw : artist;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static MediaServerSoundtrackMatchDto SelectHigherScore(
        MediaServerSoundtrackMatchDto baseline,
        MediaServerSoundtrackMatchDto? candidate)
    {
        if (candidate == null)
        {
            return baseline;
        }

        return candidate.Score > baseline.Score ? candidate : baseline;
    }

    private static MediaServerSoundtrackMatchDto? SelectHigherScoreNullable(
        MediaServerSoundtrackMatchDto? baseline,
        MediaServerSoundtrackMatchDto? candidate)
    {
        if (baseline == null)
        {
            return candidate;
        }

        if (candidate == null)
        {
            return baseline;
        }

        return candidate.Score > baseline.Score ? candidate : baseline;
    }

    private static MediaServerSoundtrackMatchDto ChooseBestMatch(
        MediaServerSoundtrackMatchDto fallback,
        MediaServerSoundtrackMatchDto? album,
        MediaServerSoundtrackMatchDto? playlist,
        MediaServerSoundtrackMatchDto? trackAlbum)
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

        if (trackAlbum != null && trackAlbum.Score > best.Score)
        {
            best = trackAlbum;
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
            var id = GetDynamicString(item, "id");
            var albumTitle = GetDynamicString(item, "title");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(albumTitle))
            {
                continue;
            }
            if (!IsSoundtrackCandidateCompatible(title, albumTitle))
            {
                continue;
            }

            var artistName = GetDynamicNestedString(item, "artist", "name");
            var score = ComputeMatchScore(title, albumTitle);
            var model = new MediaServerSoundtrackMatchDto
            {
                Kind = "album",
                DeezerId = id,
                Title = albumTitle,
                Subtitle = artistName,
                Url = $"https://www.deezer.com/album/{Uri.EscapeDataString(id)}",
                CoverUrl = GetDynamicString(item, "cover_medium") ?? GetDynamicString(item, "cover") ?? BuildDeezerSearchUrl(query),
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
            var id = GetDynamicString(item, "id");
            var playlistTitle = GetDynamicString(item, "title");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(playlistTitle))
            {
                continue;
            }
            if (!IsSoundtrackCandidateCompatible(title, playlistTitle))
            {
                continue;
            }

            var creator = GetDynamicNestedString(item, "user", "name");
            var score = ComputeMatchScore(title, playlistTitle) - 2;
            var model = new MediaServerSoundtrackMatchDto
            {
                Kind = "playlist",
                DeezerId = id,
                Title = playlistTitle,
                Subtitle = creator,
                Url = $"https://www.deezer.com/playlist/{Uri.EscapeDataString(id)}",
                CoverUrl = GetDynamicString(item, "picture_medium") ?? GetDynamicString(item, "picture") ?? BuildDeezerSearchUrl(query),
                Score = score
            };

            if (best == null || model.Score > best.Score)
            {
                best = model;
            }
        }

        return best;
    }

    private static MediaServerSoundtrackMatchDto? SelectBestTrackAlbumMatch(DeezerSearchResult? result, string title, string query)
    {
        if (result?.Data == null || result.Data.Length == 0)
        {
            return null;
        }

        MediaServerSoundtrackMatchDto? best = null;
        foreach (var item in result.Data)
        {
            var albumNode = GetDynamicProperty(item, "album");
            if (albumNode == null)
            {
                continue;
            }

            var albumId = GetDynamicString(albumNode, "id");
            var albumTitle = GetDynamicString(albumNode, "title");
            if (string.IsNullOrWhiteSpace(albumId) || string.IsNullOrWhiteSpace(albumTitle))
            {
                continue;
            }
            if (!IsSoundtrackCandidateCompatible(title, albumTitle))
            {
                continue;
            }

            var artistName = GetDynamicNestedString(item, "artist", "name");
            var score = ComputeMatchScore(title, albumTitle) - 1;
            var model = new MediaServerSoundtrackMatchDto
            {
                Kind = "album",
                DeezerId = albumId,
                Title = albumTitle,
                Subtitle = artistName,
                Url = $"https://www.deezer.com/album/{Uri.EscapeDataString(albumId)}",
                CoverUrl = GetDynamicString(albumNode, "cover_medium") ?? GetDynamicString(albumNode, "cover") ?? BuildDeezerSearchUrl(query),
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
        if (string.IsNullOrWhiteSpace(normalizedTarget) || string.IsNullOrWhiteSpace(normalizedCandidate))
        {
            return 1;
        }

        var targetContains = normalizedCandidate.Contains(normalizedTarget, StringComparison.OrdinalIgnoreCase);
        var candidateContains = normalizedTarget.Contains(normalizedCandidate, StringComparison.OrdinalIgnoreCase);
        var overlap = ComputeTokenOverlap(normalizedTarget, normalizedCandidate);
        var score = 10 + (int)Math.Round(overlap * 58);

        if (targetContains || candidateContains)
        {
            score += 22;
        }

        if (LooksLikeSoundtrackTitle(normalizedCandidate))
        {
            score += 10;
        }

        if (overlap < 0.2 && !(targetContains || candidateContains))
        {
            score = Math.Min(score, 32);
        }

        return Math.Clamp(score, 1, 100);
    }

    private static bool IsSoundtrackCandidateCompatible(string mediaTitle, string candidateTitle)
    {
        var normalizedMedia = NormalizeMatchingText(mediaTitle);
        var normalizedCandidate = NormalizeMatchingText(candidateTitle);
        if (string.IsNullOrWhiteSpace(normalizedMedia) || string.IsNullOrWhiteSpace(normalizedCandidate))
        {
            return false;
        }

        if (normalizedCandidate.Contains(normalizedMedia, StringComparison.OrdinalIgnoreCase)
            || normalizedMedia.Contains(normalizedCandidate, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var mediaTokens = ExtractCoreTokens(normalizedMedia);
        var candidateTokens = ExtractCoreTokens(normalizedCandidate);
        if (mediaTokens.Count == 0 || candidateTokens.Count == 0)
        {
            return false;
        }

        var shared = mediaTokens.Count(token => candidateTokens.Contains(token));
        if (shared >= 2)
        {
            return true;
        }

        var ratio = (double)shared / mediaTokens.Count;
        return ratio >= 0.34;
    }

    private static double ComputeTokenOverlap(string normalizedLeft, string normalizedRight)
    {
        var leftTokens = ExtractCoreTokens(normalizedLeft);
        var rightTokens = ExtractCoreTokens(normalizedRight);
        if (leftTokens.Count == 0 || rightTokens.Count == 0)
        {
            return 0;
        }

        var shared = leftTokens.Count(token => rightTokens.Contains(token));
        return (double)shared / leftTokens.Count;
    }

    private static HashSet<string> ExtractCoreTokens(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length >= 3)
            .Where(token => !SoundtrackStopWords.Contains(token))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
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

    private static IReadOnlyList<string> BuildSoundtrackQueries(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return new[] { "soundtrack" };
        }

        var normalizedTitle = title.Trim();
        var sanitizedTitle = SanitizeSoundtrackTitle(normalizedTitle);
        var queries = new List<string>
        {
            $"{normalizedTitle} soundtrack",
            $"{normalizedTitle} original soundtrack",
            $"{normalizedTitle} ost",
            normalizedTitle
        };

        if (!string.Equals(sanitizedTitle, normalizedTitle, StringComparison.OrdinalIgnoreCase))
        {
            queries.Add($"{sanitizedTitle} soundtrack");
            queries.Add($"{sanitizedTitle} original soundtrack");
            queries.Add($"{sanitizedTitle} ost");
            queries.Add(sanitizedTitle);
        }

        return queries
            .Select(query => query.Trim())
            .Where(query => !string.IsNullOrWhiteSpace(query))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string SanitizeSoundtrackTitle(string title)
    {
        var sanitized = TitleBracketNoiseRegex.Replace(title, " ");
        foreach (var token in SoundtrackNoiseTokens)
        {
            sanitized = Regex.Replace(
                sanitized,
                $@"\b{Regex.Escape(token)}\b",
                " ",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        sanitized = TitleWhitespaceRegex.Replace(sanitized, " ").Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? title.Trim() : sanitized;
    }

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

    private static string? GetDynamicString(object? source, string propertyName)
    {
        var token = AsDynamicToken(source);
        if (token is not JObject obj || !obj.TryGetValue(propertyName, StringComparison.OrdinalIgnoreCase, out var value))
        {
            return null;
        }

        return value.Type switch
        {
            JTokenType.String => value.Value<string>(),
            JTokenType.Integer => value.ToString(),
            JTokenType.Float => value.ToString(),
            _ => null
        };
    }

    private static string? GetDynamicNestedString(object? source, string objectName, string propertyName)
    {
        var nested = GetDynamicProperty(source, objectName);
        if (nested == null)
        {
            return null;
        }

        return GetDynamicString(nested, propertyName);
    }

    private static object? GetDynamicProperty(object? source, string propertyName)
    {
        var token = AsDynamicToken(source);
        if (token is not JObject obj || !obj.TryGetValue(propertyName, StringComparison.OrdinalIgnoreCase, out var value))
        {
            return null;
        }

        return value;
    }

    private static JToken? AsDynamicToken(object? source)
    {
        if (source == null)
        {
            return null;
        }

        if (source is JToken token)
        {
            return token;
        }

        if (source is JsonElement element)
        {
            return JToken.Parse(element.GetRawText());
        }

        try
        {
            return JToken.FromObject(source);
        }
        catch
        {
            return null;
        }
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
