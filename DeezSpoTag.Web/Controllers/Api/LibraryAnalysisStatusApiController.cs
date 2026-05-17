using DeezSpoTag.Services.Library;
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

    public LibraryAnalysisStatusApiController(LibraryRepository repository)
    {
        _repository = repository;
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
        var latest = await _repository.GetLatestTrackAnalysisAsync(cancellationToken);
        if (latest is null)
        {
            return NotFound();
        }

        return Ok(latest);
    }

    [HttpGet("current")]
    public Task<IActionResult> GetCurrent(CancellationToken cancellationToken)
    {
        return GetCurrentProcessingResultAsync(cancellationToken);
    }

    [HttpGet("processing")]
    public Task<IActionResult> GetProcessing(CancellationToken cancellationToken)
    {
        return GetCurrentProcessingResultAsync(cancellationToken);
    }

    private async Task<IActionResult> GetCurrentProcessingResultAsync(CancellationToken cancellationToken)
    {
        var processing = await _repository.GetProcessingTrackAsync(cancellationToken);
        if (processing is null)
        {
            return NotFound();
        }

        return Ok(processing);
    }
}
