using DeezSpoTag.Web.Services;
using DeezSpoTag.Web.Services.Favorites;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/favorites")]
[Authorize]
public sealed class FavoritesApiController : ControllerBase
{
    private readonly FavoritesProviderRegistry _registry;

    public FavoritesApiController(FavoritesProviderRegistry registry)
    {
        _registry = registry;
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int? limit, CancellationToken cancellationToken)
    {
        var resolvedLimit = limit.HasValue && limit.Value > 0 ? limit.Value : 50;

        var providers = await Task.WhenAll(_registry.Providers.Select(async provider =>
        {
            var result = await ResolveAsync(provider, resolvedLimit, cancellationToken);
            return new
            {
                key = provider.Key,
                displayName = provider.DisplayName,
                iconPath = provider.IconPath,
                result.Available,
                result.Message,
                result.Albums,
                result.Playlists,
                result.Tracks
            };
        }));

        return Ok(new { providers });
    }

    /// <summary>
    /// A provider that throws must not take the tab down with it: one platform being broken is
    /// exactly the case the tab has to survive.
    /// </summary>
    private static async Task<FavoritesResult> ResolveAsync(
        IFavoritesProvider provider,
        int limit,
        CancellationToken cancellationToken)
    {
        try
        {
            return await provider.GetFavoritesAsync(limit, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new FavoritesResult(
                false,
                $"{provider.DisplayName} favorites unavailable.",
                new List<FavoriteItem>(),
                new List<FavoriteItem>(),
                new List<FavoriteItem>());
        }
    }
}
