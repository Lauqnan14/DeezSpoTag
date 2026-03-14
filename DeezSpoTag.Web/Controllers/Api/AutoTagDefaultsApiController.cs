using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/autotag/defaults")]
[ApiController]
[Authorize]
public sealed class AutoTagDefaultsApiController : ControllerBase
{
    private readonly AutoTagDefaultsStore _store;

    public AutoTagDefaultsApiController(AutoTagDefaultsStore store)
    {
        _store = store;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var defaults = await _store.LoadAsync();
        return Ok(defaults);
    }

    public sealed record UpdateDefaultsRequest(
        string? DefaultFileProfile,
        Dictionary<string, string>? LibraryProfiles,
        Dictionary<string, string>? LibrarySchedules);

    [HttpPost]
    public async Task<IActionResult> Update([FromBody] UpdateDefaultsRequest request)
    {
        var cleaned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (request.LibraryProfiles != null)
        {
            foreach (var (key, value) in request.LibraryProfiles)
            {
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }
                cleaned[key.Trim()] = value.Trim();
            }
        }

        var scheduleCleaned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (request.LibrarySchedules != null)
        {
            foreach (var (key, value) in request.LibrarySchedules)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }
                var schedule = value?.Trim();
                if (string.IsNullOrWhiteSpace(schedule))
                {
                    continue;
                }
                scheduleCleaned[key.Trim()] = schedule;
            }
        }

        var defaults = new AutoTagDefaultsDto(
            string.IsNullOrWhiteSpace(request.DefaultFileProfile) ? null : request.DefaultFileProfile.Trim(),
            cleaned,
            scheduleCleaned);

        var saved = await _store.SaveAsync(defaults);
        return Ok(saved);
    }
}
