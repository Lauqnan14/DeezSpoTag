using DeezSpoTag.Services.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/library/settings/artist-cache")]
[ApiController]
[Authorize]
public class LibraryArtistCacheApiController : ControllerBase
{
    private readonly ArtistPageCacheRepository _artistPageCache;

    public LibraryArtistCacheApiController(ArtistPageCacheRepository artistPageCache)
    {
        _artistPageCache = artistPageCache;
    }

    [HttpPost("clear")]
    public async Task<IActionResult> Clear([FromQuery] string? source, CancellationToken cancellationToken)
    {
        await _artistPageCache.ClearAsync(source?.Trim(), cancellationToken);
        return Ok(new { ok = true, source = source ?? "all" });
    }

    [HttpGet("stats")]
    public async Task<IActionResult> Stats(CancellationToken cancellationToken)
    {
        var stats = await _artistPageCache.TryGetStatsAsync(cancellationToken);
        if (stats == null)
        {
            return Ok(new { ok = false, reason = "library_db_not_configured" });
        }

        return Ok(new { ok = true, stats });
    }
}
