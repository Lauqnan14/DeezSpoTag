using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HtmlAgilityPack;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class BandcampClient
{
    private const string BandcampHost = "bandcamp.com";
    private const string SearchPath = "api/bcsearch_public_api/1/autocomplete_elastic";

    private readonly HttpClient _httpClient;
    private readonly ILogger<BandcampClient> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public BandcampClient(HttpClient httpClient, ILogger<BandcampClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:85.0) Gecko/20100101 Firefox/85.0");
        }
    }

    public async Task<List<BandcampSearchResult>> SearchTracksAsync(string query, CancellationToken cancellationToken)
    {
        var payload = new
        {
            fan_id = default(string),
            full_page = false,
            search_filter = "t",
            search_text = query
        };

        var searchUri = new UriBuilder(Uri.UriSchemeHttps, BandcampHost)
        {
            Path = SearchPath
        }.Uri;

        while (true)
        {
            var response = await _httpClient.PostAsJsonAsync(searchUri, payload, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests || response.StatusCode == HttpStatusCode.Forbidden)
            {
                _logger.LogWarning("Bandcamp rate limit hit; waiting 3s.");
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Bandcamp search failed with status {Status}.", response.StatusCode);
                return new List<BandcampSearchResult>();
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!doc.RootElement.TryGetProperty("auto", out var autoElement) || !autoElement.TryGetProperty("results", out var resultsElement))
            {
                return new List<BandcampSearchResult>();
            }

            return resultsElement.EnumerateArray()
                .Select(item => item.Deserialize<BandcampSearchResult>(_jsonOptions))
                .Where(static result => result != null)
                .Select(static result => result!)
                .ToList();
        }
    }

    public async Task<BandcampTrack?> GetTrackAsync(string url, CancellationToken cancellationToken)
    {
        while (true)
        {
            var response = await _httpClient.GetAsync(url, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests || response.StatusCode == HttpStatusCode.Forbidden)
            {
                _logger.LogWarning("Bandcamp track page rate limit hit; waiting 3s.");
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Bandcamp track page failed with status {Status}.", response.StatusCode);
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var script = doc.DocumentNode.SelectSingleNode("//script[@type='application/ld+json']");
            if (script == null)
            {
                _logger.LogWarning("Bandcamp track page missing ld+json data at {Url}.", url);
                return null;
            }

            var json = HtmlEntity.DeEntitize(script.InnerText);
            return JsonSerializer.Deserialize<BandcampTrack>(json, _jsonOptions);
        }
    }
}
