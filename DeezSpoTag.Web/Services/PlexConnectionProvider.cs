using System;
using System.Threading;
using System.Threading.Tasks;

using DeezSpoTag.Integrations.Plex;

namespace DeezSpoTag.Web.Services;

/// <summary>
/// Resolves the stored Plex connection for a sync target.
/// <para>
/// Exists so <c>PlexPlaylistSyncTarget</c> can be constructed by DI without depending on
/// PlaylistSyncService, which would be circular: the service takes the engine, the engine takes
/// the registry, and the registry takes the targets.
/// </para>
/// </summary>
public sealed class PlexConnectionProvider
{
    private readonly PlatformAuthService _authService;

    public PlexConnectionProvider(PlatformAuthService authService)
    {
        _authService = authService;
    }

    public async Task<PlexTargetConnection?> GetAsync(CancellationToken cancellationToken)
    {
        var state = await _authService.LoadAsync();
        var plex = state.Plex;
        if (plex is null
            || string.IsNullOrWhiteSpace(plex.Url)
            || string.IsNullOrWhiteSpace(plex.Token)
            || string.IsNullOrWhiteSpace(plex.MachineIdentifier))
        {
            return null;
        }

        return new PlexTargetConnection(plex.Url, plex.Token, plex.MachineIdentifier);
    }
}
