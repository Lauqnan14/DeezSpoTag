using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DeezSpoTag.Web.Controllers;

[Authorize]
[DisableRateLimiting]
[Route("GenreIntelligence")]
public sealed class GenreIntelligenceController : Controller
{
    [HttpGet("")]
    public IActionResult Index()
        => View();
}
