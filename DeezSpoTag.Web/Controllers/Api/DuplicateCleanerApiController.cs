using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/library/duplicates")]
[Authorize]
public class DuplicateCleanerApiController : ControllerBase
{
    private readonly LibraryRepository _repository;
    private readonly LibraryConfigStore _configStore;
    private readonly DuplicateCleanerService _duplicateCleanerService;

    public DuplicateCleanerApiController(
        LibraryRepository repository,
        LibraryConfigStore configStore,
        DuplicateCleanerService duplicateCleanerService)
    {
        _repository = repository;
        _configStore = configStore;
        _duplicateCleanerService = duplicateCleanerService;
    }

    [HttpPost("scan")]
    public async Task<IActionResult> Scan([FromQuery] long? folderId, [FromQuery] bool useDupsFolder = true, CancellationToken cancellationToken = default)
    {
        var folders = _repository.IsConfigured
            ? await _repository.GetFoldersAsync(cancellationToken)
            : _configStore.GetFolders();
        var enabledFolders = folders.Where(folder => folder.Enabled).ToList();

        if (folderId.HasValue)
        {
            var selected = enabledFolders.FirstOrDefault(folder => folder.Id == folderId.Value);
            if (selected is null)
            {
                return BadRequest("Selected library folder not found or disabled.");
            }
            enabledFolders = new List<FolderDto> { selected };
        }

        if (enabledFolders.Count == 0)
        {
            return Ok(new
            {
                filesScanned = 0,
                duplicatesFound = 0,
                deleted = 0,
                spaceFreedBytes = 0,
                duplicatesFolderName = DuplicateCleanerService.DuplicatesFolderName
            });
        }

        var result = await _duplicateCleanerService.ScanAsync(enabledFolders, useDupsFolder, cancellationToken);
        return Ok(new
        {
            filesScanned = result.FilesScanned,
            duplicatesFound = result.DuplicatesFound,
            deleted = result.Deleted,
            spaceFreedBytes = result.SpaceFreedBytes,
            duplicatesFolderName = DuplicateCleanerService.DuplicatesFolderName
        });
    }
}
