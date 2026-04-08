using DeezSpoTag.Core.Models.Deezer;
using DeezSpoTag.Core.Models.Settings;
using Microsoft.Extensions.Logging;
using System;
using System.Net;
using System.Net.Security;
using Newtonsoft.Json;

namespace DeezSpoTag.Integrations.Deezer;

/// <summary>
/// Centralized session manager for Deezer authentication and cookie management
/// Exact port from deezspotag deezer.ts - manages shared state across all services
/// </summary>
public sealed class DeezerSessionManager : IDisposable
{
    private const string GetUserDataMethod = "deezer.getUserData";
    private const string DeezerScheme = "https";
    private const string DeezerWebHost = "www.deezer.com";
    private const string DeezerRootHost = "deezer.com";
    private const string DeezerMediaHost = "media.deezer.com";
    private const string DefaultCountry = "US";
    private const string DefaultLanguage = "en";
    private const int MaxRetries = 3;
    private static readonly TimeSpan HttpRequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private static readonly bool AllowInsecureSsl = string.Equals(
        Environment.GetEnvironmentVariable("DEEZSPOTAG_ALLOW_INSECURE_SSL"),
        "1",
        StringComparison.OrdinalIgnoreCase);
    private readonly ILogger<DeezerSessionManager> _logger;
    private readonly CookieContainer _sharedCookieContainer;
    private readonly Dictionary<string, string> _httpHeaders;
    private readonly Func<DeezSpoTagSettings?> _settingsProvider;
    private bool _disposed;
    
    public bool LoggedIn { get; private set; }
    public DeezerUser? CurrentUser { get; private set; }
    public List<DeezerUser> Children { get; private set; } = new();
    public IReadOnlyList<string> ChildAccounts { get; private set; } = Array.Empty<string>();
    public int SelectedAccount { get; private set; }
    
    // Shared authentication state
    public CookieContainer CookieContainer => _sharedCookieContainer;
    public Dictionary<string, string> HttpHeaders => _httpHeaders;
    
    // Gateway API token (shared across all GW calls)
    public string? ApiToken { get; set; }
    
    // API access token (for public API calls)
    public string? AccessToken { get; set; }

    private const string UserAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/79.0.3945.130 Safari/537.36";

    public DeezerSessionManager(ILogger<DeezerSessionManager> logger, Func<DeezSpoTagSettings?> settingsProvider)
    {
        _logger = logger;
        _sharedCookieContainer = new CookieContainer();
        _settingsProvider = settingsProvider;
        
        // EXACT PORT: Initialize headers like deezspotag deezer.ts constructor
        _httpHeaders = new Dictionary<string, string>
        {
            ["User-Agent"] = UserAgent,
            ["Accept"] = "*/*",
            ["Accept-Charset"] = "utf-8,ISO-8859-1;q=0.7,*;q=0.3",
            ["Content-Type"] = "text/plain;charset=UTF-8",
            ["Connection"] = "keep-alive"
        };

        ApplyLocaleOverride();
        
        LoggedIn = false;
    }

    /// <summary>
    /// Login using ARL token - EXACT PORT from deezspotag deezer.ts loginViaArl
    /// </summary>
    public async Task<bool> LoginViaArlAsync(string arl, int child = 0)
    {
        if (string.IsNullOrWhiteSpace(arl))
        {
            _logger.LogWarning("ARL is null or empty");
            return false;
        }

        try
        {
            // EXACT PORT: Create and set ARL cookie like deezspotag
            var cookie = new Cookie("arl", arl.Trim(), "/", ".deezer.com")
            {
                HttpOnly = true
            };
            _sharedCookieContainer.Add(cookie);
            
            _logger.LogDebug("Set ARL cookie: {ArlLength} characters", arl.Trim().Length);

            // Get user data to validate login - this will also set the API token
            var userData = await GetUserDataAsync();
            
            // EXACT PORT: Check login validation like deezspotag
            if (userData?.User?.UserId == null || userData.User.UserId == 0)
            {
                _logger.LogWarning("Invalid ARL - USER_ID is null or 0");
                LoggedIn = false;
                return false;
            }

            // EXACT PORT: Process login data like deezspotag _postLogin
            await PostLoginAsync(userData);
            ChangeAccount(child);

            LoggedIn = true;
            _logger.LogInformation("Successfully logged in as user: {UserName} (ID: {UserId})", 
                CurrentUser?.Name, CurrentUser?.Id);
            
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to login with ARL");
            LoggedIn = false;
            CurrentUser = null;
            return false;
        }
    }

    /// <summary>
    /// Get user data using Gateway API - EXACT PORT from deezspotag gw.ts get_user_data
    /// This also handles API token extraction
    /// </summary>
    private async Task<DeezerUserData> GetUserDataAsync()
    {
        // EXACT PORT: Call getUserData without token first, then extract token from response
        var response = await GatewayApiCallAsync<DeezerUserData>(GetUserDataMethod, new { });
        
        // EXACT PORT: Extract API token from response like deezspotag
        if (string.IsNullOrEmpty(ApiToken) && response?.CheckForm != null)
        {
            ApiToken = response.CheckForm;
            _logger.LogDebug("Extracted API token from getUserData response");
        }
        
        return response ?? new DeezerUserData();
    }

    /// <summary>
    /// Gateway API call with shared authentication - EXACT PORT from deezspotag gw.ts api_call
    /// </summary>
    public async Task<T> GatewayApiCallAsync<T>(string method, object? args = null, Dictionary<string, object>? parameters = null) where T : class
    {
        args ??= new { };
        parameters ??= new Dictionary<string, object>();

        // EXACT PORT: Get API token if needed (except for getUserData)
        if (string.IsNullOrEmpty(ApiToken) && method != GetUserDataMethod)
        {
            ApiToken = await GetTokenAsync();
        }

        var queryParams = new Dictionary<string, object>
        {
            ["api_version"] = "1.0",
            ["api_token"] = method == GetUserDataMethod ? "null" : ApiToken ?? "null",
            ["input"] = "3",
            ["method"] = method,
            ["cid"] = Random.Shared.Next(0, 1_000_000_000)
        };

        // Add additional parameters
        foreach (var param in parameters)
        {
            queryParams[param.Key] = param.Value;
        }

        var url = $"{BuildDeezerWebBaseUrl()}/ajax/gw-light.php?{BuildQueryString(queryParams)}";
        return await ExecuteWithRetryAsync(
            method,
            () => ExecuteGatewayRequestAsync<T>(method, args, url),
            endpoint => new DeezerGatewayException($"Failed to call {endpoint} after {MaxRetries} retries"));
    }

    /// <summary>
    /// Gateway API call with shared authentication - Alias for GatewayApiCallAsync
    /// </summary>
    public async Task<T> GatewayCallAsync<T>(string method, object? args = null, Dictionary<string, object>? parameters = null) where T : class
    {
        return await GatewayApiCallAsync<T>(method, args, parameters);
    }

    /// <summary>
    /// Public API call with shared authentication - EXACT PORT from deezspotag api.ts call
    /// </summary>
    public async Task<T> PublicApiCallAsync<T>(string endpoint, Dictionary<string, object>? args = null) where T : class
    {
        args ??= new Dictionary<string, object>();
        
        if (!string.IsNullOrEmpty(AccessToken))
        {
            args["access_token"] = AccessToken;
        }

        var url = $"{BuildDeezerPublicApiBaseUrl()}/{endpoint}";
        var queryString = BuildQueryString(args);
        if (!string.IsNullOrEmpty(queryString))
        {
            url += $"?{queryString}";
        }

        return await ExecuteWithRetryAsync(
            endpoint,
            () => ExecutePublicRequestAsync<T>(endpoint, url),
            name => new InvalidOperationException($"Failed to call {name} after {MaxRetries} retries"));
    }

    private async Task<T> ExecuteGatewayRequestAsync<T>(string method, object args, string url) where T : class
    {
        using var httpClient = CreateHttpClient();
        var json = JsonConvert.SerializeObject(args);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var response = await httpClient.PostAsync(url, content);
        var responseContent = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("GW API call failed: {StatusCode} - {Content}", response.StatusCode, responseContent);
            throw new DeezerGatewayException($"GW API call failed: {response.StatusCode}");
        }

        var settings = new JsonSerializerSettings();
        settings.Converters.Add(new StringToBooleanConverter());
        var gwResponse = JsonConvert.DeserializeObject<DeezerGwResponse<T>>(responseContent, settings);
        if (gwResponse == null)
        {
            _logger.LogWarning(
                "GW API returned an unparsable/null payload for {Method}. Raw content length: {Length}",
                method,
                responseContent?.Length ?? 0);
            throw new RetryableApiResponseException($"GW API returned null payload for {method}");
        }

        if (gwResponse.Error != null && HasError(gwResponse.Error))
        {
            await HandleGwErrorAsync(gwResponse.Error);
            throw new RetryableApiResponseException($"Gateway error returned for {method}");
        }

        if (string.IsNullOrEmpty(ApiToken) && method == GetUserDataMethod && gwResponse.Results is DeezerUserData userData)
        {
            ApiToken = userData.CheckForm;
        }

        if (gwResponse.Results == null)
        {
            if (typeof(T) == typeof(Newtonsoft.Json.Linq.JObject))
            {
                return (T)(object)new Newtonsoft.Json.Linq.JObject();
            }

            throw new RetryableApiResponseException($"Gateway response missing results for {method}");
        }

        return gwResponse.Results;
    }

    private async Task<T> ExecutePublicRequestAsync<T>(string endpoint, string url) where T : class
    {
        using var httpClient = CreateHttpClient();
        var response = await httpClient.GetAsync(url);
        var content = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("API call failed: {StatusCode} - {Content}", response.StatusCode, content);
            throw new InvalidOperationException($"API call failed: {response.StatusCode}");
        }

        var serializerSettings = new JsonSerializerSettings
        {
            MissingMemberHandling = MissingMemberHandling.Ignore,
            NullValueHandling = NullValueHandling.Ignore
        };

        try
        {
            var directResponse = JsonConvert.DeserializeObject<T>(content, serializerSettings);
            if (!EqualityComparer<T>.Default.Equals(directResponse, default!))
            {
                return directResponse;
            }
        }
        catch (JsonException)
        {
            // Fall through to wrapped response parsing.
        }

        var jsonResponse = JsonConvert.DeserializeObject<DeezerApiResponse<T>>(content, serializerSettings);

        if (jsonResponse?.Error != null)
        {
            await HandleApiErrorAsync(jsonResponse.Error);
            throw new RetryableApiResponseException($"API error returned for {endpoint}");
        }

        if (!EqualityComparer<T>.Default.Equals(jsonResponse!.Data, default!))
        {
            return jsonResponse.Data;
        }

        if (!EqualityComparer<T>.Default.Equals(jsonResponse.Result, default!))
        {
            return jsonResponse.Result;
        }

        throw new InvalidOperationException($"Unable to parse response for {endpoint}");
    }

    private async Task<T> ExecuteWithRetryAsync<T>(
        string operationName,
        Func<Task<T>> operation,
        Func<string, Exception> finalExceptionFactory)
    {
        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (HttpRequestException ex) when (IsRetryableError(ex))
            {
                await HandleRetryableRequestFailureAsync(ex, attempt);
            }
            catch (TaskCanceledException ex)
            {
                await HandleRetryableRequestFailureAsync(ex, attempt);
            }
            catch (RetryableApiResponseException) when (attempt < MaxRetries)
            {
                await Task.Delay(RetryDelay);
            }
        }

        throw finalExceptionFactory(operationName);
    }

    private async Task HandleRetryableRequestFailureAsync(Exception ex, int attempt)
    {
        var reason = ex is TaskCanceledException ? "timeout" : "retryable-http-error";
        _logger.LogWarning(ex, "Retryable request failure ({Reason}). Retrying in {RetryDelaySeconds} seconds.", reason, RetryDelay.TotalSeconds);
        if (attempt < MaxRetries)
        {
            await Task.Delay(RetryDelay);
        }
    }

    private static string BuildQueryString(IEnumerable<KeyValuePair<string, object>> values)
    {
        return string.Join("&", values.Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value.ToString()!)}"));
    }

    public sealed class RetryableApiResponseException : Exception
    {
        public RetryableApiResponseException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// Get track URLs using media API - EXACT PORT from deezspotag deezer.ts get_tracks_url
    /// </summary>
    public async Task<List<string?>> GetTracksUrlAsync(string[] trackTokens, string format)
    {
        var results = await GetTracksUrlWithStatusAsync(trackTokens, format);
        return results.Select(result => result.Url).ToList();
    }

    /// <summary>
    /// Get track URLs using media API with error codes preserved (for token refresh logic).
    /// </summary>
    public async Task<List<DeezerMediaResult>> GetTracksUrlWithStatusAsync(string[] trackTokens, string format)
    {
        if (trackTokens == null || trackTokens.Length == 0)
            return new List<DeezerMediaResult>();

        if (!HasValidMediaSession())
        {
            _logger.LogError("Not logged in or no license token available");
            return trackTokens.Select(_ => DeezerMediaResult.Empty()).ToList();
        }

        ValidateMediaFormatPermission(format);
        var requestBody = BuildMediaRequestBody(trackTokens, format);

        try
        {
            using var httpClient = CreateHttpClient();
            var json = JsonConvert.SerializeObject(requestBody);
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            _logger.LogDebug("Requesting media URLs for {Count} tracks with format {Format}", trackTokens.Length, format);

            var response = await httpClient.PostAsync(BuildDeezerMediaApiUrl(), content);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode || !TryParseMediaResponse(responseContent, response, out var mediaResponse))
            {
                return trackTokens.Select(_ => DeezerMediaResult.Empty()).ToList();
            }

            return mediaResponse.Data.Select(MapMediaResult).ToList();
        }
        catch (Exception ex) when (!(ex is DeezerException))
        {
            _logger.LogError(ex, "Failed to get media URLs");
            return trackTokens.Select(_ => DeezerMediaResult.Empty()).ToList();
        }
    }

    private bool HasValidMediaSession()
    {
        return LoggedIn && CurrentUser != null && !string.IsNullOrEmpty(CurrentUser.LicenseToken);
    }

    private void ValidateMediaFormatPermission(string format)
    {
        if ((format == "FLAC" || format.StartsWith("MP4_RA")) && CurrentUser!.CanStreamLossless != true)
        {
            throw new DeezerException($"User does not have permission to stream {format} format", "WrongLicense");
        }

        if (format == "MP3_320" && CurrentUser!.CanStreamHq != true)
        {
            throw new DeezerException($"User does not have permission to stream {format} format", "WrongLicense");
        }
    }

    private object BuildMediaRequestBody(string[] trackTokens, string format)
    {
        return new
        {
            license_token = CurrentUser!.LicenseToken,
            media = new[]
            {
                new
                {
                    type = "FULL",
                    formats = new[]
                    {
                        new
                        {
                            cipher = "BF_CBC_STRIPE",
                            format
                        }
                    }
                }
            },
            track_tokens = trackTokens
        };
    }

    private bool TryParseMediaResponse(
        string responseContent,
        HttpResponseMessage response,
        out DeezerMediaResponse mediaResponse)
    {
        mediaResponse = JsonConvert.DeserializeObject<DeezerMediaResponse>(responseContent) ?? new DeezerMediaResponse();
        if (response.IsSuccessStatusCode && mediaResponse.Data != null)
        {
            return true;
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Media API request failed: {StatusCode} - {Content}", response.StatusCode, responseContent);
        }
        else
        {
            _logger.LogError("Invalid media API response: {Response}", responseContent);
        }

        return false;
    }

    private DeezerMediaResult MapMediaResult(DeezerMediaData data)
    {
        if (TryGetMediaErrorResult(data, out var errorResult))
        {
            return errorResult;
        }

        if (data.Media?.Count > 0 && data.Media[0].Sources?.Count > 0)
        {
            var url = data.Media[0].Sources[0].Url;
            _logger.LogDebug("Got media URL: {Url}", url);
            return new DeezerMediaResult { Url = url };
        }

        _logger.LogWarning("No media URL available for track");
        return DeezerMediaResult.Empty();
    }

    private bool TryGetMediaErrorResult(DeezerMediaData data, out DeezerMediaResult result)
    {
        result = DeezerMediaResult.Empty();
        if (data.Errors == null || data.Errors.Count == 0)
        {
            return false;
        }

        var error = data.Errors[0];
        _logger.LogWarning("Media API error for track: Code {Code}, Message: {Message}", error.Code, error.Message);
        if (error.Code == 2002)
        {
            throw new DeezerException($"Track not available in country: {CurrentUser!.Country}", "WrongGeolocation");
        }

        result = new DeezerMediaResult
        {
            Url = null,
            ErrorCode = error.Code,
            ErrorMessage = error.Message
        };
        return true;
    }

    /// <summary>
    /// Post-login processing - EXACT PORT from deezspotag deezer.ts _postLogin
    /// </summary>
    private async Task PostLoginAsync(DeezerUserData userData)
    {
        Children.Clear();

        // EXACT PORT: Check if family account like deezspotag
        var user = userData.User;
        var multiAccount = user?.MultiAccount;
        var isFamily = user is not null
            && multiAccount?.Enabled == true
            && !multiAccount.IsSubAccount;

        if (isFamily)
        {
            try
            {
                // EXACT PORT: Get child accounts like deezspotag
                var childAccounts = await GatewayApiCallAsync<List<GwChildAccount>>("deezer.getChildAccounts");
                _logger.LogDebug("Retrieved {Count} child accounts", childAccounts.Count);
                
                foreach (var child in childAccounts.Where(static child => child.ExtraFamily?.IsLoggableAs == true))
                {
                    var resolvedCountry = ResolveCountry(user!.Options?.LicenseCountry);
                    var resolvedLanguage = ResolveLanguage(user.Setting?.Global?.Language);
                    var childUser = new DeezerUser
                    {
                        Id = child.UserId.ToString(),
                        Name = child.BlogName ?? "",
                        Picture = child.UserPicture ?? "",
                        LicenseToken = user.Options?.LicenseToken ?? "",
                        CanStreamHq = user.Options?.WebHq == true || user.Options?.MobileHq == true,
                        CanStreamLossless = user.Options?.WebLossless == true || user.Options?.MobileLossless == true,
                        Country = resolvedCountry,
                        Language = resolvedLanguage,
                        LovedTracksId = child.LovedTracksId ?? ""
                    };

                    Children.Add(childUser);
                    _logger.LogDebug("Added child account: {Name} (ID: {Id})", childUser.Name, childUser.Id);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to get child accounts");
            }
        }
        
        // EXACT PORT: Add main user if no children like deezspotag
        if (Children.Count == 0)
        {
            var resolvedCountry = ResolveCountry(userData.User?.Options?.LicenseCountry);
            var resolvedLanguage = ResolveLanguage(userData.User?.Setting?.Global?.Language);
            var mainUser = new DeezerUser
            {
                Id = (userData.User?.UserId ?? 0).ToString(),
                Name = userData.User?.BlogName ?? "",
                Picture = userData.User?.UserPicture ?? "",
                LicenseToken = userData.User?.Options?.LicenseToken ?? "",
                CanStreamHq = userData.User?.Options?.WebHq == true || userData.User?.Options?.MobileHq == true,
                CanStreamLossless = userData.User?.Options?.WebLossless == true || userData.User?.Options?.MobileLossless == true,
                Country = resolvedCountry,
                Language = resolvedLanguage
            };

            Children.Add(mainUser);
        }

        UpdateChildAccounts();
    }

    /// <summary>
    /// Change active account - EXACT PORT from deezspotag deezer.ts changeAccount
    /// </summary>
    public void ChangeAccount(int childIndex)
    {
        if (Children.Count == 0) return;

        if (childIndex >= Children.Count) 
            childIndex = 0;

        CurrentUser = Children[childIndex];
        SelectedAccount = childIndex;

        // EXACT PORT: Set language header like deezspotag
        var lang = CurrentUser.Language?.Replace("[^0-9A-Za-z *,-.;=]", "");
        if (!string.IsNullOrEmpty(lang))
        {
            lang = (lang.Length > 2 && lang[2] == '-')
                ? lang.Substring(0, 5)
                : lang.Substring(0, Math.Min(2, lang.Length));

            _httpHeaders["Accept-Language"] = lang;
        }

        _logger.LogDebug("Changed to account: {UserName} (Index: {Index})", CurrentUser.Name, SelectedAccount);
    }

    /// <summary>
    /// Create HTTP client with shared cookies and headers
    /// </summary>
    private HttpClient CreateHttpClient()
    {
        ApplyLocaleOverride();
        var handler = new HttpClientHandler()
        {
            CookieContainer = _sharedCookieContainer
        };
        handler.ServerCertificateCustomValidationCallback = static (message, cert, chain, errors) =>
            errors == SslPolicyErrors.None || AllowInsecureSsl;

        var httpClient = new HttpClient(handler)
        {
            Timeout = HttpRequestTimeout
        };
        
        // Apply all shared headers
        foreach (var header in _httpHeaders)
        {
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
        }
        
        return httpClient;
    }

    private void ApplyLocaleOverride()
    {
        var language = ResolveLanguage(CurrentUser?.Language);
        var country = ResolveCountry(CurrentUser?.Country);

        _httpHeaders["Content-Language"] = $"{language}-{country}";
        _httpHeaders["Accept-Language"] = $"{language}-{country},{language};q=0.9,en-US;q=0.8,en;q=0.7";
    }

    private string ResolveCountry(string? fallback)
    {
        var settings = _settingsProvider?.Invoke();
        var overrideCountry = Environment.GetEnvironmentVariable("DEEZER_COUNTRY");
        if (!string.IsNullOrWhiteSpace(settings?.DeezerCountry))
        {
            return settings.DeezerCountry;
        }

        if (!string.IsNullOrWhiteSpace(overrideCountry))
        {
            return overrideCountry;
        }

        return !string.IsNullOrWhiteSpace(fallback) ? fallback : DefaultCountry;
    }

    private string ResolveLanguage(string? fallback)
    {
        var settings = _settingsProvider?.Invoke();
        var overrideLanguage = Environment.GetEnvironmentVariable("DEEZER_LANGUAGE");
        if (!string.IsNullOrWhiteSpace(settings?.DeezerLanguage))
        {
            return settings.DeezerLanguage;
        }

        if (!string.IsNullOrWhiteSpace(overrideLanguage))
        {
            return overrideLanguage;
        }

        return !string.IsNullOrWhiteSpace(fallback) ? fallback : DefaultLanguage;
    }

    public string? GetCookieValue(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var deezerUris = new[]
        {
            new Uri(BuildDeezerWebBaseUrl()),
            new Uri(BuildDeezerRootBaseUrl())
        };

        var cookie = deezerUris
            .Select(uri => _sharedCookieContainer.GetCookies(uri)[name])
            .FirstOrDefault(cookie => cookie != null && !string.IsNullOrEmpty(cookie.Value));
        if (cookie != null)
        {
            return cookie.Value;
        }

        return null;
    }

    /// <summary>
    /// Get API token - EXACT PORT from deezspotag gw.ts _get_token
    /// </summary>
    private async Task<string> GetTokenAsync()
    {
        var userData = await GetUserDataAsync();
        return userData?.CheckForm ?? "";
    }

    /// <summary>
    /// Handle Gateway API errors - EXACT PORT from deezspotag gw.ts error handling
    /// </summary>
    private async Task HandleGwErrorAsync(object error)
    {
        var errorJson = JsonConvert.SerializeObject(error);
        if (errorJson.Contains("No song data", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Deezer GW missing track data: {Error}", errorJson);
        }
        else
        {
            _logger.LogError("Deezer GW Error: {Error}", errorJson);
        }

        // EXACT PORT: Handle invalid token errors like deezspotag/refreezer
        if (errorJson.Contains("invalid api token", StringComparison.OrdinalIgnoreCase) ||
            errorJson.Contains("Invalid CSRF token", StringComparison.OrdinalIgnoreCase) ||
            errorJson.Contains("VALID_TOKEN_REQUIRED", StringComparison.OrdinalIgnoreCase))
        {
            ApiToken = await GetTokenAsync();
            return; // Retry will happen in calling method
        }

        throw new DeezerGatewayException($"GW API Error: {errorJson}");
    }

    /// <summary>
    /// Handle Public API errors - EXACT PORT from deezspotag api.ts error handling
    /// </summary>
    private async Task HandleApiErrorAsync(DeezerApiError error)
    {
        _logger.LogError("Deezer API Error: Code {Code}, Message: {Message}", error.Code, error.Message);

        // EXACT PORT: Handle rate limits like deezspotag
        switch (error.Code)
        {
            case 4:
            case 700:
                await Task.Delay(5000);
                break;
            default:
                throw new InvalidOperationException($"API Error: {error.Code} - {error.Message}");
        }
    }

    private static bool HasError(object error)
    {
        if (error == null) return false;
        
        if (error is Newtonsoft.Json.Linq.JArray array)
        {
            return array.Count > 0;
        }
        
        if (error is Newtonsoft.Json.Linq.JObject obj)
        {
            return obj.Count > 0;
        }
        
        if (error is Dictionary<string, object> dict)
        {
            return dict.Count > 0;
        }
        
        return false;
    }

    private static bool IsRetryableError(HttpRequestException ex)
    {
        var message = ex.Message.ToLower();
        return message.Contains("timeout") || 
               message.Contains("connection") || 
               message.Contains("network") ||
               message.Contains("reset");
    }

    /// <summary>
    /// Logout and clear session
    /// </summary>
    public async Task LogoutAsync()
    {
        LoggedIn = false;
        CurrentUser = null;
        Children.Clear();
        UpdateChildAccounts();
        SelectedAccount = 0;
        ApiToken = null;
        AccessToken = null;
        
        // Clear cookies
        foreach (Cookie cookie in _sharedCookieContainer.GetCookies(new Uri(BuildDeezerWebBaseUrl())))
        {
            cookie.Expired = true;
        }
        
        _logger.LogDebug("User logged out and session cleared");
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _logger.LogDebug("DeezerSessionManager disposed");
        GC.SuppressFinalize(this);
    }

    private static string BuildDeezerWebBaseUrl()
        => new UriBuilder(DeezerScheme, DeezerWebHost).Uri.ToString().TrimEnd('/');

    private static string BuildDeezerRootBaseUrl()
        => new UriBuilder(DeezerScheme, DeezerRootHost).Uri.ToString().TrimEnd('/');

    private static string BuildDeezerPublicApiBaseUrl()
        => new UriBuilder(DeezerScheme, "api.deezer.com").Uri.ToString().TrimEnd('/');

    private static string BuildDeezerMediaApiUrl()
        => new UriBuilder(DeezerScheme, DeezerMediaHost) { Path = "/v1/get_url" }.Uri.ToString();

    private void UpdateChildAccounts()
    {
        ChildAccounts = Children
            .Select(child => child.Name ?? string.Empty)
            .ToArray();
    }
}

/// <summary>
/// Response models for shared use
/// </summary>
public class DeezerGwResponse<T>
{
    public T Results { get; set; } = default!;
    public object Error { get; set; } = new object();
}

public class DeezerApiResponse<T>
{
    public T? Data { get; set; }
    public T? Result { get; set; }
    public DeezerApiError? Error { get; set; }
}

public class DeezerApiError
{
    public int Code { get; set; }
    public string Message { get; set; } = "";
}

public class DeezerMediaResponse
{
    public List<DeezerMediaData> Data { get; set; } = new();
}

public class DeezerMediaData
{
    public List<DeezerMediaError>? Errors { get; set; }
    public List<DeezerMedia>? Media { get; set; }
}

public class DeezerMediaError
{
    public int Code { get; set; }
    public string Message { get; set; } = "";
}

public class DeezerMedia
{
    public List<DeezerMediaSource> Sources { get; set; } = new();
}

public class DeezerMediaSource
{
    public string Url { get; set; } = "";
}

public class DeezerMediaResult
{
    public string? Url { get; set; }
    public int? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }

    public static DeezerMediaResult Empty()
    {
        return new DeezerMediaResult { Url = null, ErrorCode = null, ErrorMessage = null };
    }
}

public class DeezerException : Exception
{
    public string ErrorCode { get; }

    public DeezerException(string message, string errorCode) : base(message)
    {
        ErrorCode = errorCode;
    }
}

public class StringToBooleanConverter : JsonConverter<bool>
{
    public override bool ReadJson(JsonReader reader, Type objectType, bool existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.String)
        {
            var stringValue = reader.Value?.ToString();
            return stringValue == "1" || stringValue?.ToLower() == "true";
        }
        
        if (reader.TokenType == JsonToken.Boolean)
        {
            return (bool)reader.Value!;
        }
        
        if (reader.TokenType == JsonToken.Integer)
        {
            return Convert.ToInt32(reader.Value) != 0;
        }
        
        return false;
    }

    public override void WriteJson(JsonWriter writer, bool value, JsonSerializer serializer)
    {
        writer.WriteValue(value);
    }
}
