using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using DeezSpoTag.Services.Authentication;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Utils;
using DeezSpoTag.Core.Constants;
using DeezSpoTag.Core.Security;
using DeezSpoTag.Integrations.Deezer;

namespace DeezSpoTag.API.Controllers
{
    /// <summary>
    /// Login controller backed by the centralized Deezer session and stored ARL.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [AutoValidateAntiforgeryToken]
    public class LoginController : ControllerBase
    {
        private const string InternalServerErrorMessage = "Internal server error";
        private readonly ILoginStorageService _loginStorage;
        private readonly DeezerClient _deezerClient;
        private readonly AuthenticatedDeezerService _authenticatedDeezerService;
        private readonly ILogger<LoginController> _logger;
        private readonly bool _isSingleUser;

        public LoginController(
            ILoginStorageService loginStorage,
            DeezerClient deezerClient,
            AuthenticatedDeezerService authenticatedDeezerService,
            IConfiguration configuration,
            ILogger<LoginController> logger)
        {
            _loginStorage = loginStorage ?? throw new ArgumentNullException(nameof(loginStorage));
            _deezerClient = deezerClient ?? throw new ArgumentNullException(nameof(deezerClient));
            _authenticatedDeezerService = authenticatedDeezerService ?? throw new ArgumentNullException(nameof(authenticatedDeezerService));
            ArgumentNullException.ThrowIfNull(configuration);
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // Get single user mode setting (like deezspotag isSingleUser)
            _isSingleUser = configuration.GetValue<bool>("IsSingleUser", false);
        }

        /// <summary>
        /// Get login status
        /// Ported from: /deezspotag/webui/src/server/routes/api/get/connect.ts
        /// </summary>
        [HttpGet("status")]
        [HttpGet("/api/authentication/status")]
        public async Task<IActionResult> Status()
        {
            try
            {
                var client = await _authenticatedDeezerService.GetAuthenticatedClientAsync();
                if (client?.LoggedIn != true || client.CurrentUser == null)
                {
                    return Ok(new
                    {
                        status = LoginStatus.FAILED,
                        arl = default(string),
                        live = false,
                        user = default(object),
                        childs = Array.Empty<string>(),
                        currentChild = 0
                    });
                }

                return Ok(new
                {
                    status = LoginStatus.SUCCESS,
                    arl = default(string),
                    live = true,
                    user = new
                    {
                        id = client.CurrentUser.Id,
                        name = client.CurrentUser.Name,
                        picture = client.CurrentUser.Picture,
                        country = client.CurrentUser.Country,
                        can_stream_lossless = client.CurrentUser.CanStreamLossless,
                        can_stream_hq = client.CurrentUser.CanStreamHq
                    },
                    childs = client.ChildAccounts ?? Array.Empty<string>(),
                    currentChild = client.SelectedAccount
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error getting login status");
                return StatusCode(500, new { error = InternalServerErrorMessage });
            }
        }

        /// <summary>
        /// Login with ARL token
        /// Complete port from: /deezspotag/webui/src/server/routes/api/post/loginArl.ts
        /// </summary>
        [HttpPost("loginArl")]
        [HttpPost("login/arl")]
        [HttpPost("/api/authentication/login/arl")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> LoginArl([FromBody] LoginArlRequest request)
        {
            try
            {
                var validationResult = ValidateArlRequest(request);
                if (validationResult != null)
                {
                    return validationResult;
                }

                _logger.LogDebug("LoginArl called with ARL length: {Length}, Child: {Child}",
                    request.Arl.Length, request.Child);

                var response = await GetArlLoginStatusAsync(request);
                var returnValue = BuildArlLoginResponse(request.Arl, response);
                await PersistArlLoginStateAsync(request, response);
                return Ok(returnValue);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error in LoginArl");
                return StatusCode(500, new { error = InternalServerErrorMessage });
            }
        }

        private BadRequestObjectResult? ValidateArlRequest(LoginArlRequest request)
        {
            var normalizedArl = DeezerAuthUtils.NormalizeArl(request.Arl);
            if (!string.IsNullOrEmpty(normalizedArl) && DeezerAuthUtils.IsValidArlLength(normalizedArl))
            {
                request.Arl = normalizedArl;
                return null;
            }

            _logger.LogWarning("LoginArl called with an invalid ARL");
            return BadRequest(new { error = "A valid ARL is required" });
        }

        private async Task<int> GetArlLoginStatusAsync(LoginArlRequest request)
        {
            if (_deezerClient.LoggedIn)
            {
                return LoginStatus.ALREADY_LOGGED;
            }

            try
            {
                var success = await _deezerClient.LoginViaArlAsync(request.Arl, request.Child ?? 0);
                return success ? LoginStatus.SUCCESS : LoginStatus.FAILED;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error during ARL login");
                return LoginStatus.FAILED;
            }
        }

        private object BuildArlLoginResponse(string arl, int response)
        {
            return new
            {
                status = response,
                arl,
                user = _deezerClient.CurrentUser,
                childs = _deezerClient.ChildAccounts ?? Array.Empty<string>(),
                currentChild = _deezerClient.SelectedAccount
            };
        }

        private async Task PersistArlLoginStateAsync(LoginArlRequest request, int response)
        {
            if (response != LoginStatus.NOT_AVAILABLE && response != LoginStatus.FAILED)
            {
                LogQueueStartupState();
                if (_isSingleUser)
                {
                    await _loginStorage.SaveLoginCredentialsAsync(BuildArlLoginData(request.Arl));
                }

                return;
            }

            if (_isSingleUser)
            {
                await _loginStorage.ResetLoginCredentialsAsync();
            }
        }

        private void LogQueueStartupState()
        {
            try
            {
                _logger.LogDebug("Queue started successfully after login");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to start queue after login");
            }
        }

        private LoginData BuildArlLoginData(string arl)
        {
            var user = _deezerClient.CurrentUser;
            return new LoginData
            {
                AccessToken = null,
                Arl = arl,
                User = DeezerUserDataMapper.ToLoginUserData(user)
            };
        }

        /// <summary>
        /// Logout user
        /// Complete port from: /deezspotag/webui/src/server/routes/api/post/logout.ts
        /// </summary>
        [HttpPost("logout")]
        [HttpPost("/api/authentication/logout")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Logout()
        {
            try
            {
                // Reset credentials in single user mode (exact logic from deezspotag)
                if (_isSingleUser)
                {
                    await _loginStorage.ResetLoginCredentialsAsync();
                }

                // Clear session data like deezspotag (exact port from deezspotag logout logic)
                try
                {
                    await ClearSessionDataAsync();
                    _logger.LogDebug("Session data cleared successfully");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Failed to clear session data during logout");
                    // Don't fail logout if session cleanup fails
                }

                return Ok(new { logged_out = true });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error in Logout");
                return StatusCode(500, new { error = InternalServerErrorMessage });
            }
        }

        /// <summary>
        /// Clear session data
        /// Ported from: deezspotag session cleanup logic
        /// </summary>
        private async Task ClearSessionDataAsync()
        {
            try
            {
                // Clear Deezer client session
                if (_deezerClient.LoggedIn)
                {
                    await _deezerClient.LogoutAsync();
                    _logger.LogDebug("Deezer client session cleared");
                }

                _logger.LogDebug("Session data cleanup completed");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw;
            }
        }

    }

    /// <summary>
    /// Login ARL request model
    /// Exact port from deezspotag RawLoginArlBody interface
    /// </summary>
    public class LoginArlRequest
    {
        public required string Arl { get; set; }
        public int? Child { get; set; }
    }

}
