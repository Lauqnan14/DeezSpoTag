using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/quality-scanner")]
[Authorize]
public sealed class QualityScannerApiController : ControllerBase
{
    private readonly QualityScannerService _qualityScannerService;

    public QualityScannerApiController(QualityScannerService qualityScannerService)
    {
        _qualityScannerService = qualityScannerService;
    }

    [HttpPost("start")]
    public async Task<IActionResult> Start([FromQuery] QualityScannerStartRequestQuery request)
    {
        request ??= new QualityScannerStartRequestQuery();

        var resolvedScope = string.IsNullOrWhiteSpace(request.Scope) ? "watchlist" : request.Scope;
        var resolvedTrigger = string.IsNullOrWhiteSpace(request.Trigger) ? "manual" : request.Trigger;
        var resolvedMinBitDepth = request.MinBitDepth.HasValue && request.MinBitDepth.Value > 0
            ? Math.Clamp(request.MinBitDepth.Value, 1, 64)
            : (int?)null;
        var resolvedMinSampleRateHz = request.MinSampleRateKhz.HasValue && request.MinSampleRateKhz.Value > 0
            ? Math.Clamp((int)Math.Round(request.MinSampleRateKhz.Value * 1000d), 1000, 768000)
            : (int?)null;
        var markAutomationWindow = string.Equals(resolvedTrigger, "automation", StringComparison.OrdinalIgnoreCase);
        var started = await _qualityScannerService.StartAsync(
            new QualityScannerStartRequest
            {
                Scope = resolvedScope,
                FolderId = request.FolderId,
                MinFormat = request.MinFormat,
                MinBitDepth = resolvedMinBitDepth,
                MinSampleRateHz = resolvedMinSampleRateHz,
                QueueAtmosAlternatives = request.QueueAtmos,
                CooldownMinutes = request.CooldownMinutes,
                Trigger = resolvedTrigger,
                MarkAutomationWindow = markAutomationWindow
            },
            CancellationToken.None);

        return Ok(new
        {
            started,
            state = _qualityScannerService.GetState()
        });
    }

    [HttpGet("status")]
    public IActionResult Status()
    {
        return Ok(_qualityScannerService.GetState());
    }

    [HttpPost("stop")]
    public async Task<IActionResult> Stop()
    {
        await _qualityScannerService.StopAsync();
        return Ok(_qualityScannerService.GetState());
    }

    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings(CancellationToken cancellationToken)
    {
        var settings = await _qualityScannerService.GetAutomationSettingsAsync(cancellationToken);
        return Ok(settings);
    }

    [HttpPost("settings")]
    public async Task<IActionResult> UpdateSettings(
        [FromBody] QualityScannerAutomationSettingsRequest request,
        CancellationToken cancellationToken)
    {
        var existing = await _qualityScannerService.GetAutomationSettingsAsync(cancellationToken);
        var intervalMinutes = request.IntervalMinutes.HasValue && request.IntervalMinutes.Value > 0
            ? request.IntervalMinutes.Value
            : existing.IntervalMinutes;
        var cleaned = new QualityScannerAutomationSettingsDto(
            Enabled: request.Enabled ?? existing.Enabled,
            IntervalMinutes: Math.Clamp(intervalMinutes, 15, 10080),
            Scope: string.Equals(request.Scope ?? existing.Scope, "all", StringComparison.OrdinalIgnoreCase) ? "all" : "watchlist",
            FolderId: request.FolderId ?? existing.FolderId,
            QueueAtmosAlternatives: request.QueueAtmosAlternatives ?? existing.QueueAtmosAlternatives,
            CooldownMinutes: Math.Clamp(request.CooldownMinutes ?? existing.CooldownMinutes, 0, 43200),
            LastStartedUtc: null,
            LastFinishedUtc: null);

        var saved = await _qualityScannerService.UpdateAutomationSettingsAsync(cleaned, cancellationToken);
        return Ok(saved);
    }
}

public sealed class QualityScannerAutomationSettingsRequest
{
    public bool? Enabled { get; set; }
    public int? IntervalMinutes { get; set; }
    public string? Scope { get; set; }
    public long? FolderId { get; set; }
    public bool? QueueAtmosAlternatives { get; set; }
    public int? CooldownMinutes { get; set; }
}

public sealed class QualityScannerStartRequestQuery
{
    public string? Scope { get; set; }
    public long? FolderId { get; set; }
    public string? MinFormat { get; set; }
    public int? MinBitDepth { get; set; }
    public double? MinSampleRateKhz { get; set; }
    public bool? QueueAtmos { get; set; }
    public int? CooldownMinutes { get; set; }
    public string? Trigger { get; set; }
}
