using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/library/analysis")]
[ApiController]
[Authorize]
[Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryToken]
public sealed class LibraryAnalysisStatusApiController : ControllerBase
{
    private readonly LibraryRepository _repository;
    private readonly TrackAnalysisBackgroundService _analysisService;

    public LibraryAnalysisStatusApiController(
        LibraryRepository repository,
        TrackAnalysisBackgroundService analysisService)
    {
        _repository = repository;
        _analysisService = analysisService;
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken cancellationToken)
    {
        var status = await _repository.GetAnalysisStatusAsync(cancellationToken);
        return Ok(status);
    }

    [HttpGet("latest")]
    public async Task<IActionResult> GetLatest(CancellationToken cancellationToken)
    {
        var runtime = _analysisService.GetRuntimeSnapshot();
        var latest = runtime.Latest ?? await _repository.GetLatestTrackAnalysisAsync(cancellationToken);
        if (latest is null)
        {
            return NotFound();
        }

        return Ok(latest);
    }

    [HttpGet("current")]
    public IActionResult GetCurrent()
    {
        return GetCurrentProcessingResult();
    }

    [HttpGet("processing")]
    public IActionResult GetProcessing()
    {
        return GetCurrentProcessingResult();
    }

    private IActionResult GetCurrentProcessingResult()
    {
        var runtime = _analysisService.GetRuntimeSnapshot();
        var processing = runtime.Current;
        if (processing is null)
        {
            return NotFound();
        }

        return Ok(processing);
    }

    [HttpGet("runtime")]
    public IActionResult GetRuntime()
    {
        return Ok(_analysisService.GetRuntimeSnapshot());
    }
}
