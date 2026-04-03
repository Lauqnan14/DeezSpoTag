using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/library/runtime")]
[Authorize]
public class LibraryRuntimeApiController : ControllerBase
{
    private readonly LibraryRuntimeSnapshotService _runtimeSnapshotService;

    public LibraryRuntimeApiController(LibraryRuntimeSnapshotService runtimeSnapshotService)
    {
        _runtimeSnapshotService = runtimeSnapshotService;
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] long? folderId, CancellationToken cancellationToken)
    {
        var snapshot = await _runtimeSnapshotService.BuildSnapshotAsync(folderId, cancellationToken);
        return Ok(new
        {
            scanStatus = snapshot.ScanStatus,
            stats = snapshot.Stats,
            refreshPolicy = new
            {
                scanStatusActiveMs = snapshot.RefreshPolicy.ScanStatusActiveMs,
                scanStatusIdleMs = snapshot.RefreshPolicy.ScanStatusIdleMs,
                analysisMs = snapshot.RefreshPolicy.AnalysisMs,
                minArtistRefreshMs = snapshot.RefreshPolicy.MinArtistRefreshMs
            }
        });
    }
}
