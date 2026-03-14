using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[LocalApiAuthorize]
[Route("api/spotify/metadata")]
public class SpotifyMetadataRawApiController : ControllerBase
{
    private readonly SpotifyPathfinderMetadataClient _pathfinder;

    public SpotifyMetadataRawApiController(SpotifyPathfinderMetadataClient pathfinder)
    {
        _pathfinder = pathfinder;
    }

    [HttpGet("raw")]
    public async Task<IActionResult> GetRaw([FromQuery] string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return BadRequest(new { error = "URL is required." });
        }

        var doc = await _pathfinder.FetchRawByUrlAsync(url, cancellationToken);
        if (doc is null)
        {
            return Ok(new { available = false });
        }

        return Content(doc.RootElement.GetRawText(), "application/json");
    }
}
