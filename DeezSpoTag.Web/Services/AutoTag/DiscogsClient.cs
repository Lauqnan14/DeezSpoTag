using System.Net;
using System.Text.Json;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class DiscogsClient
{
    private const string DiscogsApiHost = "api.discogs.com";
    private const string DatabaseSearchPath = "database/search";

    private readonly HttpClient _httpClient;
    private readonly ILogger<DiscogsClient> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private string? _token;
    private int _rateLimitPerMinute = 25;
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

    public DiscogsClient(HttpClient httpClient, ILogger<DiscogsClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DeezSpoTag/1.0 (Discogs)");
        }
    }

    public void SetToken(string token)
    {
        _token = token;
        _rateLimitPerMinute = 60;
    }

    public void SetRateLimit(int rateLimit)
    {
        _rateLimitPerMinute = rateLimit;
    }

    public async Task<bool> ValidateTokenAsync(CancellationToken cancellationToken)
    {
        var response = await GetAsync(DatabaseSearchPath, new Dictionary<string, string> { ["q"] = "test" }, cancellationToken);
        if (response == null)
        {
            return false;
        }
        if (response.StatusCode == HttpStatusCode.OK)
        {
            return true;
        }

        _logger.LogWarning("Discogs token validation failed: {Status}", response.StatusCode);
        return false;
    }

    public async Task<List<DiscogsSearchResult>> SearchAsync(string? type, string? query, string? title, string? artist, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(type)) parameters["type"] = type;
        if (!string.IsNullOrWhiteSpace(query)) parameters["q"] = query;
        if (!string.IsNullOrWhiteSpace(title)) parameters["title"] = title;
        if (!string.IsNullOrWhiteSpace(artist)) parameters["artist"] = artist;

        var response = await GetAsync(DatabaseSearchPath, parameters, cancellationToken);
        if (response == null)
        {
            return new List<DiscogsSearchResult>();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!doc.RootElement.TryGetProperty("results", out var resultsElement) || resultsElement.ValueKind != JsonValueKind.Array)
        {
            return new List<DiscogsSearchResult>();
        }

        var list = new List<DiscogsSearchResult>();
        foreach (var element in resultsElement.EnumerateArray())
        {
            var typeValue = element.GetProperty("type").GetString() ?? string.Empty;
            if (!string.Equals(typeValue, "release", StringComparison.OrdinalIgnoreCase) && !string.Equals(typeValue, "master", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var result = JsonSerializer.Deserialize<DiscogsSearchResult>(element.GetRawText(), _jsonOptions);
                if (result != null)
                {
                    list.Add(result);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Skipping Discogs search result that could not be deserialized.");
            }
        }

        return list;
    }

    public async Task<DiscogsRelease?> GetReleaseAsync(DiscogsReleaseType releaseType, long id, CancellationToken cancellationToken)
    {
        var endpoint = releaseType == DiscogsReleaseType.Master
            ? $"masters/{id}"
            : $"releases/{id}";
        var response = await GetAsync(endpoint, new Dictionary<string, string>(), cancellationToken);
        if (response == null)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        try
        {
            return await JsonSerializer.DeserializeAsync<DiscogsRelease>(stream, _jsonOptions, cancellationToken);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize Discogs release payload for {Type}:{Id}.", releaseType, id);
            return null;
        }
    }

    private async Task<HttpResponseMessage?> GetAsync(string path, Dictionary<string, string> query, CancellationToken cancellationToken)
    {
        await ApplyRateLimitAsync(cancellationToken);
        var queryString = query.Count == 0
            ? string.Empty
            : string.Join("&", query.Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value)}"));

        var requestUrl = new UriBuilder(Uri.UriSchemeHttps, DiscogsApiHost)
        {
            Path = path,
            Query = queryString
        }.Uri;
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        if (!string.IsNullOrWhiteSpace(_token))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Discogs token={_token}");
        }

        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.Headers.TryGetValues("X-Discogs-Ratelimit-Remaining", out var values))
        {
            var remaining = values.FirstOrDefault();
            if (int.TryParse(remaining, out var parsed) && parsed < 1)
            {
                _logger.LogWarning("Discogs rate limit hit, waiting 10s...");
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                return await GetAsync(path, query, cancellationToken);
            }
        }

        _lastRequest = DateTimeOffset.UtcNow;
        return response;
    }

    private async Task ApplyRateLimitAsync(CancellationToken cancellationToken)
    {
        if (_rateLimitPerMinute <= 0)
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
            await Task.Delay(TimeSpan.FromMilliseconds(waitMs), cancellationToken);
        }
    }
}
