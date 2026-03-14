using DeezSpoTag.Services.Settings;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers
{
    public class HomeController : Controller
    {
        private readonly ISettingsService _settingsService;

        public HomeController(ISettingsService settingsService)
        {
            _settingsService = settingsService;
        }

        public IActionResult Index()
        {
            var settings = _settingsService.LoadSettings();
            ViewData["SpotifyHomeFeedAutoRefreshEnabled"] = settings.SpotifyHomeFeedAutoRefreshEnabled;
            ViewData["SpotifyHomeFeedAutoRefreshHours"] = settings.SpotifyHomeFeedAutoRefreshHours;
            ViewData["SpotifyHomeFeedCacheEnabled"] = settings.SpotifyHomeFeedCacheEnabled;
            return View();
        }

        public IActionResult Error()
        {
            return View();
        }

        public IActionResult Connect()
        {
            return View();
        }
    }
}
