using DeezSpoTag.Core.Models.Deezer;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Utils;
using DeezSpoTag.Web.Services;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[LocalApiAuthorize]
[Route("api/spotify/home-feed")]
public sealed class SpotifyHomeFeedApiController : ControllerBase
{
    private const string ArtistType = "artist";
    private const string AlbumType = "album";
    private const string TrackType = "track";
    private const string PlaylistType = "playlist";
    private const string EpisodeType = "episode";
    private const string ShowType = "show";
    private const string StationType = "station";
    private const string SectionItemsKey = "sectionItems";
    private const string ItemsKey = "items";
    private const string ContentKey = "content";
    private const string DataKey = "data";
    private const string HomeKey = "home";
    private const string GreetingKey = "greeting";
    private const string TextKey = "text";
    private const string UriKey = "uri";
    private const string IdKey = "id";
    private const string TypeKey = "type";
    private const string NameKey = "name";
    private const string DescriptionKey = "description";
    private const string ArtistsKey = "artists";
    private const string AlbumKey = "album";
    private const string CoverArtKey = "coverArt";
    private const string AlbumOfTrackKey = "albumOfTrack";
    private const string ProfileKey = "profile";
    private const string TitleKey = "title";
    private const string TransformedLabelKey = "transformedLabel";
    private const string CardRepresentationKey = "cardRepresentation";
    private const string SubtitleKey = "subtitle";
    private const string TotalKey = "total";
    private const string FollowersKey = "followers";
    private const string TrackCountKey = "trackCount";
    private const string VisualsKey = "visuals";
    private const string AvatarImageKey = "avatarImage";
    private const string ImageKey = "image";
    private const string ImagesKey = "images";
    private const string SectionContainerKey = "sectionContainer";
    private const string SectionsKey = "sections";
    private const string PublicKey = "public";
    private const string CollaborativeKey = "collaborative";
    private const string SpotifySource = "spotify";
    private const string SourcesKey = "sources";
    private const string UrlKey = "url";
    private const string TotalMillisecondsKey = "totalMilliseconds";
    private const string DefaultCacheKey = "default";
    private const string TrendingSongsTitle = "Trending songs";
    private const string PopularRadioTitle = "Popular radio";
    private const string PopularRadiosTitle = "Popular radios";
    private const string DefaultMusicBrowseCategoryId = "0JQ5DAqbMKFSi39LMRT0Cy";
    private const string FallbackPopularRadioSectionUri = "spotify:section:0JQ5DAnM3wGh0gz1MXnu4h";
    private const string ErrorBrowseDisabled = "Spotify browse disabled.";
    private const string ErrorCategoryIdRequired = "Category id is required.";
    private const string ErrorCategoryPlaylistsUnavailable = "Spotify category playlists unavailable.";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly bool SpotifyBrowseEnabled = ReadSpotifyBrowseEnabled();
    private static readonly HashSet<string> SupportedHomeItemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ArtistType,
        AlbumType,
        TrackType,
        PlaylistType,
        StationType,
        EpisodeType,
        ShowType
    };
    private const string TrendingSongsSectionUri = "spotify:section:0JQ5DB5E8N831KzFzsBBQ2";
    private readonly SpotifyPathfinderMetadataClient _pathfinderClient;
    private readonly SpotifyMetadataService _spotifyMetadataService;
    private readonly SpotifyDeezerAlbumResolver _spotifyDeezerAlbumResolver;
    private readonly SongLinkResolver _songLinkResolver;
    private readonly DeezSpoTag.Integrations.Deezer.DeezerClient _deezerClient;
    private readonly DeezSpoTag.Services.Settings.ISettingsService _settingsService;
    private readonly SpotifyBlobService _blobService;
    private readonly PlatformAuthService _platformAuthService;
    private readonly SpotifyUserAuthStore _userAuthStore;
    private readonly ISpotifyUserContextAccessor _userContext;
    private readonly ILogger<SpotifyHomeFeedApiController> _logger;
    private readonly IWebHostEnvironment _hostEnvironment;
    private static readonly TimeSpan MatchCacheTtl = TimeSpan.FromMinutes(10);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string? DeezerUrl, DateTimeOffset Stamp)> MatchCache
        = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset Stamp, object Item)> BrowseCategoryCache
        = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset Stamp, IReadOnlyList<object> Items, int Total)> BrowseCategoryPlaylistsCache
        = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object BrowseCategoriesLock = new();
    private static (DateTimeOffset Stamp, List<object> Categories)? BrowseCategoriesCache;
    private static readonly object BrowseCategoriesFileLock = new();
    private static readonly object FeedCacheLock = new();
    private static readonly Dictionary<string, (DateTimeOffset Stamp, string Greeting, List<object> Sections)> HomeFeedCache
        = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object PopularRadioCacheLock = new();
    private static (DateTimeOffset Stamp, object Section)? PopularRadioSectionCache;
    private static readonly TimeSpan HomeFeedCacheTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan PopularRadioCacheTtl = TimeSpan.FromMinutes(20);
    private static readonly JsonSerializerOptions CompactJsonSerializerOptions = new()
    {
        WriteIndented = false
    };
    private static readonly string[] PersonalSectionKeywords =
    {
        "favorites",
        "favourites",
        "liked songs",
        "your top playlists",
        "your top playlist",
        "your playlists",
        "loved tracks",
        "your library",
        "your episodes",
        "your shows"
    };
    private static readonly string[] PersonalItemKeywords =
    {
        "liked songs",
        "loved tracks",
        "favorites",
        "favourites",
        "your library",
        "your top playlists",
        "your top playlist",
        "your playlist",
        "your playlists",
        "your episodes",
        "your shows",
        "my playlist",
        "my playlists"
    };
    private static string ReplaceWithTimeout(string input, string pattern, string replacement, RegexOptions options = RegexOptions.None)
        => Regex.Replace(input, pattern, replacement, options, RegexTimeout);

    private sealed record HomeFeedSelection(
        bool Success,
        string Greeting,
        List<object> Sections,
        int WebplayerCount,
        int FallbackCount,
        JsonDocument? WebplayerDocument);

    private sealed record BrowseCategoryPlaylistsResult(
        bool Success,
        IReadOnlyList<object> Items,
        int Total,
        bool Cached,
        bool ContainsFullList = false,
        string? Error = null);

    private sealed record TrendingBrowseCandidate(string? Id, string? Uri, string Name);

    private sealed record HomeItemIdentity(string ItemType, string ItemId, string Uri);

    private sealed record HomeItemMetadata(
        string? CoverUrl = null,
        string? Artists = null,
        string? Description = null,
        string? AlbumId = null,
        string? AlbumName = null,
        int? DurationMs = null,
        int? TrackCount = null,
        int? Followers = null,
        bool? IsPublic = null,
        bool? Collaborative = null);

    private sealed record MetadataEnrichmentLimits(
        int MaxItemsPerSectionToEnrich,
        int MaxPlaylistMetadataLookups,
        int MaxArtistMetadataLookups);

    private static bool ReadSpotifyBrowseEnabled()
    {
        var value = Environment.GetEnvironmentVariable("DEEZSPOTAG_SPOTIFY_BROWSE_ENABLED");
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    public SpotifyHomeFeedApiController(
        SpotifyHomeFeedCollaborators collaborators,
        ILogger<SpotifyHomeFeedApiController> logger,
        IWebHostEnvironment hostEnvironment)
    {
        _pathfinderClient = collaborators.PathfinderClient;
        _spotifyMetadataService = collaborators.SpotifyMetadataService;
        _spotifyDeezerAlbumResolver = collaborators.SpotifyDeezerAlbumResolver;
        _songLinkResolver = collaborators.SongLinkResolver;
        _deezerClient = collaborators.DeezerClient;
        _settingsService = collaborators.SettingsService;
        _blobService = collaborators.BlobService;
        _platformAuthService = collaborators.PlatformAuthService;
        _userAuthStore = collaborators.UserAuthStore;
        _userContext = collaborators.UserContextAccessor;
        _logger = logger;
        _hostEnvironment = hostEnvironment;
    }

    [HttpGet]
    public async Task<IActionResult> GetHomeFeed([FromQuery] string? timeZone, [FromQuery] bool debug, [FromQuery] bool refresh = false, CancellationToken cancellationToken = default)
    {
        var cacheKey = DefaultCacheKey;
        try
        {
            _logger.LogInformation("Spotify home feed requested. tz=TimeZone");
            cacheKey = await ResolveHomeFeedCacheKeyAsync();
            var settings = _settingsService.LoadSettings();
            if (!refresh && settings.SpotifyHomeFeedCacheEnabled && !debug &&
                TryGetFreshHomeFeedCache(cacheKey, out var cachedFeed))
            {
                return Ok(new
                {
                    success = true,
                    greeting = cachedFeed.Greeting,
                    sections = cachedFeed.Sections,
                    cached = true
                });
            }
            var homeFeed = await FetchSelectedHomeFeedAsync(timeZone, cancellationToken);
            var finalSections = homeFeed.Sections;
            var greeting = homeFeed.Greeting;
            var success = finalSections.Count > 0;

            if (settings.SpotifyHomeFeedCacheEnabled && success)
            {
                StoreHomeFeedCache(cacheKey, greeting, finalSections);
            }

            var response = new
            {
                success,
                greeting,
                sections = finalSections,
                warning = success ? null : "Spotify web-player auth unavailable or returned empty feed.",
                diagnostics = debug && homeFeed.WebplayerDocument != null ? BuildHomeFeedDiagnostics(homeFeed.WebplayerDocument) : null,
                sectionDiagnostics = debug
                    ? BuildHomeFeedSectionDiagnostics(homeFeed, finalSections)
                    : null,
                authDiagnostics = _hostEnvironment.IsDevelopment()
                    ? await BuildHomeFeedAuthDiagnosticsAsync(cancellationToken)
                    : null
            };

            return Ok(response);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Spotify home feed fetch failed.");
            return StatusCode(502, new { success = false, error = "Spotify home feed failed." });
        }
    }

    [HttpGet("sections")]
    public async Task<IActionResult> GetHomeFeedSections([FromQuery] string? timeZone, [FromQuery] bool refresh = false, CancellationToken cancellationToken = default)
    {
        var cacheKey = DefaultCacheKey;
        try
        {
            cacheKey = await ResolveHomeFeedCacheKeyAsync();
            var settings = _settingsService.LoadSettings();
            if (!refresh && settings.SpotifyHomeFeedCacheEnabled &&
                TryGetFreshHomeFeedCache(cacheKey, out var cachedFeed))
            {
                var mapped = await MapHomeSectionsAsync(cachedFeed.Sections, cancellationToken);
                return Ok(new { success = mapped.Count > 0, sections = mapped, cached = true });
            }

            var homeFeed = await FetchSelectedHomeFeedAsync(timeZone, cancellationToken);
            var sections = homeFeed.Sections;
            if (sections.Count > 0 && settings.SpotifyHomeFeedCacheEnabled)
            {
                StoreHomeFeedCache(cacheKey, homeFeed.Greeting, sections);
            }

            var mappedSections = await MapHomeSectionsAsync(sections, cancellationToken);
            return Ok(new { success = mappedSections.Count > 0, sections = mappedSections });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Spotify home sections mapping failed.");
            return StatusCode(502, new { success = false, error = "Spotify home sections failed." });
        }
    }

    [HttpPost("cache/clear")]
    public IActionResult ClearHomeFeedCache()
    {
        ClearRuntimeAndPersistedCaches();

        return Ok(new { success = true });
    }

    [HttpPost("clear-cache")]
    public IActionResult ClearHomeFeedCacheLegacy()
        => ClearHomeFeedCache();

    public static void ClearRuntimeAndPersistedCaches()
    {
        lock (FeedCacheLock)
        {
            HomeFeedCache.Clear();
        }

        BrowseCategoryCache.Clear();
        BrowseCategoryPlaylistsCache.Clear();
        lock (BrowseCategoriesLock)
        {
            BrowseCategoriesCache = null;
        }

        TryDeleteHomeFeedCache();
        TryDeleteBrowseCategoriesCache();
        lock (PopularRadioCacheLock)
        {
            PopularRadioSectionCache = null;
        }
    }

    private static void StoreBrowseCategoriesCache(List<object> categories)
    {
        lock (BrowseCategoriesLock)
        {
            BrowseCategoriesCache = (DateTimeOffset.UtcNow, categories);
        }
    }

    [HttpGet("browse")]
    public async Task<IActionResult> GetBrowseCategories([FromQuery] bool refresh = false, [FromQuery] bool debug = false, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!SpotifyBrowseEnabled)
            {
                return StatusCode(404, new { success = false, error = ErrorBrowseDisabled });
            }

            var settings = _settingsService.LoadSettings();
            var browseCacheTtl = TimeSpan.FromMinutes(Math.Clamp(settings.SpotifyBrowseCacheMinutes, 1, 1440));
            _logger.LogInformation("Spotify browse categories requested.");
            if (!refresh && TryGetFreshBrowseCategoriesCache(browseCacheTtl, out var cachedCategories))
            {
                return Ok(new { success = true, categories = cachedCategories });
            }

            var doc = await _pathfinderClient.FetchBrowseAllWithBlobAsync(cancellationToken);
            if (doc is null)
            {
                return Ok(BuildUnavailableBrowseCategoriesResponse(debug));
            }

            var parsed = ParseBrowseCategories(doc);
            var categories = MapBrowseCategories(parsed);

            if (categories.Count == 0 && TryBuildBrowseCategoriesFallback("Using last known browse cache (empty response).", out var emptyFallback))
            {
                return Ok(emptyFallback);
            }

            StoreBrowseCategoriesCache(categories);
            TryPersistBrowseCategoriesCache(categories);
            if (debug)
            {
                return Ok(BuildBrowseCategoriesDebugResponse(categories, doc));
            }
            return Ok(new { success = true, categories });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Spotify browse categories fetch failed.");
            if (TryBuildBrowseCategoriesFallback("Using last known browse cache (browse failed).", out var failedFallback))
            {
                return Ok(failedFallback);
            }
            return Ok(BuildBrowseCategoriesErrorResponse(ex, debug));
        }
    }

    private static object BuildUnavailableBrowseCategoriesResponse(bool debug)
    {
        if (TryBuildBrowseCategoriesFallback("Using last known browse cache (auth unavailable).", out var unavailableFallback))
        {
            return unavailableFallback;
        }

        return debug
            ? new
            {
                success = false,
                error = "Spotify browse categories unavailable.",
                categories = Array.Empty<object>(),
                debug = "pathfinder=unavailable"
            }
            : new { success = false, error = "Spotify browse categories unavailable.", categories = Array.Empty<object>() };
    }

    private static List<object> MapBrowseCategories(IEnumerable<object> parsed)
    {
        return parsed.Select(MapBrowseCategory).ToList();
    }

    private static object MapBrowseCategory(object category)
    {
        var type = category.GetType();
        return new
        {
            id = type.GetProperty("id")?.GetValue(category),
            uri = type.GetProperty("uri")?.GetValue(category),
            name = type.GetProperty("name")?.GetValue(category),
            image_url = type.GetProperty("imageUrl")?.GetValue(category),
            background_color = type.GetProperty("backgroundColor")?.GetValue(category),
            section = type.GetProperty("section")?.GetValue(category)
        };
    }

    private static object BuildBrowseCategoriesDebugResponse(List<object> categories, JsonDocument doc)
    {
        return new
        {
            success = true,
            categories,
            debug = new
            {
                categoryCount = categories.Count,
                shape = BuildBrowseShapeSummary(doc),
                diagnostics = BuildBrowseDiagnostics(doc)
            }
        };
    }

    private static object BuildBrowseCategoriesErrorResponse(Exception ex, bool debug)
    {
        return debug
            ? new { success = false, error = ex.Message, categories = Array.Empty<object>(), debug = ex.ToString() }
            : new { success = false, error = ex.Message, categories = Array.Empty<object>() };
    }

    [HttpGet("browse/resolve")]
    public async Task<IActionResult> ResolveBrowseCategory([FromQuery] string? id, [FromQuery] string? uri, [FromQuery] bool refresh = false, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!SpotifyBrowseEnabled)
            {
                return StatusCode(404, new { success = false, error = ErrorBrowseDisabled });
            }

            var settings = _settingsService.LoadSettings();
            var browseCacheTtl = TimeSpan.FromMinutes(Math.Clamp(settings.SpotifyBrowseCacheMinutes, 1, 1440));
            var categoryId = ResolveBrowseCategoryId(id, uri);
            if (string.IsNullOrWhiteSpace(categoryId))
            {
                return BadRequest(new { success = false, error = ErrorCategoryIdRequired });
            }

            var now = DateTimeOffset.UtcNow;
            if (!refresh && BrowseCategoryCache.TryGetValue(categoryId, out var cached) &&
                now - cached.Stamp <= browseCacheTtl)
            {
                return Ok(new { success = true, item = cached.Item, cached = true });
            }

            var pageUri = BuildBrowsePageUri(categoryId, uri);
            if (string.IsNullOrWhiteSpace(pageUri))
            {
                return Ok(new { success = false, error = "Spotify browse category unavailable." });
            }

            var pageDoc = await _pathfinderClient.FetchBrowsePageWithBlobAsync(pageUri, 0, 10, 0, 10, cancellationToken);
            if (pageDoc is null)
            {
                return Ok(new { success = false, error = "Spotify browse category unavailable." });
            }

            var (playlistDoc, _) = await FetchFirstPlaylistSectionAsync(pageDoc, 0, 1, cancellationToken);
            if (playlistDoc is null)
            {
                return Ok(new { success = false, error = ErrorCategoryPlaylistsUnavailable });
            }

            var items = ParseBrowsePlaylistItems(playlistDoc);
            if (items.Count == 0)
            {
                return Ok(new { success = false, error = "No playlists available for this category." });
            }

            var item = items[0];
            BrowseCategoryCache[categoryId] = (DateTimeOffset.UtcNow, item);
            return Ok(new { success = true, item });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Spotify browse category resolve failed.");
            return StatusCode(502, new { success = false, error = "Spotify browse category resolve failed." });
        }
    }

    [HttpGet("browse/category")]
    public async Task<IActionResult> GetBrowseCategory(
        [FromQuery] string? categoryId,
        [FromQuery] string? uri,
        [FromQuery] bool refresh = false,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 30,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!SpotifyBrowseEnabled)
            {
                return StatusCode(404, new { success = false, error = ErrorBrowseDisabled });
            }

            var settings = _settingsService.LoadSettings();
            var browseCacheTtl = TimeSpan.FromMinutes(Math.Clamp(settings.SpotifyBrowseCacheMinutes, 1, 1440));
            var resolvedId = ResolveBrowseCategoryId(categoryId, uri);
            if (string.IsNullOrWhiteSpace(resolvedId))
            {
                return BadRequest(new { success = false, error = ErrorCategoryIdRequired });
            }

            if (!refresh && TryGetFreshBrowseCategoryPlaylistsCache(resolvedId, browseCacheTtl, out var cachedPlaylists))
            {
                var slice = cachedPlaylists.Items.Skip(offset).Take(limit).ToList();
                return Ok(new
                {
                    success = true,
                    items = slice,
                    offset,
                    limit,
                    total = cachedPlaylists.Total,
                    cached = true
                });
            }

            offset = Math.Max(offset, 0);
            limit = Math.Clamp(limit, 1, 50);
            var result = await FetchBrowseCategoryPlaylistsAsync(resolvedId, uri, offset, limit, cancellationToken);
            if (!result.Success)
            {
                return Ok(new { success = false, error = result.Error ?? ErrorCategoryPlaylistsUnavailable });
            }

            if (offset == 0 && result.Items.Count > 0)
            {
                BrowseCategoryCache[resolvedId] = (DateTimeOffset.UtcNow, result.Items[0]);
            }

            return Ok(new
            {
                success = true,
                items = result.ContainsFullList
                    ? result.Items.Skip(offset).Take(limit).ToList()
                    : result.Items,
                offset,
                limit,
                total = result.Total,
                cached = result.Cached
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Spotify browse category fetch failed.");
            return StatusCode(502, new { success = false, error = "Spotify browse category failed." });
        }
    }

    public sealed record SpotifyBrowseBatchRequest(
        IReadOnlyList<string> CategoryIds,
        int Limit = 20,
        int BatchSize = 3,
        int DelayMs = 1200,
        bool Refresh = false);

    [HttpPost("browse/batch")]
    public async Task<IActionResult> GetBrowseCategoriesBatch(
        [FromBody] SpotifyBrowseBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!SpotifyBrowseEnabled)
            {
                return StatusCode(404, new { success = false, error = ErrorBrowseDisabled });
            }

            if (request?.CategoryIds == null || request.CategoryIds.Count == 0)
            {
                return BadRequest(new { success = false, error = "Category ids are required." });
            }

            var settings = _settingsService.LoadSettings();
            var browseCacheTtl = TimeSpan.FromMinutes(Math.Clamp(settings.SpotifyBrowseCacheMinutes, 1, 1440));
            var limit = Math.Clamp(request.Limit, 1, 50);
            var batchSize = Math.Clamp(request.BatchSize, 1, 10);
            var delayMs = Math.Clamp(request.DelayMs, 0, 5000);

            var now = DateTimeOffset.UtcNow;
            var results = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var ids = request.CategoryIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (var i = 0; i < ids.Count; i += batchSize)
            {
                var batch = ids.Skip(i).Take(batchSize).ToList();
                foreach (var id in batch)
                {
                    var result = await ResolveBrowseBatchCategoryAsync(id, request.Refresh, browseCacheTtl, now, limit, cancellationToken);
                    results[id] = result;
                }

                if (delayMs > 0 && i + batchSize < ids.Count)
                {
                    await Task.Delay(delayMs, cancellationToken);
                }
            }

            return Ok(new { success = true, results });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Spotify browse batch fetch failed.");
            return StatusCode(502, new { success = false, error = "Spotify browse batch failed." });
        }
    }

    [HttpPost("map")]
    public async Task<IActionResult> MapBatch([FromBody] SpotifyHomeMapRequest request, CancellationToken cancellationToken)
    {
        if (request?.Urls == null || request.Urls.Count == 0)
        {
            return Ok(new { success = true, matches = new Dictionary<string, string?>() });
        }

        var now = DateTimeOffset.UtcNow;
        var urls = request.Urls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var matches = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<string>();
        foreach (var url in urls)
        {
            if (MatchCache.TryGetValue(url, out var cached) && now - cached.Stamp <= MatchCacheTtl)
            {
                matches[url] = cached.DeezerUrl;
            }
            else
            {
                pending.Add(url);
            }
        }

        if (pending.Count == 0)
        {
            return Ok(new { success = true, matches });
        }

        using var throttle = new SemaphoreSlim(4);
        var tasks = pending.Select(async url =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                var deezerUrl = await ResolveDeezerUrlAsync(url, cancellationToken);
                MatchCache[url] = (deezerUrl, DateTimeOffset.UtcNow);
                lock (matches)
                {
                    matches[url] = deezerUrl;
                }
            }
            finally
            {
                throttle.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);

        return Ok(new { success = true, matches });
    }

    private static bool TryGetFreshHomeFeedCache(
        string cacheKey,
        out (DateTimeOffset Stamp, string Greeting, List<object> Sections) cache)
    {
        lock (FeedCacheLock)
        {
            if (HomeFeedCache.TryGetValue(cacheKey, out cache) &&
                DateTimeOffset.UtcNow - cache.Stamp <= HomeFeedCacheTtl)
            {
                return true;
            }
        }

        cache = default;
        return false;
    }

    private static (DateTimeOffset Stamp, string Greeting, List<object> Sections) StoreHomeFeedCache(
        string cacheKey,
        string greeting,
        List<object> sections)
    {
        lock (FeedCacheLock)
        {
            HomeFeedCache[cacheKey] = (DateTimeOffset.UtcNow, greeting ?? string.Empty, sections);
            return HomeFeedCache[cacheKey];
        }
    }

    private static object BuildHomeFeedSectionDiagnostics(HomeFeedSelection homeFeed, List<object> finalSections)
    {
        return new
        {
            webplayerCount = homeFeed.WebplayerCount,
            pathfinderCount = 0,
            finalCount = finalSections.Count,
            fallbackCount = homeFeed.FallbackCount,
            pathfinderEnabled = false,
            webplayerTitles = ExtractSectionTitles(homeFeed.WebplayerCount > 0 ? homeFeed.Sections : Array.Empty<object>()),
            pathfinderTitles = Array.Empty<string>(),
            fallbackTitles = Array.Empty<string>(),
            finalTitles = ExtractSectionTitles(finalSections),
            webplayerDuplicates = FindDuplicateTitles(homeFeed.WebplayerCount > 0 ? homeFeed.Sections : Array.Empty<object>()),
            pathfinderDuplicates = Array.Empty<string>(),
            fallbackDuplicates = Array.Empty<string>(),
            webplayerSummary = SummarizeSections(homeFeed.WebplayerCount > 0 ? homeFeed.Sections : Array.Empty<object>(), "webplayer"),
            pathfinderSummary = Array.Empty<object>(),
            fallbackSummary = Array.Empty<object>(),
            finalSummary = SummarizeSections(finalSections, "final")
        };
    }

    private static bool TryGetFreshBrowseCategoriesCache(TimeSpan ttl, out List<object> categories)
    {
        lock (BrowseCategoriesLock)
        {
            if (BrowseCategoriesCache.HasValue &&
                DateTimeOffset.UtcNow - BrowseCategoriesCache.Value.Stamp <= ttl)
            {
                categories = BrowseCategoriesCache.Value.Categories;
                return true;
            }
        }

        categories = new List<object>();
        return false;
    }

    private static bool TryBuildBrowseCategoriesFallback(string warning, out object response)
    {
        if (TryLoadPersistedBrowseCategoriesCache(out var categories))
        {
            response = new
            {
                success = true,
                categories,
                cached = true,
                warning
            };
            return true;
        }

        response = null!;
        return false;
    }

    private static bool TryGetFreshBrowseCategoryPlaylistsCache(
        string categoryId,
        TimeSpan ttl,
        out (DateTimeOffset Stamp, IReadOnlyList<object> Items, int Total) cache)
    {
        if (BrowseCategoryPlaylistsCache.TryGetValue(categoryId, out cache) &&
            DateTimeOffset.UtcNow - cache.Stamp <= ttl)
        {
            return true;
        }

        cache = default;
        return false;
    }

    private async Task<BrowseCategoryPlaylistsResult> FetchBrowseCategoryPlaylistsAsync(
        string categoryId,
        string? uri,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        var pageUri = BuildBrowsePageUri(categoryId, uri);
        if (string.IsNullOrWhiteSpace(pageUri))
        {
            return new BrowseCategoryPlaylistsResult(false, Array.Empty<object>(), 0, false, false, ErrorCategoryPlaylistsUnavailable);
        }

        var pageDoc = await _pathfinderClient.FetchBrowsePageWithBlobAsync(pageUri, 0, 20, 0, 20, cancellationToken);
        if (pageDoc is null)
        {
            return new BrowseCategoryPlaylistsResult(false, Array.Empty<object>(), 0, false, false, ErrorCategoryPlaylistsUnavailable);
        }

        var (playlistDoc, containsFullList) = await FetchFirstPlaylistSectionAsync(pageDoc, offset, limit, cancellationToken);
        if (playlistDoc is null)
        {
            return new BrowseCategoryPlaylistsResult(false, Array.Empty<object>(), 0, false, false, ErrorCategoryPlaylistsUnavailable);
        }

        var items = DeduplicateBrowseItems(ParseBrowsePlaylistItems(playlistDoc));
        var total = containsFullList
            ? items.Count
            : (TryGetBrowseTotal(playlistDoc.RootElement) ?? items.Count);

        if (containsFullList)
        {
            BrowseCategoryPlaylistsCache[categoryId] = (DateTimeOffset.UtcNow, items, total);
        }

        return new BrowseCategoryPlaylistsResult(true, items, total, false, containsFullList);
    }

    private async Task<object> ResolveBrowseBatchCategoryAsync(
        string id,
        bool refresh,
        TimeSpan ttl,
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken)
    {
        if (!refresh &&
            BrowseCategoryPlaylistsCache.TryGetValue(id, out var cached) &&
            now - cached.Stamp <= ttl)
        {
            return new
            {
                success = true,
                items = cached.Items,
                total = cached.Total,
                cached = true
            };
        }

        var result = await FetchBrowseCategoryPlaylistsAsync(id, null, 0, limit, cancellationToken);
        return result.Success
            ? new
            {
                success = true,
                items = result.Items,
                total = result.Total,
                cached = result.Cached
            }
            : new
            {
                success = false,
                error = result.Error ?? ErrorCategoryPlaylistsUnavailable
            };
    }

    private async Task<HomeFeedSelection> FetchSelectedHomeFeedAsync(string? timeZone, CancellationToken cancellationToken)
    {
        var blobTask = _pathfinderClient.FetchHomeFeedWithBlobAsync(timeZone, cancellationToken);
        var legacyTask = _pathfinderClient.FetchHomeFeedLegacyWithBlobAsync(timeZone, cancellationToken);
        var trendingTask = TryFetchTrendingSongsSectionAsync(cancellationToken);
        await Task.WhenAll(blobTask, legacyTask, trendingTask);

        var blobDoc = await blobTask;
        var blobResult = blobDoc == null
            ? (Success: false, Greeting: string.Empty, Sections: new List<object>())
            : ParseHomeFeed(blobDoc);
        var legacyDoc = await legacyTask;
        var legacyResult = legacyDoc == null
            ? (Success: false, Greeting: string.Empty, Sections: new List<object>())
            : ParseHomeFeed(legacyDoc);
        var selectedSections = blobResult.Sections.Count > 0 ? blobResult.Sections : legacyResult.Sections;
        selectedSections = MergeMissingPopularRadioSection(selectedSections, legacyResult.Sections);
        selectedSections = await EnsurePopularRadioSectionAsync(selectedSections, timeZone, cancellationToken);
        var greeting = !string.IsNullOrWhiteSpace(blobResult.Greeting) ? blobResult.Greeting : legacyResult.Greeting ?? string.Empty;
        var finalSections = AddTrendingSection(selectedSections, await trendingTask);

        return new HomeFeedSelection(
            finalSections.Count > 0,
            greeting ?? string.Empty,
            finalSections,
            blobResult.Sections.Count,
            legacyResult.Sections.Count,
            blobDoc);
    }

    private static List<object> MergeMissingPopularRadioSection(
        IReadOnlyList<object> primarySections,
        IReadOnlyList<object> legacySections)
    {
        var merged = primarySections.ToList();
        if (ContainsSectionTitle(merged, PopularRadioTitle) || ContainsSectionTitle(merged, PopularRadiosTitle))
        {
            return merged;
        }

        foreach (var section in legacySections)
        {
            if (!TitleMatches(section, PopularRadioTitle) && !TitleMatches(section, PopularRadiosTitle))
            {
                continue;
            }

            var items = TryGetAnonymousItems(section);
            if (items == null || items.Count == 0)
            {
                continue;
            }

            merged.Add(section);
            break;
        }

        if (ContainsSectionTitle(merged, PopularRadioTitle) || ContainsSectionTitle(merged, PopularRadiosTitle))
        {
            return merged;
        }

        if (TryBuildPopularRadioSectionFromFanSections(primarySections) is { } synthesized)
        {
            merged.Add(synthesized);
        }

        return merged;
    }

    private async Task<List<object>> EnsurePopularRadioSectionAsync(
        IReadOnlyList<object> sections,
        string? timeZone,
        CancellationToken cancellationToken)
    {
        var merged = sections.ToList();
        var existingIndex = FindPopularRadioSectionIndex(merged);
        var existingCount = existingIndex >= 0
            ? (TryGetAnonymousItems(merged[existingIndex])?.Count ?? 0)
            : 0;

        // Existing home/legacy popular-radio payloads are often thin (8-10 cards).
        // Rehydrate from browse page when absent or clearly truncated.
        if (existingIndex >= 0 && existingCount > 10)
        {
            return merged;
        }

        var hydratedSection = await TryFetchPopularRadioSectionFromBrowseAsync(timeZone, cancellationToken);
        if (hydratedSection is null)
        {
            return merged;
        }

        if (existingIndex >= 0)
        {
            merged[existingIndex] = hydratedSection;
            return merged;
        }

        merged.Add(hydratedSection);
        return merged;
    }

    private async Task<object?> TryFetchPopularRadioSectionFromBrowseAsync(string? timeZone, CancellationToken cancellationToken)
    {
        if (!SpotifyBrowseEnabled)
        {
            return null;
        }

        if (TryGetCachedPopularRadioSection(out var cached))
        {
            return cached;
        }

        var pageUri = await TryResolveMusicBrowsePageUriAsync(cancellationToken);
        var sectionSnapshot = await TryResolvePopularRadioBrowseSectionAsync(pageUri, cancellationToken);
        if (string.IsNullOrWhiteSpace(sectionSnapshot.SectionUri))
        {
            return null;
        }

        var hydratedItems = await FetchPopularRadioHydratedItemsAsync(
            sectionSnapshot.SectionUri,
            timeZone,
            cancellationToken);

        if (hydratedItems.Count == 0)
        {
            hydratedItems.AddRange(sectionSnapshot.InlineItems);
        }

        if (hydratedItems.Count == 0)
        {
            return null;
        }

        hydratedItems = DeduplicatePopularRadioItems(hydratedItems);

        var normalizedSection = new
        {
            uri = sectionSnapshot.SectionUri,
            title = PopularRadioTitle,
            __preserveAllItems = true,
            preserveAllItems = true,
            items = hydratedItems
        };

        CachePopularRadioSection(normalizedSection);
        return normalizedSection;
    }

    private async Task<(string SectionUri, List<object> InlineItems)> TryResolvePopularRadioBrowseSectionAsync(
        string? pageUri,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pageUri))
        {
            return (string.Empty, new List<object>());
        }

        var pageDoc = await _pathfinderClient.FetchBrowsePageWithBlobAsync(pageUri, 0, 50, 0, 50, cancellationToken);
        var pageSections = pageDoc is null ? new List<object>() : ParseBrowsePageSections(pageDoc);
        var popularRadioSection = pageSections.FirstOrDefault(section =>
            IsPopularRadioTitle(TryGetAnonymousTitle(section)));
        var sectionUri = popularRadioSection is null
            ? string.Empty
            : (TryGetAnonymousString(popularRadioSection, UriKey) ?? string.Empty);
        if (string.IsNullOrWhiteSpace(sectionUri))
        {
            sectionUri = FallbackPopularRadioSectionUri;
        }

        return (
            sectionUri,
            ExtractInlinePopularRadioItems(popularRadioSection));
    }

    private async Task<List<object>> FetchPopularRadioHydratedItemsAsync(
        string sectionUri,
        string? timeZone,
        CancellationToken cancellationToken)
    {
        var hydratedItems = new List<object>();
        var homeSectionDoc = await _pathfinderClient.FetchHomeSectionWithBlobAsync(sectionUri, timeZone, 0, 60, cancellationToken);
        AppendMappedBrowseItems(homeSectionDoc, hydratedItems);

        var sectionDoc = await _pathfinderClient.FetchBrowseSectionWithBlobAsync(sectionUri, 0, 60, cancellationToken);
        AppendMappedBrowseItems(sectionDoc, hydratedItems);

        return hydratedItems;
    }

    private static void AppendMappedBrowseItems(JsonDocument? sourceDoc, List<object> target)
    {
        if (sourceDoc is null)
        {
            return;
        }

        var sourceItems = ParseBrowsePlaylistItems(sourceDoc);
        foreach (var sourceItem in sourceItems)
        {
            var mapped = MapBrowsePlaylistToHomeRawItem(sourceItem);
            if (mapped is not null)
            {
                target.Add(mapped);
            }
        }
    }

    private static List<object> ExtractInlinePopularRadioItems(object? popularRadioSection)
    {
        if (popularRadioSection is null)
        {
            return new List<object>();
        }

        var inlineItems = TryGetAnonymousItems(popularRadioSection);
        if (inlineItems is null || inlineItems.Count == 0)
        {
            return new List<object>();
        }

        return inlineItems.Where(item => item is not null).ToList()!;
    }

    private static List<object> DeduplicatePopularRadioItems(List<object> hydratedItems)
    {
        return hydratedItems
            .Where(item => item is not null)
            .GroupBy(
                item => TryGetAnonymousString(item, UriKey) ?? $"{TryGetAnonymousString(item, TypeKey)}:{TryGetAnonymousString(item, IdKey)}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private async Task<string?> TryResolveMusicBrowsePageUriAsync(CancellationToken cancellationToken)
    {
        var browseDoc = await _pathfinderClient.FetchBrowseAllWithBlobAsync(cancellationToken);
        if (browseDoc is not null)
        {
            var categories = ParseBrowseCategories(browseDoc);
            var musicCategory = categories.FirstOrDefault(category =>
            {
                var name = TryGetAnonymousString(category, NameKey);
                if (string.IsNullOrWhiteSpace(name))
                {
                    return false;
                }

                return string.Equals(name.Trim(), "Music", StringComparison.OrdinalIgnoreCase);
            });

            if (musicCategory is not null)
            {
                var categoryId = TryGetAnonymousString(musicCategory, IdKey);
                var categoryUri = TryGetAnonymousString(musicCategory, UriKey);
                var categoryPageUri = BuildBrowsePageUri(categoryId, categoryUri);
                if (!string.IsNullOrWhiteSpace(categoryPageUri))
                {
                    return categoryPageUri;
                }
            }
        }

        return BuildBrowsePageUri(DefaultMusicBrowseCategoryId, null);
    }

    private static int FindPopularRadioSectionIndex(List<object> sections)
    {
        for (var i = 0; i < sections.Count; i++)
        {
            if (IsPopularRadioTitle(TryGetAnonymousTitle(sections[i])))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsPopularRadioTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var normalized = title.Trim();
        return string.Equals(normalized, PopularRadioTitle, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, PopularRadiosTitle, StringComparison.OrdinalIgnoreCase);
    }

    private static object? MapBrowsePlaylistToHomeRawItem(object? browseItem)
    {
        if (browseItem is null)
        {
            return null;
        }

        var uri = TryGetAnonymousString(browseItem, UriKey);
        var (itemType, itemId) = TryParseSpotifyUri(uri);
        if (string.IsNullOrWhiteSpace(itemId) ||
            !string.Equals(itemType, PlaylistType, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var name = TryGetAnonymousString(browseItem, NameKey);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var coverUrl = TryGetAnonymousString(browseItem, "imageUrl");
        var description = TryGetAnonymousString(browseItem, DescriptionKey);

        return new
        {
            id = itemId,
            uri = uri ?? $"spotify:{PlaylistType}:{itemId}",
            type = PlaylistType,
            name,
            artists = (string?)null,
            description,
            coverUrl,
            trackCount = (int?)null,
            followers = (int?)null,
            @public = (bool?)null,
            collaborative = (bool?)null,
            albumId = (string?)null,
            albumName = (string?)null,
            durationMs = (int?)null
        };
    }

    private static bool TryGetCachedPopularRadioSection(out object? section)
    {
        lock (PopularRadioCacheLock)
        {
            if (PopularRadioSectionCache is { } cached &&
                DateTimeOffset.UtcNow - cached.Stamp <= PopularRadioCacheTtl)
            {
                section = cached.Section;
                return true;
            }
        }

        section = null;
        return false;
    }

    private static void CachePopularRadioSection(object section)
    {
        lock (PopularRadioCacheLock)
        {
            PopularRadioSectionCache = (DateTimeOffset.UtcNow, section);
        }
    }

    private static object? TryBuildPopularRadioSectionFromFanSections(IReadOnlyList<object> sections)
    {
        var radioItems = new List<object>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var section in sections)
        {
            var title = TryGetAnonymousTitle(section);
            if (string.IsNullOrWhiteSpace(title) ||
                !title.StartsWith("For fans of", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var items = TryGetAnonymousItems(section);
            if (items == null || items.Count == 0)
            {
                continue;
            }

            foreach (var item in items)
            {
                TryAddPopularRadioCandidate(item, seen, radioItems);
            }
        }

        if (radioItems.Count < 4)
        {
            return null;
        }

        return new
        {
            uri = "spotify:section:popular-radio-synth",
            title = PopularRadioTitle,
            items = radioItems.Take(20).ToList()
        };
    }

    private static void TryAddPopularRadioCandidate(
        object? item,
        HashSet<string> seen,
        List<object> radioItems)
    {
        if (!IsPopularRadioCandidateItem(item))
        {
            return;
        }

        var key = TryResolvePopularRadioCandidateKey(item);
        if (!seen.Add(key))
        {
            return;
        }

        radioItems.Add(item!);
    }

    private static string TryResolvePopularRadioCandidateKey(object? item)
    {
        if (item is null)
        {
            return string.Empty;
        }

        var uri = TryGetAnonymousString(item, UriKey);
        if (!string.IsNullOrWhiteSpace(uri))
        {
            return uri;
        }

        var itemType = TryGetAnonymousString(item, TypeKey) ?? string.Empty;
        var itemId = TryGetAnonymousString(item, IdKey) ?? string.Empty;
        return $"{itemType}:{itemId}";
    }

    private static bool IsPopularRadioCandidateItem(object? item)
    {
        if (item == null)
        {
            return false;
        }

        var itemType = TryGetAnonymousString(item, TypeKey) ?? string.Empty;
        if (!itemType.Equals(PlaylistType, StringComparison.OrdinalIgnoreCase) &&
            !itemType.Equals(StationType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = TryGetAnonymousString(item, NameKey)
                   ?? TryGetAnonymousString(item, TitleKey)
                   ?? string.Empty;
        if (name.Contains("radio", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var uri = TryGetAnonymousString(item, UriKey) ?? string.Empty;
        return uri.StartsWith("spotify:station:", StringComparison.OrdinalIgnoreCase);
    }

    private static List<object> AddTrendingSection(IReadOnlyList<object> sections, object? trendingSection)
    {
        if (trendingSection == null || ContainsSectionTitle(sections, TrendingSongsTitle))
        {
            return sections.ToList();
        }

        var updated = new List<object> { trendingSection };
        updated.AddRange(sections);
        return updated;
    }

    private async Task<string?> ResolveDeezerUrlAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _songLinkResolver.ResolveByUrlAsync(url, cancellationToken);
            if (!string.IsNullOrWhiteSpace(result?.DeezerUrl))
            {
                return result.DeezerUrl;
            }

            if (!string.IsNullOrWhiteSpace(result?.Isrc))
            {
                try
                {
                    var track = await _deezerClient.GetTrackByIsrcAsync(result.Isrc);
                    if (track != null && !string.IsNullOrWhiteSpace(track.Id) && track.Id != "0")
                    {
                        return $"https://www.deezer.com/track/{track.Id}";
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Best effort.
                }
            }

            if (SpotifyMetadataService.TryParseSpotifyUrl(url, out var type, out var id)
                && string.Equals(type, AlbumType, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(id))
            {
                var albumUrl = $"https://open.spotify.com/album/{id}";
                var deezerAlbumUrl = await ResolveDeezerAlbumUrlAsync(albumUrl, cancellationToken);
                if (!string.IsNullOrWhiteSpace(deezerAlbumUrl))
                {
                    return deezerAlbumUrl;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best effort.
        }

        return null;
    }

    private async Task<string?> ResolveDeezerAlbumUrlAsync(string spotifyAlbumUrl, CancellationToken cancellationToken)
    {
        var metadata = await _pathfinderClient.FetchByUrlAsync(spotifyAlbumUrl, cancellationToken);
        if (metadata == null || !string.Equals(metadata.Type, AlbumType, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var deezerId = await _spotifyDeezerAlbumResolver.ResolveAlbumIdAsync(metadata, cancellationToken);
        return string.IsNullOrWhiteSpace(deezerId) ? null : $"https://www.deezer.com/album/{deezerId}";
    }

    public sealed class SpotifyHomeMapRequest
    {
        public List<string> Urls { get; set; } = new();
    }

    private static string? ResolveBrowseCategoryId(string? id, string? uri)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            return id.Trim();
        }

        if (string.IsNullOrWhiteSpace(uri))
        {
            return null;
        }

        if (Uri.TryCreate(uri, UriKind.Absolute, out var absolute))
        {
            var segments = absolute.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length > 0)
            {
                return segments[^1];
            }
        }

        if (uri.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = uri.Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
            {
                return parts[^1];
            }
        }

        return null;
    }

    private static (bool Success, string Greeting, List<object> Sections) ParseHomeFeed(JsonDocument doc)
    {
        if (!doc.RootElement.TryGetProperty(DataKey, out var dataElement) ||
            !dataElement.TryGetProperty(HomeKey, out var homeElement) ||
            homeElement.ValueKind != JsonValueKind.Object)
        {
            return (false, string.Empty, new List<object>());
        }

        var greeting = TryGetString(homeElement, GreetingKey, TextKey) ?? string.Empty;
        var sections = new List<object>();

        var sectionCandidates = TryGetSectionItemsElement(homeElement, out var sectionItemsElement)
            ? sectionItemsElement.EnumerateArray()
            : FindSectionCandidates(homeElement);

        foreach (var sectionItem in sectionCandidates)
        {
            if (!sectionItem.TryGetProperty(DataKey, out var sectionData) || sectionData.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var title = TryGetString(sectionData, TitleKey, TransformedLabelKey)
                        ?? TryGetString(sectionData, TitleKey, TextKey);
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var sectionUri = TryGetString(sectionItem, UriKey) ?? string.Empty;
            var items = ParseSectionItems(sectionItem);

            if (items.Count > 0)
            {
                sections.Add(new { uri = sectionUri, title, items });
            }
        }

        return (true, greeting, sections);
    }

    private static List<object> ParseSectionItems(JsonElement sectionItem)
    {
        var items = new List<object>();

        if (!sectionItem.TryGetProperty(SectionItemsKey, out var sectionItems) ||
            sectionItems.ValueKind != JsonValueKind.Object ||
            !sectionItems.TryGetProperty(ItemsKey, out var itemsElement) ||
            itemsElement.ValueKind != JsonValueKind.Array)
        {
            return items;
        }

        foreach (var item in itemsElement.EnumerateArray())
        {
            if (!TryGetContentData(item, out var contentData))
            {
                continue;
            }

            var candidates = ExpandContentCandidates(contentData);
            var identity = TryResolveHomeItemIdentity(item, candidates);
            if (identity is null)
            {
                continue;
            }

            var name = TryGetStringFromCandidates(candidates, "name")
                       ?? TryGetStringFromCandidates(candidates, TitleKey)
                       ?? TryGetStringFromCandidates(candidates, "episodeName")
                       ?? TryGetStringFromCandidates(candidates, ProfileKey, NameKey)
                       ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var metadata = BuildHomeItemMetadata(identity.ItemType, contentData, candidates);
            items.Add(new
            {
                id = identity.ItemId,
                uri = identity.Uri,
                type = identity.ItemType,
                name,
                artists = metadata.Artists,
                description = metadata.Description,
                coverUrl = metadata.CoverUrl,
                trackCount = metadata.TrackCount,
                followers = metadata.Followers,
                @public = metadata.IsPublic,
                collaborative = metadata.Collaborative,
                albumId = metadata.AlbumId,
                albumName = metadata.AlbumName,
                durationMs = metadata.DurationMs
            });
        }

        return items;
    }

    private static bool TryGetContentData(JsonElement item, out JsonElement contentData)
    {
        contentData = default;
        return item.TryGetProperty(ContentKey, out var content) &&
               content.ValueKind == JsonValueKind.Object &&
               content.TryGetProperty(DataKey, out contentData) &&
               contentData.ValueKind == JsonValueKind.Object;
    }

    private static HomeItemIdentity? TryResolveHomeItemIdentity(JsonElement item, IEnumerable<JsonElement> candidates)
    {
        var uri = TryResolveHomeItemUri(item, candidates);
        var (itemType, itemId) = TryParseSpotifyUri(uri);

        if (string.IsNullOrWhiteSpace(itemType) || string.IsNullOrWhiteSpace(itemId))
        {
            itemId = TryGetStringFromCandidates(candidates, IdKey)
                     ?? TryGetStringAtFromCandidates(candidates, ProfileKey, IdKey)
                     ?? TryGetStringAtFromCandidates(candidates, ProfileKey, DataKey, IdKey);
            itemType = NormalizeHomeItemType(
                TryGetStringFromCandidates(candidates, TypeKey)
                ?? TryGetStringFromCandidates(candidates, "__typename"));

            if (!string.IsNullOrWhiteSpace(itemType) &&
                SupportedHomeItemTypes.Contains(itemType) &&
                string.IsNullOrWhiteSpace(uri) &&
                !string.IsNullOrWhiteSpace(itemId))
            {
                uri = $"spotify:{itemType}:{itemId}";
            }
        }

        return string.IsNullOrWhiteSpace(itemType) || string.IsNullOrWhiteSpace(itemId) || string.IsNullOrWhiteSpace(uri)
            ? null
            : new HomeItemIdentity(itemType, itemId, uri);
    }

    private static string? TryResolveHomeItemUri(JsonElement item, IEnumerable<JsonElement> candidates)
    {
        return TryGetString(item, UriKey)
               ?? TryGetStringFromCandidates(candidates, UriKey)
               ?? TryGetStringAtFromCandidates(candidates, ProfileKey, UriKey)
               ?? TryGetStringAtFromCandidates(candidates, ProfileKey, DataKey, UriKey);
    }

    private static (string? ItemType, string? ItemId) TryParseSpotifyUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return (null, null);
        }

        var uriParts = uri.Split(':', StringSplitOptions.RemoveEmptyEntries);
        return uriParts.Length >= 3 ? (uriParts[1], uriParts[2]) : (null, null);
    }

    private static string? NormalizeHomeItemType(string? itemType)
    {
        if (string.IsNullOrWhiteSpace(itemType))
        {
            return null;
        }

        var normalized = itemType.Trim().ToLowerInvariant();
        if (normalized.Contains(EpisodeType, StringComparison.OrdinalIgnoreCase))
        {
            return EpisodeType;
        }

        if (normalized.Contains(ShowType, StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("podcast", StringComparison.OrdinalIgnoreCase))
        {
            return ShowType;
        }

        return normalized;
    }

    private static HomeItemMetadata BuildHomeItemMetadata(
        string itemType,
        JsonElement contentData,
        IEnumerable<JsonElement> candidates)
    {
        if (string.Equals(itemType, TrackType, StringComparison.OrdinalIgnoreCase))
        {
            return BuildTrackMetadata(contentData, candidates);
        }

        if (string.Equals(itemType, AlbumType, StringComparison.OrdinalIgnoreCase))
        {
            return new HomeItemMetadata(
                CoverUrl: TryGetStringAtFromCandidates(candidates, CoverArtKey, SourcesKey, 0, UrlKey),
                Artists: ExtractArtistsFromItems(contentData, ArtistsKey, ItemsKey)
                    ?? TryGetStringAtFromCandidates(candidates, ArtistsKey, 0, NameKey)
                    ?? TryGetStringFromCandidates(candidates, ArtistType, NameKey));
        }

        if (string.Equals(itemType, PlaylistType, StringComparison.OrdinalIgnoreCase))
        {
            return BuildPlaylistMetadata(candidates);
        }

        if (string.Equals(itemType, ArtistType, StringComparison.OrdinalIgnoreCase))
        {
            return new HomeItemMetadata(
                CoverUrl: TryGetStringAtFromCandidates(candidates, VisualsKey, AvatarImageKey, SourcesKey, 0, UrlKey)
                    ?? TryGetStringAtFromCandidates(candidates, AvatarImageKey, SourcesKey, 0, UrlKey)
                    ?? FindFirstImageUrl(contentData));
        }

        if (string.Equals(itemType, EpisodeType, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(itemType, ShowType, StringComparison.OrdinalIgnoreCase))
        {
            return new HomeItemMetadata(
                CoverUrl: TryGetStringAtFromCandidates(candidates, CoverArtKey, SourcesKey, 0, UrlKey)
                    ?? TryGetStringAtFromCandidates(candidates, "coverImage", SourcesKey, 0, UrlKey)
                    ?? TryGetStringAtFromCandidates(candidates, ImageKey, SourcesKey, 0, UrlKey)
                    ?? TryGetStringAtFromCandidates(candidates, VisualsKey, AvatarImageKey, SourcesKey, 0, UrlKey)
                    ?? FindFirstImageUrl(contentData),
                Artists: TryGetStringFromCandidates(candidates, "publisher")
                    ?? TryGetStringFromCandidates(candidates, "show", "name")
                    ?? TryGetStringFromCandidates(candidates, "podcast", "name"),
                Description: TryGetStringFromCandidates(candidates, DescriptionKey),
                DurationMs: TryGetIntAt(contentData, "duration", TotalMillisecondsKey)
                    ?? TryGetIntAt(contentData, "episodeDuration", TotalMillisecondsKey));
        }

        if (string.Equals(itemType, StationType, StringComparison.OrdinalIgnoreCase))
        {
            return new HomeItemMetadata(CoverUrl: TryGetStringAtFromCandidates(candidates, ImageKey, SourcesKey, 0, UrlKey));
        }

        return new HomeItemMetadata();
    }

    private static HomeItemMetadata BuildTrackMetadata(JsonElement contentData, IEnumerable<JsonElement> candidates)
    {
        var albumId = TryParseSpotifyUri(TryGetStringFromCandidates(candidates, AlbumOfTrackKey, UriKey)).ItemId;
        return new HomeItemMetadata(
            CoverUrl: TryGetStringAtFromCandidates(candidates, AlbumOfTrackKey, CoverArtKey, SourcesKey, 0, UrlKey),
            Artists: ExtractArtistsFromItems(contentData, ArtistsKey, ItemsKey)
                ?? ExtractArtistsFromItems(contentData, "firstArtist", ItemsKey),
            AlbumId: albumId,
            AlbumName: TryGetStringFromCandidates(candidates, AlbumOfTrackKey, NameKey),
            DurationMs: TryGetIntAt(contentData, "duration", TotalMillisecondsKey)
                ?? TryGetIntAt(contentData, "trackDuration", TotalMillisecondsKey));
    }

    private static HomeItemMetadata BuildPlaylistMetadata(IEnumerable<JsonElement> candidates)
    {
        return new HomeItemMetadata(
            CoverUrl: TryGetStringAtFromCandidates(candidates, ImagesKey, ItemsKey, 0, SourcesKey, 0, UrlKey),
            Artists: TryGetStringFromCandidates(candidates, "ownerV2", DataKey, "name"),
            Description: TryGetStringAtFromCandidates(candidates, CardRepresentationKey, SubtitleKey, TransformedLabelKey)
                ?? TryGetStringAtFromCandidates(candidates, CardRepresentationKey, SubtitleKey, TextKey)
                ?? TryGetStringAtFromCandidates(candidates, SubtitleKey, TransformedLabelKey)
                ?? TryGetStringAtFromCandidates(candidates, SubtitleKey, TextKey)
                ?? TryGetStringFromCandidates(candidates, DescriptionKey),
            TrackCount: TryGetIntAtFromCandidates(candidates, "tracks", TotalKey)
                ?? TryGetIntAtFromCandidates(candidates, TrackCountKey)
                ?? TryGetIntAtFromCandidates(candidates, "totalTracks")
                ?? TryGetIntAtFromCandidates(candidates, "attributes", TrackCountKey)
                ?? TryGetIntAtFromCandidates(candidates, "attributes", "totalTracks")
                ?? TryGetIntAtFromCandidates(candidates, "metrics", "tracks")
                ?? TryGetIntAtFromCandidates(candidates, "metadata", TrackCountKey),
            Followers: TryGetIntAtFromCandidates(candidates, FollowersKey, TotalKey)
                ?? TryGetIntAtFromCandidates(candidates, "followersCount")
                ?? TryGetIntAtFromCandidates(candidates, "metrics", FollowersKey)
                ?? TryGetIntAtFromCandidates(candidates, "metadata", FollowersKey)
                ?? TryGetIntAtFromCandidates(candidates, "ownerV2", DataKey, FollowersKey),
            IsPublic: TryGetBoolAtFromCandidates(candidates, PublicKey)
                ?? TryGetBoolAtFromCandidates(candidates, "isPublic"),
            Collaborative: TryGetBoolAtFromCandidates(candidates, CollaborativeKey)
                ?? TryGetBoolAtFromCandidates(candidates, "isCollaborative"));
    }

    private static bool TryGetSectionItemsElement(JsonElement homeElement, out JsonElement sectionItemsElement)
    {
        sectionItemsElement = default;
        return homeElement.TryGetProperty(SectionContainerKey, out var sectionContainer) &&
               sectionContainer.ValueKind == JsonValueKind.Object &&
               sectionContainer.TryGetProperty(SectionsKey, out var sectionsElement) &&
               sectionsElement.ValueKind == JsonValueKind.Object &&
               sectionsElement.TryGetProperty(ItemsKey, out sectionItemsElement) &&
               sectionItemsElement.ValueKind == JsonValueKind.Array;
    }

    private static IEnumerable<JsonElement> FindSectionCandidates(JsonElement root)
    {
        var stack = new Stack<JsonElement>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current.ValueKind == JsonValueKind.Object)
            {
                if (current.TryGetProperty(SectionItemsKey, out var sectionItems) &&
                    sectionItems.ValueKind == JsonValueKind.Object &&
                    sectionItems.TryGetProperty(ItemsKey, out var itemsElement) &&
                    itemsElement.ValueKind == JsonValueKind.Array)
                {
                    yield return current;
                }

                foreach (var prop in current.EnumerateObject())
                {
                    stack.Push(prop.Value);
                }
            }
            else if (current.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in current.EnumerateArray())
                {
                    stack.Push(item);
                }
            }
        }
    }

    private static List<object> ParseBrowseCategories(JsonDocument doc)
    {
        if (!TryGetBrowseItems(doc, out var itemsElement))
        {
            return new List<object>();
        }

        var categories = new List<object>();
        foreach (var section in itemsElement.EnumerateArray())
        {
            if (!TryGetBrowseCategorySection(section, out var sectionTitle, out var sectionItemList))
            {
                continue;
            }

            foreach (var item in sectionItemList.EnumerateArray())
            {
                var category = TryBuildBrowseCategory(sectionTitle, item);
                if (category is null)
                {
                    continue;
                }
                categories.Add(category);
            }
        }

        return categories;
    }

    private static List<object> ParseBrowsePageSections(JsonDocument doc)
    {
        if (!doc.RootElement.TryGetProperty(DataKey, out var dataElement) || dataElement.ValueKind != JsonValueKind.Object)
        {
            return new List<object>();
        }

        if (!TryResolveBrowseData(dataElement, out var browseData))
        {
            return new List<object>();
        }

        if (!TryResolveBrowseSections(browseData, out var itemsElement))
        {
            return new List<object>();
        }

        var sections = new List<object>();
        foreach (var sectionItem in itemsElement.EnumerateArray())
        {
            if (!sectionItem.TryGetProperty(DataKey, out var sectionData) || sectionData.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var title = TryGetString(sectionData, TitleKey, TransformedLabelKey)
                        ?? TryGetString(sectionData, TitleKey, "text");
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var sectionUri = TryGetString(sectionItem, "uri") ?? string.Empty;
            var items = ParseSectionItems(sectionItem);
            if (items.Count == 0)
            {
                continue;
            }

            sections.Add(new { uri = sectionUri, title, items });
        }

        return sections;
    }

    private static bool TryGetBrowseItems(JsonDocument doc, out JsonElement itemsElement)
    {
        itemsElement = default;
        return doc.RootElement.TryGetProperty(DataKey, out var dataElement)
               && dataElement.ValueKind == JsonValueKind.Object
               && TryResolveBrowseData(dataElement, out var browseData)
               && TryResolveBrowseSections(browseData, out itemsElement);
    }

    private static bool TryGetBrowseCategorySection(JsonElement section, out string? sectionTitle, out JsonElement sectionItems)
    {
        sectionTitle = null;
        sectionItems = default;

        if (!section.TryGetProperty(DataKey, out var sectionData) || sectionData.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        sectionTitle = TryGetString(sectionData, TitleKey, TransformedLabelKey)
                       ?? TryGetString(sectionData, TitleKey, "text");
        return section.TryGetProperty(SectionItemsKey, out var sectionItemsNode)
               && sectionItemsNode.ValueKind == JsonValueKind.Object
               && sectionItemsNode.TryGetProperty(ItemsKey, out sectionItems)
               && sectionItems.ValueKind == JsonValueKind.Array;
    }

    private static object? TryBuildBrowseCategory(string? sectionTitle, JsonElement item)
    {
        if (!TryGetContentData(item, out var contentData))
        {
            return null;
        }

        var candidates = ExpandContentCandidates(contentData).ToArray();
        var uri = TryGetStringFromCandidates(candidates, "uri")
                  ?? TryGetString(item, "uri")
                  ?? string.Empty;
        var name = ResolveBrowseCategoryName(candidates);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return new
        {
            id = ResolveBrowseCategoryIdFromCandidates(candidates, uri),
            uri,
            name,
            imageUrl = ResolveBrowseCategoryImageUrl(candidates, contentData),
            backgroundColor = TryGetStringAtFromCandidates(candidates, CardRepresentationKey, "backgroundColor", "hex"),
            section = sectionTitle
        };
    }

    private static string ResolveBrowseCategoryName(IEnumerable<JsonElement> candidates)
    {
        return TryGetStringAtFromCandidates(candidates, CardRepresentationKey, TitleKey, TransformedLabelKey)
               ?? TryGetStringAtFromCandidates(candidates, CardRepresentationKey, TitleKey, "text")
               ?? TryGetStringAtFromCandidates(candidates, TitleKey, TransformedLabelKey)
               ?? TryGetStringAtFromCandidates(candidates, TitleKey, "text")
               ?? TryGetStringFromCandidates(candidates, "name")
               ?? string.Empty;
    }

    private static string ResolveBrowseCategoryIdFromCandidates(IEnumerable<JsonElement> candidates, string uri)
    {
        var categoryId = TryGetStringFromCandidates(candidates, "id")
                         ?? TryGetStringFromCandidates(candidates, "categoryId")
                         ?? TryGetStringFromCandidates(candidates, "category_id")
                         ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(categoryId) || string.IsNullOrWhiteSpace(uri))
        {
            return categoryId;
        }

        var parts = uri.Split(':', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[^1] : string.Empty;
    }

    private static string? ResolveBrowseCategoryImageUrl(IEnumerable<JsonElement> candidates, JsonElement contentData)
    {
        return TryGetStringAtFromCandidates(candidates, CardRepresentationKey, "artwork", SourcesKey, 0, UrlKey)
               ?? TryGetStringAtFromCandidates(candidates, ImageKey, SourcesKey, 0, UrlKey)
               ?? TryGetStringAtFromCandidates(candidates, ImagesKey, ItemsKey, 0, SourcesKey, 0, UrlKey)
               ?? TryGetStringAtFromCandidates(candidates, CoverArtKey, SourcesKey, 0, UrlKey)
               ?? TryGetStringAtFromCandidates(candidates, "visuals", "avatarImage", SourcesKey, 0, UrlKey)
               ?? FindFirstImageUrl(contentData);
    }

    private static IEnumerable<JsonElement> ExpandContentCandidates(JsonElement contentData)
    {
        yield return contentData;

        if (contentData.ValueKind == JsonValueKind.Object &&
            contentData.TryGetProperty(DataKey, out var inner) &&
            inner.ValueKind == JsonValueKind.Object)
        {
            yield return inner;

            if (inner.TryGetProperty(DataKey, out var innerData) &&
                innerData.ValueKind == JsonValueKind.Object)
            {
                yield return innerData;
            }
        }
    }

    private static string? TryGetStringFromCandidates(IEnumerable<JsonElement> candidates, params string[] path)
    {
        return candidates
            .Select(candidate => TryGetString(candidate, path))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string? TryGetStringAtFromCandidates(IEnumerable<JsonElement> candidates, params object[] path)
    {
        return candidates
            .Select(candidate => TryGetStringAt(candidate, path))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static int? TryGetIntAtFromCandidates(IEnumerable<JsonElement> candidates, params object[] path)
    {
        return candidates
            .Select(candidate => TryGetIntAt(candidate, path))
            .FirstOrDefault(value => value.HasValue);
    }

    private static bool? TryGetBoolAtFromCandidates(IEnumerable<JsonElement> candidates, params object[] path)
    {
        return candidates
            .Select(candidate => TryGetBoolAt(candidate, path))
            .FirstOrDefault(value => value.HasValue);
    }

    private static List<object> BuildHomeFeedDiagnostics(JsonDocument doc)
    {
        var results = new List<object>();
        if (!doc.RootElement.TryGetProperty(DataKey, out var dataElement) ||
            !dataElement.TryGetProperty("home", out var homeElement) ||
            homeElement.ValueKind != JsonValueKind.Object)
        {
            return results;
        }

        if (!homeElement.TryGetProperty(SectionContainerKey, out var sectionContainer) ||
            sectionContainer.ValueKind != JsonValueKind.Object ||
            !sectionContainer.TryGetProperty(SectionsKey, out var sectionsElement) ||
            sectionsElement.ValueKind != JsonValueKind.Object ||
            !sectionsElement.TryGetProperty(ItemsKey, out var sectionItemsElement) ||
            sectionItemsElement.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var sectionItem in sectionItemsElement.EnumerateArray())
        {
            var title = TryGetString(sectionItem, DataKey, TitleKey, TransformedLabelKey)
                        ?? TryGetString(sectionItem, DataKey, TitleKey, "text")
                        ?? string.Empty;
            var details = DescribeHomeFeedSectionItems(sectionItem);

            results.Add(new
            {
                title,
                details.ItemsCount,
                details.ItemKeys,
                details.ContentKeys,
                details.ContentDataKeys,
                details.ContentDataInnerKeys
            });
        }

        return results;
    }

    private sealed record HomeFeedSectionDiagnosticDetails(
        int ItemsCount,
        string? ItemKeys,
        string? ContentKeys,
        string? ContentDataKeys,
        string? ContentDataInnerKeys);

    private static HomeFeedSectionDiagnosticDetails DescribeHomeFeedSectionItems(JsonElement sectionItem)
    {
        if (!TryGetProperty(sectionItem, SectionItemsKey, out var sectionItems)
            || !TryGetProperty(sectionItems, ItemsKey, out var itemsElement)
            || itemsElement.ValueKind != JsonValueKind.Array)
        {
            return new HomeFeedSectionDiagnosticDetails(0, null, null, null, null);
        }

        var itemsCount = itemsElement.GetArrayLength();
        if (itemsCount == 0)
        {
            return new HomeFeedSectionDiagnosticDetails(itemsCount, null, null, null, null);
        }

        var firstItem = itemsElement[0];
        var itemKeys = GetObjectKeys(firstItem);
        var contentKeys = TryGetProperty(firstItem, ContentKey, out var content) ? GetObjectKeys(content) : null;
        var contentDataKeys = TryGetProperty(content, DataKey, out var contentData) ? GetObjectKeys(contentData) : null;
        var contentDataInnerKeys = TryGetProperty(contentData, DataKey, out var inner) ? GetObjectKeys(inner) : null;
        return new HomeFeedSectionDiagnosticDetails(itemsCount, itemKeys, contentKeys, contentDataKeys, contentDataInnerKeys);
    }

    private async Task<object?> BuildHomeFeedAuthDiagnosticsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var isAuthenticated = HttpContext?.User?.Identity?.IsAuthenticated ?? false;
            var userId = _userContext.UserId;
            var userAuth = await ResolveHomeFeedUserAuthAsync(userId, cancellationToken);
            var platformAuth = await ResolveHomeFeedPlatformAuthAsync(cancellationToken);
            var resolvedAuth = ResolveHomeFeedBlobPathAsync(userAuth.BlobPath, platformAuth.BlobPath);

            SpotifyBlobService.SpotifyWebPlayerTokenInfo? tokenInfo = null;
            if (!string.IsNullOrWhiteSpace(resolvedAuth.BlobPath))
            {
                tokenInfo = await _blobService.GetWebPlayerTokenInfoAsync(resolvedAuth.BlobPath, cancellationToken);
                if (!isAuthenticated && tokenInfo is not null && tokenInfo.IsAnonymous == false)
                {
                    isAuthenticated = true;
                }
            }

            return new
            {
                isAuthenticated,
                userId,
                userActiveAccount = userAuth.ActiveAccount,
                platformActiveAccount = platformAuth.ActiveAccount,
                resolvedFrom = resolvedAuth.ResolvedFrom,
                resolvedBlobPath = resolvedAuth.BlobPath,
                tokenInfo = tokenInfo == null
                    ? null
                    : new
                    {
                        tokenInfo.IsAnonymous,
                        tokenInfo.Country,
                        tokenInfo.ClientId,
                        tokenInfo.Error
                    }
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Spotify home feed auth diagnostics failed.");
            return new { error = "auth_diagnostics_failed" };
        }
    }

    private sealed record HomeFeedResolvedAuth(string? BlobPath, string ResolvedFrom);

    private sealed record HomeFeedAccountAuthState(string? ActiveAccount, string? BlobPath);

    private async Task<HomeFeedAccountAuthState> ResolveHomeFeedUserAuthAsync(string? userId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return new HomeFeedAccountAuthState(null, null);
        }

        var userState = await _userAuthStore.LoadAsync(userId);
        var userBlob = SpotifyUserAuthStore.ResolveActiveWebPlayerBlobPath(userState);
        var validBlob = await ValidateHomeFeedBlobPathAsync(userBlob, cancellationToken);
        return new HomeFeedAccountAuthState(userState.ActiveAccount, validBlob);
    }

    private async Task<HomeFeedAccountAuthState> ResolveHomeFeedPlatformAuthAsync(CancellationToken cancellationToken)
    {
        var platformState = await _platformAuthService.LoadAsync();
        var activeAccount = platformState.Spotify?.ActiveAccount;
        var blobPath = platformState.Spotify?.Accounts
            .FirstOrDefault(a => a.Name.Equals(activeAccount ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            ?.WebPlayerBlobPath;
        var validBlob = await ValidateHomeFeedBlobPathAsync(blobPath, cancellationToken);
        return new HomeFeedAccountAuthState(activeAccount, validBlob);
    }

    private static HomeFeedResolvedAuth ResolveHomeFeedBlobPathAsync(
        string? userBlobPath,
        string? platformBlobPath)
    {
        if (!string.IsNullOrWhiteSpace(userBlobPath))
        {
            return new HomeFeedResolvedAuth(userBlobPath, "user");
        }

        if (!string.IsNullOrWhiteSpace(platformBlobPath))
        {
            return new HomeFeedResolvedAuth(platformBlobPath, "platform");
        }

        return new HomeFeedResolvedAuth(null, "none");
    }

    private async Task<string?> ValidateHomeFeedBlobPathAsync(string? blobPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(blobPath))
        {
            return null;
        }

        return await _blobService.IsWebPlayerBlobAsync(blobPath, cancellationToken)
            ? blobPath
            : null;
    }

    private static string GetObjectKeys(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        return string.Join(",", element.EnumerateObject().Select(p => p.Name));
    }

    private static bool TryResolveBrowseSections(JsonElement browseData, out JsonElement itemsElement)
    {
        itemsElement = default;
        if (browseData.TryGetProperty(SectionsKey, out var sectionsElement))
        {
            if (sectionsElement.ValueKind == JsonValueKind.Object &&
                sectionsElement.TryGetProperty(ItemsKey, out itemsElement) &&
                itemsElement.ValueKind == JsonValueKind.Array)
            {
                return true;
            }

            if (sectionsElement.ValueKind == JsonValueKind.Array)
            {
                itemsElement = sectionsElement;
                return true;
            }
        }

        if (browseData.TryGetProperty(SectionContainerKey, out var container) &&
            container.ValueKind == JsonValueKind.Object &&
            container.TryGetProperty(SectionsKey, out var containerSections) &&
            containerSections.ValueKind == JsonValueKind.Object &&
            containerSections.TryGetProperty(ItemsKey, out itemsElement) &&
            itemsElement.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        return false;
    }

    private static bool TryResolveBrowseData(JsonElement dataElement, out JsonElement browseData)
    {
        browseData = default;
        foreach (var key in new[] { "browseV2", "browse", "browseAll", "browseStart" })
        {
            if (TryResolveNamedBrowseData(dataElement, key, out browseData))
            {
                return true;
            }
        }

        return TryResolveFallbackBrowseData(dataElement, out browseData);
    }

    private static bool TryResolveNamedBrowseData(JsonElement dataElement, string propertyName, out JsonElement browseData)
    {
        browseData = default;
        if (!dataElement.TryGetProperty(propertyName, out var candidate) || candidate.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (candidate.TryGetProperty(DataKey, out browseData) && browseData.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        browseData = candidate;
        return true;
    }

    private static bool TryResolveFallbackBrowseData(JsonElement dataElement, out JsonElement browseData)
    {
        browseData = default;
        foreach (var property in dataElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object ||
                !property.Name.Contains("browse", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.TryGetProperty(DataKey, out browseData) && browseData.ValueKind == JsonValueKind.Object)
            {
                return true;
            }

            browseData = property.Value;
            return true;
        }

        return false;
    }

    private static string? BuildBrowsePageUri(string? categoryId, string? uri)
    {
        if (!string.IsNullOrWhiteSpace(uri))
        {
            return uri;
        }

        if (string.IsNullOrWhiteSpace(categoryId))
        {
            return null;
        }

        return $"spotify:page:{categoryId.Trim()}";
    }

    private static string? FindFirstUriByPrefix(JsonElement element, string prefix)
    {
        foreach (var nested in EnumerateChildElements(element))
        {
            var directMatch = TryReadUriWithPrefix(nested, prefix);
            if (!string.IsNullOrWhiteSpace(directMatch))
            {
                return directMatch;
            }

            var descendantMatch = FindFirstUriByPrefix(nested, prefix);
            if (!string.IsNullOrWhiteSpace(descendantMatch))
            {
                return descendantMatch;
            }
        }

        return null;
    }

    private static List<string> FindUrisByPrefix(JsonElement element, string prefix)
    {
        var results = new List<string>();
        foreach (var nested in EnumerateChildElements(element))
        {
            var directMatch = TryReadUriWithPrefix(nested, prefix);
            if (!string.IsNullOrWhiteSpace(directMatch))
            {
                results.Add(directMatch);
            }

            results.AddRange(FindUrisByPrefix(nested, prefix));
        }

        return results.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<(JsonDocument? Document, bool ContainsFullList)> FetchFirstPlaylistSectionAsync(
        JsonDocument pageDoc,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        var directItems = ParseBrowsePlaylistItems(pageDoc);
        if (directItems.Count > 0)
        {
            return (pageDoc, true);
        }

        var sectionUris = FindUrisByPrefix(pageDoc.RootElement, "spotify:section:");
        foreach (var sectionUri in sectionUris)
        {
            var sectionDoc = await _pathfinderClient.FetchBrowseSectionWithBlobAsync(sectionUri, offset, limit, cancellationToken);
            if (sectionDoc is null)
            {
                continue;
            }

            var items = ParseBrowsePlaylistItems(sectionDoc);
            if (items.Count > 0)
            {
                return (sectionDoc, false);
            }
        }

        return (null, false);
    }

    private static List<object> DeduplicateBrowseItems(List<object> items)
    {
        var deduplicated = new List<object>(items.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (item is null)
            {
                continue;
            }

            var uri = TryGetAnonymousString(item, UriKey);
            var key = !string.IsNullOrWhiteSpace(uri)
                ? uri
                : $"{TryGetAnonymousString(item, TypeKey)}:{TryGetAnonymousString(item, IdKey)}:{TryGetAnonymousString(item, NameKey)}";
            if (!seen.Add(key))
            {
                continue;
            }

            deduplicated.Add(item);
        }

        return deduplicated;
    }

    private async Task<object?> TryFetchTrendingSongsSectionAsync(CancellationToken cancellationToken)
    {
        if (!SpotifyBrowseEnabled)
        {
            return null;
        }

        var trendingFromUri = await TryFetchTrendingSongsSectionByUriAsync(cancellationToken);
        if (trendingFromUri != null)
        {
            return trendingFromUri;
        }

        var browseDoc = await _pathfinderClient.FetchBrowseAllWithBlobAsync(cancellationToken);
        if (browseDoc is null)
        {
            return null;
        }

        var categories = ParseBrowseCategories(browseDoc);
        if (categories.Count == 0)
        {
            return null;
        }

        return await FindTrendingSectionFromBrowseCategoriesAsync(categories, cancellationToken);
    }

    private async Task<object?> TryFetchTrendingSongsSectionByUriAsync(CancellationToken cancellationToken)
    {
        var sectionDoc = await _pathfinderClient.FetchBrowseSectionWithBlobAsync(
            TrendingSongsSectionUri,
            0,
            20,
            cancellationToken);
        if (sectionDoc is null)
        {
            return null;
        }

        var items = ParseBrowseSectionTrackItems(sectionDoc);
        if (items.Count == 0)
        {
            return null;
        }

        return new
        {
            uri = TrendingSongsSectionUri,
            title = TrendingSongsTitle,
            items
        };
    }

    private static bool ContainsSectionTitle(IEnumerable<object> sections, string title)
    {
        return sections.Any(section =>
        {
            var current = TryGetAnonymousTitle(section);
            return !string.IsNullOrWhiteSpace(current) &&
                   current.Equals(title, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static string? ReadAnonymousString(object obj, string name)
    {
        var prop = obj.GetType().GetProperty(name);
        return prop?.GetValue(obj) as string;
    }

    private static int? TryGetBrowseTotal(JsonElement root)
    {
        var dataElement = root.ValueKind == JsonValueKind.Object && root.TryGetProperty(DataKey, out var data)
            ? data
            : root;

        var total = TryGetIntAt(dataElement, "browseSection", SectionItemsKey, "pagingInfo", TotalKey);
        if (total.HasValue)
        {
            return total;
        }

        total = TryGetIntAt(dataElement, "browseSection", SectionItemsKey, "paging", TotalKey);
        if (total.HasValue)
        {
            return total;
        }

        return null;
    }

    private static List<TrendingBrowseCandidate> BuildTrendingBrowseCandidates(IEnumerable<object> categories)
    {
        return categories
            .Select(category => new TrendingBrowseCandidate(
                ReadAnonymousString(category, "id"),
                ReadAnonymousString(category, "uri"),
                ReadAnonymousString(category, "name") ?? string.Empty))
            .Where(static candidate => IsTrendingBrowseCandidate(candidate))
            .OrderByDescending(static candidate => candidate.Name.Contains("Charts", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(static candidate => candidate.Name.Contains("Trending", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(static candidate => candidate.Name.Contains("Top", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static bool IsTrendingBrowseCandidate(TrendingBrowseCandidate candidate)
    {
        return string.Equals(candidate.Id, "charts", StringComparison.OrdinalIgnoreCase)
               || string.Equals(candidate.Id, "toplists", StringComparison.OrdinalIgnoreCase)
               || candidate.Name.Contains("Charts", StringComparison.OrdinalIgnoreCase)
               || candidate.Name.Contains("Top", StringComparison.OrdinalIgnoreCase)
               || candidate.Name.Contains("Trending", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<object?> FindTrendingSectionFromBrowseCategoriesAsync(
        IEnumerable<object> categories,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in BuildTrendingBrowseCandidates(categories))
        {
            var section = await TryFetchTrendingSectionFromCategoryAsync(candidate, cancellationToken);
            if (section != null)
            {
                return section;
            }
        }

        return null;
    }

    private async Task<object?> TryFetchTrendingSectionFromCategoryAsync(
        TrendingBrowseCandidate candidate,
        CancellationToken cancellationToken)
    {
        var pageUri = BuildBrowsePageUri(candidate.Id, candidate.Uri);
        if (string.IsNullOrWhiteSpace(pageUri))
        {
            return null;
        }

        var pageDoc = await _pathfinderClient.FetchBrowsePageWithBlobAsync(pageUri, 0, 20, 0, 20, cancellationToken);
        if (pageDoc is null)
        {
            return null;
        }

        return FindTrendingSection(ParseBrowsePageSections(pageDoc));
    }

    private static object? FindTrendingSection(IEnumerable<object> sections)
    {
        return sections.FirstOrDefault(section => TitleMatches(section, TrendingSongsTitle))
               ?? sections.FirstOrDefault(section => TitleContains(section, "Trending"));
    }

    private static bool TitleMatches(object section, string expectedTitle)
    {
        var title = TryGetAnonymousTitle(section) ?? string.Empty;
        return title.Equals(expectedTitle, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TitleContains(object section, string expectedToken)
    {
        var title = TryGetAnonymousTitle(section) ?? string.Empty;
        return title.Contains(expectedToken, StringComparison.OrdinalIgnoreCase);
    }

    private static List<object> ParseBrowsePlaylistItems(JsonDocument doc)
    {
        if (!doc.RootElement.TryGetProperty(DataKey, out var dataElement))
        {
            return new List<object>();
        }

        var items = new List<object>();

        foreach (var sectionItems in EnumerateSectionItems(dataElement))
        {
            foreach (var item in sectionItems.EnumerateArray())
            {
                if (!TryGetContentData(item, out var contentData))
                {
                    continue;
                }

                var candidates = ExpandContentCandidates(contentData);
                var playlistItem = TryBuildBrowsePlaylistItem(item, contentData, candidates);
                if (playlistItem is null)
                {
                    continue;
                }
                items.Add(playlistItem);
            }
        }

        return items;
    }

    private static List<object> ParseBrowseSectionTrackItems(JsonDocument doc)
    {
        if (!doc.RootElement.TryGetProperty(DataKey, out var dataElement))
        {
            return new List<object>();
        }

        var items = new List<object>();

        foreach (var sectionItems in EnumerateSectionItems(dataElement))
        {
            foreach (var item in sectionItems.EnumerateArray())
            {
                if (!TryGetContentData(item, out var contentData))
                {
                    continue;
                }

                var candidates = ExpandContentCandidates(contentData);
                var trackItem = TryBuildBrowseSectionTrackItem(item, contentData, candidates);
                if (trackItem is null)
                {
                    continue;
                }
                items.Add(trackItem);
            }
        }

        return items;
    }

    private static IEnumerable<JsonElement> EnumerateSectionItems(JsonElement element)
    {
        if (TryGetSectionItemsArray(element, out var items))
        {
            yield return items;
        }

        foreach (var nested in EnumerateChildElements(element))
        {
            foreach (var childItems in EnumerateSectionItems(nested))
            {
                yield return childItems;
            }
        }
    }

    private static object? TryBuildBrowsePlaylistItem(
        JsonElement item,
        JsonElement contentData,
        IEnumerable<JsonElement> candidates)
    {
        var uri = TryGetStringFromCandidates(candidates, UriKey)
                  ?? TryGetString(item, UriKey)
                  ?? string.Empty;
        var (itemType, _) = TryParseSpotifyUri(uri);
        if (string.IsNullOrWhiteSpace(uri) ||
            !uri.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(itemType ?? PlaylistType, PlaylistType, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var name = TryGetStringAtFromCandidates(candidates, CardRepresentationKey, TitleKey, TransformedLabelKey)
                   ?? TryGetStringAtFromCandidates(candidates, CardRepresentationKey, TitleKey, TextKey)
                   ?? TryGetStringFromCandidates(candidates, NameKey)
                   ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return new
        {
            name,
            description = TryGetStringAtFromCandidates(candidates, CardRepresentationKey, SubtitleKey, TransformedLabelKey)
                ?? TryGetStringAtFromCandidates(candidates, CardRepresentationKey, SubtitleKey, TextKey)
                ?? TryGetStringAtFromCandidates(candidates, SubtitleKey, TransformedLabelKey)
                ?? TryGetStringAtFromCandidates(candidates, SubtitleKey, TextKey)
                ?? TryGetStringFromCandidates(candidates, DescriptionKey)
                ?? string.Empty,
            imageUrl = TryGetStringAtFromCandidates(candidates, CardRepresentationKey, "artwork", SourcesKey, 0, UrlKey)
                ?? TryGetStringAtFromCandidates(candidates, "artwork", SourcesKey, 0, UrlKey)
                ?? TryGetStringAtFromCandidates(candidates, ImageKey, SourcesKey, 0, UrlKey)
                ?? TryGetStringAtFromCandidates(candidates, ImagesKey, ItemsKey, 0, SourcesKey, 0, UrlKey)
                ?? TryGetStringAtFromCandidates(candidates, CoverArtKey, SourcesKey, 0, UrlKey)
                ?? TryGetStringAtFromCandidates(candidates, VisualsKey, AvatarImageKey, SourcesKey, 0, UrlKey)
                ?? FindFirstImageUrl(contentData),
            type = PlaylistType,
            uri
        };
    }

    private static object? TryBuildBrowseSectionTrackItem(
        JsonElement item,
        JsonElement contentData,
        IEnumerable<JsonElement> candidates)
    {
        var uri = TryGetString(item, UriKey)
                  ?? TryGetStringFromCandidates(candidates, UriKey)
                  ?? TryGetStringAtFromCandidates(candidates, ProfileKey, UriKey)
                  ?? TryGetStringAtFromCandidates(candidates, ProfileKey, DataKey, UriKey);
        var (itemType, itemId) = TryParseSpotifyUri(uri);
        if (!string.Equals(itemType, TrackType, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(itemId))
        {
            return null;
        }

        var name = TryGetStringFromCandidates(candidates, NameKey)
                   ?? TryGetStringFromCandidates(candidates, ProfileKey, NameKey)
                   ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var metadata = BuildTrackMetadata(contentData, candidates);
        return new
        {
            id = itemId,
            uri = uri ?? $"spotify:track:{itemId}",
            type = TrackType,
            name,
            artists = metadata.Artists,
            description = default(string?),
            coverUrl = metadata.CoverUrl,
            albumId = metadata.AlbumId,
            albumName = metadata.AlbumName,
            durationMs = metadata.DurationMs
        };
    }

    private static string BuildBrowseShapeSummary(JsonDocument doc)
    {
        try
        {
            var root = doc.RootElement;
            var hasData = root.TryGetProperty(DataKey, out var dataElement);
            JsonElement browseV2 = default;
            var hasBrowseV2 = hasData && dataElement.TryGetProperty("browseV2", out browseV2) && browseV2.ValueKind == JsonValueKind.Object;
            JsonElement browseData = default;
            var hasBrowseData = hasBrowseV2 && browseV2.TryGetProperty(DataKey, out browseData) && browseData.ValueKind == JsonValueKind.Object;
            JsonElement sections = default;
            var hasSections = hasBrowseData && browseData.TryGetProperty(SectionsKey, out sections) && sections.ValueKind == JsonValueKind.Object;
            JsonElement items = default;
            var hasItems = hasSections && sections.TryGetProperty(ItemsKey, out items) && items.ValueKind == JsonValueKind.Array;
            var itemCount = hasItems ? items.GetArrayLength() : 0;
            var dataKeys = hasData && dataElement.ValueKind == JsonValueKind.Object
                ? string.Join(",", dataElement.EnumerateObject().Select(p => p.Name).Take(8))
                : "none";
            var hasErrors = root.TryGetProperty("errors", out var errorsElement) && errorsElement.ValueKind == JsonValueKind.Array;
            var browseStartKeys = dataElement.TryGetProperty("browseStart", out var browseStart) && browseStart.ValueKind == JsonValueKind.Object
                ? string.Join(",", browseStart.EnumerateObject().Select(p => p.Name).Take(8))
                : "none";
            var browseStartHasSections = browseStart.ValueKind == JsonValueKind.Object &&
                (browseStart.TryGetProperty(SectionsKey, out _) || browseStart.TryGetProperty(SectionContainerKey, out _));
            return $"shape=data:{hasData} browseV2:{hasBrowseV2} dataNode:{hasBrowseData} sections:{hasSections} items:{hasItems} itemCount:{itemCount} dataKeys:{dataKeys} browseStartKeys:{browseStartKeys} browseStartHasSections:{browseStartHasSections} errors:{hasErrors}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "shape=unknown";
        }
    }

    private static string BuildBrowseDiagnostics(JsonDocument doc)
    {
        try
        {
            if (!doc.RootElement.TryGetProperty(DataKey, out var dataElement) || dataElement.ValueKind != JsonValueKind.Object)
            {
                return "diag=nodata";
            }

            if (!TryResolveBrowseData(dataElement, out var browseData))
            {
                var dataKeys = string.Join(",", dataElement.EnumerateObject().Select(p => p.Name).Take(12));
                return $"diag=browseData:missing dataKeys:{dataKeys}";
            }

            var browseKeys = browseData.ValueKind == JsonValueKind.Object
                ? string.Join(",", browseData.EnumerateObject().Select(p => p.Name).Take(12))
                : browseData.ValueKind.ToString();

            if (!TryResolveBrowseSections(browseData, out var itemsElement))
            {
                return $"diag=sections:missing browseKeys:{browseKeys}";
            }

            var sectionCount = itemsElement.ValueKind == JsonValueKind.Array ? itemsElement.GetArrayLength() : 0;
            if (sectionCount == 0)
            {
                return $"diag=sections:empty browseKeys:{browseKeys}";
            }

            var firstSection = itemsElement[0];
            var firstKeys = GetLimitedObjectKeys(firstSection);
            var sectionDataKeys = firstSection.ValueKind == JsonValueKind.Object &&
                                  firstSection.TryGetProperty(DataKey, out var sectionData) &&
                                  sectionData.ValueKind == JsonValueKind.Object
                ? GetLimitedObjectKeys(sectionData)
                : string.Empty;
            var sectionDetails = DescribeBrowseSectionItems(firstSection);
            var firstItemDetails = DescribeBrowseFirstItem(firstSection);

            return $"diag=browseKeys:{browseKeys} sectionCount:{sectionCount} firstKeys:{firstKeys} sectionDataKeys:{sectionDataKeys} sectionItemsKeys:{sectionDetails.SectionItemsKeys} sectionItemsCount:{sectionDetails.SectionItemsCount} itemKeys:{firstItemDetails.ItemKeys} contentDataKeys:{firstItemDetails.ContentDataKeys} contentDataInnerKeys:{firstItemDetails.ContentDataInnerKeys} cardRepKeys:{firstItemDetails.CardRepKeys} cardRepTitleKeys:{firstItemDetails.CardRepTitleKeys} contentDataInnerDataKeys:{firstItemDetails.ContentDataInnerDataKeys}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "diag=error";
        }
    }

    private sealed record BrowseSectionDetails(string SectionItemsKeys, string SectionItemsCount);

    private sealed record BrowseFirstItemDetails(
        string ItemKeys,
        string ContentDataKeys,
        string ContentDataInnerKeys,
        string ContentDataInnerDataKeys,
        string CardRepKeys,
        string CardRepTitleKeys);

    private static string GetLimitedObjectKeys(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Object
            ? string.Join(",", element.EnumerateObject().Select(p => p.Name).Take(12))
            : element.ValueKind.ToString();
    }

    private static BrowseSectionDetails DescribeBrowseSectionItems(JsonElement firstSection)
    {
        if (firstSection.ValueKind == JsonValueKind.Object &&
            firstSection.TryGetProperty(SectionItemsKey, out var sectionItems) &&
            sectionItems.ValueKind == JsonValueKind.Object)
        {
            var count = sectionItems.TryGetProperty(ItemsKey, out var sectionItemList) && sectionItemList.ValueKind == JsonValueKind.Array
                ? sectionItemList.GetArrayLength().ToString()
                : string.Empty;
            return new BrowseSectionDetails(GetLimitedObjectKeys(sectionItems), count);
        }

        if (firstSection.ValueKind == JsonValueKind.Object &&
            firstSection.TryGetProperty(ItemsKey, out var items) &&
            items.ValueKind == JsonValueKind.Array)
        {
            return new BrowseSectionDetails(string.Empty, $"{items.GetArrayLength()}(items)");
        }

        return new BrowseSectionDetails(string.Empty, string.Empty);
    }

    private static BrowseFirstItemDetails DescribeBrowseFirstItem(JsonElement firstSection)
    {
        if (!TryResolveFirstBrowseItem(firstSection, out var firstItem))
        {
            return new BrowseFirstItemDetails(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        }

        var itemKeys = GetLimitedObjectKeys(firstItem);
        if (!firstItem.TryGetProperty(ContentKey, out var content) ||
            content.ValueKind != JsonValueKind.Object ||
            !content.TryGetProperty(DataKey, out var contentData) ||
            contentData.ValueKind != JsonValueKind.Object)
        {
            return new BrowseFirstItemDetails(itemKeys, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        }

        var contentDataKeys = GetLimitedObjectKeys(contentData);
        if (!contentData.TryGetProperty(DataKey, out var inner) || inner.ValueKind != JsonValueKind.Object)
        {
            return new BrowseFirstItemDetails(itemKeys, contentDataKeys, string.Empty, string.Empty, string.Empty, string.Empty);
        }

        var contentDataInnerKeys = GetLimitedObjectKeys(inner);
        var cardRepKeys = inner.TryGetProperty("cardRepresentation", out var cardRep) && cardRep.ValueKind == JsonValueKind.Object
            ? GetLimitedObjectKeys(cardRep)
            : string.Empty;
        var cardRepTitleKeys = cardRepKeys.Length > 0 &&
                               cardRep.TryGetProperty("title", out var cardRepTitle) &&
                               cardRepTitle.ValueKind == JsonValueKind.Object
            ? GetLimitedObjectKeys(cardRepTitle)
            : string.Empty;
        var contentDataInnerDataKeys = inner.TryGetProperty(DataKey, out var innerData) && innerData.ValueKind == JsonValueKind.Object
            ? GetLimitedObjectKeys(innerData)
            : string.Empty;

        return new BrowseFirstItemDetails(itemKeys, contentDataKeys, contentDataInnerKeys, contentDataInnerDataKeys, cardRepKeys, cardRepTitleKeys);
    }

    private static bool TryResolveFirstBrowseItem(JsonElement firstSection, out JsonElement firstItem)
    {
        firstItem = default;
        if (firstSection.ValueKind == JsonValueKind.Object &&
            firstSection.TryGetProperty(SectionItemsKey, out var sectionItemsForItem) &&
            sectionItemsForItem.ValueKind == JsonValueKind.Object &&
            sectionItemsForItem.TryGetProperty(ItemsKey, out var sectionItemsList) &&
            sectionItemsList.ValueKind == JsonValueKind.Array &&
            sectionItemsList.GetArrayLength() > 0)
        {
            firstItem = sectionItemsList[0];
            return true;
        }

        if (firstSection.ValueKind == JsonValueKind.Object &&
            firstSection.TryGetProperty(ItemsKey, out var itemsAlt) &&
            itemsAlt.ValueKind == JsonValueKind.Array &&
            itemsAlt.GetArrayLength() > 0)
        {
            firstItem = itemsAlt[0];
            return true;
        }

        return false;
    }

    private static List<object> MapHomeSections(IEnumerable<object> sections)
    {
        var result = new List<object>();
        foreach (var section in sections)
        {
            if (!TryMapHomeSection(section, out var mappedSection))
            {
                continue;
            }

            result.Add(mappedSection);
        }

        return result;
    }

    private static bool TryMapHomeSection(object section, out object mappedSection)
    {
        mappedSection = null!;
        var title = TryGetAnonymousTitle(section);
        if (string.IsNullOrWhiteSpace(title) || ContainsAnyToken(title, PersonalSectionKeywords))
        {
            return false;
        }

        var items = TryGetAnonymousItems(section);
        if (items == null || items.Count == 0)
        {
            return false;
        }

        var mappedItems = items
            .Select(item => MapHomeSectionItem(title, item))
            .Where(item => item is not null)
            .Cast<object>()
            .ToList();
        if (mappedItems.Count == 0)
        {
            return false;
        }

        var preserveAllItems = TryGetAnonymousBool(section, "__preserveAllItems") == true
                               || TryGetAnonymousBool(section, "preserveAllItems") == true;
        var pagePath = TryGetAnonymousString(section, "pagePath") ?? string.Empty;
        var hasMore = TryGetAnonymousBool(section, "hasMore") ?? false;
        mappedSection = new
        {
            source = SpotifySource,
            title,
            layout = "row",
            pagePath,
            hasMore,
            __preserveAllItems = preserveAllItems,
            preserveAllItems,
            items = mappedItems
        };
        return true;
    }

    private static object? MapHomeSectionItem(string sectionTitle, object? item)
    {
        if (item == null)
        {
            return null;
        }

        var itemType = TryGetAnonymousString(item, "type") ?? PlaylistType;
        var itemId = TryGetAnonymousString(item, "id") ?? string.Empty;
        var itemUri = ResolveMappedItemUri(item, itemType, itemId);
        var name = TryGetAnonymousString(item, "name") ?? string.Empty;
        var artists = TryGetAnonymousString(item, ArtistsKey);
        var description = TryGetAnonymousString(item, DescriptionKey);
        if (IsPersonalSpotifyHomeItem(sectionTitle, itemType, name, artists, description, itemUri))
        {
            return null;
        }

        var coverUrl = TryGetAnonymousString(item, "coverUrl");
        var albumName = TryGetAnonymousString(item, "albumName") ?? TryGetAnonymousString(item, AlbumKey);
        var durationMs = TryGetAnonymousInt(item, "durationMs");
        var trackCount = TryGetAnonymousInt(item, TrackCountKey)
                         ?? TryGetAnonymousInt(item, "nb_tracks")
                         ?? TryGetAnonymousInt(item, "track_count");
        var followers = TryGetAnonymousInt(item, FollowersKey) ?? TryGetAnonymousInt(item, "fans");
        var isPublic = TryGetAnonymousBool(item, PublicKey) ?? TryGetAnonymousBool(item, "isPublic");
        var collaborative = TryGetAnonymousBool(item, CollaborativeKey) ?? TryGetAnonymousBool(item, "isCollaborative");
        var normalizedArtists = NormalizeMappedArtists(itemType, artists);
        var normalizedDescription = NormalizeMappedDescription(description);
        var subtitle = BuildMappedSubtitle(itemType, normalizedArtists, normalizedDescription, trackCount, followers);

        return new
        {
            source = SpotifySource,
            id = itemId,
            uri = itemUri,
            type = itemType,
            name,
            title = name,
            artists = normalizedArtists,
            subtitle,
            description = normalizedDescription,
            coverUrl,
            albumName,
            durationMs,
            trackCount,
            nb_tracks = trackCount,
            followers,
            fans = followers,
            @public = isPublic,
            collaborative,
            image = coverUrl
        };
    }

    private static string ResolveMappedItemUri(object item, string itemType, string itemId)
    {
        var itemUri = TryGetAnonymousString(item, "uri") ?? string.Empty;
        return string.IsNullOrWhiteSpace(itemUri) && !string.IsNullOrWhiteSpace(itemType) && !string.IsNullOrWhiteSpace(itemId)
            ? $"spotify:{itemType}:{itemId}"
            : itemUri;
    }

    private async Task<List<object>> MapHomeSectionsAsync(IEnumerable<object> sections, CancellationToken cancellationToken)
    {
        var mapped = MapHomeSections(sections);
        if (mapped.Count == 0)
        {
            return mapped;
        }

        return await EnrichSpotifyPlaylistStatsAsync(mapped, cancellationToken);
    }

    private async Task<List<object>> EnrichSpotifyPlaylistStatsAsync(List<object> mappedSections, CancellationToken cancellationToken)
    {
        const int maxItemsPerSectionToEnrich = 64;
        const int maxPlaylistMetadataLookups = 256;
        const int maxArtistMetadataLookups = 64;
        var limits = new MetadataEnrichmentLimits(
            maxItemsPerSectionToEnrich,
            maxPlaylistMetadataLookups,
            maxArtistMetadataLookups);
        using var metadataThrottle = new SemaphoreSlim(8, 8);

        var playlistLookupTasks = new Dictionary<string, Task<SpotifyUrlMetadata?>>(StringComparer.OrdinalIgnoreCase);
        var artistLookupTasks = new Dictionary<string, Task<int?>>(StringComparer.OrdinalIgnoreCase);
        CollectSectionMetadataTasks(
            mappedSections,
            playlistLookupTasks,
            artistLookupTasks,
            metadataThrottle,
            limits,
            cancellationToken);

        var metadataTasks = new List<Task>(playlistLookupTasks.Count + artistLookupTasks.Count);
        metadataTasks.AddRange(playlistLookupTasks.Values);
        metadataTasks.AddRange(artistLookupTasks.Values);
        if (metadataTasks.Count > 0)
        {
            await Task.WhenAll(metadataTasks);
        }

        var enrichedSections = new List<object>(mappedSections.Count);
        foreach (var section in mappedSections)
        {
            var enrichedSection = await TryBuildEnrichedSectionAsync(section, playlistLookupTasks, artistLookupTasks);
            if (enrichedSection != null)
            {
                enrichedSections.Add(enrichedSection);
            }
        }

        return enrichedSections;
    }

    private void CollectSectionMetadataTasks(
        IEnumerable<object> mappedSections,
        Dictionary<string, Task<SpotifyUrlMetadata?>> playlistLookupTasks,
        Dictionary<string, Task<int?>> artistLookupTasks,
        SemaphoreSlim metadataThrottle,
        MetadataEnrichmentLimits limits,
        CancellationToken cancellationToken)
    {
        foreach (var section in mappedSections)
        {
            var items = TryGetAnonymousItems(section);
            if (items == null || items.Count == 0)
            {
                continue;
            }

            var inspectCount = Math.Min(items.Count, limits.MaxItemsPerSectionToEnrich);
            for (var index = 0; index < inspectCount; index++)
            {
                QueueHomeItemMetadataTask(
                    items[index],
                    playlistLookupTasks,
                    artistLookupTasks,
                    metadataThrottle,
                    limits,
                    cancellationToken);
            }
        }
    }

    private void QueueHomeItemMetadataTask(
        object? item,
        Dictionary<string, Task<SpotifyUrlMetadata?>> playlistLookupTasks,
        Dictionary<string, Task<int?>> artistLookupTasks,
        SemaphoreSlim metadataThrottle,
        MetadataEnrichmentLimits limits,
        CancellationToken cancellationToken)
    {
        if (item == null || !string.Equals(TryGetAnonymousString(item, "source"), SpotifySource, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var itemType = TryGetAnonymousString(item, "type");
        if (string.Equals(itemType, PlaylistType, StringComparison.OrdinalIgnoreCase))
        {
            TryQueuePlaylistMetadataTask(item, playlistLookupTasks, metadataThrottle, limits.MaxPlaylistMetadataLookups, cancellationToken);
            return;
        }

        if (string.Equals(itemType, ArtistType, StringComparison.OrdinalIgnoreCase))
        {
            TryQueueArtistMetadataTask(item, artistLookupTasks, metadataThrottle, limits.MaxArtistMetadataLookups, cancellationToken);
        }
    }

    private void TryQueuePlaylistMetadataTask(
        object item,
        Dictionary<string, Task<SpotifyUrlMetadata?>> playlistLookupTasks,
        SemaphoreSlim metadataThrottle,
        int maxPlaylistMetadataLookups,
        CancellationToken cancellationToken)
    {
        var playlistId = TryGetAnonymousString(item, "id");
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return;
        }

        var trackCount = TryGetAnonymousInt(item, TrackCountKey)
                         ?? TryGetAnonymousInt(item, "nb_tracks")
                         ?? TryGetAnonymousInt(item, "track_count");
        var playlistFollowers = TryGetAnonymousInt(item, FollowersKey)
                                ?? TryGetAnonymousInt(item, "fans");
        if (trackCount.HasValue && playlistFollowers.HasValue)
        {
            return;
        }

        if (playlistLookupTasks.Count >= maxPlaylistMetadataLookups && !playlistLookupTasks.ContainsKey(playlistId))
        {
            return;
        }

        if (!playlistLookupTasks.ContainsKey(playlistId))
        {
            playlistLookupTasks[playlistId] = FetchPlaylistMetadataSafeAsync(playlistId, metadataThrottle, cancellationToken);
        }
    }

    private void TryQueueArtistMetadataTask(
        object item,
        Dictionary<string, Task<int?>> artistLookupTasks,
        SemaphoreSlim metadataThrottle,
        int maxArtistMetadataLookups,
        CancellationToken cancellationToken)
    {
        var artistId = TryGetAnonymousString(item, "id");
        if (string.IsNullOrWhiteSpace(artistId))
        {
            return;
        }

        var artistFollowers = TryGetAnonymousInt(item, FollowersKey)
                              ?? TryGetAnonymousInt(item, "fans");
        var subtitle = TryGetAnonymousString(item, SubtitleKey);
        var subtitleHasAudience = !string.IsNullOrWhiteSpace(subtitle)
            && (subtitle.Contains("follower", StringComparison.OrdinalIgnoreCase)
                || subtitle.Contains("fan", StringComparison.OrdinalIgnoreCase));
        if (artistFollowers.HasValue || subtitleHasAudience)
        {
            return;
        }

        if (artistLookupTasks.Count >= maxArtistMetadataLookups && !artistLookupTasks.ContainsKey(artistId))
        {
            return;
        }

        if (!artistLookupTasks.ContainsKey(artistId))
        {
            artistLookupTasks[artistId] = FetchArtistFollowersSafeAsync(artistId, metadataThrottle, cancellationToken);
        }
    }

    private static async Task<object?> TryBuildEnrichedSectionAsync(
        object section,
        IReadOnlyDictionary<string, Task<SpotifyUrlMetadata?>> playlistLookupTasks,
        IReadOnlyDictionary<string, Task<int?>> artistLookupTasks)
    {
        var source = TryGetAnonymousString(section, SourcesKey) ?? SpotifySource;
        var title = TryGetAnonymousTitle(section);
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var items = TryGetAnonymousItems(section);
        if (items == null || items.Count == 0)
        {
            return null;
        }

        var enrichedItems = new List<object>(items.Count);
        foreach (var item in items)
        {
            var enrichedItem = await BuildEnrichedHomeItemAsync(item, source, playlistLookupTasks, artistLookupTasks);
            if (enrichedItem != null)
            {
                enrichedItems.Add(enrichedItem);
            }
        }

        if (enrichedItems.Count == 0)
        {
            return null;
        }

        var preserveAllItems = TryGetAnonymousBool(section, "__preserveAllItems") == true
                               || TryGetAnonymousBool(section, "preserveAllItems") == true;

        return new
        {
            source,
            title,
            layout = TryGetAnonymousString(section, "layout") ?? "row",
            pagePath = TryGetAnonymousString(section, "pagePath") ?? string.Empty,
            hasMore = TryGetAnonymousBool(section, "hasMore") ?? false,
            __preserveAllItems = preserveAllItems,
            preserveAllItems,
            items = enrichedItems
        };
    }

    private static async Task<object?> BuildEnrichedHomeItemAsync(
        object? item,
        string sectionSource,
        IReadOnlyDictionary<string, Task<SpotifyUrlMetadata?>> playlistLookupTasks,
        IReadOnlyDictionary<string, Task<int?>> artistLookupTasks)
    {
        if (item == null)
        {
            return null;
        }

        var itemSource = TryGetAnonymousString(item, "source") ?? sectionSource;
        var itemType = TryGetAnonymousString(item, "type") ?? PlaylistType;
        var itemId = TryGetAnonymousString(item, "id") ?? string.Empty;
        var itemUri = TryGetAnonymousString(item, "uri") ?? string.Empty;
        var name = TryGetAnonymousString(item, "name")
                   ?? TryGetAnonymousString(item, TitleKey)
                   ?? string.Empty;
        var artists = TryGetAnonymousString(item, ArtistsKey);
        var description = TryGetAnonymousString(item, DescriptionKey);
        var coverUrl = TryGetAnonymousString(item, "coverUrl")
                       ?? TryGetAnonymousString(item, ImageKey);
        var albumName = TryGetAnonymousString(item, "albumName")
                        ?? TryGetAnonymousString(item, AlbumKey);
        var durationMs = TryGetAnonymousInt(item, "durationMs");
        var trackCount = TryGetAnonymousInt(item, TrackCountKey)
                         ?? TryGetAnonymousInt(item, "nb_tracks")
                         ?? TryGetAnonymousInt(item, "track_count");
        var followers = TryGetAnonymousInt(item, FollowersKey)
                        ?? TryGetAnonymousInt(item, "fans");
        var isPublic = TryGetAnonymousBool(item, PublicKey)
                       ?? TryGetAnonymousBool(item, "isPublic");
        var collaborative = TryGetAnonymousBool(item, CollaborativeKey)
                            ?? TryGetAnonymousBool(item, "isCollaborative");

        (trackCount, followers) = await ApplySpotifyAudienceMetadataAsync(
            itemSource,
            itemType,
            itemId,
            trackCount,
            followers,
            playlistLookupTasks,
            artistLookupTasks);

        var normalizedArtists = NormalizeMappedArtists(itemType, artists);
        var normalizedDescription = NormalizeMappedDescription(description);
        var subtitle = BuildMappedSubtitle(itemType, normalizedArtists, normalizedDescription, trackCount, followers);

        return new
        {
            source = itemSource,
            id = itemId,
            uri = itemUri,
            type = itemType,
            name,
            title = name,
            artists = normalizedArtists,
            subtitle,
            description = normalizedDescription,
            coverUrl,
            albumName,
            durationMs,
            trackCount,
            nb_tracks = trackCount,
            followers,
            fans = followers,
            @public = isPublic,
            collaborative,
            image = coverUrl
        };
    }

    private static bool IsSpotifyItemType(string itemSource, string itemType, string expectedType, string itemId) =>
        string.Equals(itemSource, SpotifySource, StringComparison.OrdinalIgnoreCase)
        && string.Equals(itemType, expectedType, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(itemId);

    private static async Task<(int? TrackCount, int? Followers)> ApplySpotifyAudienceMetadataAsync(
        string itemSource,
        string itemType,
        string itemId,
        int? trackCount,
        int? followers,
        IReadOnlyDictionary<string, Task<SpotifyUrlMetadata?>> playlistLookupTasks,
        IReadOnlyDictionary<string, Task<int?>> artistLookupTasks)
    {
        if (IsSpotifyItemType(itemSource, itemType, PlaylistType, itemId)
            && playlistLookupTasks.TryGetValue(itemId, out var metadataTask))
        {
            var metadata = await metadataTask;
            if (metadata != null)
            {
                trackCount ??= metadata.TotalTracks;
                followers ??= metadata.Followers;
            }
        }
        else if (IsSpotifyItemType(itemSource, itemType, ArtistType, itemId)
                 && artistLookupTasks.TryGetValue(itemId, out var artistFollowersTask))
        {
            var artistFollowers = await artistFollowersTask;
            if (artistFollowers.HasValue)
            {
                followers ??= artistFollowers.Value;
            }
        }

        return (trackCount, followers);
    }

    private async Task<SpotifyUrlMetadata?> FetchPlaylistMetadataSafeAsync(
        string playlistId,
        SemaphoreSlim metadataThrottle,
        CancellationToken cancellationToken)
    {
        await metadataThrottle.WaitAsync(cancellationToken);
        try
        {
            return await _spotifyMetadataService.FetchPlaylistMetadataAsync(playlistId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Spotify playlist metadata enrichment failed for playlist {PlaylistId}.", playlistId);
            }
            return null;
        }
        finally
        {
            metadataThrottle.Release();
        }
    }

    private async Task<int?> FetchArtistFollowersSafeAsync(
        string artistId,
        SemaphoreSlim metadataThrottle,
        CancellationToken cancellationToken)
    {
        await metadataThrottle.WaitAsync(cancellationToken);
        try
        {
            var overview = await _pathfinderClient.FetchArtistOverviewAsync(artistId, cancellationToken);
            if (overview?.Followers is > 0)
            {
                return overview.Followers;
            }

            var url = $"https://open.spotify.com/artist/{artistId}";
            var metadata = await _spotifyMetadataService.FetchByUrlAsync(url, cancellationToken);
            return metadata?.Followers;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Spotify artist metadata enrichment failed for artist {ArtistId}.", artistId);
            }
            return null;
        }
        finally
        {
            metadataThrottle.Release();
        }
    }

    private static string BuildMappedSubtitle(
        string itemType,
        string? artists,
        string? description,
        int? trackCount,
        int? followers)
    {
        if (string.Equals(itemType, PlaylistType, StringComparison.OrdinalIgnoreCase))
        {
            var parts = new List<string>(2);
            if (trackCount is > 0)
            {
                parts.Add($"{trackCount.Value} tracks");
            }

            if (followers is > 0)
            {
                parts.Add($"{followers.Value} followers");
            }

            if (parts.Count > 0)
            {
                return string.Join(" - ", parts);
            }
        }
        else if (string.Equals(itemType, ArtistType, StringComparison.OrdinalIgnoreCase) &&
                 followers is > 0)
        {
            return $"{followers.Value} followers";
        }

        if (!string.IsNullOrWhiteSpace(artists))
        {
            return artists.Trim();
        }

        return description ?? string.Empty;
    }

    private static string? NormalizeMappedArtists(string? itemType, string? artists)
    {
        if (string.IsNullOrWhiteSpace(artists))
        {
            return null;
        }

        var value = artists.Trim();
        if (string.Equals(itemType, PlaylistType, StringComparison.OrdinalIgnoreCase) &&
            value.Equals(SpotifySource, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return value;
    }

    private static string? NormalizeMappedDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var decoded = System.Net.WebUtility.HtmlDecode(description);
        if (string.IsNullOrWhiteSpace(decoded))
        {
            return null;
        }

        var withoutTags = ReplaceWithTimeout(decoded, "<[^>]+>", " ");
        var compact = ReplaceWithTimeout(withoutTags, @"\s+", " ").Trim();
        return string.IsNullOrWhiteSpace(compact) ? null : compact;
    }

    private static bool IsPersonalSpotifyHomeItem(
        string? sectionTitle,
        string? itemType,
        string? name,
        string? artists,
        string? description,
        string? uri)
    {
        if (ContainsAnyToken(sectionTitle, PersonalSectionKeywords))
        {
            return true;
        }

        var normalizedType = string.IsNullOrWhiteSpace(itemType)
            ? string.Empty
            : itemType.Trim().ToLowerInvariant();
        if (normalizedType is PlaylistType or ShowType or EpisodeType or TrackType
            && (ContainsAnyToken(name, PersonalItemKeywords)
                || ContainsAnyToken(artists, PersonalItemKeywords)
                || ContainsAnyToken(description, PersonalItemKeywords)
                || ContainsAnyToken(uri, PersonalItemKeywords)))
        {
            return true;
        }

        return false;
    }

    private static bool ContainsAnyToken(string? value, string[] tokens)
    {
        if (string.IsNullOrWhiteSpace(value) || tokens.Length == 0)
        {
            return false;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return tokens
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Any(normalized.Contains);
    }

    private static List<string> ExtractSectionTitles(IEnumerable<object> sections)
    {
        return sections
            .Select(TryGetAnonymousTitle)
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Select(title => title!)
            .ToList();
    }

    private static List<string> FindDuplicateTitles(IEnumerable<object> sections)
    {
        return sections
            .Select(TryGetAnonymousTitle)
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .GroupBy(title => title!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();
    }

    private static List<object> SummarizeSections(IEnumerable<object> sections, string source)
    {
        var summaries = new List<object>();
        foreach (var section in sections)
        {
            var title = TryGetAnonymousTitle(section);
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var items = TryGetAnonymousItems(section);
            summaries.Add(new
            {
                source,
                title,
                uri = TryGetAnonymousUri(section),
                itemCount = items?.Count ?? 0
            });
        }

        return summaries;
    }

    private static string? TryGetAnonymousUri(object section)
    {
        try
        {
            var type = section.GetType();
            return type.GetProperty("uri")?.GetValue(section)?.ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private Task<string> ResolveHomeFeedCacheKeyAsync()
        => SpotifyHomeFeedCacheKey.ResolveAsync(
            DefaultCacheKey,
            _userContext,
            _userAuthStore,
            _platformAuthService);

    private static string ResolveHomeFeedCacheRoot()
    {
        return AppDataPathResolver.ResolveDataRootOrDefault(AppDataPathResolver.GetDefaultWorkersDataDir());
    }

    private static void TryPersistBrowseCategoriesCache(List<object> categories)
    {
        // Runtime cache only. Persisted JSON browse caches are intentionally disabled.
    }

    private static bool TryLoadPersistedBrowseCategoriesCache(out List<object> categories)
    {
        categories = new List<object>();
        return false;
    }

    private static void TryDeleteHomeFeedCache()
    {
        try
        {
            var configDir = ResolveHomeFeedCacheRoot();
            var spotifyDir = Path.Join(configDir, SpotifySource);
            if (!Directory.Exists(spotifyDir))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(spotifyDir, "home-feed-cache*.json"))
            {
                try
                {
                    System.IO.File.Delete(file);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Best-effort delete only.
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort delete only.
        }
    }

    private static void TryDeleteBrowseCategoriesCache()
    {
        try
        {
            var path = Path.Join(ResolveHomeFeedCacheRoot(), "spotify/browse-categories-cache.json");
            if (!System.IO.File.Exists(path))
            {
                return;
            }

            System.IO.File.Delete(path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort delete only.
        }
    }

    private static List<object>? TryGetAnonymousItems(object section)
    {
        var prop = section.GetType().GetProperty(ItemsKey);
        if (prop?.GetValue(section) is IEnumerable<object> items)
        {
            return items.ToList();
        }
        return null;
    }

    private static string? TryGetAnonymousString(object target, string name)
    {
        var prop = target.GetType().GetProperty(name);
        if (prop?.GetValue(target) is string value)
        {
            return value;
        }
        return null;
    }

    private static int? TryGetAnonymousInt(object target, string name)
    {
        var prop = target.GetType().GetProperty(name);
        var propValue = prop?.GetValue(target);
        if (propValue is int value)
        {
            return value;
        }

        if (propValue is long longValue && longValue <= int.MaxValue && longValue >= int.MinValue)
        {
            return (int)longValue;
        }

        if (propValue is string text &&
            int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static bool? TryGetAnonymousBool(object target, string name)
    {
        var prop = target.GetType().GetProperty(name);
        var propValue = prop?.GetValue(target);
        if (propValue is bool value)
        {
            return value;
        }

        if (propValue is string text && bool.TryParse(text, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string? TryGetAnonymousTitle(object section)
    {
        if (section is null)
        {
            return null;
        }
        var prop = section.GetType().GetProperty("title");
        if (prop?.GetValue(section) is string titleValue)
        {
            return titleValue.Trim();
        }
        return null;
    }

    private static string? FindFirstImageUrl(JsonElement element)
    {
        foreach (var nested in EnumerateChildElements(element))
        {
            var directImageUrl = TryReadImageUrl(nested);
            if (!string.IsNullOrWhiteSpace(directImageUrl))
            {
                return directImageUrl;
            }

            var descendantImageUrl = FindFirstImageUrl(nested);
            if (!string.IsNullOrWhiteSpace(descendantImageUrl))
            {
                return descendantImageUrl;
            }
        }

        return null;
    }

    private static string? TryGetString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
            {
                return null;
            }
            current = next;
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static bool TryNavigate(JsonElement element, object[] path, out JsonElement current)
    {
        current = element;
        if (path.Length == 0)
        {
            return false;
        }

        foreach (var segment in path)
        {
            if (segment is string name && TryGetProperty(current, name, out current))
            {
                continue;
            }

            if (segment is int index && TryGetArrayElement(current, index, out current))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement property)
    {
        property = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out property);
    }

    private static bool TryGetArrayElement(JsonElement element, int index, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var length = element.GetArrayLength();
        if (index < 0 || index >= length)
        {
            return false;
        }

        value = element[index];
        return true;
    }

    private static bool TryGetSectionItemsArray(JsonElement element, out JsonElement items)
    {
        items = default;
        return TryGetProperty(element, SectionItemsKey, out var sectionItems)
            && TryGetProperty(sectionItems, ItemsKey, out items)
            && items.ValueKind == JsonValueKind.Array;
    }

    private static IEnumerable<JsonElement> EnumerateChildElements(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                yield return property.Value;
            }

            yield break;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in element.EnumerateArray())
        {
            yield return item;
        }
    }

    private static string? TryReadUriWithPrefix(JsonElement element, string prefix)
    {
        if (!TryGetProperty(element, "uri", out var uriElement) || uriElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = uriElement.GetString();
        return !string.IsNullOrWhiteSpace(value) && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? value
            : null;
    }

    private static string? TryReadImageUrl(JsonElement element)
    {
        if (!TryGetProperty(element, UrlKey, out var urlElement) || urlElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = urlElement.GetString();
        return IsSpotifyImageHost(value) ? value : null;
    }

    private static bool IsSpotifyImageHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("i.scdn.co", StringComparison.OrdinalIgnoreCase)
            || value.Contains("spotifycdn.com", StringComparison.OrdinalIgnoreCase)
            || value.Contains("scdn.co", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetStringAt(JsonElement element, params object[] path)
    {
        if (!TryNavigate(element, path, out var current))
        {
            return null;
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static int? TryGetIntAt(JsonElement element, params object[] path)
    {
        if (!TryNavigate(element, path, out var current))
        {
            return null;
        }

        if (current.ValueKind == JsonValueKind.Number && current.TryGetInt32(out var number))
        {
            return number;
        }

        if (current.ValueKind == JsonValueKind.Number &&
            current.TryGetInt64(out var longNumber) &&
            longNumber <= int.MaxValue &&
            longNumber >= int.MinValue)
        {
            return (int)longNumber;
        }

        if (current.ValueKind == JsonValueKind.String &&
            int.TryParse(current.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static bool? TryGetBoolAt(JsonElement element, params object[] path)
    {
        if (!TryNavigate(element, path, out var current))
        {
            return null;
        }

        if (current.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (current.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        if (current.ValueKind == JsonValueKind.String &&
            bool.TryParse(current.GetString(), out var parsed))
        {
            return parsed;
        }

        if (current.ValueKind == JsonValueKind.Number && current.TryGetInt32(out var numericBool))
        {
            return numericBool != 0;
        }

        return null;
    }

    private static string? ExtractArtistsFromItems(JsonElement element, string containerName, string itemsName)
    {
        if (!element.TryGetProperty(containerName, out var container) ||
            container.ValueKind != JsonValueKind.Object ||
            !container.TryGetProperty(itemsName, out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var names = new List<string>();
        foreach (var artist in items.EnumerateArray())
        {
            var name = TryGetString(artist, ProfileKey, "name") ?? TryGetString(artist, "name");
            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name);
            }
        }

        return names.Count > 0 ? string.Join(", ", names) : null;
    }
}
