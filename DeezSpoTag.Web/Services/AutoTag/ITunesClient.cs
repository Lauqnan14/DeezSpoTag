using System.Text.Json;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class ItunesClient
{
    private const string ItunesHost = "itunes.apple.com";
    private readonly HttpClient _httpClient;
    private readonly ILogger<ItunesClient> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private int _rateLimitPerMinute = 20;
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

    public ItunesClient(HttpClient httpClient, ILogger<ItunesClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("OneTagger/1.0");
        }
    }

    public void SetRateLimit(int rateLimitPerMinute)
    {
        _rateLimitPerMinute = rateLimitPerMinute;
    }

    public async Task<ItunesSearchResults?> SearchAsync(string query, string? country, int limit, CancellationToken cancellationToken)
    {
        await ApplyRateLimitAsync(cancellationToken);
        var normalizedCountry = NormalizeCountry(country);
        var normalizedLimit = Math.Clamp(limit, 1, 200);
        var url = BuildSearchUri(query, normalizedCountry, normalizedLimit);
        var response = await _httpClient.GetAsync(url, cancellationToken);
        _lastRequest = DateTimeOffset.UtcNow;

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("iTunes search failed with status {Status}.", response.StatusCode);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<ItunesSearchResults>(stream, _jsonOptions, cancellationToken);
    }

    public async Task<ItunesSearchResult?> LookupTrackAsync(string? trackId, string? country, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(trackId))
        {
            return null;
        }

        var cleaned = trackId.Trim();
        if (!long.TryParse(cleaned, out _))
        {
            return null;
        }

        await ApplyRateLimitAsync(cancellationToken);
        var normalizedCountry = NormalizeCountry(country);
        var url = BuildLookupUri(cleaned, normalizedCountry);
        var response = await _httpClient.GetAsync(url, cancellationToken);
        _lastRequest = DateTimeOffset.UtcNow;

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("iTunes track lookup failed with status {Status}.", response.StatusCode);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<ItunesSearchResults>(stream, _jsonOptions, cancellationToken);
        if (payload?.Results == null || payload.Results.Count == 0)
        {
            return null;
        }

        return payload.Results.FirstOrDefault(result =>
                   result.IsTrack &&
                   result.TrackId?.ToString() == cleaned)
               ?? payload.Results.FirstOrDefault(result => result.IsTrack);
    }

    private async Task ApplyRateLimitAsync(CancellationToken cancellationToken)
    {
        if (_rateLimitPerMinute <= 0 || _rateLimitPerMinute == -1)
        {
            return;
        }

        if (_lastRequest == DateTimeOffset.MinValue)
        {
            return;
        }

        var diffMs = (DateTimeOffset.UtcNow - _lastRequest).TotalMilliseconds;
        var reqMs = 1000.0 / (_rateLimitPerMinute / 60.0);
        var waitMs = reqMs - diffMs;
        if (waitMs > 0)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("iTunes rate limit delay: {Delay}ms", waitMs);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(waitMs), cancellationToken);
        }
    }

    private static string NormalizeCountry(string? country)
    {
        if (string.IsNullOrWhiteSpace(country))
        {
            return "us";
        }

        var normalized = country.Trim().ToLowerInvariant();
        if (normalized.Length == 2)
        {
            return normalized;
        }

        return "us";
    }

    private static Uri BuildSearchUri(string query, string country, int limit)
    {
        var queryString = "term=" + Uri.EscapeDataString(query)
            + "&media=music&entity=song"
            + "&country=" + Uri.EscapeDataString(country)
            + "&limit=" + limit;

        return new UriBuilder(Uri.UriSchemeHttps, ItunesHost)
        {
            Path = "search",
            Query = queryString
        }.Uri;
    }

    private static Uri BuildLookupUri(string trackId, string country)
    {
        var queryString = "id=" + Uri.EscapeDataString(trackId)
            + "&entity=song"
            + "&country=" + Uri.EscapeDataString(country);

        return new UriBuilder(Uri.UriSchemeHttps, ItunesHost)
        {
            Path = "lookup",
            Query = queryString
        }.Uri;
    }
}
