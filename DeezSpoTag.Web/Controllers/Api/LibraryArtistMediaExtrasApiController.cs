using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/library/artists")]
[ApiController]
[Authorize]
[AutoValidateAntiforgeryToken]
public sealed class LibraryArtistMediaExtrasApiController : ControllerBase
{
    private readonly ArtistMediaExtrasCacheService _extrasCache;

    public LibraryArtistMediaExtrasApiController(ArtistMediaExtrasCacheService extrasCache)
    {
        _extrasCache = extrasCache;
    }

    [HttpGet("{id:long}/media-extras")]
    public async Task<IActionResult> GetMediaExtras(
        long id,
        [FromQuery] string? provider,
        CancellationToken cancellationToken)
    {
        if (id <= 0)
        {
            return BadRequest("ArtistId is required.");
        }

        var normalized = string.IsNullOrWhiteSpace(provider) ? "apple" : provider.Trim().ToLowerInvariant();
        if (normalized is not ("apple" or "tidal"))
        {
            return BadRequest("provider must be apple or tidal.");
        }

        var extras = await _extrasCache.TryGetAsync(id, normalized, cancellationToken);
        if (extras is null)
        {
            return Ok(new
            {
                available = false,
                appleId = (string?)null,
                tidalId = (string?)null,
                atmos = Array.Empty<object>(),
                videos = Array.Empty<object>(),
                hasMoreVideos = false
            });
        }

        return Ok(new
        {
            available = true,
            appleId = extras.AppleId,
            tidalId = extras.TidalId,
            atmos = extras.Atmos,
            videos = extras.Videos,
            hasMoreVideos = extras.HasMoreVideos
        });
    }
}
