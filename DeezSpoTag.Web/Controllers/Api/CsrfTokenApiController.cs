using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/security/csrf-token")]
public sealed class CsrfTokenApiController : ControllerBase
{
    private readonly IAntiforgery _antiforgery;

    public CsrfTokenApiController(IAntiforgery antiforgery)
    {
        _antiforgery = antiforgery;
    }

    [HttpGet]
    public IActionResult GetToken()
    {
        var tokenSet = _antiforgery.GetAndStoreTokens(HttpContext);
        Response.Headers.CacheControl = "no-store, no-cache, max-age=0, must-revalidate";
        Response.Headers.Pragma = "no-cache";
        Response.Headers.Expires = "0";
        return Ok(new
        {
            requestToken = tokenSet.RequestToken ?? string.Empty
        });
    }
}
