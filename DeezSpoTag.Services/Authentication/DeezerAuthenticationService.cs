using DeezSpoTag.Core.Constants;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Deezer;
using DeezSpoTag.Core.Security;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Services.Crypto;
using DeezSpoTag.Services.Utils;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;
using System.Text.Json;

namespace DeezSpoTag.Services.Authentication;

/// <summary>
/// Unified Deezer authentication service (merged from API and Services implementations)
/// Ported from: /deezspotag/deezspotag/src/utils/deezer.ts and /deezspotag/webui/src/server/helpers/loginStorage.ts
/// Supports both IDeezerAuthenticationService and API-compatible methods
/// </summary>
public class DeezerAuthenticationService : IDeezerAuthenticationService
{
    private const string DeezSpoTagFolderName = "deezspotag";
    private const string DeezerApiScheme = "https";
    private const string DeezerApiHost = "www.deezer.com";
    private readonly DeezerClient _deezerClient;
    private readonly ILogger<DeezerAuthenticationService> _logger;
    private readonly DeezerAuthUtils _authUtils;
    private readonly string _configPath;

    // Deezer API constants from deezspotag
    private const string CLIENT_ID = "172365";
    private const string CLIENT_SIGNATURE_SALT = "fb0bec7ccc063dab0417eb7b0d847f34";
    private const string USER_AGENT = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/79.0.3945.130 Safari/537.36";
    private static readonly string DeezerWebOrigin = $"{DeezerApiScheme}://{DeezerApiHost}";
    private static readonly string DeezerWebReferer = $"{DeezerWebOrigin}/";
    private static readonly Uri DeezerWebCookieUri = new(DeezerWebOrigin);
    private static readonly string DeezerAuthTokenBaseUrl = $"{DeezerApiScheme}://api.deezer.com/auth/token";
    private static readonly string DeezerGenericTrackUrl = $"{DeezerApiScheme}://api.deezer.com/platform/generic/track/3135556";
    private static readonly string DeezerUserArlUrl = $"{DeezerWebOrigin}/ajax/gw-light.php?method=user.getArl&input=3&api_version=1.0&api_token=null";
    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true
    };
    private static string DeezerApiUrl => new UriBuilder(DeezerApiScheme, DeezerApiHost)
    {
        Path = "/ajax/gw-light.php",
        Query = "api_version=1.0&api_token=null&input=3"
    }.Uri.ToString();

    // EXACT PORT: In-memory login data like deezspotag loginData variable
    private LoginCredentials _loginData = new();

    public DeezerAuthenticationService(
        DeezerClient deezerClient,
        DeezerAuthUtils authUtils,
        ILogger<DeezerAuthenticationService> logger)
    {
        _deezerClient = deezerClient ?? throw new ArgumentNullException(nameof(deezerClient));
        _authUtils = authUtils ?? throw new ArgumentNullException(nameof(authUtils));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _configPath = ResolveConfigPath();

        EnsureConfigDirectory();
    }

    #region API-Compatible Methods (from API project)

    private static string ResolveConfigPath()
    {
        var configDir = Environment.GetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configDir))
        {
            return Path.GetFullPath(configDir.Trim());
        }

        var dataDir = Environment.GetEnvironmentVariable("DEEZSPOTAG_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(dataDir))
        {
            return Path.GetFullPath(dataDir.Trim());
        }

        var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdgConfigHome))
        {
            return Path.Join(xdgConfigHome, DeezSpoTagFolderName);
        }

        var appData = Environment.GetEnvironmentVariable("APPDATA");
        if (!string.IsNullOrWhiteSpace(appData))
        {
            return Path.Join(appData, DeezSpoTagFolderName);
        }

        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Join(homeDir, ".config", DeezSpoTagFolderName);
    }

    /// <summary>
    /// API-compatible login method (merged from API project)
    /// Returns object for API compatibility
    /// </summary>
    public async Task<object> LoginAsync(string arl, int? child = null)
    {
        var normalizedArl = DeezSpoTag.Services.Utils.DeezerAuthUtils.NormalizeArl(arl);
        if (string.IsNullOrWhiteSpace(normalizedArl) || normalizedArl.Length < 10)
        {
            return new
            {
                status = 0, // FAILED
                arl = normalizedArl,
                message = "Invalid ARL token."
            };
        }

        if (!DeezSpoTag.Services.Utils.DeezerAuthUtils.IsValidArlLength(normalizedArl))
        {
            return new
            {
                status = 0, // FAILED
                arl = normalizedArl,
                message = "Invalid ARL token length."
            };
        }

        // Check Deezer availability (as in deezspotag)
        if (!await _authUtils.IsDeezerAvailableAsync())
        {
            return new
            {
                status = -1, // NOT_AVAILABLE
                arl = normalizedArl,
                message = "Deezer is not available."
            };
        }

        using var handler = new HttpClientHandler { UseCookies = true, CookieContainer = new CookieContainer() };
        using var client = new HttpClient(handler);

        var requestBody = new
        {
            method = "getUserData",
            @params = new { }
        };
        var json = JsonSerializer.Serialize(requestBody);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, DeezerApiUrl)
        {
            Content = content
        };
        request.Headers.Add("Cookie", $"arl={normalizedArl}");
        request.Headers.Add("User-Agent", USER_AGENT);
        request.Headers.Add("Accept", "application/json, text/plain, */*");
        request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
        request.Headers.Add("Origin", DeezerWebOrigin);
        request.Headers.Add("Referer", DeezerWebReferer);

        try
        {
            using var response = await client.SendAsync(request);
            var responseString = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                return new
                {
                    status = 0, // FAILED
                    arl = normalizedArl,
                    message = $"Failed to contact Deezer API. Raw: {responseString}"
                };
            }
            using var doc = JsonDocument.Parse(responseString);
            var root = doc.RootElement;
            if (root.TryGetProperty("results", out var results) && results.TryGetProperty("USER", out var user))
            {
                // Porting deezspotag: user, childs, currentChild
                var childs = results.TryGetProperty("ACCOUNTS", out var accounts) ? JsonSerializer.Deserialize<object>(accounts.GetRawText()) : null;
                var currentChild = results.TryGetProperty("SELECTED_ACCOUNT", out var selectedAccount) ? JsonSerializer.Deserialize<object>(selectedAccount.GetRawText()) : null;
                string? sid = null;
                try
                {
                    var deezerCookies = handler.CookieContainer.GetCookies(DeezerWebCookieUri);
                    sid = deezerCookies["sid"]?.Value;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    sid = null;
                }
                return new
                {
                    status = 1, // SUCCESS
                    arl = normalizedArl,
                    user = JsonSerializer.Deserialize<object>(user.GetRawText()),
                    childs = childs,
                    currentChild = currentChild,
                    sid = sid,
                    message = "Login successful."
                };
            }
            else
            {
                return new
                {
                    status = 0, // FAILED
                    arl = normalizedArl,
                    message = $"Invalid ARL token or user not found. Raw: {responseString}"
                };
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new
            {
                status = 0, // FAILED
                arl = normalizedArl,
                message = $"Exception occurred during Deezer login: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Clear session data (API-compatible method)
    /// </summary>
    public async Task ClearSessionAsync()
    {
        await LogoutAsync();
    }

    /// <summary>
    /// Get access token from email and password (API-compatible method)
    /// </summary>
    async Task<string?> IDeezerAuthenticationService.GetAccessTokenFromEmailPasswordAsync(string email, string password)
    {
        return await GetAccessTokenFromEmailPasswordInternalAsync(email, password);
    }

    /// <summary>
    /// Get ARL from access token (API-compatible method)
    /// </summary>
    async Task<string?> IDeezerAuthenticationService.GetArlFromAccessTokenAsync(string accessToken)
    {
        return await GetArlFromAccessTokenInternalAsync(accessToken);
    }

    #endregion

    /// <summary>
    /// Login with email and password
    /// Ported from: getDeezerAccessTokenFromEmailPassword in deezspotag utils/deezer.ts
    /// </summary>
    public async Task<AuthenticationResult> LoginWithEmailPasswordAsync(string email, string password)
    {
        try
        {
            if (!await _authUtils.IsDeezerAvailableAsync())
            {
                return new AuthenticationResult
                {
                    Success = false,
                    ErrorMessage = "Deezer is not available"
                };
            }

            _logger.LogInformation("Attempting login with email/password");

            // Get access token from email/password
            var accessToken = await GetAccessTokenFromEmailPasswordInternalAsync(email, password);
            if (string.IsNullOrEmpty(accessToken))
            {
                _logger.LogWarning("Failed to get access token");
                return new AuthenticationResult
                {
                    Success = false,
                    ErrorMessage = "Invalid email or password"
                };
            }

            // Get ARL from access token
            var arl = await GetArlFromAccessTokenInternalAsync(accessToken);
            if (string.IsNullOrEmpty(arl))
            {
                _logger.LogWarning("Failed to get ARL from access token");
                return new AuthenticationResult
                {
                    Success = false,
                    ErrorMessage = "Failed to obtain authentication token"
                };
            }

            if (!DeezSpoTag.Services.Utils.DeezerAuthUtils.IsValidArlLength(arl))
            {
                _logger.LogWarning("Received ARL with invalid length");
                return new AuthenticationResult
                {
                    Success = false,
                    ErrorMessage = "Invalid authentication token"
                };
            }

            // Test the ARL by logging in
            var loginResult = await LoginWithArlAsync(arl);
            if (!loginResult.Success)
            {
                return loginResult;
            }

            // Save credentials
            var credentials = new LoginCredentials
            {
                Arl = arl,
                AccessToken = accessToken
            };

            await SaveLoginCredentialsAsync(credentials);

            _logger.LogInformation("Successfully logged in with email/password");
            return loginResult;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error during email/password login");
            return new AuthenticationResult
            {
                Success = false,
                ErrorMessage = "Authentication failed due to an error"
            };
        }
    }

    /// <summary>
    /// Login with ARL token
    /// EXACT PORT: Deezer.loginViaArl in deezer-sdk/src/deezer.ts
    /// </summary>
    public async Task<AuthenticationResult> LoginWithArlAsync(string arl)
    {
        try
        {
            _logger.LogInformation("Attempting login with ARL token");

            var normalizedArl = DeezSpoTag.Services.Utils.DeezerAuthUtils.NormalizeArl(arl);
            if (string.IsNullOrWhiteSpace(normalizedArl))
            {
                return new AuthenticationResult
                {
                    Success = false,
                    ErrorMessage = "ARL token is required"
                };
            }

            if (!DeezSpoTag.Services.Utils.DeezerAuthUtils.IsValidArlLength(normalizedArl))
            {
                return new AuthenticationResult
                {
                    Success = false,
                    ErrorMessage = "Invalid ARL token length"
                };
            }

            if (!await _authUtils.IsDeezerAvailableAsync())
            {
                return new AuthenticationResult
                {
                    Success = false,
                    ErrorMessage = "Deezer is not available"
                };
            }

            // Test login with Deezer client
            var loginSuccess = await _deezerClient.LoginViaArlAsync(normalizedArl);
            if (!loginSuccess)
            {
                _logger.LogWarning("Failed to login with provided ARL");
                return new AuthenticationResult
                {
                    Success = false,
                    ErrorMessage = "Invalid ARL token"
                };
            }

            // Get user data
            var userData = _deezerClient.CurrentUser;
            if (userData == null)
            {
                _logger.LogWarning("Failed to get user data after ARL login");
                return new AuthenticationResult
                {
                    Success = false,
                    ErrorMessage = "Failed to retrieve user information"
                };
            }

            // EXACT PORT: Save credentials like deezspotag saveLoginCredentials
            var credentials = new LoginCredentials
            {
                Arl = normalizedArl,
                AccessToken = _loginData.AccessToken
            };

            await SaveLoginCredentialsAsync(credentials);

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Successfully logged in with ARL. User: {UserName} (ID: {UserId})",
                    userData.Name, userData.Id);
            }

            return BuildSuccessfulAuthenticationResult(userData, normalizedArl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error during ARL login");
            return new AuthenticationResult
            {
                Success = false,
                ErrorMessage = "Authentication failed due to an error"
            };
        }
    }

    /// <summary>
    /// Get current login status
    /// </summary>
    public async Task<AuthenticationResult> GetLoginStatusAsync()
    {
        try
        {
            if (!_deezerClient.LoggedIn || _deezerClient.CurrentUser == null)
            {
                var credentials = await GetLoginCredentialsAsync();
                var normalizedArl = DeezSpoTag.Services.Utils.DeezerAuthUtils.NormalizeArl(credentials.Arl);

                if (!string.IsNullOrEmpty(normalizedArl) && DeezSpoTag.Services.Utils.DeezerAuthUtils.IsValidArlLength(normalizedArl))
                {
                    var refreshed = await _deezerClient.LoginViaArlAsync(normalizedArl);
                    if (!refreshed || _deezerClient.CurrentUser == null)
                    {
                        return new AuthenticationResult
                        {
                            Success = false,
                            ErrorMessage = "Not logged in"
                        };
                    }
                }
                else
                {
                    return new AuthenticationResult
                    {
                        Success = false,
                        ErrorMessage = "Not logged in"
                    };
                }
            }

            var userData = _deezerClient.CurrentUser;
            var loginCredentials = await GetLoginCredentialsAsync();

            return BuildSuccessfulAuthenticationResult(userData, loginCredentials.Arl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error checking login status");
            return new AuthenticationResult
            {
                Success = false,
                ErrorMessage = "Failed to check login status"
            };
        }
    }

    /// <summary>
    /// Logout and clear credentials
    /// EXACT PORT: deezspotag logout behavior
    /// </summary>
    public async Task LogoutAsync()
    {
        try
        {
            _logger.LogInformation("Logging out user");

            // EXACT PORT: Reset credentials like deezspotag
            await ResetLoginCredentialsAsync();

            // Logout from Deezer client
            await _deezerClient.LogoutAsync();

            _logger.LogInformation("Successfully logged out");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error during logout");
            throw new InvalidOperationException("Logout failed.", ex);
        }
    }

    /// <summary>
    /// Get stored login credentials
    /// EXACT PORT: getLoginCredentials in deezspotag loginStorage.ts
    /// </summary>
    public async Task<LoginCredentials> GetLoginCredentialsAsync()
    {
        // EXACT PORT: Load if not already loaded
        if (string.IsNullOrEmpty(_loginData.Arl))
        {
            await LoadLoginCredentialsAsync();
        }

        return _loginData;
    }

    /// <summary>
    /// Check if user is currently logged in
    /// </summary>
    public async Task<bool> IsLoggedInAsync()
    {
        var status = await GetLoginStatusAsync();
        return status.Success;
    }

    #region Private Methods

    /// <summary>
    /// Get access token from email and password
    /// Ported from: getDeezerAccessTokenFromEmailPassword in deezspotag utils/deezer.ts
    /// </summary>
    private async Task<string?> GetAccessTokenFromEmailPasswordInternalAsync(string email, string password)
    {
        try
        {
            // Hash password with MD5 (as per deezspotag implementation)
            var hashedPassword = ComputeMd5Hash(password);

            // Create hash for API call
            var hashInput = $"{CLIENT_ID}{email}{hashedPassword}{CLIENT_SIGNATURE_SALT}";
            var hash = ComputeMd5Hash(hashInput);

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", USER_AGENT);

            var url = $"{DeezerAuthTokenBaseUrl}?app_id={CLIENT_ID}&login={Uri.EscapeDataString(email)}&password={hashedPassword}&hash={hash}";

            var response = await httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var responseContent = await response.Content.ReadAsStringAsync();
            var responseData = JsonSerializer.Deserialize<Dictionary<string, object>>(responseContent);

            if (responseData?.TryGetValue("access_token", out var accessTokenObj) == true)
            {
                return accessTokenObj.ToString();
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error getting access token from email/password");
            return null;
        }
    }

    /// <summary>
    /// Get ARL from access token
    /// Ported from: getDeezerArlFromAccessToken in deezspotag utils/deezer.ts
    /// </summary>
    private async Task<string?> GetArlFromAccessTokenInternalAsync(string accessToken)
    {
        try
        {
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", USER_AGENT);
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");

            // First request to set up session
            await httpClient.GetAsync(DeezerGenericTrackUrl);

            // Second request to get ARL
            var response = await httpClient.GetAsync(DeezerUserArlUrl);

            response.EnsureSuccessStatusCode();

            var responseContent = await response.Content.ReadAsStringAsync();
            var responseData = JsonSerializer.Deserialize<Dictionary<string, object>>(responseContent);

            if (responseData?.TryGetValue("results", out var arlObj) == true)
            {
                return arlObj.ToString();
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error getting ARL from access token");
            return null;
        }
    }

    /// <summary>
    /// Load login credentials from file
    /// EXACT PORT: loadLoginCredentials in deezspotag loginStorage.ts
    /// </summary>
    private async Task LoadLoginCredentialsAsync()
    {
        var credentialsPath = Path.Join(_configPath, "login.json");

        // EXACT PORT: Create config folder if it doesn't exist
        if (!Directory.Exists(_configPath))
        {
            Directory.CreateDirectory(_configPath);
        }

        // EXACT PORT: If login file doesn't exist, reset to defaults
        if (!System.IO.File.Exists(credentialsPath))
        {
            await ResetLoginCredentialsAsync();
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(credentialsPath);
            _loginData = JsonSerializer.Deserialize<LoginCredentials>(json) ?? new LoginCredentials();
        }
        catch (JsonException ex)
        {
            // EXACT PORT: Handle JSON syntax errors like deezspotag
            _logger.LogWarning(ex, "JSON syntax error in credentials file, resetting");
            await ResetLoginCredentialsAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error loading login credentials");
            _loginData = new LoginCredentials();
        }
    }

    /// <summary>
    /// Save login credentials to file
    /// EXACT PORT: saveLoginCredentials in deezspotag loginStorage.ts
    /// </summary>
    private async Task SaveLoginCredentialsAsync(LoginCredentials newCredentials)
    {
        try
        {
            // EXACT PORT: Update only provided fields like deezspotag
            if (!string.IsNullOrEmpty(newCredentials.Arl))
                _loginData.Arl = newCredentials.Arl;

            if (!string.IsNullOrEmpty(newCredentials.AccessToken))
                _loginData.AccessToken = newCredentials.AccessToken;

            EnsureConfigDirectory();

            var credentialsPath = Path.Join(_configPath, "login.json");

            // EXACT PORT: Write with indentation like deezspotag (JSON.stringify with 2 spaces)
            var json = JsonSerializer.Serialize(_loginData, IndentedJsonOptions);

            await System.IO.File.WriteAllTextAsync(credentialsPath, json);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Saved login credentials to: {Path}", credentialsPath);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error saving login credentials");
            throw new InvalidOperationException("Failed to save Deezer login credentials.", ex);
        }
    }

    /// <summary>
    /// Reset login credentials to defaults
    /// EXACT PORT: resetLoginCredentials in deezspotag loginStorage.ts
    /// </summary>
    private async Task ResetLoginCredentialsAsync()
    {
        try
        {
            EnsureConfigDirectory();

            var credentialsPath = Path.Join(_configPath, "login.json");
            var defaultCredentials = new LoginCredentials();

            // EXACT PORT: Write defaults with indentation like deezspotag
            var json = JsonSerializer.Serialize(defaultCredentials, IndentedJsonOptions);

            await System.IO.File.WriteAllTextAsync(credentialsPath, json);

            // EXACT PORT: Update in-memory data like deezspotag
            _loginData = JsonSerializer.Deserialize<LoginCredentials>(json) ?? new LoginCredentials();

            _logger.LogDebug("Reset login credentials to defaults");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error resetting login credentials");
            throw new InvalidOperationException("Failed to reset Deezer login credentials.", ex);
        }
    }

    /// <summary>
    /// Ensure config directory exists
    /// </summary>
    private void EnsureConfigDirectory()
    {
        if (!Directory.Exists(_configPath))
        {
            Directory.CreateDirectory(_configPath);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Created config directory: {Path}", _configPath);
            }
        }
    }

    /// <summary>
    /// Compute MD5 hash
    /// Ported from: _md5 function in deezspotag utils/crypto.ts
    /// </summary>
    private static string ComputeMd5Hash(string input)
    {
        // Protocol compatibility: Deezer auth handshake expects the legacy MD5 digest.
        return LegacyMd5.ComputeHexLower(input, Encoding.UTF8);
    }

    private static AuthenticationResult BuildSuccessfulAuthenticationResult(DeezerUser userData, string? arl)
    {
        return new AuthenticationResult
        {
            Success = true,
            User = new DeezerUser
            {
                Id = userData.Id,
                Name = userData.Name,
                Picture = userData.Picture ?? string.Empty,
                Country = userData.Country ?? string.Empty,
                CanStreamLossless = userData.CanStreamLossless,
                CanStreamHq = userData.CanStreamHq,
                LicenseToken = userData.LicenseToken ?? string.Empty,
                Language = userData.Language ?? string.Empty
            },
            Arl = arl
        };
    }

    #endregion
}

/// <summary>
/// Authentication status enum
/// </summary>
public enum AuthStatus
{
    NotLoggedIn,
    LoggedIn
}

/// <summary>
/// Authentication result
/// </summary>
public class AuthenticationResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public DeezerUser? User { get; set; }
    public string? Arl { get; set; }
    public AuthStatus Status => Success ? AuthStatus.LoggedIn : AuthStatus.NotLoggedIn;
}

/// <summary>
/// Login credentials storage
/// EXACT PORT: LoginFile interface in deezspotag webui types.ts
/// </summary>
public class LoginCredentials
{
    public string? Arl { get; set; }
    public string? AccessToken { get; set; }
}
