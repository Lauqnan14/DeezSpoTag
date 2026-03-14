using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/spotify/album")]
[Authorize]
public sealed class SpotifyAlbumApiController : ControllerBase
{
    private readonly SpotifyCentralMetadataService _centralMetadata;

    public SpotifyAlbumApiController(SpotifyCentralMetadataService centralMetadata)
    {
        _centralMetadata = centralMetadata;
    }

    [HttpGet("{albumId}/details")]
    public async Task<IActionResult> GetDetails(string albumId, [FromQuery] bool refresh, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(albumId))
        {
            return BadRequest("albumId is required");
        }

        var result = await _centralMetadata.GetAlbumDetailsAsync(albumId, refresh, cancellationToken);
        if (result is null)
        {
            return Ok(new { available = false });
        }

        return Ok(result);
    }
}
