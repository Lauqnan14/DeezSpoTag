using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/spotify/track")]
[Authorize]
public sealed class SpotifyTrackApiController : ControllerBase
{
    private readonly SpotifyPathfinderMetadataClient _pathfinder;
    private readonly ILogger<SpotifyTrackApiController> _logger;

    public SpotifyTrackApiController(
        SpotifyPathfinderMetadataClient pathfinder,
        ILogger<SpotifyTrackApiController> logger)
    {
        _pathfinder = pathfinder;
        _logger = logger;
    }

    [HttpGet("{trackId}/metadata")]
    public async Task<IActionResult> GetMetadata(string trackId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(trackId))
        {
            return BadRequest("trackId is required");
        }

        try
        {
            var url = $"https://open.spotify.com/track/{trackId}";
            var metadata = await _pathfinder.FetchByUrlAsync(url, cancellationToken);
            if (metadata is null)
            {
                return Ok(new { available = false });
            }

            return Ok(new { available = true, metadata });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Spotify track metadata fetch failed");
            return StatusCode(502, new { error = "Spotify track metadata failed." });
        }
    }
}
