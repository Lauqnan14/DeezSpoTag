using Microsoft.AspNetCore.Mvc;
using DeezSpoTag.API.Services;

namespace DeezSpoTag.API.Controllers;

/// <summary>
/// Search controller (ported from deezspotag search endpoints)
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class SearchController : ControllerBase
{
    private readonly DeezSpoTagSearchProxyService _searchProxy;

    public SearchController(DeezSpoTagSearchProxyService searchProxy)
    {
        _searchProxy = searchProxy;
    }

    /// <summary>
    /// Main search endpoint (equivalent to deezspotag /mainSearch)
    /// </summary>
    [HttpGet("mainSearch")]
    public async Task<IActionResult> MainSearch([FromQuery] string term)
    {
        var (status, body) = await _searchProxy.SearchAsync("deezer", term, 20, 0, null, HttpContext.RequestAborted);
        return StatusCode((int)status, body);
    }

    /// <summary>
    /// Specific search endpoint (equivalent to deezspotag /search)
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] string term, [FromQuery] string type = "",
        [FromQuery] int start = 0, [FromQuery] int nb = 25)
    {
        var (status, body) = await _searchProxy.SearchByTypeAsync("deezer", term, type, nb, start, HttpContext.RequestAborted);
        return StatusCode((int)status, body);
    }
}
