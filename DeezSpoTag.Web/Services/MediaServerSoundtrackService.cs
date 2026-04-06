using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DeezSpoTag.Integrations.Jellyfin;
using DeezSpoTag.Integrations.Plex;
using MusicBrainzArtistCredit = DeezSpoTag.Web.Services.AutoTag.ArtistCredit;
using MusicBrainzClient = DeezSpoTag.Web.Services.AutoTag.MusicBrainzClient;
using MusicBrainzRecordingSearchResults = DeezSpoTag.Web.Services.AutoTag.RecordingSearchResults;
using MusicBrainzRelease = DeezSpoTag.Web.Services.AutoTag.ReleaseSmall;

namespace DeezSpoTag.Web.Services;

public sealed partial class MediaServerSoundtrackService
{
    public sealed class Dependencies
    {
        public required PlatformAuthService PlatformAuthService { get; init; }

        public required PlexApiClient PlexApiClient { get; init; }

        public required JellyfinApiClient JellyfinApiClient { get; init; }

        public required SpotifySearchService SpotifySearchService { get; init; }

        public required MusicBrainzClient MusicBrainzClient { get; init; }

        public required MediaServerSoundtrackStore Store { get; init; }

        public required MediaServerSoundtrackCacheRepository CacheRepository { get; init; }

        public required IHttpClientFactory HttpClientFactory { get; init; }
    }

    private const int DefaultItemLimit = 120;
    private const int RegexTimeoutMilliseconds = 250;
    private const string SpotifyKindPrefix = "spotify_";
    private const string MatchKindSearch = "search";
    private const string MatchKindAlbum = "album";
    private const string MatchKindPlaylist = "playlist";
    private const string MatchKindTrack = "track";
    private const string MatchProviderSpotify = "spotify";
    private const string MatchProviderDeezer = "deezer";
    private const string MediaTypeSeries = "series";
    private const string MediaTypeMovie = "movie";
    private const string SoundtrackToken = "soundtrack";
    private const string JellyfinImagePrimary = "Primary";
    private const string JellyfinImageThumb = "Thumb";
    private const string SpotifyMarkdownLinkPattern = @"\[(?<title>[^\]]+)\]\((?<url>https:\/\/open\.spotify\.com\/(?<type>album|playlist|track)\/(?<id>[A-Za-z0-9]{22})(?:\?[^)]*)?)";
    private const string SpotifyWebLinkPattern = @"open\.spotify\.com\/(?<type>album|playlist|track)\/(?<id>[A-Za-z0-9]{22})";
    private const string SpotifyUriPattern = @"^spotify:(?<type>album|playlist|track):(?<id>[A-Za-z0-9]{22})$";
    private const string DeezerWebLinkPattern = @"deezer\.com\/(?:[a-z]{2}(?:-[a-z]{2})?\/)?(?<type>album|playlist|track)\/(?<id>\d+)";
    private static readonly TimeSpan SoundtrackCacheTtl = TimeSpan.FromHours(12);
    private static readonly TimeSpan RegexDynamicTimeout = TimeSpan.FromMilliseconds(RegexTimeoutMilliseconds);
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
        "original", "motion", "picture", "television", MediaTypeSeries, "season", MediaTypeMovie, "film",
        SoundtrackToken, "score", "music", "ost"
    };
    private static readonly HashSet<string> EditionDecoratorTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "director", "directors", "cut", "edition", "extended", "special", "theatrical", "unrated",
        "uncut", "ultimate", "final", "deluxe", "anniversary", "collector", "collectors", "remaster",
        "remastered", "version", "redux", "imax", "enhanced"
    };

    private readonly PlatformAuthService _platformAuthService;
    private readonly PlexApiClient _plexApiClient;
    private readonly JellyfinApiClient _jellyfinApiClient;
    private readonly SpotifySearchService _spotifySearchService;
    private readonly MusicBrainzClient _musicBrainzClient;
    private readonly MediaServerSoundtrackStore _store;
    private readonly MediaServerSoundtrackCacheRepository _cacheRepository;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MediaServerSoundtrackService> _logger;
    private readonly ConcurrentDictionary<string, (DateTimeOffset CachedAtUtc, MediaServerSoundtrackMatchDto Match)> _soundtrackCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<MediaServerSoundtrackMatchDto>> _soundtrackResolutionInFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _backgroundSoundtrackPersistInFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _soundtrackWarmupGate = new(6, 6);
    private readonly SemaphoreSlim _mediaCacheSyncGate = new(1, 1);
    private int _pendingSyncJobs;
    private volatile bool _syncRunning;
    private DateTimeOffset? _lastSyncStartedUtc;
    private DateTimeOffset? _lastSyncCompletedUtc;

    private sealed class TvEpisodeFetchResult
    {
        public string ShowId { get; set; } = string.Empty;

        public string ShowTitle { get; set; } = string.Empty;

        public string? ShowImageUrl { get; set; }

        public List<MediaServerTvShowSeasonItem> Seasons { get; set; } = new();

        public List<MediaServerTvShowEpisodeItem> Episodes { get; set; } = new();
    }

    public MediaServerSoundtrackService(
        Dependencies dependencies,
        ILogger<MediaServerSoundtrackService> logger)
    {
        _platformAuthService = dependencies.PlatformAuthService;
        _plexApiClient = dependencies.PlexApiClient;
        _jellyfinApiClient = dependencies.JellyfinApiClient;
        _spotifySearchService = dependencies.SpotifySearchService;
        _musicBrainzClient = dependencies.MusicBrainzClient;
        _store = dependencies.Store;
        _cacheRepository = dependencies.CacheRepository;
        _httpClientFactory = dependencies.HttpClientFactory;
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
        await _store.UpdateAsync(settings => ApplyConfigurationUpdates(settings, request), cancellationToken);

        return await GetConfigurationAsync(refreshDiscovery: false, cancellationToken);
    }

    private static bool ApplyConfigurationUpdates(
        MediaServerSoundtrackSettings settings,
        MediaServerSoundtrackConfigurationUpdateRequest request)
    {
        var changed = false;
        foreach (var serverUpdate in request.Servers)
        {
            changed |= ApplyServerConfigurationUpdate(settings, serverUpdate);
        }

        return changed;
    }

    private static bool ApplyServerConfigurationUpdate(
        MediaServerSoundtrackSettings settings,
        MediaServerSoundtrackServerPreferenceUpdateDto? serverUpdate)
    {
        var serverType = NormalizeServerType(serverUpdate?.ServerType);
        if (string.IsNullOrWhiteSpace(serverType))
        {
            return false;
        }

        var changed = false;
        var server = GetOrCreateServer(settings, serverType);
        if (serverUpdate?.AutoIncludeNewLibraries is bool autoInclude
            && server.AutoIncludeNewLibraries != autoInclude)
        {
            server.AutoIncludeNewLibraries = autoInclude;
            changed = true;
        }

        foreach (var libraryUpdate in serverUpdate?.Libraries ?? Enumerable.Empty<MediaServerSoundtrackLibraryPreferenceUpdateDto>())
        {
            changed |= ApplyLibraryConfigurationUpdate(server, libraryUpdate);
        }

        return changed;
    }

    private static bool ApplyLibraryConfigurationUpdate(
        MediaServerSoundtrackServerSettings server,
        MediaServerSoundtrackLibraryPreferenceUpdateDto? libraryUpdate)
    {
        var libraryId = NormalizeText(libraryUpdate?.LibraryId);
        if (string.IsNullOrWhiteSpace(libraryId))
        {
            return false;
        }

        var changed = false;
        var library = GetOrCreateLibrarySettings(server, libraryId, ref changed);
        changed |= UpdateLibraryToggleValue(
            libraryUpdate?.Enabled,
            library.Enabled,
            value => library.Enabled = value);
        changed |= UpdateLibraryToggleValue(
            libraryUpdate?.Ignored,
            library.Ignored,
            value => library.Ignored = value);
        library.UserConfigured = true;
        return changed;
    }

    private static MediaServerSoundtrackLibrarySettings GetOrCreateLibrarySettings(
        MediaServerSoundtrackServerSettings server,
        string libraryId,
        ref bool changed)
    {
        if (server.Libraries.TryGetValue(libraryId, out var library))
        {
            return library;
        }

        var nowUtc = DateTimeOffset.UtcNow;
        library = new MediaServerSoundtrackLibrarySettings
        {
            LibraryId = libraryId,
            Name = libraryId,
            Category = MediaServerSoundtrackConstants.MovieCategory,
            Enabled = true,
            Ignored = false,
            FirstDiscoveredUtc = nowUtc,
            LastSeenUtc = nowUtc
        };
        server.Libraries[libraryId] = library;
        changed = true;
        return library;
    }

    private static bool UpdateLibraryToggleValue(bool? requestedValue, bool currentValue, Action<bool> applyValue)
    {
        if (requestedValue is not bool nextValue || currentValue == nextValue)
        {
            return false;
        }

        applyValue(nextValue);
        return true;
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
            NormalizeMatchMetadataRows(persisted);
            return CreateItemsResponse(normalizedCategory, persisted);
        }

        var auth = await _platformAuthService.LoadAsync();
        var settings = await _store.LoadAsync(cancellationToken);

        var targets = ResolveTargetLibraries(settings, auth, normalizedCategory, normalizedServerType, normalizedLibraryId);
        if (targets.Count == 0)
        {
            return CreateItemsResponse(normalizedCategory, persisted);
        }

        try
        {
            var items = await FetchFreshLibraryItemsAsync(auth, targets, itemOffset, itemLimit, cancellationToken);
            var persistedByCacheKey = await LoadPersistedRowsForItemsAsync(items, cancellationToken);
            var resultItems = BuildResultItems(items, persistedByCacheKey);
            await ResolveAtLeastOneSoundtrackMatchForResponseAsync(resultItems, cancellationToken);
            NormalizeMatchMetadataRows(resultItems);
            await _cacheRepository.UpsertItemsAsync(resultItems, cancellationToken);
            QueueBackgroundSoundtrackResolution(resultItems);
            var finalItems = resultItems.Count > 0 ? resultItems : persisted;
            return CreateItemsResponse(normalizedCategory, finalItems);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed loading media-server soundtrack items from source; using persisted soundtrack rows.");
            return CreateItemsResponse(normalizedCategory, persisted);
        }
    }

    private static MediaServerSoundtrackItemsResponseDto CreateItemsResponse(
        string category,
        List<MediaServerSoundtrackItemDto> items)
    {
        return new MediaServerSoundtrackItemsResponseDto
        {
            Category = category,
            Total = items.Count,
            Items = items
        };
    }

    private static void NormalizeMatchMetadataRows(List<MediaServerSoundtrackItemDto> rows)
    {
        foreach (var row in rows)
        {
            NormalizeMatchMetadata(row.Soundtrack);
        }
    }

    private async Task<List<MediaServerContentItem>> FetchFreshLibraryItemsAsync(
        PlatformAuthState auth,
        IReadOnlyList<(string ServerType, string LibraryId, string LibraryName, string Category)> targets,
        int itemOffset,
        int itemLimit,
        CancellationToken cancellationToken)
    {
        var items = new List<MediaServerContentItem>();
        foreach (var target in targets)
        {
            var fetched = await FetchLibraryItemsAsync(auth, target, itemOffset, itemLimit, cancellationToken);
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

        return items
            .OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Year ?? 0)
            .Take(itemLimit)
            .ToList();
    }

    private async Task<Dictionary<string, MediaServerSoundtrackItemDto>> LoadPersistedRowsForItemsAsync(
        IReadOnlyList<MediaServerContentItem> items,
        CancellationToken cancellationToken)
    {
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
            MergePersistedRowsByCacheKey(persistedByCacheKey, libraryScopedRows.Values);
        }

        return persistedByCacheKey;
    }

    private static void MergePersistedRowsByCacheKey(
        Dictionary<string, MediaServerSoundtrackItemDto> target,
        IEnumerable<MediaServerSoundtrackItemDto> rows)
    {
        foreach (var row in rows)
        {
            var cacheKey = BuildSoundtrackItemCacheKey(row.ServerType, row.LibraryId, row.ItemId);
            if (!string.IsNullOrWhiteSpace(cacheKey))
            {
                target[cacheKey] = row;
            }
        }
    }

    private static List<MediaServerSoundtrackItemDto> BuildResultItems(
        IReadOnlyList<MediaServerContentItem> items,
        Dictionary<string, MediaServerSoundtrackItemDto> persistedByCacheKey)
    {
        return items
            .Select(item =>
            {
                var cacheKey = BuildSoundtrackItemCacheKey(item.ServerType, item.LibraryId, item.ItemId);
                persistedByCacheKey.TryGetValue(cacheKey, out var persistedRow);
                return BuildSoundtrackItemDto(item, persistedRow);
            })
            .ToList();
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

        var manualQuery = NormalizeText(request.ManualQuery);
        var match = string.IsNullOrWhiteSpace(manualQuery)
            ? await ResolveSoundtrackAsync(item, cancellationToken)
            : await ResolveManualSoundtrackAsync(item, manualQuery, cancellationToken);
        NormalizeMatchMetadata(match);
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
            ContentHash = ComputeContentHash(item),
            IsActive = true,
            LastSeenUtc = DateTimeOffset.UtcNow,
            Soundtrack = match
        };

        await _cacheRepository.UpsertItemsAsync(new[] { dto }, cancellationToken);
        return dto;
    }

    private async Task<MediaServerSoundtrackMatchDto> ResolveManualSoundtrackAsync(
        MediaServerContentItem item,
        string manualQuery,
        CancellationToken cancellationToken)
    {
        MediaServerSoundtrackMatchDto resolvedMatch;
        if (!TryCreateManualDirectMatch(item, manualQuery, out resolvedMatch))
        {
            var manualQueries = BuildManualSoundtrackQueries(item.Title, item.Year, manualQuery);
            resolvedMatch = await ResolveSoundtrackDirectAsync(item, cancellationToken, manualQueries);
        }

        NormalizeMatchMetadata(resolvedMatch);
        if (HasResolvedSoundtrack(resolvedMatch))
        {
            resolvedMatch.Locked = true;
            resolvedMatch.Reason = "manual_override";
            resolvedMatch.ResolvedAtUtc ??= DateTimeOffset.UtcNow;
        }

        var cacheKey = BuildSoundtrackCacheKey(item);
        if (!string.IsNullOrWhiteSpace(cacheKey))
        {
            _soundtrackCache[cacheKey] = (DateTimeOffset.UtcNow, resolvedMatch);
        }

        return resolvedMatch;
    }

    private static bool TryCreateManualDirectMatch(
        MediaServerContentItem item,
        string manualQuery,
        out MediaServerSoundtrackMatchDto match)
    {
        match = default!;
        var query = NormalizeText(manualQuery);
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        if (TryParseSpotifyManualTarget(query, out var spotifyType, out var spotifyId))
        {
            if (IsManualTrackBlockedForMovie(item.Category, spotifyType))
            {
                return false;
            }

            var normalizedType = NormalizeText(spotifyType).ToLowerInvariant();
            var normalizedId = NormalizeText(spotifyId);
            match = new MediaServerSoundtrackMatchDto
            {
                Kind = $"{SpotifyKindPrefix}{normalizedType}",
                DeezerId = null,
                Title = $"Manual {normalizedType}",
                Subtitle = null,
                Url = $"https://open.spotify.com/{normalizedType}/{normalizedId}",
                CoverUrl = null,
                Score = 100
            };
            return true;
        }

        if (!TryParseDeezerManualTarget(query, out var deezerType, out var deezerId))
        {
            return false;
        }

        if (IsManualTrackBlockedForMovie(item.Category, deezerType))
        {
            return false;
        }

        var normalizedDeezerType = NormalizeText(deezerType).ToLowerInvariant();
        var normalizedDeezerId = NormalizeText(deezerId);
        match = new MediaServerSoundtrackMatchDto
        {
            Kind = normalizedDeezerType,
            DeezerId = normalizedDeezerId,
            Title = $"Manual {normalizedDeezerType}",
            Subtitle = null,
            Url = $"https://www.deezer.com/{normalizedDeezerType}/{normalizedDeezerId}",
            CoverUrl = null,
            Score = 100
        };
        return true;
    }

    private static bool IsManualTrackBlockedForMovie(string? category, string? type)
    {
        var normalizedCategory = NormalizeText(category).ToLowerInvariant();
        var normalizedType = NormalizeText(type).ToLowerInvariant();
        return string.Equals(normalizedCategory, MediaServerSoundtrackConstants.MovieCategory, StringComparison.Ordinal)
            && string.Equals(normalizedType, MatchKindTrack, StringComparison.Ordinal);
    }

    private static string[] BuildManualSoundtrackQueries(string title, int? year, string manualQuery)
    {
        var normalizedManualQuery = NormalizeText(manualQuery);
        if (string.IsNullOrWhiteSpace(normalizedManualQuery))
        {
            return BuildSoundtrackQueries(title, year);
        }

        var queries = new List<string>
        {
            normalizedManualQuery
        };

        if (!normalizedManualQuery.Contains(SoundtrackToken, StringComparison.OrdinalIgnoreCase))
        {
            queries.Add($"{normalizedManualQuery} {SoundtrackToken}");
        }

        if (!normalizedManualQuery.Contains("ost", StringComparison.OrdinalIgnoreCase))
        {
            queries.Add($"{normalizedManualQuery} ost");
        }

        queries.AddRange(BuildSoundtrackQueries(title, year));

        return queries
            .Select(query => NormalizeText(query))
            .Where(query => !string.IsNullOrWhiteSpace(query))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool TryParseSpotifyManualTarget(string value, out string type, out string id)
    {
        type = string.Empty;
        id = string.Empty;

        var uriMatch = SpotifyUriRegex().Match(value);
        if (uriMatch.Success)
        {
            type = NormalizeText(uriMatch.Groups["type"].Value).ToLowerInvariant();
            id = NormalizeText(uriMatch.Groups["id"].Value);
            return !string.IsNullOrWhiteSpace(type) && !string.IsNullOrWhiteSpace(id);
        }

        var webMatch = SpotifyWebLinkRegex().Match(value);
        if (!webMatch.Success)
        {
            return false;
        }

        type = NormalizeText(webMatch.Groups["type"].Value).ToLowerInvariant();
        id = NormalizeText(webMatch.Groups["id"].Value);
        return !string.IsNullOrWhiteSpace(type) && !string.IsNullOrWhiteSpace(id);
    }

    private static bool TryParseDeezerManualTarget(string value, out string type, out string id)
    {
        type = string.Empty;
        id = string.Empty;

        var webMatch = DeezerWebLinkRegex().Match(value);
        if (!webMatch.Success)
        {
            return false;
        }

        type = NormalizeText(webMatch.Groups["type"].Value).ToLowerInvariant();
        id = NormalizeText(webMatch.Groups["id"].Value);
        return !string.IsNullOrWhiteSpace(type) && !string.IsNullOrWhiteSpace(id);
    }

    private static MediaServerSoundtrackItemDto BuildSoundtrackItemDto(
        MediaServerContentItem item,
        MediaServerSoundtrackItemDto? persistedRow)
    {
        var contentHash = ComputeContentHash(item);
        var sameContent = string.Equals(
            NormalizeText(persistedRow?.ContentHash),
            NormalizeText(contentHash),
            StringComparison.Ordinal);
        var soundtrack = sameContent
            ? persistedRow?.Soundtrack
            : null;
        if (soundtrack == null)
        {
            soundtrack = CreateFallbackSearchMatch(BuildSoundtrackQueries(item.Title)[0]);
            soundtrack.Locked = false;
            soundtrack.RetryCount = 0;
            soundtrack.ResolvedAtUtc = null;
        }

        NormalizeMatchMetadata(soundtrack);

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
            ContentHash = contentHash,
            IsActive = true,
            FirstSeenUtc = persistedRow?.FirstSeenUtc,
            LastSeenUtc = DateTimeOffset.UtcNow,
            Soundtrack = soundtrack
        };
    }

    public async Task SyncPersistentMediaCacheAsync(
        bool fullRefresh = false,
        CancellationToken cancellationToken = default)
    {
        await _mediaCacheSyncGate.WaitAsync(cancellationToken);

        _syncRunning = true;
        _lastSyncStartedUtc = DateTimeOffset.UtcNow;
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
                try
                {
                    await SyncLibraryMediaToPersistentCacheAsync(auth, target, fullRefresh, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Soundtrack sync failed for {ServerType}/{LibraryId}.", target.ServerType, target.LibraryId);
                }
            }

            _lastSyncCompletedUtc = DateTimeOffset.UtcNow;
        }
        finally
        {
            _syncRunning = false;
            _mediaCacheSyncGate.Release();
        }
    }

    public void TriggerPersistentMediaCacheSync(bool fullRefresh = false)
    {
        Interlocked.Increment(ref _pendingSyncJobs);
        _ = Task.Run(async () =>
        {
            try
            {
                await SyncPersistentMediaCacheAsync(fullRefresh, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Background soundtrack media cache sync failed.");
            }
            finally
            {
                Interlocked.Decrement(ref _pendingSyncJobs);
            }
        });
    }

    public async Task<MediaServerSoundtrackSyncStatusDto> GetSyncStatusAsync(CancellationToken cancellationToken)
    {
        var states = await _cacheRepository.GetLibrarySyncStatesAsync(cancellationToken);
        return new MediaServerSoundtrackSyncStatusDto
        {
            SyncRunning = _syncRunning,
            LastSyncStartedUtc = _lastSyncStartedUtc,
            LastSyncCompletedUtc = _lastSyncCompletedUtc,
            PendingJobs = Math.Max(Interlocked.CompareExchange(ref _pendingSyncJobs, 0, 0), 0),
            Libraries = states
        };
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
        (string ServerType, string LibraryId, string LibraryName, string Category) target,
        bool fullRefresh,
        CancellationToken cancellationToken)
    {
        const int batchSize = 150;
        var syncState = await _cacheRepository.GetLibrarySyncStateAsync(target.ServerType, target.LibraryId, cancellationToken);
        var syncStartedUtc = DateTimeOffset.UtcNow;
        var offset = fullRefresh ? 0 : Math.Max(syncState?.LastOffset ?? 0, 0);
        var syncWindowStartedUtc = syncStartedUtc;
        var totalProcessed = syncState?.TotalProcessed ?? 0;

        await _cacheRepository.UpsertLibrarySyncStateAsync(new MediaServerSoundtrackLibrarySyncStateDto
        {
            ServerType = target.ServerType,
            LibraryId = target.LibraryId,
            Category = target.Category,
            Status = "running",
            LastOffset = offset,
            LastBatchCount = 0,
            TotalProcessed = totalProcessed,
            LastSyncUtc = syncStartedUtc,
            LastSuccessUtc = syncState?.LastSuccessUtc,
            LastError = null,
            UpdatedAtUtc = syncStartedUtc
        }, cancellationToken);

        try
        {
            var currentOffset = offset;
            var lastBatchCount = 0;
            var completedCycle = false;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fetched = await FetchLibraryItemsAsync(auth, target, currentOffset, batchSize, cancellationToken);
                if (fetched.Count == 0)
                {
                    completedCycle = true;
                    currentOffset = 0;
                    lastBatchCount = 0;
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

                foreach (var row in rows)
                {
                    NormalizeMatchMetadata(row.Soundtrack);
                }

                await _cacheRepository.UpsertItemsAsync(rows, cancellationToken);
                QueueBackgroundSoundtrackResolution(rows);

                totalProcessed += rows.Count;
                lastBatchCount = rows.Count;
                currentOffset += fetched.Count;
                completedCycle = fetched.Count < batchSize;

                await _cacheRepository.UpsertLibrarySyncStateAsync(new MediaServerSoundtrackLibrarySyncStateDto
                {
                    ServerType = target.ServerType,
                    LibraryId = target.LibraryId,
                    Category = target.Category,
                    Status = "running",
                    LastOffset = currentOffset,
                    LastBatchCount = lastBatchCount,
                    TotalProcessed = totalProcessed,
                    LastSyncUtc = syncStartedUtc,
                    LastSuccessUtc = syncState?.LastSuccessUtc,
                    LastError = null,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                }, cancellationToken);

                if (!fullRefresh)
                {
                    break;
                }
            } while (!completedCycle);

            if (completedCycle && fullRefresh)
            {
                await _cacheRepository.DeactivateLibraryItemsNotSeenSinceAsync(
                    target.ServerType,
                    target.LibraryId,
                    syncWindowStartedUtc,
                    cancellationToken);
            }

            await _cacheRepository.UpsertLibrarySyncStateAsync(new MediaServerSoundtrackLibrarySyncStateDto
            {
                ServerType = target.ServerType,
                LibraryId = target.LibraryId,
                Category = target.Category,
                Status = "idle",
                LastOffset = completedCycle ? 0 : currentOffset,
                LastBatchCount = lastBatchCount,
                TotalProcessed = totalProcessed,
                LastSyncUtc = syncStartedUtc,
                LastSuccessUtc = DateTimeOffset.UtcNow,
                LastError = null,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _cacheRepository.UpsertLibrarySyncStateAsync(new MediaServerSoundtrackLibrarySyncStateDto
            {
                ServerType = target.ServerType,
                LibraryId = target.LibraryId,
                Category = target.Category,
                Status = "error",
                LastOffset = offset,
                LastBatchCount = 0,
                TotalProcessed = totalProcessed,
                LastSyncUtc = syncStartedUtc,
                LastSuccessUtc = syncState?.LastSuccessUtc,
                LastError = ex.Message,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            }, cancellationToken);
            throw;
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
                if (TryEnterBackgroundPersist(cacheKey))
                {
                    await ResolveAndPersistBackgroundSoundtrackAsync(row, cacheKey);
                }
            }
        });
    }

    private bool TryEnterBackgroundPersist(string cacheKey)
        => !string.IsNullOrWhiteSpace(cacheKey)
           && _backgroundSoundtrackPersistInFlight.TryAdd(cacheKey, 0);

    private async Task ResolveAndPersistBackgroundSoundtrackAsync(
        MediaServerSoundtrackItemDto row,
        string cacheKey)
    {
        try
        {
            var sourceItem = CreateContentItemFromRow(row);
            var resolvedMatch = await ResolveSoundtrackAsync(sourceItem, CancellationToken.None);
            if (AreMatchesEquivalent(row.Soundtrack, resolvedMatch))
            {
                return;
            }

            NormalizeResolvedMatchRetryCount(row.Soundtrack, resolvedMatch);
            NormalizeMatchMetadata(resolvedMatch);
            await PersistUpdatedSoundtrackAsync(row, resolvedMatch);
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

    private static MediaServerContentItem CreateContentItemFromRow(MediaServerSoundtrackItemDto row)
    {
        return new MediaServerContentItem
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
    }

    private static void NormalizeResolvedMatchRetryCount(
        MediaServerSoundtrackMatchDto? previousMatch,
        MediaServerSoundtrackMatchDto resolvedMatch)
    {
        var previousRetries = previousMatch?.RetryCount ?? 0;
        resolvedMatch.RetryCount = HasResolvedSoundtrack(resolvedMatch)
            ? 0
            : Math.Min(previousRetries + 1, 1000);
    }

    private async Task PersistUpdatedSoundtrackAsync(
        MediaServerSoundtrackItemDto row,
        MediaServerSoundtrackMatchDto resolvedMatch)
    {
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
            ContentHash = row.ContentHash,
            IsActive = row.IsActive,
            FirstSeenUtc = row.FirstSeenUtc,
            LastSeenUtc = DateTimeOffset.UtcNow,
            Soundtrack = resolvedMatch
        };

        await _cacheRepository.UpsertItemsAsync(new[] { updated }, CancellationToken.None);
    }

    private static bool ShouldResolveSoundtrackInBackground(MediaServerSoundtrackItemDto item)
    {
        if (item.Soundtrack?.Locked == true)
        {
            return false;
        }

        if ((item.Soundtrack?.RetryCount ?? 0) >= 12)
        {
            return false;
        }

        var kind = NormalizeText(item.Soundtrack?.Kind).ToLowerInvariant();
        if (kind.StartsWith(SpotifyKindPrefix, StringComparison.Ordinal))
        {
            var soundtrackTitle = NormalizeText(item.Soundtrack?.Title);
            if (string.IsNullOrWhiteSpace(soundtrackTitle))
            {
                return true;
            }

            return !IsSoundtrackCandidateCompatible(item.Title, soundtrackTitle, item.Year);
        }

        var deezerId = NormalizeText(item.Soundtrack?.DeezerId);
        return string.IsNullOrWhiteSpace(deezerId) || string.Equals(kind, MatchKindSearch, StringComparison.Ordinal);
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
            NormalizeMatchMetadata(row.Soundtrack);
            return;
        }
    }

    private static bool HasResolvedSoundtrack(MediaServerSoundtrackItemDto row)
    {
        if (IsMovieSingleTrackMatch(row.Category, row.Soundtrack))
        {
            return false;
        }

        return HasResolvedSoundtrack(row.Soundtrack);
    }

    private static bool HasResolvedSoundtrack(MediaServerSoundtrackMatchDto? match)
    {
        if (match == null)
        {
            return false;
        }

        var kind = NormalizeText(match.Kind).ToLowerInvariant();
        var deezerId = NormalizeText(match.DeezerId);
        if (!string.IsNullOrWhiteSpace(deezerId) && !string.Equals(kind, MatchKindSearch, StringComparison.Ordinal))
        {
            return true;
        }

        if (kind.StartsWith(SpotifyKindPrefix, StringComparison.Ordinal))
        {
            return !string.IsNullOrWhiteSpace(NormalizeText(match.Url));
        }

        return false;
    }

    private static bool IsMovieSingleTrackMatch(string? category, MediaServerSoundtrackMatchDto? match)
    {
        if (match == null)
        {
            return false;
        }

        var normalizedCategory = NormalizeText(category).ToLowerInvariant();
        if (!string.Equals(normalizedCategory, MediaServerSoundtrackConstants.MovieCategory, StringComparison.Ordinal))
        {
            return false;
        }

        var kind = NormalizeText(match.Kind).ToLowerInvariant();
        if (string.Equals(kind, MatchKindTrack, StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(kind, $"{SpotifyKindPrefix}{MatchKindTrack}", StringComparison.Ordinal);
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
        var normalizedSeasonId = NormalizeText(seasonId);
        var auth = await _platformAuthService.LoadAsync();
        var settings = await _store.LoadAsync(cancellationToken);
        var targets = ResolveTargetLibraries(
            settings,
            auth,
            MediaServerSoundtrackConstants.TvShowCategory,
            serverType,
            libraryId);
        var cached = await _cacheRepository.GetTvShowEpisodesAsync(serverType, libraryId, normalizedShowId, cancellationToken);
        if (targets.Count == 0)
        {
            return cached != null
                ? FilterCachedTvShowEpisodes(cached, normalizedSeasonId, episodeLimit)
                : new MediaServerTvShowEpisodesResponseDto();
        }

        var target = targets[0];
        var allShows = await FetchLibraryItemsAsync(auth, target, 0, 2000, cancellationToken);
        var show = allShows.FirstOrDefault(item => string.Equals(item.ItemId, normalizedShowId, StringComparison.OrdinalIgnoreCase));
        if (show is null)
        {
            return cached != null
                ? FilterCachedTvShowEpisodes(cached, normalizedSeasonId, episodeLimit)
                : new MediaServerTvShowEpisodesResponseDto
            {
                ServerType = NormalizeServerType(target.ServerType),
                ServerLabel = GetServerDisplayName(target.ServerType),
                LibraryId = target.LibraryId,
                LibraryName = target.LibraryName
            };
        }

        try
        {
            var fetched = await FetchTvEpisodesForShowAsync(auth, target, show, cancellationToken);
            var soundtrack = await ResolveSoundtrackAsync(show, cancellationToken);
            var fullResponse = BuildTvShowEpisodesResponse(target, show, fetched, soundtrack, null, null);
            await _cacheRepository.UpsertTvShowEpisodesAsync(fullResponse, cancellationToken);
            return BuildTvShowEpisodesResponse(target, show, fetched, soundtrack, normalizedSeasonId, episodeLimit);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (cached != null)
            {
                return FilterCachedTvShowEpisodes(cached, normalizedSeasonId, episodeLimit);
            }

            _logger.LogWarning(ex, "Failed loading media server TV show soundtrack episodes for {ShowId}.", normalizedShowId);
            return new MediaServerTvShowEpisodesResponseDto
            {
                ServerType = NormalizeServerType(target.ServerType),
                ServerLabel = GetServerDisplayName(target.ServerType),
                LibraryId = target.LibraryId,
                LibraryName = target.LibraryName,
                ShowId = show.ItemId,
                ShowTitle = show.Title,
                ShowImageUrl = show.ImageUrl,
                SelectedSeasonId = string.IsNullOrWhiteSpace(normalizedSeasonId) ? null : normalizedSeasonId
            };
        }
    }

    private async Task<List<MediaServerLibraryDescriptor>> DiscoverLibrariesAsync(PlatformAuthState auth, CancellationToken cancellationToken)
    {
        var discovered = new List<MediaServerLibraryDescriptor>();
        discovered.AddRange(await DiscoverPlexLibrariesAsync(auth.Plex, cancellationToken));
        discovered.AddRange(await DiscoverJellyfinLibrariesAsync(auth.Jellyfin, cancellationToken));
        return discovered;
    }

    private static MediaServerTvShowEpisodesResponseDto BuildTvShowEpisodesResponse(
        (string ServerType, string LibraryId, string LibraryName, string Category) target,
        MediaServerContentItem show,
        TvEpisodeFetchResult fetched,
        MediaServerSoundtrackMatchDto? soundtrack,
        string? normalizedSeasonId,
        int? episodeLimit)
    {
        var selectedSeasonId = NormalizeText(normalizedSeasonId);
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

        var filteredEpisodes = fetched.Episodes
            .Where(item => string.IsNullOrWhiteSpace(selectedSeasonId)
                || string.Equals(item.SeasonId, selectedSeasonId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.SeasonNumber ?? int.MaxValue)
            .ThenBy(item => item.EpisodeNumber ?? int.MaxValue)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var limitedEpisodes = episodeLimit.HasValue
            ? filteredEpisodes.Take(Math.Clamp(episodeLimit.Value, 1, 2000)).ToList()
            : filteredEpisodes;

        return new MediaServerTvShowEpisodesResponseDto
        {
            ServerType = NormalizeServerType(target.ServerType),
            ServerLabel = GetServerDisplayName(target.ServerType),
            LibraryId = target.LibraryId,
            LibraryName = target.LibraryName,
            ShowId = show.ItemId,
            ShowTitle = show.Title,
            ShowImageUrl = show.ImageUrl,
            SelectedSeasonId = string.IsNullOrWhiteSpace(selectedSeasonId) ? null : selectedSeasonId,
            TotalEpisodes = limitedEpisodes.Count,
            Seasons = seasons,
            Episodes = limitedEpisodes.Select(episode => new MediaServerTvShowEpisodeDto
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

    private static MediaServerTvShowEpisodesResponseDto FilterCachedTvShowEpisodes(
        MediaServerTvShowEpisodesResponseDto cached,
        string? normalizedSeasonId,
        int episodeLimit)
    {
        var selectedSeasonId = NormalizeText(normalizedSeasonId);
        var seasons = cached.Seasons
            .Select(season => new MediaServerTvShowSeasonDto
            {
                SeasonId = season.SeasonId,
                Title = season.Title,
                SeasonNumber = season.SeasonNumber,
                ImageUrl = season.ImageUrl,
                EpisodeCount = season.EpisodeCount
            })
            .OrderBy(season => season.SeasonNumber ?? int.MaxValue)
            .ThenBy(season => season.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var filteredEpisodes = cached.Episodes
            .Where(item => string.IsNullOrWhiteSpace(selectedSeasonId)
                || string.Equals(item.SeasonId, selectedSeasonId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.SeasonNumber ?? int.MaxValue)
            .ThenBy(item => item.EpisodeNumber ?? int.MaxValue)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(episodeLimit, 1, 2000))
            .Select(episode => new MediaServerTvShowEpisodeDto
            {
                EpisodeId = episode.EpisodeId,
                SeasonId = episode.SeasonId,
                SeasonTitle = episode.SeasonTitle,
                SeasonNumber = episode.SeasonNumber,
                EpisodeNumber = episode.EpisodeNumber,
                Title = episode.Title,
                Year = episode.Year,
                ImageUrl = episode.ImageUrl,
                Soundtrack = episode.Soundtrack
            })
            .ToList();

        return new MediaServerTvShowEpisodesResponseDto
        {
            ServerType = NormalizeServerType(cached.ServerType),
            ServerLabel = cached.ServerLabel,
            LibraryId = cached.LibraryId,
            LibraryName = cached.LibraryName,
            ShowId = cached.ShowId,
            ShowTitle = cached.ShowTitle,
            ShowImageUrl = cached.ShowImageUrl,
            SelectedSeasonId = string.IsNullOrWhiteSpace(selectedSeasonId) ? null : selectedSeasonId,
            TotalEpisodes = filteredEpisodes.Count,
            Seasons = seasons,
            Episodes = filteredEpisodes
        };
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
            changed |= MergeServerGroup(settings, group, nowUtc);
        }

        return changed;
    }

    private static bool MergeServerGroup(
        MediaServerSoundtrackSettings settings,
        IGrouping<string, MediaServerLibraryDescriptor> group,
        DateTimeOffset nowUtc)
    {
        var serverType = NormalizeServerType(group.Key);
        if (string.IsNullOrWhiteSpace(serverType))
        {
            return false;
        }

        var changed = false;
        var server = GetOrCreateServer(settings, serverType);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in group)
        {
            changed |= MergeDiscoveredLibrary(server, library, seenIds, nowUtc);
        }

        changed |= MarkStaleLibraries(server, seenIds);
        return changed;
    }

    private static bool MergeDiscoveredLibrary(
        MediaServerSoundtrackServerSettings server,
        MediaServerLibraryDescriptor library,
        HashSet<string> seenIds,
        DateTimeOffset nowUtc)
    {
        var libraryId = NormalizeText(library.LibraryId);
        if (string.IsNullOrWhiteSpace(libraryId))
        {
            return false;
        }

        seenIds.Add(libraryId);
        if (!server.Libraries.TryGetValue(libraryId, out var existing))
        {
            server.Libraries[libraryId] = new MediaServerSoundtrackLibrarySettings
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
            return true;
        }

        return UpdateExistingDiscoveredLibrary(existing, library, nowUtc);
    }

    private static bool UpdateExistingDiscoveredLibrary(
        MediaServerSoundtrackLibrarySettings existing,
        MediaServerLibraryDescriptor library,
        DateTimeOffset nowUtc)
    {
        var changed = false;
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

        return changed;
    }

    private static bool MarkStaleLibraries(MediaServerSoundtrackServerSettings server, HashSet<string> seenIds)
    {
        var changed = false;
        foreach (var staleLibrary in server.Libraries.Values.Where(entry => !seenIds.Contains(entry.LibraryId)))
        {
            if (staleLibrary.LastSeenUtc != null)
            {
                staleLibrary.LastSeenUtc = null;
                changed = true;
            }
        }

        return changed;
    }

    private static MediaServerSoundtrackConfigurationDto BuildConfigurationDto(
        MediaServerSoundtrackSettings settings,
        PlatformAuthState auth,
        IReadOnlyList<MediaServerLibraryDescriptor> discovered)
    {
        var discoveredLookup = BuildDiscoveredLookup(discovered);
        var servers = GetSupportedServerTypes()
            .Select(serverType => BuildServerConfigurationDto(serverType, settings, auth, discoveredLookup))
            .ToList();

        return new MediaServerSoundtrackConfigurationDto
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Servers = servers
        };
    }

    private static Dictionary<string, Dictionary<string, MediaServerLibraryDescriptor>> BuildDiscoveredLookup(
        IReadOnlyList<MediaServerLibraryDescriptor> discovered)
    {
        return discovered
            .GroupBy(item => NormalizeServerType(item.ServerType), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.ToDictionary(
                    item => NormalizeText(item.LibraryId),
                    item => item,
                    StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
    }

    private static string[] GetSupportedServerTypes()
    {
        return
        [
            MediaServerSoundtrackConstants.PlexServer,
            MediaServerSoundtrackConstants.JellyfinServer
        ];
    }

    private static MediaServerSoundtrackServerDto BuildServerConfigurationDto(
        string serverType,
        MediaServerSoundtrackSettings settings,
        PlatformAuthState auth,
        Dictionary<string, Dictionary<string, MediaServerLibraryDescriptor>> discoveredLookup)
    {
        var serverSettings = settings.Servers.TryGetValue(serverType, out var found)
            ? found
            : new MediaServerSoundtrackServerSettings();
        var discoveredById = discoveredLookup.TryGetValue(serverType, out var lookup)
            ? lookup
            : new Dictionary<string, MediaServerLibraryDescriptor>(StringComparer.OrdinalIgnoreCase);
        var libraries = BuildServerLibraryDtos(serverSettings, discoveredById);

        return new MediaServerSoundtrackServerDto
        {
            ServerType = serverType,
            DisplayName = GetServerDisplayName(serverType),
            Connected = IsServerConnected(serverType, auth),
            AutoIncludeNewLibraries = serverSettings.AutoIncludeNewLibraries,
            Libraries = libraries
        };
    }

    private static List<MediaServerSoundtrackLibraryDto> BuildServerLibraryDtos(
        MediaServerSoundtrackServerSettings serverSettings,
        IReadOnlyDictionary<string, MediaServerLibraryDescriptor> discoveredById)
    {
        return BuildLibraryIds(serverSettings, discoveredById)
            .Select(id => BuildLibraryDtoForConfiguration(id, serverSettings, discoveredById))
            .Where(static dto => dto != null)
            .Select(static dto => dto!)
            .ToList();
    }

    private static IEnumerable<string> BuildLibraryIds(
        MediaServerSoundtrackServerSettings serverSettings,
        IReadOnlyDictionary<string, MediaServerLibraryDescriptor> discoveredById)
    {
        var libraryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in serverSettings.Libraries.Keys)
        {
            libraryIds.Add(NormalizeText(id));
        }

        foreach (var id in discoveredById.Keys)
        {
            libraryIds.Add(NormalizeText(id));
        }

        return libraryIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase);
    }

    private static MediaServerSoundtrackLibraryDto? BuildLibraryDtoForConfiguration(
        string id,
        MediaServerSoundtrackServerSettings serverSettings,
        IReadOnlyDictionary<string, MediaServerLibraryDescriptor> discoveredById)
    {
        serverSettings.Libraries.TryGetValue(id, out var saved);
        discoveredById.TryGetValue(id, out var live);
        if (live == null && saved?.LastSeenUtc == null)
        {
            return null;
        }

        var category = NormalizeCategory(saved?.Category ?? live?.Category);
        var name = ResolveLibraryDisplayName(saved, live, id);
        return new MediaServerSoundtrackLibraryDto
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
        };
    }

    private static string ResolveLibraryDisplayName(
        MediaServerSoundtrackLibrarySettings? saved,
        MediaServerLibraryDescriptor? live,
        string fallbackId)
    {
        var name = NormalizeText(saved?.Name);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = NormalizeText(live?.Name);
        }

        return string.IsNullOrWhiteSpace(name) ? fallbackId : name;
    }

    private static List<(string ServerType, string LibraryId, string LibraryName, string Category)> ResolveTargetLibraries(
        MediaServerSoundtrackSettings settings,
        PlatformAuthState auth,
        string category,
        string? serverType,
        string? libraryId)
    {
        var normalizedServer = NormalizeServerType(serverType);
        var normalizedLibraryId = NormalizeText(libraryId);

        var targets = new List<(string ServerType, string LibraryId, string LibraryName, string Category)>();
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

                targets.Add((storedServerType, storedLibraryId, storedLibrary.Name, NormalizeCategory(storedLibrary.Category)));
            }
        }

        return targets;
    }

    private async Task<List<MediaServerContentItem>> FetchLibraryItemsAsync(
        PlatformAuthState auth,
        (string ServerType, string LibraryId, string LibraryName, string Category) target,
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
        (string ServerType, string LibraryId, string LibraryName, string Category) target,
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

        return CreateEpisodeFetchResultSeed(show);
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
            return CreateEpisodeFetchResultSeed(show);
        }

        var result = CreateEpisodeFetchResultSeed(show);

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
            return CreateEpisodeFetchResultSeed(show);
        }

        var userId = NormalizeText(jellyfin!.UserId);
        if (string.IsNullOrWhiteSpace(userId))
        {
            var currentUser = await _jellyfinApiClient.GetCurrentUserAsync(jellyfin.Url!, jellyfin.ApiKey!, cancellationToken);
            userId = NormalizeText(currentUser?.Id);
        }

        var result = CreateEpisodeFetchResultSeed(show);

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
            NormalizeMatchMetadata(resolved);
            _soundtrackCache[cacheKey] = (DateTimeOffset.UtcNow, resolved);
            return resolved;
        }
        finally
        {
            _soundtrackWarmupGate.Release();
        }
    }

    private async Task<MediaServerSoundtrackMatchDto> ResolveSoundtrackDirectAsync(
        MediaServerContentItem item,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? queryOverride = null)
    {
        var queries = queryOverride?
            .Select(query => NormalizeText(query))
            .Where(query => !string.IsNullOrWhiteSpace(query))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ?? Array.Empty<string>();
        if (queries.Length == 0)
        {
            queries = BuildSoundtrackQueries(item.Title, item.Year);
        }
        var primaryQuery = queries[0];
        var defaultMatch = CreateFallbackSearchMatch(primaryQuery);

        try
        {
            var bestMatch = defaultMatch;
            var spotifyMatch = await TryResolveSpotifySoundtrackMatchAsync(item, queries, cancellationToken);
            bestMatch = SelectHigherScore(bestMatch, spotifyMatch);
            if (bestMatch.Score < 65)
            {
                var curatedSpotify = await TryResolveMusicBrainzCuratedMatchAsync(item, queries, cancellationToken);
                bestMatch = SelectHigherScore(bestMatch, curatedSpotify);
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
            best = await TryResolveSpotifyAlbumsForQueryAsync(item, query, best, cancellationToken);
            if (ShouldReturnHighConfidence(best))
            {
                return best;
            }
        }

        if (ShouldTrySpotifyWebFallback(best))
        {
            var webSearchMatch = await TryResolveSpotifyWebSearchMatchAsync(item, queries, cancellationToken);
            best = SelectHigherScoreNullable(best, webSearchMatch);
        }

        return best;
    }

    private async Task<MediaServerSoundtrackMatchDto?> TryResolveSpotifyAlbumsForQueryAsync(
        MediaServerContentItem item,
        string query,
        MediaServerSoundtrackMatchDto? currentBest,
        CancellationToken cancellationToken)
    {
        var spotifyAlbums = await SafeSearchSpotifyAlbumsAsync(item.Title, query, cancellationToken);
        if (spotifyAlbums?.Items == null || spotifyAlbums.Items.Count == 0)
        {
            return currentBest;
        }

        var albumCandidates = spotifyAlbums.Items
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Id) && !string.IsNullOrWhiteSpace(candidate.Name))
            .Where(candidate => IsSoundtrackCandidateCompatible(item.Title, candidate.Name, item.Year))
            .OrderByDescending(candidate => ComputeMatchScore(item.Title, candidate.Name, item.Year))
            .Take(12);
        foreach (var candidate in albumCandidates)
        {
            var spotifyDirectMatch = BuildSpotifyDirectSoundtrackMatch(item.Title, item.Year, candidate);
            currentBest = SelectHigherScoreNullable(currentBest, spotifyDirectMatch);
            if (ShouldReturnHighConfidence(currentBest))
            {
                return currentBest;
            }
        }

        return currentBest;
    }

    private async Task<SpotifySearchTypeResponse?> SafeSearchSpotifyAlbumsAsync(
        string itemTitle,
        string query,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _spotifySearchService.SearchByTypeAsync(query, MatchKindAlbum, 40, 0, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Spotify soundtrack candidate search failed for {Title}", itemTitle);
            return null;
        }
    }

    private static bool ShouldTrySpotifyWebFallback(MediaServerSoundtrackMatchDto? best)
        => best == null || best.Score < 60;

    private static MediaServerSoundtrackMatchDto? BuildSpotifyDirectSoundtrackMatch(
        string mediaTitle,
        int? mediaYear,
        SpotifySearchItem candidate)
    {
        var spotifyUrl = BuildSpotifyAlbumUrl(candidate);
        var title = NormalizeText(candidate.Name);
        if (string.IsNullOrWhiteSpace(spotifyUrl) || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }
        if (!IsSoundtrackCandidateCompatible(mediaTitle, title, mediaYear))
        {
            return null;
        }

        var subtitle = ExtractSpotifyItemArtist(candidate.Subtitle);
        var score = Math.Max(ComputeMatchScore(mediaTitle, title, mediaYear) + 6, 26);
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

    private sealed class SpotifyWebSearchCandidate
    {
        public string Type { get; init; } = "track";

        public string Id { get; init; } = string.Empty;

        public string Title { get; init; } = string.Empty;

        public string Url { get; init; } = string.Empty;
    }

    private async Task<MediaServerSoundtrackMatchDto?> TryResolveSpotifyWebSearchMatchAsync(
        MediaServerContentItem item,
        IReadOnlyList<string> queries,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(20);
        MediaServerSoundtrackMatchDto? best = null;

        foreach (var query in queries.Take(5))
        {
            var markdown = await FetchSpotifyWebSearchMarkdownAsync(client, query, cancellationToken);
            if (string.IsNullOrWhiteSpace(markdown))
            {
                continue;
            }

            best = MapSpotifyWebCandidates(item, markdown, best);
            if (ShouldReturnHighConfidence(best))
            {
                return best;
            }
        }

        return best;
    }

    private async Task<string?> FetchSpotifyWebSearchMarkdownAsync(
        HttpClient client,
        string query,
        CancellationToken cancellationToken)
    {
        var encodedQuery = Uri.EscapeDataString(query);
        var bridgeUrl = $"https://r.jina.ai/http://open.spotify.com/search/{encodedQuery}";
        try
        {
            return await client.GetStringAsync(bridgeUrl, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Spotify web-search bridge failed for soundtrack query {Query}", query);
            return null;
        }
    }

    private static MediaServerSoundtrackMatchDto? MapSpotifyWebCandidates(
        MediaServerContentItem item,
        string markdown,
        MediaServerSoundtrackMatchDto? currentBest)
    {
        foreach (var candidate in ParseSpotifyWebSearchCandidates(markdown))
        {
            if (!IsSpotifyWebCandidateAllowedForItem(item, candidate))
            {
                continue;
            }

            if (!IsSoundtrackCandidateCompatible(item.Title, candidate.Title, item.Year))
            {
                continue;
            }

            var score = ComputeSpotifyWebCandidateScore(item.Title, item.Year, candidate);
            if (score < 35)
            {
                continue;
            }

            var model = new MediaServerSoundtrackMatchDto
            {
                Kind = $"spotify_{candidate.Type}",
                DeezerId = null,
                Title = candidate.Title,
                Subtitle = null,
                Url = candidate.Url,
                CoverUrl = null,
                Score = score
            };

            currentBest = SelectHigherScoreNullable(currentBest, model);
            if (ShouldReturnHighConfidence(currentBest))
            {
                return currentBest;
            }
        }

        return currentBest;
    }

    private static bool IsSpotifyWebCandidateAllowedForItem(MediaServerContentItem item, SpotifyWebSearchCandidate candidate)
    {
        var candidateType = NormalizeText(candidate.Type).ToLowerInvariant();
        if (candidateType is not (MatchKindAlbum or MatchKindPlaylist or MatchKindTrack))
        {
            return false;
        }

        var category = NormalizeText(item.Category).ToLowerInvariant();
        if (string.Equals(category, MediaServerSoundtrackConstants.MovieCategory, StringComparison.Ordinal)
            && string.Equals(candidateType, MatchKindTrack, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static List<SpotifyWebSearchCandidate> ParseSpotifyWebSearchCandidates(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return new List<SpotifyWebSearchCandidate>();
        }

        var parsed = new List<SpotifyWebSearchCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in SpotifyMarkdownLinkRegex().Matches(markdown))
        {
            if (!match.Success)
            {
                continue;
            }

            var type = NormalizeText(match.Groups["type"].Value).ToLowerInvariant();
            var id = NormalizeText(match.Groups["id"].Value);
            var title = NormalizeText(match.Groups["title"].Value);
            var url = NormalizeText(match.Groups["url"].Value);
            if (string.IsNullOrWhiteSpace(type)
                || string.IsNullOrWhiteSpace(id)
                || string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            if (!(type == MatchKindAlbum || type == MatchKindPlaylist || type == MatchKindTrack))
            {
                continue;
            }

            var key = $"{type}:{id}";
            if (!seen.Add(key))
            {
                continue;
            }

            parsed.Add(new SpotifyWebSearchCandidate
            {
                Type = type,
                Id = id,
                Title = string.IsNullOrWhiteSpace(title) ? "Spotify soundtrack" : title,
                Url = url
            });
        }

        return parsed;
    }

    private static int ComputeSpotifyWebCandidateScore(string mediaTitle, int? mediaYear, SpotifyWebSearchCandidate candidate)
    {
        var score = ComputeMatchScore(mediaTitle, candidate.Title, mediaYear);
        if (candidate.Type == MatchKindAlbum)
        {
            score += 8;
        }
        else if (candidate.Type == MatchKindPlaylist)
        {
            score += 6;
        }
        else
        {
            score += 2;
        }

        if (LooksLikeSoundtrackTitle(candidate.Title))
        {
            score += 8;
        }

        return Math.Clamp(score, 1, 100);
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
            var response = await SafeSearchMusicBrainzAsync(item.Title, query, cancellationToken);
            curatedCandidates.AddRange(ExtractMusicBrainzCuratedCandidates(item, response));
        }

        var dedupedCandidates = DeduplicateCuratedCandidates(curatedCandidates);

        MediaServerSoundtrackMatchDto? best = null;
        foreach (var candidate in dedupedCandidates)
        {
            var mapped = await TryResolveCuratedCandidateViaSpotifyAsync(
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

    private async Task<MusicBrainzRecordingSearchResults?> SafeSearchMusicBrainzAsync(
        string itemTitle,
        string query,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _musicBrainzClient.SearchAsync(query, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "MusicBrainz soundtrack candidate search failed for {Title}", itemTitle);
            return null;
        }
    }

    private static IEnumerable<MusicBrainzSoundtrackCandidate> ExtractMusicBrainzCuratedCandidates(
        MediaServerContentItem item,
        MusicBrainzRecordingSearchResults? response)
    {
        foreach (var recording in response?.Recordings ?? Enumerable.Empty<DeezSpoTag.Web.Services.AutoTag.Recording>())
        {
            foreach (var candidate in BuildCuratedCandidatesFromRecording(item, recording))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<MusicBrainzSoundtrackCandidate> BuildCuratedCandidatesFromRecording(
        MediaServerContentItem item,
        DeezSpoTag.Web.Services.AutoTag.Recording recording)
    {
        foreach (var release in recording.Releases ?? Enumerable.Empty<MusicBrainzRelease>())
        {
            var releaseTitle = NormalizeText(release.Title);
            if (string.IsNullOrWhiteSpace(releaseTitle))
            {
                continue;
            }

            var score = ComputeMusicBrainzCuratedScore(item.Title, item.Year, recording.Title, release);
            if (score < 45)
            {
                continue;
            }

            var artistHint = BuildArtistText(release.ArtistCredit) ?? BuildArtistText(recording.ArtistCredit);
            yield return new MusicBrainzSoundtrackCandidate
            {
                Title = releaseTitle,
                ArtistHint = artistHint,
                Score = score
            };

            var groupTitle = NormalizeText(release.ReleaseGroup?.Title);
            if (string.IsNullOrWhiteSpace(groupTitle)
                || string.Equals(groupTitle, releaseTitle, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return new MusicBrainzSoundtrackCandidate
            {
                Title = groupTitle,
                ArtistHint = artistHint,
                Score = Math.Max(40, score - 3)
            };
        }
    }

    private static List<MusicBrainzSoundtrackCandidate> DeduplicateCuratedCandidates(
        IEnumerable<MusicBrainzSoundtrackCandidate> candidates)
    {
        return candidates
            .GroupBy(candidate => candidate.Title, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(candidate => candidate.Score).First())
            .OrderByDescending(candidate => candidate.Score)
            .Take(8)
            .ToList();
    }

    private async Task<MediaServerSoundtrackMatchDto?> TryResolveCuratedCandidateViaSpotifyAsync(
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
            var queryCandidates = BuildCuratedSpotifyQueries(normalizedTitle, artistHint);

            MediaServerSoundtrackMatchDto? best = null;
            foreach (var query in queryCandidates)
            {
                var spotifyAlbums = await SafeSearchSpotifyCuratedAlbumsAsync(query, cancellationToken);
                var resolved = MapCuratedSpotifyAlbumCandidates(
                    item,
                    normalizedTitle,
                    artistHint,
                    scoreBonus,
                    spotifyAlbums?.Items);
                best = SelectHigherScoreNullable(best, resolved);
                if (ShouldReturnHighConfidence(best))
                {
                    return best;
                }
            }

            return best;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Spotify mapping from curated soundtrack candidate failed for {Title}", candidateTitle);
            return null;
        }
    }

    private static string[] BuildCuratedSpotifyQueries(string normalizedTitle, string? artistHint)
    {
        return new string?[]
        {
            $"{normalizedTitle} {SoundtrackToken}",
            normalizedTitle,
            string.IsNullOrWhiteSpace(artistHint) ? null : $"{normalizedTitle} {artistHint}"
        }
            .Where(static q => !string.IsNullOrWhiteSpace(q))
            .Select(static q => q!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<SpotifySearchTypeResponse?> SafeSearchSpotifyCuratedAlbumsAsync(
        string query,
        CancellationToken cancellationToken)
    {
        return await _spotifySearchService.SearchByTypeAsync(
            query,
            MatchKindAlbum,
            limit: 30,
            offset: 0,
            cancellationToken);
    }

    private static MediaServerSoundtrackMatchDto? MapCuratedSpotifyAlbumCandidates(
        MediaServerContentItem item,
        string normalizedTitle,
        string? artistHint,
        int scoreBonus,
        IReadOnlyList<SpotifySearchItem>? candidates)
    {
        MediaServerSoundtrackMatchDto? best = null;
        foreach (var candidate in candidates ?? Array.Empty<SpotifySearchItem>())
        {
            var model = BuildCuratedSpotifyModel(item, normalizedTitle, artistHint, scoreBonus, candidate);
            best = SelectHigherScoreNullable(best, model);
            if (ShouldReturnHighConfidence(best))
            {
                return best;
            }
        }

        return best;
    }

    private static MediaServerSoundtrackMatchDto? BuildCuratedSpotifyModel(
        MediaServerContentItem item,
        string normalizedTitle,
        string? artistHint,
        int scoreBonus,
        SpotifySearchItem candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.Id) || string.IsNullOrWhiteSpace(candidate.Name))
        {
            return null;
        }

        if (!IsSoundtrackCandidateCompatible(normalizedTitle, candidate.Name, null)
            && !IsSoundtrackCandidateCompatible(item.Title, candidate.Name, item.Year))
        {
            return null;
        }

        var spotifyUrl = BuildSpotifyAlbumUrl(candidate);
        if (string.IsNullOrWhiteSpace(spotifyUrl))
        {
            return null;
        }

        var subtitle = ExtractSpotifyItemArtist(candidate.Subtitle);
        var computed = Math.Max(
            ComputeMatchScore(item.Title, candidate.Name, item.Year),
            ComputeMatchScore(normalizedTitle, candidate.Name, null));
        computed += Math.Max(0, scoreBonus);
        computed += ComputeArtistHintBoost(artistHint, subtitle);
        if (LooksLikeSoundtrackTitle(candidate.Name))
        {
            computed += 6;
        }

        return new MediaServerSoundtrackMatchDto
        {
            Kind = "spotify_album",
            DeezerId = null,
            Title = NormalizeText(candidate.Name),
            Subtitle = string.IsNullOrWhiteSpace(subtitle) ? null : subtitle,
            Url = spotifyUrl,
            CoverUrl = candidate.ImageUrl,
            Score = Math.Clamp(computed, 1, 100)
        };
    }

    private static int ComputeMusicBrainzCuratedScore(string mediaTitle, int? mediaYear, string recordingTitle, MusicBrainzRelease release)
    {
        var releaseTitle = NormalizeText(release.Title);
        var score = ComputeMatchScore(mediaTitle, releaseTitle, mediaYear);
        score = Math.Max(score, ComputeMatchScore(mediaTitle, recordingTitle, mediaYear));

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

    private static TvEpisodeFetchResult CreateEpisodeFetchResultSeed(MediaServerContentItem show)
        => new()
        {
            ShowId = show.ItemId,
            ShowTitle = show.Title,
            ShowImageUrl = show.ImageUrl
        };

    private static bool ShouldReturnHighConfidence(MediaServerSoundtrackMatchDto? match)
        => match is { Score: >= 70 };

    private static int ComputeMatchScore(string targetTitle, string candidateTitle, int? targetYear = null)
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

        score += ComputeSequelAndSubtitleScoreAdjustment(targetTitle, candidateTitle);
        score += ComputeYearScoreAdjustment(targetYear, candidateTitle);

        return Math.Clamp(score, 1, 100);
    }

    private static bool IsSoundtrackCandidateCompatible(string mediaTitle, string candidateTitle, int? mediaYear)
    {
        if (IsSoundtrackCandidateCompatibleCore(mediaTitle, candidateTitle, mediaYear))
        {
            return true;
        }

        var canonicalMediaTitle = SanitizeSoundtrackTitle(mediaTitle);
        if (!string.Equals(canonicalMediaTitle, mediaTitle, StringComparison.OrdinalIgnoreCase)
            && IsSoundtrackCandidateCompatibleCore(canonicalMediaTitle, candidateTitle, mediaYear))
        {
            return true;
        }

        var canonicalCandidateTitle = SanitizeSoundtrackTitle(candidateTitle);
        if (!string.Equals(canonicalCandidateTitle, candidateTitle, StringComparison.OrdinalIgnoreCase)
            && IsSoundtrackCandidateCompatibleCore(mediaTitle, canonicalCandidateTitle, mediaYear))
        {
            return true;
        }

        return !string.Equals(canonicalMediaTitle, mediaTitle, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(canonicalCandidateTitle, candidateTitle, StringComparison.OrdinalIgnoreCase)
            && IsSoundtrackCandidateCompatibleCore(canonicalMediaTitle, canonicalCandidateTitle, mediaYear);
    }

    private static bool IsSoundtrackCandidateCompatibleCore(string mediaTitle, string candidateTitle, int? mediaYear)
    {
        var normalizedMedia = NormalizeMatchingText(mediaTitle);
        var normalizedCandidate = NormalizeMatchingText(candidateTitle);
        if (string.IsNullOrWhiteSpace(normalizedMedia) || string.IsNullOrWhiteSpace(normalizedCandidate))
        {
            return false;
        }

        var mediaTokens = ExtractCoreTokens(normalizedMedia);
        var candidateTokens = ExtractCoreTokens(normalizedCandidate);
        if (mediaTokens.Count == 0 || candidateTokens.Count == 0)
        {
            return false;
        }

        var targetContains = normalizedCandidate.Contains(normalizedMedia, StringComparison.OrdinalIgnoreCase);
        var candidateContains = normalizedMedia.Contains(normalizedCandidate, StringComparison.OrdinalIgnoreCase);
        var shared = mediaTokens.Count(token => candidateTokens.Contains(token));
        var ratio = (double)shared / mediaTokens.Count;
        var soundtrackLikeCandidate = LooksLikeSoundtrackTitle(candidateTitle);
        var nonOverlappingMediaTokens = mediaTokens
            .Where(token => !candidateTokens.Contains(token))
            .ToArray();
        var editionOnlyDelta = nonOverlappingMediaTokens.Length > 0
            && nonOverlappingMediaTokens.All(IsEditionDecoratorToken);

        if (candidateContains && mediaTokens.Count >= 3 && !soundtrackLikeCandidate && !editionOnlyDelta)
        {
            return false;
        }

        if (!soundtrackLikeCandidate && mediaTokens.Count >= 3 && ratio < 0.75 && !editionOnlyDelta)
        {
            return false;
        }

        if (!(targetContains || candidateContains) && shared < 2 && ratio < 0.34)
        {
            return false;
        }

        if (!IsSequelCompatible(mediaTitle, candidateTitle))
        {
            return false;
        }

        return IsYearCompatible(mediaYear, candidateTitle);
    }

    private static bool IsEditionDecoratorToken(string token)
        => EditionDecoratorTokens.Contains(token);

    private static int ComputeSequelAndSubtitleScoreAdjustment(string mediaTitle, string candidateTitle)
    {
        var targetSequel = ExtractSequelNumber(mediaTitle);
        var candidateSequel = ExtractSequelNumber(candidateTitle);
        if (targetSequel.HasValue)
        {
            if (candidateSequel.HasValue)
            {
                return candidateSequel.Value == targetSequel.Value ? 10 : -45;
            }

            return HasSubtitleAnchorOverlap(mediaTitle, candidateTitle) ? 5 : -32;
        }

        if (candidateSequel.HasValue)
        {
            return -18;
        }

        return 0;
    }

    private static int ComputeYearScoreAdjustment(int? targetYear, string candidateTitle)
    {
        if (!targetYear.HasValue || targetYear.Value < 1900)
        {
            return 0;
        }

        var years = ExtractTitleYears(candidateTitle);
        if (years.Count == 0)
        {
            return 0;
        }

        var smallestDiff = years
            .Select(year => Math.Abs(year - targetYear.Value))
            .DefaultIfEmpty(int.MaxValue)
            .Min();

        if (smallestDiff == 0)
        {
            return 10;
        }

        if (smallestDiff == 1)
        {
            return 4;
        }

        if (smallestDiff <= 3)
        {
            return -6;
        }

        return -14;
    }

    private static bool IsYearCompatible(int? targetYear, string candidateTitle)
    {
        if (!targetYear.HasValue || targetYear.Value < 1900)
        {
            return true;
        }

        var years = ExtractTitleYears(candidateTitle);
        if (years.Count == 0)
        {
            return true;
        }

        return years.Any(year => Math.Abs(year - targetYear.Value) <= 3);
    }

    private static bool IsSequelCompatible(string mediaTitle, string candidateTitle)
    {
        var targetSequel = ExtractSequelNumber(mediaTitle);
        if (!targetSequel.HasValue)
        {
            return true;
        }

        var candidateSequel = ExtractSequelNumber(candidateTitle);
        if (candidateSequel.HasValue)
        {
            return candidateSequel.Value == targetSequel.Value;
        }

        return HasSubtitleAnchorOverlap(mediaTitle, candidateTitle);
    }

    private static bool HasSubtitleAnchorOverlap(string mediaTitle, string candidateTitle)
    {
        var anchorTokens = ExtractSubtitleAnchorTokens(mediaTitle);
        if (anchorTokens.Count == 0)
        {
            return false;
        }

        var candidateTokens = ExtractCoreTokens(NormalizeMatchingText(candidateTitle));
        if (candidateTokens.Count == 0)
        {
            return false;
        }

        return anchorTokens.Any(candidateTokens.Contains);
    }

    private static HashSet<string> ExtractSubtitleAnchorTokens(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var colonIndex = title.IndexOf(':');
        if (colonIndex < 0 || colonIndex >= title.Length - 1)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var subtitle = title[(colonIndex + 1)..];
        return ExtractCoreTokens(NormalizeMatchingText(subtitle))
            .Where(token => token.Length >= 4)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static int? ExtractSequelNumber(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var normalized = title.Trim();

        var keywordMatch = SequelKeywordRegex().Match(normalized);
        if (keywordMatch.Success && int.TryParse(keywordMatch.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var keywordNumber))
        {
            return keywordNumber;
        }

        var beforeColonMatch = SequelBeforeColonRegex().Match(normalized);
        if (beforeColonMatch.Success
            && int.TryParse(beforeColonMatch.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var beforeColonNumber))
        {
            return beforeColonNumber;
        }

        var endNumberMatch = SequelAtEndRegex().Match(normalized);
        if (endNumberMatch.Success
            && int.TryParse(endNumberMatch.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var trailingNumber))
        {
            return trailingNumber;
        }

        var romanMatch = SequelRomanRegex().Match(normalized);
        if (!romanMatch.Success)
        {
            return null;
        }

        return romanMatch.Groups["n"].Value.ToUpperInvariant() switch
        {
            "II" => 2,
            "III" => 3,
            "IV" => 4,
            "V" => 5,
            "VI" => 6,
            "VII" => 7,
            "VIII" => 8,
            "IX" => 9,
            "X" => 10,
            "XI" => 11,
            "XII" => 12,
            _ => null
        };
    }

    private static HashSet<int> ExtractTitleYears(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return new HashSet<int>();
        }

        return TitleYearRegex()
            .Matches(title)
            .Select(static match => match.Value)
            .Select(static value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0)
            .Where(static year => year >= 1900)
            .ToHashSet();
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
            Kind = MatchKindSearch,
            DeezerId = null,
            Title = "Search on Spotify",
            Subtitle = null,
            Url = BuildSpotifySearchUrl(query),
            CoverUrl = null,
            Score = 1,
            Provider = MatchKindSearch,
            Reason = "pending_match",
            Locked = false,
            RetryCount = 0,
            ResolvedAtUtc = null
        };

    private static string BuildSpotifySearchUrl(string query)
        => $"https://open.spotify.com/search/{Uri.EscapeDataString(query)}";

    private static string[] BuildSoundtrackQueries(string title, int? year = null)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return new[] { SoundtrackToken };
        }

        var normalizedTitle = title.Trim();
        var sanitizedTitle = SanitizeSoundtrackTitle(normalizedTitle);
        var romanizedTitle = RomanizeSimpleSequelNumbers(normalizedTitle);
        var prefixBeforeColon = normalizedTitle.Split(':', 2)[0].Trim();
        var queries = new List<string>
        {
            $"{normalizedTitle} {SoundtrackToken}",
            $"{normalizedTitle} original {SoundtrackToken}",
            $"{normalizedTitle} ost",
            normalizedTitle
        };

        if (year.HasValue && year.Value > 0)
        {
            queries.Add($"{normalizedTitle} {year.Value} {SoundtrackToken}");
            queries.Add($"{normalizedTitle} {year.Value} ost");
        }

        if (!string.Equals(sanitizedTitle, normalizedTitle, StringComparison.OrdinalIgnoreCase))
        {
            queries.Add($"{sanitizedTitle} {SoundtrackToken}");
            queries.Add($"{sanitizedTitle} original {SoundtrackToken}");
            queries.Add($"{sanitizedTitle} ost");
            queries.Add(sanitizedTitle);
        }

        if (!string.Equals(romanizedTitle, normalizedTitle, StringComparison.OrdinalIgnoreCase))
        {
            queries.Add($"{romanizedTitle} {SoundtrackToken}");
            queries.Add($"{romanizedTitle} original {SoundtrackToken}");
            queries.Add($"{romanizedTitle} ost");
            queries.Add(romanizedTitle);
        }

        if (!string.IsNullOrWhiteSpace(prefixBeforeColon)
            && !string.Equals(prefixBeforeColon, normalizedTitle, StringComparison.OrdinalIgnoreCase))
        {
            queries.Add($"{prefixBeforeColon} {SoundtrackToken}");
            queries.Add(prefixBeforeColon);
        }

        return queries
            .Select(query => query.Trim())
            .Where(query => !string.IsNullOrWhiteSpace(query))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string RomanizeSimpleSequelNumbers(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        return SimpleSequelNumberRegex().Replace(
            title,
            static match =>
            {
                var value = match.Groups["n"].Value;
                return value switch
                {
                    "2" => "II",
                    "3" => "III",
                    "4" => "IV",
                    "5" => "V",
                    "6" => "VI",
                    "7" => "VII",
                    "8" => "VIII",
                    "9" => "IX",
                    "10" => "X",
                    _ => value
                };
            });
    }

    private static string SanitizeSoundtrackTitle(string title)
    {
        var sanitized = TitleBracketNoiseRegex().Replace(title, " ");
        foreach (var token in SoundtrackNoiseTokens)
        {
            var tokenRegex = new Regex(
                $@"\b{Regex.Escape(token)}\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                RegexDynamicTimeout);
            sanitized = tokenRegex.Replace(sanitized, " ");
        }

        sanitized = TitleWhitespaceRegex().Replace(sanitized, " ").Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? title.Trim() : sanitized;
    }

    [GeneratedRegex(@"\[[^\]]*\]|\([^\)]*\)|\{[^\}]*\}", RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex TitleBracketNoiseRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex TitleWhitespaceRegex();

    [GeneratedRegex(SpotifyMarkdownLinkPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex SpotifyMarkdownLinkRegex();

    [GeneratedRegex(SpotifyWebLinkPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex SpotifyWebLinkRegex();

    [GeneratedRegex(SpotifyUriPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex SpotifyUriRegex();

    [GeneratedRegex(DeezerWebLinkPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex DeezerWebLinkRegex();

    [GeneratedRegex(@"\b(?:part|chapter|episode|vol|volume)\s*(?<n>[2-9]|1[0-2])\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex SequelKeywordRegex();

    [GeneratedRegex(@"\b(?<n>[2-9]|1[0-2])\s*:", RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex SequelBeforeColonRegex();

    [GeneratedRegex(@"\b(?<n>[2-9]|1[0-2])\b\s*$", RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex SequelAtEndRegex();

    [GeneratedRegex(@"\b(?<n>II|III|IV|V|VI|VII|VIII|IX|X|XI|XII)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex SequelRomanRegex();

    [GeneratedRegex(@"\b(19|20)\d{2}\b", RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex TitleYearRegex();

    [GeneratedRegex(@"\b(?<n>[2-9]|10)\b", RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex SimpleSequelNumberRegex();

    private static string ComputeContentHash(MediaServerContentItem item)
    {
        var payload = string.Join("|",
            NormalizeServerType(item.ServerType),
            NormalizeText(item.LibraryId),
            NormalizeCategory(item.Category),
            NormalizeText(item.ItemId),
            NormalizeText(item.Title),
            item.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            NormalizeText(item.ImageUrl));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(bytes);
    }

    private static void NormalizeMatchMetadata(MediaServerSoundtrackMatchDto? match)
    {
        if (match == null)
        {
            return;
        }

        var normalizedKind = NormalizeText(match.Kind).ToLowerInvariant();
        var hasResolvedDeezerId = !string.IsNullOrWhiteSpace(NormalizeText(match.DeezerId))
            && !string.Equals(normalizedKind, MatchKindSearch, StringComparison.Ordinal);
        var hasResolvedSpotifyReference = normalizedKind.StartsWith(SpotifyKindPrefix, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(NormalizeText(match.Url));
        var hasResolvedMatch = hasResolvedDeezerId || hasResolvedSpotifyReference;
        var resolvedConfidence = Math.Clamp(match.Score, 0, 100);

        match.Provider = ResolveMatchProvider(match);
        match.Reason = ResolveMatchReason(match, hasResolvedMatch, resolvedConfidence);
        match.Locked = hasResolvedDeezerId && resolvedConfidence >= 80;
        if (hasResolvedMatch && match.ResolvedAtUtc == null)
        {
            match.ResolvedAtUtc = DateTimeOffset.UtcNow;
        }

        if (!hasResolvedMatch && match.RetryCount < 0)
        {
            match.RetryCount = 0;
        }
    }

    private static string ResolveMatchProvider(MediaServerSoundtrackMatchDto match)
    {
        var provider = NormalizeText(match.Provider).ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(provider))
        {
            return provider;
        }

        var kind = NormalizeText(match.Kind).ToLowerInvariant();
        if (kind.StartsWith(SpotifyKindPrefix, StringComparison.Ordinal))
        {
            return MatchProviderSpotify;
        }

        if (kind is MatchKindAlbum or MatchKindPlaylist or MatchKindTrack)
        {
            return MatchProviderDeezer;
        }

        var url = NormalizeText(match.Url).ToLowerInvariant();
        if (url.Contains("spotify.com", StringComparison.Ordinal))
        {
            return MatchProviderSpotify;
        }

        if (url.Contains("deezer.com", StringComparison.Ordinal))
        {
            return MatchProviderDeezer;
        }

        return MatchKindSearch;
    }

    private static string ResolveMatchReason(MediaServerSoundtrackMatchDto match, bool hasResolvedMatch, double confidence)
    {
        var existingReason = NormalizeText(match.Reason);
        if (!string.IsNullOrWhiteSpace(existingReason))
        {
            return existingReason;
        }

        if (!hasResolvedMatch)
        {
            return "pending_match";
        }

        if (confidence >= 80)
        {
            return "high_confidence_lock";
        }

        if (confidence >= 50)
        {
            return "reviewable_match";
        }

        return "low_confidence_match";
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
            return JellyfinImagePrimary;
        }

        if (episodePreferred
            && imageTags.TryGetValue(JellyfinImageThumb, out var thumbTag)
            && !string.IsNullOrWhiteSpace(thumbTag))
        {
            selectedTag = thumbTag;
            return JellyfinImageThumb;
        }

        if (imageTags.TryGetValue(JellyfinImagePrimary, out var primaryTag)
            && !string.IsNullOrWhiteSpace(primaryTag))
        {
            selectedTag = primaryTag;
            return JellyfinImagePrimary;
        }

        if (imageTags.TryGetValue(JellyfinImageThumb, out thumbTag)
            && !string.IsNullOrWhiteSpace(thumbTag))
        {
            selectedTag = thumbTag;
            return JellyfinImageThumb;
        }

        selectedTag = null;
        return JellyfinImagePrimary;
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
            MediaTypeSeries => MediaServerSoundtrackConstants.TvShowCategory,
            _ => MediaServerSoundtrackConstants.MovieCategory
        };
    }

    private static string MapPlexCategory(string? plexType)
    {
        var normalized = NormalizeText(plexType).ToLowerInvariant();
        return normalized switch
        {
            MediaTypeMovie => MediaServerSoundtrackConstants.MovieCategory,
            "show" => MediaServerSoundtrackConstants.TvShowCategory,
            MediaTypeSeries => MediaServerSoundtrackConstants.TvShowCategory,
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
            MediaTypeMovie => MediaServerSoundtrackConstants.MovieCategory,
            "movies" => MediaServerSoundtrackConstants.MovieCategory,
            "tvshows" => MediaServerSoundtrackConstants.TvShowCategory,
            "tvshow" => MediaServerSoundtrackConstants.TvShowCategory,
            MediaTypeSeries => MediaServerSoundtrackConstants.TvShowCategory,
            _ => string.Empty
        };
    }

    private static string MapJellyfinItemCategory(string? jellyfinType)
    {
        var normalized = NormalizeText(jellyfinType).ToLowerInvariant();
        return normalized switch
        {
            MediaTypeSeries => MediaServerSoundtrackConstants.TvShowCategory,
            MediaTypeMovie => MediaServerSoundtrackConstants.MovieCategory,
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
