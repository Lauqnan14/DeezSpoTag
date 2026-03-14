using Microsoft.AspNetCore.Mvc;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/apple")]
[Authorize]
public sealed class AppleSearchApiController : ControllerBase
{
    private static readonly bool AppleDisabled = ReadAppleDisabled();
    private readonly DeezSpoTagSearchService _searchService;
    private readonly ILogger<AppleSearchApiController> _logger;

    public AppleSearchApiController(DeezSpoTagSearchService searchService, ILogger<AppleSearchApiController> logger)
    {
        _searchService = searchService;
        _logger = logger;
    }

    private static bool ReadAppleDisabled()
    {
        var value = Environment.GetEnvironmentVariable("DEEZSPOTAG_APPLE_DISABLED");
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search(
        [FromQuery] string term,
        [FromQuery] int limit = 30,
        [FromQuery] int offset = 0,
        [FromQuery] string? types = null,
        [FromQuery] string? audioTraits = null,
        CancellationToken cancellationToken = default)
    {
        if (AppleDisabled)
        {
            return StatusCode(503, CreateUnavailablePayload("Apple Music is disabled."));
        }

        if (string.IsNullOrWhiteSpace(term))
        {
            return BadRequest("term is required");
        }

        try
        {
            var request = new DeezSpoTagSearchRequest(
                Engine: "apple",
                Query: term,
                Limit: limit,
                Offset: offset,
                Types: types,
                AudioTraits: audioTraits);
            var result = await _searchService.SearchAsync(request, cancellationToken);
            if (result == null)
            {
                return StatusCode(502, CreateUnavailablePayload("Apple search failed."));
            }

            return Ok(new
            {
                available = true,
                source = result.Source,
                tracks = result.Tracks,
                albums = result.Albums,
                artists = result.Artists,
                playlists = result.Playlists,
                stations = result.Stations,
                videos = result.Videos,
                hasMoreVideos = result.HasMoreVideos
            });
        }
        catch (HttpRequestException ex) when (ex.StatusCode.HasValue)
        {
            _logger.LogWarning(ex, "Apple search failed with status {StatusCode}", ex.StatusCode);
            return StatusCode((int)ex.StatusCode.Value, CreateUnavailablePayload("Apple search failed."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Apple search failed");
            return StatusCode(503, CreateUnavailablePayload("Apple search unavailable."));
        }
    }

    private static object CreateUnavailablePayload(string message)
    {
        return new
        {
            available = false,
            error = message,
            source = "apple",
            tracks = Array.Empty<object>(),
            albums = Array.Empty<object>(),
            artists = Array.Empty<object>(),
            playlists = Array.Empty<object>(),
            stations = Array.Empty<object>(),
            videos = Array.Empty<object>(),
            hasMoreVideos = false
        };
    }
}
