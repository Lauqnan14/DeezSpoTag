using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/library/recommendations")]
[ApiController]
[Authorize]
public class LibraryRecommendationsApiController : ControllerBase
{
    private const string MissingLibraryIdMessage = "libraryId is required.";
    private readonly LibraryRecommendationService _recommendationService;

    public LibraryRecommendationsApiController(LibraryRecommendationService recommendationService)
    {
        _recommendationService = recommendationService;
    }

    [HttpGet("stations")]
    public async Task<IActionResult> GetStations(
        [FromQuery] long libraryId,
        [FromQuery] long? folderId,
        CancellationToken cancellationToken)
    {
        if (libraryId <= 0)
        {
            return BadRequest(MissingLibraryIdMessage);
        }

        var stations = await _recommendationService.GetStationsAsync(libraryId, folderId, cancellationToken);
        return Ok(stations);
    }

    [HttpGet]
    public async Task<IActionResult> GetRecommendations(
        [FromQuery] long libraryId,
        [FromQuery] string? stationId,
        [FromQuery] long? folderId,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (libraryId <= 0)
        {
            return BadRequest(MissingLibraryIdMessage);
        }

        var detail = await _recommendationService.GetRecommendationsAsync(
            libraryId,
            stationId: stationId,
            folderId: folderId,
            limit: Math.Clamp(limit, 1, 50),
            cancellationToken);

        if (detail is null)
        {
            return NotFound();
        }

        if (!string.IsNullOrWhiteSpace(stationId)
            && !string.Equals(stationId.Trim(), detail.Station.Id, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(stationId.Trim(), LibraryRecommendationService.RecommendationSourceId, StringComparison.OrdinalIgnoreCase))
        {
            return NotFound();
        }

        return Ok(detail);
    }

    [HttpPost("shazam-scan")]
    public async Task<IActionResult> TriggerShazamScan(
        [FromQuery] long libraryId,
        [FromQuery] long? folderId,
        [FromQuery] bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (libraryId <= 0)
        {
            return BadRequest(MissingLibraryIdMessage);
        }

        var started = await _recommendationService.TriggerFullLibraryShazamScanAsync(
            libraryId,
            folderId,
            force,
            cancellationToken);

        return Ok(new { started, force, folderId });
    }

    [HttpGet("shazam-scan/status")]
    public async Task<IActionResult> GetShazamScanStatus(
        [FromQuery] long libraryId,
        [FromQuery] long? folderId,
        CancellationToken cancellationToken = default)
    {
        if (libraryId <= 0)
        {
            return BadRequest(MissingLibraryIdMessage);
        }

        var status = await _recommendationService.GetShazamScanStatusAsync(libraryId, folderId, cancellationToken);
        if (status is null)
        {
            return NotFound();
        }

        return Ok(status);
    }
}
