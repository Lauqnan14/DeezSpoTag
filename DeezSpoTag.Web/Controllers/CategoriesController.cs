using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers;

[Route("Categories")]
public sealed class CategoriesController : Controller
{
    [HttpGet("")]
    public IActionResult Index()
    {
        return View();
    }
}
