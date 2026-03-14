using Microsoft.AspNetCore.Mvc;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Settings;

namespace DeezSpoTag.Web.Controllers;

public class ArtistController : Controller
{
    private readonly ILogger<ArtistController> _logger;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly DeezSpoTag.Services.Download.Shared.DeezSpoTagApp _deezSpoTagApp;

    public ArtistController(
        ILogger<ArtistController> logger,
        DeezSpoTagSettingsService settingsService,
        DeezSpoTag.Services.Download.Shared.DeezSpoTagApp deezSpoTagApp)
    {
        _logger = logger;
        _settingsService = settingsService;
        _deezSpoTagApp = deezSpoTagApp;
    }

    public IActionResult Index(string id, string? source = null)
    {
        ViewData["ArtistId"] = id ?? "";
        ViewData["Source"] = source ?? "deezer";
        return View();
    }

    /// <summary>
    /// Download artist action
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Download(string id, int bitrate = 0)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            if (string.IsNullOrEmpty(id))
            {
                return BadRequest("Artist ID is required");
            }

            var settings = _settingsService.LoadSettings();
            var resolvedBitrate = DownloadSourceOrder.ResolveDeezerBitrate(settings, bitrate);
            var url = $"https://www.deezer.com/artist/{id}";
            var queued = await _deezSpoTagApp.AddToQueueAsync(new[] { url }, resolvedBitrate);
            if (queued.Count == 0)
            {
                return Json(new { success = false, message = "Nothing queued." });
            }

            return Json(new { success = true, queued = queued });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error initiating artist download: ArtistId");
            return Json(new { 
                success = false, 
                message = ex.Message 
            });
        }
    }

}
