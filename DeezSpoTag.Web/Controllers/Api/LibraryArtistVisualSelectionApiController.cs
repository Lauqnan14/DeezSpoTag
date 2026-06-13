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

    public LibraryArtistVisualSelectionApiController(LibraryArtistMetadataServices metadataServices)
    {
        _artistVisualSelectionService = metadataServices.ArtistVisualSelectionService;
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
}
