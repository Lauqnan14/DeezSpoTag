using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers;

public class SearchController : Controller
{
    public IActionResult Index(
        string term,
        string type = "track",
        string source = "spotify",
        string mode = "",
        string contextType = "",
        string contextServerType = "",
        string contextLibraryId = "",
        string contextItemId = "",
        string contextTitle = "",
        string contextYear = "")
    {
        ViewData["SearchTerm"] = term ?? "";
        ViewData["SearchType"] = type;
        ViewData["SearchSource"] = source ?? "spotify";
        ViewData["SearchMode"] = mode ?? "";
        ViewData["SoundtrackContextType"] = contextType ?? "";
        ViewData["SoundtrackContextServerType"] = contextServerType ?? "";
        ViewData["SoundtrackContextLibraryId"] = contextLibraryId ?? "";
        ViewData["SoundtrackContextItemId"] = contextItemId ?? "";
        ViewData["SoundtrackContextTitle"] = contextTitle ?? "";
        ViewData["SoundtrackContextYear"] = contextYear ?? "";
        return View();
    }
}
