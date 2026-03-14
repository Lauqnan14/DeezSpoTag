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

    public SpotifyDiscoveryTracklistApiController(SpotifyRecommendationService recommendationService)
    {
        _recommendationService = recommendationService;
    }

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
}
