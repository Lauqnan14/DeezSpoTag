using System.Collections.Concurrent;
using System.Security.Claims;

namespace DeezSpoTag.Web.Services;

public sealed class MediaServerLibraryPinUnlockService
{
    private static readonly TimeSpan UnlockLifetime = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _unlockedUntilByUser = new(StringComparer.Ordinal);

    public bool IsUnlocked(ClaimsPrincipal user)
    {
        var key = ResolveUserKey(user);
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        if (!_unlockedUntilByUser.TryGetValue(key, out var unlockedUntilUtc))
        {
            return false;
        }

        if (unlockedUntilUtc > DateTimeOffset.UtcNow)
        {
            return true;
        }

        _unlockedUntilByUser.TryRemove(key, out _);
        return false;
    }

    public void Unlock(ClaimsPrincipal user)
    {
        var key = ResolveUserKey(user);
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        _unlockedUntilByUser[key] = DateTimeOffset.UtcNow.Add(UnlockLifetime);
    }

    public void Lock(ClaimsPrincipal user)
    {
        var key = ResolveUserKey(user);
        if (!string.IsNullOrWhiteSpace(key))
        {
            _unlockedUntilByUser.TryRemove(key, out _);
        }
    }

    private static string ResolveUserKey(ClaimsPrincipal user)
    {
        return user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.Identity?.Name
            ?? string.Empty;
    }
}
