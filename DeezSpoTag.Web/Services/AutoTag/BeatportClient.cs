using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class BeatportClient
{
    private const string InvalidArt = "ab2d1d04-233d-4b08-8234-9782b34dcab8";
    private const string BeatportEmbedHost = "embed.beatport.com";
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private BeatportOAuth? _token;

    public BeatportClient(HttpClient httpClient, ILogger<BeatportClient> logger)
    {
        _httpClient = httpClient;
        _ = logger;
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:85.0) Gecko/20100101 Firefox/85.0");
        }
        _httpClient.Timeout = TimeSpan.FromSeconds(60);
    }

    public async Task<BeatportTrackResults?> SearchAsync(string query, int page, int resultsPerPage, CancellationToken cancellationToken)
    {
        var cleared = ClearSearchQuery(query);
        var url = $"https://www.beatport.com/search/tracks?q={Uri.EscapeDataString(cleared)}&page={page}&per-page={resultsPerPage}";
        var html = await _httpClient.GetStringAsync(url, cancellationToken);
        return ExtractNextData<BeatportTrackResults>(html);
    }

    public async Task<BeatportTrack?> GetTrackAsync(long id, CancellationToken cancellationToken)
    {
        var token = await GetTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.beatport.com/v4/catalog/tracks/{id}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<BeatportTrack>(stream, _jsonOptions, cancellationToken);
    }

    public async Task<BeatportRelease?> GetReleaseAsync(long id, CancellationToken cancellationToken)
    {
        var token = await GetTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.beatport.com/v4/catalog/releases/{id}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<BeatportRelease>(stream, _jsonOptions, cancellationToken);
    }

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (_token == null || _token.ExpiresIn <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        {
            var tokenUri = new UriBuilder(Uri.UriSchemeHttps, BeatportEmbedHost)
            {
                Path = "token"
            }.Uri;
            var response = await _httpClient.GetStringAsync(tokenUri, cancellationToken);
            var token = JsonSerializer.Deserialize<BeatportOAuth>(response, _jsonOptions) ?? new BeatportOAuth();
            token.ExpiresIn = token.ExpiresIn * 1000 + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 60000;
            _token = token;
        }

        return _token.AccessToken;
    }

    private static T? ExtractNextData<T>(string html)
    {
        const string marker = "<script id=\"__NEXT_DATA__\"";
        var index = html.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return default;
        }

        var start = html.IndexOf('>', index);
        if (start < 0)
        {
            return default;
        }
        start += 1;
        var end = html.IndexOf("</script>", start, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
        {
            return default;
        }
        var json = html[start..end];
        var node = JsonNode.Parse(json);
        var data = node?["props"]?["pageProps"]?["dehydratedState"]?["queries"]?[0]?["state"]?["data"];
        return data is null ? default : data.Deserialize<T>();
    }

    public static string ClearSearchQuery(string query)
    {
        var open = 0;
        var closed = 0;
        var chars = query.Where(c =>
        {
            if (c == '(')
            {
                if (open > 0) return false;
                open++;
                return true;
            }
            if (c == ')')
            {
                if (closed > 0) return false;
                closed++;
                return true;
            }
            return true;
        });
        return new string(chars.ToArray());
    }

    public static string? GetArt(BeatportRelease release, int artResolution)
    {
        if (release.Image.DynamicUri.Contains(InvalidArt, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var r = artResolution.ToString();
        return release.Image.DynamicUri
            .Replace("{w}", r, StringComparison.OrdinalIgnoreCase)
            .Replace("{h}", r, StringComparison.OrdinalIgnoreCase)
            .Replace("{x}", r, StringComparison.OrdinalIgnoreCase)
            .Replace("{y}", r, StringComparison.OrdinalIgnoreCase);
    }
}
