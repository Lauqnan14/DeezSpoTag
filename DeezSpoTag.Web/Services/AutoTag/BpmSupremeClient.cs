using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class BpmSupremeClient
{
    private const string BpmSupremeApiHost = "api.bpmsupreme.com";
    private const string BpmSupremeDownloadHost = "api.download.bpmsupreme.com";

    private readonly HttpClient _httpClient;
    private readonly ILogger<BpmSupremeClient> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public BpmSupremeClient(HttpClient httpClient, ILogger<BpmSupremeClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/101.0.4951.67 Safari/537.36.");
        }
    }

    public async Task<string> LoginAsync(string email, string password, CancellationToken cancellationToken)
    {
        var payload = new
        {
            device = new
            {
                app_version = "2.0",
                build_version = "1",
                debug = false,
                device_data_os = "web",
                device_uuid = "d2d9dc2f7cf311a3bff7f3ea6df3ba9b",
                language = "en-US"
            },
            email,
            password,
            from = "global-login"
        };

        var loginUri = new UriBuilder(Uri.UriSchemeHttps, BpmSupremeApiHost)
        {
            Path = "v4/login"
        }.Uri;

        var response = await _httpClient.PostAsJsonAsync(loginUri, payload, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<BpmSupremeResponse<BpmSupremeUser>>(_jsonOptions, cancellationToken);
        if (result?.Data == null || string.IsNullOrWhiteSpace(result.Data.SessionToken))
        {
            throw new InvalidOperationException("BPM Supreme login response missing session token.");
        }

        return result.Data.SessionToken;
    }

    public async Task<List<BpmSupremeSong>> SearchAsync(string query, BpmSupremeLibrary library, string sessionToken, CancellationToken cancellationToken)
    {
        var queryString = "term=" + Uri.EscapeDataString(query)
            + "&limit=100&skip=0&library=" + LibraryValue(library)
            + "&hide_remix=0";
        var searchUri = new UriBuilder(Uri.UriSchemeHttps, BpmSupremeDownloadHost)
        {
            Path = "v1/albums",
            Query = queryString
        }.Uri;

        using var request = new HttpRequestMessage(HttpMethod.Get, searchUri);
        request.Headers.TryAddWithoutValidation("Cookie", $"bpm_session={sessionToken}");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.TryGetValues("retry-after", out var values)
                ? values.FirstOrDefault()
                : null;
            var delay = int.TryParse(retryAfter, out var parsed) ? parsed : 3;
            _logger.LogWarning("BPM Supreme rate limited; waiting {Delay}s.", delay);
            await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
            return await SearchAsync(query, library, sessionToken, cancellationToken);
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var result = await JsonSerializer.DeserializeAsync<BpmSupremeResponse<List<BpmSupremeSong>>>(stream, _jsonOptions, cancellationToken);
        return result?.Data ?? new List<BpmSupremeSong>();
    }

    private static string LibraryValue(BpmSupremeLibrary library)
    {
        return library == BpmSupremeLibrary.Latino ? "latin" : "music";
    }
}
