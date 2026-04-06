using Microsoft.AspNetCore.Mvc;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Services.Authentication;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.RateLimiting;

namespace DeezSpoTag.Web.Controllers.Api;

/// <summary>
/// Login fix API controller for repairing corrupted login files and re-authenticating
/// </summary>
[ApiController]
[Route("api/login-fix")]
[LocalApiAuthorize]
public class LoginFixApiController : ControllerBase
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private readonly ILogger<LoginFixApiController> _logger;
    private readonly DeezerClient _deezerClient;
    private readonly ILoginStorageService _loginStorage;
    private readonly DeezerAuthenticationService _authService;

    public LoginFixApiController(
        ILogger<LoginFixApiController> logger,
        DeezerClient deezerClient,
        ILoginStorageService loginStorage,
        DeezerAuthenticationService authService)
    {
        _logger = logger;
        _deezerClient = deezerClient;
        _loginStorage = loginStorage;
        _authService = authService;
    }

    /// <summary>
    /// Fix corrupted login file and re-authenticate
    /// </summary>
    [HttpPost("repair-and-login")]
    [EnableRateLimiting("AuthEndpoints")]
    public async Task<IActionResult> RepairAndLogin()
    {
        try
        {
            _logger.LogInformation("Starting login file repair and re-authentication process");

            // Step 1: Try to extract ARL from corrupted file
            var extractedArl = await ExtractArlFromCorruptedFileAsync();
            
            if (string.IsNullOrEmpty(extractedArl))
            {
                return Ok(new
                {
                    success = false,
                    message = "Could not extract ARL from corrupted login file",
                    step = "arl_extraction_failed"
                });
            }

            _logger.LogDebug("Successfully extracted ARL from corrupted file: {ArlLength} characters", extractedArl.Length);

            // Step 2: Reset login file to clean state
            await _loginStorage.ResetLoginCredentialsAsync();
            _logger.LogDebug("Reset login file to clean state");

            // Step 3: Logout current session to clear any cached state
            if (_deezerClient.LoggedIn)
            {
                await _deezerClient.LogoutAsync();
                _logger.LogDebug("Logged out existing session");
            }
            DeezerStreamApiController.ClearPlaybackContextCache();

            // Step 4: Re-authenticate with extracted ARL
            var loginResult = await _authService.LoginWithArlAsync(extractedArl);
            
            if (!loginResult.Success)
            {
                return Ok(new
                {
                    success = false,
                    message = $"Re-authentication failed: {loginResult.ErrorMessage}",
                    step = "re_authentication_failed",
                    arl_extracted = true
                });
            }

            _logger.LogInformation("Successfully re-authenticated user: {UserName} (ID: {UserId})", 
                loginResult.User?.Name, loginResult.User?.Id);

            // Step 5: Verify lossless streaming capability
            var canStreamLossless = loginResult.User?.CanStreamLossless == true;
            var canStreamHq = loginResult.User?.CanStreamHq == true;

            _logger.LogDebug("User streaming capabilities - Lossless: {Lossless}, HQ: {HQ}",
                canStreamLossless, canStreamHq);

            return Ok(new
            {
                success = true,
                message = "Login file repaired and user re-authenticated successfully",
                user = new
                {
                    id = loginResult.User?.Id,
                    name = loginResult.User?.Name,
                    country = loginResult.User?.Country,
                    can_stream_lossless = canStreamLossless,
                    can_stream_hq = canStreamHq
                },
                streaming_capabilities = new
                {
                    lossless = canStreamLossless,
                    hq = canStreamHq,
                    basic = true
                }
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error during login file repair and re-authentication");
            return Ok(new
            {
                success = false,
                message = $"Repair process failed: {ex.Message}",
                step = "unexpected_error"
            });
        }
    }

    /// <summary>
    /// Check current authentication status and streaming capabilities
    /// </summary>
    [HttpGet("status")]
    public Task<IActionResult> GetAuthStatus()
    {
        try
        {
            var isLoggedIn = _deezerClient.LoggedIn;
            var currentUser = _deezerClient.CurrentUser;

            if (!isLoggedIn || currentUser == null)
            {
                return Task.FromResult<IActionResult>(Ok(new
                {
                    logged_in = false,
                    message = "User is not logged in",
                    streaming_capabilities = new
                    {
                        lossless = false,
                        hq = false,
                        basic = false
                    }
                }));
            }

            var canStreamLossless = currentUser.CanStreamLossless == true;
            var canStreamHq = currentUser.CanStreamHq == true;
            var streamTier = "basic";
            if (canStreamLossless)
            {
                streamTier = "lossless";
            }
            else if (canStreamHq)
            {
                streamTier = "HQ";
            }

            return Task.FromResult<IActionResult>(Ok(new
            {
                logged_in = true,
                user = new
                {
                    id = currentUser.Id,
                    name = currentUser.Name,
                    country = currentUser.Country,
                    can_stream_lossless = canStreamLossless,
                    can_stream_hq = canStreamHq
                },
                streaming_capabilities = new
                {
                    lossless = canStreamLossless,
                    hq = canStreamHq,
                    basic = true
                },
                message = $"User {currentUser.Name} is logged in with {streamTier} streaming"
            }));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error checking authentication status");
            return Task.FromResult<IActionResult>(Ok(new
            {
                logged_in = false,
                message = $"Error checking status: {ex.Message}",
                streaming_capabilities = new
                {
                    lossless = false,
                    hq = false,
                    basic = false
                }
            }));
        }
    }

    /// <summary>
    /// Force re-authentication with current ARL
    /// </summary>
    [HttpPost("force-reauth")]
    [EnableRateLimiting("AuthEndpoints")]
    public async Task<IActionResult> ForceReauth()
    {
        try
        {
            _logger.LogInformation("Starting forced re-authentication");

            // Get current credentials
            var credentials = await _authService.GetLoginCredentialsAsync();
            
            if (string.IsNullOrEmpty(credentials.Arl))
            {
                return Ok(new
                {
                    success = false,
                    message = "No ARL found in credentials"
                });
            }

            // Logout current session
            if (_deezerClient.LoggedIn)
            {
                await _deezerClient.LogoutAsync();
                _logger.LogInformation("Logged out existing session for forced re-auth");
            }
            DeezerStreamApiController.ClearPlaybackContextCache();

            // Re-authenticate
            var loginResult = await _authService.LoginWithArlAsync(credentials.Arl);
            
            if (!loginResult.Success)
            {
                return Ok(new
                {
                    success = false,
                    message = $"Forced re-authentication failed: {loginResult.ErrorMessage}"
                });
            }

            _logger.LogInformation("Forced re-authentication successful for user: {UserName}", 
                loginResult.User?.Name);

            return Ok(new
            {
                success = true,
                message = "Forced re-authentication successful",
                user = new
                {
                    id = loginResult.User?.Id,
                    name = loginResult.User?.Name,
                    can_stream_lossless = loginResult.User?.CanStreamLossless == true,
                    can_stream_hq = loginResult.User?.CanStreamHq == true
                }
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error during forced re-authentication");
            return Ok(new
            {
                success = false,
                message = $"Forced re-auth failed: {ex.Message}"
            });
        }
    }

    /// <summary>
    /// Test lossless streaming capability by attempting to get a lossless track URL
    /// </summary>
    [HttpPost("test-lossless")]
    public async Task<IActionResult> TestLosslessStreaming([FromBody] TestLosslessRequest request)
    {
        try
        {
            if (string.IsNullOrEmpty(request.TrackId))
            {
                return BadRequest("Track ID is required");
            }

            if (!_deezerClient.LoggedIn)
            {
                return Ok(new
                {
                    success = false,
                    message = "User is not logged in"
                });
            }

            var currentUser = _deezerClient.CurrentUser;
            if (currentUser == null)
            {
                return Ok(new
                {
                    success = false,
                    message = "No current user available"
                });
            }

            _logger.LogInformation("Testing lossless streaming for track TrackId with user UserName");

            // Check user permissions first
            var canStreamLossless = currentUser.CanStreamLossless == true;
            var canStreamHq = currentUser.CanStreamHq == true;

            if (!canStreamLossless)
            {
                return Ok(new
                {
                    success = false,
                    message = "User does not have lossless streaming permission",
                    user_permissions = new
                    {
                        can_stream_lossless = canStreamLossless,
                        can_stream_hq = canStreamHq
                    }
                });
            }

            // Try to get track token first
            using var scope = HttpContext.RequestServices.CreateScope();
            var gatewayService = scope.ServiceProvider.GetRequiredService<DeezerGatewayService>();
            
            var track = await gatewayService.GetTrackAsync(request.TrackId);
            if (track == null)
            {
                return Ok(new
                {
                    success = false,
                    message = "Track not found"
                });
            }

            var trackToken = track.TrackToken;
            if (string.IsNullOrEmpty(trackToken))
            {
                return Ok(new
                {
                    success = false,
                    message = "Track token not available"
                });
            }

            // Test FLAC URL retrieval
            try
            {
                var urls = await _deezerClient.GetTracksUrlAsync(new[] { trackToken }, "FLAC");
                var flacUrl = urls.FirstOrDefault();

                if (string.IsNullOrEmpty(flacUrl))
                {
                    return Ok(new
                    {
                        success = false,
                        message = "FLAC URL not available for this track",
                        track_info = new
                        {
                            id = request.TrackId,
                            title = track.SngTitle,
                            token = trackToken
                        },
                        user_permissions = new
                        {
                            can_stream_lossless = canStreamLossless,
                            can_stream_hq = canStreamHq
                        }
                    });
                }

                return Ok(new
                {
                    success = true,
                    message = "Lossless streaming test successful",
                    track_info = new
                    {
                        id = request.TrackId,
                        title = track.SngTitle,
                        token = trackToken
                    },
                    streaming_test = new
                    {
                        format = "FLAC",
                        url_obtained = true,
                        url_length = flacUrl.Length
                    },
                    user_permissions = new
                    {
                        can_stream_lossless = canStreamLossless,
                        can_stream_hq = canStreamHq
                    }
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Ok(new
                {
                    success = false,
                    message = $"FLAC streaming test failed: {ex.Message}",
                    user_permissions = new
                    {
                        can_stream_lossless = canStreamLossless,
                        can_stream_hq = canStreamHq
                    }
                });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error testing lossless streaming");
            return Ok(new
            {
                success = false,
                message = $"Test failed: {ex.Message}"
            });
        }
    }

    /// <summary>
    /// Extract ARL from corrupted login file using regex patterns
    /// </summary>
    private async Task<string?> ExtractArlFromCorruptedFileAsync()
    {
        try
        {
            // Try multiple possible locations for the login file
            var possiblePaths = new[]
            {
                Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "deezspotag", "login.json"),
                Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "deezspotag", "login.json"),
                Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "deezspotag", "login.json")
            };

            foreach (var path in possiblePaths.Where(System.IO.File.Exists))
            {
                _logger.LogDebug("Checking login file at: {Path}", path);
                
                var content = await System.IO.File.ReadAllTextAsync(path);
                
                // Try to extract ARL using regex
                var arlMatch = Regex.Match(content, @"""arl"":\s*""([^""]+)""", RegexOptions.IgnoreCase, RegexTimeout);
                if (arlMatch.Success)
                {
                    var arl = arlMatch.Groups[1].Value;
                    _logger.LogInformation("Extracted ARL from {Path}: {ArlLength} characters", path, arl.Length);
                    return arl;
                }
                
                // Try alternative pattern without quotes
                var arlMatch2 = Regex.Match(content, @"arl"":\s*""([^""]+)""", RegexOptions.IgnoreCase, RegexTimeout);
                if (arlMatch2.Success)
                {
                    var arl = arlMatch2.Groups[1].Value;
                    _logger.LogInformation("Extracted ARL (alt pattern) from {Path}: {ArlLength} characters", path, arl.Length);
                    return arl;
                }
            }

            _logger.LogWarning("No ARL found in any login file");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error extracting ARL from corrupted file");
            return null;
        }
    }
}

/// <summary>
/// Request model for testing lossless streaming
/// </summary>
public class TestLosslessRequest
{
    public string TrackId { get; set; } = "";
}
