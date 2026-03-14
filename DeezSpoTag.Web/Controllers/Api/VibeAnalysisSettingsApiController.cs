using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/library/analysis/settings")]
[ApiController]
[Authorize]
public sealed class VibeAnalysisSettingsApiController : ControllerBase
{
    private readonly VibeAnalysisSettingsStore _store;

    public VibeAnalysisSettingsApiController(VibeAnalysisSettingsStore store)
    {
        _store = store;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var settings = await _store.LoadAsync();
        return Ok(settings);
    }

    [HttpPost]
    public async Task<IActionResult> Update([FromBody] VibeAnalysisSettingsUpdateRequest request)
    {
        var existing = await _store.LoadAsync();
        var cleaned = new VibeAnalysisSettingsDto(
            request.Enabled ?? existing.Enabled,
            Math.Clamp(request.BatchSize ?? existing.BatchSize, 10, 500),
            Math.Clamp(request.IntervalMinutes ?? existing.IntervalMinutes, 5, 240));

        var saved = await _store.SaveAsync(cleaned);
        return Ok(saved);
    }
}

public sealed class VibeAnalysisSettingsUpdateRequest
{
    public bool? Enabled { get; set; }
    public int? BatchSize { get; set; }
    public int? IntervalMinutes { get; set; }
}
