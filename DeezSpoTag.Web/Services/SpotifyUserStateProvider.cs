namespace DeezSpoTag.Web.Services;

public sealed class SpotifyUserStateProvider
{
    private readonly SpotifyUserAuthStore _userAuthStore;
    private readonly ISpotifyUserContextAccessor _userContext;

    public SpotifyUserStateProvider(
        SpotifyUserAuthStore userAuthStore,
        ISpotifyUserContextAccessor userContext)
    {
        _userAuthStore = userAuthStore;
        _userContext = userContext;
    }

    public async Task<SpotifyUserAuthState?> TryLoadActiveUserStateAsync()
    {
        var userId = _userContext.UserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        return await _userAuthStore.LoadAsync(userId);
    }
}
