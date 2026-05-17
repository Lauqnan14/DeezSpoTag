using DeezSpoTag.Services.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/library/analysis")]
[ApiController]
[Authorize]
[Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryToken]
public sealed class LibraryAnalysisResetApiController : ControllerBase
{
    private readonly LibraryRepository _repository;

    public LibraryAnalysisResetApiController(LibraryRepository repository)
    {
        _repository = repository;
    }

    [HttpPost("reset")]
    public async Task<IActionResult> Reset(CancellationToken cancellationToken)
    {
        await _repository.ResetAllAnalysisAsync(cancellationToken);
        return Ok(new { reset = true });
    }
}
