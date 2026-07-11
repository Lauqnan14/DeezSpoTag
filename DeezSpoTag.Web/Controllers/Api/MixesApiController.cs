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
    private const string MelodayMixPrefix = "meloday-";
    private const string MelodayAppUserName = "Meloday";
    private const string MelodayAppUserId = "deezspotag:meloday";
    private readonly PlatformAuthService _authService;
    private readonly LibraryRepository _libraryRepository;
    private readonly MixService _mixService;

    public MixesApiController(
        PlatformAuthService authService,
        LibraryRepository libraryRepository,
        MixService mixService)
    {
        _authService = authService;
        _libraryRepository = libraryRepository;
        _mixService = mixService;
    }

    [HttpGet]
    public async Task<IActionResult> GetMixes([FromQuery] long? libraryId, CancellationToken cancellationToken)
    {
        var plexUserId = await PlexUserIdResolver.ResolveAsync(_authService, _libraryRepository, cancellationToken);
        var melodayUserId = await EnsureMelodayAppUserAsync(cancellationToken);

        var requestedLibraryId = libraryId.GetValueOrDefault();
        var mixes = new List<MixSummaryDto>();
        if (plexUserId is not null)
        {
            mixes.AddRange(requestedLibraryId > 0
                ? await _mixService.GetMixesAsync(plexUserId.Value, requestedLibraryId, cancellationToken)
                : await _mixService.GetMixesAsync(plexUserId.Value, cancellationToken));
        }

        mixes.AddRange(requestedLibraryId > 0
            ? await _mixService.GetMixesAsync(melodayUserId, requestedLibraryId, cancellationToken)
            : await _mixService.GetMixesAsync(melodayUserId, cancellationToken));

        return Ok(mixes
            .GroupBy(static mix => (mix.Id, mix.LibraryId))
            .Select(static group => group.First())
            .OrderByDescending(static mix => mix.GeneratedAtUtc)
            .ToList());
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetMix(string id, [FromQuery] long libraryId, CancellationToken cancellationToken)
    {
        if (libraryId <= 0)
        {
            return BadRequest("libraryId is required.");
        }

        var plexUserId = await ResolveMixUserIdAsync(id, cancellationToken);
        if (plexUserId is null)
        {
            return BadRequest("Plex user not configured.");
        }

        var mix = await _mixService.GetMixAsync(id, plexUserId.Value, libraryId, cancellationToken);
        if (mix is null)
        {
            return NotFound();
        }

        return Ok(mix);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteMix(string id, [FromQuery] long libraryId, CancellationToken cancellationToken)
    {
        if (libraryId <= 0)
        {
            return BadRequest("libraryId is required.");
        }

        var plexUserId = await ResolveMixUserIdAsync(id, cancellationToken);
        if (plexUserId is null)
        {
            return BadRequest("Plex user not configured.");
        }

        var deleted = await _mixService.DeleteMixAsync(id, plexUserId.Value, libraryId, cancellationToken);
        return deleted ? NoContent() : NotFound();
    }

    private async Task<long?> ResolveMixUserIdAsync(string? mixId, CancellationToken cancellationToken)
    {
        if (mixId?.StartsWith(MelodayMixPrefix, StringComparison.OrdinalIgnoreCase) == true)
        {
            return await EnsureMelodayAppUserAsync(cancellationToken);
        }

        return await PlexUserIdResolver.ResolveAsync(_authService, _libraryRepository, cancellationToken);
    }

    private async Task<long> EnsureMelodayAppUserAsync(CancellationToken cancellationToken)
        => await _libraryRepository.EnsurePlexUserAsync(
            MelodayAppUserName,
            MelodayAppUserId,
            "deezspotag",
            "meloday",
            cancellationToken);
}
