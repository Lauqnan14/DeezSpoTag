using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Integrations.YouTube;

/// <summary>
/// YouTube Data API v3 client, limited to what a playlist sync needs: playlist read,
/// playlist create, and item add/remove/replace.
///
/// Authentication is OAuth 2.0 with a Google Cloud client of type "TVs and Limited Input
/// devices", which is the flow Google's device-code policy allows. A refresh token is obtained
/// once and reused; the access token is refreshed on demand.
///
/// Track resolution to a video id is not here, by decision. This app does not search YouTube for
/// videos: <c>search.list</c> allows only 100 calls per day for the whole project, which is too
/// thin to depend on. A track reaches YouTube Music only when the library index already holds its
/// video id, so resolution stays a separate concern rather than something a playlist write can
/// trigger.
/// </summary>
public sealed class YouTubeDataApiClient
{
    public const string ServiceName = "ytmusic";
    public const string DefaultTokenUrl = "https://oauth2.googleapis.com/token";
    public const string DefaultAuthorizeUrl = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string DefaultRevokeUrl = "https://oauth2.googleapis.com/revoke";
    private const string ApiBase = "https://www.googleapis.com/youtube/v3";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<YouTubeDataApiClient>? _logger;

    public YouTubeDataApiClient(HttpClient httpClient, ILogger<YouTubeDataApiClient>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// The scopes a playlist sync needs. Read/write on playlists and playlist items; nothing
    /// else is requested.
    /// </summary>
    public static IReadOnlyList<string> RequiredScopes { get; } =
    [
        "https://www.googleapis.com/auth/youtube.readonly",
        "https://www.googleapis.com/auth/youtubep",
        "https://www.googleapis.com/auth/yt-app-managed-user"
    ];

    public string BuildAuthorizeUrl(string clientId, string redirectUri, string state, string loginHint = "")
    {
        var query = new List<KeyValuePair<string, string>>
        {
            new("client_id", clientId),
            new("redirect_uri", redirectUri),
            new("response_type", "code"),
            new("scope", string.Join(' ', RequiredScopes)),
            new("state", state),
            new("access_type", "offline"),
            // Forces a refresh token. Without this a second consent would not yield one.
            new("prompt", "consent")
        };

        if (!string.IsNullOrWhiteSpace(loginHint))
        {
            query.Add(new KeyValuePair<string, string>("login_hint", loginHint.Trim()));
        }

        var builder = new UriBuilder($"{DefaultAuthorizeUrl}?{BuildQuery(query)}");
        return builder.Uri.ToString();
    }

    /// <summary>Exchanges an authorization code for credentials including a refresh token.</summary>
    public async Task<YouTubeTokenResponse> ExchangeCodeAsync(
        string tokenUrl,
        string clientId,
        string clientSecret,
        string code,
        string redirectUri,
        CancellationToken cancellationToken = default)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code"
        });

        return await PostTokenAsync(tokenUrl, form, cancellationToken);
    }

    public async Task<YouTubeTokenResponse> RefreshAsync(
        string tokenUrl,
        string clientId,
        string clientSecret,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token"
        });

        return await PostTokenAsync(tokenUrl, form, cancellationToken);
    }

    private async Task<YouTubeTokenResponse> PostTokenAsync(
        string tokenUrl,
        FormUrlEncodedContent form,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsync(NormalizeTokenUrl(tokenUrl), form, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger?.LogWarning("YouTube token request failed with HTTP {StatusCode}.", (int)response.StatusCode);
            return new YouTubeTokenResponse(false, null, null, null, $"HTTP {(int)response.StatusCode}");
        }

        try
        {
            var payload = JsonSerializer.Deserialize<YouTubeTokenPayload>(body, SerializerOptions);
            return new YouTubeTokenResponse(
                true,
                payload?.AccessToken,
                payload?.RefreshToken,
                payload?.ExpiresInSeconds is > 0 ? DateTimeOffset.UtcNow.AddSeconds(payload.ExpiresInSeconds.Value) : null,
                null);
        }
        catch (JsonException)
        {
            return new YouTubeTokenResponse(false, null, null, null, "The token response could not be read.");
        }
    }

    public async Task<bool> RevokeAsync(
        string revokeUrl,
        string token,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token });
            using var response = await _httpClient.PostAsync(
                string.IsNullOrWhiteSpace(revokeUrl) ? DefaultRevokeUrl : revokeUrl,
                content,
                cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogWarning(ex, "YouTube token revoke request failed.");
            return false;
        }
    }

    /// <summary>Playlists the authenticated user owns or has explicitly shared with them.</summary>
    public async Task<IReadOnlyList<YouTubePlaylist>> GetMyPlaylistsAsync(
        string accessToken,
        int maxResults = 50,
        string? pageToken = null,
        CancellationToken cancellationToken = default)
    {
        var query = new List<KeyValuePair<string, string>>
        {
            new("part", "snippet,contentDetails"),
            new("mine", "true"),
            new("maxResults", Math.Clamp(maxResults, 1, 50).ToString(CultureInfo.InvariantCulture))
        };
        if (!string.IsNullOrWhiteSpace(pageToken))
        {
            query.Add(new KeyValuePair<string, string>("pageToken", pageToken));
        }

        var response = await GetAsync("playlists", query, accessToken, cancellationToken);
        return response?.Items?
            .Select(static item => new YouTubePlaylist(
                item.Id ?? string.Empty,
                item.Snippet?.Title ?? string.Empty,
                item.Snippet?.Description,
                item.Snippet?.Thumbnails?.Maxres?.Url ?? item.Snippet?.Thumbnails?.Medium?.Url,
                item.ContentDetails?.ItemCount))
            .Where(static playlist => !string.IsNullOrWhiteSpace(playlist.Id))
            .ToList() ?? (IReadOnlyList<YouTubePlaylist>)Array.Empty<YouTubePlaylist>();
    }

    public async Task<YouTubePlaylist?> GetPlaylistAsync(
        string accessToken,
        string playlistId,
        CancellationToken cancellationToken = default)
    {
        var response = await GetAsync(
            "playlists",
            [
                new("part", "snippet,contentDetails"),
                new("id", playlistId)
            ],
            accessToken,
            cancellationToken);

        var item = response?.Items?.FirstOrDefault();
        if (item is null || string.IsNullOrWhiteSpace(item.Id))
        {
            return null;
        }

        return new YouTubePlaylist(
            item.Id,
            item.Snippet?.Title ?? string.Empty,
            item.Snippet?.Description,
            item.Snippet?.Thumbnails?.Maxres?.Url ?? item.Snippet?.Thumbnails?.Medium?.Url,
            item.ContentDetails?.ItemCount);
    }

    public async Task<YouTubePlaylistWriteResult> CreatePlaylistAsync(
        string accessToken,
        string title,
        string? description,
        string privacy = "private",
        CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.Serialize(new
        {
            snippet = new { title, description },
            status = new { privacyStatus = privacy }
        }, SerializerOptions);

        using var response = await SendAsync(HttpMethod.Post, "playlists?part=snippet,status", accessToken, body, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new YouTubePlaylistWriteResult(false, null, $"HTTP {(int)response.StatusCode}");
        }

        try
        {
            var created = JsonSerializer.Deserialize<YouTubePlaylistPayload>(payload, SerializerOptions);
            return string.IsNullOrWhiteSpace(created?.Id)
                ? new YouTubePlaylistWriteResult(false, null, "The created playlist had no id.")
                : new YouTubePlaylistWriteResult(true, created.Id, null);
        }
        catch (JsonException)
        {
            return new YouTubePlaylistWriteResult(false, null, "The created playlist could not be read.");
        }
    }

    /// <summary>
    /// The video ids currently in a playlist, in order. A null or empty result means the read
    /// failed or returned nothing usable, which is not the same as an empty playlist and must
    /// never be treated as one.
    /// </summary>
    public async Task<IReadOnlyList<YouTubePlaylistItem>?> GetPlaylistItemsAsync(
        string accessToken,
        string playlistId,
        int maxResults = 50,
        string? pageToken = null,
        CancellationToken cancellationToken = default)
    {
        var query = new List<KeyValuePair<string, string>>
        {
            new("part", "snippet,contentDetails"),
            new("playlistId", playlistId),
            new("maxResults", Math.Clamp(maxResults, 1, 50).ToString(CultureInfo.InvariantCulture))
        };
        if (!string.IsNullOrWhiteSpace(pageToken))
        {
            query.Add(new KeyValuePair<string, string>("pageToken", pageToken));
        }

        var response = await GetAsync("playlistItems", query, accessToken, cancellationToken);
        var items = response?.Items;
        if (items is null)
        {
            return null;
        }

        return items
            .Select(static item => new YouTubePlaylistItem(
                item.Id ?? string.Empty,
                item.Snippet?.ResourceId?.VideoId ?? string.Empty,
                item.Snippet?.Title ?? string.Empty,
                item.Snippet is null ? null : int.TryParse(item.Snippet.Position, NumberStyles.Integer, CultureInfo.InvariantCulture, out var position) ? position : null))
            .Where(static item => !string.IsNullOrWhiteSpace(item.VideoId))
            .ToList();
    }

    public async Task<bool> AddPlaylistItemsAsync(
        string accessToken,
        string playlistId,
        IReadOnlyList<string> videoIds,
        CancellationToken cancellationToken = default)
    {
        if (videoIds.Count == 0)
        {
            return true;
        }

        var body = JsonSerializer.Serialize(new
        {
            snippet = new { playlistId },
            contentDetails = new { videoIds = videoIds }
        }, SerializerOptions);

        using var response = await SendAsync(
            HttpMethod.Post,
            "playlistItems?part=snippet",
            accessToken,
            body,
            cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> DeletePlaylistItemAsync(
        string accessToken,
        string playlistItemId,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Delete,
            $"playlistItems?id={Uri.EscapeDataString(playlistItemId)}",
            accessToken,
            null,
            cancellationToken);
        return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound;
    }

    private async Task<YouTubeListResponse?> GetAsync(
        string path,
        IReadOnlyList<KeyValuePair<string, string>> query,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"{path}?{BuildQuery(query)}",
            accessToken,
            null,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger?.LogWarning("YouTube {Path} failed with HTTP {StatusCode}.", path, (int)response.StatusCode);
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            return JsonSerializer.Deserialize<YouTubeListResponse>(body, SerializerOptions);
        }
        catch (JsonException)
        {
            _logger?.LogWarning("YouTube {Path} returned a body that could not be read.", path);
            return null;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string pathAndQuery,
        string accessToken,
        string? jsonBody,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, $"{ApiBase}/{pathAndQuery}");
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        return await _httpClient.SendAsync(request, cancellationToken);
    }

    private static string NormalizeTokenUrl(string? tokenUrl)
        => string.IsNullOrWhiteSpace(tokenUrl) ? DefaultTokenUrl : tokenUrl.Trim();

    private static string BuildQuery(IEnumerable<KeyValuePair<string, string>> values)
        => string.Join('&', values.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
}

public sealed record YouTubeTokenResponse(
    bool Success,
    string? AccessToken,
    string? RefreshToken,
    DateTimeOffset? ExpiresAtUtc,
    string? Error);

public sealed record YouTubePlaylist(
    string Id,
    string Title,
    string? Description,
    string? CoverUrl,
    int? ItemCount);

public sealed record YouTubePlaylistItem(
    string PlaylistItemId,
    string VideoId,
    string Title,
    int? Position);

public sealed record YouTubePlaylistWriteResult(bool Success, string? PlaylistId, string? Error);

internal sealed class YouTubeTokenPayload
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expires_in")]
    public int? ExpiresInSeconds { get; set; }
}

internal sealed class YouTubeListResponse
{
    [JsonPropertyName("items")]
    public List<YouTubePlaylistPayload>? Items { get; set; }
}

internal sealed class YouTubePlaylistPayload
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
    [JsonPropertyName("snippet")]
    public YouTubeSnippet? Snippet { get; set; }

    /// <summary>search.list returns the video id on the item, not under snippet.</summary>
    [JsonPropertyName("channelTitle")]
    public string? ChannelTitle { get; set; }

    [JsonPropertyName("contentDetails")]
    public YouTubeContentDetails? ContentDetails { get; set; }
}

internal sealed class YouTubeSnippet
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("thumbnails")]
    public YouTubeThumbnails? Thumbnails { get; set; }

    [JsonPropertyName("resourceId")]
    public YouTubeResourceId? ResourceId { get; set; }

    [JsonPropertyName("channelTitle")]
    public string? ChannelTitle { get; set; }

    /// <summary>playlistItems.list returns position as a JSON string, not a number.</summary>
    [JsonPropertyName("position")]
    public string? Position { get; set; }
}

internal sealed class YouTubeThumbnails
{
    [JsonPropertyName("maxres")]
    public YouTubeThumbnail? Maxres { get; set; }

    [JsonPropertyName("medium")]
    public YouTubeThumbnail? Medium { get; set; }
}

internal sealed class YouTubeThumbnail
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

internal sealed class YouTubeResourceId
{
    [JsonPropertyName("videoId")]
    public string? VideoId { get; set; }
}

internal sealed class YouTubeContentDetails
{
    [JsonPropertyName("itemCount")]
    public int? ItemCount { get; set; }
}
