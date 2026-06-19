using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace DeezSpoTag.Web.Services;

public sealed class SoulseekConnectionService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SoulseekConnectionService> _logger;

    public SoulseekConnectionService(
        IHttpClientFactory httpClientFactory,
        ILogger<SoulseekConnectionService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<SoulseekConnectionCheckResult> CheckAsync(
        SoulseekAuth? auth,
        CancellationToken cancellationToken)
    {
        if (auth is null || string.IsNullOrWhiteSpace(auth.BaseUrl))
        {
            return SoulseekConnectionCheckResult.NotConfigured();
        }

        var baseUri = NormalizeBaseUri(auth.BaseUrl);
        if (baseUri is null)
        {
            return SoulseekConnectionCheckResult.Failed("invalid_url", "slskd URL is invalid.");
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(RequestTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "api/v0/server/state"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrWhiteSpace(auth.ApiKey))
            {
                request.Headers.TryAddWithoutValidation("X-API-Key", auth.ApiKey);
            }

            using var client = _httpClientFactory.CreateClient(nameof(SoulseekConnectionService));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return SoulseekConnectionCheckResult.Failed("unauthorized", "slskd rejected the API key.", elapsedMs);
            }

            if (!response.IsSuccessStatusCode)
            {
                return SoulseekConnectionCheckResult.Failed(
                    "http_error",
                    $"slskd returned HTTP {(int)response.StatusCode}.",
                    elapsedMs);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeoutCts.Token);
            var root = document.RootElement;
            var isConnected = ReadBoolean(root, "isConnected", "IsConnected");
            var isLoggedIn = ReadBoolean(root, "isLoggedIn", "IsLoggedIn");
            var username = ReadString(root, "username", "Username", "login", "Login");

            return new SoulseekConnectionCheckResult(
                Configured: true,
                Reachable: true,
                Connected: isConnected && isLoggedIn,
                Status: isConnected && isLoggedIn ? "connected" : "disconnected",
                Message: isConnected && isLoggedIn
                    ? "slskd is connected to Soulseek."
                    : "slskd is reachable but not logged in to Soulseek.",
                Username: username,
                ResponseTimeMs: elapsedMs,
                CheckedAt: DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SoulseekConnectionCheckResult.Failed("timeout", "slskd connection timed out.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "slskd connection check failed.");
            return SoulseekConnectionCheckResult.Failed("unreachable", "slskd is unavailable.");
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "slskd server/state response was not valid JSON.");
            return SoulseekConnectionCheckResult.Failed("invalid_response", "slskd returned an invalid response.");
        }
    }

    public static Uri? NormalizeBaseUri(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = $"http://{trimmed}";
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        var builder = new UriBuilder(uri)
        {
            Path = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri;
    }

    private static bool ReadBoolean(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var property))
            {
                continue;
            }

            if (property.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return property.GetBoolean();
            }

            if (property.ValueKind == JsonValueKind.String
                && bool.TryParse(property.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return false;
    }

    private static string? ReadString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var property)
                && property.ValueKind == JsonValueKind.String)
            {
                var value = property.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }
}

public sealed record SoulseekConnectionCheckResult(
    bool Configured,
    bool Reachable,
    bool Connected,
    string Status,
    string Message,
    string? Username,
    double? ResponseTimeMs,
    DateTimeOffset? CheckedAt)
{
    public static SoulseekConnectionCheckResult NotConfigured()
        => new(false, false, false, "not_configured", "Soulseek is not configured.", null, null, null);

    public static SoulseekConnectionCheckResult Failed(
        string status,
        string message,
        double? responseTimeMs = null)
        => new(true, false, false, status, message, null, responseTimeMs, DateTimeOffset.UtcNow);
}
