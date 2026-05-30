using DeezSpoTag.Services.Download.Qobuz;
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
    private readonly IQobuzDownloadService _qobuzDownloadService;

    public QobuzDlCompatibilityApiController(
        IQobuzMetadataService qobuzMetadataService,
        IQobuzDownloadService qobuzDownloadService)
    {
        _qobuzMetadataService = qobuzMetadataService;
        _qobuzDownloadService = qobuzDownloadService;
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

    [HttpGet("download-music")]
    public async Task<IActionResult> DownloadMusic([FromQuery(Name = "track_id")] int trackId, [FromQuery] string? quality = null, CancellationToken cancellationToken = default)
    {
        if (trackId <= 0)
        {
            return BadRequest(new { success = false, error = "track_id must be greater than zero." });
        }

        try
        {
            var resolved = await _qobuzDownloadService.ResolveStreamUrlByTrackIdAsync(
                trackId,
                quality ?? "6",
                allowQualityFallback: true,
                cancellationToken);
            return Ok(new
            {
                success = true,
                data = new
                {
                    url = resolved.Url,
                    quality = resolved.SelectedQuality
                }
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return BadRequest(new { success = false, error = ex.Message });
        }
    }
}
