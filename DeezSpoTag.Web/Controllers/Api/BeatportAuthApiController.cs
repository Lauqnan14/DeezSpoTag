using DeezSpoTag.Web.Services;
using DeezSpoTag.Web.Services.AutoTag;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

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

    [HttpPost("configure")]
    public async Task<IActionResult> Configure([FromBody] BeatportConfigureRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ClientId)
            || !Uri.TryCreate(request.RedirectUri, UriKind.Absolute, out var redirect)
            || redirect.Scheme is not ("http" or "https")
            || !string.Equals(redirect.AbsolutePath, "/api/platform-auth/beatport/callback", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(redirect.Fragment)
            || !string.IsNullOrEmpty(redirect.UserInfo))
        {
            return BadRequest("A client ID and the DeezSpoTag Beatport callback URL are required.");
        }

        var clientId = request.ClientId.Trim();
        await _authService.UpdateAsync(state =>
        {
            var previous = state.Beatport;
            var sameClient = string.Equals(previous?.ClientId, clientId, StringComparison.Ordinal);
            state.Beatport = new BeatportAuth
            {
                ClientId = clientId, ClientSecret = string.IsNullOrWhiteSpace(request.ClientSecret) ? previous?.ClientSecret : request.ClientSecret.Trim(),
                RedirectUri = redirect.AbsoluteUri, Scope = request.Scope?.Trim(),
                AccessToken = sameClient ? previous?.AccessToken : null,
                RefreshToken = sameClient ? previous?.RefreshToken : null,
                ExpiresAtUtc = sameClient ? previous?.ExpiresAtUtc : null
            };
            return true;
        });
        return Ok(new { saved = true });
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
        try
        {
            await _tokens.CompleteAuthorizationAsync(code, state, cancellationToken);
            return BeatportCallbackPage(true, "Beatport connected. This window will close automatically.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return BeatportCallbackPage(false, ex.Message, StatusCodes.Status400BadRequest);
        }
    }

    [HttpDelete]
    public async Task<IActionResult> Disconnect()
    {
        await _tokens.ClearConnectionAsync();
        return Ok(new { disconnected = true });
    }

    private static ContentResult BeatportCallbackPage(bool connected, string message, int statusCode = StatusCodes.Status200OK)
    {
        var payload = JsonSerializer.Serialize(new
        {
            type = "deezspotag:beatport-auth",
            connected,
            message
        });
        var title = connected ? "Beatport connected" : "Beatport connection failed";
        var html = $$"""
            <!doctype html>
            <html lang="en">
            <head><meta charset="utf-8"><title>{{title}}</title></head>
            <body>
            <p id="message"></p>
            <script>
            const payload = {{payload}};
            document.getElementById('message').textContent = payload.message;
            if (window.opener && !window.opener.closed) {
                window.opener.postMessage(payload, window.location.origin);
                if (payload.connected) window.close();
            }
            </script>
            </body>
            </html>
            """;
        return new ContentResult { Content = html, ContentType = "text/html; charset=utf-8", StatusCode = statusCode };
    }

    public sealed record BeatportConfigureRequest(string? ClientId, string? ClientSecret, string? RedirectUri, string? Scope);
}
