using System.Diagnostics;
using DeezSpoTag.Core.Models.Soulseek;
using DeezSpoTag.Integrations.Soulseek;
using Microsoft.Extensions.Logging.Abstractions;
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

            using var client = _httpClientFactory.CreateClient(nameof(SoulseekConnectionService));
            var adapter = new SlskdClient(client, NullLogger<SlskdClient>.Instance);
            var state = await adapter.GetServerStateAsync(new SlskdCredentials(baseUri.AbsoluteUri, auth.ApiKey ?? string.Empty), timeoutCts.Token);
            var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var isConnected = state.IsConnected;
            var isLoggedIn = state.IsLoggedIn;
            var username = state.Username;

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
        catch (SlskdApiException ex)
        {
            _logger.LogDebug(ex, "slskd verified connection check failed.");
            var status = ex.IsUnauthorized ? "unauthorized"
                : ex.ResponseBody == "server_unresponsive" ? "server_unresponsive"
                : ex.StatusCode == 0 ? "unreachable" : "http_error";
            return SoulseekConnectionCheckResult.Failed(status,
                ex.IsUnauthorized ? "slskd rejected the API key." : ex.Message, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "slskd server response was not valid JSON.");
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
            trimmed = $"{Uri.UriSchemeHttp}://{trimmed}";
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
