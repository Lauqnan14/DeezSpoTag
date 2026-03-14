using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/meloday")]
[ApiController]
[Authorize]
public class MelodayApiController : ControllerBase
{
    private readonly MelodayService _melodayService;

    public MelodayApiController(MelodayService melodayService)
    {
        _melodayService = melodayService;
    }

    [HttpPost("run")]
    public async Task<IActionResult> Run(CancellationToken cancellationToken)
    {
        var result = await _melodayService.RunAsync(cancellationToken);
        if (!result.Success)
        {
            return BadRequest(new { message = result.Message });
        }

        return Ok(new { message = result.Message, playlistId = result.PlaylistId });
    }

    [HttpGet("status")]
    public IActionResult Status()
    {
        return Ok(_melodayService.GetStatus());
    }
}
