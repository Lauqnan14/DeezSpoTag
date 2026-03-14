using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/library/lyrics-refresh")]
[Authorize]
public sealed class LyricsRefreshApiController : ControllerBase
{
    private readonly LyricsRefreshQueueService _queueService;

    public LyricsRefreshApiController(LyricsRefreshQueueService queueService)
    {
        _queueService = queueService;
    }

    [HttpPost("enqueue")]
    public IActionResult Enqueue([FromBody] LyricsRefreshEnqueueRequest request)
    {
        var result = _queueService.Enqueue(request.TrackIds ?? Array.Empty<long>());
        var status = _queueService.GetStatus();
        return Ok(new
        {
            jobType = result.JobType,
            requested = result.Requested,
            enqueued = result.Enqueued,
            skipped = result.Skipped,
            status
        });
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        return Ok(_queueService.GetStatus());
    }
}

public sealed class LyricsRefreshEnqueueRequest
{
    public IReadOnlyCollection<long>? TrackIds { get; set; }
}
