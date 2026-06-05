using Microsoft.AspNetCore.Mvc;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Services;

namespace DeezSpoTag.Web.Controllers;
[Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryToken]

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
            var preferredEngine = ResolveManualPreferredEngine(settings);
            var quality = ResolveManualPreferredQuality(settings, preferredEngine, bitrate);
            var url = $"https://www.deezer.com/artist/{id}";
            var intent = new DownloadIntent
            {
                SourceService = "deezer",
                SourceUrl = url,
                PreferredEngine = preferredEngine,
                Quality = quality,
                ContentType = "music"
            };
            var result = await _intentService.EnqueueManualAsync(intent, CancellationToken.None);
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

    private static string ResolveManualPreferredEngine(DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings)
    {
        if (settings.DownloadEngineOrder?.Enabled == true)
        {
            return "auto";
        }

        var service = (settings.Service ?? string.Empty).Trim().ToLowerInvariant();
        return service is "auto" or "amazon" or "apple" or "deezer" or "qobuz" or "tidal"
            ? service
            : "auto";
    }

    private static string ResolveManualPreferredQuality(
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        string preferredEngine,
        int requestedBitrate)
    {
        return preferredEngine switch
        {
            "deezer" => DownloadSourceOrder.ResolveDeezerBitrate(settings, requestedBitrate).ToString(),
            "qobuz" => string.IsNullOrWhiteSpace(settings.QobuzQuality) ? string.Empty : settings.QobuzQuality,
            "tidal" => string.IsNullOrWhiteSpace(settings.TidalQuality) ? string.Empty : settings.TidalQuality,
            "apple" => settings.AppleMusic?.PreferredAudioProfile ?? string.Empty,
            _ => string.Empty
        };
    }

}
