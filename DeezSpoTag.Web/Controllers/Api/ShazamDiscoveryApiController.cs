using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[LocalApiAuthorize]
[Route("api/shazam")]
public sealed class ShazamDiscoveryApiController : ControllerBase
{
    private readonly ShazamDiscoveryService _discoveryService;
    private readonly ILogger<ShazamDiscoveryApiController> _logger;

    public ShazamDiscoveryApiController(
        ShazamDiscoveryService discoveryService,
        ILogger<ShazamDiscoveryApiController> logger)
    {
        _discoveryService = discoveryService;
        _logger = logger;
    }

    [HttpGet("track/{trackId}")]
    public async Task<IActionResult> Track(string trackId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(trackId))
        {
            return BadRequest(new { error = "Track ID is required." });
        }

        try
        {
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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Shazam track lookup failed for trackId {TrackId}.", trackId);
            return Ok(new { available = false });
        }
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

        try
        {
            var related = await _discoveryService.GetRelatedTracksAsync(trackId, limit, offset, cancellationToken);
            return Ok(new
            {
                available = related.Count > 0,
                tracks = related
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Shazam related-track lookup failed for trackId {TrackId}.", trackId);
            return Ok(new
            {
                available = false,
                tracks = Array.Empty<ShazamTrackCard>()
            });
        }
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

        try
        {
            var results = await _discoveryService.SearchTracksAsync(query, limit, offset, cancellationToken);
            return Ok(new
            {
                available = results.Count > 0,
                tracks = results
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Shazam search failed for query '{Query}'.", query);
            return Ok(new
            {
                available = false,
                tracks = Array.Empty<ShazamTrackCard>()
            });
        }
    }

}
