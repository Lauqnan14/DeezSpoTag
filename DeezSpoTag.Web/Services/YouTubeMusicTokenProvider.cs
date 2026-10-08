using System;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations.YouTube;

namespace DeezSpoTag.Web.Services;

/// <summary>
/// Supplies a YouTube Music access token.
/// <para>
/// Extracted from PlaylistSyncService so the sync target can be constructed by DI rather than
/// handed the whole sync service, which would make the two mutually dependent. Only the refresh
/// token is persisted, so this runs once per sync rather than once per API call.
/// </para>
/// </summary>
public sealed class YouTubeMusicTokenProvider
{
    private readonly YouTubeDataApiClient? _client;
    private readonly PlatformAuthService _authService;

    public YouTubeMusicTokenProvider(
        YouTubeDataApiClient? client,
        PlatformAuthService authService)
    {
        _client = client;
        _authService = authService;
    }

    /// <summary>True when the platform has credentials configured at all.</summary>
    public async Task<bool> IsConfiguredAsync()
    {
        var state = await _authService.LoadAsync();
        var ytmusic = state.YTMusic;
        return ytmusic is not null
            && !string.IsNullOrWhiteSpace(ytmusic.ClientId)
            && !string.IsNullOrWhiteSpace(ytmusic.ClientSecret)
            && !string.IsNullOrWhiteSpace(ytmusic.RefreshToken);
    }

    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_client is null)
        {
            return null;
        }

        var state = await _authService.LoadAsync();
        var ytmusic = state.YTMusic;
        if (ytmusic is null
            || string.IsNullOrWhiteSpace(ytmusic.ClientId)
            || string.IsNullOrWhiteSpace(ytmusic.ClientSecret)
            || string.IsNullOrWhiteSpace(ytmusic.RefreshToken))
        {
            return null;
        }

        var token = await _client.RefreshAsync(
            ytmusic.TokenUrl ?? YouTubeDataApiClient.DefaultTokenUrl,
            ytmusic.ClientId,
            ytmusic.ClientSecret,
            ytmusic.RefreshToken,
            cancellationToken);
        return token.Success ? token.AccessToken : null;
    }
}
