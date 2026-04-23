using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Utils;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Web.Services;

public sealed class SpotifyPathfinderMetadataClient
{
    private const string SpotifyDirectoryName = "spotify";
    private sealed record SpotifyAlbumReference(string? Name, string? ReleaseDate, string? ReleaseType, string? AlbumGroup, int? TotalTracks);

    private readonly record struct SpotiFlacCoverUrls(string? Small, string? Medium, string? Large);

    private sealed record PathfinderAuthContext(string AccessToken, string ClientToken, string ClientVersion, string DeviceId);

    private sealed record PersistedQueryOverride(int Version, string Sha256Hash, string? VariablesJson);

    public sealed record SpotifyArtistCandidateInfo(string ArtistId, bool Verified, int TotalAlbums, int TotalTracks);

    public sealed record SpotifyArtistSearchCandidate(string Id, string Name, string? ImageUrl);

    private sealed record ParsedSpotifyUrl(string Type, string Id);

    private sealed record WebPlayerConfig(string? ClientVersion, string? ClientId);

    private sealed record WebPlayerSessionInfo(string? ClientVersion, string? ClientId, string? DeviceId);

    private sealed record ClientTokenRequestContext(string ClientId, string ClientVersion, string DeviceId);

    public sealed record RecommendationQueryResult(JsonDocument Document, string OperationName, string VariablesJson);

    private sealed record ImageSource(string Url, int Width, int Height);

    private sealed record DiscographyRelease(string Id, string? Name, string? Artists, string? ImageUrl, string? ReleaseDate, string? ReleaseType, string AlbumGroup, int? TotalTracks);

    private sealed record AlbumUnionResult(JsonElement AlbumUnion, List<JsonElement> TrackItems);

    private sealed record PlaylistUnionResult(JsonElement PlaylistUnion, List<JsonElement> TrackItems);

    private sealed record DiscographyPageResult(JsonElement DiscographyAll, List<JsonElement> Items);

    private sealed record RelatedArtistArrays(JsonElement? RelatedContent, JsonElement? Direct);

    private sealed record PlaylistTrackProjection(SpotifyTrackSummary Summary, JsonElement TrackData);

    private const string HttpsScheme = "https";

    private const string SpotifyPartnerHost = "api-partner.spotify.com";

    private const string SpotifyOpenHost = "open.spotify.com";

    private const string SpotifyClientTokenHost = "clienttoken.spotify.com";

    private const string SpotifyPathfinderPath = "/pathfinder/v2/query";

    private const string SpotifyClientTokenPath = "/v1/clienttoken";

    private const string TrackType = "track";

    private const string AlbumType = "album";

    private const string PlaylistType = "playlist";

    private const string ArtistType = "artist";

    private const string ShowType = "show";

    private const string EpisodeType = "episode";

    private const string GetAlbumOperationName = "getAlbum";

    private const string QueryArtistOperationName = "queryArtist";

    private const string QueryArtistOverviewOperationName = "queryArtistOverview";

    private const string FetchPlaylistOperationName = "fetchPlaylist";

    private const string BrowseAllOperationName = "browseAll";

    private const string BrowsePageOperationName = "browsePage";

    private const string BrowseSectionOperationName = "browseSection";

    private const string HomeSectionOperationName = "homeSection";

    private const string IntegrationWebPlayer = "INTEGRATION_WEB_PLAYER";

    private const string SpotifyTrackFallbackTitle = "Spotify track";

    private const string UnknownText = "Unknown";

    private static readonly string PathfinderUrl = BuildUrl(SpotifyPartnerHost, SpotifyPathfinderPath);

    private static readonly string WebPlayerRootUrl = BuildUrl(SpotifyOpenHost, string.Empty);

    private static readonly string ClientTokenUrl = BuildUrl(SpotifyClientTokenHost, SpotifyClientTokenPath);

    private const string PathfinderOverridesFileName = "pathfinder-hashes.json";

    private const string WebPlayerAppPlatform = "WebPlayer";

    private const string SearchSuggestionsOperationName = "searchSuggestions";

    private const string MoreLikeThisPlaylistOperationName = "moreLikeThisPlaylist";

    private const string MoreLikeThisPlaylistDefaultHash = "5973b2230ee523c2fc589bfc296fbac7c5b822c1cd9d8c92a2a1ebb4821e8c01";

    private const string QueryTrackPageOperationName = "queryTrackPage";

    private const string QueryTrackPageFallbackHash = "b2a084f6f3c4c1a7c67c8cba5a1f1d7c6a2e4b8e4c2a6e3f2a9b9d8c5e7f3c11";

    private const string GetTrackOperationName = "getTrack";

    private const string GetTrackFallbackHash = "612585ae06ba435ad26369870deaae23b5c8800a256cd8a57e08eddc25a37294";

    private const string SpotifyArtistUriPrefix = "spotify:artist:";

    private const string SpotifyTrackUriPrefix = "spotify:track:";

    private const string WebPlayerUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private const string WebPlayerClientIdFallback = "d8a5ed958d274c2e8ee717e6a4b0971d";

    private const string SearchSuggestionsHashFallback = "a2a6e5d400e2a8331128edb2d45f9eeb2c9445ca95b4859ae7da977815928ddf";

    private const string ProfileKey = "profile";

    private const string DataKey = "data";

    private const string ItemsKey = "items";

    private const string ContentKey = "content";

    private const string ArtistsKey = "artists";

    private const string ImagesKey = "images";

    private const string GenresKey = "genres";

    private const string AlbumOfTrackKey = "albumOfTrack";

    private const string CoverArtKey = "coverArt";

    private const string TotalMillisecondsKey = "totalMilliseconds";

    private const string TrackUnionKey = "trackUnion";

    private const string ItemV2Key = "itemV2";

    private const string ArtistUnionKey = "artistUnion";

    private const string TotalCountKey = "totalCount";

    private const string IsoStringKey = "isoString";

    private const string ReleaseDateKey = "releaseDate";

    private const string ReleaseDateSnakeKey = "release_date";

    private const string ContentRatingKey = "contentRating";

    private const string CoverImageKey = "cover_image";

    private const string ImageKey = "image";

    private const string DurationKey = "duration";

    private const string OffsetKey = "offset";

    private const string CountKey = "count";

    private const string ArtistNameKey = "artistName";

    private const string TracksV2Key = "tracksV2";

    private const string FollowersKey = "followers";

    private const string StatsKey = "stats";

    private const string DiscographyKey = "discography";

    private const string TracksKey = "tracks";

    private const string LabelKey = "label";

    private const string VisualsKey = "visuals";

    private const string BiographyKey = "biography";

    private const string SourcesKey = "sources";

    private const string HeaderImageKey = "headerImage";

    private const string AvatarImageKey = "avatarImage";

    private const string ExplicitLabel = "EXPLICIT";

    private const string ReleaseTypeSingle = "SINGLE";

    private const string ReleaseTypeCompilation = "COMPILATION";

    private const string ReleaseTypeAlbum = "ALBUM";

    private const string QueryArtistHashDefault = "a55d895740a6ea09d6f34a39ee6a1e8a4c66c6889361710bd01560d1c314f1f4";

    private const string GetAlbumHashDefault = "b9bfabef66ed756e5e13f68a942deb60bd4125ec1f1be8cc42769dc0259b4b10";

    private const string QueryArtistOverviewHashDefault = "446130b4a0aa6522a686aafccddb0ae849165b5e0436fd802f96e0243617b5d8";

    private const string BrowseAllHashDefault = "864fdecccb9bb893141df3776d0207886c7fa781d9e586b9d4eb3afa387eea42";

    private const string BrowsePageHashDefault = "4078a5c7df7638dfff465e5d4e03713fdbcab8b351b9db7cd2214f20b7b76a7a";

    private const string BrowseSectionHashDefault = "9633db277767830756013f68eb0889ce7a099d2ed06c2f27b3944173cec2903b";

    private const string HomeSectionHashDefault = "3e8e118c033b10353783ec0404451de66ed44e5cb5e0caefc65e4fab7b9e0aef";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250.0);

    private static readonly TimeSpan TokenRefreshWindow = TimeSpan.FromMinutes(5.0);

    private static readonly JsonSerializerOptions ClientTokenJsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = null,
        DictionaryKeyPolicy = null
    };

    private static readonly object PathfinderOverridesLock = new object();

    private static DateTimeOffset _pathfinderOverridesStamp = DateTimeOffset.MinValue;

    private static readonly Dictionary<string, PersistedQueryOverride> PathfinderOverrides = new Dictionary<string, PersistedQueryOverride>(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, string> IsrcCache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, (DateTimeOffset Stamp, string? Name, string? ImageUrl)> ArtistSearchEnrichmentCache = new ConcurrentDictionary<string, (DateTimeOffset, string?, string?)>(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan ShowCacheTtl = TimeSpan.FromMinutes(10.0);

    private static readonly TimeSpan ArtistSearchEnrichmentCacheTtl = TimeSpan.FromMinutes(20.0);

    private static readonly ConcurrentDictionary<string, (DateTimeOffset Stamp, SpotifyUrlMetadata Data)> ShowCache = new ConcurrentDictionary<string, (DateTimeOffset, SpotifyUrlMetadata)>();

    private static readonly ConcurrentDictionary<string, (DateTimeOffset Stamp, List<SpotifyTrackSummary> Data)> ShowEpisodeCache = new ConcurrentDictionary<string, (DateTimeOffset, List<SpotifyTrackSummary>)>();

    private readonly SpotifyBlobService _blobService;

    private readonly PlatformAuthService _platformAuthService;

    private readonly SpotifyUserAuthStore _userAuthStore;

    private readonly ISpotifyUserContextAccessor _userContext;

    private readonly IHttpClientFactory _httpClientFactory;

    private readonly ILogger<SpotifyPathfinderMetadataClient> _logger;

    private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _authGate = new SemaphoreSlim(1, 1);

    private DateTimeOffset _lastBlobAuthMissingWarningUtc = DateTimeOffset.MinValue;

    private string? _clientToken;

    private DateTimeOffset _clientTokenExpiresAt;

    private string? _clientTokenClientId;

    private string? _clientTokenClientVersion;

    private string? _clientTokenDeviceId;

    private string? _blobContextBlobPath;

    private PathfinderAuthContext? _blobContext;

    private DateTimeOffset _blobContextAccessTokenExpiresAt;

    private DateTimeOffset _blobContextClientTokenExpiresAt;

    private readonly SemaphoreSlim _backgroundUserBlobResolutionGate = new SemaphoreSlim(1, 1);

    private DateTimeOffset _backgroundUserBlobCheckedAt = DateTimeOffset.MinValue;

    private string? _backgroundUserBlobCachedPath;

    private static string BuildUrl(string host, string path)
    {
        return string.IsNullOrEmpty(path) ? ($"{HttpsScheme}://{host}") : ($"{HttpsScheme}://{host}{path}");
    }

    public SpotifyPathfinderMetadataClient(SpotifyBlobService blobService, PlatformAuthService platformAuthService, SpotifyUserAuthStore userAuthStore, ISpotifyUserContextAccessor userContext, IHttpClientFactory httpClientFactory, ILogger<SpotifyPathfinderMetadataClient> logger)
    {
        _blobService = blobService;
        _platformAuthService = platformAuthService;
        _userAuthStore = userAuthStore;
        _userContext = userContext;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<SpotifyUrlMetadata?> FetchByUrlAsync(string url, CancellationToken cancellationToken)
    {
        ParsedSpotifyUrl? parsed = ParseSpotifyUrl(url);
        if (parsed is null)
        {
            return null;
        }
        string type = parsed.Type;
        if (type == ShowType || type == EpisodeType)
        {
            return parsed.Type != ShowType
                ? await FetchEpisodeAsync(parsed.Id, cancellationToken)
                : await FetchShowAsync(parsed.Id, cancellationToken);
        }
        PathfinderAuthContext? context = await BuildAuthContextAsync(cancellationToken);
        if (context is null)
        {
            return null;
        }
        return parsed.Type switch
        {
            TrackType => await FetchTrackAsync(context, parsed.Id, cancellationToken),
            AlbumType => await FetchAlbumAsync(context, parsed.Id, cancellationToken),
            PlaylistType => await FetchPlaylistAsync(context, parsed.Id, cancellationToken),
            ArtistType => await FetchArtistAsync(context, parsed.Id, cancellationToken),
            _ => null,
        };
    }

    public async Task<JsonDocument?> FetchRawByUrlAsync(string url, CancellationToken cancellationToken)
    {
        ParsedSpotifyUrl? parsed = ParseSpotifyUrl(url);
        if (parsed is null)
        {
            return null;
        }
        PathfinderAuthContext? context = await BuildAuthContextAsync(cancellationToken);
        if (context is null)
        {
            return null;
        }
        string type = parsed.Type;
        object? obj = type switch
        {
            AlbumType => new
            {
                variables = new
                {
                    uri = "spotify:album:" + parsed.Id,
                    locale = "",
                    offset = 0,
                    limit = 1000
                },
                operationName = "getAlbum",
                extensions = new
                {
                    persistedQuery = new
                    {
                        version = GetPersistedQuery(GetAlbumOperationName, 1, GetAlbumHashDefault).Version,
                        sha256Hash = GetPersistedQuery(GetAlbumOperationName, 1, GetAlbumHashDefault).Sha256Hash
                    }
                }
            },
            PlaylistType => new
            {
                variables = new
                {
                    uri = "spotify:playlist:" + parsed.Id,
                    offset = 0,
                    limit = 1000,
                    enableWatchFeedEntrypoint = false
                },
                operationName = FetchPlaylistOperationName,
                extensions = new
                {
                    persistedQuery = new
                    {
                        version = 1,
                        sha256Hash = "bb67e0af06e8d6f52b531f97468ee4acd44cd0f82b988e15c2ea47b1148efc77"
                    }
                }
            },
            ArtistType => new
            {
                variables = new
                {
                    uri = SpotifyArtistUriPrefix + parsed.Id
                },
                operationName = QueryArtistOperationName,
                extensions = new
                {
                    persistedQuery = new
                    {
                        version = GetPersistedQuery(QueryArtistOperationName, 1, QueryArtistHashDefault).Version,
                        sha256Hash = GetPersistedQuery(QueryArtistOperationName, 1, QueryArtistHashDefault).Sha256Hash
                    }
                }
            },
            _ => null,
        };
        object? payload = obj;
        if (parsed.Type == TrackType)
        {
            return await QueryTrackDocumentAsync(context, parsed.Id, cancellationToken);
        }
        return (payload != null) ? (await QueryAsync(context, payload, cancellationToken)) : null;
    }

    public async Task<SpotifyUrlMetadata?> FetchShowAsync(string showId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(showId))
        {
            return null;
        }
        if (TryGetCachedShow(showId, out SpotifyUrlMetadata? cached) && cached is not null)
        {
            return cached;
        }
        using JsonDocument? showDoc = await FetchBurstPodcastJsonAsync(ShowType, showId, cancellationToken);
        if (showDoc is null)
        {
            return cached;
        }
        JsonElement root = showDoc.RootElement;
        string title = TryGetString(root, "name") ?? "Spotify show";
        string? publisher = TryGetString(root, "publisher");
        string? description = TryGetString(root, "description");
        string sourceUrl = BuildSpotifyUrl(ShowType, showId);
        string? imageUrl = TryGetLargestImageUrl(root, CoverImageKey, ImageKey);
        List<SpotifyTrackSummary> episodes = ParseEpisodesFromBurstShow(root, showId, title, publisher);
        SpotifyUrlMetadata metadata = new SpotifyUrlMetadata(TotalTracks: TryGetInt(root, "total_episodes") ?? TryGetInt(root, "episode_count") ?? TryGetInt(root, "episodes", "total") ?? ((episodes.Count > 0) ? new int?(episodes.Count) : ((int?)null)), Type: ShowType, Id: showId, Name: title, SourceUrl: sourceUrl, ImageUrl: imageUrl, Subtitle: (!string.IsNullOrWhiteSpace(description)) ? description : publisher, DurationMs: null, TrackList: episodes, AlbumList: new List<SpotifyAlbumSummary>(), OwnerName: publisher, Followers: null, SnapshotId: null);
        CacheShow(showId, metadata);
        if (episodes.Count > 0)
        {
            CacheShowEpisodes(showId, 0, episodes.Count, episodes);
        }
        return metadata;
    }

    public async Task<SpotifyUrlMetadata?> FetchEpisodeAsync(string episodeId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(episodeId))
        {
            return null;
        }
        using JsonDocument? episodeDoc = await FetchBurstPodcastJsonAsync(EpisodeType, episodeId, cancellationToken);
        if (episodeDoc is null)
        {
            return null;
        }
        JsonElement root = episodeDoc.RootElement;
        string title = TryGetString(root, "name") ?? "Spotify episode";
        string sourceUrl = BuildSpotifyUrl(EpisodeType, episodeId);
        string? showId = TryGetString(root, ShowType, "id");
        string? showName = TryGetString(root, ShowType, "name");
        string? showPublisher = null;
        if (!string.IsNullOrWhiteSpace(showId))
        {
            SpotifyUrlMetadata? showMetadata = await FetchShowAsync(showId, cancellationToken);
            showPublisher = showMetadata?.OwnerName;
            if (string.IsNullOrWhiteSpace(showName))
            {
                showName = showMetadata?.Name;
            }
        }
        string? subtitle = !string.IsNullOrWhiteSpace(showName) ? showName : showPublisher;
        string? imageUrl = TryGetLargestImageUrl(root, CoverImageKey, ImageKey) ?? TryGetLargestImageUrl(root, ShowType, CoverImageKey, ImageKey);
        int? durationMs = TryGetInt(root, DurationKey);
        SpotifyTrackSummary episodeTrack = MapBurstEpisodeToTrackSummary(root, episodeId, showId, showName, showPublisher, sourceUrl, imageUrl);
        return new SpotifyUrlMetadata(EpisodeType, episodeId, title, sourceUrl, imageUrl, subtitle, 1, durationMs, new List<SpotifyTrackSummary> { episodeTrack }, new List<SpotifyAlbumSummary>(), showPublisher, null, null);
    }

    public async Task<List<SpotifyTrackSummary>> FetchShowEpisodesAsync(string showId, int offset, int limit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(showId))
        {
            return new List<SpotifyTrackSummary>();
        }
        int boundedOffset = Math.Max(0, offset);
        int boundedLimit = Math.Clamp(limit, 1, 50);
        if (TryGetCachedShowEpisodes(showId, boundedOffset, boundedLimit, out List<SpotifyTrackSummary> cached))
        {
            return cached;
        }
        using JsonDocument? showDoc = await FetchBurstPodcastJsonAsync(ShowType, showId, cancellationToken);
        if (showDoc is null)
        {
            return new List<SpotifyTrackSummary>();
        }
        JsonElement root = showDoc.RootElement;
        string? showName = TryGetString(root, "name");
        string? showPublisher = TryGetString(root, "publisher");
        List<SpotifyTrackSummary> allEpisodes = ParseEpisodesFromBurstShow(root, showId, showName, showPublisher);
        if (allEpisodes.Count == 0)
        {
            return new List<SpotifyTrackSummary>();
        }
        List<SpotifyTrackSummary> episodes = allEpisodes.Skip(boundedOffset).Take(boundedLimit).ToList();
        CacheShowEpisodes(showId, boundedOffset, boundedLimit, episodes);
        return episodes;
    }

    private async Task<JsonDocument?> FetchBurstPodcastJsonAsync(string metadataType, string spotifyId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(metadataType) || string.IsNullOrWhiteSpace(spotifyId))
        {
            return null;
        }
        string? blobPath = await TryResolveActiveLibrespotBlobPathAsync();
        if (string.IsNullOrWhiteSpace(blobPath))
        {
            _logger.LogWarning("Spotify burst metadata unavailable: missing librespot blob. type={Type} id={Id}", metadataType, spotifyId);
            return null;
        }
        SpotifyBlobService.SpotifyLibrespotPodcastResult result = await _blobService.GetLibrespotPodcastMetadataAsync(blobPath, metadataType, spotifyId, cancellationToken);
        if (string.IsNullOrWhiteSpace(result.PayloadJson))
        {
            _logger.LogWarning("Spotify burst metadata unavailable. type={Type} id={Id} error={Error}", metadataType, spotifyId, result.Error ?? "unknown_error");
            return null;
        }
        try
        {
            return JsonDocument.Parse(result.PayloadJson);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            _logger.LogWarning(ex, "Spotify burst metadata parse failed. type={Type} id={Id}", metadataType, spotifyId);
            return null;
        }
    }

    private async Task<string?> TryResolveActiveLibrespotBlobPathAsync()
    {
        try
        {
            string? userId = _userContext.UserId;
            if (!string.IsNullOrWhiteSpace(userId))
            {
                SpotifyUserAuthState userState = await _userAuthStore.LoadAsync(userId);
                string? userBlob = SpotifyUserAuthStore.ResolveActiveLibrespotBlobPath(userState);
                if (!string.IsNullOrWhiteSpace(userBlob)
                    && _blobService.BlobExists(userBlob)
                    && await _blobService.IsLibrespotBlobAsync(userBlob))
                {
                    return userBlob;
                }
            }
            PlatformAuthState state = await _platformAuthService.LoadAsync();
            var spotifyState = state.Spotify;
            string? active = spotifyState?.ActiveAccount;
            if (!string.IsNullOrWhiteSpace(active))
            {
                SpotifyAccount? account = spotifyState?.Accounts.FirstOrDefault((SpotifyAccount a) => a.Name.Equals(active, StringComparison.OrdinalIgnoreCase));
                string? platformBlobPath = account?.LibrespotBlobPath ?? account?.BlobPath;
                if (!string.IsNullOrWhiteSpace(platformBlobPath)
                    && _blobService.BlobExists(platformBlobPath)
                    && await _blobService.IsLibrespotBlobAsync(platformBlobPath))
                {
                    return platformBlobPath;
                }
            }
            string root = AppDataPathResolver.ResolveDataRootOrDefault(AppDataPathResolver.GetDefaultWorkersDataDir());
            string fallback = Path.Join(root, SpotifyDirectoryName, "blobs", "credentials.json");
            return _blobService.BlobExists(fallback) && await _blobService.IsLibrespotBlobAsync(fallback)
                ? fallback
                : null;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            _logger.LogDebug(ex, "Failed to resolve Spotify librespot blob path.");
            return null;
        }
    }

    private static List<SpotifyTrackSummary> ParseEpisodesFromBurstShow(JsonElement root, string? fallbackShowId, string? fallbackShowName, string? fallbackPublisher)
    {
        if (!TryGetNested(root, out var value, EpisodeType) || value.ValueKind != JsonValueKind.Array)
        {
            return new List<SpotifyTrackSummary>();
        }
        List<SpotifyTrackSummary> list = new List<SpotifyTrackSummary>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                string text = TryGetString(item, "id") ?? ExtractIdFromUri(TryGetString(item, "uri")) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    string sourceUrl = BuildSpotifyUrl(EpisodeType, text);
                    string? imageUrl = TryGetLargestImageUrl(item, CoverImageKey, ImageKey) ?? TryGetLargestImageUrl(root, CoverImageKey, ImageKey);
                    string? showId = TryGetString(item, ShowType, "id") ?? fallbackShowId;
                    string? showName = TryGetString(item, ShowType, "name") ?? fallbackShowName;
                    string? publisher = TryGetString(item, ShowType, "publisher") ?? fallbackPublisher;
                    list.Add(MapBurstEpisodeToTrackSummary(item, text, showId, showName, publisher, sourceUrl, imageUrl));
                }
            }
        }
        return list;
    }

    private static SpotifyTrackSummary MapBurstEpisodeToTrackSummary(JsonElement episode, string episodeId, string? showId, string? showName, string? publisher, string sourceUrl, string? imageUrl)
    {
        string name = TryGetString(episode, "name") ?? "Spotify episode";
        int? durationMs = TryGetInt(episode, DurationKey);
        string? releaseDate = ExtractPublishDate(episode);
        string? artists = !string.IsNullOrWhiteSpace(showName) ? showName : publisher;
        string album = ((!string.IsNullOrWhiteSpace(showName)) ? showName : (publisher ?? string.Empty));
        return new SpotifyTrackSummary(episodeId, name, artists, album, durationMs, sourceUrl, imageUrl, null, releaseDate)
        {
            AlbumId = showId
        };
    }

    private static string? ExtractPublishDate(JsonElement episode)
    {
        if (!TryGetNested(episode, out var value, "publish_time") || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        int? num = TryGetInt(value, "year");
        if (!num.HasValue || num.Value <= 0)
        {
            return null;
        }
        int? num2 = TryGetInt(value, "month");
        int? num3 = TryGetInt(value, "day");
        if (num2.HasValue && num2.Value > 0 && num3.HasValue && num3.Value > 0)
        {
            return $"{num.Value:0000}-{num2.Value:00}-{num3.Value:00}";
        }
        if (num2.HasValue && num2.Value > 0)
        {
            return $"{num.Value:0000}-{num2.Value:00}";
        }
        return num.Value.ToString(CultureInfo.InvariantCulture);
    }

    private static string? TryGetLargestImageUrl(JsonElement root, params string[] path)
    {
        if (!TryGetNested(root, out var value, path) || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        string? result = null;
        long num = -1L;
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            string? text = TryGetString(item, "url");
            if (!string.IsNullOrWhiteSpace(text))
            {
                int valueOrDefault = TryGetInt(item, "width").GetValueOrDefault();
                int valueOrDefault2 = TryGetInt(item, "height").GetValueOrDefault();
                long num2 = (long)valueOrDefault * (long)valueOrDefault2;
                if (num2 >= num)
                {
                    num = num2;
                    result = text;
                }
            }
        }
        return result;
    }

    private static string BuildShowEpisodeCacheKey(string showId, int offset, int limit)
    {
        return $"{showId}|{offset}|{limit}";
    }

    private static void CacheShow(string showId, SpotifyUrlMetadata metadata)
    {
        ShowCache[showId] = (DateTimeOffset.UtcNow, metadata);
    }

    private static bool TryGetCachedShow(string showId, out SpotifyUrlMetadata? metadata)
    {
        metadata = null;
        if (!ShowCache.TryGetValue(showId, out (DateTimeOffset, SpotifyUrlMetadata) value))
        {
            return false;
        }
        if (DateTimeOffset.UtcNow - value.Item1 > ShowCacheTtl)
        {
            ShowCache.TryRemove(showId, out (DateTimeOffset, SpotifyUrlMetadata) _);
            return false;
        }
        metadata = value.Item2;
        return true;
    }

    private static void CacheShowEpisodes(string showId, int offset, int limit, List<SpotifyTrackSummary> tracks)
    {
        string key = BuildShowEpisodeCacheKey(showId, offset, limit);
        ShowEpisodeCache[key] = (DateTimeOffset.UtcNow, tracks);
    }

    private static bool TryGetCachedShowEpisodes(string showId, int offset, int limit, out List<SpotifyTrackSummary> tracks)
    {
        tracks = new List<SpotifyTrackSummary>();
        string key = BuildShowEpisodeCacheKey(showId, offset, limit);
        if (!ShowEpisodeCache.TryGetValue(key, out (DateTimeOffset, List<SpotifyTrackSummary>) value))
        {
            return false;
        }
        if (DateTimeOffset.UtcNow - value.Item1 > ShowCacheTtl)
        {
            ShowEpisodeCache.TryRemove(key, out (DateTimeOffset, List<SpotifyTrackSummary>) _);
            return false;
        }
        tracks = value.Item2;
        return true;
    }

    public async Task<List<SpotifyTrackSummary>?> FetchAlbumTracksAsync(string albumId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(albumId))
        {
            return null;
        }
        PathfinderAuthContext? context = await BuildAuthContextAsync(cancellationToken);
        if (context is null)
        {
            return null;
        }
        AlbumUnionResult? album = await QueryAlbumUnionAsync(context, albumId, cancellationToken);
        if (album is null)
        {
            return null;
        }
        return ParseAlbumTracks(album.AlbumUnion, album.TrackItems);
    }

    public async Task<List<SpotifyTrackSummary>?> FetchPlaylistTracksAsync(string playlistId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return null;
        }
        PathfinderAuthContext? context = await BuildAuthContextAsync(cancellationToken);
        if (context is null)
        {
            return null;
        }
        PlaylistUnionResult? playlist = await QueryPlaylistUnionAsync(context, playlistId, cancellationToken);
        if (playlist is null)
        {
            return null;
        }
        return ParsePlaylistTracks(playlist.PlaylistUnion, playlist.TrackItems);
    }

    public async Task<SpotifyArtistCandidateInfo?> GetArtistCandidateInfoAsync(string artistId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artistId))
        {
            return null;
        }
        PathfinderAuthContext? context = await BuildAuthContextAsync(cancellationToken);
        if (context is null)
        {
            return null;
        }
        JsonElement? artistUnion = await QueryArtistAsync(context, artistId, cancellationToken);
        if (!artistUnion.HasValue)
        {
            return null;
        }
        JsonElement artist = artistUnion.Value;
        bool verified = TryGetBool(artist, ProfileKey, "verified") == true;
        var discography = await FetchArtistDiscographyAsync(context, artistId, cancellationToken);
        int totalAlbums = discography.Count;
        int totalTracks = discography.Sum(album => Math.Max(0, album.TotalTracks ?? 0));
        return new SpotifyArtistCandidateInfo(artistId, verified, totalAlbums, totalTracks);
    }

    public async Task<SpotifyArtistOverview?> FetchArtistOverviewAsync(string artistId, CancellationToken cancellationToken)
    {
        (PathfinderAuthContext Context, JsonElement? Artist, JsonElement? Overview)? artistQuery = await QueryArtistAndOverviewAsync(artistId, cancellationToken);
        if (!artistQuery.HasValue)
        {
            return null;
        }

        JsonElement? artist = artistQuery.Value.Artist;
        JsonElement? overview = artistQuery.Value.Overview;
        SpotifyArtistOverview? primary = artist.HasValue ? ParseArtistOverview(artistId, artist.Value) : null;
        SpotifyArtistOverview? fallback = overview.HasValue ? ParseArtistOverview(artistId, overview.Value) : null;
        return MergeArtistOverview(primary, fallback);
    }

    public async Task<SpotifyArtistHydratedPage?> FetchArtistHydratedPageAsync(string artistId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artistId))
        {
            return null;
        }
        PathfinderAuthContext? context = await BuildAuthContextAsync(cancellationToken);
        if (context is null)
        {
            return null;
        }
        Task<JsonElement?> artistTask = QueryArtistAsync(context, artistId, cancellationToken);
        Task<JsonElement?> overviewTask = QueryArtistOverviewAsync(context, artistId, cancellationToken);
        Task<List<SpotifyAlbumSummary>> discographyTask = FetchArtistDiscographyAsync(context, artistId, cancellationToken);
        await Task.WhenAll(artistTask, overviewTask, discographyTask);
        JsonElement? artist = artistTask.Result;
        JsonElement? overview = overviewTask.Result;
        if (!artist.HasValue && !overview.HasValue)
        {
            return null;
        }
        JsonElement primaryArtistValue = artist ?? overview ?? default;
        SpotifyArtistExtras extras = ParseArtistExtras(primaryArtistValue);
        if (IsPlaceholderBiography(extras.Biography))
        {
            extras = await TryHydrateArtistExtrasFromLocalesAsync(context, artistId, extras, cancellationToken);
        }
        if (IsPlaceholderBiography(extras.Biography))
        {
            _logger.LogDebug("Spotify Pathfinder biography hydration skipped: no alternate auth context available.");
        }
        List<SpotifyAlbumSummary> albums = discographyTask.Result;
        SpotifyArtistOverview parsedOverview = MergeArtistOverview(artist.HasValue ? ParseArtistOverview(artistId, artist.Value) : null, overview.HasValue ? ParseArtistOverview(artistId, overview.Value) : null) ?? ParseArtistOverview(artistId, primaryArtistValue);
        List<SpotifyTrackSummary> topTracks = MergeTopTrackSummaries(artist.HasValue ? ParseArtistTopTracks(artist.Value) : null, overview.HasValue ? ParseArtistTopTracks(overview.Value) : null);
        List<SpotifyRelatedArtist> relatedArtists = ParseArtistRelatedArtists(primaryArtistValue);
        List<SpotifyAlbumSummary> appearsOn = ParseArtistAppearsOn(primaryArtistValue);
        return new SpotifyArtistHydratedPage(parsedOverview, extras, topTracks, relatedArtists, appearsOn, albums);
    }

    public async Task<List<SpotifyAlbumSummary>> FetchArtistDiscographyAllAsync(string artistId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artistId))
        {
            return new List<SpotifyAlbumSummary>();
        }
        PathfinderAuthContext? context = await BuildAuthContextAsync(cancellationToken);
        if (context is null)
        {
            return new List<SpotifyAlbumSummary>();
        }
        return await FetchArtistDiscographyAsync(context, artistId, cancellationToken);
    }

    public async Task<SpotifyArtistExtras?> FetchArtistExtrasAsync(string artistId, CancellationToken cancellationToken)
    {
        (PathfinderAuthContext Context, JsonElement? Artist, JsonElement? Overview)? artistQuery = await QueryArtistAndOverviewAsync(artistId, cancellationToken);
        if (!artistQuery.HasValue)
        {
            return null;
        }

        PathfinderAuthContext context = artistQuery.Value.Context;
        JsonElement? artist = artistQuery.Value.Artist;
        JsonElement? overview = artistQuery.Value.Overview;
        SpotifyArtistExtras extras = ParseArtistExtras(artist ?? overview ?? default);
        if (IsPlaceholderBiography(extras.Biography))
        {
            extras = await TryHydrateArtistExtrasFromLocalesAsync(context, artistId, extras, cancellationToken);
        }
        if (IsPlaceholderBiography(extras.Biography))
        {
            _logger.LogDebug("Spotify Pathfinder extras hydration skipped: no alternate auth context available.");
        }
        return extras;
    }

    private async Task<(PathfinderAuthContext Context, JsonElement? Artist, JsonElement? Overview)?> QueryArtistAndOverviewAsync(string artistId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artistId))
        {
            return null;
        }

        PathfinderAuthContext? context = await BuildAuthContextAsync(cancellationToken);
        return context is null ? null : await QueryArtistAndOverviewAsync(context, artistId, cancellationToken);
    }

    private async Task<(PathfinderAuthContext Context, JsonElement? Artist, JsonElement? Overview)?> QueryArtistAndOverviewAsync(PathfinderAuthContext context, string artistId, CancellationToken cancellationToken)
    {
        Task<JsonElement?> artistTask = QueryArtistAsync(context, artistId, cancellationToken);
        Task<JsonElement?> overviewTask = QueryArtistOverviewAsync(context, artistId, cancellationToken);
        await Task.WhenAll(artistTask, overviewTask);

        JsonElement? artist = artistTask.Result;
        JsonElement? overview = overviewTask.Result;
        return !artist.HasValue && !overview.HasValue ? null : (context, artist, overview);
    }

    private async Task<SpotifyArtistExtras> TryHydrateArtistExtrasFromLocalesAsync(PathfinderAuthContext context, string artistId, SpotifyArtistExtras current, CancellationToken cancellationToken)
    {
        if (!IsPlaceholderBiography(current.Biography))
        {
            return current;
        }
        JsonElement? jsonElement = (await QueryArtistOverviewAsync(context, artistId, cancellationToken, "en-US")) ?? (await QueryArtistOverviewAsync(context, artistId, cancellationToken, "en"));
        JsonElement? localized = jsonElement;
        if (!localized.HasValue)
        {
            return current;
        }
        SpotifyArtistExtras localizedExtras = ParseArtistExtras(localized.Value);
        if (IsPlaceholderBiography(localizedExtras.Biography))
        {
            return current;
        }
        return current with
        {
            Biography = localizedExtras.Biography
        };
    }

    public async Task<List<SpotifyTrackSummary>> FetchArtistTopTracksAsync(string artistId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artistId))
        {
            return new List<SpotifyTrackSummary>();
        }
        PathfinderAuthContext? context = await BuildAuthContextAsync(cancellationToken);
        if (context is null)
        {
            return new List<SpotifyTrackSummary>();
        }
        Task<JsonElement?> artistTask = QueryArtistAsync(context, artistId, cancellationToken);
        Task<JsonElement?> overviewTask = QueryArtistOverviewAsync(context, artistId, cancellationToken);
        await Task.WhenAll<JsonElement?>(artistTask, overviewTask);
        JsonElement? artist = artistTask.Result;
        JsonElement? overview = overviewTask.Result;
        if (!artist.HasValue && !overview.HasValue)
        {
            return new List<SpotifyTrackSummary>();
        }
        return MergeTopTrackSummaries(artist.HasValue ? ParseArtistTopTracks(artist.Value) : null, overview.HasValue ? ParseArtistTopTracks(overview.Value) : null);
    }

    public async Task<List<SpotifyRelatedArtist>> FetchArtistRelatedArtistsAsync(string artistId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artistId))
        {
            return new List<SpotifyRelatedArtist>();
        }
        PathfinderAuthContext? context = await BuildAuthContextAsync(cancellationToken);
        if (context is null)
        {
            return new List<SpotifyRelatedArtist>();
        }
        JsonElement? artist = await QueryArtistAsync(context, artistId, cancellationToken);
        if (!artist.HasValue)
        {
            return new List<SpotifyRelatedArtist>();
        }
        return ParseArtistRelatedArtists(artist.Value);
    }

    public async Task<List<SpotifyAlbumSummary>> FetchArtistAppearsOnAsync(string artistId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artistId))
        {
            return new List<SpotifyAlbumSummary>();
        }
        PathfinderAuthContext? context = await BuildAuthContextAsync(cancellationToken);
        if (context is null)
        {
            return new List<SpotifyAlbumSummary>();
        }
        JsonElement? artist = await QueryArtistAsync(context, artistId, cancellationToken);
        if (!artist.HasValue)
        {
            return new List<SpotifyAlbumSummary>();
        }
        return ParseArtistAppearsOn(artist.Value);
    }

    public async Task<List<SpotifyTrackSummary>?> FetchPlaylistTracksWithBlobAuthAsync(string playlistId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return null;
        }
        PathfinderAuthContext? context = await BuildBlobAuthContextAsync(cancellationToken);
        if (context is null)
        {
            return null;
        }
        PlaylistUnionResult? playlist = await QueryPlaylistUnionAsync(context, playlistId, cancellationToken);
        if (playlist is null)
        {
            return null;
        }
        return ParsePlaylistTracks(playlist.PlaylistUnion, playlist.TrackItems);
    }

    public async Task<Dictionary<string, string>> FetchTrackIsrcsAsync(IReadOnlyList<string> trackIds, CancellationToken cancellationToken, int? maxConcurrency = null)
    {
        Dictionary<string, string> results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (trackIds.Count == 0)
        {
            return results;
        }
        List<string> distinct = trackIds.Where((string value) => !string.IsNullOrWhiteSpace(value)).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinct.Count == 0)
        {
            return results;
        }
        PathfinderAuthContext? context = await BuildAuthContextAsync(cancellationToken);
        if (context is null)
        {
            return results;
        }
        List<string> toFetch = CollectUncachedTrackIds(distinct, results);
        if (toFetch.Count == 0)
        {
            return results;
        }
        int concurrencyLimit = maxConcurrency ?? 8;
        if (concurrencyLimit < 1)
        {
            concurrencyLimit = 1;
        }
        SemaphoreSlim gate = new SemaphoreSlim(concurrencyLimit, concurrencyLimit);
        try
        {
            List<Task> tasks = new List<Task>(toFetch.Count);
            foreach (string id2 in toFetch)
            {
                await gate.WaitAsync(cancellationToken);
                tasks.Add(FetchTrackIsrcAsync(context, id2, results, gate, cancellationToken));
            }
            await Task.WhenAll(tasks);
            return results;
        }
        finally
        {
            gate.Dispose();
        }
    }

    private static List<string> CollectUncachedTrackIds(IEnumerable<string> trackIds, Dictionary<string, string> results)
    {
        List<string> uncachedTrackIds = new List<string>();
        foreach (string trackId in trackIds)
        {
            if (IsrcCache.TryGetValue(trackId, out string? cached) && !string.IsNullOrWhiteSpace(cached))
            {
                results[trackId] = cached;
            }
            else
            {
                uncachedTrackIds.Add(trackId);
            }
        }
        return uncachedTrackIds;
    }

    private async Task FetchTrackIsrcAsync(
        PathfinderAuthContext context,
        string trackId,
        Dictionary<string, string> results,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        try
        {
            JsonElement? trackUnion = await QueryTrackUnionAsync(context, trackId, cancellationToken);
            string? isrc = trackUnion.HasValue ? ExtractIsrc(trackUnion.Value) : null;
            if (string.IsNullOrWhiteSpace(isrc))
            {
                return;
            }
            IsrcCache[trackId] = isrc;
            lock (results)
            {
                results[trackId] = isrc;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<List<SpotifyTrackSummary>> FetchTrackSummariesByIdsAsync(IReadOnlyList<string> trackIds, CancellationToken cancellationToken, int? maxConcurrency = null)
    {
        Dictionary<string, SpotifyTrackSummary> results = new Dictionary<string, SpotifyTrackSummary>(StringComparer.OrdinalIgnoreCase);
        if (trackIds.Count == 0)
        {
            return new List<SpotifyTrackSummary>();
        }
        List<string> distinct = trackIds.Where((string value) => !string.IsNullOrWhiteSpace(value)).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinct.Count == 0)
        {
            return new List<SpotifyTrackSummary>();
        }
        PathfinderAuthContext? context = await BuildAuthContextAsync(cancellationToken);
        if (context is null)
        {
            return new List<SpotifyTrackSummary>();
        }
        int concurrencyLimit = Math.Clamp(maxConcurrency ?? 8, 1, 16);
        SemaphoreSlim gate = new SemaphoreSlim(concurrencyLimit, concurrencyLimit);
        try
        {
            List<Task> tasks = new List<Task>(distinct.Count);
            foreach (string id in distinct)
            {
                await gate.WaitAsync(cancellationToken);
                tasks.Add(Task.Run(async delegate
                {
                    try
                    {
                        JsonElement? trackUnion = await QueryTrackUnionAsync(context, id, cancellationToken);
                        if (trackUnion.HasValue)
                        {
                            SpotifyTrackSummary? summary = ParseTrackSummary(trackUnion.Value);
                            if (summary is not null)
                            {
                                lock (results)
                                {
                                    results[id] = summary;
                                }
                            }
                        }
                    }
                    finally
                    {
                        gate.Release();
                    }
                }, cancellationToken));
            }
            await Task.WhenAll(tasks);
            SpotifyTrackSummary? value;
            return (from key in distinct
                    select results.TryGetValue(key, out value) ? value : null into summary
                    where summary is not null
                    select summary).ToList();
        }
        finally
        {
            gate.Dispose();
        }
    }

    public async Task<Dictionary<string, SpotifyPathfinderAudioFeatures>> FetchTrackAudioFeaturesByIdsAsync(IReadOnlyList<string> trackIds, CancellationToken cancellationToken)
    {
        List<string> ids = trackIds.Where((string id) => !string.IsNullOrWhiteSpace(id)).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
        Dictionary<string, SpotifyPathfinderAudioFeatures> output = new Dictionary<string, SpotifyPathfinderAudioFeatures>(StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0)
        {
            return output;
        }
        PathfinderAuthContext? context = await BuildAuthContextAsync(cancellationToken);
        if (context is null)
        {
            return output;
        }
        PersistedQueryOverride persisted = GetPersistedQuery("decorateContextTracks", 1, "4210a7207beed5259945560dbeeb0bd55d709ce25de5f28021e1393b6af59121");
        for (int i = 0; i < ids.Count; i += 50)
        {
            List<string> batch = ids.Skip(i).Take(50).ToList();
            List<string> uris = batch.Select((string id) => SpotifyTrackUriPrefix + id).ToList();
            var payload = new
            {
                operationName = "decorateContextTracks",
                variables = new { uris },
                extensions = new
                {
                    persistedQuery = new
                    {
                        version = persisted.Version,
                        sha256Hash = persisted.Sha256Hash
                    }
                }
            };
            using JsonDocument? doc = await QueryAsync(context, payload, cancellationToken);
            if (doc is not null)
            {
                CollectPathfinderAudioFeatures(requestedTrackIds: new HashSet<string>(batch, StringComparer.OrdinalIgnoreCase), element: doc.RootElement, output: output);
            }
        }
        return output;
    }

    public async Task<SpotiFlacPlaylistPayload?> FetchSpotiFlacPlaylistAsync(string playlistId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return null;
        }
        PathfinderAuthContext? context = await BuildAuthContextAsync(cancellationToken);
        if (context is null)
        {
            return null;
        }
        PlaylistUnionResult? playlist = await QueryPlaylistUnionAsync(context, playlistId, cancellationToken);
        if (playlist is null)
        {
            return null;
        }
        return BuildSpotiFlacPlaylistPayload(playlist.PlaylistUnion, playlist.TrackItems);
    }

    public async Task<SpotiFlacPlaylistPayload?> FetchSpotiFlacPlaylistMetadataAsync(string playlistId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return null;
        }
        PathfinderAuthContext? context = await BuildAuthContextAsync(cancellationToken);
        if (context is null)
        {
            return null;
        }
        PlaylistUnionResult? playlist = await QueryPlaylistUnionAsync(context, playlistId, cancellationToken, 1);
        if (playlist is null)
        {
            return null;
        }
        return BuildSpotiFlacPlaylistPayload(playlist.PlaylistUnion, null);
    }

    private async Task<PathfinderAuthContext?> BuildAuthContextAsync(CancellationToken cancellationToken)
    {
        PathfinderAuthContext? blobContext = await BuildBlobAuthContextAsync(cancellationToken);
        if (blobContext is not null)
        {
            _logger.LogInformation("Spotify Pathfinder auth: using active Spotify auth context.");
            return blobContext;
        }
        LogBlobAuthMissingWarningThrottled();
        return null;
    }

    public async Task<bool> HasPathfinderAuthContextAsync(CancellationToken cancellationToken)
    {
        return await BuildBlobAuthContextAsync(cancellationToken) is not null;
    }

    public async Task<bool> HasBlobBackedAuthContextAsync(CancellationToken cancellationToken)
    {
        string? blobPath = await TryResolveActiveSpotifyBlobPathAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(blobPath))
        {
            return false;
        }

        return await IsUsableWebPlayerBlobPathAsync(blobPath, cancellationToken);
    }

    public async Task<bool> HasLibrespotAuthContextAsync(CancellationToken cancellationToken)
    {
        string? blobPath = await TryResolveActiveLibrespotBlobPathAsync();
        return !string.IsNullOrWhiteSpace(blobPath);
    }

    private void LogBlobAuthMissingWarningThrottled()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now - _lastBlobAuthMissingWarningUtc >= TimeSpan.FromMinutes(2.0))
        {
            _lastBlobAuthMissingWarningUtc = now;
            _logger.LogWarning("Spotify Pathfinder auth unavailable: no blob-backed auth context resolved.");
            return;
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Spotify Pathfinder auth unavailable: no blob-backed auth context resolved (throttled).");
        }
    }

    private async Task<PathfinderAuthContext?> BuildBlobAuthContextAsync(CancellationToken cancellationToken)
    {
        string? blobPath = await TryResolveActiveSpotifyBlobPathAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(blobPath))
        {
            var context = await BuildBlobAuthContextAsync(blobPath, cancellationToken);
            if (context is not null)
            {
                return context;
            }
        }

        string? librespotBlobPath = await TryResolveActiveLibrespotBlobPathAsync();
        if (!string.IsNullOrWhiteSpace(librespotBlobPath) &&
            !string.Equals(librespotBlobPath, blobPath, StringComparison.OrdinalIgnoreCase))
        {
            var librespotContext = await BuildBlobAuthContextAsync(librespotBlobPath, cancellationToken);
            if (librespotContext is not null)
            {
                return librespotContext;
            }
        }

        _logger.LogDebug("Spotify Pathfinder auth unavailable: missing usable auth blob path.");
        return null;
    }

    private async Task<PathfinderAuthContext?> BuildBlobAuthContextAsync(string blobPath, CancellationToken cancellationToken)
    {
        await _authGate.WaitAsync(cancellationToken);
        try
        {
            if (TryGetCachedBlobContext(blobPath, out PathfinderAuthContext? cachedContext) && cachedContext is not null)
            {
                return cachedContext;
            }

            if (await _blobService.IsLibrespotBlobAsync(blobPath, cancellationToken))
            {
                return await BuildLibrespotAuthContextAsync(blobPath, cancellationToken);
            }

            SpotifyBlobPayload? payload = await _blobService.TryLoadBlobPayloadAsync(blobPath, cancellationToken);
            if (payload is null)
            {
                _logger.LogWarning("Spotify Pathfinder auth unavailable: invalid blob payload.");
                return null;
            }
            LogBlobSnapshot(payload);
            if (payload.Cookies.Count == 0 || string.IsNullOrWhiteSpace(payload.UserAgent))
            {
                _logger.LogWarning("Spotify Pathfinder auth unavailable: blob has no web player cookies.");
                return null;
            }
            using HttpClient? cookieClient = _blobService.CreateCookieClient(payload);
            if (cookieClient is null)
            {
                _logger.LogWarning("Spotify Pathfinder auth unavailable: failed to build cookie client.");
                return null;
            }
            SpotifyBlobService.SpotifyWebPlayerTokenInfo? tokenInfo = await _blobService.GetWebPlayerTokenInfoAsync(blobPath, cancellationToken);
            string? accessToken = tokenInfo?.AccessToken;
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                _logger.LogWarning("Spotify Pathfinder auth unavailable: missing web player access token.");
                return null;
            }
            WebPlayerSessionInfo? sessionInfo = await FetchWebPlayerSessionInfoFromBlobAsync(cookieClient, payload, cancellationToken);
            if (sessionInfo is null || string.IsNullOrWhiteSpace(sessionInfo.ClientVersion))
            {
                _logger.LogWarning("Spotify Pathfinder auth unavailable: missing client version.");
                return null;
            }
            string? clientId = sessionInfo.ClientId;
            if (string.IsNullOrWhiteSpace(clientId))
            {
                clientId = tokenInfo?.ClientId;
            }
            if (string.IsNullOrWhiteSpace(clientId))
            {
                _logger.LogWarning("Spotify Pathfinder auth unavailable: missing client id.");
                return null;
            }
            string deviceId = sessionInfo.DeviceId ?? string.Empty;
            HttpClient blobClient = _httpClientFactory.CreateClient();
            string? clientToken = await GetClientTokenAsync(blobClient, clientId, sessionInfo.ClientVersion, deviceId, cancellationToken);
            if (string.IsNullOrWhiteSpace(clientToken))
            {
                _logger.LogWarning("Spotify Pathfinder auth unavailable: missing client token.");
                return null;
            }
            PathfinderAuthContext context = new PathfinderAuthContext(accessToken, clientToken, sessionInfo.ClientVersion, deviceId);
            CacheBlobContext(blobPath, context, ResolveWebPlayerAccessTokenExpiry(tokenInfo?.ExpiresAtUnixMs), (_clientTokenExpiresAt != default(DateTimeOffset)) ? _clientTokenExpiresAt : DateTimeOffset.UtcNow.AddMinutes(50.0));
            return context;
        }
        finally
        {
            _authGate.Release();
        }
    }

    private async Task<PathfinderAuthContext?> BuildLibrespotAuthContextAsync(string blobPath, CancellationToken cancellationToken)
    {
        SpotifyBlobService.SpotifyAccessTokenResult tokenResult = await _blobService.GetWebApiAccessTokenAsync(
            blobPath,
            cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(tokenResult.AccessToken))
        {
            _logger.LogWarning("Spotify Pathfinder auth unavailable: missing librespot access token.");
            return null;
        }

        HttpClient client = _httpClientFactory.CreateClient();
        WebPlayerConfig? config = await FetchPublicWebPlayerConfigAsync(client, cancellationToken);
        string? clientVersion = config?.ClientVersion;
        if (string.IsNullOrWhiteSpace(clientVersion))
        {
            _logger.LogWarning("Spotify Pathfinder auth unavailable: missing public client version for librespot session.");
            return null;
        }

        string clientId = string.IsNullOrWhiteSpace(config?.ClientId)
            ? WebPlayerClientIdFallback
            : config!.ClientId!;
        string deviceId = GenerateStableDeviceId(blobPath);
        string? clientToken = await GetClientTokenAsync(client, clientId, clientVersion, deviceId, cancellationToken);
        if (string.IsNullOrWhiteSpace(clientToken))
        {
            _logger.LogWarning("Spotify Pathfinder auth unavailable: missing client token for librespot session.");
            return null;
        }

        PathfinderAuthContext context = new PathfinderAuthContext(
            tokenResult.AccessToken,
            clientToken,
            clientVersion,
            deviceId);
        CacheBlobContext(
            blobPath,
            context,
            ResolveWebPlayerAccessTokenExpiry(tokenResult.ExpiresAtUnixMs),
            (_clientTokenExpiresAt != default(DateTimeOffset)) ? _clientTokenExpiresAt : DateTimeOffset.UtcNow.AddMinutes(50.0));
        return context;
    }

    private bool TryGetCachedBlobContext(string blobPath, out PathfinderAuthContext? context)
    {
        context = null;
        if (string.IsNullOrWhiteSpace(blobPath) || string.IsNullOrWhiteSpace(_blobContextBlobPath) || _blobContext is null || !string.Equals(_blobContextBlobPath, blobPath, StringComparison.Ordinal))
        {
            return false;
        }
        DateTimeOffset utcNow = DateTimeOffset.UtcNow;
        if (_blobContextAccessTokenExpiresAt != default(DateTimeOffset) && utcNow >= _blobContextAccessTokenExpiresAt - TokenRefreshWindow)
        {
            return false;
        }
        if (_blobContextClientTokenExpiresAt != default(DateTimeOffset) && utcNow >= _blobContextClientTokenExpiresAt - TokenRefreshWindow)
        {
            return false;
        }
        context = _blobContext;
        return true;
    }

    private void CacheBlobContext(string blobPath, PathfinderAuthContext context, DateTimeOffset accessTokenExpiresAt, DateTimeOffset clientTokenExpiresAt)
    {
        _blobContextBlobPath = blobPath;
        _blobContext = context;
        _blobContextAccessTokenExpiresAt = accessTokenExpiresAt;
        _blobContextClientTokenExpiresAt = clientTokenExpiresAt;
    }

    private async Task<string?> TryResolveActiveSpotifyBlobPathAsync(CancellationToken cancellationToken)
    {
        string? userId = _userContext.UserId;
        if (!string.IsNullOrWhiteSpace(userId))
        {
            string? userBlob = await TryResolveUserWebPlayerBlobPathAsync(userId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(userBlob))
            {
                return userBlob;
            }
        }

        string? platformBlob = await TryResolvePlatformWebPlayerBlobPathAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(platformBlob))
        {
            return platformBlob;
        }

        string? backgroundBlob = await TryResolveBackgroundUserWebPlayerBlobPathAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(backgroundBlob))
        {
            return backgroundBlob;
        }

        return null;
    }

    private async Task<string?> TryResolveBackgroundUserWebPlayerBlobPathAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_userContext.UserId))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _backgroundUserBlobCheckedAt < TimeSpan.FromSeconds(30))
        {
            return _backgroundUserBlobCachedPath;
        }

        await _backgroundUserBlobResolutionGate.WaitAsync(cancellationToken);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (now - _backgroundUserBlobCheckedAt < TimeSpan.FromSeconds(30))
            {
                return _backgroundUserBlobCachedPath;
            }

            string root = AppDataPathResolver.ResolveDataRootOrDefault(AppDataPathResolver.GetDefaultWorkersDataDir());
            string usersRoot = Path.Join(root, SpotifyDirectoryName, "users");
            if (!Directory.Exists(usersRoot))
            {
                return CacheBackgroundUserBlobPath(now, null);
            }

            List<string> userIds = EnumerateSpotifyUserIds(usersRoot);
            if (userIds.Count == 0)
            {
                return CacheBackgroundUserBlobPath(now, null);
            }

            List<string> resolved = await ResolveBackgroundUserBlobCandidatesAsync(userIds, cancellationToken);
            if (resolved.Count > 1)
            {
                _logger.LogDebug(
                    "Spotify Pathfinder background auth unresolved: found multiple user web-player blobs without request user context.");
            }

            return CacheBackgroundUserBlobPath(now, resolved.Count == 1 ? resolved[0] : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to resolve Spotify background user web-player blob path.");
            return CacheBackgroundUserBlobPath(DateTimeOffset.UtcNow, null);
        }
        finally
        {
            _backgroundUserBlobResolutionGate.Release();
        }
    }

    private async Task<string?> TryResolveUserWebPlayerBlobPathAsync(string userId, CancellationToken cancellationToken)
    {
        try
        {
            SpotifyUserAuthState userState = await _userAuthStore.LoadAsync(userId);
            List<string?> candidates = BuildWebPlayerBlobCandidates(userState);

            string? resolvedPath = await TryResolveFirstValidWebPlayerBlobPathAsync(candidates, cancellationToken);
            if (!string.IsNullOrWhiteSpace(resolvedPath))
            {
                return resolvedPath;
            }

            return await TryMaterializeUserWebPlayerBlobAsync(userId, userState, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to resolve Spotify user blob path for Pathfinder.");
            return null;
        }
    }

    private string? CacheBackgroundUserBlobPath(DateTimeOffset checkedAt, string? path)
    {
        _backgroundUserBlobCachedPath = path;
        _backgroundUserBlobCheckedAt = checkedAt;
        return path;
    }

    private static List<string> EnumerateSpotifyUserIds(string usersRoot) => Directory.EnumerateDirectories(usersRoot)
        .Select(Path.GetFileName)
        .Where(id => !string.IsNullOrWhiteSpace(id))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList()!;

    private async Task<List<string>> ResolveBackgroundUserBlobCandidatesAsync(
        IEnumerable<string> userIds,
        CancellationToken cancellationToken)
    {
        var resolved = new List<string>();
        foreach (var id in userIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SpotifyUserAuthState userState = await _userAuthStore.LoadAsync(id);
            string? resolvedPath = await TryResolveFirstValidWebPlayerBlobPathAsync(
                BuildWebPlayerBlobCandidates(userState),
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(resolvedPath))
            {
                resolved.Add(resolvedPath);
            }
        }

        return resolved;
    }

    private static List<string?> BuildWebPlayerBlobCandidates(SpotifyUserAuthState userState)
    {
        List<string?> candidates = new List<string?>
        {
            SpotifyUserAuthStore.ResolveActiveWebPlayerBlobPath(userState),
            SpotifyUserAuthStore.ResolveActiveBlobPath(userState)
        };
        candidates.AddRange(userState.Accounts
            .OrderByDescending(GetAccountUpdatedAtOrMinValue)
            .SelectMany(account => new[] { account.WebPlayerBlobPath, account.BlobPath }));
        return candidates;
    }

    private static DateTimeOffset GetAccountUpdatedAtOrMinValue(SpotifyUserAccount account)
    {
        DateTimeOffset updated = account.UpdatedAt == default(DateTimeOffset) ? account.CreatedAt : account.UpdatedAt;
        return updated == default(DateTimeOffset) ? DateTimeOffset.MinValue : updated;
    }

    private async Task<string?> TryResolvePlatformWebPlayerBlobPathAsync(CancellationToken cancellationToken)
    {
        try
        {
            PlatformAuthState state = await _platformAuthService.LoadAsync();
            SpotifyConfig? spotifyState = state.Spotify;
            if (spotifyState?.Accounts is not { Count: > 0 })
            {
                return null;
            }

            List<string?> candidates = new List<string?>();
            if (!string.IsNullOrWhiteSpace(spotifyState.ActiveAccount))
            {
                SpotifyAccount? activeAccount = spotifyState.Accounts.FirstOrDefault(account =>
                    account.Name.Equals(spotifyState.ActiveAccount, StringComparison.OrdinalIgnoreCase));
                candidates.Add(activeAccount?.WebPlayerBlobPath);
                candidates.Add(activeAccount?.BlobPath);
            }

            candidates.AddRange(spotifyState.Accounts
                .OrderByDescending(account =>
                {
                    DateTimeOffset updated = account.UpdatedAt == default(DateTimeOffset) ? account.CreatedAt : account.UpdatedAt;
                    return updated == default(DateTimeOffset) ? DateTimeOffset.MinValue : updated;
                })
                .SelectMany(account => new[] { account.WebPlayerBlobPath, account.BlobPath }));

            string? resolvedPath = await TryResolveFirstValidWebPlayerBlobPathAsync(candidates, cancellationToken);
            if (!string.IsNullOrWhiteSpace(resolvedPath))
            {
                return resolvedPath;
            }

            return await TryMaterializePlatformWebPlayerBlobAsync(spotifyState, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to resolve Spotify platform blob path for Pathfinder.");
            return null;
        }
    }

    private async Task<string?> TryMaterializeUserWebPlayerBlobAsync(string userId, SpotifyUserAuthState userState, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userState.WebPlayerSpDc))
        {
            return null;
        }

        try
        {
            SpotifyUserAccount? activeAccount = SpotifyUserAuthStore.ResolveActiveAccount(userState);
            string accountName = string.IsNullOrWhiteSpace(activeAccount?.Name) ? "web-player" : activeAccount!.Name.Trim();
            string blobDir = _userAuthStore.GetUserBlobDir(userId);
            Directory.CreateDirectory(blobDir);
            string blobPath = Path.Join(blobDir, $"{SanitizeBlobName(accountName)}.web.json");
            await _blobService.SaveWebPlayerBlobAsync(
                blobPath,
                userState.WebPlayerSpDc!,
                userState.WebPlayerUserAgent,
                cancellationToken);

            DateTimeOffset now = DateTimeOffset.UtcNow;
            bool changed = false;
            if (activeAccount is null)
            {
                activeAccount = new SpotifyUserAccount
                {
                    Name = accountName,
                    WebPlayerBlobPath = blobPath,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                userState.Accounts.Add(activeAccount);
                changed = true;
            }
            else if (!string.Equals(activeAccount.WebPlayerBlobPath, blobPath, StringComparison.Ordinal))
            {
                activeAccount.WebPlayerBlobPath = blobPath;
                activeAccount.UpdatedAt = now;
                changed = true;
            }

            if (!string.Equals(userState.ActiveAccount, activeAccount.Name, StringComparison.OrdinalIgnoreCase))
            {
                userState.ActiveAccount = activeAccount.Name;
                changed = true;
            }

            if (changed)
            {
                await _userAuthStore.SaveAsync(userId, userState);
            }

            if (await IsUsableWebPlayerBlobPathAsync(blobPath, cancellationToken))
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Recovered Spotify web-player blob for user {UserId} at {BlobPath}.", userId, blobPath);
                }
                return blobPath;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to recover Spotify web-player blob for user {UserId}.", userId);
        }

        return null;
    }

    private async Task<string?> TryMaterializePlatformWebPlayerBlobAsync(SpotifyConfig spotifyState, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(spotifyState.WebPlayerSpDc))
        {
            return null;
        }

        try
        {
            string accountName = string.IsNullOrWhiteSpace(spotifyState.ActiveAccount) ? "platform" : spotifyState.ActiveAccount.Trim();
            string root = AppDataPathResolver.ResolveDataRootOrDefault(AppDataPathResolver.GetDefaultWorkersDataDir());
            string blobDir = Path.Join(root, "spotify", "blobs");
            Directory.CreateDirectory(blobDir);
            string blobPath = Path.Join(blobDir, $"{SanitizeBlobName(accountName)}.web.json");
            await _blobService.SaveWebPlayerBlobAsync(
                blobPath,
                spotifyState.WebPlayerSpDc!,
                spotifyState.WebPlayerUserAgent,
                cancellationToken);

            DateTimeOffset now = DateTimeOffset.UtcNow;
            await _platformAuthService.UpdateAsync(platformState =>
            {
                platformState.Spotify ??= new SpotifyConfig();
                SpotifyAccount? account = platformState.Spotify.Accounts.FirstOrDefault(existing =>
                    existing.Name.Equals(accountName, StringComparison.OrdinalIgnoreCase));
                if (account is null)
                {
                    account = new SpotifyAccount
                    {
                        Name = accountName,
                        BlobPath = blobPath,
                        WebPlayerBlobPath = blobPath,
                        CreatedAt = now,
                        UpdatedAt = now
                    };
                    platformState.Spotify.Accounts.Add(account);
                }
                else
                {
                    account.WebPlayerBlobPath = blobPath;
                    account.UpdatedAt = now;
                }

                platformState.Spotify.ActiveAccount = accountName;
                platformState.Spotify.WebPlayerSpDc = spotifyState.WebPlayerSpDc;
                platformState.Spotify.WebPlayerUserAgent = spotifyState.WebPlayerUserAgent;
                return 0;
            });

            if (await IsUsableWebPlayerBlobPathAsync(blobPath, cancellationToken))
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Recovered platform Spotify web-player blob at {BlobPath}.", blobPath);
                }
                return blobPath;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to recover platform Spotify web-player blob.");
        }

        return null;
    }

    private static string SanitizeBlobName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "web-player";
        }

        char[] invalidChars = Path.GetInvalidFileNameChars();
        char[] normalized = value.Trim()
            .Select(character => invalidChars.Contains(character) ? '-' : character)
            .ToArray();
        string sanitized = new string(normalized).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "web-player" : sanitized;
    }

    private async Task<string?> TryResolveFirstValidWebPlayerBlobPathAsync(IEnumerable<string?> candidatePaths, CancellationToken cancellationToken)
    {
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? candidatePath in candidatePaths)
        {
            if (string.IsNullOrWhiteSpace(candidatePath))
            {
                continue;
            }

            string normalizedPath = candidatePath.Trim();
            if (!seen.Add(normalizedPath))
            {
                continue;
            }

            if (await IsUsableWebPlayerBlobPathAsync(normalizedPath, cancellationToken))
            {
                return normalizedPath;
            }
        }

        return null;
    }

    private async Task<bool> IsUsableWebPlayerBlobPathAsync(string blobPath, CancellationToken cancellationToken)
    {
        try
        {
            return await _blobService.IsWebPlayerBlobAsync(blobPath, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Skipping unusable Spotify web-player blob path {BlobPath}.", blobPath);
            }
            return false;
        }
    }

    private async Task<WebPlayerSessionInfo?> FetchWebPlayerSessionInfoFromBlobAsync(HttpClient cookieClient, SpotifyBlobPayload payload, CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, WebPlayerRootUrl);
            request.Headers.Accept.ParseAdd("text/html");
            using HttpResponseMessage response = await cookieClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                string snippet = await response.Content.ReadAsStringAsync(cancellationToken);
                if (snippet.Length > 200)
                {
                    snippet = string.Concat(snippet.AsSpan(0, 200), "…");
                }
                _logger.LogWarning("Spotify web-player session request failed for blob: status={Status} snippet={Snippet}", (int)response.StatusCode, string.IsNullOrWhiteSpace(snippet) ? "empty" : snippet.Replace("\r", " ").Replace("\n", " ").Trim());
                return null;
            }
            string html = await response.Content.ReadAsStringAsync(cancellationToken);
            WebPlayerConfig? config = ParseAppServerConfig(html);
            if (config is null)
            {
                string trimmed = html.Length > 200
                    ? string.Concat(html.AsSpan(0, 200), "…")
                    : html;
                _logger.LogWarning("Spotify web-player session config missing for blob. html_snippet={Snippet}", string.IsNullOrWhiteSpace(trimmed) ? "empty" : trimmed.Replace("\r", " ").Replace("\n", " ").Trim());
                return null;
            }
            string? deviceId = payload.Cookies.FirstOrDefault((SpotifyBlobCookie cookie) => cookie.Name.Equals("sp_t", StringComparison.OrdinalIgnoreCase))?.Value;
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                deviceId = TryExtractCookieValue(response, "sp_t");
            }
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                deviceId = Guid.NewGuid().ToString("N");
                _logger.LogWarning("Spotify web-player session missing sp_t cookie; generated device id.");
            }
            return new WebPlayerSessionInfo(config.ClientVersion, config.ClientId, deviceId);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            _logger.LogWarning(ex, "Failed to fetch Spotify web player session info.");
            return null;
        }
    }

    private async Task<WebPlayerConfig?> FetchPublicWebPlayerConfigAsync(HttpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, WebPlayerRootUrl);
            request.Headers.Accept.ParseAdd("text/html");
            request.Headers.UserAgent.ParseAdd(WebPlayerUserAgent);
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Spotify public web-player config request failed: status={Status}",
                    (int)response.StatusCode);
                return null;
            }

            string html = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseAppServerConfig(html);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Spotify public web-player config request failed.");
            return null;
        }
    }

    private static string? TryExtractCookieValue(HttpResponseMessage response, string cookieName)
    {
        if (response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values))
        {
            return values.Select((string value) => TryParseCookieValue(value, cookieName)).FirstOrDefault((string? cookie) => !string.IsNullOrWhiteSpace(cookie));
        }
        return null;
    }

    private static string? TryParseCookieValue(string headerValue, string cookieName)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return null;
        }
        string[] array = headerValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (array.Length == 0)
        {
            return null;
        }
        string text = array[0];
        int num = text.IndexOf('=');
        if (num <= 0)
        {
            return null;
        }
        string text2 = text.Substring(0, num);
        if (!text2.Equals(cookieName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return text[(num + 1)..];
    }

    private void LogBlobSnapshot(SpotifyBlobPayload payload)
    {
        string text = (string.IsNullOrWhiteSpace(payload.UserAgent) ? "(missing)" : payload.UserAgent.Trim());
        List<string> list = (from cookie in payload.Cookies
                             where !string.IsNullOrWhiteSpace(cookie.Name)
                             select string.IsNullOrWhiteSpace(cookie.Domain) ? cookie.Name.Trim() : (cookie.Name.Trim() + "@" + cookie.Domain.Trim())).Distinct<string>(StringComparer.OrdinalIgnoreCase).OrderBy<string, string>((string entry) => entry, StringComparer.OrdinalIgnoreCase).ToList();
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Spotify blob snapshot: userAgent={UserAgent} cookieCount={CookieCount} cookies=[{Cookies}]", text, list.Count, string.Join(", ", list));
        }
    }

    private static WebPlayerConfig? ParseAppServerConfig(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }
        Match match = Regex.Match(html, "<script id=\"appServerConfig\" type=\"text/plain\">([^<]+)</script>", RegexOptions.None, RegexTimeout);
        if (!match.Success || match.Groups.Count < 2)
        {
            return null;
        }
        string value = match.Groups[1].Value;
        try
        {
            byte[] bytes = Convert.FromBase64String(value);
            string json = Encoding.UTF8.GetString(bytes);
            using JsonDocument jsonDocument = JsonDocument.Parse(json);
            JsonElement rootElement = jsonDocument.RootElement;
            string? clientVersion = TryGetString(rootElement, "clientVersion") ?? TryGetString(rootElement, "client_version");
            string? clientId = TryGetString(rootElement, "clientId") ?? TryGetString(rootElement, "client_id") ?? TryGetString(rootElement, "clientID");
            return new WebPlayerConfig(clientVersion, clientId);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            return null;
        }
    }

    private async Task<string?> GetClientTokenAsync(HttpClient client, string clientId, string clientVersion, string deviceId, CancellationToken cancellationToken)
    {
        try
        {
            ClientTokenRequestContext? requestContext = NormalizeClientTokenRequest(clientId, clientVersion, deviceId);
            if (requestContext == null)
            {
                return null;
            }
            if (IsClientTokenValid(requestContext.ClientId, requestContext.ClientVersion, requestContext.DeviceId))
            {
                return _clientToken;
            }
            string payloadJson = SerializeClientTokenPayload(requestContext);
            using HttpRequestMessage request = CreateClientTokenRequest(payloadJson);
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                string errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                string snippet;
                if (string.IsNullOrWhiteSpace(errorBody))
                {
                    snippet = "(empty)";
                }
                else
                {
                    snippet = errorBody.Length > 200
                        ? new string(errorBody.AsSpan(0, 200))
                        : errorBody;
                }
                _logger.LogWarning("Spotify client token request failed: status={Status} body={Body} clientId={ClientId} clientVersion={ClientVersion} deviceId={DeviceId} payload={Payload}", (int)response.StatusCode, snippet, requestContext.ClientId, requestContext.ClientVersion, requestContext.DeviceId, payloadJson);
                return null;
            }
            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!TryExtractGrantedClientToken(doc.RootElement, out JsonElement granted, out string? token))
            {
                return null;
            }
            CacheClientToken(requestContext, token!, granted);
            return token;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            _logger.LogWarning(ex, "Spotify client token request failed.");
            return null;
        }
    }

    private ClientTokenRequestContext? NormalizeClientTokenRequest(string clientId, string clientVersion, string deviceId)
    {
        string normalizedClientId = clientId;
        if (string.IsNullOrWhiteSpace(normalizedClientId))
        {
            _logger.LogWarning("Spotify client token missing clientId. Falling back to web-player client id.");
            normalizedClientId = WebPlayerClientIdFallback;
        }
        if (string.IsNullOrWhiteSpace(clientVersion))
        {
            _logger.LogWarning("Spotify client token request skipped: missing clientVersion.");
            return null;
        }
        string normalizedDeviceId = deviceId;
        if (string.IsNullOrWhiteSpace(normalizedDeviceId))
        {
            normalizedDeviceId = GenerateDeviceId();
            _logger.LogWarning("Spotify client token missing deviceId. Generated new device id.");
        }
        return new ClientTokenRequestContext(normalizedClientId, clientVersion, normalizedDeviceId);
    }

    private static string SerializeClientTokenPayload(ClientTokenRequestContext requestContext)
    {
        Dictionary<string, object?> payload = new Dictionary<string, object?>
        {
            ["client_data"] = new Dictionary<string, object?>
            {
                ["client_version"] = requestContext.ClientVersion,
                ["client_id"] = requestContext.ClientId,
                ["js_sdk_data"] = new Dictionary<string, object?>
                {
                    ["device_brand"] = "unknown",
                    ["device_model"] = "unknown",
                    ["os"] = "windows",
                    ["os_version"] = "NT 10.0",
                    ["device_id"] = requestContext.DeviceId,
                    ["device_type"] = "computer"
                }
            }
        };
        return JsonSerializer.Serialize(payload, ClientTokenJsonOptions);
    }

    private static HttpRequestMessage CreateClientTokenRequest(string payloadJson)
    {
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, ClientTokenUrl)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(payloadJson))
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.UserAgent.ParseAdd(WebPlayerUserAgent);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.TryAddWithoutValidation("Authority", "clienttoken.spotify.com");
        return request;
    }

    private static bool TryExtractGrantedClientToken(JsonElement rootElement, out JsonElement granted, out string? token)
    {
        granted = default(JsonElement);
        token = null;
        if (!rootElement.TryGetProperty("response_type", out JsonElement responseType) || !string.Equals(responseType.GetString(), "RESPONSE_GRANTED_TOKEN_RESPONSE", StringComparison.Ordinal))
        {
            return false;
        }
        if (!rootElement.TryGetProperty("granted_token", out granted) || !granted.TryGetProperty("token", out JsonElement tokenProp))
        {
            return false;
        }
        token = tokenProp.GetString();
        return !string.IsNullOrWhiteSpace(token);
    }

    private void CacheClientToken(ClientTokenRequestContext requestContext, string token, JsonElement granted)
    {
        _clientToken = token;
        _clientTokenClientId = requestContext.ClientId;
        _clientTokenClientVersion = requestContext.ClientVersion;
        _clientTokenDeviceId = requestContext.DeviceId;
        _clientTokenExpiresAt = ResolveClientTokenExpiry(granted);
    }

    private bool IsClientTokenValid(string clientId, string clientVersion, string deviceId)
    {
        if (string.IsNullOrWhiteSpace(_clientToken))
        {
            return false;
        }
        if (!string.Equals(_clientTokenClientId, clientId, StringComparison.Ordinal) || !string.Equals(_clientTokenClientVersion, clientVersion, StringComparison.Ordinal) || !string.Equals(_clientTokenDeviceId, deviceId, StringComparison.Ordinal))
        {
            return false;
        }
        if (_clientTokenExpiresAt != default(DateTimeOffset) && DateTimeOffset.UtcNow >= _clientTokenExpiresAt - TokenRefreshWindow)
        {
            return false;
        }
        return true;
    }

    private static DateTimeOffset ResolveClientTokenExpiry(JsonElement granted)
    {
        if (granted.TryGetProperty("expires_after_seconds", out var value) && value.TryGetInt64(out var value2) && value2 > 0)
        {
            long num = Math.Max(0L, value2 - 60);
            return DateTimeOffset.UtcNow.AddSeconds(num);
        }
        return DateTimeOffset.UtcNow.AddMinutes(50.0);
    }

    private static DateTimeOffset ResolveWebPlayerAccessTokenExpiry(long? expiresAtUnixMs)
    {
        if (expiresAtUnixMs.HasValue && expiresAtUnixMs.Value > 0)
        {
            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(expiresAtUnixMs.Value);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                return DateTimeOffset.UtcNow.AddMinutes(30.0);
            }
        }
        return DateTimeOffset.UtcNow.AddMinutes(30.0);
    }

    private static string GenerateDeviceId()
    {
        return Guid.NewGuid().ToString("N");
    }

    private static string GenerateStableDeviceId(string seed)
    {
        if (string.IsNullOrWhiteSpace(seed))
        {
            return GenerateDeviceId();
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return Convert.ToHexString(hash).ToLowerInvariant()[..32];
    }

    private async Task<JsonDocument?> QueryAsync(PathfinderAuthContext context, object payload, CancellationToken cancellationToken)
    {
        try
        {
            HttpClient client = _httpClientFactory.CreateClient();
            string payloadJson = JsonSerializer.Serialize(payload, _jsonOptions);
            string operationName = TryGetOperationName(payloadJson) ?? "(unknown)";
            var (status, json) = await SendPathfinderRequestAsync(client, context, payloadJson, cancellationToken);
            if (status == HttpStatusCode.Unauthorized)
            {
                PathfinderAuthContext? refreshed = await BuildAuthContextAsync(cancellationToken);
                if (refreshed is not null)
                {
                    (status, json) = await SendPathfinderRequestAsync(client, refreshed, payloadJson, cancellationToken);
                }
            }
            if (ShouldReturnNullFromQueryResponse(status, json, operationName))
            {
                return null;
            }
            string responseJson = json!;
            return JsonDocument.Parse(responseJson);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            _logger.LogWarning(ex, "Spotify Pathfinder query failed.");
            return null;
        }
    }

    private bool ShouldReturnNullFromQueryResponse(HttpStatusCode status, string? json, string operationName)
    {
        if (status == HttpStatusCode.OK && !string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        if (status != HttpStatusCode.OK)
        {
            string snippet;
            if (string.IsNullOrWhiteSpace(json))
            {
                snippet = "empty";
            }
            else
            {
                snippet = json.Length > 200
                    ? string.Concat(json.AsSpan(0, 200), "…")
                    : json;
            }

            _logger.LogWarning("Spotify Pathfinder query failed: operation={OperationName} status={Status} {Snippet}", operationName, status, snippet);
        }

        return true;
    }

    private static string? TryGetOperationName(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }
        try
        {
            using JsonDocument jsonDocument = JsonDocument.Parse(payloadJson);
            JsonElement value;
            return (jsonDocument.RootElement.TryGetProperty("operationName", out value) && value.ValueKind == JsonValueKind.String) ? value.GetString() : null;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            return null;
        }
    }

    public async Task<JsonDocument?> FetchHomeFeedAsync(string? timeZone, CancellationToken cancellationToken)
    {
        PathfinderAuthContext? context = await BuildAuthContextAsync(cancellationToken);
        if (context is null)
        {
            return null;
        }
        object payload = BuildHomeFeedPayload(timeZone, context);
        return await QueryAsync(context, payload, cancellationToken);
    }

    public async Task<List<SpotifyTrackSummary>> SearchTracksAsync(string query, int limit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new List<SpotifyTrackSummary>();
        }
        PathfinderAuthContext? context = await BuildAuthContextAsync(cancellationToken);
        if (context is null)
        {
            return new List<SpotifyTrackSummary>();
        }
        string trimmedQuery = query.Trim();
        int resolvedLimit = Math.Clamp((limit <= 0) ? 20 : limit, 1, 50);
        PersistedQueryOverride persisted = GetPersistedQuery(SearchSuggestionsOperationName, 1, SearchSuggestionsHashFallback);
        IReadOnlyList<Dictionary<string, object?>> candidates = BuildSearchSuggestionVariableCandidates(trimmedQuery, resolvedLimit, context, persisted.VariablesJson);
        foreach (var payload in candidates.Select((Dictionary<string, object?> variables) => new
        {
            operationName = SearchSuggestionsOperationName,
            variables = variables,
            extensions = new
            {
                persistedQuery = new
                {
                    version = persisted.Version,
                    sha256Hash = persisted.Sha256Hash
                }
            }
        }))
        {
            using JsonDocument? doc = await QueryAsync(context, payload, cancellationToken);
            if (doc is not null)
            {
                List<SpotifyTrackSummary> tracks = ParseSearchSuggestionTracks(doc.RootElement, resolvedLimit);
                if (tracks.Count > 0)
                {
                    return tracks;
                }
            }
        }
        return new List<SpotifyTrackSummary>();
    }

    public async Task<List<SpotifyArtistSearchCandidate>> SearchArtistsAsync(string query, int limit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new List<SpotifyArtistSearchCandidate>();
        }
        PathfinderAuthContext? context = await BuildAuthContextAsync(cancellationToken);
        if (context is null)
        {
            return new List<SpotifyArtistSearchCandidate>();
        }
        string trimmedQuery = query.Trim();
        int resolvedLimit = Math.Clamp((limit <= 0) ? 20 : limit, 1, 50);
        PersistedQueryOverride persisted = GetPersistedQuery(SearchSuggestionsOperationName, 1, SearchSuggestionsHashFallback);
        IReadOnlyList<Dictionary<string, object?>> variableCandidates = BuildSearchSuggestionVariableCandidates(trimmedQuery, resolvedLimit, context, persisted.VariablesJson);
        foreach (var payload in variableCandidates.Select((Dictionary<string, object?> variables) => new
        {
            operationName = SearchSuggestionsOperationName,
            variables = variables,
            extensions = new
            {
                persistedQuery = new
                {
                    version = persisted.Version,
                    sha256Hash = persisted.Sha256Hash
                }
            }
        }))
        {
            using JsonDocument? doc = await QueryAsync(context, payload, cancellationToken);
            if (doc is not null)
            {
                List<SpotifyArtistSearchCandidate> artists = ParseSearchSuggestionArtists(doc.RootElement, resolvedLimit);
                if (artists.Count > 0)
                {
                    return await EnrichSearchSuggestionArtistsAsync(context, artists, cancellationToken);
                }
            }
        }
        return new List<SpotifyArtistSearchCandidate>();
    }

    private async Task<List<SpotifyArtistSearchCandidate>> EnrichSearchSuggestionArtistsAsync(
        PathfinderAuthContext context,
        List<SpotifyArtistSearchCandidate> artists,
        CancellationToken cancellationToken)
    {
        if (artists.Count == 0)
        {
            return artists;
        }

        SpotifyArtistSearchCandidate[] enriched = artists.ToArray();
        object sync = new object();
        using SemaphoreSlim gate = new SemaphoreSlim(4, 4);
        List<Task> tasks = new List<Task>();
        for (int i = 0; i < enriched.Length; i++)
        {
            if (HasSearchArtistImage(enriched[i]))
            {
                continue;
            }

            SpotifyArtistSearchCandidate current = enriched[i];
            if (TryApplyCachedSearchArtistEnrichment(current, out SpotifyArtistSearchCandidate cached))
            {
                enriched[i] = cached;
                continue;
            }

            tasks.Add(EnrichSearchSuggestionArtistAsync(context, enriched, i, current.Id, sync, gate, cancellationToken));
        }

        await Task.WhenAll(tasks);
        return enriched.ToList();
    }

    private static bool HasSearchArtistImage(SpotifyArtistSearchCandidate candidate) => !string.IsNullOrWhiteSpace(candidate.ImageUrl);

    private static bool TryApplyCachedSearchArtistEnrichment(
        SpotifyArtistSearchCandidate candidate,
        out SpotifyArtistSearchCandidate enrichedCandidate)
    {
        enrichedCandidate = candidate;
        if (!TryGetArtistSearchEnrichment(candidate.Id, out var cached))
        {
            return false;
        }

        string? resolvedImage = string.IsNullOrWhiteSpace(candidate.ImageUrl) ? cached.ImageUrl : candidate.ImageUrl;
        if (string.IsNullOrWhiteSpace(resolvedImage))
        {
            return false;
        }

        string? resolvedName = string.IsNullOrWhiteSpace(candidate.Name) ? cached.Name : candidate.Name;
        enrichedCandidate = candidate with
        {
            Name = resolvedName ?? candidate.Name,
            ImageUrl = resolvedImage
        };
        return true;
    }

    private Task EnrichSearchSuggestionArtistAsync(
        PathfinderAuthContext context,
        SpotifyArtistSearchCandidate[] enriched,
        int index,
        string artistId,
        object sync,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                SpotifyArtistSearchCandidate item = enriched[index];
                (string? name, string? imageUrl) resolved = await ResolveArtistSearchEnrichmentAsync(context, item.Id, cancellationToken);
                RememberArtistSearchEnrichment(item.Id, resolved.name, resolved.imageUrl);
                if (!string.IsNullOrWhiteSpace(resolved.imageUrl))
                {
                    lock (sync)
                    {
                        enriched[index] = item with
                        {
                            Name = string.IsNullOrWhiteSpace(resolved.name) ? item.Name : resolved.name,
                            ImageUrl = resolved.imageUrl
                        };
                    }
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "Spotify search artist enrichment failed for {ArtistId}", artistId);
                }
            }
            finally
            {
                gate.Release();
            }
        }, cancellationToken);
    }

    private async Task<(string? name, string? imageUrl)> ResolveArtistSearchEnrichmentAsync(
        PathfinderAuthContext context,
        string artistId,
        CancellationToken cancellationToken)
    {
        JsonElement? overview = await QueryArtistOverviewAsync(context, artistId, cancellationToken);
        if (overview.HasValue)
        {
            string? overviewName = TryGetString(overview.Value, ProfileKey, "name") ?? TryGetString(overview.Value, "name");
            string? overviewImageUrl = ExtractArtistImageUrl(overview.Value);
            if (!string.IsNullOrWhiteSpace(overviewImageUrl))
            {
                return (NormalizeOptionalText(overviewName), overviewImageUrl);
            }
        }

        JsonElement? artist = await QueryArtistAsync(context, artistId, cancellationToken);
        if (artist.HasValue)
        {
            string? artistName = TryGetString(artist.Value, ProfileKey, "name") ?? TryGetString(artist.Value, "name");
            string? artistImageUrl = ExtractArtistImageUrl(artist.Value);
            return (NormalizeOptionalText(artistName), artistImageUrl);
        }

        return (null, null);
    }

    private static bool TryGetArtistSearchEnrichment(string artistId, out (string? Name, string? ImageUrl) enrichment)
    {
        enrichment = (null, null);
        if (!ArtistSearchEnrichmentCache.TryGetValue(artistId, out var cached))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - cached.Stamp > ArtistSearchEnrichmentCacheTtl)
        {
            ArtistSearchEnrichmentCache.TryRemove(artistId, out var _);
            return false;
        }

        enrichment = (cached.Name, cached.ImageUrl);
        return true;
    }

    private static void RememberArtistSearchEnrichment(string artistId, string? name, string? imageUrl)
    {
        ArtistSearchEnrichmentCache[artistId] = (DateTimeOffset.UtcNow, NormalizeOptionalText(name), NormalizeOptionalText(imageUrl));
    }

    private static string? NormalizeOptionalText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string normalized = value.Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    public async Task<List<SpotifyTrackSummary>> FetchLibraryLikedTracksAsync(int limit, int offset, CancellationToken cancellationToken)
    {
        PathfinderAuthContext? context = await BuildAuthContextAsync(cancellationToken);
        if (context is null)
        {
            return new List<SpotifyTrackSummary>();
        }
        int resolvedLimit = Math.Clamp((limit <= 0) ? 50 : limit, 1, 100);
        int resolvedOffset = Math.Max(0, offset);
        PersistedQueryOverride persisted = GetPersistedQuery("libraryV3", 1, "9f4da031f81274d572cfedaf6fc57a737c84b43d572952200b2c36aaa8fec1c6");
        Dictionary<string, object?> variables = ((!string.IsNullOrWhiteSpace(persisted.VariablesJson)) ? BuildLibraryV3VariablesFromOverride(persisted.VariablesJson, resolvedLimit, resolvedOffset, context) : BuildLibraryV3DefaultVariables(resolvedLimit, resolvedOffset, context));
        var payload = new
        {
            operationName = "libraryV3",
            variables = variables,
            extensions = new
            {
                persistedQuery = new
                {
                    version = persisted.Version,
                    sha256Hash = persisted.Sha256Hash
                }
            }
        };
        using JsonDocument? doc = await QueryAsync(context, payload, cancellationToken);
        if (doc is null)
        {
            return new List<SpotifyTrackSummary>();
        }
        List<SpotifyTrackSummary> tracks = ParseSearchSuggestionTracks(doc.RootElement, resolvedLimit);
        return (tracks.Count <= resolvedLimit) ? tracks : tracks.Take(resolvedLimit).ToList();
    }

    public async Task<JsonDocument?> FetchRecommendationsAsync(string contextUri, string contextType, int limit, CancellationToken cancellationToken)
    {
        return (await FetchRecommendationsPayloadAsync(contextUri, contextType, limit, cancellationToken))?.Document;
    }

    public async Task<RecommendationQueryResult?> FetchRecommendationsPayloadAsync(string contextUri, string contextType, int limit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(contextUri))
        {
            return null;
        }
        PathfinderAuthContext? context = await BuildAuthContextAsync(cancellationToken);
        if (context is null)
        {
            return null;
        }
        if (!TryResolveRecommendationOperation(contextType, out string operationName, out PersistedQueryOverride persistedQuery))
        {
            _logger.LogWarning("Spotify recommendations missing Pathfinder override. contextType=ContextType");
            return null;
        }
        int resolvedLimit = Math.Clamp((limit <= 0) ? 20 : limit, 1, 50);
        Dictionary<string, object?> variables = ((!string.IsNullOrWhiteSpace(persistedQuery.VariablesJson)) ? BuildRecommendationVariablesFromOverride(persistedQuery.VariablesJson, contextUri, contextType, resolvedLimit, context) : BuildRecommendationVariables(contextUri, contextType, resolvedLimit, context));
        var payload = new
        {
            operationName = operationName,
            variables = variables,
            extensions = new
            {
                persistedQuery = new
                {
                    version = persistedQuery.Version,
                    sha256Hash = persistedQuery.Sha256Hash
                }
            }
        };
        JsonDocument? doc = await QueryAsync(context, payload, cancellationToken);
        if (doc is null)
        {
            return null;
        }
        return new RecommendationQueryResult(VariablesJson: JsonSerializer.Serialize(variables, _jsonOptions), Document: doc, OperationName: operationName);
    }

    public async Task<JsonDocument?> FetchHomeFeedWithBlobAsync(string? timeZone, CancellationToken cancellationToken)
    {
        string? blobPath = await TryResolveActiveSpotifyBlobPathAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(blobPath))
        {
            _logger.LogWarning("Spotify home feed (blob) failed: missing blob path.");
            return null;
        }
        PathfinderAuthContext? context = await BuildBlobAuthContextAsync(blobPath, cancellationToken);
        if (context is null)
        {
            _logger.LogWarning("Spotify home feed (blob) failed: blob auth unavailable.");
            return null;
        }
        object payload = BuildHomeFeedPayload(timeZone, context);
        return await QueryAsync(context, payload, cancellationToken);
    }

    public async Task<JsonDocument?> FetchHomeFeedLegacyWithBlobAsync(string? timeZone, CancellationToken cancellationToken)
    {
        string? blobPath = await TryResolveActiveSpotifyBlobPathAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(blobPath))
        {
            _logger.LogWarning("Spotify home feed (legacy) failed: missing blob path.");
            return null;
        }
        PathfinderAuthContext? context = await BuildBlobAuthContextAsync(blobPath, cancellationToken);
        if (context is null)
        {
            _logger.LogWarning("Spotify home feed (legacy) failed: blob auth unavailable.");
            return null;
        }
        object payload = BuildHomeFeedLegacyPayload(timeZone);
        return await QueryAsync(context, payload, cancellationToken);
    }

    public async Task<bool> ValidateBlobAsync(string blobPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(blobPath))
        {
            return false;
        }
        PathfinderAuthContext? context = await BuildBlobAuthContextAsync(blobPath, cancellationToken);
        if (context is null)
        {
            return false;
        }
        return (await QueryTrackUnionAsync(context, "11dFghVXANMlKmJXsNCbNl", cancellationToken)).HasValue;
    }

    private static object BuildHomeFeedPayload(string? timeZone, PathfinderAuthContext context)
    {
        string value = (string.IsNullOrWhiteSpace(timeZone) ? "America/New_York" : timeZone.Trim());
        Dictionary<string, object?> dictionary = new Dictionary<string, object?>
        {
            ["timeZone"] = value,
            ["homeEndUserIntegration"] = IntegrationWebPlayer,
            ["facet"] = string.Empty,
            ["sectionItemsLimit"] = 10
        };
        if (!string.IsNullOrWhiteSpace(context.DeviceId))
        {
            dictionary["sp_t"] = context.DeviceId;
        }
        return new
        {
            operationName = "home",
            variables = dictionary,
            extensions = new
            {
                persistedQuery = new
                {
                    version = GetPersistedQuery("home", 1, "7fa05a3b71ee950cd63f5b738a0285f7c58b20a93e735ada5ad9a8d5e116d791").Version,
                    sha256Hash = GetPersistedQuery("home", 1, "7fa05a3b71ee950cd63f5b738a0285f7c58b20a93e735ada5ad9a8d5e116d791").Sha256Hash
                }
            }
        };
    }

    private static object BuildHomeFeedLegacyPayload(string? timeZone)
    {
        string timeZone2 = (string.IsNullOrWhiteSpace(timeZone) ? "America/New_York" : timeZone.Trim());
        return new
        {
            operationName = "home",
            variables = new
            {
                timeZone = timeZone2
            },
            extensions = new
            {
                persistedQuery = new
                {
                    version = GetPersistedQuery("home_legacy", 1, "3a67ee0ea6abad2ebad2e588a9aa130fc98d6b553f5b05ac6467503d02133bdc").Version,
                    sha256Hash = GetPersistedQuery("home_legacy", 1, "3a67ee0ea6abad2ebad2e588a9aa130fc98d6b553f5b05ac6467503d02133bdc").Sha256Hash
                }
            }
        };
    }

    private static Dictionary<string, object?> BuildRecommendationVariables(string contextUri, string contextType, int limit, PathfinderAuthContext context)
    {
        Dictionary<string, object?> dictionary = new Dictionary<string, object?>
        {
            ["uri"] = contextUri,
            ["contextUri"] = contextUri,
            ["limit"] = limit,
            [OffsetKey] = 0,
            ["pageLimit"] = limit,
            ["pageOffset"] = 0,
            ["sectionItemsLimit"] = limit,
            ["itemsLimit"] = limit,
            ["first"] = limit,
            ["after"] = null,
            ["locale"] = string.Empty,
            ["market"] = "from_token",
            ["enableWatchFeedEntrypoint"] = false
        };
        if (string.Equals(contextType, PlaylistType, StringComparison.OrdinalIgnoreCase))
        {
            dictionary["playlistUri"] = contextUri;
        }
        else if (string.Equals(contextType, AlbumType, StringComparison.OrdinalIgnoreCase))
        {
            dictionary["albumUri"] = contextUri;
        }
        if (!string.IsNullOrWhiteSpace(context.DeviceId))
        {
            dictionary["sp_t"] = context.DeviceId;
        }
        return dictionary;
    }

    private static Dictionary<string, object?> BuildRecommendationVariablesFromOverride(string variablesJson, string contextUri, string contextType, int limit, PathfinderAuthContext context)
    {
        Dictionary<string, object?> dictionary = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (JsonNode.Parse(variablesJson) is JsonObject jsonObject)
            {
                foreach (KeyValuePair<string, JsonNode?> item in jsonObject)
                {
                    dictionary[item.Key] = item.Value;
                }
            }
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            return BuildRecommendationVariables(contextUri, contextType, limit, context);
        }
        if (dictionary.Count == 0)
        {
            return BuildRecommendationVariables(contextUri, contextType, limit, context);
        }
        string? contextType2 = contextType?.Trim().ToLowerInvariant();
        foreach (string item2 in dictionary.Keys.ToList())
        {
            bool isContextUri = IsContextUriKey(item2, contextType2);
            bool isSpotifyUriValue = dictionary[item2] is JsonValue jsonValue
                && jsonValue.TryGetValue<string>(out string? value3)
                && LooksLikeSpotifyUri(value3);
            if (isContextUri || isSpotifyUriValue)
            {
                dictionary[item2] = contextUri;
            }
            else if (IsLimitKey(item2))
            {
                dictionary[item2] = limit;
            }
            else if (IsOffsetKey(item2))
            {
                dictionary[item2] = 0;
            }
        }
        if (!string.IsNullOrWhiteSpace(context.DeviceId) && !dictionary.ContainsKey("sp_t"))
        {
            dictionary["sp_t"] = context.DeviceId;
        }
        return dictionary;
    }

    private static bool IsContextUriKey(string key, string? contextType)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }
        string text = key.Replace("_", string.Empty).ToLowerInvariant();
        bool flag;
        switch (text)
        {
            case "uri":
            case "contexturi":
            case "playlisturi":
            case "albumuri":
                flag = true;
                break;
            default:
                flag = false;
                break;
        }
        if (flag)
        {
            return true;
        }
        if (string.IsNullOrWhiteSpace(contextType))
        {
            return false;
        }
        return text.Contains(contextType, StringComparison.OrdinalIgnoreCase) && text.Contains("uri", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLimitKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }
        string text = key.Replace("_", string.Empty).ToLowerInvariant();
        bool flag = text.Contains("limit", StringComparison.OrdinalIgnoreCase);
        bool flag2 = flag;
        if (!flag2)
        {
            bool flag3;
            switch (text)
            {
                case "first":
                case CountKey:
                case "pagecount":
                    flag3 = true;
                    break;
                default:
                    flag3 = false;
                    break;
            }
            flag2 = flag3;
        }
        return flag2;
    }

    private static bool IsOffsetKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }
        string text = key.Replace("_", string.Empty).ToLowerInvariant();
        bool flag = text.Contains(OffsetKey, StringComparison.OrdinalIgnoreCase);
        bool flag2 = flag;
        if (!flag2)
        {
            bool flag3 = text == "start" || text == "pageoffset";
            flag2 = flag3;
        }
        return flag2;
    }

    private static bool IsSearchTopResultsKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }
        string text = key.Replace("_", string.Empty).ToLowerInvariant();
        return text == "numberoftopresults" || text == "topresults";
    }

    private static List<Dictionary<string, object?>> BuildSearchSuggestionVariableCandidates(string query, int limit, PathfinderAuthContext context, string? overrideVariablesJson)
    {
        List<Dictionary<string, object?>> candidates = new List<Dictionary<string, object?>>();
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(overrideVariablesJson))
        {
            AddCandidate(BuildSearchSuggestionVariablesFromOverride(overrideVariablesJson, query, limit, context));
        }
        AddCandidate(BuildSearchSuggestionDefaultVariables("query", query, limit, context));
        AddCandidate(BuildSearchSuggestionDefaultVariables("searchTerm", query, limit, context));
        AddCandidate(BuildSearchSuggestionDefaultVariables("q", query, limit, context));
        return candidates;
        void AddCandidate(Dictionary<string, object?> candidate)
        {
            string item = JsonSerializer.Serialize(candidate);
            if (seen.Add(item))
            {
                candidates.Add(candidate);
            }
        }
    }

    private static Dictionary<string, object?> BuildSearchSuggestionVariablesFromOverride(string variablesJson, string query, int limit, PathfinderAuthContext context)
    {
        Dictionary<string, object?> dictionary = ParseSearchSuggestionOverrideVariables(variablesJson);
        if (dictionary.Count == 0)
        {
            return BuildSearchSuggestionDefaultVariables("query", query, limit, context);
        }
        bool replacedQuery = false;
        foreach (string key in dictionary.Keys.ToList())
        {
            replacedQuery |= ApplySearchSuggestionOverrideValue(dictionary, key, query, limit);
        }
        EnsureSearchSuggestionOverrideDefaults(dictionary, replacedQuery, query, limit);
        if (!dictionary.ContainsKey("includeAudiobooks"))
        {
            dictionary["includeAudiobooks"] = false;
        }
        if (!string.IsNullOrWhiteSpace(context.DeviceId) && !dictionary.ContainsKey("sp_t"))
        {
            dictionary["sp_t"] = context.DeviceId;
        }
        return dictionary;
    }

    private static Dictionary<string, object?> ParseSearchSuggestionOverrideVariables(string variablesJson)
    {
        Dictionary<string, object?> variables = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (JsonNode.Parse(variablesJson) is JsonObject jsonObject)
            {
                foreach (KeyValuePair<string, JsonNode?> item in jsonObject)
                {
                    variables[item.Key] = item.Value;
                }
            }
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        }
        return variables;
    }

    private static bool ApplySearchSuggestionOverrideValue(Dictionary<string, object?> variables, string key, string query, int limit)
    {
        string normalizedKey = key.Replace("_", string.Empty).ToLowerInvariant();
        if (normalizedKey == "q" || normalizedKey.Contains("searchterm", StringComparison.OrdinalIgnoreCase) || normalizedKey.Contains("searchquery", StringComparison.OrdinalIgnoreCase) || normalizedKey.Equals("query", StringComparison.OrdinalIgnoreCase) || normalizedKey.Equals("term", StringComparison.OrdinalIgnoreCase))
        {
            variables[key] = query;
            return true;
        }
        if (IsSearchTopResultsKey(key) || IsLimitKey(key))
        {
            variables[key] = limit;
        }
        else if (IsOffsetKey(key))
        {
            variables[key] = 0;
        }
        return false;
    }

    private static void EnsureSearchSuggestionOverrideDefaults(Dictionary<string, object?> variables, bool replacedQuery, string query, int limit)
    {
        if (!replacedQuery)
        {
            variables["query"] = query;
        }
        if (!variables.ContainsKey("limit"))
        {
            variables["limit"] = limit;
        }
        if (!variables.ContainsKey(OffsetKey))
        {
            variables[OffsetKey] = 0;
        }
        if (!variables.ContainsKey("numberOfTopResults"))
        {
            variables["numberOfTopResults"] = limit;
        }
    }

    private static Dictionary<string, object?> BuildSearchSuggestionDefaultVariables(string queryKey, string query, int limit, PathfinderAuthContext context)
    {
        Dictionary<string, object?> dictionary = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [queryKey] = query,
            ["limit"] = limit,
            [OffsetKey] = 0,
            ["numberOfTopResults"] = limit,
            ["includeAudiobooks"] = false
        };
        if (!string.IsNullOrWhiteSpace(context.DeviceId))
        {
            dictionary["sp_t"] = context.DeviceId;
        }
        return dictionary;
    }

    private static Dictionary<string, object?> BuildLibraryV3VariablesFromOverride(string variablesJson, int limit, int offset, PathfinderAuthContext context)
    {
        Dictionary<string, object?> dictionary = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (JsonNode.Parse(variablesJson) is JsonObject jsonObject)
            {
                foreach (KeyValuePair<string, JsonNode?> item in jsonObject)
                {
                    dictionary[item.Key] = item.Value;
                }
            }
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            return BuildLibraryV3DefaultVariables(limit, offset, context);
        }
        foreach (string item2 in dictionary.Keys.ToList())
        {
            if (IsLimitKey(item2))
            {
                dictionary[item2] = limit;
            }
            else if (IsOffsetKey(item2))
            {
                dictionary[item2] = offset;
            }
            else if (item2.Equals("textFilter", StringComparison.OrdinalIgnoreCase))
            {
                dictionary[item2] = string.Empty;
            }
            else if (item2.Equals("sp_t", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(context.DeviceId))
            {
                dictionary[item2] = context.DeviceId;
            }
        }
        EnsureLibraryV3VariableDefaults(dictionary, limit, offset, context);
        return dictionary;
    }

    private static Dictionary<string, object?> BuildLibraryV3DefaultVariables(int limit, int offset, PathfinderAuthContext context)
    {
        Dictionary<string, object?> dictionary = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        EnsureLibraryV3VariableDefaults(dictionary, limit, offset, context);
        return dictionary;
    }

    private static void EnsureLibraryV3VariableDefaults(Dictionary<string, object?> variables, int limit, int offset, PathfinderAuthContext context)
    {
        variables["expandedFolders"] = Array.Empty<string>();
        variables["features"] = new string[4] { "LIKED_SONGS", "YOUR_EPISODES_V2", "PRERELEASES", "EVENTS" };
        variables["flatten"] = false;
        variables["folderUri"] = null;
        variables["includeFoldersWhenFlattening"] = true;
        variables["limit"] = limit;
        variables[OffsetKey] = offset;
        variables["order"] = null;
        variables["textFilter"] = string.Empty;
        if (!string.IsNullOrWhiteSpace(context.DeviceId))
        {
            variables["sp_t"] = context.DeviceId;
        }
    }

    private static List<SpotifyTrackSummary> ParseBrowseSectionTracks(JsonElement root, int limit)
    {
        int num = Math.Max(limit, 1);
        List<string> list = new List<string>();
        Dictionary<string, SpotifyTrackSummary> dictionary = new Dictionary<string, SpotifyTrackSummary>(StringComparer.OrdinalIgnoreCase);
        CollectBrowseSectionTracks(root, dictionary, list);
        List<SpotifyTrackSummary> list2 = new List<SpotifyTrackSummary>(Math.Min(num, list.Count));
        foreach (string item in list)
        {
            if (dictionary.TryGetValue(item, out var value))
            {
                list2.Add(value);
                if (list2.Count >= num)
                {
                    break;
                }
            }
        }
        return list2;
    }

    private static void CollectBrowseSectionTracks(JsonElement element, Dictionary<string, SpotifyTrackSummary> tracksById, List<string> orderedIds)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            CollectBrowseSectionTrackItems(element, tracksById, orderedIds);
            foreach (JsonProperty item in element.EnumerateObject())
            {
                CollectBrowseSectionTracks(item.Value, tracksById, orderedIds);
            }
            return;
        }
        if (element.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        foreach (JsonElement item3 in element.EnumerateArray())
        {
            CollectBrowseSectionTracks(item3, tracksById, orderedIds);
        }
    }

    private static void CollectBrowseSectionTrackItems(JsonElement element, IDictionary<string, SpotifyTrackSummary> tracksById, ICollection<string> orderedIds)
    {
        if (!TryGetNested(element, out var items, "sectionItems", ItemsKey) || items.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement item in items.EnumerateArray())
        {
            if (TryParseBrowseSectionTrackItem(item, out SpotifyTrackSummary? summary) && summary is not null)
            {
                AddOrReplaceTrackSummary(summary, tracksById, orderedIds);
            }
        }
    }

    private static void AddOrReplaceTrackSummary(SpotifyTrackSummary summary, IDictionary<string, SpotifyTrackSummary> tracksById, ICollection<string> orderedIds)
    {
        if (tracksById.TryGetValue(summary.Id, out SpotifyTrackSummary? current) && current is not null)
        {
            if (ShouldPreferTrackSummary(current, summary))
            {
                tracksById[summary.Id] = summary;
            }
            return;
        }

        tracksById[summary.Id] = summary;
        orderedIds.Add(summary.Id);
    }

    private static bool TryParseBrowseSectionTrackItem(JsonElement item, out SpotifyTrackSummary? summary)
    {
        summary = null;
        if (item.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        JsonElement trackData;
        SpotifyTrackSummary? spotifyTrackSummary = TryParsePlaylistItemTrack(item, out trackData);
        if (spotifyTrackSummary is null && TryGetNested(item, out var value, ContentKey, DataKey))
        {
            trackData = value;
            spotifyTrackSummary = ParseTrackSummary(trackData);
        }
        if (spotifyTrackSummary is null)
        {
            return false;
        }
        string? text = ResolveBrowseSectionTrackId(item, trackData, spotifyTrackSummary);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        string? text2 = ResolveBrowseSectionTrackName(item, trackData, spotifyTrackSummary);
        string? text3 = ResolveBrowseSectionTrackArtists(item, trackData, spotifyTrackSummary);
        if (string.IsNullOrWhiteSpace(text2) || string.IsNullOrWhiteSpace(text3))
        {
            return false;
        }
        string? album = ResolveBrowseSectionTrackAlbum(trackData, spotifyTrackSummary);
        int? durationMs = ResolveBrowseSectionTrackDuration(trackData, spotifyTrackSummary);
        string? text4 = ResolveBrowseSectionTrackImage(item, trackData, spotifyTrackSummary);
        string? albumId = ResolveBrowseSectionTrackAlbumId(trackData, spotifyTrackSummary);
        string? albumArtist = ResolveBrowseSectionTrackAlbumArtist(trackData, spotifyTrackSummary);
        IReadOnlyList<string>? artistIds2 = ResolveBrowseSectionTrackArtistIds(trackData, spotifyTrackSummary);
        string sourceUrl = ResolveBrowseSectionTrackSourceUrl(text, spotifyTrackSummary);
        summary = new SpotifyTrackSummary(text, text2, text3, album, durationMs, sourceUrl, text4, spotifyTrackSummary.Isrc ?? ExtractIsrc(trackData), spotifyTrackSummary.ReleaseDate, spotifyTrackSummary.TrackNumber, spotifyTrackSummary.DiscNumber, spotifyTrackSummary.TrackTotal, spotifyTrackSummary.Explicit)
        {
            AlbumId = albumId,
            AlbumArtist = albumArtist,
            ArtistIds = artistIds2,
            Label = spotifyTrackSummary.Label,
            Genres = spotifyTrackSummary.Genres,
            AlbumGroup = spotifyTrackSummary.AlbumGroup,
            ReleaseType = spotifyTrackSummary.ReleaseType
        };
        return true;
    }

    private static string? ResolveBrowseSectionTrackId(JsonElement item, JsonElement trackData, SpotifyTrackSummary summary)
    {
        return !string.IsNullOrWhiteSpace(summary.Id)
            ? summary.Id
            : ExtractIdFromUri(TryGetString(item, "uri"))
                ?? ExtractIdFromUri(TryGetString(trackData, "uri"))
                ?? TryGetString(trackData, "id");
    }

    private static string? ResolveBrowseSectionTrackName(JsonElement item, JsonElement trackData, SpotifyTrackSummary summary)
    {
        return SelectPreferredTrackText(summary.Name, TryGetString(trackData, "name"), TryGetString(item, "name"), TryGetString(trackData, ProfileKey, "name"));
    }

    private static string? ResolveBrowseSectionTrackArtists(JsonElement item, JsonElement trackData, SpotifyTrackSummary summary)
    {
        string? artists = SelectPreferredTrackText(summary.Artists, ExtractArtistsFromItems(trackData, ArtistsKey), ExtractArtistsFromItems(trackData, "firstArtist"), TryGetString(trackData, ArtistNameKey));
        if (!string.IsNullOrWhiteSpace(artists))
        {
            return artists;
        }

        return TryGetNested(item, out var contentData, ContentKey, DataKey)
            ? SelectPreferredTrackText(artists, ExtractArtistsFromItems(contentData, ArtistsKey), ExtractArtistsFromItems(contentData, "firstArtist"), TryGetString(contentData, ArtistNameKey))
            : artists;
    }

    private static string? ResolveBrowseSectionTrackAlbum(JsonElement trackData, SpotifyTrackSummary summary)
    {
        return SelectPreferredTrackText(summary.Album, TryGetString(trackData, AlbumOfTrackKey, "name"), TryGetString(trackData, AlbumType, "name"));
    }

    private static int? ResolveBrowseSectionTrackDuration(JsonElement trackData, SpotifyTrackSummary summary)
    {
        return summary.DurationMs
            ?? TryGetInt(trackData, "trackDuration", TotalMillisecondsKey)
            ?? TryGetInt(trackData, DurationKey, TotalMillisecondsKey)
            ?? TryGetInt(trackData, DurationKey, "milliseconds");
    }

    private static string? ResolveBrowseSectionTrackImage(JsonElement item, JsonElement trackData, SpotifyTrackSummary summary)
    {
        string? imageUrl = summary.ImageUrl
        ?? ExtractCoverUrl(trackData, AlbumOfTrackKey, CoverArtKey)
        ?? ExtractCoverUrl(trackData, AlbumType, CoverArtKey)
        ?? ExtractCoverUrl(trackData, CoverArtKey);

        if (!string.IsNullOrWhiteSpace(imageUrl))
        {
            return imageUrl;
        }

        return TryGetNested(item, out var contentData, ContentKey, DataKey)
            ? ExtractCoverUrl(contentData, AlbumOfTrackKey, CoverArtKey)
                ?? ExtractCoverUrl(contentData, AlbumType, CoverArtKey)
                ?? ExtractCoverUrl(contentData, CoverArtKey)
            : null;
    }

    private static string? ResolveBrowseSectionTrackAlbumId(JsonElement trackData, SpotifyTrackSummary summary)
    {
        return summary.AlbumId
            ?? TryGetString(trackData, AlbumOfTrackKey, "id")
            ?? ExtractIdFromUri(TryGetString(trackData, AlbumOfTrackKey, "uri"))
            ?? TryGetString(trackData, AlbumType, "id")
            ?? ExtractIdFromUri(TryGetString(trackData, AlbumType, "uri"));
    }

    private static string? ResolveBrowseSectionTrackAlbumArtist(JsonElement trackData, SpotifyTrackSummary summary)
    {
        return SelectPreferredTrackText(
            summary.AlbumArtist,
            TryGetNested(trackData, out var albumOfTrack, AlbumOfTrackKey) ? ExtractArtistsFromItems(albumOfTrack, ArtistsKey) : null,
            TryGetNested(trackData, out var album, AlbumType) ? ExtractArtistsFromItems(album, ArtistsKey) : null);
    }

    private static IReadOnlyList<string>? ResolveBrowseSectionTrackArtistIds(JsonElement trackData, SpotifyTrackSummary summary)
    {
        IReadOnlyList<string>? artistIds = summary.ArtistIds;
        return artistIds != null && artistIds.Count > 0 ? artistIds : ExtractTrackArtistIds(trackData);
    }

    private static string ResolveBrowseSectionTrackSourceUrl(string trackId, SpotifyTrackSummary summary)
    {
        return !string.IsNullOrWhiteSpace(summary.SourceUrl)
            ? summary.SourceUrl
            : BuildSpotifyUrl(TrackType, trackId);
    }

    private static List<string>? ExtractTrackArtistIds(JsonElement trackData)
    {
        if (!TryGetNested(trackData, out var value, ArtistsKey) && !TryGetNested(trackData, out value, "artistsV2"))
        {
            return null;
        }
        if (!TryResolveArtistItems(value, out var items))
        {
            return null;
        }
        List<string> list = (from artist in items.EnumerateArray()
                             select TryGetString(artist, "id") ?? ExtractIdFromUri(TryGetString(artist, "uri")) into id
                             where !string.IsNullOrWhiteSpace(id)
                             select (id)).ToList();
        return (list.Count == 0) ? null : list;
    }

    private static string? SelectPreferredTrackText(params string?[] candidates)
    {
        return candidates.Select(NormalizeTrackMetadataValue).FirstOrDefault((string? normalized) => !string.IsNullOrWhiteSpace(normalized));
    }

    private static string? NormalizeTrackMetadataValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        string text = value.Trim();
        if (text.Length == 0)
        {
            return null;
        }
        if (string.Equals(text, SpotifyTrackFallbackTitle, StringComparison.OrdinalIgnoreCase) || string.Equals(text, UnknownText, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return text;
    }

    private static List<SpotifyTrackSummary> ParseSearchSuggestionTracks(JsonElement root, int limit)
    {
        int num = Math.Max(limit, 1);
        List<string> list = new List<string>();
        Dictionary<string, SpotifyTrackSummary> dictionary = new Dictionary<string, SpotifyTrackSummary>(StringComparer.OrdinalIgnoreCase);
        CollectSearchSuggestionTracks(root, dictionary, list);
        List<SpotifyTrackSummary> list2 = new List<SpotifyTrackSummary>(Math.Min(num, list.Count));
        foreach (string item in list)
        {
            if (dictionary.TryGetValue(item, out var value))
            {
                list2.Add(value);
                if (list2.Count >= num)
                {
                    break;
                }
            }
        }
        return list2;
    }

    private static void CollectSearchSuggestionTracks(JsonElement element, Dictionary<string, SpotifyTrackSummary> tracksById, List<string> orderedIds)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            TryUpsertSearchSuggestionTrack(element, tracksById, orderedIds);
            EnumerateObjectChildren(element, child => CollectSearchSuggestionTracks(child, tracksById, orderedIds));
            return;
        }
        if (element.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        foreach (JsonElement item2 in element.EnumerateArray())
        {
            CollectSearchSuggestionTracks(item2, tracksById, orderedIds);
        }
    }

    private static void TryUpsertSearchSuggestionTrack(JsonElement element, Dictionary<string, SpotifyTrackSummary> tracksById, List<string> orderedIds)
    {
        if (!TryParseSearchSuggestionTrack(element, out SpotifyTrackSummary? summary) || summary is null)
        {
            return;
        }
        if (tracksById.TryGetValue(summary.Id, out SpotifyTrackSummary? value) && value is not null)
        {
            if (ShouldPreferTrackSummary(value, summary))
            {
                tracksById[summary.Id] = summary;
            }
            return;
        }
        tracksById[summary.Id] = summary;
        orderedIds.Add(summary.Id);
    }

    private static bool ShouldPreferTrackSummary(SpotifyTrackSummary current, SpotifyTrackSummary candidate)
    {
        return GetTrackSummaryScore(candidate) > GetTrackSummaryScore(current);
    }

    private static int GetTrackSummaryScore(SpotifyTrackSummary summary)
    {
        int num = 0;
        if (!string.IsNullOrWhiteSpace(summary.Name) && !string.Equals(summary.Name, SpotifyTrackFallbackTitle, StringComparison.OrdinalIgnoreCase))
        {
            num += 5;
        }
        if (!string.IsNullOrWhiteSpace(summary.Artists) && !string.Equals(summary.Artists, UnknownText, StringComparison.OrdinalIgnoreCase))
        {
            num += 4;
        }
        if (!string.IsNullOrWhiteSpace(summary.Album) && !string.Equals(summary.Album, UnknownText, StringComparison.OrdinalIgnoreCase))
        {
            num += 3;
        }
        if (!string.IsNullOrWhiteSpace(summary.ImageUrl))
        {
            num += 2;
        }
        if (summary.DurationMs.HasValue && summary.DurationMs.Value > 0)
        {
            num += 2;
        }
        if (!string.IsNullOrWhiteSpace(summary.Isrc))
        {
            num++;
        }
        if (!string.IsNullOrWhiteSpace(summary.AlbumId))
        {
            num++;
        }
        IReadOnlyList<string>? artistIds = summary.ArtistIds;
        if (artistIds != null && artistIds.Count > 0)
        {
            num++;
        }
        return num;
    }

    private static bool TryParseSearchSuggestionTrack(JsonElement element, out SpotifyTrackSummary? summary)
    {
        summary = null;
        foreach (JsonElement candidate in EnumerateSearchSuggestionTrackCandidates(element))
        {
            SpotifyTrackSummary? parsed = ParseTrackSummary(candidate);
            if (parsed is not null && IsTrackSummaryUsable(parsed))
            {
                summary = parsed;
                return true;
            }
        }
        return false;
    }

    private static IEnumerable<JsonElement> EnumerateSearchSuggestionTrackCandidates(JsonElement element)
    {
        if (TryGetNested(element, out var value, TrackUnionKey))
        {
            yield return value;
        }
        if (TryGetNested(element, out value, TrackType))
        {
            yield return value;
        }
        if (TryGetNested(element, out value, DataKey, TrackUnionKey))
        {
            yield return value;
        }
        if (TryGetNested(element, out value, DataKey, TrackType))
        {
            yield return value;
        }
        if (TryGetNested(element, out value, "item", TrackType))
        {
            yield return value;
        }
        if (TryGetNested(element, out value, ItemV2Key, DataKey))
        {
            yield return value;
        }
        if (IsLikelyTrackElement(element))
        {
            yield return element;
        }
    }

    private static bool IsTrackSummaryUsable(SpotifyTrackSummary summary)
    {
        if (!string.IsNullOrWhiteSpace(summary.Name) && !string.Equals(summary.Name, SpotifyTrackFallbackTitle, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (!string.IsNullOrWhiteSpace(summary.Artists) && !string.Equals(summary.Artists, UnknownText, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (!string.IsNullOrWhiteSpace(summary.Album) && !string.Equals(summary.Album, UnknownText, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return !string.IsNullOrWhiteSpace(summary.ImageUrl);
    }

    private static bool IsLikelyTrackElement(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        string? text = TryGetString(element, "uri");
        if (!string.IsNullOrWhiteSpace(text) && text.StartsWith(SpotifyTrackUriPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        string? a = TryGetString(element, "__typename");
        if (string.Equals(a, "Track", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "TrackResponseWrapper", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        JsonElement value;
        return element.TryGetProperty(AlbumOfTrackKey, out value) && element.TryGetProperty(DurationKey, out value);
    }

    private static List<SpotifyArtistSearchCandidate> ParseSearchSuggestionArtists(JsonElement root, int limit)
    {
        List<SpotifyArtistSearchCandidate> list = new List<SpotifyArtistSearchCandidate>();
        Dictionary<string, int> indexById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        CollectSearchSuggestionArtists(root, list, indexById, Math.Max(limit, 1));
        return list;
    }

    private static void CollectSearchSuggestionArtists(JsonElement element, List<SpotifyArtistSearchCandidate> artists, Dictionary<string, int> indexById, int limit)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            TryUpsertSearchSuggestionArtist(element, artists, indexById, limit);
            EnumerateObjectChildren(element, child => CollectSearchSuggestionArtists(child, artists, indexById, limit));
            return;
        }
        if (element.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        foreach (JsonElement item2 in element.EnumerateArray())
        {
            CollectSearchSuggestionArtists(item2, artists, indexById, limit);
        }
    }

    private static void TryUpsertSearchSuggestionArtist(JsonElement element, List<SpotifyArtistSearchCandidate> artists, Dictionary<string, int> indexById, int limit)
    {
        if (!TryParseSearchSuggestionArtist(element, out SpotifyArtistSearchCandidate? candidate) || candidate is null)
        {
            return;
        }
        if (indexById.TryGetValue(candidate.Id, out var value))
        {
            SpotifyArtistSearchCandidate current = artists[value];
            if (ShouldPreferArtistCandidate(current, candidate))
            {
                artists[value] = candidate;
            }
            return;
        }
        if (artists.Count < limit)
        {
            indexById[candidate.Id] = artists.Count;
            artists.Add(candidate);
        }
    }

    private static bool ShouldPreferArtistCandidate(SpotifyArtistSearchCandidate current, SpotifyArtistSearchCandidate next)
    {
        if (string.IsNullOrWhiteSpace(current.ImageUrl) && !string.IsNullOrWhiteSpace(next.ImageUrl))
        {
            return true;
        }
        if (string.IsNullOrWhiteSpace(current.Name) && !string.IsNullOrWhiteSpace(next.Name))
        {
            return true;
        }
        return false;
    }

    private static bool TryParseSearchSuggestionArtist(JsonElement element, out SpotifyArtistSearchCandidate? candidate)
    {
        SpotifyArtistSearchCandidate? bestCandidate = null;
        bool hasCandidate = false;
        if (TryGetNested(element, out var value, ArtistUnionKey))
        {
            MergeCandidate(value);
        }
        if (TryGetNested(element, out var value2, ArtistType))
        {
            MergeCandidate(value2);
        }
        if (TryGetNested(element, out var value3, DataKey, ArtistUnionKey))
        {
            MergeCandidate(value3);
        }
        if (TryGetNested(element, out var value4, DataKey, ArtistType))
        {
            MergeCandidate(value4);
        }
        if (TryGetNested(element, out var value5, ItemV2Key, DataKey))
        {
            MergeCandidate(value5);
        }
        MergeCandidate(element);
        candidate = bestCandidate;
        return hasCandidate;
        void MergeCandidate(JsonElement source)
        {
            if (TryParseArtistCandidateElement(source, out SpotifyArtistSearchCandidate? candidate2) && candidate2 is not null && (!hasCandidate || (bestCandidate is not null && ShouldPreferArtistCandidate(bestCandidate, candidate2))))
            {
                bestCandidate = candidate2;
                hasCandidate = true;
            }
        }
    }

    private static bool TryParseArtistCandidateElement(JsonElement element, out SpotifyArtistSearchCandidate? candidate)
    {
        candidate = null;
        if (!IsLikelyArtistElement(element))
        {
            return false;
        }
        string? text = TryGetString(element, "id") ?? ExtractIdFromUri(TryGetString(element, "uri"));
        if (!LooksLikeSpotifyEntityId(text))
        {
            return false;
        }
        string id = text!;
        string? text2 = TryGetString(element, ProfileKey, "name") ?? TryGetString(element, "name");
        if (string.IsNullOrWhiteSpace(text2))
        {
            return false;
        }
        string? imageUrl = ExtractArtistImageUrl(element);
        candidate = new SpotifyArtistSearchCandidate(id, text2.Trim(), imageUrl);
        return true;
    }

    private static bool LooksLikeSpotifyEntityId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 22)
        {
            return false;
        }
        return value.All(char.IsLetterOrDigit);
    }

    private static bool IsLikelyArtistElement(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        string? text = TryGetString(element, "uri");
        if (!string.IsNullOrWhiteSpace(text) && text.StartsWith(SpotifyArtistUriPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        string? a = TryGetString(element, "__typename");
        if (string.Equals(a, "Artist", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "ArtistResponseWrapper", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        string? text2 = TryGetString(element, ProfileKey, "name");
        return text2 != null && text2.Length > 0;
    }

    private static bool LooksLikeSpotifyUri(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        return value.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase) || value.Contains("open.spotify.com/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolveRecommendationOperation(string contextType, out string operationName, out PersistedQueryOverride persistedQuery)
    {
        EnsurePathfinderOverridesLoaded();
        operationName = string.Empty;
        persistedQuery = new PersistedQueryOverride(0, string.Empty, null);
        string? normalizedContext = contextType?.Trim().ToLowerInvariant();
        if (string.Equals(normalizedContext, PlaylistType, StringComparison.OrdinalIgnoreCase))
        {
            PersistedQueryOverride persistedQuery2 = GetPersistedQuery(MoreLikeThisPlaylistOperationName, 1, MoreLikeThisPlaylistDefaultHash);
            if (persistedQuery2.Version > 0 && !string.IsNullOrWhiteSpace(persistedQuery2.Sha256Hash))
            {
                operationName = MoreLikeThisPlaylistOperationName;
                persistedQuery = persistedQuery2;
                return true;
            }
        }
        var list = (from name2 in PathfinderOverrides.Keys.Where((string a) => !string.Equals(a, SearchSuggestionsOperationName, StringComparison.OrdinalIgnoreCase)).Where(IsRecommendationOperationName)
                    select new
                    {
                        Name = name2,
                        Score = ScoreRecommendationOperation(name2, normalizedContext)
                    } into entry
                    orderby entry.Score descending
                    select entry).ToList();
        if (list.Count == 0)
        {
            return false;
        }
        string name = list[0].Name;
        if (!PathfinderOverrides.TryGetValue(name, out PersistedQueryOverride? value) || value is null)
        {
            return false;
        }
        if (value.Version <= 0 || string.IsNullOrWhiteSpace(value.Sha256Hash))
        {
            return false;
        }
        operationName = name;
        persistedQuery = value;
        return true;
    }

    private static bool IsRecommendationOperationName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }
        if (string.Equals(name, SearchSuggestionsOperationName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        string text = name.Replace("_", string.Empty).ToLowerInvariant();
        return text.Contains("recommend", StringComparison.OrdinalIgnoreCase) || text.Contains("related", StringComparison.OrdinalIgnoreCase) || text.Contains("similar", StringComparison.OrdinalIgnoreCase) || text.Contains("morelike", StringComparison.OrdinalIgnoreCase) || text.Contains("suggest", StringComparison.OrdinalIgnoreCase);
    }

    private static int ScoreRecommendationOperation(string name, string? normalizedContext)
    {
        string text = name.ToLowerInvariant();
        int num = 0;
        if (text.Contains("recommend"))
        {
            num += 5;
        }
        if (text.Contains("related") || text.Contains("similar") || text.Contains("morelike"))
        {
            num += 3;
        }
        if (!string.IsNullOrWhiteSpace(normalizedContext) && text.Contains(normalizedContext, StringComparison.OrdinalIgnoreCase))
        {
            num += 4;
        }
        if (text.Contains(PlaylistType))
        {
            num += ((!string.Equals(normalizedContext, PlaylistType, StringComparison.OrdinalIgnoreCase)) ? 1 : 3);
        }
        if (text.Contains(AlbumType))
        {
            num += ((!string.Equals(normalizedContext, AlbumType, StringComparison.OrdinalIgnoreCase)) ? 1 : 3);
        }
        if (text.Contains("section") || text.Contains("row"))
        {
            num++;
        }
        return num;
    }

    public async Task<JsonDocument?> FetchBrowseAllAsync(CancellationToken cancellationToken)
    {
        return await QueryWithAuthContextAsync(BuildBrowseAllPayload(), cancellationToken);
    }

    public async Task<JsonDocument?> FetchBrowseAllWithBlobAsync(CancellationToken cancellationToken)
    {
        return await QueryWithBlobAuthContextAsync(BuildBrowseAllPayload(), "browseAll", cancellationToken);
    }

    public async Task<JsonDocument?> FetchBrowsePageAsync(string uri, int pageOffset, int pageLimit, int sectionOffset, int sectionLimit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return null;
        }

        return await QueryWithAuthContextAsync(BuildBrowsePagePayload(uri, pageOffset, pageLimit, sectionOffset, sectionLimit), cancellationToken);
    }

    public async Task<JsonDocument?> FetchBrowsePageWithBlobAsync(string uri, int pageOffset, int pageLimit, int sectionOffset, int sectionLimit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return null;
        }

        return await QueryWithBlobAuthContextAsync(BuildBrowsePagePayload(uri, pageOffset, pageLimit, sectionOffset, sectionLimit), "browsePage", cancellationToken);
    }

    public async Task<JsonDocument?> FetchBrowseSectionAsync(string uri, int offset, int limit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return null;
        }

        return await QueryWithAuthContextAsync(BuildBrowseSectionPayload(uri, offset, limit), cancellationToken);
    }

    public async Task<JsonDocument?> FetchBrowseSectionWithBlobAsync(string uri, int offset, int limit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return null;
        }

        return await QueryWithBlobAuthContextAsync(BuildBrowseSectionPayload(uri, offset, limit), "browseSection", cancellationToken);
    }

    public async Task<JsonDocument?> FetchHomeSectionWithBlobAsync(string uri, string? timeZone, int offset, int limit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return null;
        }

        string? blobPath = await TryResolveActiveSpotifyBlobPathAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(blobPath))
        {
            _logger.LogWarning("Spotify {OperationName} (blob) failed: missing blob path.", HomeSectionOperationName);
            return null;
        }

        PathfinderAuthContext? context = await BuildBlobAuthContextAsync(blobPath, cancellationToken);
        if (context is null)
        {
            _logger.LogWarning("Spotify {OperationName} (blob) failed: blob auth unavailable.", HomeSectionOperationName);
            return null;
        }

        return await QueryAsync(context, BuildHomeSectionPayload(uri, timeZone, offset, limit, context), cancellationToken);
    }

    private async Task<JsonDocument?> QueryWithAuthContextAsync(object payload, CancellationToken cancellationToken)
    {
        PathfinderAuthContext? context = await BuildAuthContextAsync(cancellationToken);
        return context is null ? null : await QueryAsync(context, payload, cancellationToken);
    }

    private async Task<JsonDocument?> QueryWithBlobAuthContextAsync(object payload, string operationName, CancellationToken cancellationToken)
    {
        string? blobPath = await TryResolveActiveSpotifyBlobPathAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(blobPath))
        {
            _logger.LogWarning("Spotify {OperationName} (blob) failed: missing blob path.", operationName);
            return null;
        }

        PathfinderAuthContext? context = await BuildBlobAuthContextAsync(blobPath, cancellationToken);
        if (context is null)
        {
            _logger.LogWarning("Spotify {OperationName} (blob) failed: blob auth unavailable.", operationName);
            return null;
        }

        return await QueryAsync(context, payload, cancellationToken);
    }

    private static object BuildBrowseAllPayload()
    {
        PersistedQueryOverride persisted = GetPersistedQuery(BrowseAllOperationName, 1, BrowseAllHashDefault);
        return new
        {
            operationName = BrowseAllOperationName,
            variables = new
            {
                pagePagination = new
                {
                    offset = 0,
                    limit = 50
                },
                sectionPagination = new
                {
                    offset = 0,
                    limit = 50
                }
            },
            extensions = new
            {
                persistedQuery = new
                {
                    version = persisted.Version,
                    sha256Hash = persisted.Sha256Hash
                }
            }
        };
    }

    private static object BuildBrowsePagePayload(string uri, int pageOffset, int pageLimit, int sectionOffset, int sectionLimit)
    {
        PersistedQueryOverride persisted = GetPersistedQuery(BrowsePageOperationName, 1, BrowsePageHashDefault);
        return new
        {
            operationName = BrowsePageOperationName,
            variables = new
            {
                pagePagination = new
                {
                    offset = pageOffset,
                    limit = pageLimit
                },
                sectionPagination = new
                {
                    offset = sectionOffset,
                    limit = sectionLimit
                },
                uri,
                browseEndUserIntegration = IntegrationWebPlayer
            },
            extensions = new
            {
                persistedQuery = new
                {
                    version = persisted.Version,
                    sha256Hash = persisted.Sha256Hash
                }
            }
        };
    }

    private static object BuildBrowseSectionPayload(string uri, int offset, int limit)
    {
        PersistedQueryOverride persisted = GetPersistedQuery(BrowseSectionOperationName, 1, BrowseSectionHashDefault);
        return new
        {
            operationName = BrowseSectionOperationName,
            variables = new
            {
                pagination = new { offset, limit },
                uri,
                browseEndUserIntegration = IntegrationWebPlayer
            },
            extensions = new
            {
                persistedQuery = new
                {
                    version = persisted.Version,
                    sha256Hash = persisted.Sha256Hash
                }
            }
        };
    }

    private static object BuildHomeSectionPayload(string uri, string? timeZone, int offset, int limit, PathfinderAuthContext context)
    {
        string normalizedTimeZone = string.IsNullOrWhiteSpace(timeZone) ? "America/New_York" : timeZone.Trim();
        int boundedOffset = Math.Max(0, offset);
        int boundedLimit = Math.Clamp((limit <= 0) ? 20 : limit, 1, 100);
        Dictionary<string, object?> variables = new Dictionary<string, object?>
        {
            ["uri"] = uri,
            ["homeEndUserIntegration"] = IntegrationWebPlayer,
            ["timeZone"] = normalizedTimeZone,
            ["sectionItemsOffset"] = boundedOffset,
            ["sectionItemsLimit"] = boundedLimit
        };
        if (!string.IsNullOrWhiteSpace(context.DeviceId))
        {
            variables["sp_t"] = context.DeviceId;
        }

        PersistedQueryOverride persisted = GetPersistedQuery(HomeSectionOperationName, 1, HomeSectionHashDefault);
        return new
        {
            operationName = HomeSectionOperationName,
            variables,
            extensions = new
            {
                persistedQuery = new
                {
                    version = persisted.Version,
                    sha256Hash = persisted.Sha256Hash
                }
            }
        };
    }

    public async Task<List<SpotifyTrackSummary>> FetchBrowseSectionTrackSummariesWithBlobAsync(string uri, int offset, int limit, CancellationToken cancellationToken)
    {
        int boundedOffset = Math.Max(0, offset);
        int boundedLimit = Math.Clamp((limit <= 0) ? 50 : limit, 1, 200);
        try
        {
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(25.0));
            using JsonDocument? doc = await FetchBrowseSectionWithBlobAsync(uri, boundedOffset, boundedLimit, timeoutCts.Token);
            if (doc is null)
            {
                return new List<SpotifyTrackSummary>();
            }
            return ParseBrowseSectionTracks(doc.RootElement, boundedLimit);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Spotify browseSection tracks timed out. uri={Uri} offset={Offset} limit={Limit}", uri, boundedOffset, boundedLimit);
            return new List<SpotifyTrackSummary>();
        }
    }

    private async Task<(HttpStatusCode Status, string? Json)> SendPathfinderRequestAsync(HttpClient client, PathfinderAuthContext context, string payloadJson, CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= 4; attempt++)
        {
            using HttpRequestMessage request = CreatePathfinderRequest(context, payloadJson);
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            string? text = response.Content != null ? await response.Content.ReadAsStringAsync(cancellationToken) : null;
            string? body = text;
            if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt == 4)
            {
                return (Status: response.StatusCode, Json: body);
            }
            TimeSpan retryDelay = ResolveRetryDelay(response, attempt);
            _logger.LogWarning("Spotify Pathfinder request rate-limited (429). Retrying in {DelaySeconds}s (attempt {Attempt}/{MaxAttempts}).", Math.Round(retryDelay.TotalSeconds, 1), attempt, 4);
            await Task.Delay(retryDelay, cancellationToken);
        }
        return (Status: HttpStatusCode.TooManyRequests, Json: null);
    }

    private static HttpRequestMessage CreatePathfinderRequest(PathfinderAuthContext context, string payloadJson)
    {
        HttpRequestMessage httpRequestMessage = new HttpRequestMessage(HttpMethod.Post, PathfinderUrl)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(payloadJson))
        };
        httpRequestMessage.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        httpRequestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
        httpRequestMessage.Headers.Add("Client-Token", context.ClientToken);
        httpRequestMessage.Headers.Add("Spotify-App-Version", context.ClientVersion);
        httpRequestMessage.Headers.Add("App-Platform", WebPlayerAppPlatform);
        httpRequestMessage.Headers.UserAgent.ParseAdd(WebPlayerUserAgent);
        return httpRequestMessage;
    }

    private static TimeSpan ResolveRetryDelay(HttpResponseMessage response, int attempt)
    {
        TimeSpan? timeSpan = response.Headers.RetryAfter?.Delta;
        if (timeSpan.HasValue)
        {
            TimeSpan valueOrDefault = timeSpan.GetValueOrDefault();
            if (valueOrDefault > TimeSpan.Zero)
            {
                return ClampRetryDelay(valueOrDefault);
            }
        }
        DateTimeOffset? dateTimeOffset = response.Headers.RetryAfter?.Date;
        if (dateTimeOffset.HasValue)
        {
            DateTimeOffset valueOrDefault2 = dateTimeOffset.GetValueOrDefault();
            if (true)
            {
                TimeSpan timeSpan2 = valueOrDefault2 - DateTimeOffset.UtcNow;
                if (timeSpan2 > TimeSpan.Zero)
                {
                    return ClampRetryDelay(timeSpan2);
                }
            }
        }
        int num = Math.Min(30, (int)Math.Pow(2.0, attempt + 1));
        return TimeSpan.FromSeconds(num);
    }

    private static TimeSpan ClampRetryDelay(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
        {
            return TimeSpan.FromSeconds(2.0);
        }
        return (delay > TimeSpan.FromMinutes(2.0)) ? TimeSpan.FromMinutes(2.0) : delay;
    }

    private async Task<SpotifyUrlMetadata?> FetchTrackAsync(PathfinderAuthContext context, string trackId, CancellationToken cancellationToken)
    {
        JsonElement? trackUnion = await QueryTrackUnionAsync(context, trackId, cancellationToken);
        if (!trackUnion.HasValue)
        {
            return null;
        }
        SpotifyTrackSummary? track = ParseTrackSummary(trackUnion.Value);
        if (track is null)
        {
            return null;
        }
        return new SpotifyUrlMetadata(TrackType, track.Id, track.Name, track.SourceUrl, track.ImageUrl, track.Artists, 1, track.DurationMs, new List<SpotifyTrackSummary> { track }, new List<SpotifyAlbumSummary>(), null, null, null);
    }

    private async Task<SpotifyUrlMetadata?> FetchAlbumAsync(PathfinderAuthContext context, string albumId, CancellationToken cancellationToken)
    {
        AlbumUnionResult? albumUnion = await QueryAlbumUnionAsync(context, albumId, cancellationToken);
        if (albumUnion is null)
        {
            return null;
        }
        JsonElement albumData = albumUnion.AlbumUnion;
        string albumName = TryGetString(albumData, "name") ?? "Spotify album";
        string? artists = ExtractArtistsFromItems(albumData, ArtistsKey);
        string? coverUrl = ExtractCoverUrl(albumData, CoverArtKey);
        int? totalTracks = TryGetInt(albumData, TracksV2Key, TotalCountKey);
        string albumUrl = BuildSpotifyUrl(AlbumType, albumId);
        List<SpotifyTrackSummary> tracks = ParseAlbumTracks(albumData, albumUnion.TrackItems);
        return new SpotifyUrlMetadata(AlbumType, albumId, albumName, albumUrl, coverUrl, artists, totalTracks ?? tracks.Count, null, tracks, new List<SpotifyAlbumSummary>
        {
            new SpotifyAlbumSummary(albumId, albumName, artists, coverUrl, albumUrl, totalTracks ?? tracks.Count)
        }, null, null, null);
    }

    private async Task<SpotifyUrlMetadata?> FetchPlaylistAsync(PathfinderAuthContext context, string playlistId, CancellationToken cancellationToken)
    {
        PlaylistUnionResult? playlistUnion = await QueryPlaylistUnionAsync(context, playlistId, cancellationToken);
        if (playlistUnion is null)
        {
            return null;
        }
        JsonElement playlistData = playlistUnion.PlaylistUnion;
        string name = TryGetString(playlistData, "name") ?? "Spotify playlist";
        string? description = TryGetString(playlistData, "description");
        string? imageUrl = ExtractPlaylistImageUrl(playlistData);
        string? ownerName = TryGetOwnerName(playlistData);
        int? followers = TryGetFollowers(playlistData);
        int? totalTracks = TryGetInt(playlistData, ContentKey, TotalCountKey);
        string playlistUrl = BuildSpotifyUrl(PlaylistType, playlistId);
        List<SpotifyTrackSummary> tracks = ParsePlaylistTracks(playlistData, playlistUnion.TrackItems);
        return new SpotifyUrlMetadata(PlaylistType, playlistId, name, playlistUrl, imageUrl, description, totalTracks ?? tracks.Count, null, tracks, new List<SpotifyAlbumSummary>(), ownerName, followers, null);
    }

    private async Task<SpotifyUrlMetadata?> FetchArtistAsync(PathfinderAuthContext context, string artistId, CancellationToken cancellationToken)
    {
        JsonElement? overviewUnion = await QueryArtistOverviewAsync(context, artistId, cancellationToken);
        if (!overviewUnion.HasValue)
        {
            return null;
        }
        JsonElement artistData = overviewUnion.Value;
        JsonElement? profile = TryGetObject(artistData, ProfileKey);
        string? name = profile.HasValue ? TryGetString(profile.Value, "name") : null;
        string? imageUrl = ExtractArtistImageUrl(artistData);
        int? followers = TryGetInt(artistData, StatsKey, FollowersKey);
        string? subtitle = followers.HasValue ? $"Followers {followers.Value:N0}" : null;
        string artistUrl = BuildSpotifyUrl(ArtistType, artistId);
        List<SpotifyAlbumSummary> albums = await FetchArtistDiscographyAsync(context, artistId, cancellationToken);
        return new SpotifyUrlMetadata(TrackList: await FetchArtistTracksAsync(context, albums, cancellationToken), Type: ArtistType, Id: artistId, Name: name ?? "Spotify artist", SourceUrl: artistUrl, ImageUrl: imageUrl, Subtitle: subtitle, TotalTracks: albums.Count, DurationMs: null, AlbumList: albums, OwnerName: null, Followers: null, SnapshotId: null);
    }

    private async Task<JsonElement?> QueryTrackUnionAsync(PathfinderAuthContext context, string trackId, CancellationToken cancellationToken)
    {
        using JsonDocument? doc = await QueryTrackDocumentAsync(context, trackId, cancellationToken);
        if (doc is null)
        {
            return null;
        }
        if (!TryGetNested(doc.RootElement, out var trackUnion, DataKey, TrackUnionKey))
        {
            return null;
        }
        return trackUnion.Clone();
    }

    private async Task<JsonDocument?> QueryTrackDocumentAsync(PathfinderAuthContext context, string trackId, CancellationToken cancellationToken)
    {
        foreach (object payload in BuildTrackPayloads(trackId))
        {
            JsonDocument? doc = await QueryAsync(context, payload, cancellationToken);
            if (doc is not null)
            {
                if (TryGetNested(doc.RootElement, out var _, DataKey, TrackUnionKey))
                {
                    return doc;
                }
                doc.Dispose();
            }
        }
        return null;
    }

    private static IEnumerable<object> BuildTrackPayloads(string trackId)
    {
        PersistedQueryOverride getTrack = GetPersistedQuery(GetTrackOperationName, 1, GetTrackFallbackHash);
        yield return new
        {
            variables = new
            {
                uri = SpotifyTrackUriPrefix + trackId
            },
            operationName = GetTrackOperationName,
            extensions = new
            {
                persistedQuery = new
                {
                    version = getTrack.Version,
                    sha256Hash = getTrack.Sha256Hash
                }
            }
        };
        PersistedQueryOverride queryTrackPage = GetPersistedQuery(QueryTrackPageOperationName, 1, QueryTrackPageFallbackHash);
        yield return new
        {
            variables = new
            {
                uri = SpotifyTrackUriPrefix + trackId,
                locale = "en"
            },
            operationName = QueryTrackPageOperationName,
            extensions = new
            {
                persistedQuery = new
                {
                    version = queryTrackPage.Version,
                    sha256Hash = queryTrackPage.Sha256Hash
                }
            }
        };
    }

    private async Task<AlbumUnionResult?> QueryAlbumUnionAsync(PathfinderAuthContext context, string albumId, CancellationToken cancellationToken)
    {
        List<JsonElement> allItems = new List<JsonElement>();
        int offset = 0;
        JsonElement? albumUnion = null;
        while (true)
        {
            JsonElement? currentUnion = await QueryAlbumUnionPageAsync(context, albumId, offset, cancellationToken);
            if (!currentUnion.HasValue)
            {
                break;
            }
            CaptureUnion(ref albumUnion, currentUnion.Value);
            List<JsonElement> itemList = ExtractClonedItems(currentUnion.Value, TracksV2Key, ItemsKey);
            if (itemList.Count == 0)
            {
                break;
            }
            allItems.AddRange(itemList);
            if (!ShouldContinueAlbumUnionQuery(allItems.Count, itemList.Count, TryGetInt(currentUnion.Value, TracksV2Key, TotalCountKey)))
            {
                break;
            }
            offset += 1000;
        }
        return (!albumUnion.HasValue) ? null : new AlbumUnionResult(albumUnion.Value.Clone(), allItems);
    }

    private async Task<JsonElement?> QueryAlbumUnionPageAsync(PathfinderAuthContext context, string albumId, int offset, CancellationToken cancellationToken)
    {
        PersistedQueryOverride persisted = GetPersistedQuery(GetAlbumOperationName, 1, "b9bfabef66ed756e5e13f68a942deb60bd4125ec1f1be8cc42769dc0259b4b10");
        var payload = new
        {
            variables = new
            {
                uri = "spotify:album:" + albumId,
                locale = string.Empty,
                offset = offset,
                limit = 1000
            },
            operationName = GetAlbumOperationName,
            extensions = new
            {
                persistedQuery = new
                {
                    version = persisted.Version,
                    sha256Hash = persisted.Sha256Hash
                }
            }
        };
        using JsonDocument? doc = await QueryAsync(context, payload, cancellationToken);
        if (doc is null || !TryGetNested(doc.RootElement, out JsonElement albumUnion, DataKey, "albumUnion"))
        {
            return null;
        }
        return albumUnion.Clone();
    }

    private static bool ShouldContinueAlbumUnionQuery(int itemCount, int pageCount, int? totalCount)
    {
        return pageCount != 0 && itemCount < (totalCount ?? itemCount) && pageCount >= 1000;
    }

    private async Task<PlaylistUnionResult?> QueryPlaylistUnionAsync(PathfinderAuthContext context, string playlistId, CancellationToken cancellationToken, int? maxItems = null)
    {
        List<JsonElement> allItems = new List<JsonElement>();
        int offset = 0;
        JsonElement? playlistUnion = null;
        while (true)
        {
            int pageLimit = ResolvePathfinderPageLimit(maxItems, allItems.Count);
            JsonElement? currentUnion = await QueryPlaylistUnionPageAsync(context, playlistId, offset, pageLimit, cancellationToken);
            if (!currentUnion.HasValue)
            {
                break;
            }
            CaptureUnion(ref playlistUnion, currentUnion.Value);
            List<JsonElement> itemList = ExtractClonedItems(currentUnion.Value, ContentKey, ItemsKey);
            if (itemList.Count == 0)
            {
                break;
            }
            allItems.AddRange(itemList);
            if (!ShouldContinuePlaylistUnionQuery(maxItems, allItems.Count, itemList.Count, TryGetInt(currentUnion.Value, ContentKey, TotalCountKey)))
            {
                break;
            }
            offset += 1000;
        }
        return (!playlistUnion.HasValue) ? null : new PlaylistUnionResult(playlistUnion.Value.Clone(), allItems);
    }

    private static int ResolvePathfinderPageLimit(int? maxItems, int currentCount)
    {
        return maxItems.HasValue
            ? Math.Clamp(maxItems.Value - currentCount, 1, 1000)
            : 1000;
    }

    private async Task<JsonElement?> QueryPlaylistUnionPageAsync(PathfinderAuthContext context, string playlistId, int offset, int pageLimit, CancellationToken cancellationToken)
    {
        var payload = new
        {
            variables = new
            {
                uri = "spotify:playlist:" + playlistId,
                offset = offset,
                limit = pageLimit,
                enableWatchFeedEntrypoint = false
            },
            operationName = FetchPlaylistOperationName,
            extensions = new
            {
                persistedQuery = new
                {
                    version = 1,
                    sha256Hash = "bb67e0af06e8d6f52b531f97468ee4acd44cd0f82b988e15c2ea47b1148efc77"
                }
            }
        };

        using JsonDocument? doc = await QueryAsync(context, payload, cancellationToken);
        if (doc is null || !TryGetNested(doc.RootElement, out var playlistUnion, DataKey, "playlistV2"))
        {
            return null;
        }

        return playlistUnion.Clone();
    }

    private static void CaptureUnion(ref JsonElement? existingUnion, JsonElement currentUnion)
    {
        if (!existingUnion.HasValue)
        {
            existingUnion = currentUnion.Clone();
        }
    }

    private static List<JsonElement> ExtractClonedItems(JsonElement union, params string[] path)
    {
        JsonElement? items = GetArray(union, path);
        return items.HasValue && items.Value.ValueKind == JsonValueKind.Array
            ? items.Value.EnumerateArray().Select(item => item.Clone()).ToList()
            : new List<JsonElement>();
    }

    private static bool ShouldContinuePlaylistUnionQuery(int? maxItems, int itemCount, int pageCount, int? totalCount)
    {
        if (pageCount == 0)
        {
            return false;
        }

        if (maxItems.HasValue && itemCount >= maxItems.Value)
        {
            return false;
        }

        return itemCount < (totalCount ?? itemCount) && pageCount >= 1000;
    }

    private async Task<JsonElement?> QueryArtistAsync(PathfinderAuthContext context, string artistId, CancellationToken cancellationToken)
    {
        var payload = new
        {
            variables = new
            {
                uri = SpotifyArtistUriPrefix + artistId
            },
            operationName = QueryArtistOperationName,
            extensions = new
            {
                persistedQuery = new
                {
                    version = GetPersistedQuery(QueryArtistOperationName, 1, QueryArtistHashDefault).Version,
                    sha256Hash = GetPersistedQuery(QueryArtistOperationName, 1, QueryArtistHashDefault).Sha256Hash
                }
            }
        };
        using JsonDocument? doc = await QueryAsync(context, payload, cancellationToken);
        if (doc is null || !TryGetNested(doc.RootElement, out var artistUnion, DataKey, ArtistUnionKey))
        {
            return null;
        }
        return artistUnion.Clone();
    }

    private async Task<JsonElement?> QueryArtistOverviewAsync(PathfinderAuthContext context, string artistId, CancellationToken cancellationToken, string locale = "")
    {
        var payload = new
        {
            variables = new
            {
                uri = SpotifyArtistUriPrefix + artistId,
                locale = locale
            },
            operationName = QueryArtistOverviewOperationName,
            extensions = new
            {
                persistedQuery = new
                {
                    version = GetPersistedQuery(QueryArtistOverviewOperationName, 1, QueryArtistOverviewHashDefault).Version,
                    sha256Hash = GetPersistedQuery(QueryArtistOverviewOperationName, 1, QueryArtistOverviewHashDefault).Sha256Hash
                }
            }
        };
        using JsonDocument? doc = await QueryAsync(context, payload, cancellationToken);
        if (doc is null || !TryGetNested(doc.RootElement, out var artistUnion, DataKey, ArtistUnionKey))
        {
            return null;
        }
        return artistUnion.Clone();
    }

    private static bool IsPlaceholderBiography(string? biography)
    {
        string text = (biography ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }
        return text.Equals("N/A", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<List<SpotifyAlbumSummary>> FetchArtistDiscographyAsync(PathfinderAuthContext context, string artistId, CancellationToken cancellationToken)
    {
        List<SpotifyAlbumSummary> albums = new List<SpotifyAlbumSummary>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int offset = 0;
        while (true)
        {
            DiscographyPageResult? page = await QueryArtistDiscographyPageAsync(context, artistId, offset, cancellationToken);
            if (page == null || page.Items.Count == 0)
            {
                break;
            }
            AppendDiscographyReleases(albums, seen, page.Items);
            int totalCount = TryGetInt(page.DiscographyAll, TotalCountKey) ?? page.Items.Count;
            if (seen.Count >= totalCount || page.Items.Count < 50)
            {
                break;
            }
            offset += 50;
        }
        return albums;
    }

    private async Task<DiscographyPageResult?> QueryArtistDiscographyPageAsync(PathfinderAuthContext context, string artistId, int offset, CancellationToken cancellationToken)
    {
        PersistedQueryOverride persisted = GetPersistedQuery("queryArtistDiscographyAll", 1, "5e07d323febb57b4a56a42abbf781490e58764aa45feb6e3dc0591564fc56599");
        var payload = new
        {
            variables = new
            {
                uri = SpotifyArtistUriPrefix + artistId,
                offset = offset,
                limit = 50,
                order = "DATE_DESC"
            },
            operationName = "queryArtistDiscographyAll",
            extensions = new
            {
                persistedQuery = new
                {
                    version = persisted.Version,
                    sha256Hash = persisted.Sha256Hash
                }
            }
        };
        using JsonDocument? doc = await QueryAsync(context, payload, cancellationToken);
        if (doc is null || !TryGetNested(doc.RootElement, out JsonElement discographyAll, DataKey, ArtistUnionKey, DiscographyKey, "all"))
        {
            return null;
        }
        List<JsonElement> itemList = ExtractClonedItems(discographyAll, ItemsKey);
        return itemList.Count == 0 ? null : new DiscographyPageResult(discographyAll.Clone(), itemList);
    }

    private static void AppendDiscographyReleases(List<SpotifyAlbumSummary> albums, HashSet<string> seen, IEnumerable<JsonElement> items)
    {
        foreach (DiscographyRelease release in items
            .Select(ExtractDiscographyRelease)
            .OfType<DiscographyRelease>()
            .Where(release => !string.IsNullOrWhiteSpace(release.Id) && seen.Add(release.Id)))
        {
            albums.Add(new SpotifyAlbumSummary(release.Id, release.Name ?? "Spotify release", release.Artists, release.ImageUrl, BuildSpotifyUrl(AlbumType, release.Id), release.TotalTracks, release.ReleaseDate, release.AlbumGroup, release.ReleaseType));
        }
    }

    private async Task<List<SpotifyTrackSummary>> FetchArtistTracksAsync(PathfinderAuthContext context, List<SpotifyAlbumSummary> albums, CancellationToken cancellationToken)
    {
        List<SpotifyTrackSummary> tracks = new List<SpotifyTrackSummary>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string albumId in albums.Select(static album => album.Id).Where(static albumId => !string.IsNullOrWhiteSpace(albumId)))
        {
            AlbumUnionResult? albumUnion = await QueryAlbumUnionAsync(context, albumId!, cancellationToken);
            if (albumUnion is null)
            {
                continue;
            }
            foreach (SpotifyTrackSummary track in from spotifyTrackSummary in ParseAlbumTracks(albumUnion.AlbumUnion, albumUnion.TrackItems)
                                                  where seen.Add(spotifyTrackSummary.Id)
                                                  select spotifyTrackSummary)
            {
                tracks.Add(track);
            }
        }
        return tracks;
    }

    private static SpotifyArtistExtras ParseArtistExtras(JsonElement artistUnion)
    {
        string? biography = ExtractArtistBiography(artistUnion);
        bool? verified = TryGetBool(artistUnion, ProfileKey, "verified");
        int? monthlyListeners = TryGetInt(artistUnion, StatsKey, "monthlyListeners") ?? TryGetInt(artistUnion, StatsKey, "listeners");
        int? rank = TryGetInt(artistUnion, StatsKey, "rank") ?? TryGetInt(artistUnion, StatsKey, "worldRank");
        return new SpotifyArtistExtras(biography, verified, monthlyListeners, rank);
    }

    private static SpotifyArtistOverview ParseArtistOverview(string artistId, JsonElement artistUnion)
    {
        JsonElement? jsonElement = TryGetObject(artistUnion, ProfileKey);
        string? text = jsonElement.HasValue ? TryGetString(jsonElement.Value, "name") : null;
        string? imageUrl = ExtractArtistImageUrl(artistUnion);
        string? headerImageUrl = ExtractArtistHeaderImageUrl(artistUnion);
        List<string> gallery = ExtractArtistGalleryUrls(artistUnion);
        int? followers = TryGetInt(artistUnion, StatsKey, FollowersKey);
        int? popularity = TryGetInt(artistUnion, StatsKey, "popularity");
        List<string> genres = ExtractArtistGenres(artistUnion);
        string sourceUrl = BuildSpotifyUrl(ArtistType, artistId);
        List<string> popularReleaseAlbumIds = ExtractPopularReleaseAlbumIds(artistUnion);
        int? totalAlbums = TryGetInt(artistUnion, DiscographyKey, "all", TotalCountKey) ?? TryGetInt(artistUnion, DiscographyKey, "all", "total") ?? SumArtistDiscographyTotal(artistUnion);
        return new SpotifyArtistOverview(artistId, text ?? "Spotify artist", imageUrl, headerImageUrl, gallery, followers, genres, sourceUrl, popularity, totalAlbums, "all", popularReleaseAlbumIds);
    }

    private static SpotifyArtistOverview? MergeArtistOverview(SpotifyArtistOverview? primary, SpotifyArtistOverview? fallback)
    {
        if (primary is null)
        {
            return fallback;
        }
        if (fallback is null)
        {
            return primary;
        }
        List<string>? genres = PreferNonEmptyList(primary.Genres, fallback.Genres);
        List<string>? gallery = PreferNonEmptyList(primary.Gallery, fallback.Gallery);
        int? followers = primary.Followers ?? fallback.Followers;
        int? popularity = primary.Popularity ?? fallback.Popularity;
        int? totalAlbums = primary.TotalAlbums ?? fallback.TotalAlbums;
        List<string>? popularReleaseAlbumIds = PreferNonEmptyList(primary.PopularReleaseAlbumIds, fallback.PopularReleaseAlbumIds);
        return primary with
        {
            ImageUrl = !string.IsNullOrWhiteSpace(primary.ImageUrl) ? primary.ImageUrl : fallback.ImageUrl,
            HeaderImageUrl = !string.IsNullOrWhiteSpace(primary.HeaderImageUrl) ? primary.HeaderImageUrl : fallback.HeaderImageUrl,
            Genres = genres ?? new List<string>(),
            Followers = followers,
            Popularity = popularity,
            TotalAlbums = totalAlbums,
            Gallery = gallery ?? new List<string>(),
            PopularReleaseAlbumIds = popularReleaseAlbumIds
        };
    }

    private static List<string> ExtractPopularReleaseAlbumIds(JsonElement artistUnion)
    {
        List<string> list = new List<string>();
        HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!TryGetNested(artistUnion, out var value, DiscographyKey, "popularReleasesAlbums", ItemsKey) || value.ValueKind != JsonValueKind.Array)
        {
            return list;
        }
        foreach (string item in (from album in value.EnumerateArray()
                                 select TryGetString(album, "id") ?? ExtractIdFromUri(TryGetString(album, "uri")) into id
                                 where !string.IsNullOrWhiteSpace(id)
                                 select (id)).Where(hashSet.Add))
        {
            list.Add(item);
        }
        return list;
    }

    private static List<SpotifyTrackSummary> ParseArtistTopTracks(JsonElement artistUnion)
    {
        List<SpotifyTrackSummary> list = new List<SpotifyTrackSummary>();
        HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, SpotifyAlbumReference> albumLookup = BuildArtistAlbumLookup(artistUnion);
        if (!TryGetNested(artistUnion, out var value, DiscographyKey, "topTracks", ItemsKey) || value.ValueKind != JsonValueKind.Array)
        {
            return list;
        }
        foreach (JsonElement item in value.EnumerateArray())
        {
            JsonElement jsonElement = item;
            if (TryGetNested(item, out var trackValue, TrackType))
            {
                jsonElement = trackValue;
            }
            else if (TryGetNested(item, out var itemValue, ItemV2Key, DataKey))
            {
                jsonElement = itemValue;
            }
            SpotifyTrackSummary? spotifyTrackSummary = ParseTrackSummary(jsonElement);
            if (spotifyTrackSummary is not null && hashSet.Add(spotifyTrackSummary.Id))
            {
                string? releaseDate = TryGetString(jsonElement, AlbumOfTrackKey, "date", IsoStringKey) ?? TryGetString(jsonElement, AlbumOfTrackKey, ReleaseDateKey) ?? TryGetString(jsonElement, AlbumOfTrackKey, "date", "year");
                bool value4 = TryGetString(jsonElement, ContentRatingKey, LabelKey)?.Equals(ExplicitLabel, StringComparison.OrdinalIgnoreCase) ?? false;
                string? text = ExtractIdFromUri(TryGetString(jsonElement, AlbumOfTrackKey, "uri"));
                if (string.IsNullOrWhiteSpace(text))
                {
                    text = spotifyTrackSummary.AlbumId;
                }
                spotifyTrackSummary = spotifyTrackSummary with
                {
                    Album = ResolveTopTrackAlbumName(spotifyTrackSummary, text, albumLookup),
                    ReleaseDate = releaseDate,
                    Explicit = value4,
                    AlbumId = text,
                    ReleaseType = ResolveTopTrackReleaseType(spotifyTrackSummary, text, albumLookup),
                    AlbumGroup = ResolveTopTrackAlbumGroup(spotifyTrackSummary, text, albumLookup)
                };
                spotifyTrackSummary = spotifyTrackSummary with
                {
                    ReleaseDate = ResolveTopTrackReleaseDate(spotifyTrackSummary, text, albumLookup)
                };
                list.Add(spotifyTrackSummary);
            }
        }
        return list;
    }

    private static List<SpotifyTrackSummary> MergeTopTrackSummaries(List<SpotifyTrackSummary>? primary, List<SpotifyTrackSummary>? fallback)
    {
        if (primary == null || primary.Count == 0)
        {
            return fallback ?? new List<SpotifyTrackSummary>();
        }
        if (fallback == null || fallback.Count == 0)
        {
            return primary;
        }
        List<SpotifyTrackSummary> list = new List<SpotifyTrackSummary>(Math.Max(primary.Count, fallback.Count));
        Dictionary<string, SpotifyTrackSummary> fallbackLookup = BuildTrackSummaryLookup(fallback);
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AppendMergedPrimaryTopTracks(list, seen, primary, fallbackLookup);
        AppendFallbackTopTracks(list, seen, fallback);
        return list;
    }

    private static Dictionary<string, SpotifyTrackSummary> BuildTrackSummaryLookup(IEnumerable<SpotifyTrackSummary> tracks)
    {
        Dictionary<string, SpotifyTrackSummary> lookup = new Dictionary<string, SpotifyTrackSummary>(StringComparer.OrdinalIgnoreCase);
        foreach (SpotifyTrackSummary track in tracks.Where(track => !string.IsNullOrWhiteSpace(track.Id) && !lookup.ContainsKey(track.Id)))
        {
            lookup[track.Id] = track;
        }
        return lookup;
    }

    private static void AppendMergedPrimaryTopTracks(List<SpotifyTrackSummary> target, HashSet<string> seen, IEnumerable<SpotifyTrackSummary> primary, Dictionary<string, SpotifyTrackSummary> fallbackLookup)
    {
        foreach (SpotifyTrackSummary track in primary)
        {
            if (string.IsNullOrWhiteSpace(track.Id))
            {
                continue;
            }
            SpotifyTrackSummary merged = fallbackLookup.TryGetValue(track.Id, out SpotifyTrackSummary? fallback)
                ? MergeTopTrackSummary(track, fallback)
                : track;
            target.Add(merged);
            seen.Add(track.Id);
        }
    }

    private static void AppendFallbackTopTracks(List<SpotifyTrackSummary> target, HashSet<string> seen, IEnumerable<SpotifyTrackSummary> fallback)
    {
        foreach (SpotifyTrackSummary track in fallback.Where(track => !string.IsNullOrWhiteSpace(track.Id) && seen.Add(track.Id)))
        {
            target.Add(track);
        }
    }

    private static SpotifyTrackSummary MergeTopTrackSummary(SpotifyTrackSummary primary, SpotifyTrackSummary fallback)
    {
        return primary with
        {
            Album = PreferPrimary(primary.Album, fallback.Album),
            ReleaseDate = PreferPrimary(primary.ReleaseDate, fallback.ReleaseDate),
            AlbumId = PreferPrimary(primary.AlbumId, fallback.AlbumId),
            ReleaseType = PreferPrimary(primary.ReleaseType, fallback.ReleaseType),
            AlbumGroup = PreferPrimary(primary.AlbumGroup, fallback.AlbumGroup),
            ImageUrl = PreferPrimary(primary.ImageUrl, fallback.ImageUrl),
            Isrc = PreferPrimary(primary.Isrc, fallback.Isrc),
            TrackNumber = primary.TrackNumber ?? fallback.TrackNumber,
            DiscNumber = primary.DiscNumber ?? fallback.DiscNumber,
            TrackTotal = primary.TrackTotal ?? fallback.TrackTotal,
            Explicit = primary.Explicit ?? fallback.Explicit,
            Genres = primary.Genres is { Count: > 0 } ? primary.Genres : fallback.Genres
        };
    }

    private static string? PreferPrimary(string? primary, string? fallback)
    {
        return !string.IsNullOrWhiteSpace(primary) ? primary : fallback;
    }

    private static List<T>? PreferNonEmptyList<T>(List<T>? primary, List<T>? fallback)
    {
        if (primary is { Count: > 0 })
        {
            return primary;
        }

        return fallback;
    }

    private static string? ResolveTopTrackAlbumName(SpotifyTrackSummary summary, string? albumId, IReadOnlyDictionary<string, SpotifyAlbumReference> albumLookup)
    {
        if (!string.IsNullOrWhiteSpace(summary.Album))
        {
            return summary.Album;
        }
        if (string.IsNullOrWhiteSpace(albumId))
        {
            return summary.Album;
        }
        if (albumLookup.TryGetValue(albumId, out SpotifyAlbumReference? value) && value is not null && !string.IsNullOrWhiteSpace(value.Name))
        {
            return value.Name;
        }
        return summary.Album;
    }

    private static string? ResolveTopTrackReleaseDate(SpotifyTrackSummary summary, string? albumId, IReadOnlyDictionary<string, SpotifyAlbumReference> albumLookup)
    {
        if (!string.IsNullOrWhiteSpace(summary.ReleaseDate))
        {
            return summary.ReleaseDate;
        }
        if (string.IsNullOrWhiteSpace(albumId))
        {
            return summary.ReleaseDate;
        }
        if (albumLookup.TryGetValue(albumId, out SpotifyAlbumReference? value) && value is not null && !string.IsNullOrWhiteSpace(value.ReleaseDate))
        {
            return value.ReleaseDate;
        }
        return summary.ReleaseDate;
    }

    private static string? ResolveTopTrackReleaseType(SpotifyTrackSummary summary, string? albumId, IReadOnlyDictionary<string, SpotifyAlbumReference> albumLookup)
    {
        if (!string.IsNullOrWhiteSpace(summary.ReleaseType))
        {
            return summary.ReleaseType;
        }
        if (string.IsNullOrWhiteSpace(albumId))
        {
            return summary.ReleaseType;
        }
        if (albumLookup.TryGetValue(albumId, out SpotifyAlbumReference? value) && value is not null && !string.IsNullOrWhiteSpace(value.ReleaseType))
        {
            return value.ReleaseType;
        }
        return summary.ReleaseType;
    }

    private static string? ResolveTopTrackAlbumGroup(SpotifyTrackSummary summary, string? albumId, IReadOnlyDictionary<string, SpotifyAlbumReference> albumLookup)
    {
        if (!string.IsNullOrWhiteSpace(summary.AlbumGroup))
        {
            return summary.AlbumGroup;
        }
        if (string.IsNullOrWhiteSpace(albumId))
        {
            return summary.AlbumGroup;
        }
        if (albumLookup.TryGetValue(albumId, out SpotifyAlbumReference? value) && value is not null && !string.IsNullOrWhiteSpace(value.AlbumGroup))
        {
            return value.AlbumGroup;
        }
        return summary.AlbumGroup;
    }

    private static Dictionary<string, SpotifyAlbumReference> BuildArtistAlbumLookup(JsonElement artistUnion)
    {
        Dictionary<string, SpotifyAlbumReference> dictionary = new Dictionary<string, SpotifyAlbumReference>(StringComparer.OrdinalIgnoreCase);
        if (TryGetNested(artistUnion, out var value, DiscographyKey, "latest") && value.ValueKind == JsonValueKind.Object)
        {
            UpsertAlbumReference(dictionary, value);
        }
        if (TryGetNested(artistUnion, out var value2, DiscographyKey, "popularReleasesAlbums", ItemsKey) && value2.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value2.EnumerateArray())
            {
                UpsertAlbumReference(dictionary, item);
            }
        }
        AddDiscographyGroupAlbums(artistUnion, "albums", dictionary);
        AddDiscographyGroupAlbums(artistUnion, "singles", dictionary);
        AddDiscographyGroupAlbums(artistUnion, "compilations", dictionary);
        return dictionary;
    }

    private static void AddDiscographyGroupAlbums(JsonElement artistUnion, string group, Dictionary<string, SpotifyAlbumReference> lookup)
    {
        if (!TryGetNested(artistUnion, out var value, DiscographyKey, group, ItemsKey) || value.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        foreach (JsonElement item in value.EnumerateArray())
        {
            UpsertAlbumReferences(lookup, EnumerateReleaseItems(item));
        }
    }

    private static List<SpotifyRelatedArtist> ParseArtistRelatedArtists(JsonElement artistUnion)
    {
        List<SpotifyRelatedArtist> list = new List<SpotifyRelatedArtist>();
        HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement item in EnumerateRelatedArtistArrays(artistUnion))
        {
            foreach (JsonElement item2 in item.EnumerateArray())
            {
                SpotifyRelatedArtist? relatedArtist = TryParseRelatedArtist(item2, hashSet);
                if ((object?)relatedArtist != null)
                {
                    list.Add(relatedArtist);
                }
            }
        }
        return list;
    }

    private static List<SpotifyAlbumSummary> ParseArtistAppearsOn(JsonElement artistUnion)
    {
        List<SpotifyAlbumSummary> list = new List<SpotifyAlbumSummary>();
        HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement item in EnumerateArtistAppearsOnArrays(artistUnion))
        {
            foreach (JsonElement item2 in item.EnumerateArray())
            {
                SpotifyAlbumSummary? summary = TryParseAppearsOnSummary(item2, hashSet);
                if ((object?)summary != null)
                {
                    list.Add(summary);
                }
            }
        }
        return list;
    }

    private static int? SumArtistDiscographyTotal(JsonElement artistUnion)
    {
        string[] source = new string[3] { "albums", "singles", "compilations" };
        List<int> list = (from @group in source
                          select TryGetInt(artistUnion, DiscographyKey, @group, TotalCountKey) ?? TryGetInt(artistUnion, DiscographyKey, @group, "total") into count
                          where count.HasValue && count.Value >= 0
                          select count.Value).ToList();
        return (list.Count == 0) ? ((int?)null) : new int?(list.Sum());
    }

    private static List<string> ExtractArtistGenres(JsonElement artistUnion)
    {
        HashSet<string> genres = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddGenres(genres, GetArray(artistUnion, ProfileKey, GenresKey));
        AddGenres(genres, GetArray(artistUnion, GenresKey));
        AddGenresFromItems(genres, GetArray(artistUnion, ProfileKey, "genreTags", ItemsKey), "name");
        AddGenresFromItems(genres, GetArray(artistUnion, ProfileKey, "genreTags", ItemsKey), "tag");
        AddGenresFromItems(genres, GetArray(artistUnion, VisualsKey, GenresKey, ItemsKey), "name");
        AddGenresFromItems(genres, GetArray(artistUnion, "relatedContent", GenresKey, ItemsKey), "name");
        return genres.ToList();
    }

    private static void UpsertAlbumReferences(Dictionary<string, SpotifyAlbumReference> lookup, IEnumerable<JsonElement> albums)
    {
        foreach (JsonElement album in albums)
        {
            UpsertAlbumReference(lookup, album);
        }
    }

    private static IEnumerable<JsonElement> EnumerateReleaseItems(JsonElement item)
    {
        if (!TryGetNested(item, out var releases, "releases", ItemsKey) || releases.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }
        foreach (JsonElement release in releases.EnumerateArray())
        {
            if (release.ValueKind == JsonValueKind.Object)
            {
                yield return release;
            }
        }
    }

    private static void UpsertAlbumReference(Dictionary<string, SpotifyAlbumReference> map, JsonElement album)
    {
        string? albumId = TryGetString(album, "id") ?? ExtractIdFromUri(TryGetString(album, "uri"));
        if (string.IsNullOrWhiteSpace(albumId))
        {
            return;
        }
        SpotifyAlbumReference candidate = CreateAlbumReference(album);
        if (!map.TryGetValue(albumId, out SpotifyAlbumReference? existing) || existing is null)
        {
            map[albumId] = candidate;
            return;
        }
        map[albumId] = MergeAlbumReference(existing, candidate);
    }

    private static SpotifyAlbumReference CreateAlbumReference(JsonElement album)
    {
        string? releaseType = NormalizeReleaseType(TryGetString(album, "type"));
        return new SpotifyAlbumReference(
            TryGetString(album, "name"),
            TryGetString(album, "date", IsoStringKey) ?? TryGetString(album, "date", "year") ?? TryGetString(album, ReleaseDateKey) ?? TryGetString(album, ReleaseDateSnakeKey),
            releaseType,
            MapReleaseTypeToAlbumGroup(releaseType),
            TryGetInt(album, TracksKey, TotalCountKey) ?? TryGetInt(album, TracksKey, CountKey));
    }

    private static SpotifyAlbumReference MergeAlbumReference(SpotifyAlbumReference existing, SpotifyAlbumReference incoming)
    {
        return new SpotifyAlbumReference(
            PreferPrimary(existing.Name, incoming.Name),
            PreferPrimary(existing.ReleaseDate, incoming.ReleaseDate),
            PreferPrimary(existing.ReleaseType, incoming.ReleaseType),
            PreferPrimary(existing.AlbumGroup, incoming.AlbumGroup),
            existing.TotalTracks ?? incoming.TotalTracks);
    }

    private static IEnumerable<JsonElement> EnumerateRelatedArtistArrays(JsonElement artistUnion)
    {
        RelatedArtistArrays arrays = GetRelatedArtistArrays(artistUnion);
        if (arrays.RelatedContent.HasValue)
        {
            yield return arrays.RelatedContent.Value;
        }
        if (arrays.Direct.HasValue)
        {
            yield return arrays.Direct.Value;
        }
    }

    private static RelatedArtistArrays GetRelatedArtistArrays(JsonElement artistUnion)
    {
        JsonElement? relatedContent = null;
        JsonElement? direct = null;
        if (TryGetNested(artistUnion, out var value, "relatedContent", "relatedArtists", ItemsKey) && value.ValueKind == JsonValueKind.Array)
        {
            relatedContent = value;
        }
        if (TryGetNested(artistUnion, out value, "relatedArtists", ItemsKey) && value.ValueKind == JsonValueKind.Array)
        {
            direct = value;
        }
        return new RelatedArtistArrays(relatedContent, direct);
    }

    private static SpotifyRelatedArtist? TryParseRelatedArtist(JsonElement item, HashSet<string> seenArtistIds)
    {
        JsonElement root = UnwrapDataNode(item);
        string? artistId = TryGetString(root, "id") ?? ExtractIdFromUri(TryGetString(root, "uri"));
        if (string.IsNullOrWhiteSpace(artistId) || !seenArtistIds.Add(artistId))
        {
            return null;
        }
        string? artistName = TryGetString(root, ProfileKey, "name") ?? TryGetString(root, "name");
        if (string.IsNullOrWhiteSpace(artistName))
        {
            return null;
        }
        string? imageUrl = ExtractCoverUrl(root, VisualsKey, AvatarImageKey) ?? ExtractCoverUrl(root, AvatarImageKey) ?? ExtractCoverUrl(root, ImagesKey);
        string sourceUrl = TryGetString(root, "sharingInfo", "shareUrl") ?? BuildSpotifyUrl(ArtistType, artistId);
        return new SpotifyRelatedArtist(artistId, artistName, BuildSingleImageList(imageUrl), sourceUrl);
    }

    private static IEnumerable<JsonElement> EnumerateArtistAppearsOnArrays(JsonElement artistUnion)
    {
        if (TryGetNested(artistUnion, out var value, "relatedContent", "appearsOn", ItemsKey) && value.ValueKind == JsonValueKind.Array)
        {
            yield return value;
        }
        if (TryGetNested(artistUnion, out value, "appearsOn", ItemsKey) && value.ValueKind == JsonValueKind.Array)
        {
            yield return value;
        }
    }

    private static SpotifyAlbumSummary? TryParseAppearsOnSummary(JsonElement item, HashSet<string> seenAlbumIds)
    {
        JsonElement root = UnwrapDataNode(item);
        DiscographyRelease? release = ExtractDiscographyRelease(root) ?? ParseRelease(root);
        if ((object?)release == null || string.IsNullOrWhiteSpace(release.Id) || !seenAlbumIds.Add(release.Id))
        {
            return null;
        }
        string sourceUrl = TryGetString(root, "sharingInfo", "shareUrl") ?? BuildSpotifyUrl(AlbumType, release.Id);
        int? totalTracks = ResolveAppearsOnTotalTracks(root) ?? release.TotalTracks;
        return new SpotifyAlbumSummary(release.Id, release.Name ?? "Spotify release", release.Artists, release.ImageUrl, sourceUrl, totalTracks, release.ReleaseDate, release.AlbumGroup, release.ReleaseType);
    }

    private static int? ResolveAppearsOnTotalTracks(JsonElement root)
    {
        if (!TryGetNested(root, out var releases, "releases", ItemsKey) || releases.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (JsonElement release in releases.EnumerateArray())
        {
            int? totalTracks = TryGetInt(release, TracksKey, TotalCountKey) ?? TryGetInt(release, TracksKey, CountKey);
            if (totalTracks.HasValue)
            {
                return totalTracks;
            }
        }
        return null;
    }

    private static JsonElement UnwrapDataNode(JsonElement item)
    {
        if (TryGetNested(item, out var data, DataKey))
        {
            return data;
        }
        return item;
    }

    private static List<SpotifyImage> BuildSingleImageList(string? imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return new List<SpotifyImage>();
        }
        return new List<SpotifyImage>
        {
            new SpotifyImage(imageUrl, null, null)
        };
    }

    private static void AddGenres(HashSet<string> genres, JsonElement? array)
    {
        if (!array.HasValue || array.Value.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        foreach (JsonElement item in array.Value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                AddGenre(genres, item.GetString());
            }
        }
    }

    private static void AddGenresFromItems(HashSet<string> genres, JsonElement? array, params string[] properties)
    {
        if (!array.HasValue || array.Value.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        foreach (JsonElement item in array.Value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            foreach (string propertyName in properties)
            {
                if (item.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    AddGenre(genres, value.GetString());
                }
            }
        }
    }

    private static void AddGenre(HashSet<string> genres, string? genre)
    {
        if (!string.IsNullOrWhiteSpace(genre))
        {
            genres.Add(genre.Trim());
        }
    }

    private static string? ExtractArtistBiography(JsonElement artistUnion)
    {
        string? text = TryGetString(artistUnion, ProfileKey, BiographyKey, "text") ?? TryGetString(artistUnion, ProfileKey, BiographyKey) ?? TryGetString(artistUnion, BiographyKey, "text") ?? TryGetString(artistUnion, BiographyKey);
        if (!string.IsNullOrWhiteSpace(text))
        {
            return text;
        }
        JsonElement? array = GetArray(artistUnion, ProfileKey, BiographyKey);
        if (array.HasValue)
        {
            foreach (JsonElement item in array.Value.EnumerateArray())
            {
                string? text2 = TryGetString(item, "text") ?? TryGetString(item, ContentKey);
                if (!string.IsNullOrWhiteSpace(text2))
                {
                    return text2;
                }
            }
        }
        return null;
    }

    private static SpotifyTrackSummary? ParseTrackSummary(JsonElement trackUnion)
    {
        string? text = TryGetString(trackUnion, "id") ?? ExtractIdFromUri(TryGetString(trackUnion, "uri"));
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        string name = TryGetString(trackUnion, "name") ?? SpotifyTrackFallbackTitle;
        string? artists = ExtractArtistsFromItems(trackUnion, ArtistsKey);
        string? album = TryGetString(trackUnion, AlbumOfTrackKey, "name");
        string? albumId = TryGetString(trackUnion, AlbumOfTrackKey, "id") ?? ExtractIdFromUri(TryGetString(trackUnion, AlbumOfTrackKey, "uri"));
        string? albumArtist = null;
        if (TryGetNested(trackUnion, out JsonElement albumNode, AlbumOfTrackKey))
        {
            albumArtist = ExtractArtistsFromItems(albumNode, ArtistsKey);
        }
        string? releaseType = NormalizeReleaseType(TryGetString(trackUnion, AlbumOfTrackKey, "type"));
        string albumGroup = MapReleaseTypeToAlbumGroup(releaseType);
        string? releaseDate = TryGetString(trackUnion, AlbumOfTrackKey, "date", IsoStringKey) ?? TryGetString(trackUnion, AlbumOfTrackKey, ReleaseDateKey) ?? TryGetString(trackUnion, AlbumOfTrackKey, ReleaseDateSnakeKey) ?? ExtractYearFromDate(trackUnion, AlbumOfTrackKey);
        int? durationMs = TryGetInt(trackUnion, DurationKey, TotalMillisecondsKey);
        string? imageUrl = ExtractCoverUrl(trackUnion, "visualIdentity") ?? ExtractCoverUrl(trackUnion, AlbumOfTrackKey, CoverArtKey);
        string? isrc = ExtractIsrc(trackUnion);
        int? trackNumber = TryGetInt(trackUnion, "trackNumber") ?? TryGetInt(trackUnion, "track_number") ?? TryGetInt(trackUnion, "number");
        int? discNumber = TryGetInt(trackUnion, "discNumber") ?? TryGetInt(trackUnion, "disc_number");
        int? trackTotal = TryGetInt(trackUnion, AlbumOfTrackKey, TracksKey, TotalCountKey) ?? TryGetInt(trackUnion, AlbumOfTrackKey, TracksV2Key, TotalCountKey);
        string? text2 = TryGetString(trackUnion, ContentRatingKey, LabelKey);
        bool? flag = ((!string.IsNullOrWhiteSpace(text2)) ? new bool?(string.Equals(text2, ExplicitLabel, StringComparison.OrdinalIgnoreCase)) : ((bool?)null));
        string? label = TryGetString(trackUnion, AlbumOfTrackKey, LabelKey);
        IReadOnlyList<string>? genres = ExtractStringArray(trackUnion, AlbumOfTrackKey, GenresKey);
        string sourceUrl = BuildSpotifyUrl(TrackType, text);
        return new SpotifyTrackSummary(text, name, artists, album, durationMs, sourceUrl, imageUrl, isrc, releaseDate, trackNumber, discNumber, trackTotal, flag)
        {
            AlbumId = albumId,
            AlbumArtist = albumArtist,
            Label = label,
            Genres = genres,
            AlbumGroup = albumGroup,
            ReleaseType = releaseType
        };
    }

    private static void CollectPathfinderAudioFeatures(JsonElement element, HashSet<string> requestedTrackIds, Dictionary<string, SpotifyPathfinderAudioFeatures> output)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            string? text = TryGetPathfinderTrackId(element);
            if (!string.IsNullOrWhiteSpace(text) && requestedTrackIds.Contains(text) && TryParsePathfinderAudioFeatures(element, out SpotifyPathfinderAudioFeatures features))
            {
                output[text] = (output.TryGetValue(text, out SpotifyPathfinderAudioFeatures? value) && value is not null ? MergePathfinderAudioFeatures(value, features) : features);
            }
            foreach (JsonProperty item in element.EnumerateObject())
            {
                CollectPathfinderAudioFeatures(item.Value, requestedTrackIds, output);
            }
            return;
        }
        if (element.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        foreach (JsonElement item2 in element.EnumerateArray())
        {
            CollectPathfinderAudioFeatures(item2, requestedTrackIds, output);
        }
    }

    private static SpotifyPathfinderAudioFeatures MergePathfinderAudioFeatures(SpotifyPathfinderAudioFeatures current, SpotifyPathfinderAudioFeatures next)
    {
        return new SpotifyPathfinderAudioFeatures(next.Danceability ?? current.Danceability, next.Energy ?? current.Energy, next.Valence ?? current.Valence, next.Acousticness ?? current.Acousticness, next.Instrumentalness ?? current.Instrumentalness, next.Speechiness ?? current.Speechiness, next.Loudness ?? current.Loudness, next.Tempo ?? current.Tempo, next.TimeSignature ?? current.TimeSignature, next.Liveness ?? current.Liveness, next.Key ?? current.Key, next.Mode ?? current.Mode);
    }

    private static string? TryGetPathfinderTrackId(JsonElement node)
    {
        return TryGetString(node, "id") ?? ExtractIdFromUri(TryGetString(node, "uri"));
    }

    private static bool TryParsePathfinderAudioFeatures(JsonElement node, out SpotifyPathfinderAudioFeatures features)
    {
        double? danceability = ReadPathfinderFeatureDouble(node, "danceability");
        double? energy = ReadPathfinderFeatureDouble(node, "energy");
        double? valence = ReadPathfinderFeatureDouble(node, "valence");
        double? acousticness = ReadPathfinderFeatureDouble(node, "acousticness");
        double? instrumentalness = ReadPathfinderFeatureDouble(node, "instrumentalness");
        double? speechiness = ReadPathfinderFeatureDouble(node, "speechiness");
        double? loudness = ReadPathfinderFeatureDouble(node, "loudness");
        double? tempo = ReadPathfinderFeatureDouble(node, "tempo", "bpm");
        int? timeSignature = ReadPathfinderFeatureInt(node, "time_signature", "timesignature");
        double? liveness = ReadPathfinderFeatureDouble(node, "liveness");
        int? key = ReadPathfinderFeatureInt(node, "key");
        int? mode = ReadPathfinderFeatureInt(node, "mode");
        bool result = danceability.HasValue || energy.HasValue || valence.HasValue || acousticness.HasValue || instrumentalness.HasValue || speechiness.HasValue || loudness.HasValue || tempo.HasValue || timeSignature.HasValue || liveness.HasValue || key.HasValue || mode.HasValue;
        features = new SpotifyPathfinderAudioFeatures(danceability, energy, valence, acousticness, instrumentalness, speechiness, loudness, tempo, timeSignature, liveness, key, mode);
        return result;
    }

    private static double? ReadPathfinderFeatureDouble(JsonElement node, params string[] names)
    {
        JsonElement value;
        double number;
        return names.Select((string name) => (TryGetPathfinderFeatureValue(node, name, out value) && TryReadPathfinderDouble(value, out number)) ? new double?(number) : ((double?)null)).FirstOrDefault((double? number) => number.HasValue);
    }

    private static int? ReadPathfinderFeatureInt(JsonElement node, params string[] names)
    {
        JsonElement value;
        int number;
        return names.Select((string name) => (TryGetPathfinderFeatureValue(node, name, out value) && TryReadPathfinderInt(value, out number)) ? new int?(number) : ((int?)null)).FirstOrDefault((int? number) => number.HasValue);
    }

    private static bool TryGetPathfinderFeatureValue(JsonElement node, string key, out JsonElement value)
    {
        string value2 = NormalizePathfinderFeatureKey(key);
        foreach (JsonElement item in EnumeratePathfinderFeatureContainers(node))
        {
            if (TryGetPathfinderFeatureValueFromContainer(item, value2, out value))
            {
                return true;
            }
        }
        value = default(JsonElement);
        return false;
    }

    private static bool TryGetPathfinderFeatureValueFromContainer(JsonElement container, string normalizedKey, out JsonElement value)
    {
        if (TryGetObjectFeatureValue(container, normalizedKey, out value))
        {
            return true;
        }
        return TryGetArrayFeatureValue(container, normalizedKey, out value);
    }

    private static IEnumerable<JsonElement> EnumeratePathfinderFeatureContainers(JsonElement node)
    {
        if (TryGetNested(node, out var value, "audioAttributes"))
        {
            yield return value;
        }
        if (TryGetNested(node, out value, "audio_attributes"))
        {
            yield return value;
        }
        if (TryGetNested(node, out value, "audioFeatures"))
        {
            yield return value;
        }
        if (TryGetNested(node, out value, "audio_features"))
        {
            yield return value;
        }
        yield return node;
    }

    private static bool TryGetObjectFeatureValue(JsonElement container, string normalizedKey, out JsonElement value)
    {
        if (container.ValueKind == JsonValueKind.Object)
        {
            JsonProperty property = container.EnumerateObject().FirstOrDefault(property => NormalizePathfinderFeatureKey(property.Name).Equals(normalizedKey, StringComparison.Ordinal));
            if (property.Name is not null)
            {
                value = property.Value;
                return true;
            }
        }

        value = default(JsonElement);
        return false;
    }

    private static bool TryGetArrayFeatureValue(JsonElement container, string normalizedKey, out JsonElement value)
    {
        if (TryGetNested(container, out var attributes, "attributes") && attributes.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement attribute in attributes.EnumerateArray())
            {
                string? key = TryGetString(attribute, "key");
                if (!string.IsNullOrWhiteSpace(key)
                    && NormalizePathfinderFeatureKey(key).Equals(normalizedKey, StringComparison.Ordinal)
                    && attribute.TryGetProperty("value", out value))
                {
                    return true;
                }
            }
        }

        value = default(JsonElement);
        return false;
    }

    private static string NormalizePathfinderFeatureKey(string key)
    {
        char[] value = key.Where(char.IsLetterOrDigit).ToArray();
        return new string(value).ToLowerInvariant();
    }

    private static bool TryReadPathfinderDouble(JsonElement value, out double number)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number))
        {
            return true;
        }
        if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
        {
            return true;
        }
        number = 0.0;
        return false;
    }

    private static bool TryReadPathfinderInt(JsonElement value, out int number)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out number))
        {
            return true;
        }
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            return true;
        }
        number = 0;
        return false;
    }

    private static List<JsonElement> ResolveTrackItems(List<JsonElement>? trackItems, JsonElement container, params string[] path)
    {
        if (trackItems is not null)
        {
            return trackItems;
        }

        JsonElement? array = GetArray(container, path);
        return array.HasValue ? array.Value.EnumerateArray().ToList() : new List<JsonElement>();
    }

    private static List<SpotifyTrackSummary> ParseAlbumTracks(JsonElement albumUnion, List<JsonElement>? trackItems)
    {
        List<SpotifyTrackSummary> list = new List<SpotifyTrackSummary>();
        string? album = TryGetString(albumUnion, "name");
        string? albumId = TryGetString(albumUnion, "id") ?? ExtractIdFromUri(TryGetString(albumUnion, "uri"));
        string? albumArtist = ExtractArtistsFromItems(albumUnion, ArtistsKey);
        string? releaseType = NormalizeReleaseType(TryGetString(albumUnion, "type"));
        string albumGroup = MapReleaseTypeToAlbumGroup(releaseType);
        string? label = TryGetString(albumUnion, LabelKey);
        IReadOnlyList<string>? genres = ExtractStringArray(albumUnion, GenresKey);
        string? defaultCoverUrl = ExtractCoverUrl(albumUnion, CoverArtKey);
        string? releaseDate = TryGetString(albumUnion, "date", IsoStringKey) ?? TryGetString(albumUnion, "date", "year") ?? TryGetString(albumUnion, ReleaseDateKey) ?? TryGetString(albumUnion, ReleaseDateSnakeKey) ?? ExtractYearFromDate(albumUnion);
        int? trackTotal = TryGetInt(albumUnion, TracksV2Key, TotalCountKey);
        List<JsonElement> items = ResolveTrackItems(trackItems, albumUnion, TracksV2Key, ItemsKey);
        if (items.Count == 0)
        {
            return list;
        }
        foreach (JsonElement item2 in items)
        {
            if (TryGetNested(item2, out var value, TrackType))
            {
                string? text2 = ExtractIdFromUri(TryGetString(value, "uri")) ?? TryGetString(value, "id");
                if (!string.IsNullOrWhiteSpace(text2))
                {
                    string name = TryGetString(value, "name") ?? SpotifyTrackFallbackTitle;
                    string? artists = ExtractArtistsFromItems(value, ArtistsKey);
                    int? durationMs = TryGetInt(value, DurationKey, TotalMillisecondsKey);
                    string sourceUrl = BuildSpotifyUrl(TrackType, text2);
                    string? imageUrl = ExtractCoverUrl(value, CoverArtKey) ?? defaultCoverUrl;
                    int? trackNumber = TryGetInt(value, "trackNumber") ?? TryGetInt(value, "track_number") ?? TryGetInt(value, "number");
                    int? discNumber = TryGetInt(value, "discNumber") ?? TryGetInt(value, "disc_number");
                    string? text3 = TryGetString(value, ContentRatingKey, LabelKey);
                    bool? flag = ((!string.IsNullOrWhiteSpace(text3)) ? new bool?(string.Equals(text3, ExplicitLabel, StringComparison.OrdinalIgnoreCase)) : ((bool?)null));
                    SpotifyTrackSummary item = new SpotifyTrackSummary(text2, name, artists, album, durationMs, sourceUrl, imageUrl, ExtractIsrc(value), releaseDate, trackNumber, discNumber, trackTotal, flag)
                    {
                        AlbumId = albumId,
                        AlbumArtist = albumArtist,
                        Label = label,
                        Genres = genres,
                        AlbumGroup = albumGroup,
                        ReleaseType = releaseType
                    };
                    list.Add(item);
                }
            }
        }
        return list;
    }

    private static List<SpotifyTrackSummary> ParsePlaylistTracks(JsonElement playlistUnion, List<JsonElement>? trackItems)
    {
        List<SpotifyTrackSummary> list = new List<SpotifyTrackSummary>();
        List<JsonElement> items = ResolveTrackItems(trackItems, playlistUnion, ContentKey, ItemsKey);
        if (items.Count == 0)
        {
            return list;
        }
        foreach (PlaylistTrackProjection item in EnumeratePlaylistTrackProjections(items))
        {
            string? album = item.Summary.Album ?? TryGetString(item.TrackData, AlbumOfTrackKey, "name") ?? TryGetString(item.TrackData, AlbumType, "name");
            int? durationMs = item.Summary.DurationMs ?? TryGetInt(item.TrackData, "trackDuration", TotalMillisecondsKey) ?? TryGetInt(item.TrackData, DurationKey, TotalMillisecondsKey) ?? TryGetInt(item.TrackData, DurationKey, "milliseconds");
            string? imageUrl = item.Summary.ImageUrl ?? ExtractCoverUrl(item.TrackData, AlbumOfTrackKey, CoverArtKey) ?? ExtractCoverUrl(item.TrackData, AlbumType, CoverArtKey) ?? ExtractCoverUrl(item.TrackData, CoverArtKey);
            string? isrc = item.Summary.Isrc ?? ExtractIsrc(item.TrackData);
            int? trackNumber = item.Summary.TrackNumber ?? TryGetInt(item.TrackData, "trackNumber") ?? TryGetInt(item.TrackData, "track_number") ?? TryGetInt(item.TrackData, "number");
            int? discNumber = item.Summary.DiscNumber ?? TryGetInt(item.TrackData, "discNumber") ?? TryGetInt(item.TrackData, "disc_number");
            bool? flag = item.Summary.Explicit;
            if (!flag.HasValue)
            {
                string? text = TryGetString(item.TrackData, ContentRatingKey, LabelKey);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    flag = string.Equals(text, ExplicitLabel, StringComparison.OrdinalIgnoreCase);
                }
            }
            list.Add(new SpotifyTrackSummary(item.Summary.Id, item.Summary.Name, item.Summary.Artists, album, durationMs, item.Summary.SourceUrl, imageUrl, isrc, item.Summary.ReleaseDate, trackNumber, discNumber, item.Summary.TrackTotal, flag)
            {
                AlbumId = item.Summary.AlbumId,
                AlbumArtist = item.Summary.AlbumArtist,
                ArtistIds = item.Summary.ArtistIds,
                Label = item.Summary.Label,
                Genres = item.Summary.Genres
            });
        }
        return list;
    }

    private static IEnumerable<PlaylistTrackProjection> EnumeratePlaylistTrackProjections(IEnumerable<JsonElement> items)
    {
        foreach (JsonElement item in items)
        {
            SpotifyTrackSummary? summary = TryParsePlaylistItemTrack(item, out JsonElement trackData);
            if (summary is not null)
            {
                yield return new PlaylistTrackProjection(summary, trackData);
            }
        }
    }

    private static SpotiFlacPlaylistPayload? BuildSpotiFlacPlaylistPayload(JsonElement playlistUnion, List<JsonElement>? trackItems)
    {
        if (playlistUnion.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        string name = TryGetString(playlistUnion, "name") ?? string.Empty;
        string? description = TryGetString(playlistUnion, "description");
        string? displayName = TryGetOwnerName(playlistUnion);
        string? images = ExtractSpotiFlacOwnerAvatar(playlistUnion);
        string? cover = ExtractSpotiFlacPlaylistCover(playlistUnion);
        int total = ExtractSpotiFlacFollowers(playlistUnion);
        List<JsonElement> items = ResolveTrackItems(trackItems, playlistUnion, ContentKey, ItemsKey);
        List<SpotiFlacAlbumTrackMetadata> list2 = BuildSpotiFlacPlaylistTracks(items);
        int total2 = TryGetInt(playlistUnion, ContentKey, TotalCountKey) ?? list2.Count;
        SpotiFlacPlaylistInfo playlistInfo = new SpotiFlacPlaylistInfo(new SpotiFlacPlaylistTracks(total2), new SpotiFlacPlaylistFollowers(total), new SpotiFlacPlaylistOwner(displayName, name, images), cover, description, null);
        return new SpotiFlacPlaylistPayload(playlistInfo, list2);
    }

    private static List<SpotiFlacAlbumTrackMetadata> BuildSpotiFlacPlaylistTracks(List<JsonElement> items)
    {
        List<SpotiFlacAlbumTrackMetadata> list = new List<SpotiFlacAlbumTrackMetadata>();
        foreach (JsonElement item in items)
        {
            SpotiFlacAlbumTrackMetadata? track = TryBuildSpotiFlacPlaylistTrack(item);
            if (track != null)
            {
                list.Add(track);
            }
        }
        return list;
    }

    private static SpotiFlacAlbumTrackMetadata? TryBuildSpotiFlacPlaylistTrack(JsonElement item)
    {
        if (!TryGetNested(item, out JsonElement value, ItemV2Key, DataKey))
        {
            return null;
        }
        JsonElement artistsData = TryGetNested(value, out JsonElement nestedArtists, ArtistsKey) ? nestedArtists : default(JsonElement);
        List<string> artistNames = ExtractSpotiFlacArtistNames(artistsData);
        List<string> artistIds = ExtractSpotiFlacArtistIds(artistsData);
        string trackId = TryGetString(value, "id") ?? ExtractIdFromUri(TryGetString(value, "uri")) ?? string.Empty;
        if (!LooksLikeSpotifyTrackId(trackId))
        {
            return null;
        }
        string albumName = string.Empty;
        string albumId = string.Empty;
        string albumArtist = string.Empty;
        string? imageUrl = null;
        if (TryGetNested(value, out JsonElement albumOfTrack, AlbumOfTrackKey))
        {
            albumName = TryGetString(albumOfTrack, "name") ?? string.Empty;
            albumId = ExtractIdFromUri(TryGetString(albumOfTrack, "uri")) ?? TryGetString(albumOfTrack, "id") ?? string.Empty;
            List<string> albumArtistNames = TryGetNested(albumOfTrack, out JsonElement albumArtistsNode, ArtistsKey) ? ExtractSpotiFlacArtistNames(albumArtistsNode) : new List<string>();
            albumArtist = albumArtistNames.Count == 0 ? string.Empty : string.Join(", ", albumArtistNames);
            if (TryGetNested(albumOfTrack, out JsonElement coverArt, CoverArtKey))
            {
                imageUrl = SelectSpotiFlacTrackCover(ExtractSpotiFlacCoverUrls(coverArt));
            }
        }
        string primaryArtistId = artistIds.Count > 0 ? artistIds[0] : string.Empty;
        return new SpotiFlacAlbumTrackMetadata(
            trackId,
            artistNames.Count == 0 ? string.Empty : string.Join(", ", artistNames),
            TryGetString(value, "name") ?? SpotifyTrackFallbackTitle,
            albumName,
            albumArtist,
            ParseSpotiFlacDuration(FormatSpotiFlacDuration(TryGetInt(value, "trackDuration", TotalMillisecondsKey).GetValueOrDefault())),
            imageUrl,
            string.Empty,
            0,
            0,
            1,
            0,
            BuildSpotifyUrl(TrackType, trackId),
            ExtractIsrc(value),
            null,
            albumId,
            BuildSpotifyUrl(AlbumType, albumId),
            primaryArtistId,
            BuildSpotifyUrl(ArtistType, primaryArtistId),
            BuildSpotiFlacArtistsData(artistIds),
            null);
    }

    private static bool LooksLikeSpotifyTrackId(string? value)
    {
        return LooksLikeSpotifyEntityId(value);
    }

    private static List<string> ExtractSpotiFlacArtistNames(JsonElement artistsData)
    {
        List<string> list = new List<string>();
        if (!TryGetNested(artistsData, out var value, ItemsKey) || value.ValueKind != JsonValueKind.Array)
        {
            return list;
        }
        foreach (JsonElement item in value.EnumerateArray())
        {
            string? text = TryGetString(item, ProfileKey, "name") ?? TryGetString(item, "name");
            if (!string.IsNullOrWhiteSpace(text))
            {
                list.Add(text);
            }
        }
        return list;
    }

    private static List<string> ExtractSpotiFlacArtistIds(JsonElement artistsData)
    {
        List<string> list = new List<string>();
        if (!TryGetNested(artistsData, out var value, ItemsKey) || value.ValueKind != JsonValueKind.Array)
        {
            return list;
        }
        list.AddRange(from item in value.EnumerateArray()
                      select ExtractIdFromUri(TryGetString(item, "uri")) into id
                      where !string.IsNullOrWhiteSpace(id)
                      select (id));
        return list;
    }

    private static List<SpotiFlacArtistSimple> BuildSpotiFlacArtistsData(List<string> artistIds)
    {
        List<SpotiFlacArtistSimple> list = new List<SpotiFlacArtistSimple>(artistIds.Count);
        foreach (string artistId in artistIds)
        {
            list.Add(new SpotiFlacArtistSimple(artistId, string.Empty, BuildSpotifyUrl(ArtistType, artistId)));
        }
        return list;
    }

    private static string? ExtractSpotiFlacOwnerAvatar(JsonElement playlistUnion)
    {
        return SelectPreferredAvatarUrl(EnumerateOwnerAvatarSources(playlistUnion));
    }

    private static IEnumerable<JsonElement> EnumerateOwnerAvatarSources(JsonElement playlistUnion)
    {
        if (!TryGetNested(playlistUnion, out JsonElement ownerData, "ownerV2", DataKey))
        {
            return Enumerable.Empty<JsonElement>();
        }
        if (!TryGetNested(ownerData, out JsonElement avatar, "avatar"))
        {
            return Enumerable.Empty<JsonElement>();
        }
        if (!TryGetNested(avatar, out JsonElement sources, SourcesKey) || sources.ValueKind != JsonValueKind.Array)
        {
            return Enumerable.Empty<JsonElement>();
        }
        return sources.EnumerateArray().ToList();
    }

    private static string? SelectPreferredAvatarUrl(IEnumerable<JsonElement> sources)
    {
        string? fallback = null;
        foreach (JsonElement source in sources)
        {
            string? url = TryGetString(source, "url");
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }
            int width = TryGetImageWidth(source);
            if (width == 300)
            {
                return url;
            }
            fallback ??= url;
        }
        return fallback;
    }

    private static int TryGetImageWidth(JsonElement source)
    {
        if (source.TryGetProperty("width", out JsonElement width) && width.ValueKind == JsonValueKind.Number)
        {
            return width.GetInt32();
        }
        if (source.TryGetProperty("maxWidth", out JsonElement maxWidth) && maxWidth.ValueKind == JsonValueKind.Number)
        {
            return maxWidth.GetInt32();
        }
        return 0;
    }

    private static int ExtractSpotiFlacFollowers(JsonElement playlistUnion)
    {
        if (!TryGetNested(playlistUnion, out var value, FollowersKey))
        {
            return 0;
        }
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(TotalCountKey, out var value2) && value2.ValueKind == JsonValueKind.Number)
        {
            return value2.GetInt32();
        }
        return (value.ValueKind == JsonValueKind.Number) ? value.GetInt32() : 0;
    }

    private static string? ExtractSpotiFlacPlaylistCover(JsonElement playlistUnion)
    {
        if (TryGetNested(playlistUnion, out var value, ImagesKey))
        {
            string? text = ExtractSpotiFlacCoverFromImages(value);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }
        if (TryGetNested(playlistUnion, out var value2, "imagesV2"))
        {
            return ExtractSpotiFlacCoverUrls(value2)?.Medium;
        }
        return null;
    }

    private static string? ExtractSpotiFlacCoverFromImages(JsonElement images)
    {
        if (TryGetNested(images, out var value, ItemsKey) && value.ValueKind == JsonValueKind.Array)
        {
            using JsonElement.ArrayEnumerator arrayEnumerator = value.EnumerateArray();
            if (arrayEnumerator.MoveNext())
            {
                SpotiFlacCoverUrls? spotiFlacCoverUrls = ExtractSpotiFlacCoverUrls(arrayEnumerator.Current);
                if (spotiFlacCoverUrls.HasValue && !string.IsNullOrWhiteSpace(spotiFlacCoverUrls.Value.Medium))
                {
                    return spotiFlacCoverUrls.Value.Medium;
                }
            }
        }
        return ExtractSpotiFlacCoverUrls(images)?.Medium;
    }

    private static SpotiFlacCoverUrls? ExtractSpotiFlacCoverUrls(JsonElement coverData)
    {
        List<ImageSource> list = FindImageSources(coverData);
        if (list.Count == 0)
        {
            return null;
        }
        List<ImageSource> list2 = list.Where((ImageSource source) => (source.Width > 64 && source.Height > 64) || (source.Width == 0 && source.Height == 0 && !string.IsNullOrWhiteSpace(source.Url))).ToList();
        if (list2.Count == 0)
        {
            return null;
        }
        list2.Sort((ImageSource a, ImageSource b) => a.Width.CompareTo(b.Width));
        string? text = null;
        string? text2 = null;
        string? text3 = null;
        foreach (ImageSource item in list2)
        {
            if (item.Width == 300)
            {
                text = item.Url;
            }
            else if (item.Width == 640)
            {
                text2 = item.Url;
            }
            else if (item.Width == 0 && text3 == null)
            {
                text3 = item.Url;
            }
        }
        string? text4 = BuildLargeCoverUrl(list2);
        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(text2) && string.IsNullOrWhiteSpace(text4) && !string.IsNullOrWhiteSpace(text3))
        {
            text = text3;
            text2 = text3;
            text4 = text3;
        }
        return new SpotiFlacCoverUrls(text, text2, text4);
    }

    private static string? SelectSpotiFlacTrackCover(SpotiFlacCoverUrls? coverUrls)
    {
        if (!coverUrls.HasValue)
        {
            return null;
        }
        if (!string.IsNullOrWhiteSpace(coverUrls.Value.Small))
        {
            return coverUrls.Value.Small;
        }

        return !string.IsNullOrWhiteSpace(coverUrls.Value.Medium)
            ? coverUrls.Value.Medium
            : coverUrls.Value.Large;
    }

    private static string FormatSpotiFlacDuration(int milliseconds)
    {
        if (milliseconds <= 0)
        {
            return string.Empty;
        }
        int num = milliseconds / 1000;
        int value = num / 60;
        int value2 = num % 60;
        return $"{value}:{value2:00}";
    }

    private static int ParseSpotiFlacDuration(string? duration)
    {
        if (string.IsNullOrWhiteSpace(duration))
        {
            return 0;
        }
        string[] array = duration.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (array.Length != 2)
        {
            return 0;
        }
        int result;
        int result2;
        return (int.TryParse(array[0], out result) && int.TryParse(array[1], out result2)) ? ((result * 60 + result2) * 1000) : 0;
    }

    private static SpotifyTrackSummary? TryParsePlaylistItemTrack(JsonElement item, out JsonElement trackData)
    {
        foreach (JsonElement item2 in EnumeratePlaylistTrackCandidates(item))
        {
            if (TryParsePlaylistTrackCandidate(item2, out SpotifyTrackSummary? spotifyTrackSummary))
            {
                trackData = item2;
                return spotifyTrackSummary;
            }
        }
        trackData = default(JsonElement);
        return null;
    }

    private static IEnumerable<JsonElement> EnumeratePlaylistTrackCandidates(JsonElement item)
    {
        if (TryGetNested(item, out var value, ItemV2Key, DataKey))
        {
            yield return value;
        }
        if (TryGetNested(item, out value, "item", DataKey))
        {
            yield return value;
        }
        if (TryGetNested(item, out value, "item", TrackType))
        {
            yield return value;
        }
        if (TryGetNested(item, out value, TrackType))
        {
            yield return value;
        }
        if (TryGetNested(item, out value, TrackUnionKey))
        {
            yield return value;
        }
    }

    private static bool TryParsePlaylistTrackCandidate(JsonElement item, out SpotifyTrackSummary? summary)
    {
        foreach (JsonElement candidate in EnumerateNestedPlaylistTracks(item))
        {
            summary = ParseTrackSummary(candidate);
            if (summary is not null)
            {
                return true;
            }
        }
        summary = null;
        return false;
    }

    private static IEnumerable<JsonElement> EnumerateNestedPlaylistTracks(JsonElement item)
    {
        if (TryGetNested(item, out var value, TrackUnionKey))
        {
            yield return value;
        }
        if (TryGetNested(item, out value, TrackType))
        {
            yield return value;
        }
        yield return item;
    }

    private static string? ExtractArtistsFromItems(JsonElement root, string property)
    {
        List<string> list = new List<string>();
        if (TryGetNested(root, out var value, property) || TryGetNested(root, out value, "artistsV2"))
        {
            AppendArtistsFromList(value, list);
        }
        if (list.Count == 0 && TryGetNested(root, out var value2, "firstArtist"))
        {
            AppendArtistsFromList(value2, list);
        }
        if (list.Count == 0 && TryGetNested(root, out var value3, "otherArtists"))
        {
            AppendArtistsFromList(value3, list);
        }
        if (list.Count == 0 && TryGetNested(root, out var value4, AlbumOfTrackKey) && (TryGetNested(value4, out var value5, ArtistsKey) || TryGetNested(value4, out value5, "artistsV2")))
        {
            AppendArtistsFromList(value5, list);
        }
        if (list.Count == 0)
        {
            string? text = TryGetString(root, ArtistNameKey) ?? TryGetString(root, ArtistType, "name");
            if (!string.IsNullOrWhiteSpace(text))
            {
                list.Add(text);
            }
        }
        return (list.Count == 0) ? null : string.Join(", ", list);
    }

    private static void AppendArtistsFromList(JsonElement artists, List<string> names)
    {
        if (TryResolveArtistItems(artists, out var items))
        {
            names.AddRange(from name in items.EnumerateArray().Select(TryGetArtistName)
                           where !string.IsNullOrWhiteSpace(name)
                           select (name));
        }
    }

    private static string? TryGetArtistName(JsonElement artist)
    {
        string? text = TryGetString(artist, ProfileKey, "name");
        if (!string.IsNullOrWhiteSpace(text))
        {
            return text;
        }
        text = TryGetString(artist, "name");
        if (!string.IsNullOrWhiteSpace(text))
        {
            return text;
        }
        return TryGetString(artist, ArtistNameKey);
    }

    private static bool TryResolveArtistItems(JsonElement artists, out JsonElement items)
    {
        if (TryGetNested(artists, out items, ItemsKey) && items.ValueKind == JsonValueKind.Array)
        {
            return true;
        }
        if (TryGetNested(artists, out items, "nodes") && items.ValueKind == JsonValueKind.Array)
        {
            return true;
        }
        if (artists.ValueKind == JsonValueKind.Array)
        {
            items = artists;
            return true;
        }
        if (TryGetNested(artists, out var value, ItemsKey, ItemsKey) && value.ValueKind == JsonValueKind.Array)
        {
            items = value;
            return true;
        }
        items = default(JsonElement);
        return false;
    }

    private static string? ExtractCoverUrl(JsonElement root, params string[] path)
    {
        if (!TryGetNested(root, out var value, path))
        {
            return null;
        }
        List<ImageSource> list = FindImageSources(value);
        if (list.Count == 0)
        {
            return null;
        }
        string? text = BuildLargeCoverUrl(list);
        if (!string.IsNullOrWhiteSpace(text))
        {
            return text;
        }
        return (from source in list
                orderby Math.Max(source.Width, source.Height) descending
                select source.Url).FirstOrDefault((string url) => !string.IsNullOrWhiteSpace(url));
    }

    private static string? ExtractCoverOrDirectImageUrl(JsonElement root, params string[] path)
    {
        string? cover = ExtractCoverUrl(root, path);
        if (!string.IsNullOrWhiteSpace(cover))
        {
            return cover;
        }

        if (!TryGetNested(root, out var value, path))
        {
            return null;
        }

        return ExtractDirectImageUrl(value);
    }

    private static string? ExtractDirectImageUrl(JsonElement node)
    {
        return node.ValueKind switch
        {
            JsonValueKind.String => NormalizeImageUrlCandidate(node.GetString()),
            JsonValueKind.Array => ExtractDirectImageUrlFromArray(node),
            JsonValueKind.Object => ExtractDirectImageUrlFromObject(node),
            _ => null
        };
    }

    private static string? ExtractDirectImageUrlFromArray(JsonElement node)
    {
        foreach (JsonElement item in node.EnumerateArray())
        {
            string? image = ExtractDirectImageUrl(item);
            if (!string.IsNullOrWhiteSpace(image))
            {
                return image;
            }
        }
        return null;
    }

    private static string? ExtractDirectImageUrlFromObject(JsonElement node)
    {
        string? direct = TryExtractDirectImageUrlCandidate(node);
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        string? sourceUrl = TryExtractSourceImageUrl(node);
        if (!string.IsNullOrWhiteSpace(sourceUrl))
        {
            return sourceUrl;
        }

        foreach (JsonProperty property in node.EnumerateObject())
        {
            string? nested = ExtractDirectImageUrl(property.Value);
            if (!string.IsNullOrWhiteSpace(nested))
            {
                return nested;
            }
        }

        return null;
    }

    private static string? TryExtractDirectImageUrlCandidate(JsonElement node)
    {
        string? candidate = TryGetString(node, "url")
            ?? TryGetString(node, "imageUrl")
            ?? TryGetString(node, "image_url")
            ?? TryGetString(node, "src")
            ?? TryGetString(node, "picture")
            ?? TryGetString(node, "picture_xl")
            ?? TryGetString(node, "picture_big")
            ?? TryGetString(node, "picture_medium");
        return NormalizeImageUrlCandidate(candidate);
    }

    private static string? TryExtractSourceImageUrl(JsonElement node)
    {
        if (!TryGetNested(node, out var sources, SourcesKey) || sources.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement source in sources.EnumerateArray())
        {
            string? sourceUrl = NormalizeImageUrlCandidate(TryGetString(source, "url"));
            if (!string.IsNullOrWhiteSpace(sourceUrl))
            {
                return sourceUrl;
            }
        }

        return null;
    }

    private static string? NormalizeImageUrlCandidate(string? value)
    {
        return LooksLikeImageUrl(value) ? value : null;
    }

    private static bool LooksLikeImageUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
        }

        return false;
    }

    private static string? ExtractPlaylistImageUrl(JsonElement playlistUnion)
    {
        if (TryGetNested(playlistUnion, out var value, ImagesKey, ItemsKey) && value.ValueKind == JsonValueKind.Array)
        {
            string? text = (from image in value.EnumerateArray()
                            select ExtractCoverUrl(image)).FirstOrDefault((string url) => !string.IsNullOrWhiteSpace(url));
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }
        if (TryGetNested(playlistUnion, out var value2, "imagesV2"))
        {
            string? text2 = ExtractCoverUrl(value2);
            if (!string.IsNullOrWhiteSpace(text2))
            {
                return text2;
            }
        }
        return null;
    }

    private static string? ExtractArtistImageUrl(JsonElement artistUnion)
    {
        var candidates = new[]
        {
            ExtractCoverOrDirectImageUrl(artistUnion, VisualsKey, AvatarImageKey),
            ExtractCoverOrDirectImageUrl(artistUnion, AvatarImageKey),
            ExtractCoverOrDirectImageUrl(artistUnion, HeaderImageKey, DataKey),
            ExtractCoverOrDirectImageUrl(artistUnion, VisualsKey, HeaderImageKey),
            ExtractCoverOrDirectImageUrl(artistUnion, VisualsKey, "heroImage"),
            ExtractCoverOrDirectImageUrl(artistUnion, VisualsKey, "bannerImage"),
            ExtractCoverOrDirectImageUrl(artistUnion, ImagesKey),
            ExtractCoverOrDirectImageUrl(artistUnion, "imagesV2"),
            ExtractCoverOrDirectImageUrl(artistUnion, ImageKey)
        };

        return candidates.FirstOrDefault(static candidate => !string.IsNullOrWhiteSpace(candidate));
    }

    private static string? ExtractArtistHeaderImageUrl(JsonElement artistUnion)
    {
        if (TryGetNested(artistUnion, out var value, HeaderImageKey, DataKey))
        {
            string? text = ExtractCoverUrl(value);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }
        if (TryGetNested(artistUnion, out var value2, VisualsKey, HeaderImageKey))
        {
            string? text2 = ExtractCoverUrl(value2);
            if (!string.IsNullOrWhiteSpace(text2))
            {
                return text2;
            }
        }
        if (TryGetNested(artistUnion, out var value3, VisualsKey, "heroImage"))
        {
            string? text3 = ExtractCoverUrl(value3);
            if (!string.IsNullOrWhiteSpace(text3))
            {
                return text3;
            }
        }
        if (TryGetNested(artistUnion, out var value4, VisualsKey, "bannerImage"))
        {
            string? text4 = ExtractCoverUrl(value4);
            if (!string.IsNullOrWhiteSpace(text4))
            {
                return text4;
            }
        }
        return null;
    }

    private static List<string> ExtractArtistGalleryUrls(JsonElement artistUnion)
    {
        List<string> list = new List<string>();
        if (TryGetNested(artistUnion, out var value, VisualsKey, "gallery"))
        {
            AppendGalleryUrls(list, value);
        }
        if (list.Count == 0 && TryGetNested(artistUnion, out var value2, VisualsKey, "galleryImages"))
        {
            AppendGalleryUrls(list, value2);
        }
        if (list.Count == 0 && TryGetNested(artistUnion, out var value3, "galleryImages"))
        {
            AppendGalleryUrls(list, value3);
        }
        return list.Where((string url) => !string.IsNullOrWhiteSpace(url)).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AppendGalleryUrls(List<string> urls, JsonElement gallery)
    {
        if (gallery.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in gallery.EnumerateArray())
            {
                AppendGalleryItem(urls, item);
            }
            return;
        }
        if (gallery.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        if (gallery.TryGetProperty(ItemsKey, out var value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item2 in value.EnumerateArray())
            {
                AppendGalleryItem(urls, item2);
            }
            return;
        }
        if (gallery.TryGetProperty(SourcesKey, out var value2))
        {
            AppendGalleryItem(urls, value2);
        }
    }

    private static void AppendGalleryItem(List<string> urls, JsonElement item)
    {
        string? text = ExtractCoverUrl(item);
        if (!string.IsNullOrWhiteSpace(text))
        {
            urls.Add(text);
        }
    }

    private static string? TryGetOwnerName(JsonElement playlistUnion)
    {
        if (!TryGetNested(playlistUnion, out var value, "ownerV2", DataKey))
        {
            return null;
        }
        return TryGetString(value, "name");
    }

    private static int? TryGetFollowers(JsonElement playlistUnion)
    {
        if (!TryGetNested(playlistUnion, out var value, FollowersKey))
        {
            return null;
        }
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(TotalCountKey, out var value2))
        {
            return (value2.ValueKind == JsonValueKind.Number) ? new int?(value2.GetInt32()) : ((int?)null);
        }
        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.GetInt32();
        }
        return null;
    }

    private static DiscographyRelease? ExtractDiscographyRelease(JsonElement item)
    {
        DiscographyRelease? primaryRelease = ParseRelease(item);
        if (primaryRelease is not null && !string.IsNullOrWhiteSpace(primaryRelease.ReleaseType))
        {
            return primaryRelease;
        }
        DiscographyRelease? nestedRelease = TryExtractNestedDiscographyRelease(item);
        return nestedRelease ?? primaryRelease;
    }

    private static DiscographyRelease? TryExtractNestedDiscographyRelease(JsonElement item)
    {
        if (TryGetNested(item, out var value, "releases", ItemsKey) && value.ValueKind == JsonValueKind.Array)
        {
            DiscographyRelease? preferred = null;
            foreach (DiscographyRelease candidate in from parsed in value.EnumerateArray().Select(ParseRelease)
                                                     where (object)parsed != null
                                                     select parsed)
            {
                preferred = PickPreferredDiscographyRelease(preferred, candidate);
            }
            if (preferred is not null)
            {
                return preferred;
            }
        }

        foreach (JsonElement candidate in EnumerateNestedReleaseCandidates(item))
        {
            DiscographyRelease? parsed = ParseRelease(candidate);
            if (parsed is not null)
            {
                return parsed;
            }
        }

        return null;
    }

    private static IEnumerable<JsonElement> EnumerateNestedReleaseCandidates(JsonElement item)
    {
        if (TryGetNested(item, out var value, ContentKey))
        {
            yield return value;
        }
        if (TryGetNested(item, out value, DataKey))
        {
            yield return value;
        }
        if (TryGetNested(item, out value, ItemV2Key, DataKey))
        {
            yield return value;
        }
        if (TryGetNested(item, out value, AlbumType))
        {
            yield return value;
        }
    }

    private static DiscographyRelease? PickPreferredDiscographyRelease(DiscographyRelease? current, DiscographyRelease candidate)
    {
        if (current is null)
        {
            return candidate;
        }
        int num = Score(current.ReleaseType);
        int num2 = Score(candidate.ReleaseType);
        if (num2 > num)
        {
            return candidate;
        }
        if (num2 == num && !string.IsNullOrWhiteSpace(candidate.ReleaseDate) && (string.IsNullOrWhiteSpace(current.ReleaseDate) || string.CompareOrdinal(candidate.ReleaseDate, current.ReleaseDate) > 0))
        {
            return candidate;
        }
        return current;
        static int Score(string? releaseType)
        {
            return releaseType switch
            {
                ReleaseTypeSingle => 4,
                "EP" => 3,
                ReleaseTypeCompilation => 2,
                ReleaseTypeAlbum => 1,
                _ => 0,
            };
        }
    }

    private static DiscographyRelease? ParseRelease(JsonElement release)
    {
        string? text = TryGetString(release, "id") ?? ExtractIdFromUri(TryGetString(release, "uri"));
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        string? name = TryGetString(release, "name");
        string? artists = ExtractArtistsFromItems(release, ArtistsKey);
        string? imageUrl = ExtractCoverUrl(release, CoverArtKey);
        string? releaseDate = TryGetString(release, "date", IsoStringKey) ?? TryGetString(release, "date", "year") ?? TryGetString(release, ReleaseDateKey) ?? TryGetString(release, ReleaseDateSnakeKey);
        string? releaseType = NormalizeReleaseType(TryGetString(release, "type") ?? TryGetString(release, "type", "name") ?? TryGetString(release, "type", "value") ?? TryGetString(release, "releaseType") ?? TryGetString(release, "albumType") ?? TryGetString(release, "album_type") ?? TryGetString(release, "albumGroup") ?? TryGetString(release, "album_group") ?? TryGetString(release, "__typename"));
        string albumGroup = MapReleaseTypeToAlbumGroup(releaseType);
        int? totalTracks = TryGetInt(release, TracksKey, TotalCountKey) ?? TryGetInt(release, TracksKey, CountKey) ?? TryGetInt(release, "trackCount") ?? TryGetInt(release, "totalTracks") ?? TryGetInt(release, "total_tracks");
        return new DiscographyRelease(text, name, artists, imageUrl, releaseDate, releaseType, albumGroup, totalTracks);
    }

    private static string? NormalizeReleaseType(string? releaseType)
    {
        string text = (releaseType ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        text = text.ToUpperInvariant();
        if (text.Contains(ReleaseTypeSingle, StringComparison.Ordinal))
        {
            return ReleaseTypeSingle;
        }
        if (text.Contains("EP", StringComparison.Ordinal))
        {
            return "EP";
        }
        if (text.Contains(ReleaseTypeCompilation, StringComparison.Ordinal))
        {
            return ReleaseTypeCompilation;
        }
        if (text.Contains(ReleaseTypeAlbum, StringComparison.Ordinal))
        {
            return ReleaseTypeAlbum;
        }
        return text;
    }

    private static string MapReleaseTypeToAlbumGroup(string? releaseType)
    {
        return releaseType switch
        {
            "EP" => "ep",
            ReleaseTypeSingle => "single",
            ReleaseTypeCompilation => "compilation",
            ReleaseTypeAlbum => AlbumType,
            _ => AlbumType,
        };
    }

    private static List<ImageSource> FindImageSources(JsonElement cover)
    {
        if (TryGetNested(cover, out var value, SourcesKey) && value.ValueKind == JsonValueKind.Array)
        {
            return ParseSources(value);
        }
        if (TryGetNested(cover, out var value2, "squareCoverImage", ImageKey, DataKey, SourcesKey) && value2.ValueKind == JsonValueKind.Array)
        {
            return ParseSources(value2);
        }
        if (TryGetNested(cover, out var value3, DataKey, SourcesKey) && value3.ValueKind == JsonValueKind.Array)
        {
            return ParseSources(value3);
        }
        return new List<ImageSource>();
    }

    private static List<ImageSource> ParseSources(JsonElement sources)
    {
        List<ImageSource> list = new List<ImageSource>();
        foreach (JsonElement item in sources.EnumerateArray())
        {
            if (TryParseImageSource(item, out ImageSource? source) && source is not null)
            {
                list.Add(source);
            }
        }
        return list;
    }

    private static bool TryParseImageSource(JsonElement item, out ImageSource? source)
    {
        string? text = TryGetString(item, "url");
        if (string.IsNullOrWhiteSpace(text))
        {
            source = null;
            return false;
        }

        int width = TryReadImageDimension(item, "width", "maxWidth");
        int height = TryReadImageDimension(item, "height", "maxHeight");
        source = new ImageSource(text, width, height);
        return true;
    }

    private static int TryReadImageDimension(JsonElement item, string primaryProperty, string fallbackProperty)
    {
        if (item.TryGetProperty(primaryProperty, out var value) && value.ValueKind == JsonValueKind.Number)
        {
            return value.GetInt32();
        }
        if (item.TryGetProperty(fallbackProperty, out var fallbackValue) && fallbackValue.ValueKind == JsonValueKind.Number)
        {
            return fallbackValue.GetInt32();
        }
        return 0;
    }

    private static void EnumerateObjectChildren(JsonElement element, Action<JsonElement> visitor)
    {
        foreach (JsonProperty item in element.EnumerateObject())
        {
            visitor(item.Value);
        }
    }

    private static string? BuildLargeCoverUrl(List<ImageSource> sources)
    {
        string? text = ExtractImageId(sources);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        return $"{HttpsScheme}://i.scdn.co/image/ab67616d000082c1{text}";
    }

    private static string? ExtractImageId(List<ImageSource> sources)
    {
        return sources.Select((ImageSource source) => ExtractImageIdFromUrl(source.Url)).FirstOrDefault((string? id) => !string.IsNullOrWhiteSpace(id));
    }

    private static string? ExtractImageIdFromUrl(string url)
    {
        string[] array = new string[3] { "ab67616d0000b273", "ab67616d00001e02", "ab67616d00004851" };
        string[] array2 = array;
        foreach (string text in array2)
        {
            int num = url.IndexOf(text, StringComparison.OrdinalIgnoreCase);
            if (num >= 0)
            {
                string text3 = url[(num + text.Length)..];
                return text3.TrimStart('/');
            }
        }
        int num3 = url.IndexOf("/image/", StringComparison.OrdinalIgnoreCase);
        if (num3 >= 0)
        {
            string text4 = url[(num3 + "/image/".Length)..];
            int num4 = text4.IndexOf('?', StringComparison.Ordinal);
            if (num4 >= 0)
            {
                text4 = text4.Substring(0, num4);
            }
            if (!string.IsNullOrWhiteSpace(text4))
            {
                string[] array3 = array;
                foreach (string text5 in array3)
                {
                    int num5 = text4.IndexOf(text5, StringComparison.OrdinalIgnoreCase);
                    if (num5 >= 0)
                    {
                        string text6 = text4[(num5 + text5.Length)..];
                        return text6.TrimStart('/');
                    }
                }
            }
        }
        return null;
    }

    private static string? ExtractIsrc(JsonElement trackData)
    {
        if (TryGetNested(trackData, out var value, "externalIds"))
        {
            if (value.TryGetProperty("isrc", out var value2) && value2.ValueKind == JsonValueKind.String)
            {
                return value2.GetString();
            }
            if (value.TryGetProperty(ItemsKey, out var value3) && value3.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in value3.EnumerateArray())
                {
                    string? a = TryGetString(item, "type");
                    string? text = TryGetString(item, "value");
                    if (string.Equals(a, "ISRC", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }
            }
        }
        return null;
    }

    private static string BuildSpotifyUrl(string type, string id)
    {
        return "https://open.spotify.com/" + type + "/" + id;
    }

    private static string? ExtractIdFromUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return null;
        }
        string[] array = uri.Split(':', StringSplitOptions.RemoveEmptyEntries);
        return (array.Length != 0) ? array[^1] : null;
    }

    private static bool TryGetNested(JsonElement root, out JsonElement value, params string[] path)
    {
        value = root;
        foreach (string propertyName in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(propertyName, out var value2))
            {
                value = default(JsonElement);
                return false;
            }
            value = value2;
        }
        return true;
    }

    private static JsonElement? GetArray(JsonElement root, params string[] path)
    {
        if (!TryGetNested(root, out var value, path))
        {
            return null;
        }
        return (value.ValueKind == JsonValueKind.Array) ? new JsonElement?(value) : ((JsonElement?)null);
    }

    private static string? TryGetString(JsonElement root, params string[] path)
    {
        if (!TryGetNested(root, out var value, path))
        {
            return null;
        }
        return (value.ValueKind == JsonValueKind.String) ? value.GetString() : null;
    }

    private static string? ExtractYearFromDate(JsonElement element)
    {
        if (!TryGetNested(element, out var value, "date"))
        {
            return null;
        }
        if (value.TryGetProperty("year", out var value2))
        {
            if (value2.ValueKind == JsonValueKind.Number)
            {
                return value2.GetInt32().ToString();
            }
            if (value2.ValueKind == JsonValueKind.String)
            {
                return value2.GetString();
            }
        }
        return null;
    }

    private static string? ExtractYearFromDate(JsonElement element, params string[] path)
    {
        if (path == null || path.Length == 0)
        {
            return ExtractYearFromDate(element);
        }
        if (!TryGetNested(element, out var value, path))
        {
            return null;
        }
        return ExtractYearFromDate(value);
    }

    private static List<string>? ExtractStringArray(JsonElement root, params string[] path)
    {
        JsonElement? array = GetArray(root, path);
        if (!array.HasValue)
        {
            return null;
        }
        List<string> list = (from value in array.Value.EnumerateArray()
                             where value.ValueKind == JsonValueKind.String
                             select value.GetString() into value
                             where !string.IsNullOrWhiteSpace(value)
                             select (value)).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
        return (list.Count == 0) ? null : list;
    }

    private static JsonElement? TryGetObject(JsonElement root, params string[] path)
    {
        if (!TryGetNested(root, out var value, path))
        {
            return null;
        }
        return (value.ValueKind == JsonValueKind.Object) ? new JsonElement?(value) : ((JsonElement?)null);
    }

    private static int? TryGetInt(JsonElement root, params string[] path)
    {
        if (!TryGetNested(root, out var value, path))
        {
            return null;
        }
        return (value.ValueKind == JsonValueKind.Number) ? new int?(value.GetInt32()) : ((int?)null);
    }

    private static bool? TryGetBool(JsonElement root, params string[] path)
    {
        if (!TryGetNested(root, out var value, path))
        {
            return null;
        }
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static ParsedSpotifyUrl? ParseSpotifyUrl(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }
        string text = input.Trim();
        if (text.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase))
        {
            string[] array = text.Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (array.Length >= 3)
            {
                return new ParsedSpotifyUrl(array[1], array[2]);
            }
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? result))
        {
            return null;
        }
        string host = result.Host;
        if (host != "open.spotify.com" && host != "play.spotify.com")
        {
            return null;
        }
        string[] array2 = result.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (array2.Length < 2)
        {
            return null;
        }
        string text2 = array2[0].ToLowerInvariant();
        string id = array2[1];
        bool flag;
        switch (text2)
        {
            case TrackType:
            case AlbumType:
            case PlaylistType:
            case ArtistType:
            case ShowType:
            case EpisodeType:
                flag = true;
                break;
            default:
                flag = false;
                break;
        }
        return flag ? new ParsedSpotifyUrl(text2, id) : null;
    }

    private static PersistedQueryOverride GetPersistedQuery(string operationName, int defaultVersion, string defaultHash)
    {
        EnsurePathfinderOverridesLoaded();
        if (PathfinderOverrides.TryGetValue(operationName, out PersistedQueryOverride? value) && value is not null && value.Version > 0 && !string.IsNullOrWhiteSpace(value.Sha256Hash))
        {
            return value;
        }
        return new PersistedQueryOverride(defaultVersion, defaultHash, null);
    }

    private static void EnsurePathfinderOverridesLoaded()
    {
        string? path = ResolvePathfinderOverridesPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }
        DateTime lastWriteTimeUtc = File.GetLastWriteTimeUtc(path);
        DateTimeOffset dateTimeOffset = new DateTimeOffset(lastWriteTimeUtc, TimeSpan.Zero);
        if (dateTimeOffset <= _pathfinderOverridesStamp)
        {
            return;
        }
        lock (PathfinderOverridesLock)
        {
            if (dateTimeOffset <= _pathfinderOverridesStamp)
            {
                return;
            }
            try
            {
                if (!TryLoadPathfinderOverrides(path, out Dictionary<string, PersistedQueryOverride> overrides))
                {
                    return;
                }
                PathfinderOverrides.Clear();
                foreach (KeyValuePair<string, PersistedQueryOverride> item in overrides)
                {
                    PathfinderOverrides[item.Key] = item.Value;
                }
                _pathfinderOverridesStamp = dateTimeOffset;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                // Keep the last successfully loaded overrides if refresh fails.
            }
        }
    }

    private static bool TryLoadPathfinderOverrides(string path, out Dictionary<string, PersistedQueryOverride> overrides)
    {
        overrides = new Dictionary<string, PersistedQueryOverride>(StringComparer.OrdinalIgnoreCase);
        using JsonDocument jsonDocument = JsonDocument.Parse(File.ReadAllText(path));
        if (!jsonDocument.RootElement.TryGetProperty("operations", out JsonElement operations) || operations.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        Dictionary<string, PersistedQueryOverride> parsedOverrides = new Dictionary<string, PersistedQueryOverride>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty item in operations.EnumerateObject())
        {
            if (TryBuildPersistedQueryOverride(item.Value, out PersistedQueryOverride persisted))
            {
                parsedOverrides[item.Name] = persisted;
            }
        }
        overrides = parsedOverrides;
        return true;
    }

    private static bool TryBuildPersistedQueryOverride(JsonElement value, out PersistedQueryOverride persisted)
    {
        persisted = new PersistedQueryOverride(0, string.Empty, null);
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        int version = value.TryGetProperty("version", out JsonElement versionNode) && versionNode.ValueKind == JsonValueKind.Number
            ? versionNode.GetInt32()
            : 1;
        string? hash = value.TryGetProperty("sha256Hash", out JsonElement hashNode) && hashNode.ValueKind == JsonValueKind.String
            ? hashNode.GetString()
            : null;
        string? variablesJson = value.TryGetProperty("variables", out JsonElement variablesNode) && variablesNode.ValueKind == JsonValueKind.Object
            ? variablesNode.GetRawText()
            : null;
        if (string.IsNullOrWhiteSpace(hash))
        {
            return false;
        }
        persisted = new PersistedQueryOverride(version, hash, variablesJson);
        return true;
    }

    private static string? ResolvePathfinderOverridesPath()
    {
        string? configRoot = Environment.GetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(configRoot))
        {
            configRoot = Environment.GetEnvironmentVariable("DEEZSPOTAG_DATA_DIR");
        }
        return string.IsNullOrWhiteSpace(configRoot)
            ? null
            : Path.Join(configRoot, "spotify", PathfinderOverridesFileName);
    }
}

public sealed record SpotifyArtistExtras(
    string? Biography,
    bool? Verified,
    int? MonthlyListeners,
    int? Rank);

public sealed record SpotifyArtistHydratedPage(
    SpotifyArtistOverview Overview,
    SpotifyArtistExtras Extras,
    List<SpotifyTrackSummary> TopTracks,
    List<SpotifyRelatedArtist> RelatedArtists,
    List<SpotifyAlbumSummary> AppearsOn,
    List<SpotifyAlbumSummary> Albums);

public sealed record SpotifyArtistOverview(
    string Id,
    string Name,
    string? ImageUrl,
    string? HeaderImageUrl,
    List<string> Gallery,
    int? Followers,
    List<string> Genres,
    string? SourceUrl,
    int? Popularity,
    int? TotalAlbums,
    string DiscographyType,
    List<string>? PopularReleaseAlbumIds = null);

public sealed record SpotifyPathfinderAudioFeatures(
    double? Danceability,
    double? Energy,
    double? Valence,
    double? Acousticness,
    double? Instrumentalness,
    double? Speechiness,
    double? Loudness,
    double? Tempo,
    int? TimeSignature,
    double? Liveness,
    int? Key,
    int? Mode);
