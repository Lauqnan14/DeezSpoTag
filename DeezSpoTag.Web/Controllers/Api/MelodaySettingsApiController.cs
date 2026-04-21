using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/meloday/settings")]
[ApiController]
[Authorize]
public sealed class MelodaySettingsApiController : ControllerBase
{
    private readonly MelodaySettingsStore _store;
    private readonly MelodayOptions _defaults;

    public MelodaySettingsApiController(MelodaySettingsStore store, IOptions<MelodayOptions> defaults)
    {
        _store = store;
        _defaults = defaults.Value;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var settings = await _store.LoadAsync(_defaults);
        return Ok(settings);
    }

    [HttpPost]
    public async Task<IActionResult> Update([FromBody] MelodayOptions request)
    {
        if (request is null)
        {
            return BadRequest("Settings payload is required.");
        }

        var cleaned = new MelodayOptions
        {
            Enabled = request.Enabled,
            LibraryName = string.IsNullOrWhiteSpace(request.LibraryName) ? null : request.LibraryName.Trim(),
            PlaylistPrefix = string.IsNullOrWhiteSpace(request.PlaylistPrefix) ? _defaults.PlaylistPrefix : request.PlaylistPrefix.Trim(),
            BaseUrl = string.IsNullOrWhiteSpace(request.BaseUrl) ? _defaults.BaseUrl : request.BaseUrl.Trim(),
            ExcludePlayedDays = ClampAllowZeroOrDefault(request.ExcludePlayedDays, _defaults.ExcludePlayedDays, 0, 365),
            HistoryLookbackDays = ClampPositiveOrDefault(request.HistoryLookbackDays, _defaults.HistoryLookbackDays, 1, 365),
            MaxTracks = ClampPositiveOrDefault(request.MaxTracks, _defaults.MaxTracks, 10, 500),
            HistoricalRatio = ClampAllowZeroOrDefault(request.HistoricalRatio, _defaults.HistoricalRatio, 0d, 1d),
            SonicSimilarLimit = ClampPositiveOrDefault(request.SonicSimilarLimit, _defaults.SonicSimilarLimit, 1, 50),
            SonicSimilarityDistance = ClampPositiveOrDefault(request.SonicSimilarityDistance, _defaults.SonicSimilarityDistance, 0.05d, 1d),
            UpdateIntervalMinutes = ClampPositiveOrDefault(request.UpdateIntervalMinutes, _defaults.UpdateIntervalMinutes, 5, 1440),
            MoodMapPath = _defaults.MoodMapPath,
            CoversPath = _defaults.CoversPath,
            FontsPath = _defaults.FontsPath,
            MainFontFile = _defaults.MainFontFile,
            BrandFontFile = _defaults.BrandFontFile
        };

        var saved = await _store.SaveAsync(cleaned);
        return Ok(saved);
    }

    private static int ClampPositiveOrDefault(int value, int fallback, int min, int max)
    {
        var effective = value <= 0 ? fallback : value;
        return Math.Clamp(effective, min, max);
    }

    private static int ClampAllowZeroOrDefault(int value, int fallback, int min, int max)
    {
        var effective = value < 0 ? fallback : value;
        return Math.Clamp(effective, min, max);
    }

    private static double ClampPositiveOrDefault(double value, double fallback, double min, double max)
    {
        var effective = value <= 0 ? fallback : value;
        return Math.Clamp(effective, min, max);
    }

    private static double ClampAllowZeroOrDefault(double value, double fallback, double min, double max)
    {
        var effective = value < 0 ? fallback : value;
        return Math.Clamp(effective, min, max);
    }
}
