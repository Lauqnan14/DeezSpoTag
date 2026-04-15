using System.Net.Http.Json;
using System.Text.Json.Serialization;
using DeezSpoTag.Services.Apple;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Apple;

public sealed class AppleWidevineLicenseClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AppleWidevineLicenseClient> _logger;

    // Different license endpoints for regular tracks vs radio stations (matching GUI behavior)
    private const string RegularLicenseEndpoint = "https://play.itunes.apple.com/WebObjects/MZPlay.woa/wa/acquireWebPlaybackLicense";
    private const string RadioLicenseEndpoint = "https://play.itunes.apple.com/WebObjects/MZPlay.woa/web/radio/versions/1/license";

    public AppleWidevineLicenseClient(IHttpClientFactory httpClientFactory, ILogger<AppleWidevineLicenseClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<byte[]> AcquireKeyAsync(
        AppleWidevineLicenseRequest licenseRequest,
        CancellationToken cancellationToken)
    {
        var adamId = licenseRequest.AdamId;
        var authorizationToken = licenseRequest.AuthorizationToken;
        var mediaUserToken = licenseRequest.MediaUserToken;
        var keyUri = licenseRequest.KeyUri;
        var uriPrefix = licenseRequest.UriPrefix;
        var pssh = licenseRequest.Pssh;
        if (!HasValidTokens(authorizationToken, mediaUserToken))
        {
            return Array.Empty<byte>();
        }

        var initData = Convert.FromBase64String(pssh);
        var widevineHeader = initData.Length > 32 ? initData[32..] : initData;
        var request = AppleWidevineCdm.BuildRequest(widevineHeader);

        var payload = new LicenseRequestPayload
        {
            Challenge = Convert.ToBase64String(request.SignedRequest),
            KeySystem = "com.widevine.alpha",
            // Apple expects the original HLS key URI (typically "<prefix>,<kid>") in this field.
            // Using "<prefix>,<pssh>" can fail for Atmos tracks.
            Uri = string.IsNullOrWhiteSpace(keyUri) ? $"{uriPrefix},{pssh}" : keyUri,
            AdamId = adamId,
            IsLibrary = false,
            UserInitiated = true
        };

        var licenseEndpoint = ResolveLicenseEndpoint(adamId, licenseRequest.LicenseEndpointOverride);

        var client = _httpClientFactory.CreateClient();
        const int maxAttempts = 3;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            using var httpRequest = BuildLicenseHttpRequest(
                licenseEndpoint,
                payload,
                authorizationToken,
                mediaUserToken);

            using var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return await TryExtractLicenseKeyAsync(response, adamId, request.LicenseRequestMsg, cancellationToken);
            }

            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "Apple license request failed: StatusCode={StatusCode}, Attempt={Attempt}/{MaxAttempts}, Response={Response}",
                response.StatusCode,
                attempt + 1,
                maxAttempts,
                errorBody.Length > 500 ? errorBody[..500] + "..." : errorBody);

            LogLicenseAuthorizationFailure(response.StatusCode);
            if (await DelayForRetryIfNeededAsync(response, attempt, maxAttempts, cancellationToken))
            {
                continue;
            }

            return Array.Empty<byte>();
        }

        return Array.Empty<byte>();
    }

    private void LogLicenseAuthorizationFailure(System.Net.HttpStatusCode statusCode)
    {
        if (statusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            _logger.LogError("Apple license 401 Unauthorized: authorizationToken may be expired.");
            return;
        }

        if (statusCode == System.Net.HttpStatusCode.Forbidden)
        {
            _logger.LogError("Apple license 403 Forbidden: mediaUserToken may be expired or invalid.");
        }
    }

    private async Task<bool> DelayForRetryIfNeededAsync(
        HttpResponseMessage response,
        int attempt,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        if (!ShouldRetry(response.StatusCode, attempt, maxAttempts))
        {
            return false;
        }

        var delay = ComputeRetryDelay(response, attempt);
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Apple license retry: status={StatusCode}, waiting {Delay}ms.",
                response.StatusCode,
                delay.TotalMilliseconds);
        }
        await Task.Delay(delay, cancellationToken);
        return true;
    }

    private bool HasValidTokens(string authorizationToken, string mediaUserToken)
    {
        if (string.IsNullOrWhiteSpace(authorizationToken))
        {
            _logger.LogError("Apple license acquisition failed: authorizationToken is missing.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(mediaUserToken))
        {
            _logger.LogError("Apple license acquisition failed: mediaUserToken is missing.");
            return false;
        }

        if (mediaUserToken.Length < 50)
        {
            _logger.LogError("Apple license acquisition failed: mediaUserToken appears invalid (length={Length}).", mediaUserToken.Length);
            return false;
        }

        return true;
    }

    private string ResolveLicenseEndpoint(string adamId, string? licenseEndpointOverride)
    {
        var isRadioStation = adamId.StartsWith("ra.", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(licenseEndpointOverride))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Apple license: Using station key server endpoint for adamId={AdamId}", adamId);            }
            return licenseEndpointOverride;
        }

        if (isRadioStation)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Apple license: Using radio station endpoint for adamId={AdamId}", adamId);            }
            return RadioLicenseEndpoint;
        }

        return RegularLicenseEndpoint;
    }

    private static HttpRequestMessage BuildLicenseHttpRequest(
        string licenseEndpoint,
        LicenseRequestPayload payload,
        string authorizationToken,
        string mediaUserToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, licenseEndpoint)
        {
            Content = JsonContent.Create(payload)
        };

        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {authorizationToken}");
        request.Headers.TryAddWithoutValidation("x-apple-music-user-token", mediaUserToken);
        request.Headers.TryAddWithoutValidation("Media-User-Token", mediaUserToken);
        request.Headers.TryAddWithoutValidation("Cookie", $"media-user-token={mediaUserToken}");
        request.Headers.TryAddWithoutValidation("x-apple-renewal", "true");
        request.Headers.TryAddWithoutValidation("Origin", "https://music.apple.com");
        request.Headers.TryAddWithoutValidation("Referer", "https://music.apple.com/");
        request.Headers.TryAddWithoutValidation("User-Agent", AppleUserAgentPool.GetAuthenticatedUserAgent());
        return request;
    }

    private async Task<byte[]> TryExtractLicenseKeyAsync(
        HttpResponseMessage response,
        string adamId,
        byte[] licenseRequestMsg,
        CancellationToken cancellationToken)
    {
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        LicenseResponsePayload? responsePayload;
        try
        {
            responsePayload = System.Text.Json.JsonSerializer.Deserialize<LicenseResponsePayload>(responseBody);
        }
        catch (System.Text.Json.JsonException ex)
        {
            _logger.LogWarning(ex, "Apple license response JSON parse failed. Response: {Response}",
                responseBody.Length > 500 ? responseBody[..500] + "..." : responseBody);
            return Array.Empty<byte>();
        }

        if (responsePayload == null)
        {
            _logger.LogWarning("Apple license response deserialized to null.");
            return Array.Empty<byte>();
        }

        if (responsePayload.ErrorCode != 0 || responsePayload.Status != 0)
        {
            _logger.LogWarning(
                "Apple license acquisition failed: ErrorCode={ErrorCode}, Status={Status}. This may indicate an expired mediaUserToken.",
                responsePayload.ErrorCode,
                responsePayload.Status);
            return Array.Empty<byte>();
        }

        var licenseBytes = Convert.FromBase64String(responsePayload.License ?? string.Empty);
        if (licenseBytes.Length == 0)
        {
            _logger.LogWarning("Apple license response contained empty license data.");
            return Array.Empty<byte>();
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Apple license acquired successfully for adamId={AdamId}", adamId);        }
        return AppleWidevineCdm.ExtractContentKey(licenseBytes, licenseRequestMsg);
    }

    private static bool ShouldRetry(System.Net.HttpStatusCode statusCode, int attempt, int maxAttempts)
    {
        if (attempt >= maxAttempts - 1)
        {
            return false;
        }

        return statusCode == System.Net.HttpStatusCode.TooManyRequests || (int)statusCode >= 500;
    }

    private static TimeSpan ComputeRetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            return response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
        }

        return TimeSpan.FromSeconds(Math.Pow(2, attempt));
    }

    private sealed class LicenseRequestPayload
    {
        [JsonPropertyName("challenge")]
        public string Challenge { get; set; } = string.Empty;

        [JsonPropertyName("key-system")]
        public string KeySystem { get; set; } = "";

        [JsonPropertyName("uri")]
        public string Uri { get; set; } = string.Empty;

        [JsonPropertyName("adamId")]
        public string AdamId { get; set; } = string.Empty;

        [JsonPropertyName("isLibrary")]
        public bool IsLibrary { get; set; }

        [JsonPropertyName("user-initiated")]
        public bool UserInitiated { get; set; }
    }

    private sealed class LicenseResponsePayload
    {
        [JsonPropertyName("errorCode")]
        public int ErrorCode { get; set; }

        [JsonPropertyName("license")]
        public string? License { get; set; }

        [JsonPropertyName("status")]
        public int Status { get; set; }
    }

    public sealed record AppleWidevineLicenseRequest(
        string AdamId,
        string AuthorizationToken,
        string MediaUserToken,
        string KeyUri,
        string UriPrefix,
        string Pssh,
        string? LicenseEndpointOverride = null);
}
