using Microsoft.AspNetCore.Mvc;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Services;

namespace DeezSpoTag.Web.Controllers;

public class ArtistController : Controller
{
    private readonly ILogger<ArtistController> _logger;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly DownloadIntentService _intentService;

    public ArtistController(
        ILogger<ArtistController> logger,
        DeezSpoTagSettingsService settingsService,
        DownloadIntentService intentService)
    {
        _logger = logger;
        _settingsService = settingsService;
        _intentService = intentService;
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
            var intent = new DownloadIntent
            {
                SourceService = "deezer",
                SourceUrl = url,
                PreferredEngine = "deezer",
                Quality = resolvedBitrate.ToString(),
                ContentType = "music"
            };
            var result = await _intentService.EnqueueAsync(intent, HttpContext.RequestAborted);
            var queued = result.Queued
                .Select(static uuid => new Dictionary<string, object> { ["uuid"] = uuid })
                .ToList();
            return DeezerQueueActionResultHelper.FromQueued(this, queued);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DeezerQueueActionResultHelper.FromError(this, _logger, ex, "Error initiating artist download: ArtistId");
        }
    }

}
