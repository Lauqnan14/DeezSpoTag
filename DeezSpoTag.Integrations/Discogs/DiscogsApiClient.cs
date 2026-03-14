using System.Net.Http.Headers;
using System.Text.Json;

namespace DeezSpoTag.Integrations.Discogs;

public sealed class DiscogsApiClient
{
    private const string DiscogsScheme = "https";
    private const string DiscogsHost = "api.discogs.com";
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public DiscogsApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "DeezSpoTag/1.0 +https://github.com");
        }
        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<DiscogsIdentity?> GetIdentityAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var identity = await FetchIdentityAsync(BuildIdentityUrl(), token, cancellationToken);
        if (identity != null)
        {
            return identity;
        }

        var fallbackUrl = $"{BuildIdentityUrl()}?token={Uri.EscapeDataString(token)}";
        return await FetchIdentityAsync(fallbackUrl, token: null, cancellationToken);
    }

    private static string BuildIdentityUrl()
        => new UriBuilder(DiscogsScheme, DiscogsHost) { Path = "/oauth/identity" }.Uri.ToString();

    private async Task<DiscogsIdentity?> FetchIdentityAsync(string url, string? token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Discogs token={token}");
        }
        request.Headers.TryAddWithoutValidation("User-Agent", "DeezSpoTag/1.0 +https://github.com");
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<DiscogsIdentity>(stream, _jsonOptions, cancellationToken);
    }
}

public sealed class DiscogsIdentity
{
    public string? Username { get; set; }
    public string? Name { get; set; }
    public string? Location { get; set; }
    public string? AvatarUrl { get; set; }
    public string? Profile { get; set; }
}
