using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/autotag")]
[Authorize]
[AutoValidateAntiforgeryToken]
public sealed class AutoTagEnhancementController : ControllerBase
{
    private readonly AutoTagFolderScopeDependencies _folderScopeDependencies;

    public AutoTagEnhancementController(AutoTagFolderScopeDependencies folderScopeDependencies)
    {
        _folderScopeDependencies = folderScopeDependencies;
    }

    [HttpGet("enhancement/technical-profiles")]
    public async Task<IActionResult> GetEnhancementTechnicalProfiles(
        [FromQuery] string? folderIds,
        [FromQuery] string? scope,
        CancellationToken cancellationToken)
    {
        var folders = await AutoTagFolderScopeHelper.ResolveLibraryFoldersAsync(
            _folderScopeDependencies.LibraryRepository,
            _folderScopeDependencies.LibraryConfigStore,
            cancellationToken);
        var enabledFolders = folders
            .Where(folder => folder.Enabled
                && !string.IsNullOrWhiteSpace(folder.RootPath)
                && LibraryFolderPathSafety.IsMusicFolder(folder))
            .ToList();
        var folderIdsFromQuery = AutoTagFolderScopeHelper.ParseFolderIdsQuery(folderIds);
        var selectedFolderIds = AutoTagFolderScopeHelper.NormalizeFolderIds(folderIdsFromQuery, enabledFolders);

        if (folderIdsFromQuery.Count > 0 && selectedFolderIds.Count == 0)
        {
            return BadRequest("Selected library folders were not found or are disabled.");
        }

        var resolvedScope = string.Equals(scope, "watchlist", StringComparison.OrdinalIgnoreCase)
            ? "watchlist"
            : "all";
        var tracks = await _folderScopeDependencies.LibraryRepository.GetQualityScanTracksAsync(
            resolvedScope,
            selectedFolderIds.Count == 1 ? selectedFolderIds[0] : null,
            minFormat: null,
            minBitDepth: null,
            minSampleRateHz: null,
            cancellationToken);
        if (selectedFolderIds.Count > 1)
        {
            var allowed = selectedFolderIds.ToHashSet();
            tracks = tracks
                .Where(track => track.DestinationFolderId.HasValue && allowed.Contains(track.DestinationFolderId.Value))
                .ToList();
        }

        var profiles = tracks
            .Select(QualityScanTrackFormatter.FormatTechnicalProfile)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                value = group.First(),
                count = group.Count()
            })
            .OrderByDescending(item => item.count)
            .ThenBy(item => item.value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Ok(new
        {
            scope = resolvedScope,
            folderIds = selectedFolderIds,
            totalTracks = tracks.Count,
            profiles
        });
    }
}
