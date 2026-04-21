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
            ExcludePlayedDays = MelodayClamp.AllowZeroOrDefault(request.ExcludePlayedDays, _defaults.ExcludePlayedDays, 0, 365),
            HistoryLookbackDays = MelodayClamp.PositiveOrDefault(request.HistoryLookbackDays, _defaults.HistoryLookbackDays, 1, 365),
            MaxTracks = MelodayClamp.PositiveOrDefault(request.MaxTracks, _defaults.MaxTracks, 10, 500),
            HistoricalRatio = MelodayClamp.AllowZeroOrDefault(request.HistoricalRatio, _defaults.HistoricalRatio, 0d, 1d),
            SonicSimilarLimit = MelodayClamp.PositiveOrDefault(request.SonicSimilarLimit, _defaults.SonicSimilarLimit, 1, 50),
            SonicSimilarityDistance = MelodayClamp.PositiveOrDefault(request.SonicSimilarityDistance, _defaults.SonicSimilarityDistance, 0.05d, 1d),
            UpdateIntervalMinutes = MelodayClamp.PositiveOrDefault(request.UpdateIntervalMinutes, _defaults.UpdateIntervalMinutes, 5, 1440),
            MoodMapPath = _defaults.MoodMapPath,
            CoversPath = _defaults.CoversPath,
            FontsPath = _defaults.FontsPath,
            MainFontFile = _defaults.MainFontFile,
            BrandFontFile = _defaults.BrandFontFile
        };

        var saved = await _store.SaveAsync(cleaned);
        return Ok(saved);
    }
}
