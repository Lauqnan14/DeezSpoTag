using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers;

public class SearchController : Controller
{
    public IActionResult Index([FromQuery] SearchQuery query)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        ViewData["SearchTerm"] = query.Term ?? string.Empty;
        ViewData["SearchType"] = query.Type;
        ViewData["SearchSource"] = query.Source ?? "spotify";
        ViewData["SearchMode"] = query.Mode ?? string.Empty;
        ViewData["SoundtrackContextType"] = query.ContextType ?? string.Empty;
        ViewData["SoundtrackContextServerType"] = query.ContextServerType ?? string.Empty;
        ViewData["SoundtrackContextLibraryId"] = query.ContextLibraryId ?? string.Empty;
        ViewData["SoundtrackContextItemId"] = query.ContextItemId ?? string.Empty;
        ViewData["SoundtrackContextTitle"] = query.ContextTitle ?? string.Empty;
        ViewData["SoundtrackContextYear"] = query.ContextYear ?? string.Empty;
        return View();
    }

    public sealed class SearchQuery
    {
        public string? Term { get; init; }
        public string Type { get; init; } = "track";
        public string? Source { get; init; } = "spotify";
        public string? Mode { get; init; }
        public string? ContextType { get; init; }
        public string? ContextServerType { get; init; }
        public string? ContextLibraryId { get; init; }
        public string? ContextItemId { get; init; }
        public string? ContextTitle { get; init; }
        public string? ContextYear { get; init; }
    }
}
