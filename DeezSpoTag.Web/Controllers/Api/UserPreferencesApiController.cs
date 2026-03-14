using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/user-preferences")]
[ApiController]
[Authorize]
public sealed class UserPreferencesApiController : ControllerBase
{
    private readonly UserPreferencesStore _store;

    public UserPreferencesApiController(UserPreferencesStore store)
    {
        _store = store;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var prefs = await _store.LoadAsync();
        return Ok(prefs);
    }

    [HttpPost]
    public async Task<IActionResult> Save([FromBody] UserPreferencesDto prefs)
    {
        await _store.SaveAsync(prefs);
        return Ok(prefs);
    }
}
