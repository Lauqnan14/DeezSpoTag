using System.Text.RegularExpressions;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Services.Authentication;
using DeezSpoTag.Services.Settings;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[LocalApiAuthorize]
[Route("api/connect")]
public sealed class ConnectApiController : ControllerBase
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DeezerAvailabilityCacheTtl = TimeSpan.FromMinutes(10);
    private static readonly Uri DeezerAvailabilityUri = new UriBuilder(Uri.UriSchemeHttps, "www.deezer.com").Uri;
    private static readonly object DeezerAvailabilityCacheLock = new();
    private static DateTimeOffset _deezerAvailabilityCheckedAt = DateTimeOffset.MinValue;
    private static bool? _cachedDeezerAvailability;
    private readonly ILogger<ConnectApiController> _logger;
    private readonly DeezerClient _deezerClient;
    private readonly ILoginStorageService _loginStorage;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly IHttpClientFactory _httpClientFactory;

    public ConnectApiController(
        ILogger<ConnectApiController> logger,
        DeezerClient deezerClient,
        ILoginStorageService loginStorage,
        DeezSpoTagSettingsService settingsService,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _deezerClient = deezerClient;
        _loginStorage = loginStorage;
        _settingsService = settingsService;
        _httpClientFactory = httpClientFactory;
    }

    [HttpGet]
    public async Task<IActionResult> Connect()
    {
        var gate = EnsureAccess();
        if (gate != null)
        {
            return gate;
        }

        try
        {
            var autologin = !_deezerClient.LoggedIn;
            var currentUser = _deezerClient.CurrentUser;
            var settings = _settingsService.LoadSettings();
            var defaultSettings = DeezSpoTagSettingsService.GetStaticDefaultSettings();

            var result = new
            {
                autologin,
                currentUser = currentUser != null ? new
                {
                    id = currentUser.Id?.ToString() ?? "0",
                    name = currentUser.Name ?? string.Empty,
                    picture = currentUser.Picture ?? string.Empty,
                    country = currentUser.Country ?? string.Empty,
                    can_stream_lossless = currentUser.CanStreamLossless == true,
                    can_stream_hq = currentUser.CanStreamHq == true,
                    language = currentUser.Language ?? string.Empty,
                    loved_tracks = currentUser.LovedTracksId ?? string.Empty
                } : null,
                deezerAvailable = await IsDeezerAvailableAsync(),
                settingsData = new
                {
                    settings,
                    defaultSettings
                },
                singleUser = await GetSingleUserCredentialsAsync(autologin)
            };

            return Ok(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Connect endpoint failed");
            return Ok(new
            {
                autologin = true,
                currentUser = default(object),
                deezerAvailable = true,
                settingsData = new
                {
                    settings = _settingsService.LoadSettings(),
                    defaultSettings = DeezSpoTagSettingsService.GetStaticDefaultSettings()
                },
                singleUser = default(object),
                error = ex.Message
            });
        }
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> RefreshUserData()
    {
        var gate = EnsureAccess();
        if (gate != null)
        {
            return gate;
        }

        try
        {
            if (!_deezerClient.LoggedIn)
            {
                return Ok(new
                {
                    success = false,
                    message = "User is not logged in"
                });
            }

            var credentials = await _loginStorage.LoadLoginCredentialsAsync();
            if (!string.IsNullOrWhiteSpace(credentials?.Arl))
            {
                await _deezerClient.LoginViaArlAsync(credentials.Arl);
            }

            return Ok(new
            {
                success = true,
                message = "User data refreshed successfully",
                user = _deezerClient.CurrentUser != null ? new
                {
                    id = _deezerClient.CurrentUser.Id?.ToString() ?? "0",
                    name = _deezerClient.CurrentUser.Name ?? string.Empty,
                    can_stream_lossless = _deezerClient.CurrentUser.CanStreamLossless == true,
                    can_stream_hq = _deezerClient.CurrentUser.CanStreamHq == true
                } : null
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error refreshing Deezer user data");
            return Ok(new
            {
                success = false,
                message = $"Error refreshing user data: {ex.Message}"
            });
        }
    }

    private async Task<object?> GetSingleUserCredentialsAsync(bool autologin)
    {
        if (!autologin)
        {
            return null;
        }

        try
        {
            var credentials = await _loginStorage.LoadLoginCredentialsAsync();
            if (!string.IsNullOrWhiteSpace(credentials?.Arl))
            {
                return new
                {
                    hasStoredCredentials = true
                };
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load saved credentials");
        }

        return null;
    }

    private async Task<bool> IsDeezerAvailableAsync()
    {
        lock (DeezerAvailabilityCacheLock)
        {
            if (_cachedDeezerAvailability.HasValue
                && DateTimeOffset.UtcNow - _deezerAvailabilityCheckedAt < DeezerAvailabilityCacheTtl)
            {
                return _cachedDeezerAvailability.Value;
            }
        }

        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("Cookie", "dz_lang=en; Domain=deezer.com; Path=/; Secure; hostOnly=false;");
            using var response = await client.GetAsync(DeezerAvailabilityUri);
            if (!response.IsSuccessStatusCode)
            {
                CacheDeezerAvailability(false);
                return false;
            }

            var content = await response.Content.ReadAsStringAsync();
            var titleMatch = Regex.Match(content, @"<title[^>]*>([^<]+)<\/title>", RegexOptions.None, RegexTimeout);
            var title = titleMatch.Success ? titleMatch.Groups[1].Value.Trim() : string.Empty;
            var available = title != "Deezer will soon be available in your country.";
            CacheDeezerAvailability(available);
            return available;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Deezer availability check failed");
            return true;
        }
    }

    private static void CacheDeezerAvailability(bool available)
    {
        lock (DeezerAvailabilityCacheLock)
        {
            _cachedDeezerAvailability = available;
            _deezerAvailabilityCheckedAt = DateTimeOffset.UtcNow;
        }
    }

    private UnauthorizedObjectResult? EnsureAccess()
    {
        return LocalApiAccess.IsAllowed(HttpContext)
            ? null
            : Unauthorized("Authentication required.");
    }
}
