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

    internal static string FormatVocabularyDisplayName(string name)
        => DeezSpoTag.Web.Services.AutoTag.LocalAutoTagRunner.CapitalizeGenre(name);

    [HttpGet("taxonomy")]
    public async Task<IActionResult> GetTaxonomy(CancellationToken cancellationToken)
    {
        var taxa = await _service.GetTaxonomyAsync(cancellationToken);

        // Three provenances, because "built in" no longer says enough: the original
        // 156 protected terms and the 3,745 generated from the researched master are
        // both protected, but they came from different places and the user should be
        // able to tell which is which. Only a term the user created is editable.
        var customIds = (await _service.GetCustomTaxaAsync(cancellationToken))
            .Select(item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var coreIds = PersonalGenreTaxonomy.GetDefaultTaxa()
            .Select(item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var researched = DeezSpoTag.Services.Genre.ResearchedGenreCatalog.Current;

        return Ok(new
        {
            version = PersonalGenreResolver.Version,
            // The researched vocabulary that supplies Genre and Style recognition,
            // with the source it was generated from, so a user can see which
            // vocabulary a classification was actually made against.
            researched = new
            {
                researched.Version,
                researched.Genres,
                researched.Styles,
                AliasCount = researched.Aliases.Count,
                ExclusionCount = researched.ExclusionReasons.Count,
                AmbiguityCount = researched.AmbiguityReasons.Count,
                SourceSha256 = researched.SourceSha256
            },
            // Explains where the values this stage interprets actually come from.
            input = "audio-file-tags",
            // The one authoritative source for the region vocabulary. The client
            // builds its navigation from this rather than repeating the names, so a
            // region added on the server appears without a matching client change.
            regions = PersonalGenreRegions.All.Select(region => new
            {
                slug = region.Slug,
                name = region.Name,
                order = region.Order,
                icon = region.Icon
            }),
            taxa = taxa.Select(item => new
            {
                item.Id,
                item.Name,
                displayName = item.Kind is PersonalGenreTaxonKind.Genre or PersonalGenreTaxonKind.Style
                    ? FormatVocabularyDisplayName(item.Name)
                    : item.Name,
                kind = item.Kind.ToString().ToLowerInvariant(),
                item.ParentIds,
                item.ContextOnly,
                item.Aliases,
                // Regional organization metadata. Null or empty means unscoped, which
                // is how every universal term is expressed. It never affects resolution.
                item.Regions,
                // Core is the legacy protected vocabulary, Researched is the generated
                // master, Custom is the user's own. Both protected origins are
                // read-only; only Custom offers Edit and Delete.
                origin = !customIds.Contains(item.Id)
                    ? (coreIds.Contains(item.Id) ? "core" : "researched")
                    : "custom",
                editable = customIds.Contains(item.Id),
                builtIn = !customIds.Contains(item.Id)
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
    {
        var settings = await _service.GetSettingsAsync(cancellationToken);
        return Ok(new
        {
            settings.Enabled,
            settings.MaxGenres,
            settings.PreserveUnmappedTags,
            settings.IncludeParentGenres,
            // Genre normalization is owned here. The alias rules and block list are
            // returned as plain pairs/values so the client does not have to know
            // the storage shape, and so an empty list stays an empty list.
            settings.NormalizeGenreTags,
            genreTagAliasRules = settings.GenreTagAliasRules?
                .Select(rule => new { rule.Alias, rule.Canonical })
                .ToArray() ?? [],
            genreTagBlockList = settings.GenreTagBlockList ?? []
        });
    }

    [HttpPost("settings")]
    public async Task<IActionResult> UpdateSettings(
        [FromBody] PersonalGenreSettingsRequest request,
        CancellationToken cancellationToken)
    {
        // Every field is nullable so an omitted one can be told apart from a
        // deliberate value. Without this, a client that only means to change the
        // genre count would send no normalization fields, and binding those onto
        // the settings record would read their defaults as an instruction: the
        // toggle would be turned off and the rules would be reset.
        var current = await _service.GetSettingsAsync(cancellationToken);
        var merged = new PersonalGenreSettings(
            request.Enabled ?? current.Enabled,
            request.MaxGenres ?? current.MaxGenres,
            request.PreserveUnmappedTags ?? current.PreserveUnmappedTags,
            request.IncludeParentGenres ?? current.IncludeParentGenres,
            request.NormalizeGenreTags ?? current.NormalizeGenreTags,
            request.GenreTagAliasRules?.ToArray() ?? current.GenreTagAliasRules,
            request.GenreTagBlockList?.ToArray() ?? current.GenreTagBlockList);

        await _service.SaveSettingsAsync(merged, cancellationToken);
        return await GetSettings(cancellationToken);
    }

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

    /// <summary>
    /// Reports what Genre Cleanup would change in a file's tags, without writing it.
    /// </summary>
    /// <remarks>
    /// This is the review surface for the cleanup decision: what the file holds now,
    /// what it would hold afterwards, and which values moved, were canonicalized,
    /// were removed or were preserved as unknown. It reads the file and produces no
    /// side effect; the write stays where it already was, in an enabled AutoTag run.
    /// </remarks>
    [HttpGet("tracks/{trackId:long}/cleanup-preview")]
    public async Task<IActionResult> GetCleanupPreview(long trackId, CancellationToken cancellationToken)
    {
        var preview = await _service.GetCleanupPreviewAsync(trackId, cancellationToken);
        return preview is null ? NotFound() : Ok(preview);
    }

    /// <summary>Returns auditable construction decisions without writing tags or saving a resolution.</summary>
    [HttpGet("tracks/{trackId:long}/construction-preview")]
    public async Task<IActionResult> GetStyleConstructionPreview(long trackId, CancellationToken cancellationToken)
    {
        var preview = await _service.GetStyleConstructionPreviewAsync(trackId, cancellationToken);
        return preview is null ? NotFound() : Ok(preview);
    }

    /// <summary>
    /// Re-resolves a track from its audio file.
    ///
    /// This reads the file's tags. It does not consult analysis results, provider
    /// metadata or any stored evidence, so what it reports always describes the
    /// file as it is now.
    /// </summary>
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

/// <summary>
/// The settings write request, with every field optional.
/// </summary>
/// <remarks>
/// This is deliberately not <see cref="PersonalGenreSettings"/>. That record is
/// fully populated, so an omitted field would bind to its default and be
/// indistinguishable from a value the user actually chose — which for
/// <c>NormalizeGenreTags</c> means false. A partial write has to be able to say
/// "I am not touching this one", so the transport type is where nullability
/// belongs.
/// </remarks>
public sealed class PersonalGenreSettingsRequest
{
    public bool? Enabled { get; set; }

    public int? MaxGenres { get; set; }

    public bool? PreserveUnmappedTags { get; set; }

    public bool? IncludeParentGenres { get; set; }

    public bool? NormalizeGenreTags { get; set; }

    public List<PersonalGenreAliasRule>? GenreTagAliasRules { get; set; }

    public List<string>? GenreTagBlockList { get; set; }
}
