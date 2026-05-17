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
    public async Task<IActionResult> Run([FromQuery] int batchSize = 100, CancellationToken cancellationToken = default)
    {
        batchSize = Math.Clamp(batchSize, 10, 500);
        await _analysisService.AnalyzeNowAsync(batchSize, cancellationToken, forceWhenDisabled: true);
        return Ok(new { queued = false, completed = true, batchSize, fullScan = true });
    }
}
