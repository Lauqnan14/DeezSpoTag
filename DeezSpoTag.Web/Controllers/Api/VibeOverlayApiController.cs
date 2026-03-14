using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/library/vibe-overlay")]
[Authorize]
public sealed class VibeOverlayApiController : ControllerBase
{
    private readonly VibeMatchService _matchService;

    public VibeOverlayApiController(VibeMatchService matchService)
    {
        _matchService = matchService;
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] long trackId, [FromQuery] int limit = 20, CancellationToken cancellationToken = default)
    {
        if (trackId <= 0)
        {
            return BadRequest("Track ID required.");
        }

        var response = await _matchService.GetMatchesAsync(trackId, Math.Clamp(limit, 1, 50), cancellationToken);
        var sourceFeatures = BuildOverlayFeatures(response.SourceAnalysis);
        var matches = response.Matches.Select(match => new VibeOverlayTrackDto(
            match.TrackId,
            match.Title,
            match.ArtistName,
            match.AlbumTitle,
            match.Score,
            BuildOverlayFeatures(match),
            match.MoodTags ?? Array.Empty<string>())).ToList();

        var payload = new VibeOverlayResponseDto(
            response.SourceTrackId,
            response.SourceTitle,
            response.SourceArtist,
            sourceFeatures,
            response.SourceAnalysis?.MoodTags ?? Array.Empty<string>(),
            matches);

        return Ok(payload);
    }

    private static VibeOverlayFeatureDto[] BuildOverlayFeatures(TrackAnalysisResultDto? analysis)
    {
        if (analysis is null)
        {
            return Array.Empty<VibeOverlayFeatureDto>();
        }

        return new[]
        {
            new VibeOverlayFeatureDto("energy", "Energy", analysis.Energy, 0, 1, null),
            new VibeOverlayFeatureDto("valence", "Mood", analysis.Valence, 0, 1, null),
            new VibeOverlayFeatureDto("danceability", "Danceability", analysis.Danceability, 0, 1, null),
            new VibeOverlayFeatureDto("arousal", "Arousal", analysis.Arousal, 0, 1, null),
            new VibeOverlayFeatureDto("bpm", "Tempo", analysis.Bpm, 60, 200, "BPM")
        };
    }

    private static VibeOverlayFeatureDto[] BuildOverlayFeatures(VibeMatchTrackDto match)
    {
        return new[]
        {
            new VibeOverlayFeatureDto("energy", "Energy", match.Energy, 0, 1, null),
            new VibeOverlayFeatureDto("valence", "Mood", match.Valence, 0, 1, null),
            new VibeOverlayFeatureDto("danceability", "Danceability", match.Danceability, 0, 1, null),
            new VibeOverlayFeatureDto("arousal", "Arousal", match.Arousal, 0, 1, null),
            new VibeOverlayFeatureDto("bpm", "Tempo", match.Bpm, 60, 200, "BPM")
        };
    }
}
