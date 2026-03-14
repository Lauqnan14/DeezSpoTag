using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[LocalApiAuthorize]
[Route("api/shazam")]
public sealed class ShazamResolveApiController : ControllerBase
{
    private readonly DeezSpoTag.Integrations.Deezer.DeezerClient _deezerClient;
    private readonly ILogger<ShazamResolveApiController> _logger;

    public ShazamResolveApiController(
        DeezSpoTag.Integrations.Deezer.DeezerClient deezerClient,
        ILogger<ShazamResolveApiController> logger)
    {
        _deezerClient = deezerClient;
        _logger = logger;
    }

    [HttpGet("resolve-deezer")]
    public async Task<IActionResult> ResolveDeezer(
        [FromQuery] string? isrc,
        [FromQuery] string? title,
        [FromQuery] string? artist,
        [FromQuery] string? album,
        [FromQuery] int? durationMs)
    {
        var normalizedIsrc = (isrc ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(normalizedIsrc))
        {
            try
            {
                var track = await _deezerClient.GetTrackByIsrcAsync(normalizedIsrc);
                var deezerId = track?.Id?.ToString();
                if (!string.IsNullOrWhiteSpace(deezerId) && deezerId != "0")
                {
                    return Ok(new
                    {
                        available = true,
                        source = "isrc",
                        deezerId,
                        deezerUrl = $"https://www.deezer.com/track/{deezerId}"
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Shazam Deezer ISRC resolve failed.");
            }
        }

        var titleValue = (title ?? string.Empty).Trim();
        var artistValue = (artist ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(titleValue) || string.IsNullOrWhiteSpace(artistValue))
        {
            return Ok(new { available = false, source = "metadata", message = "Title and artist are required." });
        }

        var albumValue = (album ?? string.Empty).Trim();
        var resolvedId = await _deezerClient.GetTrackIdFromMetadataAsync(
            artistValue,
            titleValue,
            albumValue,
            durationMs);

        if (!string.IsNullOrWhiteSpace(resolvedId) && resolvedId != "0")
        {
            return Ok(new
            {
                available = true,
                source = "metadata",
                deezerId = resolvedId,
                deezerUrl = $"https://www.deezer.com/track/{resolvedId}"
            });
        }

        return Ok(new { available = false, source = "metadata", message = "No Deezer match found." });
    }
}
