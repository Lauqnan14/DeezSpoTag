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
        var scopedStatus = _coordinator.GetArtistCacheRefreshStatus(runId, id);
        var activeRunId = scopedStatus?.RunId ?? runId;
        var complete = scopedStatus is not null
            ? scopedStatus.RunState == "idle" && scopedStatus.CacheRefresh.FailedArtists == 0
            : await _cacheRefresh.IsCompleteAsync(
                await _cacheRefresh.BuildAutomaticRequestAsync(id, ArtistCacheRefreshScope.Full), cancellationToken);
        var status = scopedStatus ?? _coordinator.GetStatus();
        var completed = Guid.TryParseExact(activeRunId, "N", out _)
            ? await _cacheRefresh.GetRunResultAsync(activeRunId!, cancellationToken) : null;
        if (completed?.ArtistId is { } completedArtist && completedArtist != id) completed = null;
        var revision = await _cacheRefresh.GetCachedArtistRevisionAsync(id, cancellationToken);
        return Ok(new { complete, runId = scopedStatus?.RunId, revision, status, completed });
    }

}
