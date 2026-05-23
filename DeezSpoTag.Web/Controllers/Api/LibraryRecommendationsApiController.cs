using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/library/recommendations")]
[ApiController]
[Authorize]
[Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryToken]
public class LibraryRecommendationsApiController : ControllerBase
{
    private const string MissingLibraryIdMessage = "libraryId is required.";
    private readonly LibraryRecommendationService _recommendationService;

    public sealed record RecommendationRejectRequest(
        long LibraryId,
        long? FolderId,
        string? StationId,
        string? TrackSourceId,
        string? Isrc,
        string? Title,
        string? Artist,
        int Limit = 50);

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

    [HttpPost("rebuild")]
    public async Task<IActionResult> RebuildRecommendations(
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

        var detail = await _recommendationService.RebuildRecommendationsAsync(
            libraryId,
            stationId,
            folderId,
            Math.Clamp(limit, 1, 50),
            cancellationToken);

        return detail is null ? NotFound() : Ok(detail);
    }

    [HttpPost("reject")]
    public async Task<IActionResult> RejectRecommendation(
        [FromBody] RecommendationRejectRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return BadRequest("Request body is required.");
        }

        if (request.LibraryId <= 0)
        {
            return BadRequest(MissingLibraryIdMessage);
        }

        if (string.IsNullOrWhiteSpace(request.StationId))
        {
            return BadRequest("stationId is required.");
        }

        if (string.IsNullOrWhiteSpace(request.TrackSourceId))
        {
            return BadRequest("trackSourceId is required.");
        }

        if (!IsValidRecommendationTrackSourceId(request.TrackSourceId))
        {
            return BadRequest("trackSourceId must be a numeric Deezer track id.");
        }

        var detail = await _recommendationService.RejectRecommendationTrackAsync(
            new RecommendationRejectionUpsertInput(
                request.LibraryId,
                request.FolderId,
                request.StationId,
                request.TrackSourceId,
                request.Isrc,
                request.Title,
                request.Artist),
            Math.Clamp(request.Limit, 1, 50),
            cancellationToken);

        return detail is null ? NotFound() : Ok(detail);
    }

    private static bool IsValidRecommendationTrackSourceId(string? trackSourceId)
    {
        return !string.IsNullOrWhiteSpace(trackSourceId)
               && long.TryParse(trackSourceId.Trim(), out _);
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
