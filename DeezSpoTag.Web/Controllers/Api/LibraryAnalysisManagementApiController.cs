using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/library/analysis")]
[ApiController]
[Authorize]
[Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryToken]
public sealed class LibraryAnalysisRunApiController : ControllerBase
{
    private readonly TrackAnalysisBackgroundService _analysisService;

    public LibraryAnalysisRunApiController(TrackAnalysisBackgroundService analysisService)
    {
        _analysisService = analysisService;
    }

    [HttpPost("run")]
    public IActionResult Run([FromQuery] int batchSize = 100)
    {
        batchSize = Math.Clamp(batchSize, 10, 500);
        var started = _analysisService.TryStartManualAnalysis(batchSize);
        return Ok(new { queued = started, running = true, batchSize, fullScan = true });
    }
}
