using DeezSpoTag.Services.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/quicktag")]
[Authorize]
public sealed class QuickTagLibraryApiController : ControllerBase
{
    private readonly LibraryRepository _repository;

    public QuickTagLibraryApiController(LibraryRepository repository)
    {
        _repository = repository;
    }

    [HttpGet("track/{id:long}")]
    public async Task<IActionResult> GetTrack(long id, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return BadRequest(new { error = "Library DB not configured." });
        }

        var info = await _repository.GetTrackAudioInfoAsync(id, cancellationToken);
        if (info is null || string.IsNullOrWhiteSpace(info.FilePath))
        {
            return NotFound(new { error = "Track file unavailable." });
        }

        return Ok(new
        {
            trackId = info.TrackId,
            title = info.Title,
            artist = info.ArtistName,
            album = info.AlbumTitle,
            durationMs = info.DurationMs,
            filePath = info.FilePath,
            directory = Path.GetDirectoryName(info.FilePath) ?? string.Empty,
            fileName = Path.GetFileName(info.FilePath)
        });
    }
}
