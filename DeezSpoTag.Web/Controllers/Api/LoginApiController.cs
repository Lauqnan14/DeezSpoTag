using Microsoft.AspNetCore.Mvc;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Services.Authentication;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Services.Utils;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.RateLimiting;
using System.Text.Json;

namespace DeezSpoTag.Web.Controllers.Api
{
    /// <summary>
    /// Login API controller for the Web project - exact port from deezspotag login system
    /// Ported from: /deezspotag/webui/src/server/routes/api/post/loginArl.ts and loginEmail.ts
    /// </summary>
    [Route("api/login")]
    [ApiController]
    [LocalApiAuthorize]
    public class LoginApiController : ControllerBase
    {
        private readonly ILogger<LoginApiController> _logger;
        private readonly DeezerClient _deezerClient;
        private readonly ILoginStorageService _loginStorage;
        private readonly IConfiguration _configuration;
        private readonly DeezSpoTagSettingsService _settingsService;
        private readonly DeezerAuthUtils _authUtils;
        private readonly AppleMusicWrapperService _appleWrapperService;

        // Login status constants exactly like deezspotag
        private const int LOGIN_STATUS_NOT_AVAILABLE = -1;
        private const int LOGIN_STATUS_FAILED = 0;
        private const int LOGIN_STATUS_SUCCESS = 1;
        private const int LOGIN_STATUS_ALREADY_LOGGED = 2;
        private const string IsSingleUserSetting = "IsSingleUser";
        public LoginApiController(
            ILogger<LoginApiController> logger,
            DeezerClient deezerClient,
            ILoginStorageService loginStorage,
            IConfiguration configuration,
            DeezSpoTagSettingsService settingsService,
            DeezerAuthUtils authUtils,
            AppleMusicWrapperService appleWrapperService)
        {
            _logger = logger;
            _deezerClient = deezerClient;
            _loginStorage = loginStorage;
            _configuration = configuration;
            _settingsService = settingsService;
            _authUtils = authUtils;
            _appleWrapperService = appleWrapperService;
        }

        /// <summary>
        /// Get login status - exact port from deezspotag connect.ts
        /// </summary>
        [HttpGet("status")]
        public async Task<IActionResult> Status()
        {
            var gate = EnsureAccess();
            if (gate != null)
            {
                return gate;
            }

            try
            {
                var loginData = await _loginStorage.LoadLoginCredentialsAsync();

                if (loginData?.Arl == null || loginData.User == null)
                {
                    return Ok(BuildFailedLoginResponse(LOGIN_STATUS_FAILED, hasStoredCredentials: false));
                }

                var normalizedArl = DeezSpoTag.Services.Utils.DeezerAuthUtils.NormalizeArl(loginData.Arl);
                var hasLiveSession = _deezerClient.LoggedIn && _deezerClient.CurrentUser != null;
                if (!hasLiveSession)
                {
                    if (string.IsNullOrEmpty(normalizedArl))
                    {
                        await _loginStorage.ResetLoginCredentialsAsync();
                        return Ok(BuildFailedLoginResponse(LOGIN_STATUS_FAILED, hasStoredCredentials: false));
                    }

                    DeezerStreamApiController.ClearPlaybackContextCache();
                    var refreshSuccess = await _deezerClient.LoginViaArlAsync(normalizedArl, 0);
                    if (!refreshSuccess || _deezerClient.CurrentUser == null)
                    {
                        _logger.LogInformation("Stored Deezer session is no longer valid. Clearing persisted login state.");
                        await _loginStorage.ResetLoginCredentialsAsync();
                        return Ok(BuildFailedLoginResponse(LOGIN_STATUS_FAILED, hasStoredCredentials: false));
                    }

                    await _loginStorage.SaveLoginCredentialsAsync(CreateLoginData(normalizedArl, loginData.AccessToken, _deezerClient.CurrentUser));
                }

                return Ok(BuildStatusResponse(LOGIN_STATUS_SUCCESS, !string.IsNullOrWhiteSpace(normalizedArl ?? loginData.Arl), loginData.User));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error getting login status");
                return Ok(BuildFailedLoginResponse(LOGIN_STATUS_FAILED, hasStoredCredentials: false));
            }
        }

        /// <summary>
        /// Login with ARL - exact port from deezspotag loginArl.ts
        /// </summary>
        [HttpPost("loginArl")]
        [EnableRateLimiting("AuthEndpoints")]
        public async Task<IActionResult> LoginArl([FromBody] LoginArlRequest request)
        {
            var gate = EnsureAccess();
            if (gate != null)
            {
                return gate;
            }

            var normalizedArl = DeezSpoTag.Services.Utils.DeezerAuthUtils.NormalizeArl(request.Arl);
            try
            {
                if (string.IsNullOrEmpty(normalizedArl))
                {
                    _logger.LogWarning("LoginArl called with empty ARL");
                    return Ok(BuildFailedLoginResponse(LOGIN_STATUS_FAILED, hasStoredCredentials: false));
                }

                if (!DeezSpoTag.Services.Utils.DeezerAuthUtils.IsValidArlLength(normalizedArl))
                {
                    _logger.LogWarning("LoginArl called with invalid ARL length");
                    return Ok(BuildFailedLoginResponse(LOGIN_STATUS_FAILED, hasStoredCredentials: false));
                }

                _logger.LogDebug("LoginArl called with ARL length: {Length}, Child: {Child}", 
                    normalizedArl.Length, request.Child);

                // Exact logic from deezspotag loginArl.ts
                int response;
                
                if (!_deezerClient.LoggedIn)
                {
                    try
                    {
                        // Use DeezerClient directly to validate ARL and get user info
                        var success = await _deezerClient.LoginViaArlAsync(normalizedArl, request.Child ?? 0);
                        response = success ? LOGIN_STATUS_SUCCESS : LOGIN_STATUS_FAILED;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "Error during ARL login");
                        response = LOGIN_STATUS_FAILED;
                    }
                }
                else
                {
                    response = LOGIN_STATUS_ALREADY_LOGGED;
                }

                // Check if Deezer is available (exact port from deezspotag loginArl.ts)
                if (!(await _authUtils.IsDeezerAvailableAsync()))
                {
                    response = LOGIN_STATUS_NOT_AVAILABLE;
                    _logger.LogWarning("Deezer is not available in this region");
                }

                // Prepare return value exactly like deezspotag
                await PersistLoginResultAsync(response, normalizedArl);
                return Ok(BuildStatusResponse(response, !string.IsNullOrWhiteSpace(normalizedArl)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error in LoginArl");
                return Ok(BuildFailedLoginResponse(
                    LOGIN_STATUS_NOT_AVAILABLE,
                    !string.IsNullOrWhiteSpace(normalizedArl),
                    ex.Message));
            }
        }

        /// <summary>
        /// Login with email and password - exact port from deezspotag loginEmail.ts
        /// </summary>
        [HttpPost("loginEmail")]
        [EnableRateLimiting("AuthEndpoints")]
        public async Task<IActionResult> LoginEmail([FromBody] LoginEmailRequest request)
        {
            var gate = EnsureAccess();
            if (gate != null)
            {
                return gate;
            }

            try
            {
                if (string.IsNullOrEmpty(request.Email) || string.IsNullOrEmpty(request.Password))
                {
                    _logger.LogWarning("LoginEmail called with missing email or password");
                    return BadRequest(new { error = "Email and password are required" });
                }

                _logger.LogDebug("LoginEmail called for email {Email}", request.Email);

                var arl = await _authUtils.LoginWithEmailPasswordAsync(request.Email, request.Password);
                var normalizedArl = DeezSpoTag.Services.Utils.DeezerAuthUtils.NormalizeArl(arl);

                if (string.IsNullOrEmpty(normalizedArl))
                {
                    _logger.LogWarning("LoginEmail failed to obtain ARL");
                    return Ok(new { status = LOGIN_STATUS_FAILED });
                }

                if (!DeezSpoTag.Services.Utils.DeezerAuthUtils.IsValidArlLength(normalizedArl))
                {
                    _logger.LogWarning("LoginEmail obtained invalid ARL length");
                    return Ok(new { status = LOGIN_STATUS_FAILED });
                }

                var success = await _deezerClient.LoginViaArlAsync(normalizedArl);
                var response = success ? LOGIN_STATUS_SUCCESS : LOGIN_STATUS_FAILED;

                if (!(await _authUtils.IsDeezerAvailableAsync()))
                {
                    response = LOGIN_STATUS_NOT_AVAILABLE;
                    _logger.LogWarning("Deezer is not available in this region");
                }

                await PersistLoginResultAsync(response, normalizedArl);
                return Ok(BuildStatusResponse(response, !string.IsNullOrWhiteSpace(normalizedArl)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error in LoginEmail");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        [HttpPost("auto-login")]
        [EnableRateLimiting("AuthEndpoints")]
        public async Task<IActionResult> AutoLogin()
        {
            var gate = EnsureAccess();
            if (gate != null)
            {
                return gate;
            }

            try
            {
                var credentials = await _loginStorage.LoadLoginCredentialsAsync();
                var normalizedArl = DeezSpoTag.Services.Utils.DeezerAuthUtils.NormalizeArl(credentials?.Arl);
                if (string.IsNullOrWhiteSpace(normalizedArl) || !DeezSpoTag.Services.Utils.DeezerAuthUtils.IsValidArlLength(normalizedArl))
                {
                    return Ok(BuildFailedLoginResponse(LOGIN_STATUS_FAILED, hasStoredCredentials: false));
                }

                var response = LOGIN_STATUS_FAILED;
                if (_deezerClient.LoggedIn)
                {
                    response = LOGIN_STATUS_ALREADY_LOGGED;
                }
                else
                {
                    response = await _deezerClient.LoginViaArlAsync(normalizedArl, 0)
                        ? LOGIN_STATUS_SUCCESS
                        : LOGIN_STATUS_FAILED;
                }

                if (!(await _authUtils.IsDeezerAvailableAsync()))
                {
                    response = LOGIN_STATUS_NOT_AVAILABLE;
                }

                await PersistLoginResultAsync(response, normalizedArl);
                return Ok(BuildStatusResponse(response, hasStoredCredentials: true));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error in AutoLogin");
                return Ok(BuildFailedLoginResponse(LOGIN_STATUS_FAILED, hasStoredCredentials: false));
            }
        }

        private async Task PersistLoginResultAsync(int response, string? normalizedArl)
        {
            DeezerStreamApiController.ClearPlaybackContextCache();
            var isSingleUser = _configuration.GetValue<bool>(IsSingleUserSetting, true);
            if (response is LOGIN_STATUS_SUCCESS or LOGIN_STATUS_ALREADY_LOGGED)
            {
                if (isSingleUser)
                {
                    await _loginStorage.SaveLoginCredentialsAsync(CreateLoginData(normalizedArl, null, _deezerClient.CurrentUser));
                }

                if (_deezerClient.CurrentUser != null)
                {
                    DeezerAccountCapabilityService.UpdateMaxBitrateForUser(_deezerClient.CurrentUser, _settingsService, _logger);
                }

                return;
            }

            if (isSingleUser)
            {
                await _loginStorage.ResetLoginCredentialsAsync();
            }
        }

        private static LoginData CreateLoginData(string? arl, string? accessToken, DeezSpoTag.Core.Models.Deezer.DeezerUser? user)
        {
            return new LoginData
            {
                AccessToken = accessToken,
                Arl = arl,
                User = DeezerUserDataMapper.ToLoginUserData(user)
            };
        }

        private object BuildStatusResponse(int status, bool hasStoredCredentials, UserData? fallbackUser = null)
        {
            var currentUser = _deezerClient.CurrentUser;
            var user = currentUser != null || fallbackUser != null
                ? new
                {
                    id = currentUser?.Id?.ToString() ?? fallbackUser?.Id ?? "0",
                    name = currentUser?.Name ?? fallbackUser?.Name ?? string.Empty,
                    picture = currentUser?.Picture ?? fallbackUser?.Picture ?? string.Empty,
                    country = currentUser?.Country ?? fallbackUser?.Country ?? string.Empty,
                    can_stream_lossless = currentUser?.CanStreamLossless ?? fallbackUser?.CanStreamLossless ?? false,
                    can_stream_hq = currentUser?.CanStreamHq ?? fallbackUser?.CanStreamHq ?? false
                }
                : default(object);

            return new
            {
                status,
                hasStoredCredentials,
                user,
                childs = _deezerClient.ChildAccounts ?? Array.Empty<string>(),
                currentChild = _deezerClient.SelectedAccount
            };
        }

        private static object BuildFailedLoginResponse(int status, bool hasStoredCredentials, string? error = null)
        {
            return new
            {
                status,
                hasStoredCredentials,
                user = default(object),
                childs = Array.Empty<string>(),
                currentChild = 0,
                error
            };
        }

        /// <summary>
        /// Logout user - exact port from deezspotag logout.ts
        /// </summary>
        [HttpPost("logout")]
        public async Task<IActionResult> Logout(CancellationToken cancellationToken)
        {
            var gate = EnsureAccess();
            if (gate != null)
            {
                return gate;
            }

            try
            {
                _logger.LogInformation("Starting logout process");

                // Reset credentials (exact logic from deezspotag)
                var isSingleUser = _configuration.GetValue<bool>(IsSingleUserSetting, true);
                if (isSingleUser)
                {
                    await _loginStorage.ResetLoginCredentialsAsync();
                    _logger.LogDebug("Login credentials reset");
                }

                // Clear Deezer client session
                if (_deezerClient.LoggedIn)
                {
                    await _deezerClient.LogoutAsync();
                    _logger.LogDebug("Deezer client session cleared");
                }

                DeezerStreamApiController.ClearPlaybackContextCache();

                // Also clear Apple wrapper session on manual logout so restart does not auto-restore Apple auth.
                var appleLogoutResult = await _appleWrapperService.LogoutExternalWrapperSessionAsync(cancellationToken);
                if (!appleLogoutResult.Success)
                {
                    _logger.LogWarning("Apple wrapper session clear failed during logout: {Error}", appleLogoutResult.Error);
                }

                _logger.LogInformation("User logged out successfully");
                return Ok(new { logged_out = true });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error in Logout");
                return Ok(new { logged_out = false, error = ex.Message });
            }
        }

        private UnauthorizedObjectResult? EnsureAccess()
        {
            return LocalApiAccess.IsAllowed(HttpContext)
                ? null
                : Unauthorized("Authentication required.");
        }

    }

    /// <summary>
    /// Login ARL request model - exact port from deezspotag RawLoginArlBody interface
    /// </summary>
    public class LoginArlRequest
    {
        public required string Arl { get; set; }
        public int? Child { get; set; }
    }

    /// <summary>
    /// Login email request model - exact port from deezspotag loginEmail request structure
    /// </summary>
    public class LoginEmailRequest
    {
        public required string Email { get; set; }
        public required string Password { get; set; }
        public string? AccessToken { get; set; }
    }
}
