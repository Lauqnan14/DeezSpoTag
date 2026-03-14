using DeezSpoTag.Web.Services.LinkMapping;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/link-map")]
[Authorize]
public sealed class LinkMapApiController : ControllerBase
{
    private readonly DeezerLinkMappingService _deezerLinkMappingService;

    public LinkMapApiController(DeezerLinkMappingService deezerLinkMappingService)
    {
        _deezerLinkMappingService = deezerLinkMappingService;
    }

    [HttpGet("deezer")]
    public async Task<IActionResult> MapToDeezer(
        [FromQuery] string? url,
        CancellationToken cancellationToken = default)
    {
        var result = await _deezerLinkMappingService.MapToDeezerAsync(url, cancellationToken);
        return Ok(result);
    }
}
