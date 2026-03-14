using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/autotag")]
[Authorize]
public class AutoTagPlatformsController : ControllerBase
{
    private readonly AutoTagMetadataService _metadataService;

    public AutoTagPlatformsController(AutoTagMetadataService metadataService)
    {
        _metadataService = metadataService;
    }

    [HttpGet("platforms")]
    public async Task<IActionResult> Platforms()
    {
        var json = await _metadataService.GetPlatformsJsonAsync();
        if (string.IsNullOrWhiteSpace(json))
        {
            return StatusCode(503, "Platform metadata unavailable.");
        }

        return Content(json, "application/json");
    }
}
