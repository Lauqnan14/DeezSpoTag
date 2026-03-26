using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/spotify/track")]
[Authorize]
public sealed class SpotifyTrackApiController : ControllerBase
{
    private readonly SpotifyPathfinderMetadataClient _pathfinder;
    private readonly ILogger<SpotifyTrackApiController> _logger;

    public SpotifyTrackApiController(
        SpotifyPathfinderMetadataClient pathfinder,
        ILogger<SpotifyTrackApiController> logger)
    {
        _pathfinder = pathfinder;
        _logger = logger;
    }

    [HttpGet("{trackId}/metadata")]
    public async Task<IActionResult> GetMetadata(string trackId, CancellationToken cancellationToken)
        => await SpotifyMetadataActionHelper.FetchByUrlAsync(
            this,
            _pathfinder,
            _logger,
            new SpotifyMetadataActionHelper.SpotifyMetadataFetchRequest(
                Id: trackId,
                IdParameterName: "trackId",
                SpotifyType: "track",
                FailureLogMessage: "Spotify track metadata fetch failed",
                FailureResponseMessage: "Spotify track metadata failed."),
            cancellationToken);
}
