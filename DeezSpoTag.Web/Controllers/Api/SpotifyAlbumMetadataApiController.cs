using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/spotify/album")]
[Authorize]
public sealed class SpotifyAlbumMetadataApiController : ControllerBase
{
    private readonly SpotifyPathfinderMetadataClient _pathfinder;

    public SpotifyAlbumMetadataApiController(SpotifyPathfinderMetadataClient pathfinder)
    {
        _pathfinder = pathfinder;
    }

    [HttpGet("{albumId}/metadata")]
    public async Task<IActionResult> GetMetadata(string albumId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(albumId))
        {
            return BadRequest("albumId is required");
        }

        var url = $"https://open.spotify.com/album/{albumId}";
        var metadata = await _pathfinder.FetchByUrlAsync(url, cancellationToken);
        if (metadata is null)
        {
            return Ok(new { available = false });
        }

        return Ok(new { available = true, metadata });
    }
}
