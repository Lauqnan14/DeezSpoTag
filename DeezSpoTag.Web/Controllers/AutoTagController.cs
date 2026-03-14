using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers;

[Route("[controller]")]
public class AutoTagController : Controller
{
    [HttpGet("")]
    public IActionResult Index()
    {
        return View();
    }

    [HttpGet("QuickTag")]
    public IActionResult QuickTag()
    {
        return View();
    }
}
