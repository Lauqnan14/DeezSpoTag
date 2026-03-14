using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/spotify/artist")]
[Authorize]
public sealed class SpotifyArtistApiController : ControllerBase
{
    private readonly SpotifyPathfinderMetadataClient _pathfinder;
    private readonly ILogger<SpotifyArtistApiController> _logger;

    public SpotifyArtistApiController(
        SpotifyPathfinderMetadataClient pathfinder,
        ILogger<SpotifyArtistApiController> logger)
    {
        _pathfinder = pathfinder;
        _logger = logger;
    }

    [HttpGet("{spotifyId}/related")]
    public async Task<IActionResult> GetRelatedArtists(string spotifyId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(spotifyId))
        {
            return BadRequest("spotifyId is required");
        }

        try
        {
            var results = await _pathfinder.FetchArtistRelatedArtistsAsync(spotifyId, cancellationToken);
            return Ok(new { relatedArtists = results });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Spotify related artists fetch failed");
            return StatusCode(502, new { error = "Spotify related artists failed." });
        }
    }

    [HttpGet("{spotifyId}/appears-on")]
    public async Task<IActionResult> GetAppearsOn(string spotifyId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(spotifyId))
        {
            return BadRequest("spotifyId is required");
        }

        try
        {
            var results = await _pathfinder.FetchArtistAppearsOnAsync(spotifyId, cancellationToken);
            return Ok(new { appearsOn = results });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Spotify appears-on fetch failed");
            return StatusCode(502, new { error = "Spotify appears-on failed." });
        }
    }

    [HttpGet("{spotifyId}/metadata")]
    public async Task<IActionResult> GetMetadata(string spotifyId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(spotifyId))
        {
            return BadRequest("spotifyId is required");
        }

        try
        {
            var url = $"https://open.spotify.com/artist/{spotifyId}";
            var metadata = await _pathfinder.FetchByUrlAsync(url, cancellationToken);
            if (metadata is null)
            {
                return Ok(new { available = false });
            }

            return Ok(new { available = true, metadata });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Spotify artist metadata fetch failed");
            return StatusCode(502, new { error = "Spotify artist metadata failed." });
        }
    }
}
