using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/library/vibe-match")]
[Authorize]
public sealed class VibeMatchApiController : ControllerBase
{
    private readonly VibeMatchService _service;

    public VibeMatchApiController(VibeMatchService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] long trackId, [FromQuery] int limit = 20, CancellationToken cancellationToken = default)
    {
        if (trackId <= 0)
        {
            return BadRequest("Track ID required.");
        }

        var response = await _service.GetMatchesAsync(trackId, Math.Clamp(limit, 1, 100), cancellationToken);
        return Ok(response);
    }
}
