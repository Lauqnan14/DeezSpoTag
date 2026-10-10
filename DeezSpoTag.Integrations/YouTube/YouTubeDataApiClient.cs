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
/// Track resolution to a video id is not here, by decision. This client never calls
/// <c>search.list</c>: <c>search.list</c> costs 100 quota units against a 10,000/day budget for
/// the whole project, which is too thin to depend on. A track reaches YouTube Music when
/// <c>PlatformTrackIdentityResolver</c> resolves it through the quota-free YouTube Music search
/// client or when the library index already holds its video id, so resolution stays a separate
/// concern rather than something a playlist write can trigger.
/// </summary>
public sealed class YouTubeDataApiClient
{
    public const string ServiceName = "ytmusic";
    public const string DefaultTokenUrl = "https://oauth2.googleapis.com/token";
    public const string DefaultAuthorizeUrl = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string DefaultRevokeUrl = "https://oauth2.googleapis.com/revoke";
    private const string ApiBase = "https://www.googleapis.com/youtube/v3";

    /// <summary>
    /// A read stops being trustworthy well before this. It exists only so a provider that keeps
    /// handing back the same <c>nextPageToken</c> cannot spin the pass forever; a legitimate
    /// playlist is a handful of pages.
    /// </summary>
    private const int MaxPlaylistPages = 1000;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<YouTubeDataApiClient>? _logger;

    /// <summary>
    /// The gap between two playlist-item additions. The Data API rate-limits a burst of inserts,
    /// and the reference paces at the same second per track; without it a large first write comes
    /// back 403 rather than slow.
    /// </summary>
    public TimeSpan AddPacing { get; set; } = TimeSpan.FromSeconds(1);

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
        var collected = new List<YouTubePlaylist>();
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        var nextToken = pageToken;
        var pages = 0;
        var pageSize = Math.Clamp(maxResults, 1, 50);

        while (pages++ < MaxPlaylistPages)
        {
            var query = new List<KeyValuePair<string, string>>
            {
                new("part", "snippet,contentDetails"),
                new("mine", "true"),
                new("maxResults", pageSize.ToString(CultureInfo.InvariantCulture))
            };
            if (!string.IsNullOrWhiteSpace(nextToken))
            {
                query.Add(new KeyValuePair<string, string>("pageToken", nextToken));
            }

            var response = await GetAsync("playlists", query, accessToken, cancellationToken);
            var items = response?.Items;
            if (items is not null)
            {
                foreach (var item in items)
                {
                    var playlist = new YouTubePlaylist(
                        item.Id ?? string.Empty,
                        item.Snippet?.Title ?? string.Empty,
                        item.Snippet?.Description,
                        item.Snippet?.Thumbnails?.Maxres?.Url ?? item.Snippet?.Thumbnails?.Medium?.Url,
                        item.ContentDetails?.ItemCount);
                    if (string.IsNullOrWhiteSpace(playlist.Id))
                    {
                        continue;
                    }

                    collected.Add(playlist);
                    if (collected.Count >= maxResults)
                    {
                        return collected;
                    }
                }
            }

            // A body with no items array is read as an empty list, exactly as before: this is the
            // playlist LIST, where an unrecognised response means "nothing this call can offer"
            // rather than a membership that a mirror would then act on.
            var next = items is null ? null : response!.NextPageToken;
            if (string.IsNullOrWhiteSpace(next))
            {
                return collected;
            }

            if (!seenTokens.Add(next))
            {
                _logger?.LogWarning("YouTube playlists returned a repeating nextPageToken; the list was truncated.");
                break;
            }

            nextToken = next;
        }

        return collected;
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

    public async Task<PlaylistMetadataWriteResult> WritePlaylistMetadataAsync(
        string accessToken, PlaylistMetadataWrite write, CancellationToken cancellationToken)
    {
        var description = PlaylistMetadataFieldStatus.NotRequested;
        var artwork = PlaylistMetadataFieldStatus.NotRequested;
        var messages = new List<string>();
        if (string.IsNullOrWhiteSpace(accessToken))
            return PlaylistMetadataWriteResult.Failed(write, "YouTube Music is not connected.");
        if (write.Description is not null)
        {
            try
            {
                var (applied, message) = await UpdatePlaylistDescriptionAsync(accessToken, write, cancellationToken).ConfigureAwait(false);
                description = applied ? PlaylistMetadataFieldStatus.Applied : PlaylistMetadataFieldStatus.Failed;
                if (message is not null) messages.Add(message);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TimeoutException
                || ex is TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                description = PlaylistMetadataFieldStatus.Failed;
                messages.Add("YouTube playlist description request did not complete.");
            }
        }
        if (write.Artwork is not null)
        {
            try
            {
                var (applied, message) = await UpdatePlaylistArtworkAsync(accessToken, write, cancellationToken).ConfigureAwait(false);
                artwork = applied ? PlaylistMetadataFieldStatus.Applied : PlaylistMetadataFieldStatus.Failed;
                if (message is not null) messages.Add(message);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TimeoutException
                || ex is TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                artwork = PlaylistMetadataFieldStatus.Failed;
                messages.Add("YouTube playlist artwork request did not complete.");
            }
        }
        return new(description, artwork, messages.Count == 0 ? null : string.Join(" ", messages));
    }

    private async Task<(bool Applied, string? Message)> UpdatePlaylistDescriptionAsync(
        string accessToken, PlaylistMetadataWrite write, CancellationToken cancellationToken)
    {
        using var read = await SendAsync(HttpMethod.Get, $"playlists?part=snippet&id={Uri.EscapeDataString(write.PlaylistId)}",
            accessToken, null, cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccessStatusCode) return (false, $"YouTube description could not be read (HTTP {(int)read.StatusCode}).");
        using var document = JsonDocument.Parse(await read.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() != 1
            || items[0].ReadString("id") != write.PlaylistId || items[0].ReadObject("snippet") is not { } snippet
            || string.IsNullOrWhiteSpace(snippet.ReadString("title")))
            return (false, "YouTube description update could not read the destination playlist's title.");
        var fields = new Dictionary<string, object> { ["title"] = snippet.ReadString("title")!, ["description"] = write.Description! };
        if (snippet.ReadString("defaultLanguage") is { Length: > 0 } language) fields["defaultLanguage"] = language;
        using var response = await SendAsync(HttpMethod.Put, "playlists?part=snippet", accessToken,
            JsonSerializer.Serialize(new { id = write.PlaylistId, snippet = fields }), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return (false, $"YouTube description update failed (HTTP {(int)response.StatusCode}).");
        using var changed = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return changed.RootElement.ReadString("id") == write.PlaylistId
            ? (true, null) : (false, "YouTube description update did not confirm the playlist id.");
    }

    private async Task<(bool Applied, string? Message)> UpdatePlaylistArtworkAsync(
        string accessToken, PlaylistMetadataWrite write, CancellationToken cancellationToken)
    {
        var artwork = write.Artwork!;
        if (artwork.ContentType != "image/jpeg" || artwork.Bytes.Length == 0 || artwork.Bytes.Length > 2 * 1024 * 1024)
            return (false, "YouTube artwork must be a JPEG within the supported image payload limit.");
        using var read = await SendAsync(HttpMethod.Get,
            $"playlistImages?part=snippet&playlistId={Uri.EscapeDataString(write.PlaylistId)}", accessToken, null, cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccessStatusCode)
            return (false, $"YouTube playlist image could not be read (HTTP {(int)read.StatusCode}); the current token may lack playlist image permission.");
        using var document = JsonDocument.Parse(await read.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return (false, "YouTube playlist image response was incomplete.");
        if (!string.IsNullOrWhiteSpace(root.ReadString("nextPageToken")))
            return (false, "YouTube playlist image listing was incomplete; no image was uploaded.");
        var images = items.EnumerateArray().Where(item => item.ReadString("snippet.type") == "hero").ToList();
        if (images.Count > 1 || items.EnumerateArray().Any(item => item.ReadString("snippet.type") is null))
            return (false, "YouTube playlist image identity was incomplete.");
        var imageId = images.Count == 1 ? images[0].ReadString("id") : null;
        if (images.Count == 1 && string.IsNullOrWhiteSpace(imageId))
            return (false, "YouTube playlist image identity was missing.");
        var metadata = new Dictionary<string, object> { ["snippet"] = new { playlistId = write.PlaylistId, type = "hero" } };
        if (imageId is not null) metadata["id"] = imageId;
        using var content = new MultipartContent("related");
        content.Add(new StringContent(JsonSerializer.Serialize(metadata), Encoding.UTF8, "application/json"));
        var media = new ByteArrayContent(artwork.Bytes);
        media.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(media);
        using var request = new HttpRequestMessage(imageId is null ? HttpMethod.Post : HttpMethod.Put,
            "https://www.googleapis.com/upload/youtube/v3/playlistImages?uploadType=multipart&part=snippet") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return (false, $"YouTube artwork update failed (HTTP {(int)response.StatusCode}); the current token may lack playlist image permission.");
        using var uploaded = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return !string.IsNullOrWhiteSpace(uploaded.RootElement.ReadString("id"))
            ? (true, null) : (false, "YouTube artwork update did not confirm an image id.");
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
    /// The video ids currently in a playlist, in order, following <c>nextPageToken</c> until the
    /// list is complete. A null result means the read failed part-way; a genuinely empty playlist
    /// returns an empty list, which is not the same thing and must never be treated as one.
    /// <para>
    /// <paramref name="maxResults"/> caps how many items are wanted overall rather than how many
    /// the provider is asked for per request: asking for one page and returning it as the whole
    /// membership would hand a 50-item view of a 100-item playlist to a mirror pass, which then
    /// reads the other 50 as tracks the destination should lose. The collapse guard does not catch
    /// that - 50 of 100 is exactly the halfway line it still trusts.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<YouTubePlaylistItem>?> GetPlaylistItemsAsync(
        string accessToken,
        string playlistId,
        int maxResults = 50,
        string? pageToken = null,
        CancellationToken cancellationToken = default)
    {
        var collected = new List<YouTubePlaylistItem>();
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        var nextToken = pageToken;
        var pages = 0;
        var pageSize = Math.Clamp(maxResults, 1, 50);

        while (pages++ < MaxPlaylistPages)
        {
            var query = new List<KeyValuePair<string, string>>
            {
                new("part", "snippet,contentDetails"),
                new("playlistId", playlistId),
                new("maxResults", pageSize.ToString(CultureInfo.InvariantCulture))
            };
            if (!string.IsNullOrWhiteSpace(nextToken))
            {
                query.Add(new KeyValuePair<string, string>("pageToken", nextToken));
            }

            var response = await GetAsync("playlistItems", query, accessToken, cancellationToken);
            if (response?.Items is null)
            {
                // Nothing usable came back - including a page that failed after earlier pages
                // succeeded. Reporting the pages collected so far would be a truncated membership,
                // which the engine cannot tell apart from a short playlist.
                return null;
            }

            foreach (var item in response.Items)
            {
                var parsed = new YouTubePlaylistItem(
                    item.Id ?? string.Empty,
                    item.Snippet?.ResourceId?.VideoId ?? string.Empty,
                    item.Snippet?.Title ?? string.Empty,
                    item.Snippet is null ? null : int.TryParse(item.Snippet.Position, NumberStyles.Integer, CultureInfo.InvariantCulture, out var position) ? position : null);
                if (string.IsNullOrWhiteSpace(parsed.VideoId))
                {
                    continue;
                }

                collected.Add(parsed);
                if (collected.Count >= maxResults)
                {
                    return collected;
                }
            }

            var next = response.NextPageToken;
            if (string.IsNullOrWhiteSpace(next))
            {
                return collected;
            }

            if (!seenTokens.Add(next))
            {
                _logger?.LogWarning(
                    "YouTube playlistItems for {PlaylistId} returned a repeating nextPageToken; the read is truncated.",
                    playlistId);
                return null;
            }

            nextToken = next;
        }

        _logger?.LogWarning(
            "YouTube playlistItems for {PlaylistId} exceeded {Pages} pages; the read is truncated.",
            playlistId,
            MaxPlaylistPages);
        return null;
    }

    /// <summary>
    /// Appends videos to a playlist, one request each and in order.
    /// <para>
    /// <c>playlistItems.insert</c> takes ONE item carrying <c>snippet.resourceId</c>; there is no
    /// batch field, so a payload shaped as <c>contentDetails.videoIds</c> describes a resource the
    /// endpoint does not model and the call is accepted without adding anything. Order is the
    /// other reason for one-at-a-time: the playlist's date-added stamp is the only ordering a
    /// platform append can express, and a batch or a write that continues past a rejection would
    /// leave later tracks stamped earlier than the ones in front of them.
    /// </para>
    /// </summary>
    public async Task<bool> AddPlaylistItemsAsync(
        string accessToken,
        string playlistId,
        IReadOnlyList<string> videoIds,
        CancellationToken cancellationToken = default)
    {
        var scheduled = videoIds
            .Where(static videoId => !string.IsNullOrWhiteSpace(videoId))
            .ToList();
        if (scheduled.Count == 0)
        {
            return true;
        }

        for (var index = 0; index < scheduled.Count; index++)
        {
            var body = JsonSerializer.Serialize(new
            {
                snippet = new
                {
                    playlistId,
                    resourceId = new
                    {
                        kind = "youtube#video",
                        videoId = scheduled[index]
                    }
                }
            }, SerializerOptions);

            using var response = await SendAsync(
                HttpMethod.Post,
                "playlistItems?part=snippet",
                accessToken,
                body,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning(
                    "YouTube failed to add a video to {PlaylistId} with HTTP {StatusCode}.",
                    playlistId,
                    (int)response.StatusCode);
                return false;
            }

            if (AddPacing > TimeSpan.Zero && index < scheduled.Count - 1)
            {
                await Task.Delay(AddPacing, cancellationToken);
            }
        }

        return true;
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

    [JsonPropertyName("nextPageToken")]
    public string? NextPageToken { get; set; }
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
