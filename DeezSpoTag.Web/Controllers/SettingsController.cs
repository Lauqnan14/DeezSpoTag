using Microsoft.AspNetCore.Mvc;
using DeezSpoTag.Services.Settings;

namespace DeezSpoTag.Web.Controllers
{
    /// <summary>
    /// Settings MVC controller for rendering settings views
    /// Note: API functionality is handled by DeezSpoTag.API.Controllers and api/settings endpoints
    /// </summary>
    public class SettingsController : Controller
    {
        private readonly ILogger<SettingsController> _logger;
        private readonly DeezSpoTagSettingsService _settingsService;

        public SettingsController(ILogger<SettingsController> logger, DeezSpoTagSettingsService settingsService)
        {
            _logger = logger;
            _settingsService = settingsService;
        }

        /// <summary>
        /// GET: /Settings
        /// </summary>
        public IActionResult Index()
        {
            try
            {
                _logger.LogDebug("Settings page requested");
                
                // Load settings to pass to the view
                var settings = _settingsService.LoadSettings();
                ViewData["Settings"] = settings;
                
                return View();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error loading settings page");
                ViewData["Error"] = "Failed to load settings";
                return View();
            }
        }

        /// <summary>
        /// GET: /Settings/Settings (alternative route)
        /// </summary>
        public IActionResult Settings()
        {
            return RedirectToAction("Index");
        }
    }
}
