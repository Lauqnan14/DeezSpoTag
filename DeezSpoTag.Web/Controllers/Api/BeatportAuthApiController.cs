using DeezSpoTag.Web.Services;
using DeezSpoTag.Web.Services.AutoTag;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[LocalApiAuthorize]
[Route("api/platform-auth/beatport")]
[AutoValidateAntiforgeryToken]
public sealed class BeatportAuthApiController : ControllerBase
{
    private readonly PlatformAuthService _authService;
    private readonly BeatportTokenService _tokens;
    public BeatportAuthApiController(PlatformAuthService authService, BeatportTokenService tokens)
    { _authService = authService; _tokens = tokens; }

    [HttpGet("status")]
    public async Task<IActionResult> Status()
    {
        var auth = (await _authService.LoadAsync()).Beatport;
        return Ok(new
        {
            configured = !string.IsNullOrWhiteSpace(auth?.ClientId) && !string.IsNullOrWhiteSpace(auth?.RedirectUri),
            connected = !string.IsNullOrWhiteSpace(auth?.RefreshToken)
                || (!string.IsNullOrWhiteSpace(auth?.AccessToken) && auth.ExpiresAtUtc > DateTimeOffset.UtcNow),
            clientId = auth?.ClientId,
            redirectUri = auth?.RedirectUri,
            scope = auth?.Scope,
            expiresAtUtc = auth?.ExpiresAtUtc
        });
    }

    [HttpPost("configure")]
    public async Task<IActionResult> Configure([FromBody] BeatportConfigureRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ClientId)
            || !Uri.TryCreate(request.RedirectUri, UriKind.Absolute, out var redirect)
            || redirect.Scheme is not ("http" or "https")) return BadRequest("A client ID and HTTP(S) redirect URI are required.");
        await _authService.UpdateAsync(state =>
        {
            var previous = state.Beatport;
            state.Beatport = new BeatportAuth
            {
                ClientId = request.ClientId.Trim(), ClientSecret = string.IsNullOrWhiteSpace(request.ClientSecret) ? previous?.ClientSecret : request.ClientSecret.Trim(),
                RedirectUri = redirect.AbsoluteUri, Scope = request.Scope?.Trim(),
                AccessToken = previous?.ClientId == request.ClientId.Trim() ? previous?.AccessToken : null,
                RefreshToken = previous?.ClientId == request.ClientId.Trim() ? previous?.RefreshToken : null,
                ExpiresAtUtc = previous?.ClientId == request.ClientId.Trim() ? previous?.ExpiresAtUtc : null
            };
            return true;
        });
        return Ok(new { success = true });
    }

    [HttpGet("connect")]
    public async Task<IActionResult> Connect(CancellationToken cancellationToken)
    {
        var request = await _tokens.CreateAuthorizationRequestAsync(cancellationToken);
        return Ok(new { authorizationUrl = request.AuthorizationUrl, state = request.State });
    }

    [HttpGet("callback")]
    public async Task<IActionResult> Callback([FromQuery] string code, [FromQuery] string state, CancellationToken cancellationToken)
    {
        await _tokens.CompleteAuthorizationAsync(code, state, cancellationToken);
        return Content("Beatport connected. You can close this window.", "text/plain");
    }

    [HttpDelete]
    public async Task<IActionResult> Disconnect()
    {
        await _authService.UpdateAsync(state => { if (state.Beatport is not null) { state.Beatport.AccessToken = null; state.Beatport.RefreshToken = null; state.Beatport.ExpiresAtUtc = null; } return true; });
        return Ok(new { success = true });
    }

    public sealed record BeatportConfigureRequest(string? ClientId, string? ClientSecret, string? RedirectUri, string? Scope);
}
