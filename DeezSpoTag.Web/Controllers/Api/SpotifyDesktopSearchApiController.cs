using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/spotify/search-desktop")]
[Authorize]
public class SpotifyDesktopSearchApiController : ControllerBase
{
    private readonly SpotifyDesktopSearchService _searchService;

    public SpotifyDesktopSearchApiController(SpotifyDesktopSearchService searchService)
    {
        _searchService = searchService;
    }

    [HttpGet]
    public async Task<IActionResult> Search(
        [FromQuery] string query,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return BadRequest(new { error = "Query is required." });
        }
        if (IsUrl(query))
        {
            return BadRequest(new { error = "Search expects text, not a URL." });
        }

        var result = await _searchService.SearchAsync(query, Math.Clamp(limit, 1, 50), cancellationToken);
        if (result == null)
        {
            return Ok(new { available = false });
        }

        return Ok(new
        {
            available = true,
            tracks = result.Tracks,
            albums = result.Albums,
            artists = result.Artists,
            playlists = result.Playlists
        });
    }

    private static bool IsUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}

