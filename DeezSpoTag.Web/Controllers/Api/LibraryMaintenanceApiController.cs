using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/library/maintenance")]
[Authorize]
public class LibraryMaintenanceApiController : ControllerBase
{
    private readonly LibraryRepository _repository;
    private readonly LibraryConfigStore _configStore;
    private readonly ILogger<LibraryMaintenanceApiController> _logger;

    public LibraryMaintenanceApiController(
        LibraryRepository repository,
        LibraryConfigStore configStore,
        ILogger<LibraryMaintenanceApiController> logger)
    {
        _repository = repository;
        _configStore = configStore;
        _logger = logger;
    }

    [HttpPost("cleanup-missing")]
    public async Task<IActionResult> CleanupMissing([FromQuery] long? folderId = null, CancellationToken cancellationToken = default)
    {
        if (!_repository.IsConfigured)
        {
            return Ok(new { ok = false, reason = "library_db_not_configured", removed = 0 });
        }

        var removed = await _repository.CleanupMissingFilesAsync(folderId, cancellationToken);
        var scopeLabel = await ResolveFolderScopeLabelAsync(folderId, cancellationToken);
        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"{scopeLabel} cleanup removed {removed} missing files."));
        return Ok(new { ok = true, removed });
    }

    [HttpPost("clear")]
    public async Task<IActionResult> ClearLibrary([FromQuery] long? folderId = null, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Library clear request received.");
        if (!_repository.IsConfigured)
        {
            _logger.LogWarning("Library clear skipped because repository is not configured.");
            return Ok(new
            {
                ok = false,
                reason = "library_db_not_configured",
                artistsRemoved = 0,
                albumsRemoved = 0,
                tracksRemoved = 0
            });
        }

        var result = folderId.HasValue
            ? await _repository.ClearFolderLocalContentAsync(folderId.Value, cancellationToken)
            : await _repository.ClearLibraryDataAsync(cancellationToken);
        var scopeLabel = await ResolveFolderScopeLabelAsync(folderId, cancellationToken);
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "{ScopeLabel} clear completed. artists={ArtistsRemoved}, albums={AlbumsRemoved}, tracks={TracksRemoved}",
                scopeLabel,
                result.ArtistsRemoved,
                result.AlbumsRemoved,
                result.TracksRemoved);
        }
        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"{scopeLabel} cleared (metadata only): artists={result.ArtistsRemoved}, albums={result.AlbumsRemoved}, tracks={result.TracksRemoved}."));

        return Ok(new
        {
            ok = true,
            artistsRemoved = result.ArtistsRemoved,
            albumsRemoved = result.AlbumsRemoved,
            tracksRemoved = result.TracksRemoved
        });
    }

    private async Task<string> ResolveFolderScopeLabelAsync(long? folderId, CancellationToken cancellationToken)
    {
        if (!folderId.HasValue)
        {
            return "Library";
        }

        var folders = await _repository.GetFoldersAsync(cancellationToken);
        var folder = folders.FirstOrDefault(item => item.Id == folderId.Value);
        return folder is null ? $"Folder {folderId.Value}" : $"Folder {folder.DisplayName}";
    }
}
