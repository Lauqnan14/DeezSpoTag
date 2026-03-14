using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[LocalApiAuthorize]
[Route("api/shazam")]
public sealed class ShazamDiscoveryApiController : ControllerBase
{
    private readonly ShazamDiscoveryService _discoveryService;

    public ShazamDiscoveryApiController(
        ShazamDiscoveryService discoveryService)
    {
        _discoveryService = discoveryService;
    }

    [HttpGet("track/{trackId}")]
    public async Task<IActionResult> Track(string trackId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(trackId))
        {
            return BadRequest(new { error = "Track ID is required." });
        }

        var track = await _discoveryService.GetTrackAsync(trackId, cancellationToken);
        if (track == null)
        {
            return Ok(new { available = false });
        }

        return Ok(new
        {
            available = true,
            track
        });
    }

    [HttpGet("related/{trackId}")]
    public async Task<IActionResult> Related(
        string trackId,
        [FromQuery] int limit = 20,
        [FromQuery] int offset = 0,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(trackId))
        {
            return BadRequest(new { error = "Track ID is required." });
        }

        var related = await _discoveryService.GetRelatedTracksAsync(trackId, limit, offset, cancellationToken);
        return Ok(new
        {
            available = related.Count > 0,
            tracks = related
        });
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search(
        [FromQuery] string query,
        [FromQuery] int limit = 20,
        [FromQuery] int offset = 0,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return BadRequest(new { error = "Query is required." });
        }

        var results = await _discoveryService.SearchTracksAsync(query, limit, offset, cancellationToken);
        return Ok(new
        {
            available = results.Count > 0,
            tracks = results
        });
    }

}
