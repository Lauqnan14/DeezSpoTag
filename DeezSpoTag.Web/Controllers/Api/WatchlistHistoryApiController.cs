using DeezSpoTag.Services.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/history/watchlist")]
[ApiController]
[Authorize]
public class WatchlistHistoryApiController : ControllerBase
{
    private readonly LibraryRepository _repository;

    public WatchlistHistoryApiController(LibraryRepository repository)
    {
        _repository = repository;
    }

    [HttpGet]
    public async Task<IActionResult> GetHistory(
        [FromQuery] int? limit,
        [FromQuery] int? offset,
        [FromQuery] long? sinceId,
        CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return StatusCode(503, new { error = "Library DB not configured." });
        }

        var cappedLimit = Math.Clamp(limit ?? 50, 1, 200);
        var safeOffset = Math.Max(offset ?? 0, 0);
        var safeSinceId = Math.Max(sinceId ?? 0, 0);

        if (safeSinceId > 0)
        {
            var changedEntries = await _repository.GetWatchlistHistorySinceAsync(safeSinceId, cappedLimit, cancellationToken);
            return Ok(new { entries = changedEntries, total = changedEntries.Count, limit = cappedLimit, offset = 0, sinceId = safeSinceId });
        }

        var entries = await _repository.GetWatchlistHistoryAsync(cappedLimit, safeOffset, cancellationToken);
        var total = await _repository.GetWatchlistHistoryCountAsync(cancellationToken);
        return Ok(new { entries, total, limit = cappedLimit, offset = safeOffset });
    }
}
