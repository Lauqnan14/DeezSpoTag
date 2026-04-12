using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using DeezSpoTag.Services.Library;
using System.Globalization;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/autotag/defaults")]
[ApiController]
[Authorize]
public sealed class AutoTagDefaultsApiController : ControllerBase
{
    private readonly AutoTagDefaultsStore _store;
    private readonly AutoTagProfileResolutionService _profileResolutionService;
    private readonly TaggingProfileService _profileService;
    private readonly LibraryRepository _libraryRepository;
    private readonly AutoTagService _autoTagService;
    private readonly ILogger<AutoTagDefaultsApiController> _logger;

    public AutoTagDefaultsApiController(
        AutoTagDefaultsStore store,
        AutoTagProfileResolutionService profileResolutionService,
        TaggingProfileService profileService,
        LibraryRepository libraryRepository,
        AutoTagService autoTagService,
        ILogger<AutoTagDefaultsApiController> logger)
    {
        _store = store;
        _profileResolutionService = profileResolutionService;
        _profileService = profileService;
        _libraryRepository = libraryRepository;
        _autoTagService = autoTagService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var state = await _profileResolutionService.LoadNormalizedStateAsync(includeFolders: true, cancellationToken);
        return Ok(state.Defaults);
    }

    public sealed record UpdateDefaultsRequest(
        string? DefaultFileProfile,
        Dictionary<string, string>? LibrarySchedules,
        int? RecentDownloadWindowHours);

    [HttpPost]
    public async Task<IActionResult> Update([FromBody] UpdateDefaultsRequest request, CancellationToken cancellationToken)
    {
        var state = await _profileResolutionService.LoadNormalizedStateAsync(includeFolders: true, cancellationToken);
        var previousSchedules = state.Defaults.LibrarySchedules;
        var profiles = state.Profiles;

        var requestedDefaultReference = request.DefaultFileProfile?.Trim();
        if (!string.IsNullOrWhiteSpace(requestedDefaultReference))
        {
            var defaultProfile = await _profileService.SetDefaultProfileAsync(requestedDefaultReference);
            if (defaultProfile is null)
            {
                return BadRequest("Selected default AutoTag profile does not exist.");
            }

            state = await _profileResolutionService.LoadNormalizedStateAsync(includeFolders: true, cancellationToken);
            profiles = state.Profiles;
        }

        var allowedScheduleFolderIds = _libraryRepository.IsConfigured
            ? state.FoldersById.Keys
                .Select(id => id.ToString(CultureInfo.InvariantCulture))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scheduleCleaned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (request.LibrarySchedules != null)
        {
            foreach (var (key, value) in request.LibrarySchedules)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                var folderId = key.Trim();
                if (_libraryRepository.IsConfigured && !allowedScheduleFolderIds.Contains(folderId))
                {
                    continue;
                }

                var schedule = value?.Trim();
                if (string.IsNullOrWhiteSpace(schedule))
                {
                    continue;
                }

                scheduleCleaned[folderId] = schedule;
            }
        }

        var resolvedDefaultProfileId = profiles
            .FirstOrDefault(profile => profile.IsDefault)
            ?.Id;
        var recentDownloadWindowHours = request.RecentDownloadWindowHours
            ?? state.Defaults.RecentDownloadWindowHours
            ?? AutoTagDefaultsDto.DefaultRecentDownloadWindowHours;
        if (recentDownloadWindowHours < 0)
        {
            recentDownloadWindowHours = AutoTagDefaultsDto.DefaultRecentDownloadWindowHours;
        }
        var defaults = new AutoTagDefaultsDto(
            resolvedDefaultProfileId,
            scheduleCleaned,
            recentDownloadWindowHours);
        await _store.SaveAsync(defaults);

        var normalizedState = await _profileResolutionService.LoadNormalizedStateAsync(includeFolders: true, cancellationToken);
        if (!AreSchedulesEquivalent(previousSchedules, normalizedState.Defaults.LibrarySchedules))
        {
            await StopRunningEnhancementForScheduleChangeAsync();
        }

        return Ok(normalizedState.Defaults);
    }

    private async Task StopRunningEnhancementForScheduleChangeAsync()
    {
        if (!_autoTagService.TryGetRunningEnhancementJobId(out var runningEnhancementJobId)
            || string.IsNullOrWhiteSpace(runningEnhancementJobId))
        {
            return;
        }

        var stopped = await _autoTagService.StopJobAsync(runningEnhancementJobId);
        if (stopped)
        {
            _logger.LogInformation(
                "Stopped running enhancement job {JobId} after schedule update.",
                runningEnhancementJobId);
        }
    }

    private static bool AreSchedulesEquivalent(
        IReadOnlyDictionary<string, string>? left,
        IReadOnlyDictionary<string, string>? right)
    {
        var normalizedLeft = NormalizeScheduleMap(left);
        var normalizedRight = NormalizeScheduleMap(right);
        if (normalizedLeft.Count != normalizedRight.Count)
        {
            return false;
        }

        foreach (var (folderId, schedule) in normalizedLeft)
        {
            if (!normalizedRight.TryGetValue(folderId, out var rightSchedule)
                || !string.Equals(schedule, rightSchedule, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<string, string> NormalizeScheduleMap(IReadOnlyDictionary<string, string>? source)
    {
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (source == null)
        {
            return normalized;
        }

        foreach (var (rawFolderId, rawSchedule) in source)
        {
            var folderId = rawFolderId?.Trim();
            var schedule = rawSchedule?.Trim();
            if (string.IsNullOrWhiteSpace(folderId) || string.IsNullOrWhiteSpace(schedule))
            {
                continue;
            }

            normalized[folderId] = schedule;
        }

        return normalized;
    }
}
