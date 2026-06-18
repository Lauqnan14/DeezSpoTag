using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DeezSpoTag.Integrations.Tidal;
using DeezSpoTag.Web.Services;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/tidal/search")]
[Authorize]
public sealed class TidalSearchApiController : ControllerBase
{
    private const string TidalSource = "tidal";
    private const string TrackType = "track";
    private const string AlbumType = "album";
    private const string ArtistType = "artist";
    private const string PlaylistType = "playlist";
    private const string VideoType = "video";
    private const string AtmosType = "atmos";
    private const string TitleProperty = "title";
    private static readonly HashSet<string> AllowedTypes = new(StringComparer.Ordinal)
    {
        TrackType,
        AlbumType,
        ArtistType,
        PlaylistType,
        VideoType,
        AtmosType
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ITidalAccessTokenProvider _tidalAccessTokenProvider;
    private readonly ILogger<TidalSearchApiController> _logger;

    public TidalSearchApiController(
        IHttpClientFactory httpClientFactory,
        ITidalAccessTokenProvider tidalAccessTokenProvider,
        ILogger<TidalSearchApiController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _tidalAccessTokenProvider = tidalAccessTokenProvider;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Search(
        [FromQuery] string query,
        [FromQuery] string? type = null,
        [FromQuery] int limit = 25,
        CancellationToken cancellationToken = default)
    {
        if (!ExternalSearchControllerHelpers.TryPrepareSearchRequest(
                query,
                type,
                limit,
                out var normalizedType,
                out var normalizedLimit,
                out var errorResult,
                AllowedTypes))
        {
            return errorResult!;
        }

        try
        {
            return Ok(await BuildSearchPayloadAsync(query, normalizedType, normalizedLimit, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tidal search failed.");
            return StatusCode(StatusCodes.Status502BadGateway, new { available = false, error = "Tidal search failed." });
        }
    }

    private async Task<object> BuildSearchPayloadAsync(
        string query,
        string? normalizedType,
        int normalizedLimit,
        CancellationToken cancellationToken)
    {
        var token = await _tidalAccessTokenProvider.GetAccessTokenAsync(cancellationToken);
        var tracks = normalizedType is null or TrackType or AtmosType
            ? await SearchTypedAsync("tracks", query, normalizedLimit, token, MapTrack, cancellationToken)
            : new List<object>();
        var albums = normalizedType is null or AlbumType or AtmosType
            ? await SearchTypedAsync("albums", query, normalizedLimit, token, MapAlbum, cancellationToken)
            : new List<object>();
        var artists = normalizedType is null or ArtistType
            ? await SearchTypedAsync("artists", query, normalizedLimit, token, MapArtist, cancellationToken)
            : new List<object>();
        var playlists = normalizedType is null or PlaylistType
            ? await SearchTypedAsync("playlists", query, normalizedLimit, token, MapPlaylist, cancellationToken)
            : new List<object>();
        var videos = normalizedType is null or VideoType
            ? await SearchTypedAsync("videos", query, normalizedLimit, token, MapVideo, cancellationToken)
            : new List<object>();

        if (normalizedType == AtmosType)
        {
            tracks = tracks.Where(HasAtmosObject).ToList();
            albums = albums.Where(HasAtmosObject).ToList();
        }

        return new
        {
            available = true,
            tracks,
            albums,
            artists,
            playlists,
            videos,
            totals = ExternalSearchControllerHelpers.BuildTotals(
                tracks.Count,
                albums.Count,
                artists.Count,
                playlists.Count)
        };
    }

    private async Task<List<object>> SearchTypedAsync(
        string endpointType,
        string query,
        int limit,
        string token,
        Func<JsonElement, object?> mapper,
        CancellationToken cancellationToken)
    {
        var items = await FetchItemsAsync(endpointType, query, limit, token, cancellationToken);
        return items
            .Select(UnwrapSearchItem)
            .Select(mapper)
            .Where(static mapped => mapped != null)
            .ToList()!;
    }

    private async Task<List<JsonElement>> FetchItemsAsync(
        string endpointType,
        string query,
        int limit,
        string token,
        CancellationToken cancellationToken)
    {
        var currentToken = token;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var response = await SendSearchRequestAsync(endpointType, query, limit, currentToken, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && attempt == 0)
            {
                _tidalAccessTokenProvider.Invalidate();
                currentToken = await _tidalAccessTokenProvider.GetAccessTokenAsync(cancellationToken);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                break;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var typedItems = ExtractSearchItems(doc.RootElement, endpointType);
            if (typedItems.Count > 0)
            {
                return typedItems;
            }
        }

        return await FetchGenericSearchItemsAsync(endpointType, query, limit, currentToken, cancellationToken);
    }

    private async Task<List<JsonElement>> FetchGenericSearchItemsAsync(
        string endpointType,
        string query,
        int limit,
        string token,
        CancellationToken cancellationToken)
    {
        var currentToken = token;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var response = await SendGenericSearchRequestAsync(endpointType, query, limit, currentToken, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && attempt == 0)
            {
                _tidalAccessTokenProvider.Invalidate();
                currentToken = await _tidalAccessTokenProvider.GetAccessTokenAsync(cancellationToken);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                return new List<JsonElement>();
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return ExtractSearchItems(doc.RootElement, endpointType);
        }

        return new List<JsonElement>();
    }

    private async Task<HttpResponseMessage> SendSearchRequestAsync(
        string endpointType,
        string query,
        int limit,
        string token,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        var requestUrl =
            $"https://api.tidal.com/v1/search/{endpointType}?query={Uri.EscapeDataString(query)}&limit={limit}&offset=0&countryCode=US";
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendGenericSearchRequestAsync(
        string endpointType,
        string query,
        int limit,
        string token,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        var requestUrl =
            $"https://api.tidal.com/v1/search?query={Uri.EscapeDataString(query)}&types={Uri.EscapeDataString(ToGenericSearchType(endpointType))}&limit={limit}&offset=0&countryCode=US";
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendTidalRequestAsync(
        string requestUrl,
        string token,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, cancellationToken);
    }

    private static List<JsonElement> ExtractSearchItems(JsonElement root, string endpointType)
    {
        if (TryGetItemsArray(root, out var directItems))
        {
            return CloneArrayItems(directItems);
        }

        if (root.TryGetProperty(endpointType, out var typedNode) && TryGetItemsArray(typedNode, out var typedItems))
        {
            return CloneArrayItems(typedItems);
        }

        if (root.TryGetProperty("data", out var dataNode))
        {
            if (TryGetItemsArray(dataNode, out var dataItems))
            {
                return CloneArrayItems(dataItems);
            }

            if (dataNode.TryGetProperty(endpointType, out var dataTypedNode)
                && TryGetItemsArray(dataTypedNode, out var dataTypedItems))
            {
                return CloneArrayItems(dataTypedItems);
            }
        }

        return new List<JsonElement>();
    }

    private static bool TryGetItemsArray(JsonElement element, out JsonElement itemsElement)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            itemsElement = element;
            return true;
        }

        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("items", out itemsElement)
            && itemsElement.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        itemsElement = default;
        return false;
    }

    private static List<JsonElement> CloneArrayItems(JsonElement itemsElement)
        => itemsElement.ValueKind == JsonValueKind.Array
            ? itemsElement.EnumerateArray().Select(element => element.Clone()).ToList()
            : new List<JsonElement>();

    private static string ToGenericSearchType(string endpointType)
        => endpointType switch
        {
            "tracks" => "TRACKS",
            "albums" => "ALBUMS",
            "artists" => "ARTISTS",
            "playlists" => "PLAYLISTS",
            "videos" => "VIDEOS",
            _ => endpointType.ToUpperInvariant()
        };

    private static JsonElement UnwrapSearchItem(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("item", out var itemElement)
            && itemElement.ValueKind == JsonValueKind.Object)
        {
            return itemElement;
        }

        return element;
    }

    private static object MapTrack(JsonElement item)
    {
        var id = GetAnyString(item, "id");
        var url = GetString(item, "url");
        var artist = item.TryGetProperty("artist", out var artistNode)
            ? GetString(artistNode, "name")
            : string.Empty;
        var artistId = item.TryGetProperty("artist", out artistNode)
            ? GetAnyString(artistNode, "id")
            : string.Empty;
        var albumTitle = string.Empty;
        var coverId = string.Empty;
        if (item.TryGetProperty("album", out var albumNode))
        {
            albumTitle = GetString(albumNode, "title");
            coverId = GetString(albumNode, "cover");
        }

        if (string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(id))
        {
            url = $"https://tidal.com/browse/track/{Uri.EscapeDataString(id)}";
        }

        var duration = GetInt(item, "duration");
        var audioQuality = GetString(item, "audioQuality");
        var hasAtmos = HasAtmos(item);
        return new
        {
            source = TidalSource,
            type = TrackType,
            name = ComposeTitle(GetString(item, TitleProperty), GetString(item, "version")),
            artist,
            artistId,
            artistIds = BuildArtistIds(item, artistId),
            album = albumTitle,
            image = BuildImageUrl(coverId),
            duration,
            durationMs = Math.Max(0, duration) * 1000L,
            isrc = GetString(item, "isrc"),
            tidalId = id,
            tidalType = TrackType,
            tidalUrl = url,
            externalUrl = url,
            hasHiRes = audioQuality.Contains("HI_RES", StringComparison.OrdinalIgnoreCase),
            hasAtmos,
            audioQuality
        };
    }

    private static object MapAlbum(JsonElement item)
    {
        var id = GetAnyString(item, "id");
        var url = GetString(item, "url");
        var artist = item.TryGetProperty("artist", out var artistNode)
            ? GetString(artistNode, "name")
            : string.Empty;
        var artistId = item.TryGetProperty("artist", out artistNode)
            ? GetAnyString(artistNode, "id")
            : string.Empty;
        if (string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(id))
        {
            url = $"https://tidal.com/browse/album/{Uri.EscapeDataString(id)}";
        }

        return new
        {
            source = TidalSource,
            type = AlbumType,
            name = ComposeTitle(GetString(item, TitleProperty), GetString(item, "version")),
            artist,
            artistId,
            artistIds = BuildArtistIds(item, artistId),
            image = BuildImageUrl(GetString(item, "cover")),
            release_date = GetString(item, "releaseDate"),
            trackCount = GetInt(item, "numberOfTracks"),
            tidalId = id,
            tidalType = AlbumType,
            tidalUrl = url,
            externalUrl = url,
            hasAtmos = HasAtmos(item),
            audioQuality = GetString(item, "audioQuality")
        };
    }

    private static object MapVideo(JsonElement item)
    {
        var id = GetAnyString(item, "id");
        var url = GetString(item, "url");
        if (string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(id))
        {
            url = $"https://tidal.com/browse/video/{Uri.EscapeDataString(id)}";
        }

        var artist = item.TryGetProperty("artist", out var artistNode)
            ? GetString(artistNode, "name")
            : GetString(item, "artistName");
        var artistId = item.TryGetProperty("artist", out artistNode)
            ? GetAnyString(artistNode, "id")
            : string.Empty;

        return new
        {
            source = TidalSource,
            type = VideoType,
            name = ComposeTitle(GetString(item, TitleProperty), GetString(item, "version")),
            artist,
            artistId,
            artistIds = BuildArtistIds(item, artistId),
            image = BuildImageUrl(GetString(item, "imageId"), fallbackId: GetString(item, "image")),
            releaseDate = GetString(item, "releaseDate"),
            duration = GetInt(item, "duration"),
            durationMs = Math.Max(0, GetInt(item, "duration")) * 1000L,
            previewUrl = GetString(item, "previewUrl"),
            tidalId = id,
            tidalType = VideoType,
            tidalUrl = url,
            externalUrl = url,
            hasAtmos = HasAtmos(item)
        };
    }

    private static object MapArtist(JsonElement item)
    {
        var id = GetAnyString(item, "id");
        var url = GetString(item, "url");
        if (string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(id))
        {
            url = $"https://tidal.com/browse/artist/{Uri.EscapeDataString(id)}";
        }
        int? followers = null;

        return new
        {
            source = TidalSource,
            type = ArtistType,
            name = GetString(item, "name"),
            image = BuildImageUrl(GetString(item, "picture")),
            followers,
            tidalId = id,
            tidalType = ArtistType,
            tidalUrl = url,
            externalUrl = url
        };
    }

    private static string[] BuildArtistIds(JsonElement item, string primaryArtistId)
    {
        var ids = new List<string>();
        if (!string.IsNullOrWhiteSpace(primaryArtistId))
        {
            ids.Add(primaryArtistId);
        }

        if (item.TryGetProperty("artists", out var artistsElement)
            && artistsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var artist in artistsElement.EnumerateArray())
            {
                var id = GetAnyString(artist, "id");
                if (!string.IsNullOrWhiteSpace(id))
                {
                    ids.Add(id);
                }
            }
        }

        return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static object MapPlaylist(JsonElement item)
    {
        var id = GetString(item, "uuid");
        if (string.IsNullOrWhiteSpace(id))
        {
            id = GetAnyString(item, "id");
        }

        var url = GetString(item, "url");
        if (string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(id))
        {
            url = $"https://tidal.com/browse/playlist/{Uri.EscapeDataString(id)}";
        }

        return new
        {
            source = TidalSource,
            type = PlaylistType,
            name = GetString(item, TitleProperty),
            owner = "Tidal",
            image = BuildImageUrl(GetString(item, "squareImage"), fallbackId: GetString(item, "image")),
            trackCount = GetInt(item, "numberOfTracks"),
            tidalId = id,
            tidalType = PlaylistType,
            tidalUrl = url,
            externalUrl = url
        };
    }

    private static string BuildImageUrl(string? imageId, string? fallbackId = null)
    {
        var id = !string.IsNullOrWhiteSpace(imageId) ? imageId : fallbackId;
        if (string.IsNullOrWhiteSpace(id))
        {
            return string.Empty;
        }

        var normalized = id.Replace("-", "/", StringComparison.Ordinal).Trim('/');
        return $"https://resources.tidal.com/images/{normalized}/750x750.jpg";
    }

    private static string ComposeTitle(string? title, string? version) =>
        ExternalSearchControllerHelpers.ComposeTitle(title, version);

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private static string GetAnyString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return string.Empty;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? string.Empty,
            JsonValueKind.Number => property.TryGetInt64(out var value) ? value.ToString() : property.ToString(),
            _ => string.Empty
        };
    }

    private static int GetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
        {
            return number;
        }

        if (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out number))
        {
            return number;
        }

        return 0;
    }

    private static bool HasAtmosObject(object item)
    {
        var property = item.GetType().GetProperty("hasAtmos");
        return property?.GetValue(item) is bool value && value;
    }

    private static bool HasAtmos(JsonElement item)
    {
        if (StringContainsAtmos(GetString(item, "audioQuality")))
        {
            return true;
        }

        foreach (var propertyName in new[] { "audioModes", "audioMode", "mediaMetadata", "tags" })
        {
            if (item.TryGetProperty(propertyName, out var property) && JsonElementContainsAtmos(property))
            {
                return true;
            }
        }

        return false;
    }

    private static bool JsonElementContainsAtmos(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => StringContainsAtmos(element.GetString()),
            JsonValueKind.Array => element.EnumerateArray().Any(JsonElementContainsAtmos),
            JsonValueKind.Object => element.EnumerateObject().Any(prop => JsonElementContainsAtmos(prop.Value)),
            _ => false
        };
    }

    private static bool StringContainsAtmos(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Contains("ATMOS", StringComparison.OrdinalIgnoreCase);
    }
}
