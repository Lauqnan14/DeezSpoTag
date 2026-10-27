using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/library/artists")]
[Authorize]
[AutoValidateAntiforgeryToken]
public sealed class LibraryArtistArtworkApiController : ControllerBase
{
    private readonly ArtistArtworkCatalogService _artwork;
    private readonly UserPreferencesStore _preferences;
    private readonly ArtistMetadataAutomationCoordinator _coordinator;
    private readonly DeezSpoTag.Services.Library.LibraryRepository _repository;

    public LibraryArtistArtworkApiController(
        ArtistArtworkCatalogService artwork,
        DeezSpoTag.Services.Library.LibraryRepository repository,
        UserPreferencesStore preferences, ArtistMetadataAutomationCoordinator coordinator)
    {
        _artwork = artwork;
        _repository = repository;
        _preferences = preferences;
        _coordinator = coordinator;
    }

    [HttpGet("{id:long}/artwork")]
    public async Task<IActionResult> Get(long id, CancellationToken cancellationToken = default)
    {
        if (id <= 0) return BadRequest("ArtistId is required.");
        var result = await _artwork.GetAsync(id, cancellationToken, (await _preferences.LoadAsync()).MetadataUpdaterOcrTextArtBlocking);
        return string.IsNullOrWhiteSpace(result.ArtistName) ? NotFound() : Ok(result);
    }

    [HttpPost("{id:long}/artwork/refresh")]
    public async Task<IActionResult> Refresh(
        long id,
        [FromQuery] bool force = false,
        CancellationToken cancellationToken = default)
    {
        var artist = await _repository.GetArtistAsync(id, cancellationToken);
        if (artist is null) return NotFound();
        var (queued, runId) = await _coordinator.EnqueueArtistCacheRefreshAsync(id, cancellationToken, ArtistCacheRefreshScope.Artwork);
        return Ok(new { queued, runId, status = _coordinator.GetArtistCacheRefreshStatus(runId) ?? _coordinator.GetStatus() });
    }
}
