using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
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
    private readonly DeezSpoTag.Web.Services.LibraryConfigStore _configStore;

    public LibraryArtistsApiController(
        LibraryRepository repository,
        DeezSpoTag.Web.Services.LibraryConfigStore configStore)
    {
        _repository = repository;
        _configStore = configStore;
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
            var localArtists = (await _configStore.GetLocalArtistsAsync()).Select(localArtist => new
            {
                localArtist.Id,
                localArtist.Name,
                AvailableLocally = true,
                PreferredImagePath = localArtist.ImagePath,
                PreferredBackgroundPath = localArtist.BackgroundImagePath
            });
            return Ok(localArtists);
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
            var localAlbums = await _configStore.GetLocalAlbumsAsync(id);
            return Ok(localAlbums);
        }

        var dbAlbums = await _repository.GetArtistAlbumsAsync(id, folderId, cancellationToken);
        return Ok(dbAlbums);
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetArtist(long id, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            var localArtist = (await _configStore.GetLocalArtistsAsync()).FirstOrDefault(item => item.Id == id);
            if (localArtist is null)
            {
                return NotFound();
            }
            return Ok(new { localArtist.Id, localArtist.Name, PreferredImagePath = localArtist.ImagePath, PreferredBackgroundPath = localArtist.BackgroundImagePath });
        }

        var dbArtist = await _repository.GetArtistAsync(id, cancellationToken);
        if (dbArtist is null)
        {
            return NotFound();
        }

        return Ok(dbArtist);
    }

}
