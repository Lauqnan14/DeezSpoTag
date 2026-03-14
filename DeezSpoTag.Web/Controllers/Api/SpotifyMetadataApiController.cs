using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[LocalApiAuthorize]
[Route("api/spotify/metadata")]
public class SpotifyMetadataApiController : ControllerBase
{
    private readonly SpotifyMetadataService _metadataService;

    public SpotifyMetadataApiController(SpotifyMetadataService metadataService)
    {
        _metadataService = metadataService;
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return BadRequest(new { error = "URL is required." });
        }

        var metadata = await _metadataService.FetchByUrlAsync(url, cancellationToken);
        if (metadata == null)
        {
            return Ok(new { available = false });
        }

        return Ok(new
        {
            available = true,
            metadata
        });
    }
}
