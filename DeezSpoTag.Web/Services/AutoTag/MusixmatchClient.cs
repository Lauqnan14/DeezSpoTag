using System.Text.Json;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class MusixmatchClient
{
    private const string MusixmatchApiBase = "https://apic-desktop.musixmatch.com/ws/1.1";
    private readonly HttpClient _httpClient;
    private readonly ILogger<MusixmatchClient> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _token;

    public MusixmatchClient(HttpClient httpClient, ILogger<MusixmatchClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/86.0.4240.183 Safari/537.36");
        }
        if (!_httpClient.DefaultRequestHeaders.Contains("authority"))
        {
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("authority", "apic-desktop.musixmatch.com");
        }
        if (!_httpClient.DefaultRequestHeaders.Contains("cookie"))
        {
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("cookie", "AWSELBCORS=0; AWSELB=0");
        }
    }

    public async Task<MusixmatchMacroCallsBody<MusixmatchBody>?> FetchLyricsAsync(string title, string artist, CancellationToken cancellationToken, int retryCount = 0)
    {
        var response = await GetAsync<MusixmatchResponse<MusixmatchMacroCallsBody<MusixmatchBody>>>(
            "macro.subtitles.get",
            new Dictionary<string, string>
            {
                ["format"] = "json",
                ["namespace"] = "lyrics_richsynced",
                ["optional_calls"] = "track.richsync",
                ["subtitle_format"] = "lrc",
                ["q_artist"] = artist,
                ["q_track"] = title
            },
            cancellationToken);

        if (response == null)
        {
            return null;
        }

        if (response.Message.Header.StatusCode == 401)
        {
            _logger.LogWarning("Musixmatch captcha on lyrics fetch; retrying after delay.");
            if (retryCount >= 3)
            {
                return null;
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retryCount + 3)), cancellationToken);
            return await FetchLyricsAsync(title, artist, cancellationToken, retryCount + 1);
        }

        return response.Message.Body;
    }

    private async Task<T?> GetAsync<T>(string action, Dictionary<string, string> query, CancellationToken cancellationToken)
    {
        await EnsureTokenAsync(action, cancellationToken);

        query["app_id"] = "web-desktop-app-v1.0";
        if (!string.IsNullOrWhiteSpace(_token))
        {
            query["usertoken"] = _token!;
        }
        query["t"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();

        var url = $"{MusixmatchApiBase}/{action}?" + string.Join("&", query.Select(kvp => kvp.Key + "=" + Uri.EscapeDataString(kvp.Value)));
        var response = await _httpClient.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Musixmatch request {Action} failed with status {Status}.", action, response.StatusCode);
            return default;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, _jsonOptions, cancellationToken);
    }

    private async Task EnsureTokenAsync(string action, CancellationToken cancellationToken)
    {
        if (action == "token.get")
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(_token))
        {
            return;
        }

        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(_token))
            {
                return;
            }

            await FetchTokenAsync(cancellationToken);
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task FetchTokenAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Fetching Musixmatch token.");
        using var document = await GetRawTokenAsync(cancellationToken);
        var statusCode = document.RootElement
            .GetProperty("message")
            .GetProperty("header")
            .GetProperty("status_code")
            .GetInt32();
        if (statusCode == 401)
        {
            _logger.LogWarning("Musixmatch captcha when getting token; waiting 10s.");
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            await FetchTokenAsync(cancellationToken);
            return;
        }

        var token = document.RootElement
            .GetProperty("message")
            .GetProperty("body")
            .GetProperty("user_token")
            .GetString();

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Musixmatch token missing from response.");
        }

        _token = token;
    }

    private async Task<JsonDocument> GetRawTokenAsync(CancellationToken cancellationToken)
    {
        var query = new Dictionary<string, string>
        {
            ["user_language"] = "en",
            ["app_id"] = "web-desktop-app-v1.0",
            ["t"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()
        };
        var url = $"{MusixmatchApiBase}/token.get?" + string.Join("&", query.Select(kvp => kvp.Key + "=" + Uri.EscapeDataString(kvp.Value)));
        var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }
}
