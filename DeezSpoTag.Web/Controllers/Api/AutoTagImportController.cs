using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/autotag")]
[Authorize]
public class AutoTagImportController : ControllerBase
{
    private readonly ExternalFileImportService _externalImportService;

    public AutoTagImportController(ExternalFileImportService externalImportService)
    {
        _externalImportService = externalImportService;
    }

    [HttpGet("import-preview")]
    public async Task<IActionResult> PreviewImport(
        [FromQuery] string sourcePath,
        [FromQuery] long? targetFolderId,
        [FromQuery] bool runAutoTag,
        [FromQuery] string? profileId,
        CancellationToken cancellationToken)
    {
        var request = new ExternalImportRequest(sourcePath, targetFolderId, runAutoTag, profileId);
        var result = await _externalImportService.PreviewAsync(request, cancellationToken);
        if (!result.Success)
        {
            return BadRequest(result);
        }

        return Ok(result);
    }

    [HttpPost("import-files")]
    public async Task<IActionResult> ImportFiles([FromBody] ExternalImportRequest request, CancellationToken cancellationToken)
    {
        var result = await _externalImportService.ImportAsync(request, cancellationToken);
        if (!result.Success)
        {
            return BadRequest(result);
        }

        return Ok(result);
    }
}
