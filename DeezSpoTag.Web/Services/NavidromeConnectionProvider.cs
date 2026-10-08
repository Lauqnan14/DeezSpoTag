using System;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations.Navidrome;

namespace DeezSpoTag.Web.Services;

/// <summary>
/// Resolves the stored Navidrome connection for a sync target. Exists so the target can be
/// constructed by DI without depending on PlaylistSyncService, which would be circular.
/// </summary>
public sealed class NavidromeConnectionProvider
{
    private readonly PlatformAuthService _authService;

    public NavidromeConnectionProvider(PlatformAuthService authService)
    {
        _authService = authService;
    }

    public async Task<NavidromeTargetConnection?> GetAsync(CancellationToken cancellationToken)
    {
        var state = await _authService.LoadAsync();
        var navidrome = state.Navidrome;
        if (navidrome is null
            || string.IsNullOrWhiteSpace(navidrome.Url)
            || string.IsNullOrWhiteSpace(navidrome.Username))
        {
            return null;
        }

        return new NavidromeTargetConnection(navidrome.Url, navidrome.Username, navidrome.Password ?? string.Empty);
    }
}
