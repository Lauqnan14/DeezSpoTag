using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers;

public class SearchController : Controller
{
    public IActionResult Index(string term, string type = "track")
    {
        ViewData["SearchTerm"] = term ?? "";
        ViewData["SearchType"] = type;
        return View();
    }
}