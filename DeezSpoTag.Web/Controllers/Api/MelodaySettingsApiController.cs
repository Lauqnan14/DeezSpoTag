using DeezSpoTag.Web.Services;
using DeezSpoTag.Services.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/meloday/settings")]
[ApiController]
[Authorize]
[Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryToken]
[Microsoft.AspNetCore.Mvc.IgnoreAntiforgeryToken]
public sealed class MelodaySettingsApiController : ControllerBase
{
    private readonly MelodaySettingsStore _store;
    private readonly MelodayOptions _defaults;
    private readonly LibraryRepository _libraryRepository;

    public MelodaySettingsApiController(
        MelodaySettingsStore store,
        IOptions<MelodayOptions> defaults,
        LibraryRepository libraryRepository)
    {
        _store = store;
        _defaults = defaults.Value;
        _libraryRepository = libraryRepository;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var settings = await _store.LoadAsync(_defaults);
        return Ok(settings);
    }

    [HttpGet("libraries")]
    public async Task<IActionResult> Libraries(CancellationToken cancellationToken)
    {
        if (!_libraryRepository.IsConfigured)
        {
            return Ok(Array.Empty<object>());
        }

        var folders = (await _libraryRepository.GetConfiguredEnabledMusicFoldersAsync(cancellationToken))
            .Where(static folder => folder.LibraryId.HasValue && !string.IsNullOrWhiteSpace(folder.LibraryName))
            .GroupBy(static folder => folder.LibraryId!.Value)
            .OrderBy(static group => group.First().LibraryName, StringComparer.OrdinalIgnoreCase);
        var libraries = new List<object>();
        foreach (var group in folders)
        {
            var trackIds = new HashSet<long>();
            foreach (var folder in group)
            {
                foreach (var trackId in await _libraryRepository.GetTrackIdsForLibraryScopeAsync(
                    group.Key,
                    folder.Id,
                    cancellationToken))
                {
                    trackIds.Add(trackId);
                }
            }
            if (trackIds.Count == 0)
            {
                continue;
            }

            var primaryFolder = group.First();
            libraries.Add(new
            {
                id = group.Key,
                name = primaryFolder.LibraryName,
                trackCount = trackIds.Count
            });
        }

        return Ok(libraries);
    }

    [HttpPost]
    public async Task<IActionResult> Update([FromBody] MelodayOptions request)
    {
        if (request is null)
        {
            return BadRequest("Settings payload is required.");
        }

        var targetServers = MelodayTargetServers.Normalize(request.TargetServers, defaultToAll: false);
        if (request.Enabled && targetServers.Count == 0)
        {
            return BadRequest("Select at least one Meloday target server.");
        }

        var targetLibraryIds = MelodayService.NormalizeTargetLibraryIds(request.TargetLibraryIds);
        if (request.Enabled && targetLibraryIds.Count == 0)
        {
            return BadRequest("Select at least one Meloday target library.");
        }

        var cleaned = new MelodayOptions
        {
            Enabled = request.Enabled,
            PlaylistPrefix = string.IsNullOrWhiteSpace(request.PlaylistPrefix) ? _defaults.PlaylistPrefix : request.PlaylistPrefix.Trim(),
            BaseUrl = string.IsNullOrWhiteSpace(request.BaseUrl) ? _defaults.BaseUrl : request.BaseUrl.Trim(),
            ExcludePlayedDays = MelodayClamp.AllowZeroOrDefault(request.ExcludePlayedDays, _defaults.ExcludePlayedDays, 0, 365),
            HistoryLookbackDays = MelodayClamp.PositiveOrDefault(request.HistoryLookbackDays, _defaults.HistoryLookbackDays, 1, 365),
            MaxTracks = MelodayClamp.PositiveOrDefault(request.MaxTracks, _defaults.MaxTracks, 10, 500),
            HistoricalRatio = MelodayClamp.AllowZeroOrDefault(request.HistoricalRatio, _defaults.HistoricalRatio, 0d, 1d),
            SonicSimilarLimit = MelodayClamp.PositiveOrDefault(request.SonicSimilarLimit, _defaults.SonicSimilarLimit, 1, 50),
            SonicSimilarityDistance = MelodayClamp.PositiveOrDefault(request.SonicSimilarityDistance, _defaults.SonicSimilarityDistance, 0.05d, 1d),
            UpdateIntervalMinutes = MelodayClamp.PositiveOrDefault(request.UpdateIntervalMinutes, _defaults.UpdateIntervalMinutes, 5, 1440),
            Mode = MelodayModes.Normalize(request.Mode),
            MoodMapPath = _defaults.MoodMapPath,
            TargetServers = targetServers,
            TargetLibraryIds = targetLibraryIds
        };

        var saved = await _store.SaveAsync(cleaned);
        return Ok(saved);
    }
}
