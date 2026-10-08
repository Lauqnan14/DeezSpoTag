using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/library/artists")]
[ApiController]
[Authorize]
[Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryToken]
public sealed class LibraryArtistExternalCacheApiController : ControllerBase
{
    private readonly LibraryConfigStore _configStore;
    private readonly DeezSpoTag.Services.Library.LibraryRepository _repository;
    private readonly ArtistMetadataAutomationCoordinator _coordinator;

    public LibraryArtistExternalCacheApiController(
        LibraryConfigStore configStore,
        DeezSpoTag.Services.Library.LibraryRepository repository,
        ArtistMetadataAutomationCoordinator coordinator)
    {
        _configStore = configStore;
        _repository = repository;
        _coordinator = coordinator;
    }

    [HttpPost("{id:long}/external-cache/refresh")]
    public async Task<IActionResult> RefreshExternalArtistCache(long id, CancellationToken cancellationToken)
    {
        if (id <= 0)
        {
            return BadRequest("ArtistId is required.");
        }

        var artist = await _repository.GetArtistAsync(id, cancellationToken);
        if (artist is null || string.IsNullOrWhiteSpace(artist.Name))
        {
            return NotFound("Artist not found.");
        }
        var queued = await _coordinator.EnqueueArtistCacheRefreshAsync(id, cancellationToken);

        if (queued) _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"Artist cache refresh queued for artist {id}."));

        return Ok(new { queued, status = _coordinator.GetStatus() });
    }
}
