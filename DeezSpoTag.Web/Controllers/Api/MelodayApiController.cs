using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/meloday")]
[ApiController]
[Authorize]
[Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryToken]
[Microsoft.AspNetCore.Mvc.IgnoreAntiforgeryToken]
public class MelodayApiController : ControllerBase
{
    // A run updates every populated library and may contact three remote media servers.
    private static readonly TimeSpan ManualRunTimeout = TimeSpan.FromMinutes(15);
    private readonly MelodayService _melodayService;

    public MelodayApiController(MelodayService melodayService)
    {
        _melodayService = melodayService;
    }

    [HttpPost("run")]
    public async Task<IActionResult> Run(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ManualRunTimeout);

        MelodayRunResult result;
        try
        {
            result = await _melodayService.RunAsync(refreshHistory: true, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status408RequestTimeout, new { message = "Meloday run timed out before completion." });
        }

        if (!result.Success)
        {
            return BadRequest(new
            {
                message = result.Message,
                status = result.Status,
                historySources = result.HistorySources
            });
        }

        return Ok(new
        {
            message = result.Message,
            playlistId = result.PlaylistId,
            status = result.Status,
            historySources = result.HistorySources
        });
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status()
    {
        return Ok(await _melodayService.GetStatusAsync());
    }
}
