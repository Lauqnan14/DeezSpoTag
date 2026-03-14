using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[LocalApiAuthorize]
[Route("api/apple-music/wrapper-ref")]
public sealed class AppleMusicWrapperApiController : ControllerBase
{
    private readonly AppleMusicWrapperService _wrapperService;
    private readonly ILogger<AppleMusicWrapperApiController> _logger;

    public AppleMusicWrapperApiController(
        AppleMusicWrapperService wrapperService,
        ILogger<AppleMusicWrapperApiController> logger)
    {
        _wrapperService = wrapperService;
        _logger = logger;
    }

    [HttpGet("status")]
    public IActionResult Status()
    {
        var gate = EnsureWrapperAccess();
        if (gate != null)
        {
            return gate;
        }

        return Ok(ToResponse(_wrapperService.GetStatus()));
    }

    [HttpGet("helper/status")]
    public async Task<IActionResult> HelperStatus(CancellationToken cancellationToken)
    {
        var gate = EnsureWrapperAccess();
        if (gate != null)
        {
            return gate;
        }

        var result = await _wrapperService.GetExternalWrapperHelperStatusAsync(cancellationToken);
        return Ok(result);
    }

    [HttpGet("helper/health")]
    public async Task<IActionResult> HelperHealth(CancellationToken cancellationToken)
    {
        var gate = EnsureWrapperAccess();
        if (gate != null)
        {
            return gate;
        }

        var result = await _wrapperService.CheckExternalWrapperHealthAsync(cancellationToken);
        return Ok(result);
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] AppleWrapperLoginRequest payload, CancellationToken cancellationToken)
    {
        var gate = EnsureWrapperAccess();
        if (gate != null)
        {
            return gate;
        }

        try
        {
            var status = await _wrapperService.StartLoginAsync(payload.Email, payload.Password, cancellationToken);
            return Ok(ToResponse(status));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Apple Music wrapper login failed.");
            return StatusCode(500, "Apple Music wrapper login failed.");
        }
    }

    [HttpPost("2fa")]
    public async Task<IActionResult> SubmitTwoFactor([FromBody] AppleWrapperTwoFactorRequest payload, CancellationToken cancellationToken)
    {
        var gate = EnsureWrapperAccess();
        if (gate != null)
        {
            return gate;
        }

        try
        {
            var status = await _wrapperService.SubmitTwoFactorAsync(payload.Code, cancellationToken);
            return Ok(ToResponse(status));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Apple Music wrapper 2FA failed.");
            return StatusCode(500, "Apple Music wrapper 2FA failed.");
        }
    }

    private object ToResponse(AppleMusicWrapperStatusSnapshot status)
    {
        return new
        {
            status = status.Status,
            message = status.Message,
            email = status.Email,
            needsTwoFactor = status.NeedsTwoFactor,
            wrapperReady = status.WrapperReady,
            externalMode = AppleMusicWrapperService.IsExternalModeEnabled(),
            recentOutput = _wrapperService.GetRecentOutput(),
            diagnostics = _wrapperService.GetDiagnostics()
        };
    }

    public sealed class AppleWrapperLoginRequest
    {
        [Required]
        public string Email { get; set; } = string.Empty;

        [Required]
        public string Password { get; set; } = string.Empty;
    }

    public sealed class AppleWrapperTwoFactorRequest
    {
        [Required]
        public string Code { get; set; } = string.Empty;
    }

    private UnauthorizedObjectResult? EnsureWrapperAccess()
    {
        if (LocalApiAccess.IsAllowed(HttpContext))
        {
            return null;
        }

        return Unauthorized("Authentication required.");
    }
}
