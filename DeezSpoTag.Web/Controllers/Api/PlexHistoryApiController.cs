using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/plex/history")]
[ApiController]
[Authorize]
public class PlexHistoryApiController : ControllerBase
{
    private readonly PlexHistoryImportService _importService;

    public PlexHistoryApiController(PlexHistoryImportService importService)
    {
        _importService = importService;
    }

    [HttpPost("import")]
    public async Task<IActionResult> Import(CancellationToken cancellationToken)
    {
        var imported = await _importService.ImportAsync(cancellationToken);
        return Ok(new { imported });
    }
}
