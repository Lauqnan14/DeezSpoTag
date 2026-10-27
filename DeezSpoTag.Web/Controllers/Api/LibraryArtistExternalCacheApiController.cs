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
    private readonly ArtistMetadataCacheRefreshService _cacheRefresh;

    public LibraryArtistExternalCacheApiController(
        LibraryConfigStore configStore,
        DeezSpoTag.Services.Library.LibraryRepository repository,
        ArtistMetadataAutomationCoordinator coordinator, ArtistMetadataCacheRefreshService cacheRefresh)
    {
        _configStore = configStore;
        _repository = repository;
        _coordinator = coordinator;
        _cacheRefresh = cacheRefresh;
    }

    [HttpPost("{id:long}/external-cache/refresh")]
    public async Task<IActionResult> RefreshExternalArtistCache(long id, CancellationToken cancellationToken, [FromQuery] bool automatic = false)
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
        var (queued, runId) = await _coordinator.EnqueueArtistCacheRefreshAsync(id, cancellationToken, automatic: automatic);

        if (queued) _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"Artist cache refresh queued for artist {id}."));

        return Ok(new { queued, runId, status = _coordinator.GetArtistCacheRefreshStatus(runId) ?? _coordinator.GetStatus() });
    }
    [HttpGet("{id:long}/external-cache/status")]
    public async Task<IActionResult> Status(long id, [FromQuery] string? runId, CancellationToken cancellationToken)
    {
        var request = await _cacheRefresh.BuildAutomaticRequestAsync(id, ArtistCacheRefreshScope.Full);
        var complete = await _cacheRefresh.IsCompleteAsync(request, cancellationToken);
        var status = _coordinator.GetArtistCacheRefreshStatus(runId, id) ?? _coordinator.GetStatus();
        var completed = Guid.TryParseExact(runId, "N", out _) ? await _cacheRefresh.GetRunResultAsync(runId!, cancellationToken) : null;
        if (completed?.ArtistId != id) completed = null;
        return Ok(new { complete, status, completed });
    }

}
