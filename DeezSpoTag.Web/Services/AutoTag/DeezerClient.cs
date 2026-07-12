using System.Net;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class DeezerClient
{
    private const string HttpsScheme = "https";
    private const string DeezerApiHost = "api.deezer.com";
    private const string DeezerCdnImageHost = "e-cdns-images.dzcdn.net";
    private const int RateLimitCode = 4;
    private const int MaxAttempts = 3;
    private static readonly TimeSpan[] RetryBackoff =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4)
    ];
    private readonly HttpClient _httpClient;
    private readonly ILogger<DeezerClient> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public DeezerClient(HttpClient httpClient, ILogger<DeezerClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
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

    public async Task<DeezerTrackFull?> GetTrackByIsrcAsync(string isrc, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(isrc))
        {
            return null;
        }

        var normalized = new string(isrc.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : await GetAsync<DeezerTrackFull>($"/track/isrc:{normalized}", new Dictionary<string, string>(), cancellationToken);
    }

    public async Task<DeezerAlbumFull?> GetAlbumAsync(long id, CancellationToken cancellationToken)
    {
        return await GetAsync<DeezerAlbumFull>($"/album/{id}", new Dictionary<string, string>(), cancellationToken);
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

            return outcome.Value;
        }

        return default;
    }

    private static string BuildRequestUrl(string path, Dictionary<string, string> query)
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

    private readonly record struct GetAttemptOutcome<T>(T? Value, bool RetryRequested)
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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Encoding.UTF8.GetString(bytes);
            }
        }

        return Encoding.UTF8.GetString(bytes);
    }

}
