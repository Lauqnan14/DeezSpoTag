using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/library/artists")]
[ApiController]
[Authorize]
[Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryToken]
public sealed class LibraryArtistVisualSelectionApiController : ControllerBase
{
    private readonly ArtistVisualSelectionService _artistVisualSelectionService;
    private readonly ArtistVisualCacheService _artistVisualCacheService;

    public LibraryArtistVisualSelectionApiController(LibraryArtistMetadataServices metadataServices)
    {
        _artistVisualSelectionService = metadataServices.ArtistVisualSelectionService;
        _artistVisualCacheService = metadataServices.ArtistVisualCacheService;
    }

    [HttpPost("{id:long}/visuals")]
    public async Task<IActionResult> SaveVisuals(
        long id,
        [FromBody] ArtistVisualSelectionRequest request,
        CancellationToken cancellationToken)
    {
        if (id <= 0)
        {
            return BadRequest("ArtistId is required.");
        }

        var result = await _artistVisualSelectionService.SaveAsync(id, request ?? new ArtistVisualSelectionRequest(), cancellationToken);
        if (!result.Success)
        {
            return StatusCode(result.StatusCode, result.Error);
        }

        return Ok(new
        {
            stored = true,
            avatarPath = result.AvatarPath,
            backgroundPath = result.BackgroundPath,
            warnings = result.Warnings
        });
    }

    [HttpPost("{id:long}/visuals/cache")]
    public async Task<IActionResult> CacheVisuals(
        long id,
        [FromBody] ArtistVisualCacheRequest? request,
        CancellationToken cancellationToken)
    {
        if (id <= 0)
        {
            return BadRequest("ArtistId is required.");
        }

        var candidates = request?.Candidates ?? new List<ArtistVisualCacheCandidate>();
        var cached = await _artistVisualCacheService.CacheAsync(id, candidates, cancellationToken);
        return Ok(cached);
    }
}

public sealed class ArtistVisualCacheRequest
{
    public List<ArtistVisualCacheCandidate> Candidates { get; set; } = new();
}
