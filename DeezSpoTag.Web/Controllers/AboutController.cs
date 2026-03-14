using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers;

public class AboutController : Controller
{
    public IActionResult Index()
    {
        return View();
    }
}
