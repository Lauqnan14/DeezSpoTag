using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/tidal/search")]
[Authorize]
public sealed class TidalSearchApiController : ControllerBase
{
    private const string TidalSource = "tidal";
    private const string EncodedClientId = "NkJEU1JkcEs5aHFFQlRnVQ==";
    private const string EncodedClientSecret = "eGV1UG1ZN25icFo5SUliTEFjUTkzc2hrYTFWTmhlVUFxTjZJY3N6alRHOD0=";
    private const string AuthUrl = "https://auth.tidal.com/v1/oauth2/token";
    private static readonly SemaphoreSlim TokenLock = new(1, 1);
    private static string _cachedToken = string.Empty;
    private static DateTimeOffset _cachedTokenExpiresUtc = DateTimeOffset.MinValue;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TidalSearchApiController> _logger;

    public TidalSearchApiController(
        IHttpClientFactory httpClientFactory,
        ILogger<TidalSearchApiController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Search(
        [FromQuery] string query,
        [FromQuery] string? type = null,
        [FromQuery] int limit = 25,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return BadRequest(new { available = false, error = "Query is required." });
        }

        limit = Math.Clamp(limit, 1, 50);
        var normalizedType = NormalizeType(type);

        try
        {
            var token = await GetAccessTokenAsync(cancellationToken);
            var tracks = normalizedType is null or "track"
                ? await SearchTypedAsync("tracks", query, limit, token, MapTrack, cancellationToken)
                : new List<object>();
            var albums = normalizedType is null or "album"
                ? await SearchTypedAsync("albums", query, limit, token, MapAlbum, cancellationToken)
                : new List<object>();
            var artists = normalizedType is null or "artist"
                ? await SearchTypedAsync("artists", query, limit, token, MapArtist, cancellationToken)
                : new List<object>();
            var playlists = normalizedType is null or "playlist"
                ? await SearchTypedAsync("playlists", query, limit, token, MapPlaylist, cancellationToken)
                : new List<object>();

            return Ok(new
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
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Tidal search failed for query {Query}", query);
            return StatusCode(500, new { available = false, error = "Tidal search failed." });
        }
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
                InvalidateCachedToken();
                currentToken = await GetAccessTokenAsync(cancellationToken);
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

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_cachedToken) && _cachedTokenExpiresUtc > DateTimeOffset.UtcNow.AddSeconds(30))
        {
            return _cachedToken;
        }

        await TokenLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(_cachedToken) && _cachedTokenExpiresUtc > DateTimeOffset.UtcNow.AddSeconds(30))
            {
                return _cachedToken;
            }

            var client = _httpClientFactory.CreateClient();
            var clientId = Encoding.UTF8.GetString(Convert.FromBase64String(EncodedClientId));
            var clientSecret = Encoding.UTF8.GetString(Convert.FromBase64String(EncodedClientSecret));
            var authValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
            using var request = new HttpRequestMessage(HttpMethod.Post, AuthUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["grant_type"] = "client_credentials"
                })
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authValue);

            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Tidal auth failed with status {(int)response.StatusCode}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var token = GetString(doc.RootElement, "access_token");
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("Tidal auth did not return an access token.");
            }

            var expiresIn = GetInt(doc.RootElement, "expires_in");
            _cachedToken = token;
            _cachedTokenExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(expiresIn > 0 ? expiresIn : 300);
            return _cachedToken;
        }
        finally
        {
            TokenLock.Release();
        }
    }

    private static void InvalidateCachedToken()
    {
        _cachedToken = string.Empty;
        _cachedTokenExpiresUtc = DateTimeOffset.MinValue;
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
            type = "track",
            name = ComposeTitle(GetString(item, "title"), GetString(item, "version")),
            artist,
            album = albumTitle,
            image = BuildImageUrl(coverId),
            duration,
            durationMs = Math.Max(0, duration) * 1000L,
            isrc = GetString(item, "isrc"),
            tidalId = id,
            tidalType = "track",
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
            type = "album",
            name = ComposeTitle(GetString(item, "title"), GetString(item, "version")),
            artist,
            image = BuildImageUrl(GetString(item, "cover")),
            release_date = GetString(item, "releaseDate"),
            trackCount = GetInt(item, "numberOfTracks"),
            tidalId = id,
            tidalType = "album",
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
            type = "artist",
            name = GetString(item, "name"),
            image = BuildImageUrl(GetString(item, "picture")),
            followers = (int?)null,
            tidalId = id,
            tidalType = "artist",
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
            type = "playlist",
            name = GetString(item, "title"),
            owner = "Tidal",
            image = BuildImageUrl(GetString(item, "squareImage"), fallbackId: GetString(item, "image")),
            trackCount = GetInt(item, "numberOfTracks"),
            tidalId = id,
            tidalType = "playlist",
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

    private static string ComposeTitle(string? title, string? version)
    {
        var first = (title ?? string.Empty).Trim();
        var second = (version ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(second) ? first : $"{first} {second}".Trim();
    }

    private static string? NormalizeType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return null;
        }

        return type.Trim().ToLowerInvariant() switch
        {
            "track" => "track",
            "album" => "album",
            "artist" => "artist",
            "playlist" => "playlist",
            _ => null
        };
    }

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
