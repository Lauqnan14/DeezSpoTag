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
        var cleaned = new MelodayOptions
        {
            Enabled = request.Enabled,
            LibraryName = string.IsNullOrWhiteSpace(request.LibraryName) ? null : request.LibraryName.Trim(),
            PlaylistPrefix = string.IsNullOrWhiteSpace(request.PlaylistPrefix) ? _defaults.PlaylistPrefix : request.PlaylistPrefix.Trim(),
            BaseUrl = string.IsNullOrWhiteSpace(request.BaseUrl) ? _defaults.BaseUrl : request.BaseUrl.Trim(),
            ExcludePlayedDays = request.ExcludePlayedDays <= 0 ? _defaults.ExcludePlayedDays : request.ExcludePlayedDays,
            HistoryLookbackDays = request.HistoryLookbackDays <= 0 ? _defaults.HistoryLookbackDays : request.HistoryLookbackDays,
            MaxTracks = request.MaxTracks <= 0 ? _defaults.MaxTracks : request.MaxTracks,
            HistoricalRatio = request.HistoricalRatio <= 0 ? _defaults.HistoricalRatio : request.HistoricalRatio,
            SonicSimilarLimit = request.SonicSimilarLimit <= 0 ? _defaults.SonicSimilarLimit : request.SonicSimilarLimit,
            SonicSimilarityDistance = request.SonicSimilarityDistance <= 0 ? _defaults.SonicSimilarityDistance : request.SonicSimilarityDistance,
            UpdateIntervalMinutes = request.UpdateIntervalMinutes <= 0 ? _defaults.UpdateIntervalMinutes : request.UpdateIntervalMinutes,
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
