using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DeezSpoTag.Web.Controllers;

[Route("Library")]
[DisableRateLimiting]
public class LibraryController : Controller
{
    [HttpGet("")]
    public IActionResult Index()
    {
        return View();
    }

    [HttpGet("Artist/{id:long}")]
    public IActionResult Artist(long id)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        ViewData["ArtistId"] = id;
        return View();
    }

    [HttpGet("Albums/{id:long}")]
    public IActionResult Albums(long id)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        ViewData["ArtistId"] = id;
        return View("Artist");
    }

    [HttpGet("Album/{id:long}")]
    public IActionResult Album(long id)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        ViewData["AlbumId"] = id;
        return View();
    }

    [HttpGet("Track/{id:long}/Analysis")]
    public IActionResult TrackAnalysis(long id)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        ViewData["TrackId"] = id;
        return View();
    }
}
