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
    public async Task<IActionResult> Run([FromQuery] int batchSize = 100)
    {
        batchSize = Math.Clamp(batchSize, 10, 500);
        var request = await _analysisService.RequestManualAnalysisAsync(batchSize);

        // The outcome is reported explicitly so the UI can distinguish a declined
        // run from a disabled feature. Enabling background analysis starts a pass
        // immediately, so "already running" is the common case here and must not be
        // reported as "enable background analysis".
        return Ok(new
        {
            queued = request.Queued,
            alreadyRunning = request.Outcome == VibeAnalysisRunOutcome.AlreadyRunning,
            enabled = request.Outcome != VibeAnalysisRunOutcome.Disabled,
            outcome = request.Outcome.ToString(),
            reason = request.Reason,
            running = request.Queued,
            batchSize,
            fullScan = true
        });
    }
}
