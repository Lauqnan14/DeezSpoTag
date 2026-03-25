using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/mixes")]
[ApiController]
[Authorize]
public class MixesApiController : ControllerBase
{
    private readonly PlatformAuthService _authService;
    private readonly LibraryRepository _libraryRepository;
    private readonly MixService _mixService;
    private readonly MixSyncService _mixSyncService;

    public MixesApiController(
        PlatformAuthService authService,
        LibraryRepository libraryRepository,
        MixService mixService,
        MixSyncService mixSyncService)
    {
        _authService = authService;
        _libraryRepository = libraryRepository;
        _mixService = mixService;
        _mixSyncService = mixSyncService;
    }

    [HttpGet]
    public async Task<IActionResult> GetMixes([FromQuery] long libraryId, CancellationToken cancellationToken)
    {
        if (libraryId <= 0)
        {
            return BadRequest("libraryId is required.");
        }

        var plexUserId = await PlexUserIdResolver.ResolveAsync(_authService, _libraryRepository, cancellationToken);
        if (plexUserId is null)
        {
            return BadRequest("Plex user not configured.");
        }

        var mixes = await _mixService.GetMixesAsync(plexUserId.Value, libraryId, cancellationToken);
        foreach (var mix in mixes)
        {
            await _mixSyncService.SyncMixAsync(mix, plexUserId.Value, cancellationToken);
        }
        return Ok(mixes);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetMix(string id, [FromQuery] long libraryId, CancellationToken cancellationToken)
    {
        if (libraryId <= 0)
        {
            return BadRequest("libraryId is required.");
        }

        var plexUserId = await PlexUserIdResolver.ResolveAsync(_authService, _libraryRepository, cancellationToken);
        if (plexUserId is null)
        {
            return BadRequest("Plex user not configured.");
        }

        var mix = await _mixService.GetMixAsync(id, plexUserId.Value, libraryId, cancellationToken);
        if (mix is null)
        {
            return NotFound();
        }

        await _mixSyncService.SyncMixAsync(mix.Summary, plexUserId.Value, cancellationToken);
        return Ok(mix);
    }
}
