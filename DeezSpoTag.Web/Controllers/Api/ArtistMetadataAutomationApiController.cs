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
