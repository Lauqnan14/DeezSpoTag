using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/availability")]
[Authorize]
public sealed class AvailabilityApiController : ControllerBase
{
    private readonly TrackAvailabilityService _availabilityService;

    public AvailabilityApiController(TrackAvailabilityService availabilityService)
    {
        _availabilityService = availabilityService;
    }

    [HttpGet("track")]
    public Task<IActionResult> GetTrackAvailability(
        [FromQuery] TrackAvailabilityRequest request,
        CancellationToken cancellationToken)
    {
        return ResolveAvailabilityAsync(request, cancellationToken);
    }

    [HttpGet("spotify")]
    public Task<IActionResult> GetSpotifyAvailability(
        [FromQuery] TrackAvailabilityRequest request,
        CancellationToken cancellationToken)
    {
        return ResolveAvailabilityAsync(request, cancellationToken);
    }

    [HttpGet("deezer")]
    public Task<IActionResult> GetDeezerAvailability(
        [FromQuery] TrackAvailabilityRequest request,
        CancellationToken cancellationToken)
    {
        return ResolveAvailabilityAsync(request, cancellationToken);
    }

    [HttpGet("apple")]
    public Task<IActionResult> GetAppleAvailability(
        [FromQuery] TrackAvailabilityRequest request,
        CancellationToken cancellationToken)
    {
        return ResolveAvailabilityAsync(request, cancellationToken);
    }

    private async Task<IActionResult> ResolveAvailabilityAsync(
        TrackAvailabilityRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await _availabilityService.ResolveAsync(request, cancellationToken));
    }
}
