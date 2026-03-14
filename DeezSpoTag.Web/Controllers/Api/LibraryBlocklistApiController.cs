using DeezSpoTag.Services.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/library/blocklist")]
[ApiController]
[Authorize]
public sealed class LibraryBlocklistApiController : ControllerBase
{
    private readonly LibraryRepository _repository;

    public LibraryBlocklistApiController(LibraryRepository repository)
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

        var entries = await _repository.GetDownloadBlocklistEntriesAsync(cancellationToken);
        return Ok(entries);
    }

    public sealed record BlocklistUpsertRequest(string Field, string Value, bool? Enabled);

    [HttpPost]
    public async Task<IActionResult> Upsert([FromBody] BlocklistUpsertRequest request, CancellationToken cancellationToken)
    {
        if (request == null
            || string.IsNullOrWhiteSpace(request.Field)
            || string.IsNullOrWhiteSpace(request.Value))
        {
            return BadRequest("Field and value are required.");
        }

        if (!_repository.IsConfigured)
        {
            return StatusCode(503, new { error = "Library DB not configured." });
        }

        var entry = await _repository.UpsertDownloadBlocklistEntryAsync(
            request.Field,
            request.Value,
            request.Enabled ?? true,
            cancellationToken);
        if (entry == null)
        {
            return BadRequest("Unsupported blocklist field or value.");
        }

        return Ok(entry);
    }

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return StatusCode(503, new { error = "Library DB not configured." });
        }

        var removed = await _repository.RemoveDownloadBlocklistEntryAsync(id, cancellationToken);
        return Ok(new { removed });
    }
}
