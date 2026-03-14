using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/autotag")]
[Authorize]
public class AutoTagFolderStructureController : ControllerBase
{
    private readonly TaggingProfileService _profileService;

    public AutoTagFolderStructureController(TaggingProfileService profileService)
    {
        _profileService = profileService;
    }

    [HttpGet("folder-structure")]
    public async Task<IActionResult> GetFolderStructure([FromQuery] string? profileId)
    {
        var profile = string.IsNullOrWhiteSpace(profileId)
            ? await _profileService.GetDefaultAsync()
            : await _profileService.GetByIdAsync(profileId);
        if (profile == null)
        {
            return NotFound();
        }

        return Ok(profile.FolderStructure);
    }

    [HttpPut("folder-structure")]
    public async Task<IActionResult> UpdateFolderStructure(
        [FromQuery] string? profileId,
        [FromBody] DeezSpoTag.Core.Models.Settings.FolderStructureSettings settings)
    {
        var profile = string.IsNullOrWhiteSpace(profileId)
            ? await _profileService.GetDefaultAsync()
            : await _profileService.GetByIdAsync(profileId);
        if (profile == null)
        {
            return NotFound();
        }

        profile.FolderStructure = settings ?? new DeezSpoTag.Core.Models.Settings.FolderStructureSettings();
        await _profileService.UpsertAsync(profile);
        return Ok(profile.FolderStructure);
    }
}
