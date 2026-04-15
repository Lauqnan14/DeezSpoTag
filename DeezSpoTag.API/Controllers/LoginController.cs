using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using DeezSpoTag.Services.Authentication;
using DeezSpoTag.Core.Constants;
using DeezSpoTag.Core.Security;
using DeezSpoTag.Integrations.Deezer;
using System.Text.Json;

namespace DeezSpoTag.API.Controllers
{
    /// <summary>
    /// Login controller - Complete port from deezspotag login system
    /// Ported from: /deezspotag/webui/src/server/routes/api/post/loginArl.ts and loginEmail.ts
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class LoginController : ControllerBase
    {
        private const string DeezerScheme = "https";
        private const string DeezerHost = "www.deezer.com";
        private const string InternalServerErrorMessage = "Internal server error";
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
        private readonly ILoginStorageService _loginStorage;
        private readonly DeezerClient _deezerClient;
        private readonly IDeezerAuthenticationService _deezerAuthService;
        private readonly ILogger<LoginController> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IHostEnvironment _hostEnvironment;
        private readonly bool _isSingleUser;

        public LoginController(
        ILoginStorageService loginStorage,
        DeezerClient deezerClient,
        DeezSpoTag.Services.Authentication.IDeezerAuthenticationService deezerAuthService,
            IConfiguration configuration,
            ILogger<LoginController> logger,
            IHttpClientFactory httpClientFactory,
            IHostEnvironment hostEnvironment)
        {
            _loginStorage = loginStorage ?? throw new ArgumentNullException(nameof(loginStorage));
            _deezerClient = deezerClient ?? throw new ArgumentNullException(nameof(deezerClient));
            _deezerAuthService = deezerAuthService ?? throw new ArgumentNullException(nameof(deezerAuthService));
            ArgumentNullException.ThrowIfNull(configuration);
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _hostEnvironment = hostEnvironment ?? throw new ArgumentNullException(nameof(hostEnvironment));

            // Get single user mode setting (like deezspotag isSingleUser)
            _isSingleUser = configuration.GetValue<bool>("IsSingleUser", false);
        }

        /// <summary>
        /// Check if Deezer is available in the current region
        /// Ported from: deezspotag/webui/src/server/deezSpoTagApp.ts isDeezerAvailable method
        /// </summary>
        private async Task<bool> IsDeezerAvailableAsync()
        {
            try
            {
                using var httpClient = _httpClientFactory.CreateClient();
                httpClient.DefaultRequestHeaders.Add("Cookie", "dz_lang=en; Domain=deezer.com; Path=/; Secure; hostOnly=false;");

                var response = await httpClient.GetAsync(BuildDeezerHomeUrl());
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync();

                // Extract title from HTML (exact port from deezspotag)
                var titleMatch = System.Text.RegularExpressions.Regex.Match(
                    content,
                    @"<title[^>]*>([^<]+)<\/title>",
                    System.Text.RegularExpressions.RegexOptions.None,
                    RegexTimeout);
                var title = titleMatch.Success ? titleMatch.Groups[1].Value.Trim() : "";

                // Check if Deezer is available (exact deezspotag logic)
                var isAvailable = title != "Deezer will soon be available in your country.";

                _logger.LogDebug("Deezer availability check: {IsAvailable} (title: {Title})", isAvailable, title);
                return isAvailable;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error checking Deezer availability");
                return false; // Assume not available on error
            }
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
                var loginData = await _loginStorage.LoadLoginCredentialsAsync();

                if (loginData?.Arl == null || loginData.User == null)
                {
                    return Ok(new
                    {
                        status = LoginStatus.FAILED,
                        arl = default(string),
                        user = default(object),
                        childs = Array.Empty<string>(),
                        currentChild = 0
                    });
                }

                return Ok(new
                {
                    status = LoginStatus.SUCCESS,
                    arl = loginData.Arl,
                    user = new
                    {
                        id = loginData.User.Id,
                        name = loginData.User.Name,
                        picture = loginData.User.Picture,
                        country = loginData.User.Country,
                        can_stream_lossless = loginData.User.CanStreamLossless,
                        can_stream_hq = loginData.User.CanStreamHq
                    },
                    childs = _deezerClient.ChildAccounts ?? Array.Empty<string>(),
                    currentChild = _deezerClient.SelectedAccount
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
                response = await ApplyDeezerAvailabilityAsync(response);
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
            if (!string.IsNullOrEmpty(request.Arl))
            {
                return null;
            }

            _logger.LogWarning("LoginArl called with empty ARL");
            return BadRequest(new { error = "ARL is required" });
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

        private async Task<int> ApplyDeezerAvailabilityAsync(int response)
        {
            if (await IsDeezerAvailableAsync())
            {
                return response;
            }

            _logger.LogWarning("Deezer is not available in this region");
            return LoginStatus.NOT_AVAILABLE;
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
        /// Login with email and password
        /// Complete port from: /deezspotag/webui/src/server/routes/api/post/loginEmail.ts
        /// </summary>
        [HttpPost("loginEmail")]
        public async Task<IActionResult> LoginEmail([FromBody] LoginEmailRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.Email) || string.IsNullOrEmpty(request.Password))
                {
                    _logger.LogWarning("LoginEmail called with missing email or password");
                    return BadRequest(new { error = "Email and password are required" });
                }

                _logger.LogDebug("LoginEmail called");

                // Exact logic from deezspotag loginEmail.ts
                string? accessToken = request.AccessToken;

                if (string.IsNullOrEmpty(accessToken))
                {
                    // Get access token from email/password (exact deezspotag logic)
                    accessToken = await _deezerAuthService.GetAccessTokenFromEmailPasswordAsync(
                        request.Email, request.Password);

                    if (accessToken == "undefined")
                        accessToken = null;
                }

                string? arl = null;
                if (!string.IsNullOrEmpty(accessToken))
                {
                    // Get ARL from access token (exact deezspotag logic)
                    arl = await _deezerAuthService.GetArlFromAccessTokenAsync(accessToken);
                }

                // Save credentials in single user mode (exact logic from deezspotag)
                if (_isSingleUser && !string.IsNullOrEmpty(accessToken))
                {
                    await _loginStorage.SaveLoginCredentialsAsync(new LoginData
                    {
                        AccessToken = accessToken,
                        Arl = arl
                    });
                }

                // Return response exactly like deezspotag
                return Ok(new { accessToken, arl });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error in LoginEmail");
                return StatusCode(500, new { error = InternalServerErrorMessage });
            }
        }

        /// <summary>
        /// Logout user
        /// Complete port from: /deezspotag/webui/src/server/routes/api/post/logout.ts
        /// </summary>
        [HttpPost("logout")]
        [HttpPost("/api/authentication/logout")]
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

        #region Authentication Controller Methods (consolidated from AuthenticationController)

        /// <summary>
        /// Login with email and password (alternative endpoint)
        /// Consolidated from AuthenticationController
        /// </summary>
        [HttpPost("login/email")]
        [HttpPost("/api/authentication/login/email")]
        public async Task<IActionResult> LoginWithEmail([FromBody] LoginEmailRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
                {
                    return BadRequest(new { error = "Email and password are required" });
                }

                var result = await _deezerAuthService.LoginWithEmailPasswordAsync(request.Email, request.Password);
                return CreateAuthenticationResponse(result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error during email login");
                return StatusCode(500, new { error = InternalServerErrorMessage });
            }
        }

        /// <summary>
        /// Login with ARL token (alternative endpoint)
        /// Consolidated from AuthenticationController
        /// </summary>
        [HttpPost("login/arl")]
        [HttpPost("/api/authentication/login/arl")]
        public async Task<IActionResult> LoginWithArl([FromBody] LoginArlRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.Arl))
                {
                    return BadRequest(new { error = "ARL token is required" });
                }

                var result = await _deezerAuthService.LoginWithArlAsync(request.Arl);
                return CreateAuthenticationResponse(result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error during ARL login");
                return StatusCode(500, new { error = InternalServerErrorMessage });
            }
        }

        /// <summary>
        /// Get stored credentials (for debugging - remove in production)
        /// Consolidated from AuthenticationController
        /// </summary>
        [HttpGet("credentials")]
        [HttpGet("/api/authentication/credentials")]
        public async Task<IActionResult> GetCredentials()
        {
            try
            {
                if (!_hostEnvironment.IsDevelopment())
                {
                    return NotFound();
                }

                var credentials = await _deezerAuthService.GetLoginCredentialsAsync();

                return Ok(new
                {
                    hasArl = !string.IsNullOrEmpty(credentials.Arl),
                    hasAccessToken = !string.IsNullOrEmpty(credentials.AccessToken),
                    // Don't return actual values for security
                    arlLength = credentials.Arl?.Length ?? 0,
                    accessTokenLength = credentials.AccessToken?.Length ?? 0
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error getting credentials");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        #endregion

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

                await _deezerAuthService.ClearSessionAsync();
                _logger.LogDebug("Authentication service session cleared");

                _logger.LogDebug("Session data cleanup completed");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw;
            }
        }

        private static string BuildDeezerHomeUrl()
            => new UriBuilder(DeezerScheme, DeezerHost) { Path = "/" }.Uri.ToString();

        private IActionResult CreateAuthenticationResponse(AuthenticationResult result)
        {
            if (!result.Success)
            {
                return BadRequest(new
                {
                    success = false,
                    error = result.ErrorMessage,
                    status = (int)result.Status
                });
            }

            return Ok(new
            {
                success = true,
                status = (int)result.Status,
                user = new
                {
                    id = result.User?.Id,
                    name = result.User?.Name,
                    picture = result.User?.Picture,
                    country = result.User?.Country,
                    can_stream_lossless = result.User?.CanStreamLossless,
                    can_stream_hq = result.User?.CanStreamHq
                },
                arl = result.Arl
            });
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

    /// <summary>
    /// Login email request model
    /// Exact port from deezspotag loginEmail request structure
    /// </summary>
    public class LoginEmailRequest
    {
        public required string Email { get; set; }
        public required string Password { get; set; }
        public string? AccessToken { get; set; }
    }


}
