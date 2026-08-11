using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[LocalApiAuthorize]
[Route("api/spotify/tracklist")]
public class SpotifyDiscoveryTracklistApiController : ControllerBase
{
    private const string UrlRequiredMessage = "URL is required.";
    private readonly SpotifyRecommendationService _recommendationService;
    private readonly PlaylistSyncService _playlistSyncService;

    public SpotifyDiscoveryTracklistApiController(
        SpotifyRecommendationService recommendationService,
        PlaylistSyncService playlistSyncService)
    {
        _recommendationService = recommendationService;
        _playlistSyncService = playlistSyncService;
    }

    public sealed record SpotifyRecommendationPlaylistSyncApiRequest(
        string? Target,
        bool Monitor,
        string? Name,
        string? Description,
        string? ImageUrl);

    [HttpGet("recommendations")]
    public async Task<IActionResult> Recommendations(
        [FromQuery] string url,
        [FromQuery] int limit = 12,
        [FromQuery] bool debug = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return BadRequest(new { error = UrlRequiredMessage });
        }

        if (debug)
        {
            var debugResult = await _recommendationService.FetchRecommendationsDebugAsync(url, limit, cancellationToken);
            if (debugResult.Sections.Count == 0)
            {
                return Ok(new
                {
                    available = false,
                    sections = Array.Empty<object>(),
                    debug = new
                    {
                        operationName = debugResult.OperationName,
                        variables = debugResult.VariablesJson,
                        raw = debugResult.RawJson
                    }
                });
            }

            return Ok(new
            {
                available = true,
                sections = debugResult.Sections,
                debug = new
                {
                    operationName = debugResult.OperationName,
                    variables = debugResult.VariablesJson,
                    raw = debugResult.RawJson
                }
            });
        }

        var sections = await _recommendationService.FetchRecommendationsAsync(url, limit, cancellationToken);
        if (sections.Count == 0)
        {
            return Ok(new { available = false, sections = Array.Empty<object>() });
        }

        return Ok(new { available = true, sections });
    }

    [ValidateAntiForgeryToken]
    [HttpPost("/api/spotify/recommendations/playlists/{playlistId}/sync")]
    public async Task<IActionResult> SyncRecommendationPlaylist(
        string playlistId,
        [FromBody] SpotifyRecommendationPlaylistSyncApiRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return BadRequest(new { error = "Playlist ID is required." });
        }

        var target = string.IsNullOrWhiteSpace(request?.Target)
            ? "navidrome"
            : request.Target.Trim();
        if (!string.Equals(target, "navidrome", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { error = "Only Navidrome sync is supported for Spotify recommendations." });
        }

        var result = await _playlistSyncService.SyncSpotifyRecommendationPlaylistToNavidromeAsync(
            new PlaylistSyncService.SpotifyRecommendationPlaylistSyncRequest(
                playlistId,
                request?.Name,
                request?.Description,
                request?.ImageUrl,
                request?.Monitor ?? false),
            cancellationToken);

        if (!result.Success)
        {
            return BadRequest(result);
        }

        return Ok(result);
    }
}
