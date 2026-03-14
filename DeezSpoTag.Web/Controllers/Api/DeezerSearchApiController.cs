using Microsoft.AspNetCore.Mvc;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/deezer/search")]
[Authorize]
public sealed class DeezerSearchApiController : SearchApiControllerBase
{
    public DeezerSearchApiController(DeezSpoTagSearchService searchService) : base(searchService) { }

    [HttpGet]
    public async Task<IActionResult> Search(
        [FromQuery] DeezerSearchQuery request,
        CancellationToken cancellationToken = default)
    {
        var searchRequest = new SearchApiRequest(
            Provider: "deezer",
            Query: request.Query ?? string.Empty,
            Limit: request.Limit ?? 50,
            Offset: request.Offset ?? 0,
            Title: request.Title,
            Artist: request.Artist,
            Album: request.Album,
            Isrc: request.Isrc,
            DurationMs: request.DurationMs);
        return await SearchAsync(searchRequest, cancellationToken);
    }

    [HttpGet("type")]
    public async Task<IActionResult> SearchByType(
        [FromQuery] string query,
        [FromQuery] string type,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken cancellationToken = default)
    {
        return await SearchByTypeAsync(
            "deezer",
            query,
            type,
            limit,
            offset,
            cancellationToken);
    }

    public sealed class DeezerSearchQuery
    {
        public string? Query { get; init; }
        public int? Limit { get; init; }
        public int? Offset { get; init; }
        public string? Title { get; init; }
        public string? Artist { get; init; }
        public string? Album { get; init; }
        public string? Isrc { get; init; }
        public long? DurationMs { get; init; }
    }
}
