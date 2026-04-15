using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DeezSpoTag.Services.Utils;

/// <summary>
/// Service for managing JWT tokens for Deezer Pipe API authentication
/// Ported from refreezer's JWT token handling
/// </summary>
public class JwtTokenService
{
    private const string HttpsScheme = "https";
    private const string DeezerAuthHost = "auth.deezer.com";
    private static readonly string ArlLoginUrl = BuildUrl(DeezerAuthHost, "/login/arl?jo=p&rto=c&i=c");
    private readonly ILogger<JwtTokenService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private string? _cachedToken;
    private DateTime _tokenExpiry = DateTime.MinValue;
    private readonly TimeSpan _tokenValidityBuffer = TimeSpan.FromMinutes(5); // Refresh 5 minutes before expiry

    public JwtTokenService(ILogger<JwtTokenService> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    private static string BuildUrl(string host, string pathAndQuery)
    {
        var authority = new UriBuilder(HttpsScheme, host).Uri.GetLeftPart(UriPartial.Authority);
        return $"{authority}{pathAndQuery}";
    }

    /// <summary>
    /// Get a valid JWT token for Pipe API authentication
    /// </summary>
    public async Task<string?> GetJsonWebTokenAsync(string arl, string? sid = null, CancellationToken cancellationToken = default)
    {
        try
        {
            // Check if we have a valid cached token
            if (!string.IsNullOrEmpty(_cachedToken) && DateTime.UtcNow < _tokenExpiry.Subtract(_tokenValidityBuffer))
            {
                return _cachedToken;
            }

            // Request new token
            var token = await RequestNewTokenAsync(arl, sid, cancellationToken);
            if (!string.IsNullOrEmpty(token))
            {
                _cachedToken = token;
                // JWT tokens typically expire in 1 hour, but we'll be conservative
                _tokenExpiry = DateTime.UtcNow.AddMinutes(50);
                _logger.LogDebug("Successfully obtained new JWT token");
            }

            return token;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to get JWT token");
            return null;
        }
    }

    /// <summary>
    /// Request a new JWT token from Deezer auth service
    /// </summary>
    private async Task<string?> RequestNewTokenAsync(string arl, string? sid, CancellationToken cancellationToken)
    {
        try
        {
            using var httpClient = _httpClientFactory.CreateClient("JwtTokenService");

            using var request = new HttpRequestMessage(HttpMethod.Post, ArlLoginUrl);

            // Set headers
            request.Headers.Add("User-Agent", "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/96.0.4664.110 Safari/537.36");
            request.Headers.Add("Accept", "*/*");
            request.Headers.Add("Accept-Language", "en-US,en;q=0.9");

            // Set cookies
            var cookieValue = $"arl={arl}";
            if (!string.IsNullOrEmpty(sid))
            {
                cookieValue += $"; sid={sid}";
            }
            request.Headers.Add("Cookie", cookieValue);

            // Set content type
            request.Content = new StringContent("", System.Text.Encoding.UTF8, "application/json");

            using var response = await httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("JWT token request failed with status: {StatusCode}", response.StatusCode);
                return null;
            }

            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

            if (string.IsNullOrEmpty(responseContent))
            {
                _logger.LogWarning("Empty response from JWT token endpoint");
                return null;
            }

            // Parse JSON response
            using var jsonDoc = JsonDocument.Parse(responseContent);
            var root = jsonDoc.RootElement;

            if (root.TryGetProperty("jwt", out var jwtElement))
            {
                var jwt = jwtElement.GetString();
                if (!string.IsNullOrEmpty(jwt))
                {
                    return jwt;
                }
            }

            _logger.LogWarning("JWT token not found in response");
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse JWT token response");
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP request failed while getting JWT token");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Unexpected error while getting JWT token");
            return null;
        }
    }

    /// <summary>
    /// Clear cached token (force refresh on next request)
    /// </summary>
    public void ClearCachedToken()
    {
        _cachedToken = null;
        _tokenExpiry = DateTime.MinValue;
        _logger.LogDebug("Cleared cached JWT token");
    }

    /// <summary>
    /// Check if we have a valid cached token
    /// </summary>
    public bool HasValidToken()
    {
        return !string.IsNullOrEmpty(_cachedToken) && DateTime.UtcNow < _tokenExpiry.Subtract(_tokenValidityBuffer);
    }
}
