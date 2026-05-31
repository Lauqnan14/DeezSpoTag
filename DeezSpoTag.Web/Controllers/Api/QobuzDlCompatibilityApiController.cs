using DeezSpoTag.Services.Metadata.Qobuz;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[AllowAnonymous]
[Route("api")]
public sealed class QobuzDlCompatibilityApiController : ControllerBase
{
    private readonly IQobuzMetadataService _qobuzMetadataService;

    public QobuzDlCompatibilityApiController(
        IQobuzMetadataService qobuzMetadataService)
    {
        _qobuzMetadataService = qobuzMetadataService;
    }

    [HttpGet("get-music")]
    public async Task<IActionResult> GetMusic([FromQuery] string q, [FromQuery] int offset = 0, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return BadRequest(new { success = false, error = "Query is required." });
        }

        var tracks = await _qobuzMetadataService.SearchTracks(q, cancellationToken);
        var paged = tracks
            .Skip(Math.Max(0, offset))
            .Take(10)
            .Select(track => new
            {
                id = track.Id,
                title = track.Title,
                isrc = track.ISRC,
                duration = track.Duration,
                performer = track.Performer == null
                    ? null
                    : new
                    {
                        id = track.Performer.Id,
                        name = track.Performer.Name
                    },
                album = track.Album == null
                    ? null
                    : new
                    {
                        id = track.Album.QobuzId,
                        title = track.Album.Title
                    }
            })
            .ToList();

        return Ok(new
        {
            success = true,
            data = new
            {
                tracks = new
                {
                    items = paged,
                    total = tracks.Count,
                    offset = Math.Max(0, offset),
                    limit = 10
                }
            }
        });
    }

}
