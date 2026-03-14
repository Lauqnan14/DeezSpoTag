using System.Security.Claims;

namespace DeezSpoTag.Web.Services;

public interface ISpotifyUserContextAccessor
{
    string? UserId { get; }
}

public sealed class SpotifyUserContextAccessor : ISpotifyUserContextAccessor
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly bool _isSingleUserMode;

    public SpotifyUserContextAccessor(IHttpContextAccessor httpContextAccessor, IConfiguration configuration)
    {
        _httpContextAccessor = httpContextAccessor;
        _isSingleUserMode = configuration.GetValue<bool>("IsSingleUser", true);
    }

    public string? UserId
        => _isSingleUserMode
            ? "default"
            : _httpContextAccessor.HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier);
}
