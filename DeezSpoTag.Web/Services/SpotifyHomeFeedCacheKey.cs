namespace DeezSpoTag.Web.Services;

internal static class SpotifyHomeFeedCacheKey
{
    public static async Task<string> ResolveAsync(
        string defaultCacheKey,
        ISpotifyUserContextAccessor userContext,
        SpotifyUserAuthStore userAuthStore,
        PlatformAuthService platformAuthService,
        Action<Exception>? onError = null)
    {
        try
        {
            var userId = userContext.UserId;
            if (!string.IsNullOrWhiteSpace(userId))
            {
                var userState = await userAuthStore.LoadAsync(userId);
                return BuildUserCacheKey(defaultCacheKey, userId, userState?.ActiveAccount);
            }

            var platformState = await platformAuthService.LoadAsync();
            var platformAccount = platformState.Spotify?.ActiveAccount;
            if (!string.IsNullOrWhiteSpace(platformAccount))
            {
                return BuildPlatformCacheKey(defaultCacheKey, platformAccount);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            onError?.Invoke(ex);
        }

        return defaultCacheKey;
    }

    public static string BuildUserCacheKey(string defaultCacheKey, string userId, string? activeAccount)
    {
        var sanitizedUser = Sanitize(defaultCacheKey, userId);
        return string.IsNullOrWhiteSpace(activeAccount)
            ? $"user_{sanitizedUser}"
            : $"user_{sanitizedUser}_{Sanitize(defaultCacheKey, activeAccount)}";
    }

    public static string BuildPlatformCacheKey(string defaultCacheKey, string platformAccount)
    {
        return $"platform_{Sanitize(defaultCacheKey, platformAccount)}";
    }

    public static string Sanitize(string defaultCacheKey, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultCacheKey;
        }

        var trimmed = value.Trim();
        var buffer = new char[trimmed.Length];
        var count = 0;
        foreach (var ch in trimmed.Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_'))
        {
            buffer[count++] = ch;
        }

        return count == 0 ? defaultCacheKey : new string(buffer, 0, count);
    }
}
