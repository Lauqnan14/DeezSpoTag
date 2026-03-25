using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
    private const string TitleProperty = "title";

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
        return await ExternalSearchControllerHelpers.RunSearchAsync(
            query,
            type,
            limit,
            _logger,
            failureMessage: "Tidal search failed.",
            async (normalizedType, normalizedLimit, ct) =>
            {
                var token = await _tidalAccessTokenProvider.GetAccessTokenAsync(ct);
                var tracks = normalizedType is null or TrackType
                    ? await SearchTypedAsync("tracks", query, normalizedLimit, token, MapTrack, ct)
                    : new List<object>();
                var albums = normalizedType is null or AlbumType
                    ? await SearchTypedAsync("albums", query, normalizedLimit, token, MapAlbum, ct)
                    : new List<object>();
                var artists = normalizedType is null or ArtistType
                    ? await SearchTypedAsync("artists", query, normalizedLimit, token, MapArtist, ct)
                    : new List<object>();
                var playlists = normalizedType is null or PlaylistType
                    ? await SearchTypedAsync("playlists", query, normalizedLimit, token, MapPlaylist, ct)
                    : new List<object>();

                return new
                {
                    available = true,
                    tracks,
                    albums,
                    artists,
                    playlists,
                    totals = new Dictionary<string, int>
                    {
                        ["tracks"] = tracks.Count,
                        ["albums"] = albums.Count,
                        ["artists"] = artists.Count,
                        ["playlists"] = playlists.Count
                    }
                };
            },
            cancellationToken);
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
        var output = new List<object>(items.Count);
        foreach (var item in items)
        {
            var mapped = mapper(item);
            if (mapped != null)
            {
                output.Add(mapped);
            }
        }

        return output;
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
                return new List<JsonElement>();
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!doc.RootElement.TryGetProperty("items", out var itemsElement) || itemsElement.ValueKind != JsonValueKind.Array)
            {
                return new List<JsonElement>();
            }

            return itemsElement.EnumerateArray().Select(element => element.Clone()).ToList();
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

    private static object MapTrack(JsonElement item)
    {
        var id = GetAnyString(item, "id");
        var url = GetString(item, "url");
        var artist = item.TryGetProperty("artist", out var artistNode)
            ? GetString(artistNode, "name")
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
        return new
        {
            source = TidalSource,
            type = TrackType,
            name = ComposeTitle(GetString(item, TitleProperty), GetString(item, "version")),
            artist,
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
            image = BuildImageUrl(GetString(item, "cover")),
            release_date = GetString(item, "releaseDate"),
            trackCount = GetInt(item, "numberOfTracks"),
            tidalId = id,
            tidalType = AlbumType,
            tidalUrl = url,
            externalUrl = url,
            audioQuality = GetString(item, "audioQuality")
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

        return new
        {
            source = TidalSource,
            type = ArtistType,
            name = GetString(item, "name"),
            image = BuildImageUrl(GetString(item, "picture")),
            followers = (int?)null,
            tidalId = id,
            tidalType = ArtistType,
            tidalUrl = url,
            externalUrl = url
        };
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
}
