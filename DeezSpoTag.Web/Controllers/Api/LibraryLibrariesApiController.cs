using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/library/libraries")]
[ApiController]
[Authorize]
public class LibraryLibrariesApiController : ControllerBase
{
    private readonly LibraryRepository _repository;
    public LibraryLibrariesApiController(LibraryRepository repository)
    {
        _repository = repository;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return StatusCode(503, new { error = "Library DB not configured." });
        }

        var libraries = await _repository.GetLibrariesAsync(cancellationToken);
        return Ok(libraries);
    }
}
