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
        using var document = await GetJsonAsync(
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

        if (document == null)
        {
            return null;
        }

        if (TryGetRootStatusCode(document.RootElement, out var statusCode) && statusCode == 401)
        {
            _logger.LogWarning("Musixmatch captcha on lyrics fetch; retrying after delay.");
            if (retryCount >= 3)
            {
                return null;
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retryCount + 3)), cancellationToken);
            return await FetchLyricsAsync(title, artist, cancellationToken, retryCount + 1);
        }

        return ParseMacroCallsBody(document.RootElement);
    }

    private async Task<JsonDocument?> GetJsonAsync(string action, Dictionary<string, string> query, CancellationToken cancellationToken)
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
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private MusixmatchMacroCallsBody<MusixmatchBody>? ParseMacroCallsBody(JsonElement root)
    {
        if (!TryGetMacroCallsElement(root, out var macroCallsElement))
        {
            return null;
        }

        var body = new MusixmatchMacroCallsBody<MusixmatchBody>();
        foreach (var macroCall in macroCallsElement.EnumerateObject())
        {
            body.MacroCalls[macroCall.Name] = ParseMacroCallResponse(macroCall.Value, macroCall.Name);
        }

        return body;
    }

    private MusixmatchResponse<MusixmatchBody> ParseMacroCallResponse(JsonElement macroCallValue, string macroCallName)
    {
        var response = new MusixmatchResponse<MusixmatchBody>();
        if (!macroCallValue.TryGetProperty("message", out var message))
        {
            return response;
        }

        if (message.TryGetProperty("header", out var header)
            && header.TryGetProperty("status_code", out var statusCodeElement)
            && statusCodeElement.ValueKind == JsonValueKind.Number
            && statusCodeElement.TryGetInt32(out var statusCode))
        {
            response.Message.Header.StatusCode = statusCode;
        }

        if (!message.TryGetProperty("body", out var bodyElement))
        {
            return response;
        }

        if (bodyElement.ValueKind == JsonValueKind.Null)
        {
            return response;
        }

        if (bodyElement.ValueKind != JsonValueKind.Object)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Musixmatch macro body for {MacroCall} returned {BodyKind}; skipping strict body mapping.",
                    macroCallName,
                    bodyElement.ValueKind);
            }
            return response;
        }

        try
        {
            response.Message.Body = bodyElement.Deserialize<MusixmatchBody>(_jsonOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Musixmatch macro body parsing failed for {MacroCall}; continuing with partial response.",
                macroCallName);
        }

        return response;
    }

    private static bool TryGetRootStatusCode(JsonElement root, out int statusCode)
    {
        statusCode = default;
        if (!root.TryGetProperty("message", out var message)
            || !message.TryGetProperty("header", out var header)
            || !header.TryGetProperty("status_code", out var statusCodeElement)
            || statusCodeElement.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        return statusCodeElement.TryGetInt32(out statusCode);
    }

    private static bool TryGetMacroCallsElement(JsonElement root, out JsonElement macroCalls)
    {
        macroCalls = default;
        if (!root.TryGetProperty("message", out var message)
            || !message.TryGetProperty("body", out var body)
            || !body.TryGetProperty("macro_calls", out var macroCallsElement)
            || macroCallsElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        macroCalls = macroCallsElement;
        return true;
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
