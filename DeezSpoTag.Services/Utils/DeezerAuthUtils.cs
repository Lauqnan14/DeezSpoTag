using DeezSpoTag.Services.Crypto;
using DeezSpoTag.Core.Security;
using Microsoft.Extensions.Logging;
using System.Buffers;
using System.Net;
using System.Text.Json;

namespace DeezSpoTag.Services.Utils;

/// <summary>
/// Deezer authentication utilities ported from deezspotag deezer.ts
/// </summary>
public class DeezerAuthUtils
{
    private const string ClientId = "172365";
    private const string ClientSignatureSalt = "fb0bec7ccc063dab0417eb7b0d847f34";
    private const string UserAgentHeader = "User-Agent";
    private const string HttpsScheme = "https";
    private const string DeezerWebHost = "www.deezer.com";
    private const string DeezerApiHost = "api.deezer.com";
    private const string DeezerConnectHost = "connect.deezer.com";
    private const string ResultsField = "results";
    private static readonly string DeezerHomeUrl = BuildUrl(DeezerWebHost, "/");
    private static readonly string DeezerInfosUrl = BuildUrl(DeezerApiHost, "/infos");
    private static readonly string DeezerAuthTokenBaseUrl = BuildUrl(DeezerApiHost, "/auth/token");
    private static readonly string DeezerOauthUserAuthBaseUrl = BuildUrl(DeezerConnectHost, "/oauth/user_auth.php");
    private static readonly string DeezerGenericTrackUrl = BuildUrl(DeezerApiHost, "/platform/generic/track/3135556");
    private static readonly string DeezerUserArlUrl = BuildUrl(DeezerWebHost, "/ajax/gw-light.php?method=user.getArl&input=3&api_version=1.0&api_token=null");
    private static readonly string DeezerUserDataUrl = BuildUrl(DeezerWebHost, "/ajax/gw-light.php?method=deezer.getUserData&input=3&api_version=1.0&api_token=null");
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly SearchValues<char> ArlValueTerminators = SearchValues.Create(";, \t\r\n");

    private readonly HttpClient _httpClient;
    private readonly ILogger<DeezerAuthUtils> _logger;

    public DeezerAuthUtils(HttpClient httpClient, ILogger<DeezerAuthUtils> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    private static string BuildUrl(string host, string pathAndQuery)
    {
        var authority = new UriBuilder(HttpsScheme, host).Uri.GetLeftPart(UriPartial.Authority);
        return $"{authority}{pathAndQuery}";
    }

    private static void ApplyBrowserHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation(UserAgentHeader, CoreUtils.UserAgentHeader);
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
    }

    /// <summary>
    /// Normalize ARL input (handles raw token or cookie string with arl=...).
    /// </summary>
    public static string? NormalizeArl(string? arl)
    {
        if (string.IsNullOrWhiteSpace(arl))
        {
            return null;
        }

        var trimmed = arl.Trim();
        var index = trimmed.IndexOf("arl=", StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            var value = trimmed[(index + 4)..];
            var stop = value.AsSpan().IndexOfAny(ArlValueTerminators);
            if (stop >= 0)
            {
                value = value[..stop];
            }

            return value.Trim().Trim('"');
        }

        return trimmed.Trim('"');
    }

    /// <summary>
    /// Validate ARL length (Deezer ARLs are typically 175 or 192 chars).
    /// </summary>
    public static bool IsValidArlLength(string? arl)
    {
        if (string.IsNullOrEmpty(arl))
        {
            return false;
        }

        return arl.Length == 175 || arl.Length == 192;
    }

    /// <summary>
    /// Check Deezer availability in current region (port of deezspotag isDeezerAvailable).
    /// </summary>
    public async Task<bool> IsDeezerAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, DeezerHomeUrl);
            request.Headers.Add("Cookie", "dz_lang=en; Domain=deezer.com; Path=/; Secure; hostOnly=false;");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var titleMatch = System.Text.RegularExpressions.Regex.Match(
                content,
                @"<title[^>]*>([^<]+)<\/title>",
                System.Text.RegularExpressions.RegexOptions.None,
                RegexTimeout);
            var title = titleMatch.Success ? titleMatch.Groups[1].Value.Trim() : "";

            if (title == "Deezer will soon be available in your country.")
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(title))
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Primary Deezer availability check failed, trying API fallback");
        }

        try
        {
            var response = await _httpClient.GetAsync(DeezerInfosUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            using var jsonDoc = JsonDocument.Parse(content);
            var root = jsonDoc.RootElement;
            if (root.TryGetProperty("open", out var openElement))
            {
                return openElement.GetBoolean();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error checking Deezer availability");
        }

        return false;
    }

    /// <summary>
    /// Get Deezer access token from email and password (port of getDeezerAccessTokenFromEmailPassword)
    /// </summary>
    public async Task<string?> GetDeezerAccessTokenFromEmailPasswordAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        try
        {
            // Hash password with MD5 using UTF-8 encoding (exact port from deezspotag)
            var hashedPassword = DecryptionService.GenerateMd5(password, "utf8");

            // Create hash for authentication (exact port from deezspotag)
            var authString = string.Join("", ClientId, email, hashedPassword, ClientSignatureSalt);
            var hash = DecryptionService.GenerateMd5(authString, "utf8");

            // Prepare request parameters
            var parameters = new Dictionary<string, string>
            {
                ["app_id"] = ClientId,
                ["login"] = email,
                ["password"] = hashedPassword,
                ["hash"] = hash
            };

            // Build query string
            var queryString = string.Join("&", parameters.Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value)}"));
            var requestUrl = $"{DeezerAuthTokenBaseUrl}?{queryString}";

            // Set headers to match deezspotag exactly
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add(UserAgentHeader, CoreUtils.UserAgentHeader);

            _logger.LogDebug("Attempting to get Deezer access token");

            var response = await _httpClient.GetAsync(requestUrl, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to get access token via api.deezer.com/auth/token. Status: {StatusCode}", response.StatusCode);
                return await GetAccessTokenViaOauthAsync(email, hashedPassword, hash, cancellationToken);
            }

            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

            using var jsonDoc = JsonDocument.Parse(responseContent);
            var root = jsonDoc.RootElement;

            if (root.TryGetProperty("access_token", out var accessTokenElement))
            {
                var accessToken = accessTokenElement.GetString();
                _logger.LogDebug("Successfully obtained access token");
                return accessToken;
            }

            _logger.LogWarning("No access token in Deezer token response.");
            return await GetAccessTokenViaOauthAsync(email, hashedPassword, hash, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error getting Deezer access token");
            return null;
        }
    }

    private async Task<string?> GetAccessTokenViaOauthAsync(string email, string hashedPassword, string hash, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{DeezerOauthUserAuthBaseUrl}?app_id={ClientId}&login={Uri.EscapeDataString(email)}&password={hashedPassword}&hash={hash}";
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add(UserAgentHeader, CoreUtils.UserAgentHeader);

            var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to get access token via connect.deezer.com. Status: {StatusCode}", response.StatusCode);
                return null;
            }

            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            using var jsonDoc = JsonDocument.Parse(responseContent);
            var root = jsonDoc.RootElement;

            if (root.TryGetProperty("access_token", out var accessTokenElement))
            {
                var accessToken = accessTokenElement.GetString();
                _logger.LogDebug("Successfully obtained access token via OAuth");
                return accessToken;
            }

            _logger.LogWarning("No access token in Deezer OAuth response.");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error getting access token via OAuth");
            return null;
        }
    }

    /// <summary>
    /// Get Deezer ARL from access token (port of getDeezerArlFromAccessToken)
    /// </summary>
    public async Task<string?> GetDeezerArlFromAccessTokenAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(accessToken))
        {
            _logger.LogWarning("Access token is null or empty");
            return null;
        }

        try
        {
            // Use CookieContainer to handle cookies like deezspotag
            using var handler = new HttpClientHandler()
            {
                UseCookies = true
            };

            using var client = new HttpClient(handler);
            client.DefaultRequestHeaders.Add(UserAgentHeader, CoreUtils.UserAgentHeader);

            _logger.LogDebug("Getting ARL from access token");

            // First request to set cookies (exact port from deezspotag)
            var authUrl = DeezerGenericTrackUrl;
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");

            var authResponse = await client.GetAsync(authUrl, cancellationToken);

            if (!authResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to authenticate with access token. Status: {StatusCode}", authResponse.StatusCode);
                return null;
            }

            // Second request to get ARL (exact port from deezspotag)
            client.DefaultRequestHeaders.Remove("Authorization");
            var arlUrl = DeezerUserArlUrl;

            var arlResponse = await client.GetAsync(arlUrl, cancellationToken);

            if (!arlResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to get ARL. Status: {StatusCode}", arlResponse.StatusCode);
                return null;
            }

            var arlContent = await arlResponse.Content.ReadAsStringAsync(cancellationToken);

            using var jsonDoc = JsonDocument.Parse(arlContent);
            var root = jsonDoc.RootElement;

            if (root.TryGetProperty(ResultsField, out var resultsElement))
            {
                var arl = resultsElement.GetString();

                if (!string.IsNullOrEmpty(arl))
                {
                    _logger.LogDebug("Successfully obtained ARL");
                    return arl;
                }
            }

            _logger.LogWarning("No ARL in Deezer response.");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error getting Deezer ARL from access token");
            return null;
        }
    }

    /// <summary>
    /// Login with email and password and get ARL directly
    /// </summary>
    public async Task<string?> LoginWithEmailPasswordAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Attempting to login with email/password");

            var refreezerArl = await LoginWithEmailPasswordRefreezerFlowAsync(email, password, cancellationToken);
            if (!string.IsNullOrWhiteSpace(refreezerArl))
            {
                _logger.LogInformation("Successfully logged in with email/password using Refreezer-compatible flow");
                return refreezerArl;
            }

            // Step 1: Get access token
            var accessToken = await GetDeezerAccessTokenFromEmailPasswordAsync(email, password, cancellationToken);

            if (string.IsNullOrEmpty(accessToken))
            {
                _logger.LogWarning("Failed to get access token");
                return null;
            }

            // Step 2: Get ARL from access token
            var arl = await GetDeezerArlFromAccessTokenAsync(accessToken, cancellationToken);

            if (string.IsNullOrEmpty(arl))
            {
                _logger.LogWarning("Failed to get ARL");
                return null;
            }

            _logger.LogInformation("Successfully logged in with email/password");
            return arl;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error during login with email/password");
            return null;
        }
    }

    private async Task<string?> LoginWithEmailPasswordRefreezerFlowAsync(string email, string password, CancellationToken cancellationToken)
    {
        try
        {
            using var handler = new HttpClientHandler
            {
                UseCookies = true,
                CookieContainer = new CookieContainer(),
                AllowAutoRedirect = true
            };

            using var client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(30);

            var hashedPassword = DecryptionService.GenerateMd5(password, "utf8");
            var authString = string.Concat(ClientId, email, hashedPassword, ClientSignatureSalt);
            var hash = DecryptionService.GenerateMd5(authString, "utf8");

            // Step 1: initialize Deezer web session cookies
            using (var bootstrap = new HttpRequestMessage(HttpMethod.Get, DeezerUserDataUrl))
            {
                ApplyBrowserHeaders(bootstrap);
                await client.SendAsync(bootstrap, cancellationToken);
            }

            // Step 2: login by email/password to set auth cookies
            var authUrl =
                $"{DeezerOauthUserAuthBaseUrl}?app_id={ClientId}&login={Uri.EscapeDataString(email)}&password={hashedPassword}&hash={hash}";
            using (var authRequest = new HttpRequestMessage(HttpMethod.Get, authUrl))
            {
                ApplyBrowserHeaders(authRequest);
                using var authResponse = await client.SendAsync(authRequest, cancellationToken);
                if (!authResponse.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Refreezer flow OAuth request failed with status {StatusCode}", authResponse.StatusCode);
                    return null;
                }

                var authBody = await authResponse.Content.ReadAsStringAsync(cancellationToken);
                using var authJson = JsonDocument.Parse(authBody);
                var root = authJson.RootElement;
                if (!root.TryGetProperty("access_token", out var _))
                {
                    _logger.LogWarning("Refreezer flow OAuth response did not include access_token");
                    return null;
                }
            }

            // Step 3: get ARL using authenticated cookie session
            using (var arlRequest = new HttpRequestMessage(HttpMethod.Get, DeezerUserArlUrl))
            {
                ApplyBrowserHeaders(arlRequest);
                using var arlResponse = await client.SendAsync(arlRequest, cancellationToken);
                if (!arlResponse.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Refreezer flow user.getArl request failed with status {StatusCode}", arlResponse.StatusCode);
                    return null;
                }

                var arlBody = await arlResponse.Content.ReadAsStringAsync(cancellationToken);
                using var arlJson = JsonDocument.Parse(arlBody);
                if (!arlJson.RootElement.TryGetProperty(ResultsField, out var arlElement))
                {
                    return null;
                }

                var arl = arlElement.GetString();
                var normalized = NormalizeArl(arl);
                return IsValidArlLength(normalized) ? normalized : null;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Refreezer-compatible email login flow failed");
            return null;
        }
    }

    /// <summary>
    /// Validate ARL token by testing it
    /// </summary>
    public async Task<bool> ValidateArlAsync(string arl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(arl))
            return false;

        try
        {
            // Test the ARL by making a request to Deezer API
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add(UserAgentHeader, CoreUtils.UserAgentHeader);
            client.DefaultRequestHeaders.Add("Cookie", $"arl={arl}");

            var testUrl = DeezerUserDataUrl;
            var response = await client.GetAsync(testUrl, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return false;

            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            using var jsonDoc = JsonDocument.Parse(content);
            var root = jsonDoc.RootElement;

            // Check if we got valid user data
            if (root.TryGetProperty(ResultsField, out var results) &&
                results.TryGetProperty("USER", out var user) &&
                user.TryGetProperty("USER_ID", out var userId))
            {
                var userIdValue = userId.GetString();
                return !string.IsNullOrEmpty(userIdValue) && userIdValue != "0";
            }

            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error validating ARL");
            return false;
        }
    }

    /// <summary>
    /// Get user info from ARL
    /// </summary>
    public async Task<DeezerUserInfo?> GetUserInfoFromArlAsync(string arl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(arl))
            return null;

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add(UserAgentHeader, CoreUtils.UserAgentHeader);
            client.DefaultRequestHeaders.Add("Cookie", $"arl={arl}");

            var userUrl = DeezerUserDataUrl;
            var response = await client.GetAsync(userUrl, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return null;

            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            using var jsonDoc = JsonDocument.Parse(content);
            var root = jsonDoc.RootElement;

            if (root.TryGetProperty(ResultsField, out var results) &&
                results.TryGetProperty("USER", out var user))
            {
                return new DeezerUserInfo
                {
                    UserId = user.TryGetProperty("USER_ID", out var userId) ? userId.GetString() : null,
                    Username = user.TryGetProperty("BLOG_NAME", out var username) ? username.GetString() : null,
                    Email = user.TryGetProperty("EMAIL", out var email) ? email.GetString() : null,
                    Country = user.TryGetProperty("COUNTRY", out var country) ? country.GetString() : null,
                    CanStreamHQ = user.TryGetProperty("OPTIONS", out var options) &&
                                 options.TryGetProperty("web_hq", out var hq) && hq.GetBoolean(),
                    CanStreamLossless = user.TryGetProperty("OPTIONS", out var options2) &&
                                       options2.TryGetProperty("web_lossless", out var lossless) && lossless.GetBoolean()
                };
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error getting user info from ARL");
            return null;
        }
    }
}

/// <summary>
/// Deezer user information
/// </summary>
public class DeezerUserInfo
{
    public string? UserId { get; set; }
    public string? Username { get; set; }
    public string? Email { get; set; }
    public string? Country { get; set; }
    public bool CanStreamHQ { get; set; }
    public bool CanStreamLossless { get; set; }
}
