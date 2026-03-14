using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers;

[Route("Spotify")]
public sealed class SpotifyBrowseController : Controller
{
    [HttpGet("Browse")]
    public IActionResult Index(string? categoryId, string? uri, string? title, string? section)
    {
        ViewData["CategoryId"] = categoryId ?? string.Empty;
        ViewData["CategoryUri"] = uri ?? string.Empty;
        ViewData["CategoryTitle"] = title ?? string.Empty;
        ViewData["BrowseSection"] = section ?? string.Empty;
        return View();
    }
}
