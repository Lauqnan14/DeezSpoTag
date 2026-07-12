using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class BeatportClient
{
    private const string ApiBase = "https://api.beatport.com/v4/";
    private const string InvalidArt = "ab2d1d04-233d-4b08-8234-9782b34dcab8";
    private readonly HttpClient _httpClient;
    private readonly BeatportTokenService _tokens;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public BeatportClient(HttpClient httpClient, BeatportTokenService tokens)
    {
        _httpClient = httpClient; _tokens = tokens;
        _httpClient.Timeout = TimeSpan.FromSeconds(60);
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DeezSpoTag/Beatport-v4");
    }

    public Task<BeatportTrackResults?> SearchAsync(string query, int page, int resultsPerPage, CancellationToken cancellationToken)
        => SendAsync<BeatportTrackResults>($"catalog/search/?q={Uri.EscapeDataString(query.Trim())}&type=tracks&page={Math.Max(1, page)}&per_page={Math.Clamp(resultsPerPage, 1, 100)}", cancellationToken);

    public Task<BeatportTrack?> GetTrackAsync(long id, CancellationToken cancellationToken)
        => SendAsync<BeatportTrack>($"catalog/tracks/{id}/", cancellationToken, notFoundReturnsNull: true);

    public Task<BeatportRelease?> GetReleaseAsync(long id, CancellationToken cancellationToken)
        => SendAsync<BeatportRelease>($"catalog/releases/{id}/", cancellationToken, notFoundReturnsNull: true);

    private async Task<T?> SendAsync<T>(string relativeUrl, CancellationToken cancellationToken, bool notFoundReturnsNull = false)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = await _tokens.GetAccessTokenAsync(forceRefresh: attempt > 0, cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + relativeUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue;
            if (notFoundReturnsNull && response.StatusCode == HttpStatusCode.NotFound) return default;
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new HttpRequestException("Beatport rate limit exceeded.", null, response.StatusCode);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<T>(stream, _jsonOptions, cancellationToken);
        }
        throw new HttpRequestException("Beatport authorization expired; reconnect the provider.", null, HttpStatusCode.Unauthorized);
    }

    public static string? GetArt(BeatportRelease release, int artResolution)
    {
        if (string.IsNullOrWhiteSpace(release.Image?.DynamicUri)
            || release.Image.DynamicUri.Contains(InvalidArt, StringComparison.OrdinalIgnoreCase)) return null;
        var resolution = Math.Clamp(artResolution, 64, 3000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return release.Image.DynamicUri.Replace("{w}", resolution, StringComparison.OrdinalIgnoreCase)
            .Replace("{h}", resolution, StringComparison.OrdinalIgnoreCase)
            .Replace("{x}", resolution, StringComparison.OrdinalIgnoreCase)
            .Replace("{y}", resolution, StringComparison.OrdinalIgnoreCase);
    }
}
