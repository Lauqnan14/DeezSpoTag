using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers;

public class ShazamController : Controller
{
    public IActionResult Results()
    {
        return View();
    }
}
