using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/library/artist-metadata")]
[Authorize]
[AutoValidateAntiforgeryToken]
public sealed class ArtistMetadataAutomationApiController(
    ArtistMetadataAutomationCoordinator coordinator) : ControllerBase
{
    [HttpGet("status")]
    public IActionResult Status() => Ok(coordinator.GetStatus());

    [HttpPost("cache/refresh")]
    public async Task<IActionResult> RefreshCache(
        [FromBody] ArtistMetadataCacheRefreshRequest? request,
        CancellationToken cancellationToken)
    {
        var queued = await coordinator.EnqueueCacheRefreshAsync(
            request ?? new ArtistMetadataCacheRefreshRequest(null, null, "auto", false),
            cancellationToken);
        return Ok(new { queued, status = coordinator.GetStatus() });
    }

    [HttpPost("cancel")]
    public async Task<IActionResult> Cancel(CancellationToken cancellationToken)
        => Ok(new { cancelled = await coordinator.CancelAsync(cancellationToken), status = coordinator.GetStatus() });

    [HttpPost("cache/pause")]
    public Task<IActionResult> PauseCache([FromBody] ArtistMetadataRunControlRequest request, CancellationToken cancellationToken)
        => ControlRun("cache-refresh", request, resume: false, cancellationToken);

    [HttpPost("cache/resume")]
    public Task<IActionResult> ResumeCache([FromBody] ArtistMetadataRunControlRequest request, CancellationToken cancellationToken)
        => ControlRun("cache-refresh", request, resume: true, cancellationToken);

    [HttpPost("targets/pause")]
    public Task<IActionResult> PauseTargets([FromBody] ArtistMetadataRunControlRequest request, CancellationToken cancellationToken)
        => ControlRun("target-update", request, resume: false, cancellationToken);

    [HttpPost("targets/resume")]
    public Task<IActionResult> ResumeTargets([FromBody] ArtistMetadataRunControlRequest request, CancellationToken cancellationToken)
        => ControlRun("target-update", request, resume: true, cancellationToken);

    private async Task<IActionResult> ControlRun(string operation, ArtistMetadataRunControlRequest request,
        bool resume, CancellationToken cancellationToken)
    {
        var accepted = resume
            ? await coordinator.ResumeAsync(operation, request.RunId, cancellationToken)
            : await coordinator.PauseAsync(operation, request.RunId, cancellationToken);
        var result = new { accepted, status = coordinator.GetStatus() };
        return accepted ? Ok(result) : Conflict(result);
    }

    [HttpPost("targets/update")]
    public async Task<IActionResult> UpdateTargets(
        [FromBody] MetadataUpdaterRunRequest? request,
        CancellationToken cancellationToken)
    {
        var queued = await coordinator.EnqueueTargetUpdateAsync(
            request ?? new MetadataUpdaterRunRequest(),
            cancellationToken);
        return Ok(new { queued, status = coordinator.GetStatus() });
    }
}

public sealed record ArtistMetadataRunControlRequest(string RunId);
