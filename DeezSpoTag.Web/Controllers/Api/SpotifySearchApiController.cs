using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/spotify/search")]
[Authorize]
public sealed class SpotifySearchApiController : SearchApiControllerBase
{
    public SpotifySearchApiController(DeezSpoTagSearchService searchService) : base(searchService) { }

    [HttpGet]
    public async Task<IActionResult> Search(
        [FromQuery] string query,
        [FromQuery] int limit = 25,
        CancellationToken cancellationToken = default)
    {
        var request = new SearchApiRequest(
            Provider: "spotify",
            Query: query,
            Limit: limit,
            Offset: 0);
        return await SearchAsync(request, cancellationToken);
    }

    [HttpGet("type")]
    public async Task<IActionResult> SearchByType(
        [FromQuery] string query,
        [FromQuery] string type,
        [FromQuery] int limit = 25,
        [FromQuery] int offset = 0,
        CancellationToken cancellationToken = default)
    {
        return await SearchByTypeAsync("spotify", query, type, limit, offset, cancellationToken);
    }
}
