using DeezSpoTag.Core.Models.Qobuz;
using DeezSpoTag.Services.Metadata.Qobuz;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/qobuz")]
[Authorize]
public sealed class QobuzController : ControllerBase
{
    private readonly IQobuzMetadataService _metadataService;

    public QobuzController(IQobuzMetadataService metadataService)
    {
        _metadataService = metadataService;
    }

    [HttpGet("search/artist")]
    public async Task<ActionResult<List<QobuzArtist>>> SearchArtists([FromQuery] string query)
    {
        var results = await _metadataService.SearchArtists(query, HttpContext.RequestAborted);
        return Ok(results);
    }

    [HttpGet("track/isrc/{isrc}")]
    public async Task<ActionResult<QobuzTrack>> GetTrackByISRC(string isrc)
    {
        var track = await _metadataService.FindTrackByISRC(isrc, HttpContext.RequestAborted);
        if (track == null)
        {
            return NotFound();
        }

        return Ok(track);
    }

    [HttpGet("album/upc/{upc}")]
    public async Task<ActionResult<QobuzAlbum>> GetAlbumByUPC(string upc)
    {
        var album = await _metadataService.FindAlbumByUPC(upc, HttpContext.RequestAborted);
        if (album == null)
        {
            return NotFound();
        }

        return Ok(album);
    }
}
