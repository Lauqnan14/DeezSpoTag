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
    public async Task<IActionResult> GetTaxonomy(CancellationToken cancellationToken)
    {
        var taxa = await _service.GetTaxonomyAsync(cancellationToken);
        var builtInIds = PersonalGenreTaxonomy.GetDefaultTaxa()
            .Select(item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return Ok(new
        {
            version = PersonalGenreTaxonomy.Version,
            taxa = taxa.Select(item => new
            {
                item.Id,
                item.Name,
                kind = item.Kind.ToString().ToLowerInvariant(),
                item.ParentIds,
                item.ContextOnly,
                item.Aliases,
                builtIn = builtInIds.Contains(item.Id)
            })
        });
    }

    [HttpGet("taxonomy/custom")]
    public async Task<IActionResult> GetCustomTaxa(CancellationToken cancellationToken)
        => Ok(await _service.GetCustomTaxaAsync(cancellationToken));

    [HttpPost("taxonomy/custom")]
    public async Task<IActionResult> UpsertCustomTaxon(
        [FromBody] PersonalGenreTaxon taxon,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _service.UpsertCustomTaxonAsync(taxon, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("taxonomy/custom/{taxonId}")]
    public async Task<IActionResult> DeleteCustomTaxon(
        string taxonId,
        CancellationToken cancellationToken)
    {
        try
        {
            await _service.DeleteCustomTaxonAsync(taxonId, cancellationToken);
            return NoContent();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

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

    [HttpGet("configuration/export")]
    public async Task<IActionResult> ExportConfiguration(CancellationToken cancellationToken)
        => Ok(await _service.ExportConfigurationAsync(cancellationToken));

    [HttpPost("configuration/import")]
    public async Task<IActionResult> ImportConfiguration(
        [FromBody] PersonalGenreConfiguration configuration,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _service.ImportConfigurationAsync(configuration, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("scopes/{scopeType}/{scopeId:long}/locks")]
    public async Task<IActionResult> GetScopedLocks(
        string scopeType,
        long scopeId,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _service.GetScopedLocksAsync(scopeType, scopeId, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("scopes/{scopeType}/{scopeId:long}/locks")]
    public async Task<IActionResult> SaveScopedLock(
        string scopeType,
        long scopeId,
        [FromBody] PersonalGenreLockRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _service.SaveScopedLockAsync(
                new PersonalGenreScopedLock(scopeType, scopeId, request.TaxonId, request.Enabled ?? true),
                cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("scopes/{scopeType}/{scopeId:long}/locks/{taxonId}")]
    public async Task<IActionResult> DeleteScopedLock(
        string scopeType,
        long scopeId,
        string taxonId,
        CancellationToken cancellationToken)
    {
        try
        {
            await _service.DeleteScopedLockAsync(scopeType, scopeId, taxonId, cancellationToken);
            return NoContent();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("tracks/{trackId:long}/scope")]
    public async Task<IActionResult> GetTrackScope(long trackId, CancellationToken cancellationToken)
    {
        var scope = await _service.GetTrackScopeAsync(trackId, cancellationToken);
        return scope is null ? NotFound() : Ok(scope);
    }

    [HttpGet("tracks/{trackId:long}/history")]
    public async Task<IActionResult> GetTrackHistory(
        long trackId,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
        => Ok(await _service.GetTrackHistoryAsync(trackId, limit, cancellationToken));

    [HttpPost("rebuild")]
    public async Task<IActionResult> Rebuild(
        [FromQuery] long afterTrackId = 0,
        [FromQuery] int batchSize = 200,
        CancellationToken cancellationToken = default)
        => Ok(await _service.RebuildBatchAsync(afterTrackId, batchSize, cancellationToken));

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
