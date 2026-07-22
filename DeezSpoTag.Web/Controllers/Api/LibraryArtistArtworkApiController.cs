using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/library/artists")]
[Authorize]
[AutoValidateAntiforgeryToken]
public sealed class LibraryArtistArtworkApiController : ControllerBase
{
    private readonly ArtistArtworkCatalogService _artwork;
    private readonly ArtistMetadataCacheRefreshService _cacheRefresh;
    private readonly DeezSpoTag.Services.Library.LibraryRepository _repository;

    public LibraryArtistArtworkApiController(
        ArtistArtworkCatalogService artwork,
        ArtistMetadataCacheRefreshService cacheRefresh,
        DeezSpoTag.Services.Library.LibraryRepository repository)
    {
        _artwork = artwork;
        _cacheRefresh = cacheRefresh;
        _repository = repository;
    }

    [HttpGet("{id:long}/artwork")]
    public async Task<IActionResult> Get(long id, [FromQuery] bool refresh = true, CancellationToken cancellationToken = default)
    {
        if (id <= 0) return BadRequest("ArtistId is required.");
        if (refresh)
        {
            var artist = await _repository.GetArtistAsync(id, cancellationToken);
            if (artist is null) return NotFound();
            await _cacheRefresh.RefreshArtistAsync(id, artist.Name, "auto", false, cancellationToken);
        }
        var result = await _artwork.GetAsync(id, cancellationToken);
        return string.IsNullOrWhiteSpace(result.ArtistName) ? NotFound() : Ok(result);
    }

    [HttpPost("{id:long}/artwork/refresh")]
    public async Task<IActionResult> Refresh(long id, CancellationToken cancellationToken)
    {
        var artist = await _repository.GetArtistAsync(id, cancellationToken);
        if (artist is null) return NotFound();
        await _cacheRefresh.RefreshArtistAsync(id, artist.Name, "auto", false, cancellationToken);
        var result = await _artwork.GetAsync(id, cancellationToken);
        return string.IsNullOrWhiteSpace(result.ArtistName) ? NotFound() : Ok(result);
    }
}
