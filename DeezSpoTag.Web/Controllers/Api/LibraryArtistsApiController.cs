using DeezSpoTag.Services.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/library/artists")]
[ApiController]
[Authorize]
[Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryToken]
public class LibraryArtistsApiController : ControllerBase
{
    private readonly LibraryRepository _repository;

    public LibraryArtistsApiController(LibraryRepository repository)
    {
        _repository = repository;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] string? availability,
        [FromQuery] long? folderId,
        [FromQuery] string? search,
        [FromQuery] string? sort,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            if (page.HasValue || pageSize.HasValue)
            {
                var safePage = Math.Max(1, page ?? 1);
                var safePageSize = Math.Clamp(pageSize ?? 300, 1, 1000);
                return Ok(new
                {
                    items = Array.Empty<ArtistDto>(),
                    totalCount = 0,
                    page = safePage,
                    pageSize = safePageSize,
                    hasMore = false
                });
            }

            return Ok(Array.Empty<ArtistDto>());
        }

        if (page.HasValue || pageSize.HasValue)
        {
            var artistPage = await _repository.GetArtistsPageAsync(
                availability,
                folderId,
                page ?? 1,
                pageSize ?? 300,
                search,
                sort,
                cancellationToken);
            return Ok(new
            {
                items = artistPage.Items,
                totalCount = artistPage.TotalCount,
                page = artistPage.Page,
                pageSize = artistPage.PageSize,
                hasMore = (artistPage.Page * artistPage.PageSize) < artistPage.TotalCount
            });
        }

        var dbArtists = await _repository.GetArtistsAsync(availability, folderId, cancellationToken);
        return Ok(dbArtists);
    }

    [HttpGet("{id:long}/albums")]
    public async Task<IActionResult> GetAlbums(
        long id,
        [FromQuery] long? folderId,
        CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return Ok(Array.Empty<AlbumDto>());
        }

        var dbAlbums = await _repository.GetArtistAlbumsAsync(id, folderId, cancellationToken);
        return Ok(dbAlbums);
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetArtist(long id, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return NotFound();
        }

        var dbArtist = await _repository.GetArtistAsync(id, cancellationToken);
        if (dbArtist is null)
        {
            return NotFound();
        }

        return Ok(dbArtist);
    }

}
