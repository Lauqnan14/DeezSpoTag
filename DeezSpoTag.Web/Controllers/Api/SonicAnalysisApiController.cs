using System;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

/// <summary>
/// Sonic Analysis control surface, surfaced inside the existing Vibe Analysis
/// area.
///
/// <para>Deliberately reports and maintains only. It exposes no vector, no
/// distance, and no scoring parameter, because none of those are things a person
/// tuning a music library needs to reason about, and every one of them is an
/// implementation detail that can change without a migration.</para>
/// </summary>
[ApiController]
[Route("api/library/analysis/sonic")]
[Authorize]
public sealed class SonicAnalysisApiController : ControllerBase
{
    private readonly SonicAnalysisService _sonicService;

    public SonicAnalysisApiController(SonicAnalysisService sonicService)
    {
        _sonicService = sonicService;
    }

    [HttpGet("status")]
    public Task<SonicAnalysisStatusDto> GetStatus(CancellationToken cancellationToken)
        => _sonicService.GetStatusAsync(cancellationToken);

    [HttpGet("settings")]
    public Task<SonicAnalysisSettingsDto> GetSettings()
        => _sonicService.GetSettingsAsync();

    [HttpPost("settings")]
    [ValidateAntiForgeryToken]
    public Task<SonicAnalysisSettingsDto> SaveSettings(
        SonicAnalysisSettingsDto settings,
        CancellationToken cancellationToken)
        => _sonicService.SaveSettingsAsync(settings, cancellationToken);

    /// <summary>Reloads stored vectors. Does not re-embed anything.</summary>
    [HttpPost("rebuild-index")]
    [ValidateAntiForgeryToken]
    public Task<SonicAnalysisStatusDto> RebuildIndex(CancellationToken cancellationToken)
        => _sonicService.RebuildIndexAsync(cancellationToken);

    /// <summary>Queues a bounded batch of tracks whose embedding is missing or stale.</summary>
    [HttpPost("reanalyze")]
    [ValidateAntiForgeryToken]
    public Task<SonicReanalyzeResultDto> Reanalyze(
        [FromQuery] long? libraryId,
        CancellationToken cancellationToken)
        => _sonicService.ReanalyzeAsync(libraryId, cancellationToken);
}
