using System.Net.Http.Headers;
using System.Text.Json;
using DeezSpoTag.Core.Models.Qobuz;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace DeezSpoTag.Integrations.Qobuz;

public interface IQobuzApiClient
{
    Task<QobuzAutosuggestResponse?> SearchAutosuggestAsync(string store, string query, CancellationToken cancellationToken);
    Task<QobuzArtist?> GetArtistAsync(int artistId, string store, int offset, int limit, CancellationToken cancellationToken);
    Task<QobuzTrackSearchResponse?> SearchTracksAsync(string query, int limit, int offset, CancellationToken cancellationToken);
    Task<QobuzAlbumSearchResponse?> SearchAlbumsAsync(string query, int limit, int offset, CancellationToken cancellationToken);
    Task<QobuzArtistSearchResponse?> SearchArtistsAsync(string query, int limit, int offset, CancellationToken cancellationToken);
    Task<QobuzTrack?> GetTrackAsync(int trackId, CancellationToken cancellationToken);
}

public sealed class QobuzApiClient : IQobuzApiClient
{
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
