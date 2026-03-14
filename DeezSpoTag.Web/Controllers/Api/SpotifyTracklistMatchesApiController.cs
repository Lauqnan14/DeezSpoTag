using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[LocalApiAuthorize]
[Route("api/spotify/tracklist")]
public class SpotifyTracklistMatchesApiController : ControllerBase
{
    private readonly ISpotifyTracklistMatchStore _matchStore;

    public SpotifyTracklistMatchesApiController(ISpotifyTracklistMatchStore matchStore)
    {
        _matchStore = matchStore;
    }

    [HttpGet("matches")]
    public IActionResult Matches([FromQuery] string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return BadRequest(new { error = "Token is required." });
        }

        var snapshot = _matchStore.GetSnapshot(token);
        if (snapshot == null)
        {
            return Ok(new { available = false });
        }

        return Ok(new
        {
            available = true,
            pending = snapshot.Pending,
            matched = snapshot.Matched,
            failed = snapshot.Failed,
            rechecking = snapshot.Rechecking,
            matches = snapshot.Matches
        });
    }
}
