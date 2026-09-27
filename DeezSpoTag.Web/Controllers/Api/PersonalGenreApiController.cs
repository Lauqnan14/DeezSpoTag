using DeezSpoTag.Services.Genre;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/personal-genre")]
[ApiController]
[Authorize]
[AutoValidateAntiforgeryToken]
public sealed class PersonalGenreApiController : ControllerBase
{
    private readonly PersonalGenreService _service;

    public PersonalGenreApiController(PersonalGenreService service)
    {
        _service = service;
    }

    [HttpGet("taxonomy")]
    public IActionResult GetTaxonomy()
        => Ok(new
        {
            version = PersonalGenreTaxonomy.Version,
            taxa = _service.GetTaxonomy()
        });

    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings(CancellationToken cancellationToken)
        => Ok(await _service.GetSettingsAsync(cancellationToken));

    [HttpPost("settings")]
    public async Task<IActionResult> UpdateSettings(
        [FromBody] PersonalGenreSettings settings,
        CancellationToken cancellationToken)
        => Ok(await _service.SaveSettingsAsync(settings, cancellationToken));

    [HttpGet("mappings")]
    public async Task<IActionResult> GetMappings(CancellationToken cancellationToken)
        => Ok(await _service.GetMappingsAsync(cancellationToken));

    [HttpPost("mappings")]
    public async Task<IActionResult> UpsertMapping(
        [FromBody] PersonalGenreMapping mapping,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _service.UpsertMappingAsync(mapping, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("mappings/{id:long}")]
    public async Task<IActionResult> DeleteMapping(long id, CancellationToken cancellationToken)
    {
        await _service.DeleteMappingAsync(id, cancellationToken);
        return NoContent();
    }

    [HttpGet("rules")]
    public async Task<IActionResult> GetRules(CancellationToken cancellationToken)
        => Ok(await _service.GetRulesAsync(cancellationToken));

    [HttpPost("rules")]
    public async Task<IActionResult> UpsertRule(
        [FromBody] PersonalGenreRule rule,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _service.UpsertRuleAsync(rule, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("rules/{id:long}")]
    public async Task<IActionResult> DeleteRule(long id, CancellationToken cancellationToken)
    {
        await _service.DeleteRuleAsync(id, cancellationToken);
        return NoContent();
    }

    [HttpGet("tracks/{trackId:long}/locks")]
    public async Task<IActionResult> GetLocks(long trackId, CancellationToken cancellationToken)
        => Ok(await _service.GetLocksAsync(trackId, cancellationToken));

    [HttpPost("tracks/{trackId:long}/locks")]
    public async Task<IActionResult> SaveLock(
        long trackId,
        [FromBody] PersonalGenreLockRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _service.SaveLockAndResolveAsync(
                new PersonalGenreLock(trackId, request.TaxonId, request.Enabled ?? true),
                cancellationToken);
            return result is null ? NotFound() : Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("tracks/{trackId:long}/locks/{taxonId}")]
    public async Task<IActionResult> DeleteLock(
        long trackId,
        string taxonId,
        CancellationToken cancellationToken)
    {
        var result = await _service.DeleteLockAndResolveAsync(trackId, taxonId, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("tracks/{trackId:long}")]
    public async Task<IActionResult> GetTrack(long trackId, CancellationToken cancellationToken)
    {
        var result = await _service.GetTrackResultAsync(trackId, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost("tracks/{trackId:long}/resolve")]
    public async Task<IActionResult> ResolveTrack(long trackId, CancellationToken cancellationToken)
    {
        var result = await _service.ResolveTrackAsync(trackId, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }
}


public sealed class PersonalGenreLockRequest
{
    public string TaxonId { get; set; } = string.Empty;
    public bool? Enabled { get; set; }
}
