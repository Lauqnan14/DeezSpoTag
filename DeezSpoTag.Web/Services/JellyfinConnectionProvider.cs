using System;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations.Jellyfin;

namespace DeezSpoTag.Web.Services;

/// <summary>
/// Resolves the stored Jellyfin connection for a sync target. Exists so the target can be
/// constructed by DI without depending on PlaylistSyncService, which would be circular.
/// </summary>
public sealed class JellyfinConnectionProvider
{
    private readonly PlatformAuthService _authService;

    public JellyfinConnectionProvider(PlatformAuthService authService)
    {
        _authService = authService;
    }

    public async Task<JellyfinTargetConnection?> GetAsync(CancellationToken cancellationToken)
    {
        var state = await _authService.LoadAsync();
        var jellyfin = state.Jellyfin;
        if (jellyfin is null
            || string.IsNullOrWhiteSpace(jellyfin.Url)
            || string.IsNullOrWhiteSpace(jellyfin.ApiKey)
            || string.IsNullOrWhiteSpace(jellyfin.UserId))
        {
            return null;
        }

        return new JellyfinTargetConnection(jellyfin.Url, jellyfin.ApiKey, jellyfin.UserId);
    }
}
