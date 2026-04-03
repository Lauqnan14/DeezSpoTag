using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/library/stats")]
[Authorize]
public class LibraryStatsApiController : ControllerBase
{
    private readonly LibraryStatsSnapshotService _libraryStatsSnapshotService;

    public LibraryStatsApiController(LibraryStatsSnapshotService libraryStatsSnapshotService)
    {
        _libraryStatsSnapshotService = libraryStatsSnapshotService;
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] long? folderId, CancellationToken cancellationToken)
    {
        var payload = await _libraryStatsSnapshotService.BuildStatsPayloadAsync(folderId, cancellationToken);
        return Ok(payload);
    }
}
