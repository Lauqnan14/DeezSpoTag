using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

/// <summary>
/// API for the "Artist's Aliases" settings section: CRUD for user-defined
/// artist alias groups, plus the merge trigger. Saving a group re-points the
/// library DB, merges folders / rewrites names on disk, and starts the
/// targeted AutoTag enhancement run for the affected files.
/// </summary>
[Route("api/artists/aliases")]
[ApiController]
[Authorize]
[Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryToken]
public sealed class ArtistAliasesApiController : ControllerBase
{
    private readonly ArtistAliasService _aliasService;
    private readonly ArtistAliasMergeService _mergeService;
    private readonly AutoTagAliasEnhancementLauncher _enhancementLauncher;
    private readonly DeezSpoTag.Services.Library.LibraryRepository _libraryRepository;
    private readonly ILogger<ArtistAliasesApiController> _logger;

    public ArtistAliasesApiController(
        ArtistAliasService aliasService,
        ArtistAliasMergeService mergeService,
        AutoTagAliasEnhancementLauncher enhancementLauncher,
        DeezSpoTag.Services.Library.LibraryRepository libraryRepository,
        ILogger<ArtistAliasesApiController> logger)
    {
        _aliasService = aliasService;
        _mergeService = mergeService;
        _enhancementLauncher = enhancementLauncher;
        _libraryRepository = libraryRepository;
        _logger = logger;
    }

    public sealed record AliasGroupRequest(string? PreferredName, IReadOnlyList<string>? Aliases);

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        if (!_aliasService.IsConfigured)
        {
            return StatusCode(503, new { error = "Library DB not configured." });
        }

        var groups = await _aliasService.GetAllGroupsAsync(cancellationToken);
        return Ok(new { groups });
    }

    /// <summary>Library artist names for the merge picker.</summary>
    [HttpGet("library-artists")]
    public async Task<IActionResult> GetLibraryArtists(
        [FromQuery] string? search,
        [FromQuery] int? limit,
        CancellationToken cancellationToken)
    {
        var safeLimit = Math.Clamp(limit ?? 500, 1, 2000);
        if (!_libraryRepository.IsConfigured)
        {
            return Ok(Array.Empty<object>());
        }

        var page = await _libraryRepository.GetArtistsPageAsync(
            availability: "all",
            folderId: null,
            page: 1,
            pageSize: safeLimit,
            search: string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
            sort: "name",
            cancellationToken);
        return Ok(new
        {
            items = page.Items.Select(artist => new { id = artist.Id, name = artist.Name })
        });
    }

    /// <summary>
    /// Saves (creates/updates/merges) a group and executes the merge:
    /// DB re-point + on-disk folder merge + targeted enhancement run.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> SaveGroup([FromBody] AliasGroupRequest? request, CancellationToken cancellationToken)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.PreferredName))
        {
            return BadRequest("A preferred artist name is required.");
        }

        var aliasNames = (request.Aliases ?? Array.Empty<string>())
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Select(alias => alias.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (aliasNames.Count == 0)
        {
            return BadRequest("Select or enter at least one more artist name to merge.");
        }

        if (!_aliasService.IsConfigured || !_mergeService.IsConfigured)
        {
            return StatusCode(503, new { error = "Library DB not configured." });
        }

        ArtistAliasGroupDto group;
        try
        {
            group = await _aliasService.SaveGroupAsync(request.PreferredName, aliasNames, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }

        ArtistAliasMergeResult? mergeResult = null;
        IReadOnlyList<string> jobIds = Array.Empty<string>();
        try
        {
            mergeResult = await _mergeService.MergeGroupAsync(group.Id, cancellationToken);
            jobIds = await _enhancementLauncher.StartForFilesAsync(mergeResult.AffectedFilePaths, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Artist alias merge execution failed for group {GroupId}", group.Id);
            return Ok(new
            {
                group,
                merge = (object?)null,
                enhancementJobIds = jobIds,
                error = $"The alias group was saved but the merge failed: {ex.Message}"
            });
        }

        return Ok(new
        {
            group,
            merge = new
            {
                filesScanned = mergeResult.FilesScanned,
                filesMoved = mergeResult.FilesMoved,
                artworkFilesMoved = mergeResult.ArtworkFilesMoved,
                conflictsResolved = mergeResult.ConflictsResolved,
                artistRowsMerged = mergeResult.ArtistRowsMerged,
                trackRowsRewritten = mergeResult.TrackRowsRewritten,
                errors = mergeResult.Errors
            },
            enhancementJobIds = jobIds
        });
    }

    /// <summary>Deletes (un-merges) a group. Already-merged files are not split back.</summary>
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> DeleteGroup(long id, CancellationToken cancellationToken)
    {
        if (!_aliasService.IsConfigured)
        {
            return StatusCode(503, new { error = "Library DB not configured." });
        }

        var deleted = await _aliasService.DeleteGroupAsync(id, cancellationToken);
        return deleted ? Ok(new { deleted = true }) : NotFound(new { error = "Alias group not found." });
    }
}
