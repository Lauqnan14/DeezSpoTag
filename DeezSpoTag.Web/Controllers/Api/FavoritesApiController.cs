using Microsoft.AspNetCore.Mvc;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/favorites")]
[Authorize]
public sealed class FavoritesApiController : ControllerBase
{
    private readonly SpotifyFavoritesService _spotifyFavoritesService;
    private readonly DeezerFavoritesService _deezerFavoritesService;

    public FavoritesApiController(
        SpotifyFavoritesService spotifyFavoritesService,
        DeezerFavoritesService deezerFavoritesService)
    {
        _spotifyFavoritesService = spotifyFavoritesService;
        _deezerFavoritesService = deezerFavoritesService;
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int? limit, CancellationToken cancellationToken)
    {
        var resolvedLimit = limit.HasValue && limit.Value > 0 ? limit.Value : 50;

        var spotifyTask = _spotifyFavoritesService.GetFavoritesAsync(resolvedLimit, cancellationToken);
        var deezerTask = _deezerFavoritesService.GetFavoritesAsync(resolvedLimit, cancellationToken);

        await Task.WhenAll(spotifyTask, deezerTask);

        return Ok(new
        {
            spotify = spotifyTask.Result,
            deezer = deezerTask.Result
        });
    }
}
