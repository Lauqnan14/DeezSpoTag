using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using JsonSerializer = System.Text.Json.JsonSerializer;
using System.Security.Cryptography;
using System.Text;
using System.Linq;
using System.Net;
using System.Globalization;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Authentication;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Apple;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Core.Security;
using GwTrack = DeezSpoTag.Core.Models.Deezer.GwTrack;

namespace DeezSpoTag.Web.Controllers
{
    [Route("api")]
    [ApiController]
    public class ApiController : ControllerBase
    {
        private const string TrackType = "track";
        private const string AlbumType = "album";
        private const string ArtistType = "artist";
        private const string PlaylistType = "playlist";
        private const string CoverImageType = "cover";
        private const string TitleField = "title";
        private const string NameField = "name";
        private const string UrlField = "url";
        private const string SourceField = "source";
        private const string TypeField = "type";
        private const string DataField = "data";
        private const string AttributesField = "attributes";
        private const string DeezerSource = "deezer";
        private const string AppleSource = "apple";
        private const string SpotifySource = "spotify";
        private const string ArtistPageCacheSource = "artist-page";
        private const string SmartTracklistType = "smarttracklist";
        private const string ShowType = "show";
        private const string EpisodeType = "episode";
        private const string ReleasesField = "releases";
        private const string RelatedField = "related";
        private const string ReleaseDateField = "release_date";
        private const string CoverUpperField = "COVER";
        private const string DescriptionUpperField = "DESCRIPTION";
        private const string NbSongUpperField = "NB_SONG";
        private const string SngIdUpperField = "SNG_ID";
        private const string ArtIdUpperField = "ART_ID";
        private const string ArtNameUpperField = "ART_NAME";
        private const string AlbIdUpperField = "ALB_ID";
        private const string AlbTitleUpperField = "ALB_TITLE";
        private const string AlbPictureUpperField = "ALB_PICTURE";
        private const string DurationUpperField = "DURATION";
        private const string DurationField = "duration";
        private const string TitleUpperField = "TITLE";
        private const string PictureUpperField = "PICTURE";
        private const string NbTracksField = "nb_tracks";
        private const string TracksField = "tracks";
        private const string SingleType = "single";
        private const string RecordTypeField = "record_type";
        private const string ArtPictureUpperField = "ART_PICTURE";
        private const string NbFanUpperField = "NB_FAN";
        private const string TargetField = "target";
        private const string PicturesField = "pictures";
        private const string ApplicationJsonContentType = "application/json";
        private const string CrossDeviceClientIdHeader = "X-DeezSpoTag-ClientId";
        private const bool HomeCacheEnabled = true;
        private static readonly TimeSpan TracklistCacheFallbackWindow = TimeSpan.FromDays(7);
        private static readonly object HomeCacheLock = new();
        private static readonly Dictionary<string, (DateTimeOffset Stamp, object Result)> HomeCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan HomeCacheTtl = TimeSpan.FromMinutes(2);
        private static readonly string[] PersonalHomeSectionKeywords =
        {
            "playlists you love",
            "your playlists",
            "favorites",
            "favourites",
            "liked songs",
            "loved tracks",
            "recently played",
            "your library"
        };
        private static readonly string[] PersonalHomePlaylistKeywords =
        {
            "liked songs",
            "loved tracks",
            "favorites",
            "favourites",
            "your playlist",
            "your playlists",
            "my playlist",
            "my playlists",
            "recently played",
            "your library"
        };
        private readonly ILogger<ApiController> _logger;
        private readonly DeezerGatewayService _deezerGatewayService;
        private readonly DeezSpoTagSettingsService _settingsService;
        private readonly ILoginStorageService _loginStorage;
        private readonly DeezSpoTag.Web.Services.LibraryConfigStore _libraryConfigStore;
        private readonly ArtistPageCacheRepository _artistPageCache;
        private readonly AppleMusicCatalogService _appleCatalog;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ISpotifyIdResolver _spotifyIdResolver;
        private readonly ISpotifyArtworkResolver _spotifyArtworkResolver;
        private readonly DeezSpoTag.Web.Services.SpotifyArtistService _spotifyArtistService;
        private readonly TracklistSongCacheStore _tracklistSongCacheStore;
        private readonly CrossDeviceSyncService _crossDeviceSyncService;
        private readonly SpotifyHomeFeedRuntimeService _spotifyHomeFeedRuntimeService;

        public sealed class ApiControllerMusicServices
        {
            public ApiControllerMusicServices(
                AppleMusicCatalogService appleCatalog,
                IHttpClientFactory httpClientFactory,
                ISpotifyIdResolver spotifyIdResolver,
                ISpotifyArtworkResolver spotifyArtworkResolver,
                DeezSpoTag.Web.Services.SpotifyArtistService spotifyArtistService)
            {
                AppleCatalog = appleCatalog;
                HttpClientFactory = httpClientFactory;
                SpotifyIdResolver = spotifyIdResolver;
                SpotifyArtworkResolver = spotifyArtworkResolver;
                SpotifyArtistService = spotifyArtistService;
            }

            public AppleMusicCatalogService AppleCatalog { get; }
            public IHttpClientFactory HttpClientFactory { get; }
            public ISpotifyIdResolver SpotifyIdResolver { get; }
            public ISpotifyArtworkResolver SpotifyArtworkResolver { get; }
            public DeezSpoTag.Web.Services.SpotifyArtistService SpotifyArtistService { get; }
        }

        public sealed class ApiControllerDependencies
        {
            public required ILogger<ApiController> Logger { get; init; }
            public required DeezerGatewayService DeezerGatewayService { get; init; }
            public required DeezSpoTagSettingsService SettingsService { get; init; }
            public required ILoginStorageService LoginStorage { get; init; }
            public required DeezSpoTag.Web.Services.LibraryConfigStore LibraryConfigStore { get; init; }
            public required ArtistPageCacheRepository ArtistPageCache { get; init; }
            public required ApiControllerMusicServices MusicServices { get; init; }
            public required TracklistSongCacheStore TracklistSongCacheStore { get; init; }
            public required CrossDeviceSyncService CrossDeviceSyncService { get; init; }
            public required SpotifyHomeFeedRuntimeService SpotifyHomeFeedRuntimeService { get; init; }
        }

        public ApiController(ApiControllerDependencies dependencies)
        {
            _logger = dependencies.Logger;
            _deezerGatewayService = dependencies.DeezerGatewayService;
            _settingsService = dependencies.SettingsService;
            _loginStorage = dependencies.LoginStorage;
            _libraryConfigStore = dependencies.LibraryConfigStore;
            _artistPageCache = dependencies.ArtistPageCache;
            _appleCatalog = dependencies.MusicServices.AppleCatalog;
            _httpClientFactory = dependencies.MusicServices.HttpClientFactory;
            _spotifyIdResolver = dependencies.MusicServices.SpotifyIdResolver;
            _spotifyArtworkResolver = dependencies.MusicServices.SpotifyArtworkResolver;
            _spotifyArtistService = dependencies.MusicServices.SpotifyArtistService;
            _tracklistSongCacheStore = dependencies.TracklistSongCacheStore;
            _crossDeviceSyncService = dependencies.CrossDeviceSyncService;
            _spotifyHomeFeedRuntimeService = dependencies.SpotifyHomeFeedRuntimeService;
        }

        // Login endpoints removed - handled by DeezSpoTag.API.Controllers.LoginController

        [HttpGet("deezer/search")]
        public async Task<IActionResult> Search([FromQuery] string term, [FromQuery] string type = TrackType,
            [FromQuery] int start = 0, [FromQuery] int nb = 25)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(term))
                {
                    return Ok(BuildDeezerSearchResponse(Array.Empty<object>(), 0, type, "Search term is required"));
                }

                var gwResponse = await _deezerGatewayService.SearchAsync(term, start, nb, suggest: false, artistSuggest: false, topTracks: false);
                var section = type.ToLower() switch
                {
                    TrackType => gwResponse.Track,
                    AlbumType => gwResponse.Album,
                    ArtistType => gwResponse.Artist,
                    PlaylistType => gwResponse.Playlist,
                    _ => null
                };

                if (section?.Data != null)
                {
                    return Ok(BuildDeezerSearchResponse(section.Data, section.Total, type));
                }

                return Ok(BuildDeezerSearchResponse(Array.Empty<object>(), 0, type, "Search failed."));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var safeTerm = LogSanitizer.OneLine(term);
                var safeType = LogSanitizer.OneLine(type);
                _logger.LogError(ex, "Error in Search for term: {Term}, type: {Type}", safeTerm, safeType);
                return Ok(BuildDeezerSearchResponse(Array.Empty<object>(), 0, type, "Search failed."));
            }
        }

        [HttpGet("search")]
        public async Task<IActionResult> UnifiedSearch([FromQuery] string term, [FromQuery] bool offline = false, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(term))
            {
                return Ok(BuildUnifiedSearchResponse(source: offline ? "offline" : "gw", error: "Search term is required"));
            }

            if (offline)
            {
                try
                {
                    if (!_libraryConfigStore.HasLocalLibraryData())
                    {
                        return Ok(BuildUnifiedSearchResponse(source: "offline", error: "Offline search is unavailable."));
                    }

                    var offlineResults = SearchOfflineAsync(term.Trim(), cancellationToken);
                    return Ok(BuildUnifiedSearchResponse(
                        offlineResults.Tracks,
                        offlineResults.Albums,
                        offlineResults.Artists,
                        offlineResults.Playlists,
                        "offline"));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Offline library search failed for term {Term}", LogSanitizer.OneLine(term));
                    return Ok(BuildUnifiedSearchResponse(source: "offline", error: "Offline search failed."));
                }
            }

            try
            {
                var gwResponse = await _deezerGatewayService.SearchAsync(term.Trim(), 0, 128, suggest: false, artistSuggest: false, topTracks: false);
                var tracks = await MapGwSectionAsync(gwResponse.Track, MapGwTrackAsync, cancellationToken);
                var albums = await MapGwSectionAsync(gwResponse.Album, MapGwAlbumAsync, cancellationToken);
                var artists = await MapGwSectionAsync(gwResponse.Artist, MapGwArtistAsync, cancellationToken);
                var playlists = MapGwSection(gwResponse.Playlist, MapGwPlaylist);

                return Ok(BuildUnifiedSearchResponse(tracks, albums, artists, playlists, "gw"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "GW search failed for term {Term}", LogSanitizer.OneLine(term));
                return Ok(BuildUnifiedSearchResponse(source: "gw", error: "Search failed."));
            }
        }

        [HttpGet("search/suggestions")]
        public IActionResult SearchSuggestions([FromQuery] string term)
        {
            return Ok(new
            {
                suggestions = Array.Empty<string>(),
                source = "disabled"
            });
        }

        private static object BuildDeezerSearchResponse(IEnumerable<object> data, int total, string type, string error = "")
        {
            return new
            {
                data,
                total,
                type,
                error
            };
        }

        private static object BuildUnifiedSearchResponse(
            IEnumerable<object>? tracks = null,
            IEnumerable<object>? albums = null,
            IEnumerable<object>? artists = null,
            IEnumerable<object>? playlists = null,
            string source = "gw",
            string error = "")
        {
            return new
            {
                tracks = tracks ?? Array.Empty<object>(),
                albums = albums ?? Array.Empty<object>(),
                artists = artists ?? Array.Empty<object>(),
                playlists = playlists ?? Array.Empty<object>(),
                source,
                error
            };
        }

        private static object BuildParseLinkResponse(string? type = null, string? id = null, string error = "")
        {
            return new
            {
                type = type ?? string.Empty,
                id = id ?? string.Empty,
                error
            };
        }

        private static object BuildHomeSectionsResponse(IEnumerable<object>? sections = null)
        {
            return new
            {
                sections = sections?.ToArray() ?? Array.Empty<object>()
            };
        }

        [HttpGet("deezer/parse-link")]
        public async Task<IActionResult> ParseDeezerLink([FromQuery] string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return Ok(BuildParseLinkResponse(error: "URL is required."));
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return Ok(BuildParseLinkResponse(error: "Invalid URL."));
            }

            if (TryParseDeezerUrl(uri, out var type, out var id))
            {
                return Ok(BuildParseLinkResponse(type, id));
            }

            if (IsDeezerShareLink(uri))
            {
                var resolved = await ResolveDeezerShareLinkAsync(uri);
                if (resolved != null && TryParseDeezerUrl(resolved, out type, out id))
                {
                    return Ok(BuildParseLinkResponse(type, id));
                }
            }

            return Ok(BuildParseLinkResponse(error: "Link is not recognizable."));
        }

        [HttpGet("home")]
        public async Task<IActionResult> GetHome([FromQuery] string? channel = null, [FromQuery] string? raw = null, [FromQuery] string? refresh = null, [FromQuery] string? timeZone = null)
        {
            try
            {
                var rawEnabled = IsTruthyQueryFlag(raw);
                var refreshEnabled = IsTruthyQueryFlag(refresh);
                channel = NormalizeHomeChannel(channel);
                var cacheScope = ResolveHomeCacheScope();
                var cacheKey = GetHomeCacheKey(channel, cacheScope, timeZone);
                var allowCache = HomeCacheEnabled;

                var cachedResult = ResolveHomeCacheResult(cacheKey, allowCache, rawEnabled, refreshEnabled);
                if (cachedResult != null)
                {
                    return Ok(cachedResult);
                }

                var deezerLanguage = ResolveDeezerLanguage();
                var page = await FetchHomePageAsync(channel, deezerLanguage);

                if (rawEnabled)
                {
                    return Content(page.ToString(Newtonsoft.Json.Formatting.None), ApplicationJsonContentType);
                }

                var result = await MapHomePageAsync(page, HttpContext.RequestAborted);
                if (string.IsNullOrWhiteSpace(channel))
                {
                    var deezerSections = ExtractHomeSections(result);
                    var spotifySections = await _spotifyHomeFeedRuntimeService.GetMappedSectionsAsync(
                        timeZone,
                        refreshEnabled,
                        HttpContext.RequestAborted);
                    var spotifyCategories = await _spotifyHomeFeedRuntimeService.GetBrowseCategoriesAsync(
                        refreshEnabled,
                        HttpContext.RequestAborted);
                    var categorizedSections = MergeSpotifyCategoriesIntoHomeCategories(deezerSections, spotifyCategories);
                    result = BuildHomeSectionsResponse(MergeSpotifySectionsIntoHomeSections(categorizedSections, spotifySections));
                }
                StoreHomeCacheResult(cacheKey, allowCache, result);
                return Ok(result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error fetching home data");
                return Ok(BuildHomeSectionsResponse());
            }
        }

        private static bool IsTruthyQueryFlag(string? value) =>
            string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);

        private static string? NormalizeHomeChannel(string? channel)
        {
            if (string.IsNullOrWhiteSpace(channel))
            {
                return channel;
            }

            var normalized = channel.Trim();
            for (var i = 0; i < 2; i++)
            {
                normalized = Uri.UnescapeDataString(normalized);
            }

            normalized = normalized.Replace("\\", "/").TrimStart('/');
            if (!normalized.Contains('/') && normalized.Length > 0)
            {
                normalized = $"channels/{normalized}";
            }

            return normalized;
        }

        private static string GetHomeCacheKey(string? channel, string cacheScope, string? timeZone)
        {
            var scope = string.IsNullOrWhiteSpace(cacheScope) ? "default" : cacheScope;
            var zone = string.IsNullOrWhiteSpace(timeZone) ? "default" : timeZone.Trim().ToLowerInvariant();
            return string.IsNullOrWhiteSpace(channel)
                ? $"home:{scope}:default:{zone}"
                : $"home:{scope}:{channel}";
        }

        private static IReadOnlyList<object> ExtractHomeSections(object homeResponse)
        {
            var sectionsValue = homeResponse.GetType()
                .GetProperty("sections")
                ?.GetValue(homeResponse);

            return sectionsValue switch
            {
                IReadOnlyList<object> list => list,
                IEnumerable<object> enumerable => enumerable.ToList(),
                System.Collections.IEnumerable enumerable => enumerable.Cast<object>().ToList(),
                _ => Array.Empty<object>()
            };
        }

        private static IReadOnlyList<object> MergeSpotifySectionsIntoHomeSections(
            IReadOnlyList<object> deezerSections,
            IReadOnlyList<object> spotifySections)
        {
            if (spotifySections.Count == 0)
            {
                return deezerSections;
            }

            if (deezerSections.Count == 0)
            {
                return spotifySections;
            }

            var merged = deezerSections.ToList();
            var discoverIndex = merged.FindIndex(section =>
                string.Equals(TryReadAnonymousString(section, TitleField), "discover", StringComparison.OrdinalIgnoreCase));
            var insertIndex = discoverIndex >= 0 ? discoverIndex + 1 : merged.Count;
            merged.InsertRange(insertIndex, spotifySections);
            return merged;
        }

        private static IReadOnlyList<object> MergeSpotifyCategoriesIntoHomeCategories(
            IReadOnlyList<object> sections,
            IReadOnlyList<object> spotifyCategories)
        {
            if (sections.Count == 0 || spotifyCategories.Count == 0)
            {
                return sections;
            }

            var categoryIndex = sections.ToList().FindIndex(IsHomeCategoriesSection);
            if (categoryIndex < 0)
            {
                return sections;
            }

            var existingSection = sections[categoryIndex];
            var deezerItems = TryReadAnonymousItems(existingSection)
                .Where(item => !string.Equals(TryReadAnonymousString(item, SourceField), SpotifySource, StringComparison.OrdinalIgnoreCase))
                .Take(7)
                .ToList();
            var spotifyItems = spotifyCategories
                .Select(BuildSpotifyHomeCategoryItem)
                .Where(item => item != null)
                .Cast<object>()
                .Take(7)
                .ToList();
            if (spotifyItems.Count == 0)
            {
                return sections;
            }

            var next = sections.ToList();
            next[categoryIndex] = new
            {
                title = TryReadAnonymousString(existingSection, TitleField) ?? "Categories",
                layout = TryReadAnonymousString(existingSection, "layout") ?? "grid",
                pagePath = TryReadAnonymousString(existingSection, "pagePath") ?? string.Empty,
                hasMore = TryReadAnonymousBool(existingSection, "hasMore") ?? false,
                filter = TryReadAnonymousObject(existingSection, "filter"),
                items = deezerItems.Concat(spotifyItems).ToList(),
                related = TryReadAnonymousObject(existingSection, RelatedField)
            };
            return next;
        }

        private static bool IsHomeCategoriesSection(object section)
        {
            var title = TryReadAnonymousString(section, TitleField);
            return string.Equals(title, "categories", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(title, "your top genres", StringComparison.OrdinalIgnoreCase);
        }

        private static object? BuildSpotifyHomeCategoryItem(object category)
        {
            var id = TryReadAnonymousString(category, "id");
            var name = TryReadAnonymousString(category, NameField) ?? TryReadAnonymousString(category, TitleField);
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            return new
            {
                source = SpotifySource,
                type = "category",
                id,
                categoryId = id,
                uri = TryReadAnonymousString(category, "uri"),
                title = name,
                name,
                coverUrl = TryReadAnonymousString(category, "image_url")
                           ?? TryReadAnonymousString(category, "imageUrl")
                           ?? TryReadAnonymousString(category, "coverUrl")
                           ?? string.Empty,
                background_color = TryReadAnonymousString(category, "background_color")
                                   ?? TryReadAnonymousString(category, "backgroundColor")
            };
        }

        private static IReadOnlyList<object> TryReadAnonymousItems(object value)
        {
            var itemsValue = TryReadAnonymousObject(value, "items");
            return itemsValue switch
            {
                IReadOnlyList<object> list => list,
                IEnumerable<object> enumerable => enumerable.ToList(),
                System.Collections.IEnumerable enumerable => enumerable.Cast<object>().ToList(),
                _ => Array.Empty<object>()
            };
        }

        private static object? TryReadAnonymousObject(object value, string propertyName)
        {
            try
            {
                return value.GetType().GetProperty(propertyName)?.GetValue(value);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return null;
            }
        }

        private static bool? TryReadAnonymousBool(object value, string propertyName)
        {
            var raw = TryReadAnonymousObject(value, propertyName);
            return raw switch
            {
                bool boolean => boolean,
                _ => null
            };
        }

        private static string? TryReadAnonymousString(object value, string propertyName)
        {
            try
            {
                return value.GetType().GetProperty(propertyName)?.GetValue(value)?.ToString();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return null;
            }
        }

        private string ResolveHomeCacheScope()
        {
            if (User?.Identity?.IsAuthenticated == true)
            {
                var username = User.Identity?.Name;
                if (!string.IsNullOrWhiteSpace(username))
                {
                    return $"user:{username.Trim().ToLowerInvariant()}";
                }
            }

            var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString();
            return string.IsNullOrWhiteSpace(remoteIp) ? "anonymous" : $"ip:{remoteIp}";
        }

        private static object? ResolveHomeCacheResult(string cacheKey, bool allowCache, bool rawEnabled, bool refreshEnabled)
        {
            if (rawEnabled || !allowCache)
            {
                return null;
            }

            if (refreshEnabled)
            {
                lock (HomeCacheLock)
                {
                    HomeCache.Remove(cacheKey);
                }

                return null;
            }

            lock (HomeCacheLock)
            {
                if (HomeCache.TryGetValue(cacheKey, out var cached)
                    && DateTimeOffset.UtcNow - cached.Stamp <= HomeCacheTtl)
                {
                    return cached.Result;
                }
            }

            return null;
        }

        private string ResolveDeezerLanguage()
        {
            var settings = _settingsService.LoadSettings();
            return string.IsNullOrWhiteSpace(settings.DeezerLanguage)
                ? "en"
                : settings.DeezerLanguage.Trim();
        }

        private Task<JObject> FetchHomePageAsync(string? channel, string deezerLanguage) =>
            string.IsNullOrWhiteSpace(channel)
                ? _deezerGatewayService.GetHomePageAsync(deezerLanguage)
                : _deezerGatewayService.GetChannelPageAsync(channel.Trim(), deezerLanguage);

        private static void StoreHomeCacheResult(string cacheKey, bool allowCache, object result)
        {
            if (!allowCache)
            {
                return;
            }

            lock (HomeCacheLock)
            {
                HomeCache[cacheKey] = (DateTimeOffset.UtcNow, result);
            }
        }

        [HttpGet("tracklist")]
        public async Task<IActionResult> GetTracklist(
            [FromQuery] string id,
            [FromQuery] string type,
            [FromQuery] string? refresh = null,
            [FromHeader(Name = CrossDeviceClientIdHeader)] string? sourceClientIdHeader = null,
            CancellationToken cancellationToken = default)
        {
            TracklistSongCacheEntry? cachedEntry = null;
            try
            {
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(type))
                {
                    return BadRequest("ID and type are required");
                }

                var normalizedId = id.Trim();
                var normalizedType = type.Trim();
                if (RequiresNumericDeezerId(normalizedType) && !IsNumericDeezerId(normalizedId))
                {
                    return BadRequest("Invalid Deezer ID.");
                }

                var refreshRequested = IsTruthyQueryFlag(refresh);
                var tracklistTypeKey = normalizedType.ToLowerInvariant();
                var sourceClientId = NormalizeCrossDeviceClientId(sourceClientIdHeader);

                cachedEntry = await _tracklistSongCacheStore.TryGetAsync(tracklistTypeKey, normalizedId, cancellationToken);
                if (!refreshRequested
                    && cachedEntry != null
                    && TracklistSongCacheStore.IsFresh(cachedEntry, DateTimeOffset.UtcNow))
                {
                    return Content(cachedEntry.PayloadJson, ApplicationJsonContentType);
                }

                var liveResponse = await FetchTracklistFromSourceAsync(normalizedId, normalizedType);
                if (TryExtractTracklistPayloadJson(liveResponse, out var payloadJson, out var trackCount))
                {
                    await StoreTracklistCacheAndNotifyAsync(
                        tracklistTypeKey,
                        normalizedId,
                        payloadJson,
                        trackCount,
                        sourceClientId,
                        cancellationToken);

                    return liveResponse;
                }

                if (TryBuildFallbackTracklistCacheResponse(cachedEntry, out var fallbackResponse))
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug(
                            "Serving fallback tracklist cache. type={TracklistType} id={TracklistId} status={StatusCode}",
                            tracklistTypeKey,
                            normalizedId,
                            ResolveStatusCode(liveResponse));
                    }
                    return fallbackResponse;
                }

                return liveResponse;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error fetching tracklist data for Type Id");
                if (TryBuildFallbackTracklistCacheResponse(cachedEntry, out var fallbackResponse))
                {
                    return fallbackResponse;
                }

                return StatusCode(500, "Error fetching data");
            }
        }

        private async Task StoreTracklistCacheAndNotifyAsync(
            string tracklistTypeKey,
            string normalizedId,
            string payloadJson,
            int trackCount,
            string? sourceClientId,
            CancellationToken cancellationToken)
        {
            var payloadHash = ComputePayloadHash(payloadJson);
            var upsertResult = await _tracklistSongCacheStore.UpsertAsync(
                tracklistTypeKey,
                normalizedId,
                payloadJson,
                payloadHash,
                trackCount,
                cancellationToken);

            if (!upsertResult.HasChanged || string.IsNullOrWhiteSpace(upsertResult.PreviousHash))
            {
                return;
            }

            await _crossDeviceSyncService.PublishTracklistUpdatedAsync(
                tracklistTypeKey,
                normalizedId,
                trackCount,
                payloadHash,
                sourceClientId,
                cancellationToken);
        }

        private async Task<IActionResult> FetchTracklistFromSourceAsync(string id, string type)
        {
            var gatewayResponse = await TryGetGatewayTracklistResponseAsync(id, type);
            if (gatewayResponse != null)
            {
                return gatewayResponse;
            }

            using var httpClient = _httpClientFactory.CreateClient();
            if (string.Equals(type, ArtistType, StringComparison.OrdinalIgnoreCase))
            {
                return await GetArtistData(httpClient, id);
            }

            if (string.Equals(type, TrackType, StringComparison.OrdinalIgnoreCase))
            {
                return await GetTrackAsAlbumTracklistAsync(httpClient, id);
            }

            return await GetDefaultTracklistAsync(httpClient, id, type);
        }

        private static bool TryBuildFallbackTracklistCacheResponse(TracklistSongCacheEntry? cacheEntry, out IActionResult fallbackResponse)
        {
            fallbackResponse = new EmptyResult();
            if (cacheEntry == null || string.IsNullOrWhiteSpace(cacheEntry.PayloadJson))
            {
                return false;
            }

            if (DateTimeOffset.UtcNow - cacheEntry.UpdatedUtc > TracklistCacheFallbackWindow)
            {
                return false;
            }

            fallbackResponse = new ContentResult
            {
                Content = cacheEntry.PayloadJson,
                ContentType = ApplicationJsonContentType,
                StatusCode = StatusCodes.Status200OK
            };
            return true;
        }

        private static string? NormalizeCrossDeviceClientId(string? rawValue)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return null;
            }

            var sanitized = new string(rawValue.Trim().Where(ch => !char.IsControl(ch)).ToArray());
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                return null;
            }

            return sanitized.Length <= 128 ? sanitized : sanitized[..128];
        }

        private static bool TryExtractTracklistPayloadJson(IActionResult result, out string payloadJson, out int trackCount)
        {
            payloadJson = string.Empty;
            trackCount = 0;

            switch (result)
            {
                case ObjectResult objectResult
                    when IsSuccessStatusCode(objectResult.StatusCode) && objectResult.Value != null:
                    payloadJson = JsonSerializer.Serialize(objectResult.Value);
                    break;
                case JsonResult jsonResult
                    when IsSuccessStatusCode(jsonResult.StatusCode) && jsonResult.Value != null:
                    payloadJson = JsonSerializer.Serialize(jsonResult.Value);
                    break;
                case ContentResult contentResult
                    when IsSuccessStatusCode(contentResult.StatusCode)
                         && IsJsonContentType(contentResult.ContentType)
                         && !string.IsNullOrWhiteSpace(contentResult.Content):
                    payloadJson = contentResult.Content;
                    break;
                default:
                    return false;
            }

            trackCount = ResolveTrackCount(payloadJson);
            return true;
        }

        private static bool IsSuccessStatusCode(int? statusCode)
        {
            return !statusCode.HasValue || (statusCode.Value >= StatusCodes.Status200OK && statusCode.Value < StatusCodes.Status300MultipleChoices);
        }

        private static bool IsJsonContentType(string? contentType)
        {
            return !string.IsNullOrWhiteSpace(contentType)
                   && contentType.Contains(ApplicationJsonContentType, StringComparison.OrdinalIgnoreCase);
        }

        private static int ResolveTrackCount(string payloadJson)
        {
            try
            {
                using var document = JsonDocument.Parse(payloadJson);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return 0;
                }

                if (document.RootElement.TryGetProperty(NbTracksField, out var nbTracksElement)
                    && nbTracksElement.ValueKind == JsonValueKind.Number
                    && nbTracksElement.TryGetInt32(out var nbTracks))
                {
                    return Math.Max(0, nbTracks);
                }

                if (!document.RootElement.TryGetProperty(TracksField, out var tracksElement))
                {
                    return 0;
                }

                if (tracksElement.ValueKind == JsonValueKind.Array)
                {
                    return tracksElement.GetArrayLength();
                }

                if (tracksElement.ValueKind == JsonValueKind.Object
                    && tracksElement.TryGetProperty(DataField, out var dataElement)
                    && dataElement.ValueKind == JsonValueKind.Array)
                {
                    return dataElement.GetArrayLength();
                }
            }
            catch
            {
                // Ignore malformed payloads and treat track count as unknown.
            }

            return 0;
        }

        private static string ComputePayloadHash(string payloadJson)
        {
            var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson));
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        private static int ResolveStatusCode(IActionResult result)
        {
            return result switch
            {
                ObjectResult objectResult => objectResult.StatusCode ?? StatusCodes.Status200OK,
                JsonResult jsonResult => jsonResult.StatusCode ?? StatusCodes.Status200OK,
                ContentResult contentResult => contentResult.StatusCode ?? StatusCodes.Status200OK,
                StatusCodeResult statusCodeResult => statusCodeResult.StatusCode,
                _ => StatusCodes.Status200OK
            };
        }

        private static bool RequiresNumericDeezerId(string type)
        {
            return string.Equals(type, TrackType, StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, AlbumType, StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, ArtistType, StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, PlaylistType, StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, ShowType, StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, EpisodeType, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNumericDeezerId(string id)
        {
            return !string.IsNullOrWhiteSpace(id) && id.All(char.IsDigit);
        }

        private async Task<IActionResult?> TryGetGatewayTracklistResponseAsync(string id, string type)
        {
            if (string.Equals(type, AlbumType, StringComparison.OrdinalIgnoreCase))
            {
                return await TryGetGatewayAlbumTracklistAsync(id);
            }

            if (string.Equals(type, PlaylistType, StringComparison.OrdinalIgnoreCase))
            {
                return await TryGetGatewayPlaylistTracklistAsync(id);
            }

            if (string.Equals(type, SmartTracklistType, StringComparison.OrdinalIgnoreCase))
            {
                return await TryGetGatewaySmartTracklistAsync(id);
            }

            if (string.Equals(type, ShowType, StringComparison.OrdinalIgnoreCase))
            {
                return await TryGetGatewayShowTracklistAsync(id);
            }

            return null;
        }

        private async Task<IActionResult?> TryGetGatewayAlbumTracklistAsync(string id)
        {
            try
            {
                var albumPage = await _deezerGatewayService.GetAlbumPageWithSongsAsync(id);
                var albumSongs = albumPage?.Songs?.Data;
                if (albumPage?.Data == null || albumSongs == null || albumSongs.Count <= 0)
                {
                    return null;
                }

                var coverMd5 = albumPage.Data.AlbPicture;
                var coverXl = string.IsNullOrWhiteSpace(coverMd5)
                    ? string.Empty
                    : $"https://e-cdns-images.dzcdn.net/images/cover/{coverMd5}/1000x1000-000000-80-0-0.jpg";
                var coverBig = string.IsNullOrWhiteSpace(coverMd5)
                    ? string.Empty
                    : $"https://e-cdns-images.dzcdn.net/images/cover/{coverMd5}/500x500-000000-80-0-0.jpg";

                var tracks = albumSongs.Select(track => new
                {
                    id = track.SngId.ToString(),
                    title = track.SngTitle,
                    duration = track.Duration,
                    isrc = track.Isrc,
                    track_position = track.TrackNumber > 0 ? track.TrackNumber : track.Position + 1,
                    stream_track_id = ResolveDeezerStreamTrackId(track),
                    track_token = track.TrackToken ?? string.Empty,
                    md5_origin = track.Md5Origin ?? string.Empty,
                    media_version = track.MediaVersion > 0 ? track.MediaVersion.ToString() : string.Empty,
                    fallback_id = ResolveDeezerFallbackId(track),
                    artist = new { id = track.ArtId.ToString(), name = track.ArtName },
                    album = new { id = track.AlbId, title = track.AlbTitle, cover_medium = coverBig }
                }).ToList();

                return Ok(new
                {
                    id = albumPage.Data.AlbId,
                    title = albumPage.Data.AlbTitle,
                    artist = new { id = albumPage.Data.ArtId.ToString(), name = albumPage.Data.ArtName },
                    cover_xl = coverXl,
                    cover_big = coverBig,
                    nb_tracks = tracks.Count,
                    tracks
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Gateway album tracklist fetch failed for AlbumId");
                return null;
            }
        }

        private async Task<IActionResult?> TryGetGatewayPlaylistTracklistAsync(string id)
        {
            try
            {
                var playlistPage = await _deezerGatewayService.GetPlaylistPageWithSongsAsync(id);
                var playlistSongs = playlistPage?.Songs?.Data;
                if (playlistPage?.Data == null || playlistSongs == null || playlistSongs.Count <= 0)
                {
                    return null;
                }

                var pictureXl = BuildGatewayImageUrl("playlist", playlistPage.Data.PlaylistPicture, "1000x1000");
                var pictureBig = BuildGatewayImageUrl("playlist", playlistPage.Data.PlaylistPicture, "500x500");
                var ownerId = PickPlaylistOwnerValue(playlistPage.Data.OwnerId, playlistPage.Data.UserId);
                var ownerName = PickPlaylistOwnerValue(playlistPage.Data.OwnerName, playlistPage.Data.UserName);
                var ownerPicture = PickPlaylistOwnerValue(playlistPage.Data.OwnerPicture, playlistPage.Data.UserPicture);
                var ownerAvatar = BuildGatewayImageUrl("user", ownerPicture, "250x250");
                var tracks = playlistSongs.Select(MapGatewayPlaylistTrack).ToList();
                var creator = BuildPlaylistOwnerObject(ownerId, ownerName, ownerAvatar, includePicture: true);
                var owner = BuildPlaylistOwnerObject(ownerId, ownerName, ownerAvatar, includePicture: false);

                return Ok(new
                {
                    id = playlistPage.Data.PlaylistId,
                    title = playlistPage.Data.Title,
                    description = playlistPage.Data.Description,
                    duration = playlistPage.Data.Duration,
                    @public = playlistPage.Data.IsPublic || playlistPage.Data.Status == 1,
                    collaborative = playlistPage.Data.Collaborative || playlistPage.Data.Status == 2,
                    status = playlistPage.Data.Status,
                    checksum = playlistPage.Data.Checksum,
                    creation_date = playlistPage.Data.CreationDate,
                    fans = playlistPage.Data.Fans,
                    followers = playlistPage.Data.Fans,
                    is_loved_track = playlistPage.Data.IsLovedTrack,
                    creator,
                    owner,
                    picture_xl = pictureXl,
                    picture_big = pictureBig,
                    nb_tracks = playlistPage.Data.NbSong,
                    tracks
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Gateway playlist tracklist fetch failed for PlaylistId");
                return null;
            }
        }

        private static string BuildGatewayImageUrl(string imageType, string? md5, string dimensions) =>
            string.IsNullOrWhiteSpace(md5)
                ? string.Empty
                : $"https://e-cdns-images.dzcdn.net/images/{imageType}/{md5}/{dimensions}-000000-80-0-0.jpg";

        private static string PickPlaylistOwnerValue(string? preferred, string? fallback) =>
            !string.IsNullOrWhiteSpace(preferred) ? preferred : fallback ?? string.Empty;

        private static object MapGatewayPlaylistTrack(GwTrack track, int index)
        {
            var albumCover = string.IsNullOrWhiteSpace(track.AlbPicture)
                ? string.Empty
                : BuildDeezerImage(CoverImageType, track.AlbPicture) ?? string.Empty;
            return new
            {
                id = track.SngId.ToString(),
                title = track.SngTitle,
                duration = track.Duration,
                isrc = track.Isrc,
                track_position = track.Position > 0 ? track.Position : index + 1,
                stream_track_id = ResolveDeezerStreamTrackId(track),
                track_token = track.TrackToken ?? string.Empty,
                md5_origin = track.Md5Origin ?? string.Empty,
                media_version = track.MediaVersion > 0 ? track.MediaVersion.ToString() : string.Empty,
                fallback_id = ResolveDeezerFallbackId(track),
                artist = new { id = track.ArtId.ToString(), name = track.ArtName },
                album = new
                {
                    id = track.AlbId,
                    title = track.AlbTitle,
                    cover_medium = albumCover
                }
            };
        }

        private static object? BuildPlaylistOwnerObject(
            string? ownerId,
            string? ownerName,
            string ownerAvatar,
            bool includePicture)
        {
            if (string.IsNullOrWhiteSpace(ownerId) && string.IsNullOrWhiteSpace(ownerName))
            {
                return null;
            }

            if (includePicture)
            {
                return new
                {
                    id = ownerId,
                    name = ownerName,
                    picture = ownerAvatar,
                    avatar = ownerAvatar
                };
            }

            return new
            {
                id = ownerId,
                name = ownerName,
                avatar = ownerAvatar
            };
        }

        private async Task<IActionResult?> TryGetGatewaySmartTracklistAsync(string id)
        {
            try
            {
                var smartTracklist = await _deezerGatewayService.GetSmartTracklistPageAsync(id);
                if (smartTracklist == null)
                {
                    return null;
                }

                var results = smartTracklist["results"] as JObject ?? smartTracklist;
                var data = results["DATA"] as JObject ?? results["data"] as JObject;
                var songs = results["SONGS"] as JObject ?? results["songs"] as JObject;
                var songsData = songs?["data"] as JArray ?? songs?["DATA"] as JArray;
                if (data == null || songsData == null)
                {
                    return null;
                }

                var coverMd5 = data[CoverUpperField]?["MD5"]?.ToString()
                               ?? data[CoverUpperField]?["md5"]?.ToString()
                               ?? data.Value<string>(CoverUpperField);
                var subtitle = data.Value<string>("SUBTITLE") ?? string.Empty;
                var description = data.Value<string>(DescriptionUpperField) ?? string.Empty;
                var nbTracks = data.Value<int?>(NbSongUpperField);
                var coverXl = string.IsNullOrWhiteSpace(coverMd5)
                    ? string.Empty
                    : $"https://e-cdns-images.dzcdn.net/images/cover/{coverMd5}/1000x1000-000000-80-0-0.jpg";
                var coverBig = string.IsNullOrWhiteSpace(coverMd5)
                    ? string.Empty
                    : $"https://e-cdns-images.dzcdn.net/images/cover/{coverMd5}/500x500-000000-80-0-0.jpg";

                var tracks = songsData.Select((trackItem, index) =>
                {
                    var track = trackItem as JObject;
                    var trackId = track?.Value<string>(SngIdUpperField)
                                  ?? track?.Value<string>("id")
                                  ?? string.Empty;
                    var title = track?.Value<string>("SNG_TITLE")
                                ?? track?.Value<string>(TitleField)
                                ?? string.Empty;
                    var artistId = track?.Value<string>(ArtIdUpperField)
                                   ?? track?[ArtistType]?.Value<string>("id")
                                   ?? string.Empty;
                    var artistName = track?.Value<string>(ArtNameUpperField)
                                     ?? track?[ArtistType]?.Value<string>("name")
                                     ?? string.Empty;
                    var albumId = track?.Value<string>(AlbIdUpperField)
                                   ?? track?[AlbumType]?.Value<string>("id")
                                   ?? string.Empty;
                    var albumTitle = track?.Value<string>(AlbTitleUpperField)
                                      ?? track?[AlbumType]?.Value<string>(TitleField)
                                      ?? string.Empty;
                    var albumPicture = track?.Value<string>(AlbPictureUpperField)
                                       ?? track?[AlbumType]?.Value<string>("md5_image")
                                       ?? track?[AlbumType]?.Value<string>(CoverImageType);
                    var albumCover = string.IsNullOrWhiteSpace(albumPicture)
                        ? string.Empty
                        : BuildDeezerImage(CoverImageType, albumPicture) ?? string.Empty;
                    var trackToken = track?.Value<string>("TRACK_TOKEN")
                                     ?? track?["track_token"]?.ToString()
                                     ?? string.Empty;
                    var md5Origin = track?.Value<string>("MD5_ORIGIN")
                                    ?? track?["md5_origin"]?.ToString()
                                    ?? string.Empty;
                    var mediaVersion = track?.Value<string>("MEDIA_VERSION")
                                       ?? track?["media_version"]?.ToString()
                                       ?? string.Empty;
                    var fallbackTrackId = track?["FALLBACK"]?[SngIdUpperField]?.ToString()
                                         ?? track?["fallback"]?[SngIdUpperField]?.ToString()
                                         ?? track?["fallback_id"]?.ToString()
                                         ?? string.Empty;
                    var streamTrackId = !string.IsNullOrWhiteSpace(fallbackTrackId)
                        ? fallbackTrackId
                        : trackId;
                    var duration = track?.Value<int?>(DurationUpperField)
                                   ?? track?.Value<int?>(DurationField)
                                   ?? 0;
                    var position = track?.Value<int?>("TRACK_NUMBER")
                                   ?? track?.Value<int?>("POSITION")
                                   ?? index + 1;
                    var isrc = track?.Value<string>("ISRC")
                        ?? track?.Value<string>("isrc")
                        ?? string.Empty;

                    return new
                    {
                        id = trackId,
                        title,
                        duration,
                        isrc,
                        track_position = position,
                        stream_track_id = streamTrackId,
                        track_token = trackToken,
                        md5_origin = md5Origin,
                        media_version = mediaVersion,
                        fallback_id = fallbackTrackId,
                        artist = new { id = artistId, name = artistName },
                        album = new { id = albumId, title = albumTitle, cover_medium = albumCover }
                    };
                }).ToList();

                return Ok(new
                {
                    id = data.Value<string>("SMARTTRACKLIST_ID") ?? id,
                    title = data.Value<string>(TitleUpperField) ?? string.Empty,
                    subtitle,
                    description,
                    artist = string.IsNullOrWhiteSpace(subtitle) ? null : new { name = subtitle },
                    cover_xl = coverXl,
                    cover_big = coverBig,
                    picture_xl = coverXl,
                    picture_big = coverBig,
                    nb_tracks = nbTracks ?? tracks.Count,
                    tracks
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Gateway smarttracklist fetch failed for SmartTracklistId");
                return null;
            }
        }

        private async Task<IActionResult?> TryGetGatewayShowTracklistAsync(string id)
        {
            try
            {
                var showPage = await _deezerGatewayService.GetShowPageAsync(id);
                if (showPage == null)
                {
                    return null;
                }

                var results = showPage["results"] as JObject ?? showPage;
                var data = results["DATA"] as JObject ?? results["data"] as JObject;
                var episodes = results["EPISODES"] as JObject ?? results["episodes"] as JObject;
                var episodesData = episodes?["data"] as JArray ?? episodes?["DATA"] as JArray;
                if (data == null || episodesData == null)
                {
                    return null;
                }

                var pictureMd5 = data.Value<string>("SHOW_ART_MD5")
                                 ?? data.Value<string>("SHOW_PICTURE")
                                 ?? data.Value<string>(PictureUpperField)
                                 ?? data.Value<string>("picture");
                var pictureXl = string.IsNullOrWhiteSpace(pictureMd5)
                    ? string.Empty
                    : $"https://e-cdns-images.dzcdn.net/images/talk/{pictureMd5}/1000x1000-000000-80-0-0.jpg";
                var pictureBig = string.IsNullOrWhiteSpace(pictureMd5)
                    ? string.Empty
                    : $"https://e-cdns-images.dzcdn.net/images/talk/{pictureMd5}/500x500-000000-80-0-0.jpg";
                var showTitle = data.Value<string>("SHOW_NAME")
                                ?? data.Value<string>(TitleUpperField)
                                ?? string.Empty;
                var showDescription = data.Value<string>("SHOW_DESCRIPTION")
                                      ?? data.Value<string>(DescriptionUpperField)
                                      ?? string.Empty;

                var tracks = episodesData.Select((episodeToken, index) =>
                {
                    var episode = episodeToken as JObject;
                    var episodeId = episode?.Value<string>("EPISODE_ID")
                                   ?? episode?.Value<string>("id")
                                   ?? string.Empty;
                    var title = episode?.Value<string>("EPISODE_TITLE")
                                ?? episode?.Value<string>(TitleField)
                                ?? string.Empty;
                    var episodeUrl = episode?.Value<string>("EPISODE_DIRECT_STREAM_URL")
                                     ?? episode?.Value<string>("EPISODE_URL")
                                     ?? episode?.Value<string>("link")
                                     ?? string.Empty;
                    var duration = episode?.Value<int?>(DurationUpperField)
                                   ?? episode?.Value<int?>(DurationField)
                                   ?? 0;
                    var position = episode?.Value<int?>("EPISODE_NUMBER")
                                   ?? episode?.Value<int?>("POSITION")
                                   ?? index + 1;

                    return new
                    {
                        id = episodeId,
                        title,
                        duration,
                        track_position = position,
                        type = EpisodeType,
                        content_type = "podcast",
                        is_episode = true,
                        artist = new { id, name = showTitle },
                        album = new { id, title = showTitle, cover_medium = pictureBig },
                        link = episodeUrl,
                        preview = episodeUrl
                    };
                }).ToList();

                return Ok(new
                {
                    id,
                    type = ShowType,
                    content_type = "podcast",
                    title = showTitle,
                    description = showDescription,
                    picture_xl = pictureXl,
                    picture_big = pictureBig,
                    nb_tracks = tracks.Count,
                    tracks
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Gateway show tracklist fetch failed for ShowId");
                return null;
            }
        }

        private async Task<IActionResult> GetTrackAsAlbumTracklistAsync(HttpClient httpClient, string id)
        {
            var trackResponse = await httpClient.GetAsync($"https://api.deezer.com/track/{id}");
            if (!trackResponse.IsSuccessStatusCode)
            {
                return NotFound("Track not found");
            }

            var trackContent = await trackResponse.Content.ReadAsStringAsync();
            using var trackDoc = JsonDocument.Parse(trackContent);
            if (trackDoc.RootElement.TryGetProperty("error", out _))
            {
                return NotFound("Track not found");
            }

            if (trackDoc.RootElement.TryGetProperty(AlbumType, out var albumElement)
                && albumElement.TryGetProperty("id", out var albumIdElement))
            {
                var albumId = albumIdElement.GetInt64().ToString();
                var albumResponse = await httpClient.GetAsync($"https://api.deezer.com/album/{albumId}");
                if (albumResponse.IsSuccessStatusCode)
                {
                    var albumContent = await albumResponse.Content.ReadAsStringAsync();
                    var albumTracksResponse = await httpClient.GetAsync($"https://api.deezer.com/album/{albumId}/tracks");
                    if (albumTracksResponse.IsSuccessStatusCode)
                    {
                        var albumTracksContent = await albumTracksResponse.Content.ReadAsStringAsync();
                        var albumObj = JsonSerializer.Deserialize<Dictionary<string, object>>(albumContent);
                        var albumTracksObj = JsonSerializer.Deserialize<Dictionary<string, object>>(albumTracksContent);
                        if (albumObj != null && albumTracksObj != null && albumTracksObj.TryGetValue("data", out var albumTracksData))
                        {
                            albumObj[TracksField] = albumTracksData;
                            albumObj["selected_track_id"] = id;
                            return Ok(albumObj);
                        }
                    }
                }
            }

            var trackData = JsonSerializer.Deserialize<Dictionary<string, object>>(trackContent);
            if (trackData != null)
            {
                var trackItem = new Dictionary<string, object>(trackData, StringComparer.OrdinalIgnoreCase);
                trackItem.Remove(TracksField);
                trackItem.Remove(NbTracksField);
                trackData[TracksField] = new[] { trackItem };
                trackData[NbTracksField] = 1;
                return Ok(trackData);
            }

            return Content(trackContent, ApplicationJsonContentType);
        }

        private async Task<IActionResult> GetDefaultTracklistAsync(HttpClient httpClient, string id, string type)
        {
            var apiUrl = type.ToLowerInvariant() switch
            {
                AlbumType => $"https://api.deezer.com/album/{id}",
                PlaylistType => $"https://api.deezer.com/playlist/{id}",
                _ => string.Empty
            };
            if (string.IsNullOrWhiteSpace(apiUrl))
            {
                return BadRequest($"Unsupported type: {type}");
            }

            var response = await httpClient.GetAsync(apiUrl);
            if (!response.IsSuccessStatusCode)
            {
                return NotFound($"{type} not found");
            }

            var content = await response.Content.ReadAsStringAsync();
            var tracksResponse = await httpClient.GetAsync($"{apiUrl}/tracks");
            if (!tracksResponse.IsSuccessStatusCode)
            {
                return Content(content, ApplicationJsonContentType);
            }

            var tracksContent = await tracksResponse.Content.ReadAsStringAsync();
            var mainObj = JsonSerializer.Deserialize<Dictionary<string, object>>(content);
            var tracksObj = JsonSerializer.Deserialize<Dictionary<string, object>>(tracksContent);
            if (mainObj != null && tracksObj != null && tracksObj.TryGetValue("data", out var tracksData))
            {
                mainObj[TracksField] = tracksData;
            }

            return Ok(mainObj);
        }

        [HttpGet("artist-page")]
        public async Task<IActionResult> GetArtistPage([FromQuery] string id, [FromQuery] string? source, [FromQuery] string? refresh = null, CancellationToken cancellationToken = default)
        {
            var startedUtc = DateTimeOffset.UtcNow;
            if (string.IsNullOrWhiteSpace(id))
            {
                return BadRequest("ID is required");
            }

            var refreshRequested = IsTruthyQueryFlag(refresh);
            var normalizedSource = string.IsNullOrWhiteSpace(source) ? DeezerSource : source.Trim().ToLowerInvariant();
            if (!IsSupportedArtistPageSource(normalizedSource))
            {
                return BadRequest("Unsupported source");
            }

            if (normalizedSource == SpotifySource)
            {
                return await BuildSpotifyArtistPageResponseAsync(id, refreshRequested, startedUtc, cancellationToken);
            }

            var cacheKey = $"{normalizedSource}:{id}";
            var existingCache = await GetArtistPageCacheSnapshotAsync(cacheKey, refreshRequested, cancellationToken);
            var cachedResponse = TryGetCachedArtistPageResponseAsync(
                id,
                normalizedSource,
                startedUtc,
                refreshRequested,
                existingCache,
                cancellationToken);
            if (cachedResponse != null)
            {
                return cachedResponse;
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Artist page cache miss. refresh={Refresh}", refreshRequested);
            }
            return await BuildFetchedArtistPageResponseAsync(
                id,
                normalizedSource,
                cacheKey,
                startedUtc,
                existingCache,
                cancellationToken);
        }

        private static bool IsSupportedArtistPageSource(string source) =>
            source == DeezerSource || source == AppleSource || source == SpotifySource;

        private async Task<ArtistCacheEntry?> GetArtistPageCacheSnapshotAsync(
            string cacheKey,
            bool refreshRequested,
            CancellationToken cancellationToken)
        {
            if (refreshRequested)
            {
                return await _artistPageCache.TryGetAsync(ArtistPageCacheSource, cacheKey, cancellationToken);
            }

            var existingCache = await _artistPageCache.TryGetAsync(ArtistPageCacheSource, cacheKey, cancellationToken);
            if (existingCache == null || !_artistPageCache.IsUsable(existingCache.FetchedUtc))
            {
                return existingCache;
            }

            if (!HasArtistPageExtras(existingCache.PayloadJson))
            {
                _logger.LogInformation("Artist page cache missing extras");
                return existingCache;
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Artist page cache hit. fresh={IsFresh}", _artistPageCache.IsFresh(existingCache.FetchedUtc));
            }
            return existingCache;
        }

        private ContentResult? TryGetCachedArtistPageResponseAsync(
            string id,
            string normalizedSource,
            DateTimeOffset startedUtc,
            bool refreshRequested,
            ArtistCacheEntry? existingCache,
            CancellationToken cancellationToken)
        {
            if (refreshRequested || existingCache == null || !_artistPageCache.IsUsable(existingCache.FetchedUtc) || !HasArtistPageExtras(existingCache.PayloadJson))
            {
                return null;
            }

            if (!_artistPageCache.IsFresh(existingCache.FetchedUtc))
            {
                _ = Task.Run(() => RefreshArtistPageCacheAsync(id, normalizedSource), cancellationToken);
            }

            var elapsedMs = (DateTimeOffset.UtcNow - startedUtc).TotalMilliseconds;
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Artist page response (cache). elapsed_ms={ElapsedMs}", elapsedMs);
            }
            return Content(existingCache.PayloadJson, ApplicationJsonContentType);
        }

        private async Task<IActionResult> BuildFetchedArtistPageResponseAsync(
            string id,
            string normalizedSource,
            string cacheKey,
            DateTimeOffset startedUtc,
            ArtistCacheEntry? existingCache,
            CancellationToken cancellationToken)
        {
            try
            {
                using var httpClient = _httpClientFactory.CreateClient();
                var payload = await BuildArtistPageAsync(httpClient, id, normalizedSource, cancellationToken);
                if (payload == null)
                {
                    return NotFound("Artist not found");
                }

                var payloadJson = JsonSerializer.Serialize(payload);
                var releaseCount = CountDiscographyEntries(payload);
                var existingReleaseCount = existingCache == null ? 0 : CountDiscographyEntries(existingCache.PayloadJson);
                if (normalizedSource == DeezerSource && releaseCount == 0 && existingReleaseCount > 0)
                {
                    _logger.LogWarning(
                        "Returning previous artist-page payload to preserve non-empty discography. existing_releases={ExistingCount}",
                        existingReleaseCount);
                    var fallbackElapsedMs = (DateTimeOffset.UtcNow - startedUtc).TotalMilliseconds;
                    if (_logger.IsEnabled(LogLevel.Information))
                    {
                        _logger.LogInformation("Artist page response (fallback-cache). elapsed_ms={ElapsedMs}", fallbackElapsedMs);
                    }
                    return Content(existingCache!.PayloadJson, ApplicationJsonContentType);
                }

                if (normalizedSource != DeezerSource || releaseCount > 0)
                {
                    await _artistPageCache.UpsertAsync(ArtistPageCacheSource, cacheKey, payloadJson, DateTimeOffset.UtcNow, cancellationToken);
                }
                else
                {
                    _logger.LogWarning("Skipping artist-page cache write with empty discography");
                }

                var elapsedMs = (DateTimeOffset.UtcNow - startedUtc).TotalMilliseconds;
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Artist page response (fetch). elapsed_ms={ElapsedMs}", elapsedMs);
                }
                return Ok(payload);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var elapsedMs = (DateTimeOffset.UtcNow - startedUtc).TotalMilliseconds;
                _logger.LogWarning("Artist page response (error). elapsed_ms={ElapsedMs}", elapsedMs);
                _logger.LogError(ex, "Error building artist page response");
                return StatusCode(500, "Error fetching artist data");
            }
        }

        private async Task<IActionResult> BuildSpotifyArtistPageResponseAsync(
            string id,
            bool refreshRequested,
            DateTimeOffset startedUtc,
            CancellationToken cancellationToken)
        {
            try
            {
                var payload = await BuildSpotifyArtistPageAsync(id, refreshRequested, cancellationToken);
                if (payload == null)
                {
                    return NotFound("Artist not found");
                }

                var elapsedMs = (DateTimeOffset.UtcNow - startedUtc).TotalMilliseconds;
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Artist page response (spotify-service). refresh={Refresh} elapsed_ms={ElapsedMs}", refreshRequested, elapsedMs);
                }
                return Ok(payload);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var elapsedMs = (DateTimeOffset.UtcNow - startedUtc).TotalMilliseconds;
                _logger.LogWarning("Artist page response (spotify-service-error). elapsed_ms={ElapsedMs}", elapsedMs);
                _logger.LogError(ex, "Error building Spotify artist page");
                return StatusCode(500, "Error fetching artist data");
            }
        }


        private static string? GetPropertyOrDefaultSafe(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            {
                return null;
            }
            return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        }

        private static string? GetNestedString(JsonElement element, string objectName, string propertyName)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(objectName, out var nested))
            {
                return null;
            }

            if (nested.ValueKind != JsonValueKind.Object || !nested.TryGetProperty(propertyName, out var value))
            {
                return null;
            }

            return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        }

        private static bool HasArtistPageExtras(string? payloadJson)
        {
            if (string.IsNullOrWhiteSpace(payloadJson))
            {
                return false;
            }

            try
            {
                using var doc = JsonDocument.Parse(payloadJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                return doc.RootElement.TryGetProperty("playlists", out var playlists)
                       && playlists.ValueKind == JsonValueKind.Array
                       && doc.RootElement.TryGetProperty(RelatedField, out var related)
                       && related.ValueKind == JsonValueKind.Array
                       && HasDiscographyEntries(doc.RootElement);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return false;
            }
        }

        private static bool HasDiscographyEntries(JsonElement root)
        {
            return CountDiscographyEntries(root) > 0;
        }

        private static int CountDiscographyEntries(Dictionary<string, object>? payload)
        {
            if (payload == null || !payload.TryGetValue(ReleasesField, out var releasesObj))
            {
                return 0;
            }

            try
            {
                var payloadJson = JsonSerializer.Serialize(releasesObj);
                using var doc = JsonDocument.Parse(payloadJson);
                return CountReleaseEntriesElement(doc.RootElement);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return 0;
            }
        }

        private static int CountDiscographyEntries(string? payloadJson)
        {
            if (string.IsNullOrWhiteSpace(payloadJson))
            {
                return 0;
            }

            try
            {
                using var doc = JsonDocument.Parse(payloadJson);
                return CountDiscographyEntries(doc.RootElement);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return 0;
            }
        }

        private static int CountDiscographyEntries(JsonElement root)
        {
            if (!root.TryGetProperty(ReleasesField, out var releases) || releases.ValueKind != JsonValueKind.Object)
            {
                return 0;
            }

            var count = 0;
            count += releases.EnumerateObject().Sum(static property => CountReleaseEntriesElement(property.Value));

            return count;
        }

        private static int CountReleaseEntriesElement(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                return element.GetArrayLength();
            }

            if (element.ValueKind == JsonValueKind.Object)
            {
                var count = 0;
                foreach (var property in element.EnumerateObject())
                {
                    count += CountReleaseEntriesElement(property.Value);
                }

                return count;
            }

            return 0;
        }

        private async Task<IActionResult> GetArtistData(HttpClient httpClient, string artistId)
        {
            var cached = await _artistPageCache.TryGetAsync(DeezerSource, artistId, CancellationToken.None);
            if (cached != null && _artistPageCache.IsUsable(cached.FetchedUtc))
            {
                _logger.LogInformation("Artist cache hit (deezer). id=ArtistId fresh=IsFresh");
                if (!_artistPageCache.IsFresh(cached.FetchedUtc))
                {
                    _ = Task.Run(() => RefreshDeezerArtistCacheAsync(artistId));
                }

                return Content(cached.PayloadJson, ApplicationJsonContentType);
            }
            _logger.LogInformation("Artist cache miss (deezer). id=ArtistId");

            try
            {
                var artistData = await FetchDeezerArtistDataAsync(httpClient, artistId);
                if (artistData == null)
                {
                    return NotFound("Artist not found");
                }

                var payloadJson = JsonSerializer.Serialize(artistData);
                await _artistPageCache.UpsertAsync(DeezerSource, artistId, payloadJson, DateTimeOffset.UtcNow, CancellationToken.None);

                return Ok(artistData);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error fetching artist data for ID: ArtistId");
                return StatusCode(500, "Error fetching artist data");
            }
        }

        private async Task<Dictionary<string, object>?> BuildArtistPageAsync(HttpClient httpClient, string id, string source, CancellationToken cancellationToken)
        {
            if (source == AppleSource)
            {
                return await BuildAppleArtistPageAsync(id, cancellationToken);
            }

            if (source == SpotifySource)
            {
                return await BuildSpotifyArtistPageAsync(id, forceRefresh: false, cancellationToken);
            }

            var deezerData = await FetchDeezerArtistDataAsync(httpClient, id);
            if (deezerData == null)
            {
                return null;
            }

            if (deezerData.TryGetValue("name", out var nameObj))
            {
                var artistName = nameObj?.ToString() ?? string.Empty;
                await TryApplySpotifyHeroAsync(deezerData, artistName, cancellationToken);
            }

            return deezerData;
        }

        private async Task<Dictionary<string, object>?> BuildSpotifyArtistPageAsync(string spotifyArtistId, bool forceRefresh, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(spotifyArtistId))
            {
                return null;
            }

            var artistPage = await _spotifyArtistService.GetArtistPageBySpotifyIdAsync(
                spotifyArtistId,
                spotifyArtistId,
                forceRefresh: forceRefresh,
                cancellationToken);
            if (artistPage is null || !artistPage.Available)
            {
                return null;
            }

            return DeezSpoTag.Web.Services.SpotifyArtistPagePayloadMapper.Build(artistPage);
        }

        private async Task TryApplySpotifyHeroAsync(
            Dictionary<string, object> deezerData,
            string artistName,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(artistName))
            {
                return;
            }

            var settings = _settingsService.LoadSettings();
            var threshold = settings.SpotifyHeroDiscographyMatchThreshold;
            if (threshold <= 0 || threshold > 1)
            {
                threshold = 0.6;
            }

            var spotifyResult = await _spotifyArtistService.GetArtistPageByNameAsync(artistName, cancellationToken);
            if (spotifyResult == null)
            {
                return;
            }

            var deezerTitles = ExtractDeezerReleaseTitles(deezerData.TryGetValue(ReleasesField, out var releasesObj) ? releasesObj : null);
            var spotifyTitles = new HashSet<string>(
                spotifyResult.Albums.Select(album => NormalizeTitle(album.Name)).Where(title => !string.IsNullOrWhiteSpace(title)),
                StringComparer.OrdinalIgnoreCase);

            var overrideNames = new HashSet<string>(
                settings.SpotifyHeroOverrideArtistNames ?? new List<string>(),
                StringComparer.OrdinalIgnoreCase);
            var forceOverride = !string.IsNullOrWhiteSpace(artistName) && overrideNames.Contains(artistName.Trim());
            var matchStats = GetDiscographyMatchStats(deezerTitles, spotifyTitles);
            _logger.LogInformation("Spotify hero match stats for ArtistName (deezer_id=DeezerId): deezer=DeezerCount, spotify=SpotifyCount, matches=MatchCount, deezer_ratio=DeezerRatio:P0, spotify_ratio=SpotifyRatio:P0, threshold=Threshold:P0, override=Override");

            if (!forceOverride && !HasDiscographyMatch(matchStats, threshold))
            {
                return;
            }

            var heroUrl = spotifyResult.Artist.HeaderImageUrl;
            if (string.IsNullOrWhiteSpace(heroUrl) && spotifyResult.Artist.Images.Count > 0)
            {
                heroUrl = spotifyResult.Artist.Images[0].Url;
            }

            if (string.IsNullOrWhiteSpace(heroUrl))
            {
                return;
            }

            deezerData["spotify_hero"] = heroUrl;
        }

        private static bool HasDiscographyMatch(DiscographyMatchStats stats, double threshold)
        {
            if (stats.DeezerCount == 0 || stats.SpotifyCount == 0)
            {
                return false;
            }

            return stats.DeezerCoverage >= threshold && stats.SpotifyCoverage >= threshold;
        }

        private static DiscographyMatchStats GetDiscographyMatchStats(
            HashSet<string> deezerTitles,
            HashSet<string> spotifyTitles)
        {
            if (deezerTitles.Count == 0 || spotifyTitles.Count == 0)
            {
                return new DiscographyMatchStats(0, 0, 0, 0, 0);
            }

            var matchCount = deezerTitles.Count(title => spotifyTitles.Contains(title));
            var deezerCoverage = matchCount / (double)deezerTitles.Count;
            var spotifyCoverage = matchCount / (double)spotifyTitles.Count;
            return new DiscographyMatchStats(deezerTitles.Count, spotifyTitles.Count, matchCount, deezerCoverage, spotifyCoverage);
        }

        private sealed record DiscographyMatchStats(
            int DeezerCount,
            int SpotifyCount,
            int MatchCount,
            double DeezerCoverage,
            double SpotifyCoverage);

        private static HashSet<string> ExtractDeezerReleaseTitles(object? releasesObj)
        {
            var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (releasesObj == null)
            {
                return titles;
            }

            if (releasesObj is IDictionary<string, object> dict)
            {
                foreach (var entry in dict.Values)
                {
                    AddReleaseTitles(entry, titles);
                }
                return titles;
            }

            if (releasesObj is JObject jObject)
            {
                foreach (var prop in jObject.Properties())
                {
                    AddReleaseTitles(prop.Value, titles);
                }
                return titles;
            }

            AddReleaseTitles(releasesObj, titles);
            return titles;
        }

        private static void AddReleaseTitles(object? collection, HashSet<string> titles)
        {
            switch (collection)
            {
                case null:
                    return;
                case JArray jArray:
                    foreach (var token in jArray)
                    {
                        AddReleaseTitles(token, titles);
                    }
                    return;
                case IEnumerable<object> list when collection is not string:
                    foreach (var item in list)
                    {
                        AddReleaseTitles(item, titles);
                    }
                    return;
                case JObject jObject:
                    AddNormalizedTitle(jObject.Value<string>(TitleField), titles);
                    return;
                case JsonElement element when element.ValueKind == JsonValueKind.Object
                                             && element.TryGetProperty(TitleField, out var titleProp)
                                             && titleProp.ValueKind == JsonValueKind.String:
                    AddNormalizedTitle(titleProp.GetString(), titles);
                    return;
                case IDictionary<string, object> dict when dict.TryGetValue(TitleField, out var titleObj):
                    AddNormalizedTitle(titleObj?.ToString(), titles);
                    return;
            }
        }

        private static void AddNormalizedTitle(string? title, HashSet<string> titles)
        {
            var normalized = NormalizeTitle(title);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                titles.Add(normalized);
            }
        }

        private static Dictionary<string, string> BuildReleaseDateByAlbumId(object? releasesObj)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (releasesObj == null)
            {
                return result;
            }

            try
            {
                var payload = JsonSerializer.Serialize(releasesObj);
                using var doc = JsonDocument.Parse(payload);
                CollectReleaseDates(doc.RootElement, result);
            }
            catch (System.Text.Json.JsonException)
            {
                return result;
            }

            return result;
        }

        private static void CollectReleaseDates(JsonElement element, Dictionary<string, string> target)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("id", out var idProp)
                    && element.TryGetProperty(ReleaseDateField, out var releaseDateProp))
                {
                    var albumId = ReadJsonScalar(idProp);
                    var releaseDate = ReadJsonScalar(releaseDateProp);
                    if (!string.IsNullOrWhiteSpace(albumId) && !string.IsNullOrWhiteSpace(releaseDate))
                    {
                        target[albumId] = releaseDate;
                    }
                }

                foreach (var property in element.EnumerateObject())
                {
                    CollectReleaseDates(property.Value, target);
                }

                return;
            }

            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    CollectReleaseDates(item, target);
                }
            }
        }

        private static bool TryGetTrackAlbumId(JsonElement track, out string albumId)
        {
            albumId = string.Empty;
            if (!track.TryGetProperty(AlbumType, out var album) || album.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!album.TryGetProperty("id", out var idProp))
            {
                return false;
            }

            albumId = ReadJsonScalar(idProp) ?? string.Empty;
            return !string.IsNullOrWhiteSpace(albumId);
        }

        private static void ApplyAlbumReleaseDate(Dictionary<string, object> trackObj, string releaseDate)
        {
            if (!trackObj.TryGetValue(AlbumType, out var albumObj) || albumObj == null)
            {
                return;
            }

            if (albumObj is Dictionary<string, object> albumDict)
            {
                albumDict[ReleaseDateField] = releaseDate;
                trackObj[AlbumType] = albumDict;
                return;
            }

            if (albumObj is IDictionary<string, object> albumMap)
            {
                albumMap[ReleaseDateField] = releaseDate;
                trackObj[AlbumType] = albumMap;
                return;
            }

            if (albumObj is JsonElement albumElement && albumElement.ValueKind == JsonValueKind.Object)
            {
                var normalized = JsonSerializer.Deserialize<Dictionary<string, object>>(albumElement.GetRawText())
                    ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                normalized[ReleaseDateField] = releaseDate;
                trackObj[AlbumType] = normalized;
            }
        }

        private static string? ReadJsonScalar(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.GetRawText(),
                _ => null
            };
        }

        private static string NormalizeTitle(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return string.Empty;
            }

            var buffer = new char[input.Length];
            var length = 0;
            var lastWasSpace = false;
            foreach (var ch in input)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    buffer[length++] = char.ToLowerInvariant(ch);
                    lastWasSpace = false;
                }
                else if (!lastWasSpace)
                {
                    buffer[length++] = ' ';
                    lastWasSpace = true;
                }
            }

            return new string(buffer, 0, length).Trim();
        }

        private async Task<Dictionary<string, object>?> BuildAppleArtistPageAsync(string id, CancellationToken cancellationToken)
        {
            using var artistDoc = await _appleCatalog.GetArtistAsync(id, "us", "en-US", cancellationToken);
            if (!artistDoc.RootElement.TryGetProperty(DataField, out var artistData) || artistData.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var artistItem = artistData.EnumerateArray().FirstOrDefault();
            if (artistItem.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var name = GetNestedString(artistItem, AttributesField, NameField) ?? "Unknown Artist";
            var genres = Array.Empty<string>();
            var hasAttrs = artistItem.TryGetProperty(AttributesField, out var attrs) && attrs.ValueKind == JsonValueKind.Object;
            if (hasAttrs
                && attrs.TryGetProperty("genreNames", out var genreNames)
                && genreNames.ValueKind == JsonValueKind.Array)
            {
                genres = genreNames.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? string.Empty)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .ToArray();
            }

            var artistArtwork = hasAttrs ? ExtractAppleArtworkUrl(attrs, 1000) : null;
            var releases = await FetchAppleArtistAlbumsAsync(id, name, cancellationToken);

            return new Dictionary<string, object>
            {
                ["id"] = id,
                [NameField] = name,
                ["genres"] = genres,
                ["picture_xl"] = artistArtwork ?? string.Empty,
                ["picture_big"] = artistArtwork ?? string.Empty,
                ["picture_medium"] = artistArtwork ?? string.Empty,
                [ReleasesField] = releases
            };
        }

        private async Task<Dictionary<string, object>> FetchAppleArtistAlbumsAsync(string id, string artistName, CancellationToken cancellationToken)
        {
            using var albumsDoc = await _appleCatalog.GetArtistAlbumsAsync(id, "us", "en-US", limit: 100, offset: 0, cancellationToken);
            if (!albumsDoc.RootElement.TryGetProperty(DataField, out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return new Dictionary<string, object>();
            }

            var httpClient = _httpClientFactory.CreateClient();
            var releasesByType = new Dictionary<string, List<Dictionary<string, object>>>(StringComparer.OrdinalIgnoreCase);
            foreach (var album in data.EnumerateArray())
            {
                var releaseEntry = await TryBuildAppleReleaseEntryAsync(httpClient, album, artistName, cancellationToken);
                if (releaseEntry == null)
                {
                    continue;
                }

                if (!releasesByType.TryGetValue(releaseEntry.RecordType, out var bucket))
                {
                    bucket = new List<Dictionary<string, object>>();
                    releasesByType[releaseEntry.RecordType] = bucket;
                }

                bucket.Add(releaseEntry.Entry);
            }

            return BuildOrderedReleaseBuckets(releasesByType);
        }

        private static string ResolveAppleRecordType(bool isCompilation, bool isSingle)
        {
            if (isCompilation)
            {
                return "compile";
            }

            return isSingle ? SingleType : AlbumType;
        }

        private static Dictionary<string, object> BuildOrderedReleaseBuckets(
            Dictionary<string, List<Dictionary<string, object>>> releasesByType)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var orderedKeys = new[] { AlbumType, SingleType, "compile" };
            foreach (var pair in orderedKeys
                         .Select(key => (key, list: releasesByType.TryGetValue(key, out var list) ? list : null))
                         .Where(static pair => pair.list is { Count: > 0 }))
            {
                result[pair.key] = pair.list!;
            }

            return result;
        }

        private static async Task<AppleReleaseEntry?> TryBuildAppleReleaseEntryAsync(
            HttpClient httpClient,
            JsonElement album,
            string artistName,
            CancellationToken cancellationToken)
        {
            if (album.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var albumId = album.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(albumId))
            {
                return null;
            }

            if (!album.TryGetProperty(AttributesField, out var attributes) || attributes.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var title = GetPropertyOrDefaultSafe(attributes, NameField) ?? string.Empty;
            var releaseDate = GetPropertyOrDefaultSafe(attributes, "releaseDate") ?? string.Empty;
            var trackCount = attributes.TryGetProperty("trackCount", out var tracksProp) && tracksProp.ValueKind == JsonValueKind.Number
                && tracksProp.TryGetInt32(out var tracks)
                ? tracks
                : 0;
            var contentRating = GetPropertyOrDefaultSafe(attributes, "contentRating");
            var isSingle = attributes.TryGetProperty("isSingle", out var isSingleProp) && isSingleProp.ValueKind == JsonValueKind.True;
            var isCompilation = attributes.TryGetProperty("isCompilation", out var isCompilationProp) && isCompilationProp.ValueKind == JsonValueKind.True;
            var link = GetPropertyOrDefaultSafe(attributes, UrlField) ?? string.Empty;
            var cover = ExtractAppleArtworkUrl(attributes, 1000) ?? string.Empty;
            var coverSmall = ExtractAppleArtworkUrl(attributes, 300) ?? cover;

            var deezerMatch = await ResolveDeezerAlbumMatchAsync(httpClient, artistName, title, releaseDate, cancellationToken);
            var releaseSource = deezerMatch != null ? DeezerSource : AppleSource;
            var releaseId = deezerMatch?.Id ?? albumId;
            var releaseLink = deezerMatch?.Url ?? link;
            var recordType = ResolveAppleRecordType(isCompilation, isSingle);

            var entry = new Dictionary<string, object>
            {
                ["id"] = releaseId,
                [TitleField] = title,
                [ReleaseDateField] = releaseDate,
                [NbTracksField] = trackCount,
                ["explicit_lyrics"] = string.Equals(contentRating, "explicit", StringComparison.OrdinalIgnoreCase),
                [RecordTypeField] = recordType,
                ["link"] = releaseLink,
                [CoverImageType] = cover,
                ["cover_small"] = coverSmall,
                [SourceField] = releaseSource
            };

            return new AppleReleaseEntry(recordType, entry);
        }

        private static string? ExtractAppleArtworkUrl(JsonElement attributes, int size)
        {
            if (!attributes.TryGetProperty("artwork", out var art) || art.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!art.TryGetProperty(UrlField, out var urlProp) || urlProp.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var template = urlProp.GetString();
            if (string.IsNullOrWhiteSpace(template))
            {
                return null;
            }

            return template
                .Replace("{w}", size.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
                .Replace("{h}", size.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<DeezerAlbumMatch?> ResolveDeezerAlbumMatchAsync(
            HttpClient httpClient,
            string artistName,
            string albumTitle,
            string releaseDate,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(artistName) || string.IsNullOrWhiteSpace(albumTitle))
            {
                return null;
            }

            var query = $"{artistName} {albumTitle}".Trim();
            var url = $"https://api.deezer.com/search/album?q={Uri.EscapeDataString(query)}&limit=8";
            using var response = await httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var targetArtist = NormalizeMatchToken(artistName);
            var targetTitle = NormalizeMatchToken(albumTitle);
            var targetYear = releaseDate.Length >= 4 ? releaseDate[..4] : string.Empty;

            foreach (var item in data.EnumerateArray())
            {
                if (!TryParseDeezerAlbumCandidate(item, out var candidate))
                {
                    continue;
                }

                var parsedCandidate = candidate!;
                if (!IsMatchingDeezerAlbumCandidate(parsedCandidate, targetArtist, targetTitle, targetYear))
                {
                    continue;
                }

                return new DeezerAlbumMatch(parsedCandidate.Id, parsedCandidate.Link ?? $"https://www.deezer.com/album/{parsedCandidate.Id}");
            }

            return null;
        }

        private static bool TryParseDeezerAlbumCandidate(JsonElement item, out DeezerAlbumCandidate? candidate)
        {
            candidate = default;
            if (item.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var id = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number
                ? idEl.GetInt64().ToString()
                : null;
            var title = item.TryGetProperty(TitleField, out var titleEl) ? titleEl.GetString() : null;
            var artist = item.TryGetProperty(ArtistType, out var artistEl) && artistEl.ValueKind == JsonValueKind.Object
                && artistEl.TryGetProperty("name", out var nameEl)
                ? nameEl.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
            {
                return false;
            }

            var year = item.TryGetProperty(ReleaseDateField, out var dateEl) ? dateEl.GetString() : null;
            var link = item.TryGetProperty("link", out var linkEl) ? linkEl.GetString() : null;
            candidate = new DeezerAlbumCandidate(id, title, artist, year, link);
            return true;
        }

        private static bool IsMatchingDeezerAlbumCandidate(
            DeezerAlbumCandidate candidate,
            string targetArtist,
            string targetTitle,
            string targetYear)
        {
            if (NormalizeMatchToken(candidate.Artist) != targetArtist)
            {
                return false;
            }

            if (NormalizeMatchToken(candidate.Title) != targetTitle)
            {
                return false;
            }

            return string.IsNullOrWhiteSpace(targetYear)
                   || string.IsNullOrWhiteSpace(candidate.Year)
                   || candidate.Year.StartsWith(targetYear, StringComparison.Ordinal);
        }

        private static string NormalizeMatchToken(string value)
        {
            var lowered = value.Trim().ToLowerInvariant();
            var buffer = new char[lowered.Length];
            var idx = 0;
            foreach (var ch in lowered)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    buffer[idx++] = ch;
                }
                else if (char.IsWhiteSpace(ch))
                {
                    buffer[idx++] = ' ';
                }
            }

            var cleaned = new string(buffer, 0, idx);
            return string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        private sealed record DeezerAlbumMatch(string Id, string Url);
        private sealed record DeezerAlbumCandidate(string Id, string Title, string Artist, string? Year, string? Link);
        private sealed record AppleReleaseEntry(string RecordType, Dictionary<string, object> Entry);
        private sealed record ArtworkLookupRequest(
            string? Title,
            string? Artist,
            string? Album,
            string? DeezerMd5,
            string DeezerType,
            bool IsArtist,
            bool AllowApple = true);

        private sealed record HomePlaylistContext(
            string? Title,
            string? Subtitle,
            string? Description,
            string? Target,
            string? OwnerId,
            string? OwnerName,
            bool IsLovedTrackPlaylist,
            string? CurrentDeezerUserId,
            string? CurrentDeezerUserName);

        private async Task<Dictionary<string, object>?> FetchDeezerArtistDataAsync(HttpClient httpClient, string artistId)
        {
            // Get artist basic info
            var artistResponse = await httpClient.GetAsync($"https://api.deezer.com/artist/{artistId}");
            if (!artistResponse.IsSuccessStatusCode)
            {
                return null;
            }

            var artistContent = await artistResponse.Content.ReadAsStringAsync();
            using var artistDoc = JsonDocument.Parse(artistContent);
            var artistData = JsonSerializer.Deserialize<Dictionary<string, object>>(artistContent);

            if (artistData == null)
            {
                return null;
            }

            // Get artist releases exactly like deezspotag does - structured by tabs
            var releases = await GetArtistDiscographyTabs(httpClient, artistId);
            var releaseDateByAlbumId = BuildReleaseDateByAlbumId(releases);

            await TryPopulateDeezerArtistTopTracksAsync(httpClient, artistId, artistData, releaseDateByAlbumId);
            await TryPopulateDeezerArtistPlaylistsAsync(httpClient, artistId, artistData);
            await TryPopulateDeezerRelatedArtistsAsync(httpClient, artistId, artistData);

            // Add releases to artist data exactly like deezspotag
            artistData[ReleasesField] = releases;

            return artistData;
        }

        private static void ApplyKnownReleaseDateToTrack(
            Dictionary<string, object> trackObj,
            JsonElement track,
            IReadOnlyDictionary<string, string> releaseDateByAlbumId)
        {
            if (TryGetTrackAlbumId(track, out var albumId)
                && releaseDateByAlbumId.TryGetValue(albumId, out var releaseDate)
                && !string.IsNullOrWhiteSpace(releaseDate))
            {
                trackObj[ReleaseDateField] = releaseDate;
                ApplyAlbumReleaseDate(trackObj, releaseDate);
            }
        }

        private static object[] BuildEnrichedTopTracks(JsonElement topData, IReadOnlyDictionary<string, string> releaseDateByAlbumId)
        {
            var enrichedTopTracks = new List<object>();
            foreach (var track in topData.EnumerateArray())
            {
                var trackObj = JsonSerializer.Deserialize<Dictionary<string, object>>(track.GetRawText())
                    ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                ApplyKnownReleaseDateToTrack(trackObj, track, releaseDateByAlbumId);
                enrichedTopTracks.Add(trackObj);
            }

            return enrichedTopTracks.ToArray();
        }

        private static async Task TryPopulateCollectionFromEndpointAsync(
            HttpClient httpClient,
            string endpoint,
            Dictionary<string, object> artistData,
            string targetField)
        {
            var response = await httpClient.GetAsync(endpoint);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            var content = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(content);
            if (!doc.RootElement.TryGetProperty("data", out var data))
            {
                return;
            }

            artistData[targetField] = JsonSerializer.Deserialize<object[]>(data.GetRawText()) ?? Array.Empty<object>();
        }

        private static async Task TryPopulateDeezerRelatedArtistsAsync(HttpClient httpClient, string artistId, Dictionary<string, object> artistData)
        {
            await TryPopulateCollectionFromEndpointAsync(
                httpClient,
                $"https://api.deezer.com/artist/{artistId}/related?limit=24",
                artistData,
                RelatedField);
        }

        private static async Task TryPopulateDeezerArtistPlaylistsAsync(HttpClient httpClient, string artistId, Dictionary<string, object> artistData)
        {
            await TryPopulateCollectionFromEndpointAsync(
                httpClient,
                $"https://api.deezer.com/artist/{artistId}/playlists?limit=25",
                artistData,
                "playlists");
        }

        private static async Task TryPopulateDeezerArtistTopTracksAsync(
            HttpClient httpClient,
            string artistId,
            Dictionary<string, object> artistData,
            IReadOnlyDictionary<string, string> releaseDateByAlbumId)
        {
            var topResponse = await httpClient.GetAsync($"https://api.deezer.com/artist/{artistId}/top?limit=50");
            if (!topResponse.IsSuccessStatusCode)
            {
                return;
            }

            var topContent = await topResponse.Content.ReadAsStringAsync();
            using var topDoc = JsonDocument.Parse(topContent);
            if (!topDoc.RootElement.TryGetProperty("data", out var topData))
            {
                return;
            }

            artistData["top_tracks"] = BuildEnrichedTopTracks(topData, releaseDateByAlbumId);
        }

        private async Task RefreshDeezerArtistCacheAsync(string artistId)
        {
            try
            {
                using var httpClient = _httpClientFactory.CreateClient();
                var artistData = await FetchDeezerArtistDataAsync(httpClient, artistId);
                if (artistData == null)
                {
                    return;
                }

                var payloadJson = JsonSerializer.Serialize(artistData);
                var releaseCount = CountDiscographyEntries(artistData);
                if (releaseCount > 0)
                {
                    await _artistPageCache.UpsertAsync(DeezerSource, artistId, payloadJson, DateTimeOffset.UtcNow, CancellationToken.None);
                    _logger.LogInformation("Artist cache refreshed (deezer). id=ArtistId releases=ReleaseCount");
                }
                else
                {
                    _logger.LogWarning("Skipped Deezer artist cache refresh with empty discography. id=ArtistId");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to refresh Deezer artist cache for ArtistId");
            }
        }

        private async Task RefreshArtistPageCacheAsync(string id, string source)
        {
            try
            {
                if (string.Equals(source, SpotifySource, StringComparison.OrdinalIgnoreCase))
                {
                    await _spotifyArtistService.GetArtistPageBySpotifyIdAsync(id, id, forceRefresh: true, CancellationToken.None);
                    _logger.LogInformation("Artist page spotify cache refreshed via service. id=ArtistId");
                    return;
                }

                using var httpClient = _httpClientFactory.CreateClient();
                var payload = await BuildArtistPageAsync(httpClient, id, source, CancellationToken.None);
                if (payload == null)
                {
                    return;
                }

                var payloadJson = JsonSerializer.Serialize(payload);
                var cacheKey = $"{source}:{id}";
                var incomingReleaseCount = CountDiscographyEntries(payload);
                var existing = await _artistPageCache.TryGetAsync(ArtistPageCacheSource, cacheKey, CancellationToken.None);
                var existingReleaseCount = existing == null ? 0 : CountDiscographyEntries(existing.PayloadJson);

                if (incomingReleaseCount == 0 && existingReleaseCount > 0)
                {
                    _logger.LogWarning("Skipped artist page cache refresh to preserve existing discography. key=CacheKey existing_releases=ExistingCount");
                    return;
                }

                if (source == DeezerSource && incomingReleaseCount == 0)
                {
                    _logger.LogWarning("Skipped artist page cache refresh with empty Deezer discography. key=CacheKey");
                    return;
                }

                await _artistPageCache.UpsertAsync(ArtistPageCacheSource, cacheKey, payloadJson, DateTimeOffset.UtcNow, CancellationToken.None);
                _logger.LogInformation("Artist page cache refreshed. key=CacheKey releases=ReleaseCount");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to refresh artist page cache for Source Id");
            }
        }

        private async Task<Dictionary<string, object>> GetArtistDiscographyTabs(HttpClient httpClient, string artistId)
        {
            try
            {
                // Load login credentials to get ARL
                var loginData = await _loginStorage.LoadLoginCredentialsAsync();
                if (loginData?.Arl == null)
                {
                    _logger.LogWarning("No ARL available for discography fetch, using fallback");
                    return await GetArtistDiscographyFallback(httpClient, artistId);
                }

                // ARL cookie is handled by the session manager

                // Get discography tabs exactly like deezspotag
                var discographyTabs = await _deezerGatewayService.GetArtistDiscographyTabsAsync(artistId);

                // Convert to the expected format and log what we got
                var result = new Dictionary<string, object>();
                foreach (var tab in discographyTabs)
                {
                    result[tab.Key] = tab.Value;
                    if (_logger.IsEnabled(LogLevel.Information))
                    {
                        _logger.LogInformation("Tab '{TabName}' has {Count} releases", tab.Key, tab.Value.Count);
                    }
                }

                // If we got no data at all, fall back to public API
                var totalReleases = discographyTabs.Values.Sum(list => list.Count);
                if (totalReleases == 0)
                {
                    _logger.LogWarning("Gateway API returned no releases, falling back to public API");
                    return await GetArtistDiscographyFallback(httpClient, artistId);
                }

                _logger.LogInformation("Gateway API returned TotalCount total releases across TabCount tabs for artist ArtistId");

                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error fetching artist discography for ID: ArtistId, falling back to public API");
                return await GetArtistDiscographyFallback(httpClient, artistId);
            }
        }

        private async Task<Dictionary<string, object>> GetArtistDiscographyFallback(HttpClient httpClient, string artistId)
        {
            var result = new Dictionary<string, object>
            {
                ["all"] = new List<object>(),
                [AlbumType] = new List<object>(),
                [SingleType] = new List<object>(),
                ["featured"] = new List<object>(),
                ["more"] = new List<object>()
            };

            try
            {
                var albumsData = await TryGetArtistAlbumDataArrayAsync(httpClient, artistId);
                if (albumsData.HasValue)
                {
                    PopulateFallbackDiscography(result, albumsData.Value, artistId);
                }

                _logger.LogInformation("Fallback API returned Count total releases for artist ArtistId");

                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error in fallback discography fetch for artist ArtistId");
                return result;
            }
        }

        private static void PopulateFallbackDiscography(
            Dictionary<string, object> target,
            JsonElement albumsData,
            string artistId)
        {
            var processedIds = new HashSet<string>();
            foreach (var release in albumsData.EnumerateArray())
            {
                if (!TryBuildFallbackReleaseEntry(release, artistId, out var releaseId, out var releaseObj, out var isMainArtist))
                {
                    continue;
                }

                if (!processedIds.Add(releaseId))
                {
                    continue;
                }

                CategorizeFallbackRelease(target, releaseObj, isMainArtist);
            }
        }

        private static void CategorizeFallbackRelease(Dictionary<string, object> target, Dictionary<string, object> releaseObj, bool isMainArtist)
        {
            if (!isMainArtist)
            {
                ((List<object>)target["featured"]).Add(releaseObj);
                return;
            }

            ((List<object>)target["all"]).Add(releaseObj);
            var recordType = releaseObj[RecordTypeField].ToString();
            if (recordType == SingleType || recordType == "ep")
            {
                ((List<object>)target[SingleType]).Add(releaseObj);
            }
            else
            {
                ((List<object>)target[AlbumType]).Add(releaseObj);
            }
        }

        private static bool TryBuildFallbackReleaseEntry(
            JsonElement release,
            string artistId,
            out string releaseId,
            out Dictionary<string, object> releaseObj,
            out bool isMainArtist)
        {
            releaseId = release.TryGetProperty("id", out var id) ? id.GetInt64().ToString() : "0";
            releaseObj = new Dictionary<string, object>
            {
                ["id"] = releaseId,
                [TitleField] = release.TryGetProperty(TitleField, out var title) ? title.GetString() ?? string.Empty : string.Empty,
                [CoverImageType] = release.TryGetProperty(CoverImageType, out var cover) ? cover.GetString() ?? string.Empty : string.Empty,
                ["cover_small"] = release.TryGetProperty("cover_small", out var coverSmall) ? coverSmall.GetString() ?? string.Empty : string.Empty,
                ["cover_medium"] = release.TryGetProperty("cover_medium", out var coverMedium) ? coverMedium.GetString() ?? string.Empty : string.Empty,
                ["cover_big"] = release.TryGetProperty("cover_big", out var coverBig) ? coverBig.GetString() ?? string.Empty : string.Empty,
                ["cover_xl"] = release.TryGetProperty("cover_xl", out var coverXl) ? coverXl.GetString() ?? string.Empty : string.Empty,
                [ReleaseDateField] = release.TryGetProperty(ReleaseDateField, out var releaseDate) ? releaseDate.GetString() ?? string.Empty : string.Empty,
                [NbTracksField] = release.TryGetProperty(NbTracksField, out var tracks) ? tracks.GetInt32() : 0,
                ["link"] = release.TryGetProperty("link", out var link) ? link.GetString() ?? string.Empty : string.Empty,
                [RecordTypeField] = release.TryGetProperty(RecordTypeField, out var type) ? type.GetString() ?? AlbumType : AlbumType,
                ["explicit_lyrics"] = release.TryGetProperty("explicit_lyrics", out var explicitLyrics) && explicitLyrics.GetBoolean()
            };

            var artistIdFromRelease = release.TryGetProperty(ArtistType, out var artistElement)
                                      && artistElement.TryGetProperty("id", out var artistIdElement)
                ? artistIdElement.GetInt64().ToString()
                : artistId;
            isMainArtist = artistIdFromRelease == artistId;
            return true;
        }

        private static async Task<JsonElement?> TryGetArtistAlbumDataArrayAsync(HttpClient httpClient, string artistId)
        {
            var albumsResponse = await httpClient.GetAsync($"https://api.deezer.com/artist/{artistId}/albums?limit=500");
            if (!albumsResponse.IsSuccessStatusCode)
            {
                return null;
            }

            var albumsContent = await albumsResponse.Content.ReadAsStringAsync();
            using var albumsDoc = JsonDocument.Parse(albumsContent);
            if (!albumsDoc.RootElement.TryGetProperty("data", out var albumsData))
            {
                return null;
            }

            return albumsData.Clone();
        }

        private static List<object> MapGwSection(GwSearchSection? section, Func<JsonElement, object> mapper)
        {
            if (section?.Data == null || section.Data.Length == 0)
            {
                return new List<object>();
            }

            var items = new List<object>(section.Data.Length);
            items.AddRange(section.Data.Select(entry =>
            {
                var json = entry switch
                {
                    JToken token => token.ToString(Formatting.None),
                    JsonElement element => element.GetRawText(),
                    _ => JsonSerializer.Serialize(entry)
                };
                using var doc = JsonDocument.Parse(json);
                return mapper(doc.RootElement);
            }));

            return items;
        }

        private static async Task<List<object>> MapGwSectionAsync(
            GwSearchSection? section,
            Func<JsonElement, CancellationToken, Task<object>> mapper,
            CancellationToken cancellationToken)
        {
            if (section?.Data == null || section.Data.Length == 0)
            {
                return new List<object>();
            }

            var tasks = new List<Task<object>>(section.Data.Length);
            tasks.AddRange(section.Data.Select(entry =>
            {
                var json = entry switch
                {
                    JToken token => token.ToString(Formatting.None),
                    JsonElement element => element.GetRawText(),
                    _ => JsonSerializer.Serialize(entry)
                };
                return Task.Run(async () =>
                {
                    using var doc = JsonDocument.Parse(json);
                    return await mapper(doc.RootElement, cancellationToken);
                }, cancellationToken);
            }));

            var results = await Task.WhenAll(tasks);
            return results.ToList();
        }

        private async Task<object> MapGwTrackAsync(JsonElement item, CancellationToken cancellationToken)
        {
            var artistName = GetString(item, ArtNameUpperField);
            if (string.IsNullOrWhiteSpace(artistName)
                && item.TryGetProperty("ARTISTS", out var artists)
                && artists.ValueKind == JsonValueKind.Array)
            {
                var first = artists.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Object)
                {
                    artistName = GetString(first, ArtNameUpperField);
                }
            }

            var albumTitle = GetString(item, AlbTitleUpperField) ?? GetString(item, AlbumType);
            var albumMd5 = GetString(item, AlbPictureUpperField);
            var image = await ResolveArtworkUrlAsync(
                new ArtworkLookupRequest(
                    albumTitle,
                    artistName ?? GetString(item, ArtistType),
                    albumTitle,
                    albumMd5,
                    CoverImageType,
                    false,
                    AllowApple: false),
                cancellationToken);

            return new
            {
                type = TrackType,
                deezerId = GetString(item, SngIdUpperField) ?? GetString(item, "id"),
                name = GetString(item, "SNG_TITLE") ?? GetString(item, TitleField),
                artist = artistName ?? GetString(item, ArtistType),
                album = albumTitle,
                image,
                url = GetString(item, "link")
            };
        }

        private async Task<object> MapGwAlbumAsync(JsonElement item, CancellationToken cancellationToken)
        {
            var albumTitle = GetString(item, AlbTitleUpperField) ?? GetString(item, TitleField);
            var artistName = GetString(item, ArtNameUpperField) ?? GetString(item, ArtistType);
            var albumMd5 = GetString(item, AlbPictureUpperField);
            var image = await ResolveArtworkUrlAsync(
                new ArtworkLookupRequest(
                    albumTitle,
                    artistName,
                    albumTitle,
                    albumMd5,
                    CoverImageType,
                    false,
                    AllowApple: false),
                cancellationToken);

            return new
            {
                type = AlbumType,
                deezerId = GetString(item, AlbIdUpperField) ?? GetString(item, "id"),
                name = albumTitle,
                artist = artistName,
                release_date = GetString(item, "PHYSICAL_RELEASE_DATE") ?? GetString(item, ReleaseDateField),
                image,
                url = GetString(item, "link")
            };
        }

        private async Task<object> MapGwArtistAsync(JsonElement item, CancellationToken cancellationToken)
        {
            var artistName = GetString(item, ArtNameUpperField) ?? GetString(item, "name");
            var artistMd5 = GetString(item, ArtPictureUpperField);
            var image = await ResolveArtworkUrlAsync(
                new ArtworkLookupRequest(
                    null,
                    artistName,
                    null,
                    artistMd5,
                    ArtistType,
                    true,
                    AllowApple: false),
                cancellationToken);

            return new
            {
                type = ArtistType,
                deezerId = GetString(item, ArtIdUpperField) ?? GetString(item, "id"),
                name = artistName,
                followers = GetLong(item, NbFanUpperField),
                image,
                url = GetString(item, "link")
            };
        }

        private static object MapGwPlaylist(JsonElement item)
        {
            var ownerId = GetString(item, "PARENT_USER_ID")
                          ?? GetString(item, "USER_ID")
                          ?? GetString(item, "owner_id")
                          ?? GetString(item, "ownerId");
            var ownerName = GetString(item, "PARENT_USER_NAME")
                            ?? GetString(item, "PARENT_USERNAME")
                            ?? GetString(item, "USER_NAME")
                            ?? GetString(item, "owner");
            var ownerPicture = GetString(item, "PARENT_USER_PICTURE")
                               ?? GetString(item, "USER_PICTURE")
                               ?? GetString(item, "owner_picture");
            var playlistPicture = GetString(item, "PLAYLIST_PICTURE") ?? GetString(item, PictureUpperField);
            var ownerAvatar = string.IsNullOrWhiteSpace(ownerPicture)
                ? null
                : $"https://e-cdns-images.dzcdn.net/images/user/{ownerPicture}/250x250-000000-80-0-0.jpg";
            var isPublic = GetBool(item, "IS_PUBLIC") ?? GetBool(item, "PUBLIC");
            var isCollaborative = GetBool(item, "COLLABORATIVE");
            var status = GetInt(item, "STATUS");
            var checksum = GetString(item, "CHECKSUM");
            var creationDate = GetString(item, "CREATION_DATE") ?? GetString(item, "creation_date");
            var fans = GetLong(item, NbFanUpperField) ?? GetLong(item, "fans");
            var nbTracks = GetLong(item, NbSongUpperField) ?? GetLong(item, NbTracksField);
            var duration = GetLong(item, DurationUpperField) ?? GetLong(item, DurationField);
            var isLovedTrack = GetBool(item, "IS_LOVED_TRACK");

            return new
            {
                type = PlaylistType,
                deezerId = GetString(item, "PLAYLIST_ID") ?? GetString(item, "id"),
                name = GetString(item, TitleUpperField) ?? GetString(item, TitleField),
                owner = ownerName,
                owner_id = ownerId,
                description = GetString(item, DescriptionUpperField) ?? GetString(item, "description"),
                nb_tracks = nbTracks,
                duration = duration,
                @public = isPublic ?? (status == 1 ? true : null),
                collaborative = isCollaborative ?? (status == 2 ? true : null),
                status,
                checksum,
                creation_date = creationDate,
                fans,
                followers = fans,
                is_loved_track = isLovedTrack,
                creator = string.IsNullOrWhiteSpace(ownerId) && string.IsNullOrWhiteSpace(ownerName)
                    ? null
                    : new
                    {
                        id = ownerId,
                        name = ownerName,
                        picture = ownerAvatar,
                        avatar = ownerAvatar
                    },
                image = BuildDeezerImage(PlaylistType, playlistPicture),
                url = GetString(item, "link")
            };
        }

        private async Task<string?> ResolveArtworkUrlAsync(ArtworkLookupRequest request, CancellationToken cancellationToken)
        {
            var settings = _settingsService.LoadSettings();
            var fallbackOrder = ResolveArtworkFallbackOrder(settings);
            if (!request.AllowApple)
            {
                fallbackOrder = fallbackOrder
                    .Where(source => !string.Equals(source, AppleSource, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            foreach (var source in fallbackOrder)
            {
                var sourceImage = await ResolveArtworkUrlForSourceAsync(source, request, cancellationToken);
                if (!string.IsNullOrWhiteSpace(sourceImage))
                {
                    return sourceImage;
                }
            }

            return null;
        }

        private static IReadOnlyList<string> ResolveArtworkFallbackOrder(DeezSpoTagSettings settings)
        {
            if (!settings.ArtworkFallbackEnabled)
            {
                return new[] { DeezerSource };
            }

            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { AppleSource, DeezerSource, SpotifySource };
            var order = (settings.ArtworkFallbackOrder ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => value.ToLowerInvariant())
                .Where(value => allowed.Contains(value))
                .Distinct()
                .ToList();

            if (order.Count == 0)
            {
                order.AddRange(new[] { AppleSource, DeezerSource, SpotifySource });
            }
            else
            {
                order.AddRange(new[] { AppleSource, DeezerSource, SpotifySource }
                    .Where(fallback => !order.Contains(fallback)));
            }

            return order;
        }

        private static string? BuildDeezerImage(string type, string? hash)
        {
            if (string.IsNullOrWhiteSpace(hash))
            {
                return null;
            }

            return $"https://e-cdns-images.dzcdn.net/images/{type}/{hash}/1000x1000-000000-80-0-0.jpg";
        }

        private static string ResolveDeezerStreamTrackId(GwTrack track)
        {
            var fallbackId = ResolveDeezerFallbackId(track);
            if (fallbackId > 0)
            {
                return fallbackId.ToString(CultureInfo.InvariantCulture);
            }

            return track.SngId > 0
                ? track.SngId.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
        }

        private static int ResolveDeezerFallbackId(GwTrack track)
        {
            if (track.FallbackId.HasValue && track.FallbackId.Value > 0)
            {
                return track.FallbackId.Value;
            }

            return ExtractFallbackId(track.Fallback);
        }

        private static int ExtractFallbackId(object? fallback)
        {
            if (fallback == null)
            {
                return 0;
            }

            return fallback switch
            {
                int value when value > 0 => value,
                long value when value > 0 && value <= int.MaxValue => (int)value,
                string value when int.TryParse(value, out var parsed) && parsed > 0 => parsed,
                JObject obj => ExtractFallbackId(obj.Value<string>(SngIdUpperField) ?? obj.Value<string>("id")),
                JValue value => ExtractFallbackId(value.ToString()),
                _ => 0
            };
        }

        private async Task<string?> ResolveArtworkUrlForSourceAsync(
            string source,
            ArtworkLookupRequest request,
            CancellationToken cancellationToken)
        {
            switch (source)
            {
                case AppleSource:
                    {
                        var appleUrl = await TryResolveAppleArtworkUrlAsync(
                            request.Title,
                            request.Artist,
                            request.Album,
                            request.IsArtist,
                            "Apple artwork lookup failed.",
                            cancellationToken);

                        if (string.IsNullOrWhiteSpace(appleUrl))
                        {
                            return null;
                        }

                        return appleUrl;
                    }
                case SpotifySource:
                    {
                        if (request.IsArtist)
                        {
                            if (!string.IsNullOrWhiteSpace(request.Artist))
                            {
                                return await _spotifyArtworkResolver.ResolveArtistImageByNameAsync(request.Artist, cancellationToken);
                            }
                            return null;
                        }

                        var spotifyId = await _spotifyIdResolver.ResolveTrackIdAsync(
                            request.Title ?? string.Empty,
                            request.Artist ?? string.Empty,
                            request.Album,
                            null,
                            cancellationToken);
                        if (string.IsNullOrWhiteSpace(spotifyId))
                        {
                            return null;
                        }

                        return await _spotifyArtworkResolver.ResolveAlbumCoverUrlAsync(spotifyId, cancellationToken);
                    }
                default:
                    return BuildDeezerImage(request.DeezerType, request.DeezerMd5);
            }
        }

        private async Task<string?> TryResolveAppleArtworkUrlAsync(
            string? title,
            string? artist,
            string? album,
            bool isArtist,
            string warningMessage,
            CancellationToken cancellationToken)
        {
            var settings = _settingsService.LoadSettings();
            var appleArtworkSize = AppleQueueHelpers.GetAppleArtworkSize(settings);
            var appleFormat = AppleQueueHelpers.GetAppleArtworkFormat(settings);
            var appleDims = AppleQueueHelpers.GetAppleArtworkDimensions(settings);

            try
            {
                if (isArtist)
                {
                    if (string.IsNullOrWhiteSpace(artist))
                    {
                        return null;
                    }

                    var artistUrl = await AppleQueueHelpers.ResolveAppleArtistImageAsync(
                        _appleCatalog,
                        artist,
                        settings.AppleMusic?.Storefront ?? "us",
                        appleArtworkSize,
                        cancellationToken);
                    return string.IsNullOrWhiteSpace(artistUrl)
                        ? null
                        : AppleQueueHelpers.BuildAppleArtworkUrl(
                            artistUrl,
                            appleDims.SizeText,
                            appleArtworkSize,
                            appleArtworkSize,
                            appleFormat);
                }

                var appleUrl = await AppleQueueHelpers.ResolveAppleCoverFromCatalogAsync(
                    _appleCatalog,
                    new AppleQueueHelpers.AppleCatalogCoverLookup
                    {
                        Title = title,
                        Artist = artist,
                        Album = album,
                        Storefront = settings.AppleMusic?.Storefront ?? "us",
                        Size = appleArtworkSize,
                        Logger = _logger
                    },
                    cancellationToken);

                if (string.IsNullOrWhiteSpace(appleUrl))
                {
                    appleUrl = await AppleQueueHelpers.ResolveAppleCoverAsync(
                        _httpClientFactory,
                        title,
                        artist,
                        album,
                        appleArtworkSize,
                        _logger,
                        cancellationToken);
                }

                return string.IsNullOrWhiteSpace(appleUrl)
                    ? null
                    : AppleQueueHelpers.BuildAppleArtworkUrl(
                        appleUrl,
                        appleDims.SizeText,
                        appleArtworkSize,
                        appleArtworkSize,
                        appleFormat);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Apple artwork resolution warning: {Message}", warningMessage);
                return null;
            }
        }

        private static string? GetString(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        private static long? GetLong(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
            {
                return value.TryGetInt64(out var parsed) ? parsed : null;
            }

            return null;
        }

        private static int? GetInt(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (element.TryGetProperty(name, out var value))
            {
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed))
                {
                    return parsed;
                }

                if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsedString))
                {
                    return parsedString;
                }
            }

            return null;
        }

        private static bool? GetBool(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (element.TryGetProperty(name, out var value))
            {
                if (value.ValueKind == JsonValueKind.True)
                {
                    return true;
                }

                if (value.ValueKind == JsonValueKind.False)
                {
                    return false;
                }

                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed))
                {
                    return parsed != 0;
                }

                if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsedBool))
                {
                    return parsedBool;
                }

                if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsedInt))
                {
                    return parsedInt != 0;
                }
            }

            return null;
        }

        private static bool TryParseDeezerUrl(Uri uri, out string type, out string id)
        {
            type = string.Empty;
            id = string.Empty;

            var host = uri.Host.ToLowerInvariant();
            if (!host.Contains("deezer.com"))
            {
                return false;
            }

            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                return false;
            }

            var validTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                TrackType,
                AlbumType,
                ArtistType,
                PlaylistType
            };

            for (var i = 0; i < parts.Length - 1; i++)
            {
                var candidate = parts[i];
                if (!validTypes.Contains(candidate))
                {
                    continue;
                }

                type = candidate.ToLowerInvariant();
                id = parts[i + 1];
                return !string.IsNullOrWhiteSpace(id) && id.All(char.IsDigit);
            }

            return false;
        }

        private static bool IsDeezerShareLink(Uri uri)
        {
            var host = uri.Host.ToLowerInvariant();
            return host == "dzr.page.link" || host.EndsWith(".dzr.page.link", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<Uri?> ResolveDeezerShareLinkAsync(Uri uri)
        {
            try
            {
                using var handler = new HttpClientHandler
                {
                    AllowAutoRedirect = false
                };
                using var httpClient = new HttpClient(handler);
                using var request = new HttpRequestMessage(HttpMethod.Head, uri);
                using var response = await httpClient.SendAsync(request);

                if (response.Headers.Location == null)
                {
                    return null;
                }

                var target = response.Headers.Location;
                if (!target.IsAbsoluteUri)
                {
                    target = new Uri(uri, target);
                }

                return target;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return null;
            }
        }

        private async Task<object> MapHomePageAsync(JObject response, CancellationToken cancellationToken)
        {
            var source = ResolveHomeSource(response);
            var (currentDeezerUserId, currentDeezerUserName) = await LoadCurrentDeezerUserAsync();
            var sectionsToken = source["sections"] as JArray;
            if (sectionsToken == null)
            {
                return BuildHomeSectionsResponse();
            }

            var sections = new List<object>();
            foreach (var sectionToken in sectionsToken)
            {
                if (sectionToken is not JObject sectionObj)
                {
                    continue;
                }

                if (ShouldSkipHomeSection(sectionObj, out var title, out var normalizedLayout))
                {
                    continue;
                }

                var items = await MapHomeSectionItemsAsync(
                    sectionObj["items"] as JArray,
                    currentDeezerUserId,
                    currentDeezerUserName,
                    cancellationToken);
                if (items.Count == 0)
                {
                    continue;
                }

                sections.Add(new
                {
                    title,
                    layout = normalizedLayout,
                    pagePath = ResolveHomeSectionPagePath(sectionObj),
                    hasMore = sectionObj.Value<bool?>("hasMoreItems") ?? false,
                    filter = BuildHomeSectionFilter(sectionObj["filter"] as JObject),
                    items,
                    related = BuildHomeSectionRelated(sectionObj[RelatedField] as JObject)
                });
            }

            return BuildHomeSectionsResponse(sections);
        }

        private static JObject ResolveHomeSource(JObject response)
        {
            if (response.TryGetValue("results", out var resultsToken) && resultsToken is JObject resultsObj)
            {
                return resultsObj;
            }

            return response;
        }

        private async Task<(string? UserId, string? UserName)> LoadCurrentDeezerUserAsync()
        {
            var loginData = await _loginStorage.LoadLoginCredentialsAsync();
            return (loginData?.User?.Id, loginData?.User?.Name);
        }

        private static bool ShouldSkipHomeSection(JObject sectionObj, out string title, out string normalizedLayout)
        {
            title = sectionObj.Value<string>(TitleField) ?? string.Empty;
            normalizedLayout = "row";

            if (IsPersonalHomeSectionTitle(title))
            {
                return true;
            }

            var layout = sectionObj.Value<string>("layout") ?? string.Empty;
            if (layout == "ads")
            {
                return true;
            }

            normalizedLayout = NormalizeHomeSectionLayout(layout);
            return false;
        }

        private static string NormalizeHomeSectionLayout(string layout) =>
            layout switch
            {
                "grid" => "grid",
                "grid-preview-one" => "grid",
                "grid-preview-two" => "grid",
                "filterable-grid" => "filterable-grid",
                _ => "row"
            };

        private async Task<List<object>> MapHomeSectionItemsAsync(
            JArray? itemsToken,
            string? currentDeezerUserId,
            string? currentDeezerUserName,
            CancellationToken cancellationToken)
        {
            if (itemsToken == null)
            {
                return new List<object>();
            }

            var itemTasks = new List<Task<object?>>();
            foreach (var itemToken in itemsToken)
            {
                if (itemToken is not JObject itemObj)
                {
                    continue;
                }

                itemTasks.Add(MapHomeItemAsync(
                    itemObj,
                    currentDeezerUserId,
                    currentDeezerUserName,
                    cancellationToken));
            }

            var mappedItems = await Task.WhenAll(itemTasks);
            return mappedItems.Where(static mapped => mapped != null).Cast<object>().ToList();
        }

        private static string ResolveHomeSectionPagePath(JObject sectionObj) =>
            sectionObj.Value<string>(TargetField)
            ?? sectionObj[RelatedField]?.Value<string>(TargetField)
            ?? string.Empty;

        private static object? BuildHomeSectionRelated(JObject? relatedToken)
        {
            if (relatedToken == null)
            {
                return null;
            }

            return new
            {
                target = relatedToken.Value<string>(TargetField) ?? string.Empty,
                label = relatedToken.Value<string>("label") ?? string.Empty,
                mandatory = relatedToken.Value<bool?>("mandatory") ?? false
            };
        }

        private static object? BuildHomeSectionFilter(JObject? filterToken)
        {
            if (filterToken == null)
            {
                return null;
            }

            var options = (filterToken["options"] as JArray)?
                .Select(opt => new
                {
                    id = opt?["id"]?.ToString() ?? string.Empty,
                    label = opt?["label"]?.ToString() ?? string.Empty
                })
                .Where(opt => !string.IsNullOrWhiteSpace(opt.id))
                .Cast<object>()
                .ToList();

            return new
            {
                default_option_id = filterToken.Value<string>("default_option_id") ?? string.Empty,
                options = options ?? new List<object>()
            };
        }

        private async Task<object?> MapHomeItemAsync(
            JObject item,
            string? currentDeezerUserId,
            string? currentDeezerUserName,
            CancellationToken cancellationToken)
        {
            var source = item.Value<string>("source") ?? string.Empty;
            var normalizedSource = string.IsNullOrWhiteSpace(source)
                ? DeezerSource
                : source.Trim().ToLowerInvariant();
            var state = CreateHomeItemMappingState(item, normalizedSource);
            if (state.Type == "flow")
            {
                return null;
            }

            ApplyPlaylistHomeItemData(state);
            if (ShouldSkipPersonalHomePlaylist(state, currentDeezerUserId, currentDeezerUserName))
            {
                return null;
            }

            ApplyHomeItemTypeSpecificData(state, item);
            var image = await ResolveHomeItemImageAsync(state, item, cancellationToken);
            var filterOptionIds = ExtractHomeItemFilterOptionIds(item);
            var pictures = ExtractHomeItemPictures(item);
            return BuildHomeItemResponse(state, item, image, filterOptionIds, pictures);
        }

        private sealed class HomeItemMappingState
        {
            public HomeItemMappingState(string source, string type, string id, string title, string subtitle, string target, JObject? data)
            {
                Source = source;
                Type = type;
                Id = id;
                Title = title;
                Subtitle = subtitle;
                Target = target;
                Data = data;
            }

            public string Source { get; }
            public string Type { get; }
            public string Id { get; set; }
            public string Title { get; set; }
            public string Subtitle { get; set; }
            public string Target { get; set; }
            public JObject? Data { get; }
            public string? OwnerName { get; set; }
            public string? OwnerId { get; set; }
            public long? NbTracks { get; set; }
            public long? Fans { get; set; }
            public long? Duration { get; set; }
            public bool? IsPublic { get; set; }
            public bool? IsCollaborative { get; set; }
            public int? Status { get; set; }
            public string? Checksum { get; set; }
            public string? CreationDate { get; set; }
            public string? Description { get; set; }
            public string? Md5Override { get; set; }
            public string? ImageTypeOverride { get; set; }
            public string? Link { get; set; }
            public string? Md5Image { get; set; }
            public string? PictureType { get; set; }
            public string? ReleaseDate { get; set; }
            public string? RecordType { get; set; }
            public bool? ExplicitLyrics { get; set; }
            public object? Artist { get; set; }
            public object? Album { get; set; }
            public object? User { get; set; }
            public string? BackgroundColor { get; set; }
            public object? BackgroundImage { get; set; }
            public object? LogoImage { get; set; }
            public string? Logo { get; set; }
            public string? ImageOverride { get; set; }
            public bool IsLovedTrackPlaylist { get; set; }
            public string? StreamTrackId { get; set; }
            public string? TrackToken { get; set; }
            public string? Md5Origin { get; set; }
            public string? MediaVersion { get; set; }
            public string? FallbackId { get; set; }
        }

        private static HomeItemMappingState CreateHomeItemMappingState(JObject item, string normalizedSource)
        {
            var type = item.Value<string>("type")?.ToLowerInvariant() ?? string.Empty;
            return new HomeItemMappingState(
                normalizedSource,
                type,
                item.Value<string>("id") ?? string.Empty,
                item.Value<string>(TitleField) ?? string.Empty,
                item.Value<string>("subtitle") ?? item.Value<string>("description") ?? string.Empty,
                item.Value<string>(TargetField) ?? string.Empty,
                item["data"] as JObject);
        }

        private static bool ShouldSkipPersonalHomePlaylist(
            HomeItemMappingState state,
            string? currentDeezerUserId,
            string? currentDeezerUserName)
        {
            if (!string.Equals(state.Type, PlaylistType, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return IsPersonalHomePlaylist(
                new HomePlaylistContext(
                    state.Title,
                    state.Subtitle,
                    state.Description,
                    state.Target,
                    state.OwnerId,
                    state.OwnerName,
                    state.IsLovedTrackPlaylist,
                    currentDeezerUserId,
                    currentDeezerUserName));
        }

        private static void ApplyPlaylistHomeItemData(HomeItemMappingState state)
        {
            if (state.Data == null || state.Type != PlaylistType)
            {
                return;
            }

            state.Title = state.Data.Value<string>(TitleUpperField) ?? state.Title;
            state.Description = state.Data.Value<string>(DescriptionUpperField);
            state.OwnerName = state.Data.Value<string>("PARENT_USERNAME")
                              ?? state.Data.Value<string>("PARENT_USER_NAME")
                              ?? state.Data.Value<string>("USER_NAME");
            state.OwnerId = state.Data.Value<string>("PARENT_USER_ID")
                            ?? state.Data.Value<string>("USER_ID");
            var ownerPicture = state.Data.Value<string>("PARENT_USER_PICTURE")
                               ?? state.Data.Value<string>("USER_PICTURE");
            state.NbTracks = state.Data.Value<long?>(NbSongUpperField);
            state.Fans = state.Data.Value<long?>(NbFanUpperField);
            state.Duration = state.Data.Value<long?>(DurationUpperField);
            state.IsPublic = state.Data.Value<bool?>("IS_PUBLIC");
            state.IsCollaborative = state.Data.Value<bool?>("COLLABORATIVE");
            state.Status = state.Data.Value<int?>("STATUS");
            state.Checksum = state.Data.Value<string>("CHECKSUM");
            state.CreationDate = state.Data.Value<string>("CREATION_DATE");
            state.Md5Override = state.Data.Value<string>("PLAYLIST_PICTURE") ?? state.Data.Value<string>(PictureUpperField);
            state.ImageTypeOverride = state.Data.Value<string>("PICTURE_TYPE") ?? PlaylistType;
            state.Link = state.Data.Value<string>("LINK") ?? state.Data.Value<string>("link");
            state.IsLovedTrackPlaylist = state.Data.Value<bool?>("IS_LOVED_TRACK")
                ?? state.Data.Value<bool?>("is_loved_track")
                ?? false;
            var ownerPictureUrl = string.IsNullOrWhiteSpace(ownerPicture)
                ? null
                : $"https://e-cdns-images.dzcdn.net/images/user/{ownerPicture}/250x250-000000-80-0-0.jpg";
            state.User = string.IsNullOrWhiteSpace(state.OwnerId) && string.IsNullOrWhiteSpace(state.OwnerName)
                ? null
                : new
                {
                    id = state.OwnerId,
                    name = state.OwnerName,
                    picture = ownerPictureUrl
                };
            if (string.IsNullOrWhiteSpace(state.Subtitle))
            {
                state.Subtitle = state.OwnerName ?? state.Subtitle;
            }
        }

        private static void ApplyHomeItemTypeSpecificData(HomeItemMappingState state, JObject item)
        {
            if (state.Type == AlbumType && state.Data != null)
            {
                ApplyAlbumHomeItemData(state);
                return;
            }

            if (state.Type == ArtistType && state.Data != null)
            {
                ApplyArtistHomeItemData(state);
                return;
            }

            if (state.Type == ArtistType)
            {
                ApplyArtistFallbackHomeItemData(state, item);
                return;
            }

            if (state.Type == TrackType)
            {
                ApplyTrackHomeItemData(state, item);
                return;
            }

            if (state.Type == SmartTracklistType && state.Data != null)
            {
                ApplySmartTracklistHomeItemData(state, item);
                return;
            }

            if (state.Type == ShowType && state.Data != null)
            {
                ApplyShowHomeItemData(state);
                return;
            }

            if (state.Type == "channel")
            {
                ApplyChannelHomeItemData(state, item);
            }
        }

        private static void ApplyAlbumHomeItemData(HomeItemMappingState state)
        {
            state.Id = state.Data!.Value<string>(AlbIdUpperField) ?? state.Data.Value<string>("id") ?? state.Id;
            state.Title = state.Data.Value<string>(AlbTitleUpperField) ?? state.Data.Value<string>(TitleField) ?? state.Title;
            var artistName = state.Data.Value<string>(ArtNameUpperField);
            var artistId = state.Data.Value<string>(ArtIdUpperField);
            state.Subtitle = string.IsNullOrWhiteSpace(state.Subtitle) ? artistName ?? state.Subtitle : state.Subtitle;
            state.Md5Image = state.Data.Value<string>(AlbPictureUpperField);
            state.PictureType = CoverImageType;
            state.ReleaseDate = state.Data.Value<string>("PHYSICAL_RELEASE_DATE") ?? state.Data.Value<string>("RELEASE_DATE");
            state.NbTracks = state.Data.Value<long?>("NUMBER_TRACK") ?? state.Data.Value<long?>("NB_TRACK");
            state.RecordType = state.Data.Value<string>("TYPE");
            state.ExplicitLyrics = state.Data.Value<bool?>("EXPLICIT_LYRICS");
            state.Link = state.Data.Value<string>("ALB_LINK") ?? state.Data.Value<string>("LINK") ?? state.Data.Value<string>("link");
            state.Artist = string.IsNullOrWhiteSpace(artistId) && string.IsNullOrWhiteSpace(artistName)
                ? null
                : new { id = artistId, name = artistName };
            state.Album = new { id = state.Id, title = state.Title };
        }

        private static void ApplyArtistHomeItemData(HomeItemMappingState state)
        {
            state.Id = state.Data!.Value<string>(ArtIdUpperField) ?? state.Data.Value<string>("id") ?? state.Id;
            state.Title = state.Data.Value<string>(ArtNameUpperField) ?? state.Data.Value<string>("name") ?? state.Title;
            state.Md5Image = state.Data.Value<string>(ArtPictureUpperField);
            state.PictureType = ArtistType;
            state.Fans = state.Data.Value<long?>(NbFanUpperField) ?? state.Data.Value<long?>("fans");
            state.Link = state.Data.Value<string>("ART_LINK") ?? state.Data.Value<string>("LINK") ?? state.Data.Value<string>("link");
        }

        private static void ApplyArtistFallbackHomeItemData(HomeItemMappingState state, JObject item)
        {
            state.Id = item.Value<string>("id") ?? state.Id;
            if (string.IsNullOrWhiteSpace(state.Title))
            {
                state.Title = item.Value<string>(NameField) ?? state.Title;
            }

            state.Md5Image ??= item.Value<string>(ArtPictureUpperField);
            state.PictureType ??= ArtistType;
            state.ImageOverride = ExtractUrl(item, "picture_xl", "picture_big", "picture_medium", "picture");
        }

        private static void ApplyTrackHomeItemData(HomeItemMappingState state, JObject item)
        {
            var data = state.Data;
            state.Id = data?.Value<string>(SngIdUpperField)
                ?? data?.Value<string>("id")
                ?? item.Value<string>("id")
                ?? state.Id;
            state.Title = data?.Value<string>("SNG_TITLE")
                ?? data?.Value<string>(TitleField)
                ?? item.Value<string>(TitleField)
                ?? item.Value<string>(NameField)
                ?? state.Title;

            var artistName = data?.Value<string>(ArtNameUpperField)
                ?? data?[ArtistType]?.Value<string>(NameField);
            var artistId = data?.Value<string>(ArtIdUpperField)
                ?? data?[ArtistType]?.Value<string>("id");
            state.Subtitle = string.IsNullOrWhiteSpace(state.Subtitle)
                ? artistName ?? state.Subtitle
                : state.Subtitle;

            state.Duration = data?.Value<long?>(DurationUpperField)
                ?? data?.Value<long?>(DurationField)
                ?? state.Duration;
            state.Md5Image = data?.Value<string>(AlbPictureUpperField)
                ?? data?[AlbumType]?.Value<string>("md5_image")
                ?? state.Md5Image;
            state.PictureType ??= CoverImageType;

            var albumId = data?.Value<string>(AlbIdUpperField)
                ?? data?[AlbumType]?.Value<string>("id");
            var albumTitle = data?.Value<string>(AlbTitleUpperField)
                ?? data?[AlbumType]?.Value<string>(TitleField);
            state.Artist = string.IsNullOrWhiteSpace(artistId) && string.IsNullOrWhiteSpace(artistName)
                ? state.Artist
                : new { id = artistId, name = artistName };
            state.Album = string.IsNullOrWhiteSpace(albumId) && string.IsNullOrWhiteSpace(albumTitle)
                ? state.Album
                : new { id = albumId, title = albumTitle };

            state.TrackToken = NormalizeHomeTrackContextValue(
                data?.Value<string>("TRACK_TOKEN")
                ?? data?["track_token"]?.ToString()
                ?? item.Value<string>("track_token"));
            state.Md5Origin = NormalizeHomeTrackContextValue(
                data?.Value<string>("MD5_ORIGIN")
                ?? data?["md5_origin"]?.ToString()
                ?? item.Value<string>("md5_origin"));
            state.MediaVersion = NormalizeHomeTrackContextValue(
                data?.Value<string>("MEDIA_VERSION")
                ?? data?["media_version"]?.ToString()
                ?? item.Value<string>("media_version"));

            var fallbackId = ExtractHomeTrackFallbackId(data, item);
            state.FallbackId = string.IsNullOrWhiteSpace(fallbackId) ? null : fallbackId;
            state.StreamTrackId = NormalizeHomeTrackContextValue(
                item.Value<string>("stream_track_id")
                ?? data?.Value<string>("stream_track_id")
                ?? (string.IsNullOrWhiteSpace(state.FallbackId) ? state.Id : state.FallbackId));
        }

        private static string? ExtractHomeTrackFallbackId(JObject? data, JObject item)
        {
            var directFallback = NormalizeHomeTrackContextValue(
                item.Value<string>("fallback_id")
                ?? data?["fallback_id"]?.ToString()
                ?? data?["FALLBACK_ID"]?.ToString());
            if (!string.IsNullOrWhiteSpace(directFallback))
            {
                return directFallback;
            }

            var fallbackToken = data?["FALLBACK"] ?? data?["fallback"];
            var fallbackId = ExtractFallbackId(fallbackToken);
            return fallbackId > 0 ? fallbackId.ToString(CultureInfo.InvariantCulture) : null;
        }

        private static string? NormalizeHomeTrackContextValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static void ApplySmartTracklistHomeItemData(HomeItemMappingState state, JObject item)
        {
            state.Id = state.Data!.Value<string>("SMARTTRACKLIST_ID") ?? state.Data.Value<string>("id") ?? state.Id;
            state.Title = state.Data.Value<string>(TitleUpperField) ?? state.Data.Value<string>(TitleField) ?? state.Title;
            state.Subtitle = state.Data.Value<string>("SUBTITLE") ?? state.Subtitle;
            state.Description = state.Data.Value<string>(DescriptionUpperField) ?? state.Description;
            state.NbTracks = state.Data.Value<long?>(NbSongUpperField) ?? state.NbTracks;
            var cover = state.Data[CoverUpperField] as JObject;
            state.Md5Image = cover?.Value<string>("MD5") ?? cover?.Value<string>("md5") ?? state.Data.Value<string>(CoverUpperField);
            state.PictureType = cover?.Value<string>("TYPE") ?? cover?.Value<string>("type") ?? CoverImageType;

            if (string.IsNullOrWhiteSpace(state.Md5Image) || !state.Md5Image.Contains('-'))
            {
                return;
            }

            var picture = item[PicturesField]?.FirstOrDefault() as JObject;
            var pictureMd5 = picture?.Value<string>("md5");
            var pictureTypeOverride = picture?.Value<string>("type");
            if (!string.IsNullOrWhiteSpace(pictureMd5))
            {
                state.Md5Image = pictureMd5;
                state.PictureType = pictureTypeOverride ?? state.PictureType;
            }
        }

        private static void ApplyShowHomeItemData(HomeItemMappingState state)
        {
            state.Id = state.Data!.Value<string>("SHOW_ID") ?? state.Data.Value<string>("id") ?? state.Id;
            state.Title = state.Data.Value<string>("SHOW_NAME") ?? state.Data.Value<string>(TitleUpperField) ?? state.Title;
            state.Description = state.Data.Value<string>("SHOW_DESCRIPTION") ?? state.Data.Value<string>(DescriptionUpperField) ?? state.Description;
            state.Md5Image = state.Data.Value<string>("SHOW_ART_MD5") ?? state.Data.Value<string>("SHOW_PICTURE");
            state.PictureType = "talk";
            state.Link = state.Data.Value<string>("LINK") ?? state.Data.Value<string>("link");
        }

        private static void ApplyChannelHomeItemData(HomeItemMappingState state, JObject item)
        {
            state.Title = item.Value<string>(TitleField) ?? state.Title;
            state.Target = item.Value<string>(TargetField) ?? state.Target;
            state.BackgroundColor = item.Value<string>("background_color");
            state.BackgroundImage = item[PicturesField]?.FirstOrDefault();
            state.LogoImage = item["logo_image"];
            state.Logo = state.Data?.Value<string>("logo");
            state.Md5Image = (state.BackgroundImage as JObject)?.Value<string>("md5") ?? state.Md5Image;
            state.PictureType = (state.BackgroundImage as JObject)?.Value<string>("type") ?? state.PictureType;
            state.Link = item.Value<string>("link") ?? state.Link;
        }

        private static bool LooksLikeUrl(string? value) =>
            !string.IsNullOrWhiteSpace(value)
            && (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

        private static string? ExtractUrl(JObject? obj, params string[] keys)
        {
            if (obj == null)
            {
                return null;
            }

            return keys
                .Select(obj.Value<string>)
                .FirstOrDefault(LooksLikeUrl);
        }

        private async Task<string?> ResolveHomeItemImageAsync(
            HomeItemMappingState state,
            JObject item,
            CancellationToken cancellationToken)
        {
            var (md5, imageType) = ExtractHomeItemDeezerImage(item);
            if (!string.IsNullOrWhiteSpace(state.Md5Override))
            {
                md5 = state.Md5Override;
                imageType = state.ImageTypeOverride ?? imageType;
            }

            if (!string.IsNullOrWhiteSpace(state.Md5Image))
            {
                if (LooksLikeUrl(state.Md5Image))
                {
                    state.ImageOverride ??= state.Md5Image;
                    state.Md5Image = null;
                }

                md5 = state.Md5Image;
                imageType = state.PictureType ?? imageType;
            }

            var artistNameForLookup = string.Equals(state.Type, ArtistType, StringComparison.OrdinalIgnoreCase)
                ? state.Title
                : state.Subtitle;
            var imageRequest = new ArtworkLookupRequest(
                state.Title,
                artistNameForLookup,
                null,
                md5,
                imageType ?? CoverImageType,
                string.Equals(state.Type, ArtistType, StringComparison.OrdinalIgnoreCase));
            return state.ImageOverride ?? await ResolveArtworkUrlForSourceAsync(
                state.Source,
                imageRequest,
                cancellationToken);
        }

        private static string[] ExtractHomeItemFilterOptionIds(JObject item)
        {
            var filterOptions = item["filter_option_ids"] as JArray;
            return filterOptions == null
                ? Array.Empty<string>()
                : filterOptions.Select(opt => opt?.ToString() ?? string.Empty)
                    .Where(opt => !string.IsNullOrWhiteSpace(opt))
                    .ToArray();
        }

        private sealed record HomeItemPicture(string? Md5, string? Type);

        private static List<HomeItemPicture>? ExtractHomeItemPictures(JObject item)
        {
            var picturesToken = item[PicturesField] as JArray;
            return picturesToken?
                .Select(pic => new HomeItemPicture(
                    pic?["md5"]?.ToString(),
                    pic?[TypeField]?.ToString()))
                .Where(pic => !string.IsNullOrWhiteSpace(pic.Md5))
                .ToList();
        }

        private static object BuildHomeItemResponse(
            HomeItemMappingState state,
            JObject item,
            string? image,
            string[] filterOptionIds,
            List<HomeItemPicture>? pictures)
        {
            return new
            {
                source = state.Source,
                type = state.Type,
                id = state.Id,
                title = state.Title,
                subtitle = state.Subtitle,
                description = state.Description,
                nb_tracks = state.NbTracks,
                fans = state.Fans,
                duration = state.Duration,
                @public = state.IsPublic ?? (state.Status == 1 ? true : null),
                collaborative = state.IsCollaborative ?? (state.Status == 2 ? true : null),
                status = state.Status,
                checksum = state.Checksum,
                creation_date = state.CreationDate,
                link = state.Link,
                md5_image = state.Md5Image,
                picture_type = state.PictureType,
                release_date = state.ReleaseDate,
                record_type = state.RecordType,
                explicit_lyrics = state.ExplicitLyrics,
                artist = state.Artist,
                album = state.Album,
                user = state.User,
                background_color = state.BackgroundColor,
                background_image = state.BackgroundImage,
                logo_image = state.LogoImage,
                logo = state.Logo,
                cover_title = item.Value<string>("cover_title") ?? item.Value<string>("caption"),
                pictures,
                filter_option_ids = filterOptionIds,
                play_button = item.Value<bool?>("play_button") ?? false,
                image,
                target = state.Target,
                stream_track_id = state.StreamTrackId,
                track_token = state.TrackToken,
                md5_origin = state.Md5Origin,
                media_version = state.MediaVersion,
                fallback_id = state.FallbackId
            };
        }

        private static bool IsPersonalHomeSectionTitle(string? title)
        {
            return ContainsAnyToken(title, PersonalHomeSectionKeywords);
        }

        private static bool IsPersonalHomePlaylist(HomePlaylistContext context)
        {
            if (context.IsLovedTrackPlaylist)
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(context.CurrentDeezerUserId)
                && !string.IsNullOrWhiteSpace(context.OwnerId)
                && string.Equals(context.CurrentDeezerUserId.Trim(), context.OwnerId.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(context.CurrentDeezerUserName)
                && !string.IsNullOrWhiteSpace(context.OwnerName)
                && string.Equals(context.CurrentDeezerUserName.Trim(), context.OwnerName.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(context.CurrentDeezerUserId)
                && !string.IsNullOrWhiteSpace(context.Target)
                && context.Target.Contains($"/user/{context.CurrentDeezerUserId.Trim()}", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (ContainsAnyToken(context.Target, PersonalHomePlaylistKeywords))
            {
                return true;
            }

            return ContainsAnyToken(context.Title, PersonalHomePlaylistKeywords)
                   || ContainsAnyToken(context.Subtitle, PersonalHomePlaylistKeywords)
                   || ContainsAnyToken(context.Description, PersonalHomePlaylistKeywords);
        }

        private static bool ContainsAnyToken(string? value, string[] tokens)
        {
            if (string.IsNullOrWhiteSpace(value) || tokens.Length == 0)
            {
                return false;
            }

            var normalized = value.Trim().ToLowerInvariant();
            return tokens
                .Where(static token => !string.IsNullOrWhiteSpace(token))
                .Any(token => normalized.Contains(token, StringComparison.OrdinalIgnoreCase));
        }

        private static (string? Md5, string? Type) ExtractHomeItemDeezerImage(JObject item)
        {
            var picture = item[PicturesField]?.FirstOrDefault() as JObject;
            var md5 = picture?.Value<string>("md5");
            var type = picture?.Value<string>(TypeField);
            if (!string.IsNullOrWhiteSpace(md5))
            {
                return (md5, type ?? CoverImageType);
            }

            var cover = item[CoverImageType] as JObject;
            md5 = cover?.Value<string>("md5");
            type = cover?.Value<string>(TypeField);
            if (!string.IsNullOrWhiteSpace(md5))
            {
                return (md5, type ?? CoverImageType);
            }

            var linked = item["image_linked_item"] as JObject;
            md5 = linked?.Value<string>("md5");
            type = linked?.Value<string>(TypeField);
            if (!string.IsNullOrWhiteSpace(md5))
            {
                return (md5, type ?? CoverImageType);
            }

            var data = item[DataField] as JObject;
            md5 = data?.Value<string>(AlbPictureUpperField)
                  ?? data?.Value<string>(ArtPictureUpperField)
                  ?? data?.Value<string>("PLAYLIST_PICTURE")
                  ?? data?.Value<string>(PictureUpperField);
            if (!string.IsNullOrWhiteSpace(md5))
            {
                var derivedType = data?.Value<string>("PICTURE_TYPE")
                                  ?? (data?.ContainsKey(ArtPictureUpperField) == true ? ArtistType : CoverImageType);
                return (md5, derivedType);
            }

            return (null, null);
        }

        private OfflineSearchResults SearchOfflineAsync(string query, CancellationToken cancellationToken)
        {
            if (!_libraryConfigStore.HasLocalLibraryData())
            {
                return new OfflineSearchResults();
            }

            var dbQuery = query.Replace("%", "\\%").Replace("_", "\\_");
            var like = $"%{dbQuery}%";

            var tracks = _libraryConfigStore.SearchTracksAsync(like, cancellationToken);
            var albums = _libraryConfigStore.SearchAlbumsAsync(like, cancellationToken);
            var artists = _libraryConfigStore.SearchArtistsAsync(like, cancellationToken);

            return new OfflineSearchResults
            {
                Tracks = tracks.Select(track => new
                {
                    type = TrackType,
                    deezerId = track.DeezerId,
                    name = track.Title,
                    artist = track.ArtistName,
                    album = track.AlbumTitle,
                    image = track.CoverPath is null ? null : $"/api/library/image?path={Uri.EscapeDataString(track.CoverPath)}&size=240",
                    url = track.DeezerId is null ? null : $"https://www.deezer.com/track/{track.DeezerId}"
                }).ToList<object>(),
                Albums = albums.Select(album => new
                {
                    type = AlbumType,
                    deezerId = album.DeezerId,
                    name = album.Title,
                    artist = album.ArtistName,
                    release_date = string.Empty,
                    image = album.CoverPath is null ? null : $"/api/library/image?path={Uri.EscapeDataString(album.CoverPath)}&size=240",
                    url = album.DeezerId is null ? null : $"https://www.deezer.com/album/{album.DeezerId}"
                }).ToList<object>(),
                Artists = artists.Select(artist => new
                {
                    type = ArtistType,
                    deezerId = artist.DeezerId,
                    name = artist.Name,
                    followers = default(long?),
                    image = artist.ImagePath is null ? null : $"/api/library/image?path={Uri.EscapeDataString(artist.ImagePath)}&size=240",
                    url = artist.DeezerId is null ? null : $"https://www.deezer.com/artist/{artist.DeezerId}"
                }).ToList<object>(),
                Playlists = new List<object>()
            };
        }

        private sealed class OfflineSearchResults
        {
            public List<object> Tracks { get; init; } = new();
            public List<object> Albums { get; init; } = new();
            public List<object> Artists { get; init; } = new();
            public List<object> Playlists { get; init; } = new();
        }
    }
}
