using System;
using System.Threading;
using System.Threading.Tasks;

using DeezSpoTag.Integrations.Apple;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Integrations.Qobuz;
using DeezSpoTag.Integrations.Spotify;
using DeezSpoTag.Integrations.Tidal;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Services.Utils;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Web.Services;

/// <summary>
/// The session each streaming playlist destination needs, taken from the credential pipeline the
/// app ALREADY has for that service.
/// <para>
/// This deliberately adds no new credential store and no new login. Each service is connected
/// somewhere in the app today - Spotify through the signed-in web-player blob, Deezer through the
/// ARL session, TIDAL through the refreshing token provider, Apple Music through the wrapper's auth
/// file, Qobuz through its stored account - and a playlist destination has to use the same
/// credential the rest of the app uses. A separate field would mean a user had to sign in twice,
/// and the two copies would drift: one refreshed, one silently stale.
/// </para>
/// <para>
/// Every method returns null when the app has no usable session, so the destination reports itself
/// as unconfigured instead of failing with an auth error the user cannot act on.
/// </para>
/// </summary>
public sealed class PlatformSyncConnectionProvider
{
    private readonly PlatformAuthService _authService;
    private readonly SpotifyPathfinderMetadataClient? _spotifyPathfinderClient;
    private readonly DeezerSessionManager? _deezerSessionManager;
    private readonly JwtTokenService? _jwtTokenService;
    private readonly ITidalAccessTokenProvider? _tidalAccessTokenProvider;
    private readonly ISettingsService? _settingsService;
    private readonly ILogger<PlatformSyncConnectionProvider>? _logger;

    public PlatformSyncConnectionProvider(
        PlatformAuthService authService,
        SpotifyPathfinderMetadataClient? spotifyPathfinderClient = null,
        DeezerSessionManager? deezerSessionManager = null,
        JwtTokenService? jwtTokenService = null,
        ITidalAccessTokenProvider? tidalAccessTokenProvider = null,
        ISettingsService? settingsService = null,
        ILogger<PlatformSyncConnectionProvider>? logger = null)
    {
        _authService = authService;
        _spotifyPathfinderClient = spotifyPathfinderClient;
        _deezerSessionManager = deezerSessionManager;
        _jwtTokenService = jwtTokenService;
        _tidalAccessTokenProvider = tidalAccessTokenProvider;
        _settingsService = settingsService;
        _logger = logger;
    }

    /// <summary>
    /// Whether the signed-in Spotify session can be used for playlist writing.
    /// <para>
    /// The readiness check deliberately asks the same question the writes ask, by going through
    /// <see cref="SpotifyPathfinderMetadataClient"/> rather than minting a Web API token directly.
    /// Those are not the same credential: the Web API token is only obtainable from a librespot
    /// blob, so an account signed in the ordinary way with an <c>sp_dc</c> web-player blob would
    /// report itself unconfigured here while its writes worked fine - or the reverse, which is worse.
    /// </para>
    /// </summary>
    public async Task<SpotifyTargetCredentials?> GetSpotifyAsync(CancellationToken cancellationToken)
    {
        if (_spotifyPathfinderClient is null)
        {
            return null;
        }

        try
        {
            SpotifyPathfinderMetadataClient.SpotifyWebPlayerSession? session =
                await _spotifyPathfinderClient.TryGetWebPlayerSessionAsync(cancellationToken).ConfigureAwait(false);
            return session is null ? null : new SpotifyTargetCredentials(session.AccessToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Resolving the Spotify session for playlist sync failed.");
            return null;
        }
    }

    /// <summary>
    /// A Deezer bearer token for playlist writing, obtained from the ARL the user signed in with.
    /// <para>
    /// The ARL is the credential the app already stores. It is not itself a bearer token, so it is
    /// exchanged for one through the same <see cref="JwtTokenService"/> the rest of the app already
    /// uses for Deezer's Pipe API - which caches and refreshes, so a scheduled pass does not depend
    /// on the user having logged in recently.
    /// </para>
    /// </summary>
    public async Task<DeezerTargetSession?> GetDeezerAsync(CancellationToken cancellationToken)
    {
        if (_deezerSessionManager is null || _jwtTokenService is null)
        {
            return null;
        }

        if (!_deezerSessionManager.LoggedIn)
        {
            return null;
        }

        // An access token already established at login is used as-is.
        if (!string.IsNullOrWhiteSpace(_deezerSessionManager.AccessToken))
        {
            return new DeezerTargetSession(
                _deezerSessionManager.AccessToken!,
                _deezerSessionManager.CurrentUser?.Id.ToString());
        }

        var arl = _deezerSessionManager.Arl;
        if (string.IsNullOrWhiteSpace(arl))
        {
            return null;
        }

        try
        {
            var bearer = await _jwtTokenService
                .GetJsonWebTokenAsync(arl, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(bearer)
                ? null
                : new DeezerTargetSession(bearer!, _deezerSessionManager.CurrentUser?.Id.ToString());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Exchanging the Deezer ARL for a playlist token failed.");
            return null;
        }
    }

    /// <summary>
    /// The Qobuz app id and user token, as stored by the existing Qobuz account connection. The
    /// download secret the app also keeps is for stream access and is not used for playlists.
    /// </summary>
    public async Task<QobuzTargetCredentials?> GetQobuzAsync(CancellationToken cancellationToken)
    {
        var qobuz = (await _authService.LoadAsync().ConfigureAwait(false)).Qobuz;
        return qobuz is null
               || string.IsNullOrWhiteSpace(qobuz.AppId)
               || string.IsNullOrWhiteSpace(qobuz.AuthToken)
            ? null
            : new QobuzTargetCredentials(qobuz.AppId, qobuz.AuthToken, qobuz.UserId);
    }

    /// <summary>
    /// A current TIDAL access token, from the provider the rest of the app already uses. That
    /// provider refreshes the stored token, which is what makes an unattended pass work: TIDAL
    /// tokens are short-lived, so reading the stored value directly fails every run that is not
    /// immediately after a manual login.
    /// </summary>
    public async Task<TidalTargetCredentials?> GetTidalAsync(CancellationToken cancellationToken)
    {
        if (_tidalAccessTokenProvider is null)
        {
            return null;
        }

        try
        {
            if (!await _tidalAccessTokenProvider.HasAuthenticatedSessionAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var accessToken = await _tidalAccessTokenProvider
                .GetAccessTokenAsync(cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return null;
            }

            var country = await _tidalAccessTokenProvider
                .GetCountryCodeAsync(cancellationToken)
                .ConfigureAwait(false);
            return new TidalTargetCredentials(accessToken, country);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Resolving a TIDAL access token for playlist sync failed.");
            return null;
        }
    }

    /// <summary>
    /// The Apple Music bearer token and Media-User-Token, from the settings service that already
    /// resolves them (including the migration from the older settings file into the wrapper's auth
    /// file). Reading them here rather than from the raw auth file keeps one definition of "the
    /// Apple Music token this app is using".
    /// </summary>
    public async Task<AppleTargetCredentials?> GetAppleAsync(CancellationToken cancellationToken)
    {
        if (_settingsService is null)
        {
            return null;
        }

        // LoadSettings is synchronous and already applies the Apple auth overlay, so the tokens it
        // returns are the same ones the rest of the app sees rather than a second read of the file.
        var settings = _settingsService.LoadSettings();
        var apple = settings.AppleMusic;
        if (apple is null
            || string.IsNullOrWhiteSpace(apple.AuthorizationToken)
            || string.IsNullOrWhiteSpace(apple.MediaUserToken))
        {
            return null;
        }

        return new AppleTargetCredentials(apple.AuthorizationToken, apple.MediaUserToken, null);
    }
}
