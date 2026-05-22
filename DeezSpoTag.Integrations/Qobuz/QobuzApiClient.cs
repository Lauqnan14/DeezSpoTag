using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeezSpoTag.Core.Models.Qobuz;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace DeezSpoTag.Integrations.Qobuz;

public interface IQobuzApiClient
{
    Task<QobuzAutosuggestResponse?> SearchAutosuggestAsync(string store, string query, CancellationToken cancellationToken);
    Task<QobuzCatalogSearchResponse?> SearchCatalogAsync(string query, int limit, int offset, CancellationToken cancellationToken);
    Task<List<int>> GetAlbumPageTrackIdsAsync(string albumUrl, CancellationToken cancellationToken);
    Task<List<QobuzTrack>> GetAlbumPageTracksAsync(string albumUrl, CancellationToken cancellationToken);
    Task<QobuzArtist?> GetArtistAsync(int artistId, string store, int offset, int limit, CancellationToken cancellationToken);
    Task<QobuzTrackSearchResponse?> SearchTracksAsync(string query, int limit, int offset, CancellationToken cancellationToken);
    Task<QobuzAlbumSearchResponse?> SearchAlbumsAsync(string query, int limit, int offset, CancellationToken cancellationToken);
    Task<QobuzArtistSearchResponse?> SearchArtistsAsync(string query, int limit, int offset, CancellationToken cancellationToken);
    Task<QobuzTrack?> GetTrackAsync(int trackId, CancellationToken cancellationToken);
}

public sealed class QobuzApiClient : IQobuzApiClient
{
    private static readonly Regex AlbumTrackIdRegex = new("data-track=\"(?<id>\\d+)\"", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex AlbumTrackBlockRegex = new(
        "data-track=\"(?<id>\\d+)\"(?:(?!<div class=\"track(?:\\s|\")).)*?data-track-v2=\"(?<metadata>[^\"]+)\"(?:(?!<div class=\"track(?:\\s|\")).)*?<span class=\"track__item track__item--duration\">(?<duration>[^<]+)</span>",
        RegexOptions.Compiled | RegexOptions.Singleline,
        TimeSpan.FromSeconds(1));
    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly QobuzApiConfig _config;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public QobuzApiClient(HttpClient httpClient, IMemoryCache cache, IOptions<QobuzApiConfig> options)
    {
        _httpClient = httpClient;
        _cache = cache;
        _config = options.Value;

        if (_httpClient.BaseAddress == null)
        {
            _httpClient.BaseAddress = new Uri(_config.BaseUrl);
        }

        if (_httpClient.DefaultRequestHeaders.Accept.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }
    }

    public async Task<QobuzAutosuggestResponse?> SearchAutosuggestAsync(string store, string query, CancellationToken cancellationToken)
    {
        var resolvedStore = QobuzStoreManager.NormalizeStore(store, _config.DefaultStore);
        var url = $"/v4/{resolvedStore}/catalog/search/autosuggest?q={Uri.EscapeDataString(query)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("x-requested-with", "XMLHttpRequest");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<QobuzAutosuggestResponse>(stream, _serializerOptions, cancellationToken);
    }

    public async Task<QobuzCatalogSearchResponse?> SearchCatalogAsync(
        string query,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        var url = $"/api.json/0.2/catalog/search?query={Uri.EscapeDataString(query)}&limit={limit}&offset={offset}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("x-app-id", _config.AppId);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<QobuzCatalogSearchResponse>(stream, _serializerOptions, cancellationToken);
    }

    public async Task<List<int>> GetAlbumPageTrackIdsAsync(string albumUrl, CancellationToken cancellationToken)
    {
        var html = await GetQobuzPageHtmlAsync(albumUrl, cancellationToken);
        if (string.IsNullOrWhiteSpace(html))
        {
            return new List<int>();
        }

        return ParseAlbumPageTrackIds(html);
    }

    public async Task<List<QobuzTrack>> GetAlbumPageTracksAsync(string albumUrl, CancellationToken cancellationToken)
    {
        var html = await GetQobuzPageHtmlAsync(albumUrl, cancellationToken);
        if (string.IsNullOrWhiteSpace(html))
        {
            return new List<QobuzTrack>();
        }

        return ParseAlbumPageTracks(html);
    }

    private async Task<string?> GetQobuzPageHtmlAsync(string albumUrl, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(albumUrl, UriKind.Absolute, out var uri)
            || !uri.Host.EndsWith("qobuz.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return await _httpClient.GetStringAsync(uri, cancellationToken);
    }

    private static List<int> ParseAlbumPageTrackIds(string html)
    {
        var trackIds = new List<int>();
        foreach (Match match in AlbumTrackIdRegex.Matches(html))
        {
            if (int.TryParse(match.Groups["id"].Value, out var trackId))
            {
                trackIds.Add(trackId);
            }
        }

        return trackIds.Distinct().ToList();
    }

    private static List<QobuzTrack> ParseAlbumPageTracks(string html)
    {
        var tracks = new List<QobuzTrack>();
        foreach (Match match in AlbumTrackBlockRegex.Matches(html))
        {
            if (!int.TryParse(match.Groups["id"].Value, out var trackId))
            {
                continue;
            }

            var track = ParseAlbumTrackMetadata(match.Groups["metadata"].Value);
            if (track == null)
            {
                continue;
            }

            track.Id = track.Id > 0 ? track.Id : trackId;
            track.Duration = ParseDurationSeconds(match.Groups["duration"].Value);
            tracks.Add(track);
        }

        return tracks
            .Where(static track => track.Id > 0)
            .GroupBy(static track => track.Id)
            .Select(static group => group.First())
            .ToList();
    }

    private static QobuzTrack? ParseAlbumTrackMetadata(string encodedMetadata)
    {
        var metadata = WebUtility.HtmlDecode(encodedMetadata);
        if (string.IsNullOrWhiteSpace(metadata))
        {
            return null;
        }

        using var document = JsonDocument.Parse(metadata);
        var root = document.RootElement;
        var trackId = ReadInt32(root, "item_id");
        var title = ReadString(root, "item_name");
        if (trackId <= 0 && string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var artistName = ReadString(root, "item_brand");
        var albumTitle = ReadString(root, "item_category");
        var track = new QobuzTrack
        {
            Id = trackId,
            Title = title,
            Performer = string.IsNullOrWhiteSpace(artistName) ? null : new QobuzArtist { Name = artistName },
            Album = string.IsNullOrWhiteSpace(albumTitle)
                ? null
                : new QobuzAlbum
                {
                    Title = albumTitle,
                    Artists = string.IsNullOrWhiteSpace(artistName) ? new List<QobuzArtist>() : [new QobuzArtist { Name = artistName }]
                }
        };

        ApplyVariantQuality(track, ReadString(root, "item_variant_max"));
        return track;
    }

    private static void ApplyVariantQuality(QobuzTrack track, string? variant)
    {
        if (string.IsNullOrWhiteSpace(variant))
        {
            return;
        }

        var bitDepthMatch = Regex.Match(variant, @"(?<bits>\d+)\s*-?\s*bits?", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250));
        if (bitDepthMatch.Success && int.TryParse(bitDepthMatch.Groups["bits"].Value, out var bitDepth))
        {
            track.MaximumBitDepth = bitDepth;
            track.HiRes = bitDepth >= 24;
        }

        var sampleRateMatch = Regex.Match(variant, @"(?<rate>\d+(?:\.\d+)?)\s*k\s*hz", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250));
        if (sampleRateMatch.Success && double.TryParse(sampleRateMatch.Groups["rate"].Value, out var sampleRate))
        {
            track.MaximumSamplingRate = sampleRate;
        }
    }

    private static int ParseDurationSeconds(string duration)
    {
        var parts = duration.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2
            && int.TryParse(parts[0], out var minutes)
            && int.TryParse(parts[1], out var seconds))
        {
            return (minutes * 60) + seconds;
        }

        if (parts.Length == 3
            && int.TryParse(parts[0], out var hours)
            && int.TryParse(parts[1], out minutes)
            && int.TryParse(parts[2], out seconds))
        {
            return (hours * 3600) + (minutes * 60) + seconds;
        }

        return 0;
    }

    private static string? ReadString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int ReadInt32(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value)
               && value.ValueKind == JsonValueKind.Number
               && value.TryGetInt32(out var result)
            ? result
            : 0;
    }

    public async Task<QobuzArtist?> GetArtistAsync(int artistId, string store, int offset, int limit, CancellationToken cancellationToken)
    {
        var resolvedStore = QobuzStoreManager.NormalizeStore(store, _config.DefaultStore);
        var zone = QobuzStoreManager.GetZone(resolvedStore);
        var cookies = await GetStoreCookiesAsync(resolvedStore, cancellationToken);
        if (string.IsNullOrWhiteSpace(cookies))
        {
            return null;
        }

        var url = $"/api.json/0.2/artist/get?artist_id={artistId}&extra=albums_with_last_release&limit={limit}&offset={offset}&zone={zone}&store={resolvedStore}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("x-app-id", _config.AppId);
        request.Headers.TryAddWithoutValidation("cookie", cookies);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<QobuzArtist>(stream, _serializerOptions, cancellationToken);
    }

    public async Task<QobuzTrackSearchResponse?> SearchTracksAsync(string query, int limit, int offset, CancellationToken cancellationToken)
    {
        var url = $"/api.json/0.2/track/search?query={Uri.EscapeDataString(query)}&limit={limit}&offset={offset}&app_id={_config.AppId}";
        return await GetAsync<QobuzTrackSearchResponse>(url, cancellationToken);
    }

    public async Task<QobuzAlbumSearchResponse?> SearchAlbumsAsync(string query, int limit, int offset, CancellationToken cancellationToken)
    {
        var url = $"/api.json/0.2/album/search?query={Uri.EscapeDataString(query)}&limit={limit}&offset={offset}&app_id={_config.AppId}";
        return await GetAsync<QobuzAlbumSearchResponse>(url, cancellationToken);
    }

    public async Task<QobuzArtistSearchResponse?> SearchArtistsAsync(string query, int limit, int offset, CancellationToken cancellationToken)
    {
        var url = $"/api.json/0.2/artist/search?query={Uri.EscapeDataString(query)}&limit={limit}&offset={offset}&app_id={_config.AppId}";
        return await GetAsync<QobuzArtistSearchResponse>(url, cancellationToken);
    }

    public async Task<QobuzTrack?> GetTrackAsync(int trackId, CancellationToken cancellationToken)
    {
        var url = $"/api.json/0.2/track/get?track_id={trackId}&app_id={_config.AppId}";
        return await GetAsync<QobuzTrack>(url, cancellationToken);
    }

    private async Task<T?> GetAsync<T>(string url, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return default;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, _serializerOptions, cancellationToken);
    }

    private async Task<string?> GetStoreCookiesAsync(string store, CancellationToken cancellationToken)
    {
        var cacheKey = $"qobuz_store_cookie_{store}";
        if (_cache.TryGetValue<string>(cacheKey, out var cached) && !string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        var response = await _httpClient.GetAsync($"/{store}/discover", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        if (!response.Headers.TryGetValues("set-cookie", out var cookies))
        {
            return null;
        }

        var cookieHeader = string.Join("; ", cookies);
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            return null;
        }

        _cache.Set(cacheKey, cookieHeader, TimeSpan.FromMinutes(_config.CookieCacheMinutes));
        return cookieHeader;
    }
}
