using Microsoft.AspNetCore.Mvc;
using DeezSpoTag.Web.Services;
using Microsoft.Net.Http.Headers;

namespace DeezSpoTag.Web.Controllers;

[Route("api/playlist/cover")]
public class PlaylistCoverController : Controller
{
    private readonly PlaylistCoverService _coverService;

    public PlaylistCoverController(PlaylistCoverService coverService, ILogger<PlaylistCoverController> logger)
    {
        _coverService = coverService;
        _ = logger;
    }

    [HttpGet("background")]
    public async Task<IActionResult> GetPlaylistBackground([FromQuery] string? url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return BadRequest("Missing url");
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return BadRequest("Invalid url");
        }

        var result = await _coverService.GetBackgroundAsync(url, cancellationToken);
        if (result == null || !System.IO.File.Exists(result.FilePath))
        {
            return NotFound();
        }

        var fileInfo = new FileInfo(result.FilePath);
        var typedHeaders = Response.GetTypedHeaders();
        typedHeaders.CacheControl = new CacheControlHeaderValue
        {
            Public = true,
            MaxAge = TimeSpan.FromDays(1)
        };
        typedHeaders.ETag = new EntityTagHeaderValue($"\"{fileInfo.Length}-{fileInfo.LastWriteTimeUtc.Ticks}\"");

        return PhysicalFile(result.FilePath, result.ContentType);
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetPlaylistCoverStatus(CancellationToken cancellationToken)
    {
        var status = await PlaylistCoverService.GetPipelineStatusAsync(cancellationToken);
        return Ok(new
        {
            available = status.Available,
            toolFound = status.ToolFound,
            modelFound = status.ModelFound,
            fallbackActive = status.FallbackActive
        });
    }
}
