using System.Net;
using System.Text;
using System.Text.Json;
using DeezSpoTag.Services.Utils;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download;

public sealed class SpotifyIdResolver : ISpotifyIdResolver
{
    private static readonly TimeSpan TokenRefreshWindow = TimeSpan.FromMinutes(1);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SpotifyIdResolver> _logger;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt = DateTimeOffset.MinValue;

    public SpotifyIdResolver(IHttpClientFactory httpClientFactory, ILogger<SpotifyIdResolver> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<string?> ResolveTrackIdAsync(
        string title,
        string artist,
        string? album,
        string? isrc,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(isrc))
        {
            return null;
        }

        var token = await GetAccessTokenAsync(cancellationToken);
        var query = BuildSearchQuery(title, artist, album, isrc);
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        using var response = await SendSearchRequestAsync(token, query, cancellationToken);
        if (response == null)
        {
            return null;
        }

        using var doc = await ParseSearchDocumentAsync(response, cancellationToken);
        if (!TryGetTrackItems(doc, out var items))
        {
            return null;
        }

        return ResolveTrackIdFromItems(items, Normalize(title), Normalize(artist), isrc);
    }

    private async Task<HttpResponseMessage?> SendSearchRequestAsync(string token, string query, CancellationToken cancellationToken)
    {
        var searchUrl =
            $"https://api.spotify.com/v1/search?type=track&limit=5&market=from_token&q={WebUtility.UrlEncode(query)}";
        using var client = _httpClientFactory.CreateClient("SpotifyPublic");
        using var request = new HttpRequestMessage(HttpMethod.Get, searchUrl);
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return await SendWithRetryAsync(client, request, cancellationToken);
    }

    private static async Task<JsonDocument> ParseSearchDocumentAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static bool TryGetTrackItems(JsonDocument doc, out JsonElement items)
    {
        items = default;
        return doc.RootElement.TryGetProperty("tracks", out var tracks)
            && tracks.TryGetProperty("items", out items)
            && items.ValueKind == JsonValueKind.Array;
    }

    private static string? ResolveTrackIdFromItems(JsonElement items, string normalizedTitle, string normalizedArtist, string? isrc)
    {
        string? fallbackId = null;
        foreach (var item in items.EnumerateArray())
        {
            var itemId = TryGetTrackItemId(item);
            if (string.IsNullOrWhiteSpace(itemId))
            {
                continue;
            }

            if (IsIsrcMatch(item, isrc))
            {
                return itemId;
            }

            fallbackId ??= itemId;
            if (IsStrongMatch(item, normalizedTitle, normalizedArtist))
            {
                return itemId;
            }
        }

        return fallbackId;
    }

    private static string? TryGetTrackItemId(JsonElement item)
    {
        return item.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
    }

    private static bool IsIsrcMatch(JsonElement item, string? isrc)
    {
        if (string.IsNullOrWhiteSpace(isrc)
            || !item.TryGetProperty("external_ids", out var externalIds)
            || !externalIds.TryGetProperty("isrc", out var itemIsrcElement))
        {
            return false;
        }

        var itemIsrc = itemIsrcElement.GetString();
        return !string.IsNullOrWhiteSpace(itemIsrc)
            && string.Equals(itemIsrc, isrc, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<HttpResponseMessage?> SendWithRetryAsync(
        HttpClient client,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode != System.Net.HttpStatusCode.TooManyRequests)
        {
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Spotify search failed: status={Status} query={Query}", (int)response.StatusCode, request.RequestUri);
                response.Dispose();
                return null;
            }

            return response;
        }

        response.Dispose();
        _logger.LogDebug("Spotify search rate limited, retrying after short delay.");
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

        using var retryRequest = new HttpRequestMessage(HttpMethod.Get, request.RequestUri);
        retryRequest.Headers.Authorization = request.Headers.Authorization;
        var retryResponse = await client.SendAsync(retryRequest, cancellationToken);
        if (!retryResponse.IsSuccessStatusCode)
        {
            _logger.LogDebug("Spotify search failed after retry: status={Status} query={Query}", (int)retryResponse.StatusCode, request.RequestUri);
            retryResponse.Dispose();
            return null;
        }

        return retryResponse;
    }

    private async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_accessToken) &&
            _accessTokenExpiresAt - DateTimeOffset.UtcNow > TokenRefreshWindow)
        {
            return _accessToken;
        }

        await _tokenGate.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(_accessToken) &&
                _accessTokenExpiresAt - DateTimeOffset.UtcNow > TokenRefreshWindow)
            {
                return _accessToken;
            }

            var (totp, version) = SpotifyWebPlayerTotp.Generate();
            if (string.IsNullOrWhiteSpace(totp))
            {
                return null;
            }

            var url =
                $"https://open.spotify.com/api/token?reason=init&productType=web-player&totp={totp}&totpVer={version}&totpServer={totp}";
            using var client = _httpClientFactory.CreateClient("SpotifyPublic");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Spotify token request failed: status={Status}", (int)response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("accessToken", out var tokenElement))
            {
                return null;
            }

            var token = tokenElement.GetString();
            if (string.IsNullOrWhiteSpace(token))
            {
                return null;
            }

            _accessToken = token;
            _accessTokenExpiresAt = ParseExpiry(doc.RootElement);
            return token;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Spotify token request failed");
            return null;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private static DateTimeOffset ParseExpiry(JsonElement root)
    {
        if (root.TryGetProperty("accessTokenExpirationTimestampMs", out var expiryElement) &&
            expiryElement.TryGetInt64(out var expiryMs))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(expiryMs);
        }

        return DateTimeOffset.UtcNow.AddMinutes(30);
    }

    private static string BuildSearchQuery(string title, string artist, string? album, string? isrc)
    {
        if (!string.IsNullOrWhiteSpace(isrc))
        {
            return $"isrc:{isrc}";
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(title))
        {
            parts.Add($"track:{title}");
        }
        if (!string.IsNullOrWhiteSpace(artist))
        {
            parts.Add($"artist:{artist}");
        }
        if (!string.IsNullOrWhiteSpace(album))
        {
            parts.Add($"album:{album}");
        }

        return string.Join(" ", parts);
    }

    private static bool IsStrongMatch(JsonElement item, string normalizedTitle, string normalizedArtist)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle))
        {
            return false;
        }

        var name = item.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "";
        var itemTitle = Normalize(name);
        if (itemTitle != normalizedTitle)
        {
            return false;
        }

        if (!item.TryGetProperty("artists", out var artists) ||
            artists.ValueKind != JsonValueKind.Array ||
            artists.GetArrayLength() == 0)
        {
            return true;
        }

        var firstArtist = artists[0];
        var artistName = firstArtist.TryGetProperty("name", out var artistElement)
            ? artistElement.GetString() ?? ""
            : "";
        var itemArtist = Normalize(artistName);
        if (string.IsNullOrWhiteSpace(normalizedArtist))
        {
            return true;
        }

        return itemArtist == normalizedArtist;
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Where(char.IsLetterOrDigit))
        {
            builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }

}
