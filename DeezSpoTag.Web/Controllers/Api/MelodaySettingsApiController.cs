using DeezSpoTag.Web.Services;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Library.Dj;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/meloday/settings")]
[ApiController]
[Authorize]
[Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryToken]
public sealed class MelodaySettingsApiController : ControllerBase
{
    private readonly MelodaySettingsStore _store;
    private readonly MelodayOptions _defaults;
    private readonly LibraryRepository _libraryRepository;
    private readonly IDjStrategyCatalog _strategyCatalog;
    private Dictionary<long, string> _libraryNamesById = new();

    public MelodaySettingsApiController(
        MelodaySettingsStore store,
        IOptions<MelodayOptions> defaults,
        LibraryRepository libraryRepository,
        IDjStrategyCatalog strategyCatalog)
    {
        _store = store;
        _defaults = defaults.Value;
        _libraryRepository = libraryRepository;
        _strategyCatalog = strategyCatalog;
    }

    /// <summary>
    /// The registered DJs, for the Target Libraries DJ selector.
    ///
    /// <para>Served from the catalogue so the option list cannot drift from what can
    /// actually be selected. Adding a DJ makes it appear here with no UI change.</para>
    /// </summary>
    [HttpGet("djs")]
    public IActionResult Djs()
    {
        return Ok(_strategyCatalog.GetAll()
            .Select(static descriptor => new
            {
                id = descriptor.Id,
                displayName = descriptor.DisplayName,
                description = descriptor.Description,
                requirements = descriptor.Requirements,
            })
            .ToList());
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
    public async Task<IActionResult> Update([FromBody] MelodayOptions request, CancellationToken cancellationToken)
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

        var slots = MelodayScheduleSlots.Normalize(request.Slots);
        var libraries = MelodayScheduleSlots.NormalizeLibraries(request.Libraries);

        // A specific DJ that is not registered is rejected rather than quietly replaced
        // with Random. The selector is populated from the catalogue, so the UI cannot
        // produce this; only a hand-edited or stale payload can, and silently accepting
        // it would leave the dropdown showing something that is not what will run.
        var unknownDjs = libraries
            .Select(static library => library.DjSelection)
            .Where(selection => !MelodayDjSelections.IsRandom(selection) && !MelodayDjSelections.IsNone(selection))
            .Where(selection => _strategyCatalog.GetById(selection) is null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unknownDjs.Count > 0)
        {
            return BadRequest($"Unknown Meloday DJ selection: {string.Join(", ", unknownDjs)}.");
        }

        if (request.Enabled)
        {
            _libraryNamesById = await LoadLibraryNamesAsync(cancellationToken);
            var targetedLibraries = libraries.Where(static library => library.IsTargeted).ToList();
            if (targetedLibraries.Count == 0)
            {
                return BadRequest("Select at least one time slot for at least one library.");
            }

            foreach (var library in targetedLibraries
                .Where(library => library.SlotIds.Count > library.MaxActivePlaylists))
            {
                return BadRequest(
                    $"{ResolveLibraryDisplayName(library.LibraryId)}: {library.SlotIds.Count} of {library.MaxActivePlaylists} playlist slots selected — raise the maximum or deselect slots.");
            }
        }

        var cleaned = new MelodayOptions
        {
            Enabled = request.Enabled,
            BaseUrl = string.IsNullOrWhiteSpace(request.BaseUrl) ? _defaults.BaseUrl : request.BaseUrl.Trim(),
            ExcludePlayedDays = MelodayClamp.AllowZeroOrDefault(request.ExcludePlayedDays, _defaults.ExcludePlayedDays, 0, 365),
            HistoryLookbackDays = MelodayClamp.PositiveOrDefault(request.HistoryLookbackDays, _defaults.HistoryLookbackDays, 1, 365),
            MaxTracks = MelodayClamp.PositiveOrDefault(request.MaxTracks, _defaults.MaxTracks, 10, 500),
            HistoricalRatio = MelodayClamp.AllowZeroOrDefault(request.HistoricalRatio, _defaults.HistoricalRatio, 0d, 1d),
            SonicSimilarLimit = MelodayClamp.PositiveOrDefault(request.SonicSimilarLimit, _defaults.SonicSimilarLimit, 1, 50),
            SonicSimilarityDistance = MelodayClamp.PositiveOrDefault(request.SonicSimilarityDistance, _defaults.SonicSimilarityDistance, 0.05d, 1d),
            UpdateIntervalMinutes = MelodayClamp.PositiveOrDefault(request.UpdateIntervalMinutes, _defaults.UpdateIntervalMinutes, 5, 1440),
            Mode = MelodayModes.Sonic,
            MoodMapPath = _defaults.MoodMapPath,
            Slots = slots,
            Libraries = libraries,
            MissedRunGraceMinutes = MelodayClamp.PositiveOrDefault(request.MissedRunGraceMinutes, _defaults.MissedRunGraceMinutes, 0, 720),
            TargetServers = targetServers,
            // Kept in sync for settings files written by older builds; the scheduler reads Libraries.
            TargetLibraryIds = libraries.Where(static library => library.IsTargeted).Select(static library => library.LibraryId).ToList()
        };

        var saved = await _store.SaveAsync(cleaned);
        return Ok(saved);
    }

    private string ResolveLibraryDisplayName(long libraryId)
    {
        var name = _libraryNamesById.GetValueOrDefault(libraryId);
        return string.IsNullOrWhiteSpace(name) ? $"Library {libraryId}" : name;
    }

    private async Task<Dictionary<long, string>> LoadLibraryNamesAsync(CancellationToken cancellationToken)
    {
        if (!_libraryRepository.IsConfigured)
        {
            return new Dictionary<long, string>();
        }

        var names = new Dictionary<long, string>();
        foreach (var folder in (await _libraryRepository.GetConfiguredEnabledMusicFoldersAsync(cancellationToken))
            .Where(folder => folder.LibraryId.HasValue && !string.IsNullOrWhiteSpace(folder.LibraryName)))
        {
            names.TryAdd(folder.LibraryId!.Value, folder.LibraryName!);
        }

        return names;
    }
}
