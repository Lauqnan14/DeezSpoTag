using System.Net;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;

#pragma warning disable CA1865
namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class DeezerClient
{
    private const string HttpsScheme = "https";
    private const string DeezerApiHost = "api.deezer.com";
    private const string DeezerGatewayHost = "www.deezer.com";
    private const string DeezerGatewayPath = "/ajax/gw-light.php";
    private const string DeezerCdnImageHost = "e-cdns-images.dzcdn.net";
    private const int RateLimitCode = 4;
    private const int MaxAttempts = 3;
    private static readonly string GatewayBaseUrl = BuildUrl(DeezerGatewayHost, DeezerGatewayPath);
    private static readonly TimeSpan[] RetryBackoff =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4)
    ];
    private readonly HttpClient _httpClient;
    private readonly ILogger<DeezerClient> _logger;
    private bool _hasArl;
    private string? _gatewayApiToken;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public DeezerClient(HttpClient httpClient, ILogger<DeezerClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public void SetArl(string? arl)
    {
        _httpClient.DefaultRequestHeaders.Remove("Cookie");

        if (string.IsNullOrWhiteSpace(arl))
        {
            _hasArl = false;
            _gatewayApiToken = null;
            return;
        }

        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", $"arl={arl}");
        _hasArl = true;
        _gatewayApiToken = null;
    }

    public async Task<DeezerSearchResults<DeezerTrack>?> SearchTracksAsync(string query, CancellationToken cancellationToken)
    {
        return await GetAsync<DeezerSearchResults<DeezerTrack>>("/search/track", new Dictionary<string, string> { ["q"] = query }, cancellationToken);
    }

    public async Task<DeezerSearchResults<DeezerTrack>?> SearchTracksByIsrcAsync(string isrc, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(isrc))
        {
            return null;
        }

        var normalized = isrc.Trim().Replace("\"", string.Empty, StringComparison.Ordinal);
        var exact = await SearchTracksAsync($"isrc:\"{normalized}\"", cancellationToken);
        if (exact?.Data.Count > 0)
        {
            return exact;
        }

        return await SearchTracksAsync($"isrc:{normalized}", cancellationToken);
    }

    public async Task<DeezerTrackFull?> GetTrackAsync(long id, CancellationToken cancellationToken)
    {
        return await GetAsync<DeezerTrackFull>($"/track/{id}", new Dictionary<string, string>(), cancellationToken);
    }

    public async Task<DeezerAlbumFull?> GetAlbumAsync(long id, CancellationToken cancellationToken)
    {
        return await GetAsync<DeezerAlbumFull>($"/album/{id}", new Dictionary<string, string>(), cancellationToken);
    }

    public async Task<DeezerLyricsPayload?> GetLyricsAsync(string trackId, CancellationToken cancellationToken)
    {
        if (!_hasArl || string.IsNullOrWhiteSpace(trackId))
        {
            return null;
        }

        var payload = await GatewayCallAsync("song.getLyrics", new Dictionary<string, object> { ["SNG_ID"] = trackId }, cancellationToken);
        if (payload == null)
        {
            return null;
        }

        var payloadValue = payload.Value;
        if (payloadValue.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new DeezerLyricsPayload();

        if (TryGetPropertyIgnoreCase(payloadValue, "LYRICS_TEXT", out var unsyncedElement) &&
            unsyncedElement.ValueKind == JsonValueKind.String)
        {
            result.UnsyncedLyrics = unsyncedElement.GetString();
        }

        if (TryGetPropertyIgnoreCase(payloadValue, "LYRICS_SYNC_JSON", out var syncedElement))
        {
            result.SyncedLyrics = ParseSyncedLyrics(syncedElement);
        }

        if (result.SyncedLyrics.Count == 0 &&
            TryGetPropertyIgnoreCase(payloadValue, "LYRICS_SYNC", out var fallbackSyncElement))
        {
            result.SyncedLyrics = ParseSyncedLyrics(fallbackSyncElement);
        }

        if (string.IsNullOrWhiteSpace(result.UnsyncedLyrics) && result.SyncedLyrics.Count == 0)
        {
            return null;
        }

        return result;
    }

    public static string BuildImageUrl(string imageType, string md5, int resolution)
    {
        return BuildUrl(DeezerCdnImageHost, $"/images/{imageType}/{md5}/{resolution}x{resolution}-000000-80-0-0.jpg");
    }

    private static string BuildUrl(string host, string path)
        => $"{HttpsScheme}://{host}{path}";

    private async Task<T?> GetAsync<T>(string path, Dictionary<string, string> query, CancellationToken cancellationToken)
    {
        var url = BuildRequestUrl(path, query);
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var outcome = await ExecuteGetAttemptAsync<T>(url, attempt, cancellationToken);
            if (outcome.RetryRequested)
            {
                continue;
            }

            return outcome.Result;
        }

        return default;
    }

    private static string BuildRequestUrl(string path, IReadOnlyDictionary<string, string> query)
    {
        var url = BuildUrl(DeezerApiHost, path);
        if (query.Count == 0)
        {
            return url;
        }

        return $"{url}?{string.Join("&", query.Select(kvp => kvp.Key + "=" + Uri.EscapeDataString(kvp.Value)))}";
    }

    private async Task<GetAttemptOutcome<T>> ExecuteGetAttemptAsync<T>(
        string url,
        int attempt,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var statusOutcome = await HandleStatusCodeAsync<T>(response, attempt, cancellationToken);
            if (statusOutcome.HasValue)
            {
                return statusOutcome.Value;
            }

            var payload = await ReadResponsePayloadAsync(response, cancellationToken);
            if (string.IsNullOrWhiteSpace(payload))
            {
                _logger.LogWarning("Deezer request returned an empty payload.");
                return GetAttemptOutcome<T>.Complete();
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(payload);
            }
            catch (JsonException ex)
            {
                return await HandleInvalidJsonAsync<T>(attempt, ex, cancellationToken);
            }

            using (doc)
            {
                var apiErrorOutcome = await HandleApiErrorAsync<T>(doc.RootElement, attempt, response, cancellationToken);
                if (apiErrorOutcome.HasValue)
                {
                    return apiErrorOutcome.Value;
                }

                return GetAttemptOutcome<T>.Complete(doc.RootElement.Deserialize<T>(_jsonOptions));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await HandleUnexpectedRequestErrorAsync<T>(attempt, ex, cancellationToken);
        }
    }

    private async Task<GetAttemptOutcome<T>?> HandleStatusCodeAsync<T>(
        HttpResponseMessage response,
        int attempt,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return null;
        }

        if (CanRetry(attempt) && ShouldRetry(response.StatusCode))
        {
            await DelayForRetryAsync(attempt, response, cancellationToken);
            return GetAttemptOutcome<T>.Retry();
        }

        _logger.LogWarning("Deezer request failed with status {Status}.", response.StatusCode);
        return GetAttemptOutcome<T>.Complete();
    }

    private async Task<GetAttemptOutcome<T>> HandleInvalidJsonAsync<T>(
        int attempt,
        JsonException ex,
        CancellationToken cancellationToken)
    {
        if (CanRetry(attempt))
        {
            _logger.LogWarning(ex, "Failed parsing Deezer JSON payload (attempt {Attempt}/{MaxAttempts}); retrying.", attempt, MaxAttempts);
            await DelayForRetryAsync(attempt, response: null, cancellationToken);
            return GetAttemptOutcome<T>.Retry();
        }

        _logger.LogWarning(ex, "Failed parsing Deezer JSON payload after {MaxAttempts} attempts.", MaxAttempts);
        return GetAttemptOutcome<T>.Complete();
    }

    private async Task<GetAttemptOutcome<T>> HandleUnexpectedRequestErrorAsync<T>(
        int attempt,
        Exception ex,
        CancellationToken cancellationToken)
    {
        if (CanRetry(attempt))
        {
            _logger.LogWarning(ex, "Deezer request failed (attempt {Attempt}/{MaxAttempts}); retrying.", attempt, MaxAttempts);
            await DelayForRetryAsync(attempt, response: null, cancellationToken);
            return GetAttemptOutcome<T>.Retry();
        }

        _logger.LogWarning(ex, "Deezer request failed after {MaxAttempts} attempts.", MaxAttempts);
        return GetAttemptOutcome<T>.Complete();
    }

    private async Task<GetAttemptOutcome<T>?> HandleApiErrorAsync<T>(
        JsonElement root,
        int attempt,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("error", out var errorElement))
        {
            return null;
        }

        var error = errorElement.Deserialize<DeezerError>(_jsonOptions);
        if (error is { Code: RateLimitCode } && CanRetry(attempt))
        {
            await DelayForRetryAsync(attempt, response, cancellationToken);
            return GetAttemptOutcome<T>.Retry();
        }

        if (error != null)
        {
            _logger.LogWarning("Deezer API error {Code}: {Message}", error.Code, error.Message);
        }

        return GetAttemptOutcome<T>.Complete();
    }

    private static bool CanRetry(int attempt) => attempt < MaxAttempts;

    private readonly record struct GetAttemptOutcome<T>(T? Result, bool RetryRequested)
    {
        public static GetAttemptOutcome<T> Retry() => new(default, true);

        public static GetAttemptOutcome<T> Complete(T? value = default) => new(value, false);
    }

    private static bool ShouldRetry(HttpStatusCode statusCode)
    {
        return statusCode == HttpStatusCode.TooManyRequests
            || statusCode == HttpStatusCode.RequestTimeout
            || (int)statusCode >= 500;
    }

    private static async Task DelayForRetryAsync(int attempt, HttpResponseMessage? response, CancellationToken cancellationToken)
    {
        var retryAfter = response?.Headers?.RetryAfter?.Delta;
        var delay = retryAfter.HasValue && retryAfter.Value > TimeSpan.Zero
            ? retryAfter.Value
            : RetryBackoff[Math.Clamp(attempt - 1, 0, RetryBackoff.Length - 1)];
        await Task.Delay(delay, cancellationToken);
    }

    private static async Task<string> ReadResponsePayloadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        var encoding = response.Content.Headers.ContentEncoding;
        var looksGzip = bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B;

        if (looksGzip || encoding.Contains("gzip", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var input = new MemoryStream(bytes);
                using var gzip = new GZipStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                await gzip.CopyToAsync(output, cancellationToken);
                return Encoding.UTF8.GetString(output.ToArray());
            }
            catch (Exception ex) when (ex is not OperationCanceledException) {
                return Encoding.UTF8.GetString(bytes);
            }
        }

        if (encoding.Contains("deflate", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var input = new MemoryStream(bytes);
                using var deflate = new DeflateStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                await deflate.CopyToAsync(output, cancellationToken);
                return Encoding.UTF8.GetString(output.ToArray());
            }
            catch (Exception ex) when (ex is not OperationCanceledException) {
                return Encoding.UTF8.GetString(bytes);
            }
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private async Task<JsonElement?> GatewayCallAsync(string method, Dictionary<string, object> args, CancellationToken cancellationToken)
    {
        var token = await EnsureGatewayApiTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogWarning("Deezer gateway token unavailable for method {Method}.", method);
            return null;
        }

        var query = $"api_version=1.0&api_token={Uri.EscapeDataString(token)}&input=3&method={Uri.EscapeDataString(method)}&cid={Random.Shared.Next(0, 1_000_000_000)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{GatewayBaseUrl}?{query}")
        {
            Content = new StringContent(JsonSerializer.Serialize(args), Encoding.UTF8, "application/json")
        };

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Deezer gateway method {Method} failed with status {Status}.", method, response.StatusCode);
            return null;
        }

        var payload = await ReadResponsePayloadAsync(response, cancellationToken);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(payload);
        if (doc.RootElement.TryGetProperty("error", out var errorElement) &&
            errorElement.ValueKind == JsonValueKind.Object &&
            TryGetPropertyIgnoreCase(errorElement, "code", out var codeElement) &&
            codeElement.TryGetInt32(out var code) &&
            code != 0)
        {
            _logger.LogWarning("Deezer gateway method {Method} returned error code {Code}.", method, code);
            return null;
        }

        if (!TryGetPropertyIgnoreCase(doc.RootElement, "results", out var results))
        {
            return null;
        }

        return results.Clone();
    }

    private async Task<string?> EnsureGatewayApiTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_gatewayApiToken))
        {
            return _gatewayApiToken;
        }

        var query = "api_version=1.0&api_token=null&input=3&method=deezer.getUserData";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{GatewayBaseUrl}?{query}")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var payload = await ReadResponsePayloadAsync(response, cancellationToken);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(payload);
        if (!TryGetPropertyIgnoreCase(doc.RootElement, "results", out var results) || results.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (TryGetPropertyIgnoreCase(results, "checkForm", out var checkFormElement) &&
            checkFormElement.ValueKind == JsonValueKind.String)
        {
            _gatewayApiToken = checkFormElement.GetString();
        }

        return _gatewayApiToken;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var property = element.EnumerateObject().FirstOrDefault(property =>
                property.NameEquals(name) || property.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (property.Name != null)
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static List<string> ParseSyncedLyrics(JsonElement element) // NOSONAR
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            return ParseSyncedLyricsArray(element);
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return new List<string>();
        }

        var raw = element.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new List<string>();
        }

        return ParseSyncedLyricsString(raw);
    }

    private static List<string> ParseSyncedLyricsArray(JsonElement element)
    {
        var lines = new List<string>();
        foreach (var line in element.EnumerateArray())
        {
            if (!TryParseSyncedLyricLine(line, out var lrcLine))
            {
                continue;
            }

            lines.Add(lrcLine);
        }

        return lines;
    }

    private static List<string> ParseSyncedLyricsString(string raw)
    {
        var lines = new List<string>();
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.Contains("lrc_timestamp", StringComparison.OrdinalIgnoreCase)) // NOSONAR
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    return ParseSyncedLyrics(doc.RootElement);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) {
                // fall through to plain-text parsing
            }
        }

        foreach (var rawLine in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (rawLine.StartsWith("[", StringComparison.Ordinal)) // NOSONAR
            {
                lines.Add(rawLine);
                continue;
            }

            var parts = rawLine.Split('\t', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !int.TryParse(parts[0], out var ms))
            {
                continue;
            }

            var text = WebUtility.HtmlDecode(parts[1]);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            lines.Add($"{BuildLrcTimestamp(ms)}{text}");
        }

        return lines;
    }

    private static bool TryParseSyncedLyricLine(JsonElement line, out string lrcLine)
    {
        lrcLine = string.Empty;
        if (line.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var text = string.Empty;
        var ms = 0;
        var timestamp = string.Empty;

        if (TryGetPropertyIgnoreCase(line, "line", out var lineText) && lineText.ValueKind == JsonValueKind.String)
        {
            text = WebUtility.HtmlDecode(lineText.GetString() ?? string.Empty);
        }

        if (TryGetPropertyIgnoreCase(line, "milliseconds", out var msElement) && msElement.TryGetInt32(out var parsedMs))
        {
            ms = parsedMs;
        }

        if (TryGetPropertyIgnoreCase(line, "lrc_timestamp", out var tsElement) && tsElement.ValueKind == JsonValueKind.String)
        {
            timestamp = tsElement.GetString() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(timestamp))
        {
            timestamp = BuildLrcTimestamp(ms);
        }
        else if (!timestamp.StartsWith("[", StringComparison.Ordinal)) // NOSONAR
        {
            timestamp = $"[{timestamp}]";
        }

        lrcLine = $"{timestamp}{text}";
        return true;
    }

    private static string BuildLrcTimestamp(int milliseconds)
    {
        var timeSpan = TimeSpan.FromMilliseconds(milliseconds);
        return $"[{timeSpan.Minutes:D2}:{timeSpan.Seconds:D2}.{timeSpan.Milliseconds / 10:D2}]";
    }
}
