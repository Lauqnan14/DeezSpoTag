using System.Net;
using System.Text.Json;
using HtmlAgilityPack;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class BeatsourceClient
{
    private const string BeatsourceApiHost = "api.beatsource.com";

    private readonly HttpClient _httpClient;
    private readonly BeatsourceTokenManager _tokenManager;
    private readonly ILogger<BeatsourceClient> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public BeatsourceClient(HttpClient httpClient, BeatsourceTokenManager tokenManager, ILogger<BeatsourceClient> logger)
    {
        _httpClient = httpClient;
        _tokenManager = tokenManager;
        _logger = logger;
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:85.0) Gecko/20100101 Firefox/85.0");
        }
    }

    public async Task<BeatsourceSearchResponse?> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var token = await _tokenManager.GetTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var searchUri = BuildSearchUri(query);
        using var request = new HttpRequestMessage(HttpMethod.Get, searchUri);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _logger.LogWarning("Beatsource token expired; refreshing.");
            await _tokenManager.InvalidateAsync();
            token = await _tokenManager.GetTokenAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(token))
            {
                return null;
            }

            using var retryRequest = new HttpRequestMessage(HttpMethod.Get, searchUri);
            retryRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var retryResponse = await _httpClient.SendAsync(retryRequest, cancellationToken);
            if (!retryResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("Beatsource search failed with status {Status}.", retryResponse.StatusCode);
                return null;
            }

            await using var retryStream = await retryResponse.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<BeatsourceSearchResponse>(retryStream, _jsonOptions, cancellationToken);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Beatsource search failed with status {Status}.", response.StatusCode);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<BeatsourceSearchResponse>(stream, _jsonOptions, cancellationToken);
    }

    private static Uri BuildSearchUri(string query)
    {
        var queryString = "pubper_page=100&page=1&type=tracks&q=" + Uri.EscapeDataString(query);
        return new UriBuilder(Uri.UriSchemeHttps, BeatsourceApiHost)
        {
            Path = "v4/catalog/search",
            Query = queryString
        }.Uri;
    }
}

public sealed class BeatsourceTokenManager
{
    private const string BeatsourceWebHost = "www.beatsource.com";

    private readonly HttpClient _httpClient;
    private readonly ILogger<BeatsourceTokenManager> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public BeatsourceTokenManager(HttpClient httpClient, ILogger<BeatsourceTokenManager> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:85.0) Gecko/20100101 Firefox/85.0");
        }
    }

    public async Task<string?> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_token) && _expiresAt > DateTimeOffset.UtcNow)
        {
            return _token;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(_token) && _expiresAt > DateTimeOffset.UtcNow)
            {
                return _token;
            }

            var token = await FetchTokenAsync(cancellationToken);
            _token = token.Token;
            _expiresAt = token.ExpiresAt;
            return _token;
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task InvalidateAsync()
    {
        _token = null;
        _expiresAt = DateTimeOffset.MinValue;
        return Task.CompletedTask;
    }

    private async Task<BeatsourceToken> FetchTokenAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Refreshing Beatsource token.");
        var homeUri = new UriBuilder(Uri.UriSchemeHttps, BeatsourceWebHost).Uri;
        var html = await _httpClient.GetStringAsync(homeUri, cancellationToken);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var script = doc.DocumentNode.SelectSingleNode("//script[@id='__NEXT_DATA__']");
        if (script == null)
        {
            throw new InvalidOperationException("Missing Beatsource __NEXT_DATA__ payload.");
        }

        var json = HtmlEntity.DeEntitize(script.InnerText);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("props", out var props) ||
            !props.TryGetProperty("rootStore", out var rootStore) ||
            !rootStore.TryGetProperty("authStore", out var authStore) ||
            !authStore.TryGetProperty("user", out var user))
        {
            throw new InvalidOperationException("Missing Beatsource auth payload.");
        }

        var token = user.GetProperty("access_token").GetString();
        var expiresIn = user.GetProperty("expires_in").GetInt64();
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Missing Beatsource access token.");
        }

        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 100);
        return new BeatsourceToken(token, expiresAt);
    }
}

public sealed record BeatsourceToken(string Token, DateTimeOffset ExpiresAt);
