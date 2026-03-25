using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/library/radio")]
[ApiController]
[Authorize]
public class LibraryRadioApiController : ControllerBase
{
    private readonly PlatformAuthService _authService;
    private readonly LibraryRepository _libraryRepository;
    private readonly RadioService _radioService;

    public LibraryRadioApiController(
        PlatformAuthService authService,
        LibraryRepository libraryRepository,
        RadioService radioService)
    {
        _authService = authService;
        _libraryRepository = libraryRepository;
        _radioService = radioService;
    }

    [HttpGet("stations")]
    public async Task<IActionResult> GetStations([FromQuery] long libraryId, CancellationToken cancellationToken)
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

        var stations = await _radioService.GetStationsAsync(plexUserId.Value, libraryId, cancellationToken);
        return Ok(stations);
    }

    [HttpGet]
    public async Task<IActionResult> GetRadio(
        [FromQuery] string type,
        [FromQuery] string? value,
        [FromQuery] long libraryId,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (libraryId <= 0)
        {
            return BadRequest("libraryId is required.");
        }
        if (string.IsNullOrWhiteSpace(type))
        {
            return BadRequest("type is required.");
        }

        var plexUserId = await PlexUserIdResolver.ResolveAsync(_authService, _libraryRepository, cancellationToken);
        if (plexUserId is null)
        {
            return BadRequest("Plex user not configured.");
        }

        var detail = await _radioService.GetStationAsync(type, value, plexUserId.Value, libraryId, Math.Clamp(limit, 10, 100), cancellationToken);
        if (detail is null)
        {
            return NotFound();
        }

        return Ok(detail);
    }
}
