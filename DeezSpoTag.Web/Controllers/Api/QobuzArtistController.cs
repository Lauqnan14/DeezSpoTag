using DeezSpoTag.Core.Models.Qobuz;
using DeezSpoTag.Services.Metadata.Qobuz;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/qobuz")]
[Authorize]
public sealed class QobuzArtistController : ControllerBase
{
    private readonly QobuzArtistService _artistService;

    public QobuzArtistController(QobuzArtistService artistService)
    {
        _artistService = artistService;
    }

    [HttpGet("artist/{artistId:int}/discography")]
    public async Task<ActionResult<QobuzArtist>> GetArtistDiscography(int artistId, [FromQuery] string store = "us-en")
    {
        var artist = await _artistService.GetArtistWithDiscographyAsync(artistId, store, HttpContext.RequestAborted);
        if (artist == null)
        {
            return NotFound();
        }

        return Ok(artist);
    }
}
