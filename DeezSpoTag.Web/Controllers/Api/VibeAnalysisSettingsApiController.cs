using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/library/analysis/settings")]
[ApiController]
[Authorize]
[Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryToken]
public sealed class VibeAnalysisSettingsApiController : ControllerBase
{
    private readonly VibeAnalysisSettingsStore _store;
    private readonly TrackAnalysisBackgroundService _analysisService;

    public VibeAnalysisSettingsApiController(
        VibeAnalysisSettingsStore store,
        TrackAnalysisBackgroundService analysisService)
    {
        _store = store;
        _analysisService = analysisService;
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
            Math.Clamp(request.IntervalMinutes ?? existing.IntervalMinutes, 5, 240),
            request.UseLibraryOrder ?? existing.UseLibraryOrder,
            NormalizeLibraryOrder(request.LibraryOrder ?? existing.LibraryOrder));

        var saved = await _store.SaveAsync(cleaned);
        await _analysisService.ApplySettingsAsync(saved, HttpContext.RequestAborted);
        return Ok(saved);
    }

    private static long[] NormalizeLibraryOrder(IEnumerable<long>? libraryOrder)
    {
        if (libraryOrder is null)
        {
            return Array.Empty<long>();
        }

        return libraryOrder
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
    }
}

public sealed class VibeAnalysisSettingsUpdateRequest
{
    public bool? Enabled { get; set; }
    public int? BatchSize { get; set; }
    public int? IntervalMinutes { get; set; }
    public bool? UseLibraryOrder { get; set; }
    public long[]? LibraryOrder { get; set; }
}
