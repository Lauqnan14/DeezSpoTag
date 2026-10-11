using DeezSpoTag.Integrations.YouTube;
using DeezSpoTag.Services.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Authorize]
[Route("api/youtube/tracklist")]
public sealed class YouTubeTracklistApiController(YouTubeMusicSearchClient client, DeezSpoTagSettingsService settingsService, ILogger<YouTubeTracklistApiController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string id, [FromQuery] string type = "playlist", CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = settingsService.LoadSettings();
            var country = (string.IsNullOrWhiteSpace(settings.YouTubeMusicCountry) ? settings.DeezerCountry : settings.YouTubeMusicCountry).Trim().ToUpperInvariant();
            var tracklist = await client.GetCollectionAsync(id, type.Trim().ToLowerInvariant(), country, settings.DeezerLanguage.Trim(), cancellationToken);
            return Ok(new { available = true, tracklist });
        }
        catch (ArgumentException ex) { return BadRequest(new { available = false, error = ex.Message }); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "YouTube Music collection load failed.");
            return StatusCode(502, new { available = false, error = "YouTube Music collection unavailable." });
        }
    }
}
