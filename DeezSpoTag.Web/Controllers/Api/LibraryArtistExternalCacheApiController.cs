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
    private readonly ArtistExternalMetadataBackfillService _artistExternalMetadataBackfillService;

    public LibraryArtistExternalCacheApiController(
        LibraryConfigStore configStore,
        LibraryArtistMetadataServices metadataServices)
    {
        _configStore = configStore;
        _artistExternalMetadataBackfillService = metadataServices.ArtistExternalMetadataBackfillService;
    }

    [HttpPost("{id:long}/external-cache/refresh")]
    public async Task<IActionResult> RefreshExternalArtistCache(long id, CancellationToken cancellationToken)
    {
        if (id <= 0)
        {
            return BadRequest("ArtistId is required.");
        }

        var refreshed = await _artistExternalMetadataBackfillService.RefreshArtistAsync(id, cancellationToken);
        if (!refreshed)
        {
            return NotFound("Artist not found.");
        }

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"Artist external cache refresh completed for artist {id}."));

        return Ok(new { refreshed = true });
    }
}
