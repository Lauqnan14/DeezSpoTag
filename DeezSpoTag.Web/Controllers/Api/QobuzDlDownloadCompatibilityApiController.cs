using DeezSpoTag.Services.Download.Qobuz;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[AllowAnonymous]
[Route("api")]
public sealed class QobuzDlDownloadCompatibilityApiController : ControllerBase
{
    private readonly IQobuzDownloadService _qobuzDownloadService;

    public QobuzDlDownloadCompatibilityApiController(IQobuzDownloadService qobuzDownloadService)
    {
        _qobuzDownloadService = qobuzDownloadService;
    }

    [HttpGet("download-music")]
    public async Task<IActionResult> DownloadMusic([FromQuery(Name = "track_id")] int trackId, [FromQuery] string? quality = null, CancellationToken cancellationToken = default)
    {
        if (trackId <= 0)
        {
            return BadRequest(new { success = false, error = "track_id must be greater than zero." });
        }

        try
        {
            var resolved = await _qobuzDownloadService.ResolveStreamUrlByTrackIdAsync(
                trackId,
                quality ?? "6",
                allowQualityFallback: true,
                cancellationToken);
            return Ok(new
            {
                success = true,
                data = new
                {
                    url = resolved.Url,
                    quality = resolved.SelectedQuality
                }
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return BadRequest(new { success = false, error = ex.Message });
        }
    }
}
