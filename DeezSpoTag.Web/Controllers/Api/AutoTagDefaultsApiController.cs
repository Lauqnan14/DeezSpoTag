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

    public AutoTagDefaultsApiController(
        AutoTagDefaultsStore store,
        AutoTagProfileResolutionService profileResolutionService,
        TaggingProfileService profileService,
        LibraryRepository libraryRepository)
    {
        _store = store;
        _profileResolutionService = profileResolutionService;
        _profileService = profileService;
        _libraryRepository = libraryRepository;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var state = await _profileResolutionService.LoadNormalizedStateAsync(includeFolders: true, cancellationToken);
        return Ok(state.Defaults);
    }

    public sealed record UpdateDefaultsRequest(
        string? DefaultFileProfile,
        Dictionary<string, string>? LibrarySchedules);

    [HttpPost]
    public async Task<IActionResult> Update([FromBody] UpdateDefaultsRequest request, CancellationToken cancellationToken)
    {
        var state = await _profileResolutionService.LoadNormalizedStateAsync(includeFolders: true, cancellationToken);
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
        var defaults = new AutoTagDefaultsDto(
            resolvedDefaultProfileId,
            scheduleCleaned);
        await _store.SaveAsync(defaults);

        var normalizedState = await _profileResolutionService.LoadNormalizedStateAsync(includeFolders: true, cancellationToken);
        return Ok(normalizedState.Defaults);
    }
}
