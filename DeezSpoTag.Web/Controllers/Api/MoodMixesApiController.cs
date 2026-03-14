using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Authorize]
[Route("api/mixes")]
public sealed class MoodMixesApiController : ControllerBase
{
    private readonly MoodMixService _service;

    public MoodMixesApiController(MoodMixService service)
    {
        _service = service;
    }

    [HttpGet("mood-presets")]
    public async Task<IActionResult> GetPresets(CancellationToken cancellationToken)
    {
        return Ok(await _service.GetPresetsAsync(cancellationToken));
    }

    [HttpPost("mood")]
    public async Task<IActionResult> GenerateMix([FromBody] MoodMixRequestDto request, CancellationToken cancellationToken)
    {
        var response = await _service.GenerateMixAsync(request, cancellationToken);
        return Ok(response);
    }
}

[ApiController]
[Authorize]
[Route("api/mixes/mood")]
public sealed class MoodMixPreferencesApiController : ControllerBase
{
    private readonly MoodMixPreferencesStore _preferencesStore;

    public MoodMixPreferencesApiController(MoodMixPreferencesStore preferencesStore)
    {
        _preferencesStore = preferencesStore;
    }

    [HttpPost("save-preferences")]
    public async Task<IActionResult> SavePreferences([FromBody] MoodMixPreferencesDto preferences)
    {
        await _preferencesStore.SaveAsync(preferences);
        return Ok(new { status = "saved" });
    }
}
