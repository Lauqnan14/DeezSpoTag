using DeezSpoTag.Integrations.Discogs;
using DeezSpoTag.Integrations.Jellyfin;
using DeezSpoTag.Integrations.Navidrome;
using DeezSpoTag.Integrations.Plex;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using DeezSpoTag.Integrations.Amazon;
using DeezSpoTag.Integrations.Qobuz;
using DeezSpoTag.Integrations.Tidal;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Integrations.YouTube;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.SoundCloud;
using DeezSpoTag.Services.Download.Soulseek;
using System.Security.Cryptography;
using DeezSpoTag.Services.Authentication;
using DeezSpoTag.Services.Utils;
using DeezSpoTag.Services.Download.Amazon;
using DeezSpoTag.Services.Download.Qobuz;
using DeezSpoTag.Services.Download.Tidal;
using DeezSpoTag.Web.Filters;

namespace DeezSpoTag.Web.Controllers.Api;

internal sealed record DeezerPlatformStatus(bool Configured, bool Live, string State);
internal sealed record BeatportPlatformStatus(
    bool Configured,
    bool Connected,
    string? ClientId,
    bool ClientSecretSaved,
    string? RedirectUri,
    string? Scope,
    DateTimeOffset? ExpiresAtUtc);

public sealed class PlatformAuthApiDependencies
{
    public required PlatformAuthService AuthService { get; init; }
    public required BoomplayMetadataService BoomplayMetadataService { get; init; }
    public required DiscogsApiClient DiscogsApiClient { get; init; }
    public required PlexApiClient PlexApiClient { get; init; }

    /// <summary>Optional so a deployment without YouTube Music configured still starts.</summary>
    public YouTubeDataApiClient? YouTubeDataApiClient { get; init; }
    public required JellyfinApiClient JellyfinApiClient { get; init; }
    public required NavidromeApiClient NavidromeApiClient { get; init; }
    public required AppleMusicWrapperService AppleWrapperService { get; init; }
    public required QobuzAccountProfileService QobuzAccountProfileService { get; init; }
    public required IAmazonPublicProviderRegistry AmazonPublicProviderRegistry { get; init; }
    public required IQobuzPublicProviderRegistry QobuzPublicProviderRegistry { get; init; }
    public required ITidalPublicProviderRegistry TidalPublicProviderRegistry { get; init; }
    public required IAmazonDownloadService AmazonDownloadService { get; init; }
    public required IQobuzDownloadService QobuzDownloadService { get; init; }
    public required TidalDownloadService TidalDownloadService { get; init; }
    public required ITidalAccessTokenProvider TidalAccessTokenProvider { get; init; }
    public required DeezSpoTag.Web.Services.SoulseekConnectionService SoulseekConnectionService { get; init; }

    /// <summary>
    ///     The verified-login authority the download path also uses.
    /// </summary>
    /// <remarks>
    ///     Optional so a host that registers only the web-tier probe still resolves this controller. When it
    ///     is absent nothing is invalidated on save or logout, which only costs a stale cache in a deployment
    ///     that has no download engine registered anyway.
    /// </remarks>
    public ISoulseekConnectionService? SoulseekEligibility { get; init; }

    /// <summary>
    ///     Optional so a deployment that has not registered the SoundCloud engine still starts; the
    ///     SoundCloud endpoints then report that the token could not be checked rather than the whole
    ///     controller failing to activate.
    /// </summary>
    public ISoundCloudClient? SoundCloudClient { get; init; }
    public required DeezerSessionManager DeezerSessionManager { get; init; }
    public required ILoginStorageService LoginStorage { get; init; }
    public required ILogger<PlatformAuthApiController> Logger { get; init; }
}

public sealed class BoomplayLoginRequest
{
    public string? Cookie { get; set; }
    public string? UserAgent { get; set; }
    public string? VerificationUrl { get; set; }
}

public sealed class AmazonMusicLoginRequest
{
    public string? Host { get; set; }
    public string? Locale { get; set; }
    public string? Cookie { get; set; }
}

[ApiController]
[LocalApiAuthorize]
[Route("api/platform-auth")]
[ApiTokenAwareValidateAntiforgery]
public class PlatformAuthApiController : ControllerBase
{
    private readonly PlatformAuthService _authService;
    private readonly BoomplayMetadataService _boomplayMetadataService;
    private readonly DiscogsApiClient _discogsApiClient;
    private readonly PlexApiClient _plexApiClient;

    /// <summary>Optional so a deployment without YouTube Music configured still starts.</summary>
    private readonly YouTubeDataApiClient? _youtubeDataApiClient;
    private readonly JellyfinApiClient _jellyfinApiClient;
    private readonly NavidromeApiClient _navidromeApiClient;
    private readonly AppleMusicWrapperService _appleWrapperService;
    private readonly QobuzAccountProfileService _qobuzAccountProfileService;
    private readonly IAmazonPublicProviderRegistry _amazonPublicProviderRegistry;
    private readonly IQobuzPublicProviderRegistry _qobuzPublicProviderRegistry;
    private readonly ITidalPublicProviderRegistry _tidalPublicProviderRegistry;
    private readonly IAmazonDownloadService _amazonDownloadService;
    private readonly IQobuzDownloadService _qobuzDownloadService;
    private readonly TidalDownloadService _tidalDownloadService;
    private readonly ITidalAccessTokenProvider _tidalAccessTokenProvider;
    private readonly DeezSpoTag.Web.Services.SoulseekConnectionService _soulseekConnectionService;
    private readonly ISoulseekConnectionService? _soulseekEligibility;
    private readonly ISoundCloudClient? _soundCloudClient;
    private readonly DeezerSessionManager _deezerSessionManager;
    private readonly ILoginStorageService _loginStorage;
    private readonly ILogger<PlatformAuthApiController> _logger;
    public PlatformAuthApiController(PlatformAuthApiDependencies dependencies)
    {
        _authService = dependencies.AuthService;
        _boomplayMetadataService = dependencies.BoomplayMetadataService;
        _discogsApiClient = dependencies.DiscogsApiClient;
        _plexApiClient = dependencies.PlexApiClient;
        _youtubeDataApiClient = dependencies.YouTubeDataApiClient;
        _jellyfinApiClient = dependencies.JellyfinApiClient;
        _navidromeApiClient = dependencies.NavidromeApiClient;
        _appleWrapperService = dependencies.AppleWrapperService;
        _qobuzAccountProfileService = dependencies.QobuzAccountProfileService;
        _amazonPublicProviderRegistry = dependencies.AmazonPublicProviderRegistry;
        _qobuzPublicProviderRegistry = dependencies.QobuzPublicProviderRegistry;
        _tidalPublicProviderRegistry = dependencies.TidalPublicProviderRegistry;
        _amazonDownloadService = dependencies.AmazonDownloadService;
        _qobuzDownloadService = dependencies.QobuzDownloadService;
        _tidalDownloadService = dependencies.TidalDownloadService;
        _tidalAccessTokenProvider = dependencies.TidalAccessTokenProvider;
        _soulseekConnectionService = dependencies.SoulseekConnectionService;
        _soulseekEligibility = dependencies.SoulseekEligibility;
        _soundCloudClient = dependencies.SoundCloudClient;
        _deezerSessionManager = dependencies.DeezerSessionManager;
        _loginStorage = dependencies.LoginStorage;
        _logger = dependencies.Logger;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var gate = EnsureAccess();
        if (gate != null)
        {
            return gate;
        }

        var platformAuthTask = _authService.LoadAsync();
        var deezerLoginTask = _loginStorage.LoadLoginCredentialsAsync();
        await Task.WhenAll(platformAuthTask, deezerLoginTask);
        var state = await platformAuthTask;
        var deezerLogin = await deezerLoginTask;
        return Ok(new
        {
            spotify = state.Spotify is null ? null : new { state.Spotify.ActiveAccount, accounts = state.Spotify.Accounts?.Select(a => new { a.Name, a.Region, a.CreatedAt, a.UpdatedAt }) },
            spotifyConnected = HasSpotifyRuntimeCredentials(state.Spotify),
            deezer = ToPublicDeezer(deezerLogin, _deezerSessionManager),
            discogs = state.Discogs is null ? null : new { state.Discogs.Username, state.Discogs.AvatarUrl, state.Discogs.Location, tokenSaved = !string.IsNullOrWhiteSpace(state.Discogs.Token) },
            lastFm = ToPublicLastFm(state.LastFm),
            bpmSupreme = state.BpmSupreme is null ? null : new { state.BpmSupreme.Email, state.BpmSupreme.Library, passwordSaved = !string.IsNullOrWhiteSpace(state.BpmSupreme.Password) },
            plex = state.Plex is null ? null : new { state.Plex.Url, state.Plex.ServerName, state.Plex.MachineIdentifier, state.Plex.Version, state.Plex.Username, state.Plex.AvatarUrl, tokenSaved = !string.IsNullOrWhiteSpace(state.Plex.Token) },
            jellyfin = state.Jellyfin is null ? null : new { state.Jellyfin.Url, state.Jellyfin.Username, state.Jellyfin.UserId, state.Jellyfin.ServerName, state.Jellyfin.Version, state.Jellyfin.AvatarUrl, apiKeySaved = !string.IsNullOrWhiteSpace(state.Jellyfin.ApiKey) },
            navidrome = ToPublicNavidrome(state.Navidrome),
            ytmusic = ToPublicYTMusic(state.YTMusic),
            appleMusic = state.AppleMusic is null ? null : new { state.AppleMusic.Email, mediaUserTokenSaved = !string.IsNullOrWhiteSpace(state.AppleMusic.MediaUserToken), authorizationTokenSaved = !string.IsNullOrWhiteSpace(state.AppleMusic.AuthorizationToken), state.AppleMusic.WrapperReady, state.AppleMusic.WrapperLoggedInAt },
            qobuz = ToPublicQobuz(state.Qobuz),
            tidal = ToPublicTidal(state.Tidal),
            amazonMusic = ToPublicAmazonMusic(state.AmazonMusic),
            soulseek = ToPublicSoulseek(state.Soulseek),
            soundcloud = ToPublicSoundCloud(state.SoundCloud),
            boomplay = ToPublicBoomplay(state.Boomplay),
            beatport = ToPublicBeatport(state.Beatport)
        });
    }

    internal static DeezerPlatformStatus ToPublicDeezer(
        LoginData? login,
        DeezerSessionManager sessionManager)
    {
        var normalizedArl = DeezerAuthUtils.NormalizeArl(login?.Arl);
        var configured = login?.User is not null && DeezerAuthUtils.IsValidArlLength(normalizedArl);
        var live = sessionManager.LoggedIn && sessionManager.CurrentUser is not null;
        var state = live
            ? "connected"
            : !configured
                ? "disconnected"
                : sessionManager.ConnectionState switch
                {
                    DeezerConnectionState.Failed => "failed",
                    DeezerConnectionState.Connected => "failed",
                    _ => "authenticating"
                };

        return new DeezerPlatformStatus(configured, live, state);
    }

    [HttpGet("amazonmusic/providers")]
    public async Task<IActionResult> GetAmazonMusicProviders(CancellationToken cancellationToken)
    {
        var gate = EnsureAccess();
        if (gate != null) return gate;
        return Ok(await GetPublicAmazonProvidersAsync(cancellationToken, liveSession: false));
    }

    [HttpPut("amazonmusic/providers/{providerId}/enabled")]
    public async Task<IActionResult> SetAmazonMusicProviderEnabled(
        string providerId,
        [FromBody] AmazonProviderEnabledRequest request,
        CancellationToken cancellationToken)
    {
        var gate = EnsureAccess();
        if (gate != null) return gate;
        if (request.Enabled is not { } enabled)
        {
            return BadRequest("Enabled is required.");
        }

        var updated = await _amazonPublicProviderRegistry.SetEnabledAsync(providerId, enabled, cancellationToken);
        return updated is null ? NotFound("Unknown Amazon provider.") : Ok(ToPublicAmazonProvider(updated));
    }

    [ValidateAntiForgeryToken]
    [HttpPost("amazonmusic/providers/check")]
    public async Task<IActionResult> CheckAmazonMusicProviders(CancellationToken cancellationToken)
    {
        var gate = EnsureAccess();
        if (gate != null) return gate;
        await RunProviderStageAsync(
            AmazonSource,
            token => _amazonPublicProviderRegistry.CheckEnabledProvidersAsync(token),
            cancellationToken);
        return Ok(await GetPublicAmazonProvidersAsync(cancellationToken, liveSession: true));
    }

    private static bool HasSpotifyRuntimeCredentials(SpotifyConfig? spotify)
    {
        if (spotify?.Accounts is not { Count: > 0 })
        {
            return false;
        }

        var active = spotify.Accounts.FirstOrDefault(account =>
            string.Equals(account.Name, spotify.ActiveAccount, StringComparison.OrdinalIgnoreCase));
        active ??= spotify.Accounts.FirstOrDefault();
        if (active is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(active.LibrespotBlobPath))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(active.BlobPath)
            && !active.BlobPath.EndsWith(".web.json", StringComparison.OrdinalIgnoreCase);
    }

    [HttpGet("tidal/providers")]
    public async Task<IActionResult> GetTidalProviders(CancellationToken cancellationToken)
    {
        var gate = EnsureAccess();
        if (gate != null) return gate;
        return Ok(await GetPublicTidalProvidersAsync(cancellationToken, liveSession: false));
    }

    [HttpPut("tidal/providers/{providerId}/enabled")]
    public async Task<IActionResult> SetTidalProviderEnabled(string providerId, [FromBody] TidalProviderEnabledRequest request, CancellationToken cancellationToken)
    {
        var gate = EnsureAccess();
        if (gate != null) return gate;
        if (request.Enabled is not { } enabled)
        {
            return BadRequest("Enabled is required.");
        }

        var updated = await _tidalPublicProviderRegistry.SetEnabledAsync(providerId, enabled, cancellationToken);
        if (updated is not null && _tidalPublicProviderRegistry is TidalPublicProviderRegistry registry)
        {
            registry.NotifyProviderManuallyResolved(providerId);
        }

        return updated is null ? NotFound("Unknown Tidal provider.") : Ok(ToPublicTidalProvider(updated));
    }

    [ValidateAntiForgeryToken]
    [HttpPost("tidal/providers/check")]
    public async Task<IActionResult> CheckTidalProviders(CancellationToken cancellationToken)
    {
        var gate = EnsureAccess();
        if (gate != null) return gate;
        await RunProviderStageAsync(
            TidalSource,
            token => _tidalPublicProviderRegistry.CheckEnabledProvidersAsync(token),
            cancellationToken);
        return Ok(await GetPublicTidalProvidersAsync(cancellationToken, liveSession: true));
    }

    [HttpGet("qobuz/providers")]
    public async Task<IActionResult> GetQobuzProviders(CancellationToken cancellationToken)
    {
        var gate = EnsureAccess();
        if (gate != null) return gate;
        return Ok(await GetPublicQobuzProvidersAsync(cancellationToken, liveSession: false));
    }

    [HttpPut("qobuz/providers/{providerId}/enabled")]
    public async Task<IActionResult> SetQobuzProviderEnabled(
        string providerId,
        [FromBody] QobuzProviderEnabledRequest request,
        CancellationToken cancellationToken)
    {
        var gate = EnsureAccess();
        if (gate != null) return gate;
        if (request.Enabled is not { } enabled)
        {
            return BadRequest("Enabled is required.");
        }

        var updated = await _qobuzPublicProviderRegistry.SetEnabledAsync(providerId, enabled, cancellationToken);
        return updated is null ? NotFound("Unknown Qobuz provider.") : Ok(ToPublicProvider(updated));
    }

    [ValidateAntiForgeryToken]
    [HttpPost("qobuz/providers/check")]
    public async Task<IActionResult> CheckQobuzProviders(CancellationToken cancellationToken)
    {
        var gate = EnsureAccess();
        if (gate != null) return gate;
        await RunProviderStageAsync(
            QobuzSource,
            token => _qobuzPublicProviderRegistry.CheckEnabledProvidersAsync(token),
            cancellationToken);
        return Ok(await GetPublicQobuzProvidersAsync(cancellationToken, liveSession: true));
    }

    [HttpGet("qobuz/account")]
    public async Task<IActionResult> GetQobuzAccount(CancellationToken cancellationToken)
    {
        var gate = EnsureAccess();
        if (gate != null) return gate;
        var state = await RefreshQobuzAccountAsync(await _authService.LoadAsync(), cancellationToken);
        return Ok(ToPublicQobuz(state.Qobuz));
    }

    /// <summary>
    ///     Checks the saved SoundCloud token without changing what is stored.
    /// </summary>
    /// <remarks>
    ///     Present as its own verb so the login UI can re-verify a saved token with an empty body. A blank
    ///     submission is a check, never a clear: only the disconnect endpoint removes the token.
    /// </remarks>
    [HttpPost("soundcloud/check")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CheckSoundCloud(CancellationToken cancellationToken)
    {
        var gate = EnsureAccess();
        if (gate != null) return gate;

        var state = await _authService.LoadAsync();
        var saved = state.SoundCloud;

        if (_soundCloudClient is null || saved is null || string.IsNullOrWhiteSpace(saved.OAuthToken))
        {
            return Ok(new { tokenChecked = false, soundcloud = ToPublicSoundCloud(saved) });
        }

        var valid = await _soundCloudClient.ValidateCredentialsAsync(saved.OAuthToken, cancellationToken);

        var refreshed = await _authService.UpdateAsync(current =>
        {
            current.SoundCloud = new SoundCloudAuth
            {
                OAuthToken = saved.OAuthToken,
                CredentialsValid = valid,
                LastStatus = valid ? "connected" : "invalid_token",
                LastError = valid ? null : "SoundCloud did not accept the saved token.",
                CheckedAt = DateTimeOffset.UtcNow
            };
            return current.SoundCloud;
        });

        return Ok(new { tokenChecked = true, soundcloud = ToPublicSoundCloud(refreshed) });
    }

    [HttpGet("soulseek/connection")]
    public async Task<IActionResult> GetSoulseekConnection(CancellationToken cancellationToken)
    {
        var gate = EnsureAccess();
        if (gate != null) return gate;
        var state = await _authService.LoadAsync();

        // One probe, one answer. This used to run the web-tier check, persist it, and then run the
        // service-layer probe as well - two round trips to slskd for the same question, free to disagree, and
        // the page could render "Connected" while the API was already refusing work. The service-layer probe is
        // the authority the download path admits on, so it is the one that runs here, and its result is what
        // gets recorded and what gets returned.
        var eligibility = _soulseekEligibility is null
            ? null
            : await _soulseekEligibility.GetEligibilityAsync(cancellationToken).ConfigureAwait(false);

        if (eligibility is not null)
        {
            state = await PersistSoulseekStatusAsync(eligibility, cancellationToken);
        }

        return Ok(ToPublicSoulseek(state.Soulseek, eligibility));
    }

    /// <summary>
    ///     Writes an already-probed Soulseek result into the stored record.
    /// </summary>
    /// <remarks>
    ///     It takes the status as an argument rather than probing for itself. Probing here as well as at the
    ///     caller meant two independent round trips to slskd for one question, and the two answers could differ:
    ///     the page rendered "Connected" from one while the API admitted new work on the other. The single
    ///     service-layer probe now answers once and this only records it.
    /// </remarks>
    private async Task<PlatformAuthState> PersistSoulseekStatusAsync(
        SoulseekConnectionStatus status,
        CancellationToken cancellationToken)
    {
        var state = await _authService.LoadAsync().ConfigureAwait(false);
        if (state.Soulseek is null || string.IsNullOrWhiteSpace(state.Soulseek.BaseUrl))
        {
            return state;
        }

        return await _authService.UpdateAsync(current =>
        {
            if (current.Soulseek is null)
            {
                return current;
            }

            current.Soulseek.ConnectionValid = status.IsUsable;
            current.Soulseek.Username = status.Username ?? current.Soulseek.Username;
            current.Soulseek.LastStatus = status.State.ToString().ToLowerInvariant();
            current.Soulseek.LastError = status.IsUsable ? null : status.Message;
            if (status.CheckedAtUtc is not null)
            {
                current.Soulseek.CheckedAt = status.CheckedAtUtc;
            }

            return current;
        });
    }

    /// <summary>
    ///     Re-checks the saved SoundCloud token against SoundCloud and returns the redacted state.
    /// </summary>
    /// <remarks>
    ///     A read-only check, so a blank token in the body preserves what is saved rather than clearing it.
    /// </remarks>
    [HttpPost("soundcloud")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSoundCloud(
        [FromBody] SoundCloudAuth request,
        CancellationToken cancellationToken)
    {
        var gate = EnsureAccess();
        if (gate != null) return gate;
        if (request is null) return BadRequest("A SoundCloud token is required.");

        var currentState = await _authService.LoadAsync();
        var previous = currentState.SoundCloud;

        // A blank submission is a status check, not a disconnect. Clearing the token is the explicit
        // disconnect endpoint's job, so this cannot silently undo a working saved token.
        var token = ResolveSubmittedSecret(request.OAuthToken, previous?.OAuthToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return BadRequest("A SoundCloud token is required.");
        }

        if (_soundCloudClient is null)
        {
            return StatusCode(503, new
            {
                saved = false,
                error = "The SoundCloud engine is not available in this deployment."
            });
        }

        var valid = await _soundCloudClient.ValidateCredentialsAsync(token, cancellationToken);

        var candidate = new SoundCloudAuth
        {
            OAuthToken = token,
            CredentialsValid = valid,
            LastStatus = valid ? "connected" : "invalid_token",
            LastError = valid ? null : "SoundCloud did not accept this token.",
            CheckedAt = DateTimeOffset.UtcNow
        };

        var soundCloud = await _authService.UpdateAsync(state =>
        {
            state.SoundCloud = candidate;
            return state.SoundCloud;
        });

        if (!valid)
        {
            // Reported as a rejection rather than a save, so the UI can keep the user on the form instead of
            // showing a stored token that does not work.
            return BadRequest(new
            {
                saved = false,
                soundcloud = ToPublicSoundCloud(soundCloud),
                error = "SoundCloud did not accept this token."
            });
        }

        return Ok(new { saved = true, soundcloud = ToPublicSoundCloud(soundCloud) });
    }

    /// <summary>
    ///     Clears the saved SoundCloud token.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         SoundCloud has no token revocation endpoint that a third-party app can call the way YouTube's
    ///         does, so this removes the stored copy and nothing else. The token stays valid on SoundCloud's
    ///         side until it expires or the user revokes it in their own account settings, which is why the
    ///         login tab says so.
    ///     </para>
    ///     <para>
    ///         Clearing is required rather than optional because the credential provider sends any non-empty
    ///         token as a cookie on every page request. A stale token that is merely marked disconnected would
    ///         still be sent, degrading every public download to whatever SoundCloud returns for it.
    ///     </para>
    /// </remarks>
    [HttpPost("soundcloud/disconnect")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DisconnectSoundCloud(CancellationToken cancellationToken)
    {
        var gate = EnsureAccess();
        if (gate != null) return gate;

        var hadToken = !string.IsNullOrWhiteSpace((await _authService.LoadAsync()).SoundCloud?.OAuthToken);

        await _authService.UpdateAsync(next =>
        {
            next.SoundCloud = null;
            return next.SoundCloud;
        });

        return Ok(new
        {
            // A token that was never saved still reports true: the caller's intent was "there must be no
            // token now", and that intent is satisfied. Reporting false would imply something was cleared
            // when nothing was, which reads as a failure in the sidebar.
            disconnected = true,
            hadToken,
            soundcloud = ToPublicSoundCloud(null)
        });
    }

    [HttpGet("soundcloud/connection")]
    public async Task<IActionResult> GetSoundCloudConnection(CancellationToken cancellationToken)
    {
        var gate = EnsureAccess();
        if (gate != null) return gate;

        var state = await _authService.LoadAsync();
        var soundCloud = state.SoundCloud;

        if (_soundCloudClient is null || soundCloud is null || string.IsNullOrWhiteSpace(soundCloud.OAuthToken))
        {
            return Ok(ToPublicSoundCloud(soundCloud));
        }

        var valid = await _soundCloudClient.ValidateCredentialsAsync(
            soundCloud.OAuthToken,
            cancellationToken);

        var refreshed = await _authService.UpdateAsync(current =>
        {
            current.SoundCloud = new SoundCloudAuth
            {
                OAuthToken = soundCloud.OAuthToken,
                CredentialsValid = valid,
                LastStatus = valid ? "connected" : "invalid_token",
                LastError = valid ? null : "SoundCloud did not accept the saved token.",
                CheckedAt = DateTimeOffset.UtcNow
            };
            return current.SoundCloud;
        });

        return Ok(ToPublicSoundCloud(refreshed));
    }

    [HttpGet("public-providers/status")]
    public async Task<IActionResult> GetPublicProviderStatus(
        [FromQuery] bool check,
        CancellationToken cancellationToken)
    {
        var gate = EnsureAccess();
        if (gate != null) return gate;

        if (check)
        {
            await Task.WhenAll(
                RunProviderStageAsync(
                    QobuzSource,
                    token => _qobuzPublicProviderRegistry.CheckEnabledProvidersAsync(token),
                    cancellationToken),
                RunProviderStageAsync(
                    AmazonSource,
                    token => _amazonPublicProviderRegistry.CheckEnabledProvidersAsync(token),
                    cancellationToken),
                RunProviderStageAsync(
                    TidalSource,
                    token => _tidalPublicProviderRegistry.CheckEnabledProvidersAsync(token),
                    cancellationToken));
        }

        var qobuzTask = ResolveProviderStatusAsync(
            QobuzSource,
            token => GetPublicQobuzProvidersAsync(token, liveSession: check),
            summary => (summary.Status, summary.OnlineCount),
            cancellationToken);
        var amazonTask = ResolveProviderStatusAsync(
            AmazonSource,
            token => GetPublicAmazonProvidersAsync(token, liveSession: check),
            summary => (summary.Status, summary.OnlineCount),
            cancellationToken);
        var tidalTask = ResolveProviderStatusAsync(
            TidalSource,
            token => GetPublicTidalProvidersAsync(token, liveSession: check),
            summary => (summary.Status, summary.OnlineCount),
            cancellationToken);

        await Task.WhenAll(qobuzTask, amazonTask, tidalTask);
        var qobuz = qobuzTask.Result;
        var amazon = amazonTask.Result;
        var tidal = tidalTask.Result;
        return Ok(new
        {
            qobuz = new { status = qobuz.Status, onlineCount = qobuz.OnlineCount },
            amazonMusic = new { status = amazon.Status, onlineCount = amazon.OnlineCount },
            tidal = new { status = tidal.Status, onlineCount = tidal.OnlineCount }
        });
    }

    [ValidateAntiForgeryToken]
    [HttpPost("spotify")]
    public IActionResult SaveSpotify()
    {
        var gate = EnsureAccess();
        if (gate != null)
        {
            return gate;
        }

        return BadRequest("Spotify credentials are managed via /api/spotify-credentials.");
    }

    [ValidateAntiForgeryToken]
    [HttpPost("discogs")]
    public async Task<IActionResult> SaveDiscogs([FromBody] DiscogsAuth request, CancellationToken cancellationToken)
    {
        var gate = EnsureAccess();
        if (gate != null)
        {
            return gate;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Token))
        {
            return BadRequest("Discogs token is required.");
        }

        var identity = await _discogsApiClient.GetIdentityAsync(request.Token, cancellationToken);
        if (identity == null)
        {
            return BadRequest("Discogs token is invalid or unauthorized.");
        }
        var discogs = await _authService.UpdateAsync(state =>
        {
            state.Discogs = new DiscogsAuth
            {
                Token = request.Token,
                Username = identity.Username,
                AvatarUrl = identity.AvatarUrl,
                Location = identity.Location
            };

            return state.Discogs;
        });
        return Ok(new { saved = true, discogs });
    }

    [ValidateAntiForgeryToken]
    [HttpPost("qobuz")]
    public async Task<IActionResult> SaveQobuz([FromBody] QobuzAuth request, CancellationToken cancellationToken)
    {
        var gate = EnsureAccess();
        if (gate != null)
        {
            return gate;
        }

        if (request is null)
        {
            return BadRequest("Qobuz credentials are required.");
        }

        var existingState = await _authService.LoadAsync();
        var authToken = string.IsNullOrWhiteSpace(request.AuthToken)
            ? existingState.Qobuz?.AuthToken
            : request.AuthToken.Trim();
        var submittedAppSecret = string.IsNullOrWhiteSpace(request.AppSecret)
            ? request.DownloadSecret
            : request.AppSecret;
        var existingAppSecret = string.IsNullOrWhiteSpace(existingState.Qobuz?.AppSecret)
            ? existingState.Qobuz?.DownloadSecret
            : existingState.Qobuz.AppSecret;
        var appSecret = string.IsNullOrWhiteSpace(submittedAppSecret)
            ? existingAppSecret
            : submittedAppSecret.Trim();
        if (string.IsNullOrWhiteSpace(appSecret))
        {
            return BadRequest("Qobuz App Secret is required.");
        }
        if (string.IsNullOrWhiteSpace(authToken))
        {
            return BadRequest("Qobuz User Auth Token is required.");
        }

        var appId = string.IsNullOrWhiteSpace(request.AppId) ? "712109809" : request.AppId.Trim();
        var accountResult = await _qobuzAccountProfileService.FetchAsync(appId, authToken, cancellationToken);
        if (accountResult.Status == QobuzAccountProfileStatus.InvalidToken)
        {
            return BadRequest(accountResult.Error ?? "Qobuz User Auth Token is invalid.");
        }
        if (accountResult.Status == QobuzAccountProfileStatus.Unavailable)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                accountResult.Error ?? "Qobuz account lookup is unavailable.");
        }

        var qobuz = await _authService.UpdateAsync(state =>
        {
            state.Qobuz = new QobuzAuth
            {
                AppId = appId,
                AuthToken = authToken,
                AppSecret = appSecret,
                UserId = accountResult?.Profile?.UserId,
                DisplayName = accountResult?.Profile?.DisplayName,
                Country = accountResult?.Profile?.Country,
                Zone = accountResult?.Profile?.Zone,
                CredentialLabel = accountResult?.Profile?.CredentialLabel,
                SubscriptionOffer = accountResult?.Profile?.SubscriptionOffer,
                AuthTokenValid = true,
                AccountRefreshedAt = DateTimeOffset.UtcNow,
                DownloadSecret = null
            };
            return state.Qobuz;
        });

        return Ok(new { saved = true, qobuz = ToPublicQobuz(qobuz) });
    }

    [ValidateAntiForgeryToken]
    [HttpPost("tidal")]
    public async Task<IActionResult> SaveTidal([FromBody] TidalAuth request, CancellationToken cancellationToken)
    {
        var gate = EnsureAccess();
        if (gate != null) return gate;
        if (request is null) return BadRequest("Tidal credentials are required.");

        var currentState = await _authService.LoadAsync();
        var previous = currentState.Tidal;
        var clientId = ResolveSubmittedSecret(request.ClientId, previous?.ClientId);
        var clientSecret = ResolveSubmittedSecret(request.ClientSecret, previous?.ClientSecret);
        var accessToken = ResolveSubmittedSecret(request.AccessToken, previous?.AccessToken);
        var refreshToken = ResolveSubmittedSecret(request.RefreshToken, previous?.RefreshToken);
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            return BadRequest("Tidal Client ID and Client Secret are required.");
        }

        var tidal = new TidalAuth
        {
            ClientId = clientId,
            ClientSecret = clientSecret,
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            UserId = request.UserId?.Trim(),
            CountryCode = NormalizeCountryCode(request.CountryCode),
            CredentialsValid = false
        };
        await _authService.UpdateAsync(state => state.Tidal = tidal);
        _tidalAccessTokenProvider.Invalidate();
        try
        {
            if (!await _tidalAccessTokenProvider.ValidateCredentialsAsync(cancellationToken))
            {
                throw new InvalidOperationException("Tidal API validation failed.");
            }
            tidal.CredentialsValid = true;
            tidal.ValidatedAt = DateTimeOffset.UtcNow;
            await _authService.UpdateAsync(state => state.Tidal = tidal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _authService.UpdateAsync(state => state.Tidal = previous);
            _tidalAccessTokenProvider.Invalidate();
            return BadRequest($"Tidal credentials were rejected: {ex.Message}");
        }

        return Ok(new { saved = true, tidal = ToPublicTidal(tidal) });
    }

    [ValidateAntiForgeryToken]
    [HttpPost("soulseek")]
    public async Task<IActionResult> SaveSoulseek([FromBody] SoulseekAuth request, CancellationToken cancellationToken)
    {
        var gate = EnsureAccess();
        if (gate != null) return gate;
        if (request is null) return BadRequest("Soulseek connection details are required.");

        var currentState = await _authService.LoadAsync();
        var previous = currentState.Soulseek;
        var baseUrl = request.BaseUrl?.Trim();
        var apiKey = ResolveSubmittedSecret(request.ApiKey, previous?.ApiKey);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return BadRequest("slskd URL is required.");
        }

        if (DeezSpoTag.Web.Services.SoulseekConnectionService.NormalizeBaseUri(baseUrl) is null)
        {
            return BadRequest("slskd URL is invalid.");
        }

        var candidate = new SoulseekAuth
        {
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            ConnectionValid = false,
            LastStatus = "checking",
            CheckedAt = DateTimeOffset.UtcNow
        };

        var check = await _soulseekConnectionService.CheckAsync(candidate, cancellationToken);
        candidate.ConnectionValid = check.Connected;
        candidate.Username = check.Username;
        candidate.LastStatus = check.Status;
        candidate.LastError = check.Connected ? null : check.Message;
        candidate.CheckedAt = check.CheckedAt;

        var soulseek = await _authService.UpdateAsync(state =>
        {
            state.Soulseek = candidate;
            return state.Soulseek;
        });

        // The cached answer was taken against the previous details, so it no longer describes this
        // installation. Dropping it is what stops a source that was active a moment ago staying active for
        // the length of the cache window after the reader pointed it somewhere else - or nowhere.
        InvalidateSoulseekEligibility();

        return Ok(new { saved = true, soulseek = ToPublicSoulseek(soulseek) });
    }

    [ValidateAntiForgeryToken]
    [HttpPost("boomplay")]
    public async Task<IActionResult> SaveBoomplay(
        [FromBody] BoomplayLoginRequest request,
        CancellationToken cancellationToken)
    {
        var gate = EnsureAccess();
        if (gate != null) return gate;
        if (request is null) return BadRequest("Boomplay session details are required.");

        var currentState = await _authService.LoadAsync();
        var previous = currentState.Boomplay;
        var existingCookie = string.Empty;
        var keepExisting = string.IsNullOrWhiteSpace(request.Cookie)
                           && BoomplaySessionCookie.TryNormalize(previous?.Cookie, out existingCookie);
        if (!keepExisting && !BoomplaySessionCookie.TryNormalize(request.Cookie, out existingCookie))
        {
            return BadRequest(new
            {
                error = BoomplayFailureCodes.SessionMissing,
                message = "Paste the Boomplay sessionID cookie. In Chrome: open boomplay.com while "
                          + "logged in, then DevTools > Application > Cookies > sessionID, and copy "
                          + "either the value on its own or the full cookie header."
            });
        }

        // A cookie that parses but carries no sessionID can never authenticate, so say which
        // cookie is missing instead of letting the request fail later as an opaque API error.
        if (!BoomplaySessionCookie.TryExtractSessionId(existingCookie, out _))
        {
            return BadRequest(new
            {
                error = BoomplayFailureCodes.SessionMissing,
                message = "That cookie has no sessionID. Boomplay only authenticates the sessionID "
                          + "cookie; copy it from DevTools > Application > Cookies on boomplay.com."
            });
        }

        var requestedUserAgent = string.IsNullOrWhiteSpace(request.UserAgent)
            ? previous?.UserAgent
            : request.UserAgent;
        if (!BoomplaySessionCookie.TryNormalizeUserAgent(requestedUserAgent, out var userAgent))
        {
            return BadRequest("Boomplay browser user agent is required.");
        }

        // The account check runs against Boomplay's Cloudflare-free mobile API and needs no
        // verification URL: a saved cookie is only useful if Boomplay still honours the
        // sessionID in it, so an unverified cookie is rejected rather than stored.
        var validation = await _boomplayMetadataService.ValidateSessionAsync(
            existingCookie,
            userAgent,
            request.VerificationUrl,
            cancellationToken);
        if (!validation.Success)
        {
            return BadRequest(new
            {
                error = validation.FailureCode,
                message = validation.FailureCode switch
                {
                    BoomplayFailureCodes.SessionChallenged =>
                        "Boomplay challenged this browser session. Copy a fresh cookie and try again.",
                    BoomplayFailureCodes.SessionMissing =>
                        "Boomplay did not accept this session. Copy the sessionID cookie from a logged-in boomplay.com tab and try again.",
                    _ =>
                        "Boomplay could not verify this session. Try again in a moment."
                }
            });
        }

        const string lastStatus = "session_verified";

        var boomplay = await _authService.UpdateAsync(state =>
        {
            state.Boomplay = new BoomplayAuth
            {
                Cookie = existingCookie,
                UserAgent = userAgent,
                SessionValid = true,
                LastStatus = lastStatus,
                SavedAt = DateTimeOffset.UtcNow
            };

            return state.Boomplay;
        });

        return Ok(new { saved = true, boomplay = ToPublicBoomplay(boomplay) });
    }

    [ValidateAntiForgeryToken]
    [HttpPost("amazonmusic")]
    public async Task<IActionResult> SaveAmazonMusic([FromBody] AmazonMusicLoginRequest request, CancellationToken cancellationToken)
    {
        var gate = EnsureAccess();
        if (gate != null) return gate;
        if (request is null) return BadRequest("Amazon Music session details are required.");

        var currentState = await _authService.LoadAsync();
        var previous = currentState.AmazonMusic;
        var host = NormalizeAmazonHost(request.Host);
        var locale = string.IsNullOrWhiteSpace(request.Locale) ? previous?.Locale : request.Locale.Trim();
        var cookie = ResolveSubmittedSecret(request.Cookie, previous?.Cookie);

        var amazonMusic = await _authService.UpdateAsync(state =>
        {
            state.AmazonMusic = new AmazonMusicAuth
            {
                Host = host,
                Locale = string.IsNullOrWhiteSpace(locale) ? "en_US" : locale,
                Cookie = cookie,
                SavedAt = DateTimeOffset.UtcNow
            };

            return state.AmazonMusic;
        });

        return Ok(new { saved = true, amazonMusic = ToPublicAmazonMusic(amazonMusic) });
    }

    [ValidateAntiForgeryToken]
    [HttpPost("lastfm")]
    public async Task<IActionResult> SaveLastFm([FromBody] LastFmAuth request)
    {
        var gate = EnsureAccess();
        if (gate != null)
        {
            return gate;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.ApiKey))
        {
            return BadRequest("Last.fm API key is required.");
        }

        var lastFm = await _authService.UpdateAsync(state =>
        {
            state.LastFm = new LastFmAuth
            {
                ApiKey = request.ApiKey,
                Username = request.Username
            };

            return ToPublicLastFm(state.LastFm);
        });
        return Ok(new { saved = true, lastFm });
    }

    [ValidateAntiForgeryToken]
    [HttpPost("bpmsupreme")]
    public async Task<IActionResult> SaveBpmSupreme([FromBody] BpmSupremeAuth request)
    {
        var gate = EnsureAccess();
        if (gate != null)
        {
            return gate;
        }

        await _authService.UpdateAsync(state =>
        {
            state.BpmSupreme = request;
            return 0;
        });
        return Ok(new { saved = true });
    }

    [ValidateAntiForgeryToken]
    [HttpPost("plex")]
    public async Task<IActionResult> SavePlex([FromBody] PlexAuth request)
    {
        var gate = EnsureAccess();
        if (gate != null)
        {
            return gate;
        }

        await _authService.UpdateAsync(state =>
        {
            state.Plex = request;
            return 0;
        });
        return Ok(new { saved = true });
    }

    [ValidateAntiForgeryToken]
    [HttpPost("plex/login")]
    public async Task<IActionResult> LoginPlex([FromBody] PlexAuth request, CancellationToken cancellationToken)
    {
        var gate = EnsureAccess();
        if (gate != null)
        {
            return gate;
        }

        if (string.IsNullOrWhiteSpace(request.Url) || string.IsNullOrWhiteSpace(request.Token))
        {
            return BadRequest("Plex URL and token are required.");
        }

        var identity = await _plexApiClient.GetIdentityAsync(request.Url, request.Token, cancellationToken);
        if (identity is null)
        {
            return BadRequest("Unable to connect to Plex with the provided URL/token.");
        }

        var userInfo = await _plexApiClient.GetUserInfoAsync(request.Token, cancellationToken);
        var plexAvatarUrl = BuildPlexAvatarUrl(userInfo?.Thumb);

        var plex = await _authService.UpdateAsync(state =>
        {
            state.Plex = new PlexAuth
            {
                Url = request.Url,
                Token = request.Token,
                ServerName = identity.FriendlyName,
                MachineIdentifier = identity.MachineIdentifier,
                Version = identity.Version,
                Username = userInfo?.Username,
                AvatarUrl = plexAvatarUrl
            };

            return state.Plex;
        });

        return Ok(new
        {
            saved = true,
            plex
        });
    }

    [ValidateAntiForgeryToken]
    [HttpPost("jellyfin")]
    public async Task<IActionResult> SaveJellyfin([FromBody] JellyfinAuth request)
    {
        var gate = EnsureAccess();
        if (gate != null)
        {
            return gate;
        }

        await _authService.UpdateAsync(state =>
        {
            state.Jellyfin = request;
            return 0;
        });
        return Ok(new { saved = true });
    }

    [ValidateAntiForgeryToken]
    [HttpPost("jellyfin/login")]
    public async Task<IActionResult> LoginJellyfin([FromBody] JellyfinAuth request, CancellationToken cancellationToken)
    {
        var gate = EnsureAccess();
        if (gate != null)
        {
            return gate;
        }

        if (string.IsNullOrWhiteSpace(request.Url) || string.IsNullOrWhiteSpace(request.ApiKey))
        {
            return BadRequest("Jellyfin URL and API key are required.");
        }
        if (string.IsNullOrWhiteSpace(request.Username))
        {
            return BadRequest("Jellyfin username is required.");
        }

        var systemInfo = await _jellyfinApiClient.GetSystemInfoAsync(request.Url, request.ApiKey, cancellationToken);
        if (systemInfo is null)
        {
            return BadRequest("Unable to connect to Jellyfin with the provided URL/API key.");
        }

        var userInfo = await _jellyfinApiClient.ResolveUserAsync(
            request.Url,
            request.ApiKey,
            request.Username,
            request.UserId,
            cancellationToken);
        if (userInfo is null)
        {
            return BadRequest("Jellyfin API key is valid, but user lookup failed for the provided username.");
        }

        var jellyfin = await _authService.UpdateAsync(state =>
        {
            state.Jellyfin = new JellyfinAuth
            {
                Url = request.Url,
                ApiKey = request.ApiKey,
                Username = userInfo.Name ?? request.Username,
                UserId = userInfo.Id ?? request.UserId,
                ServerName = systemInfo.ServerName,
                Version = systemInfo.Version,
                AvatarUrl = BuildJellyfinAvatarUrl(request.Url, userInfo.Id ?? request.UserId)
            };

            return state.Jellyfin;
        });

        return Ok(new
        {
            saved = true,
            jellyfin
        });
    }

    /// <summary>
    /// Stores the Google OAuth client and returns the consent URL. The callback completes the
    /// exchange, so no secret is ever placed in a browser URL.
    /// </summary>
    [ValidateAntiForgeryToken]
    [HttpPost("ytmusic/authorize")]
    public async Task<IActionResult> AuthorizeYTMusic([FromBody] YTMusicAuthorizeRequest request, CancellationToken cancellationToken)
    {
        var gate = EnsureAccess();
        if (gate != null)
        {
            return gate;
        }

        if (string.IsNullOrWhiteSpace(request?.ClientId) || string.IsNullOrWhiteSpace(request.ClientSecret))
        {
            return BadRequest("An OAuth client ID and secret are required.");
        }

        if (_youtubeDataApiClient is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "YouTube Music is unavailable.");
        }

        var redirectUri = BuildYTMusicRedirectUri();
        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        var auth = await _authService.UpdateAsync(state =>
        {
            state.YTMusic = new YTMusicAuth
            {
                ClientId = request.ClientId.Trim(),
                ClientSecret = request.ClientSecret
            };
            return state.YTMusic;
        });

        var authorizeUrl = _youtubeDataApiClient.BuildAuthorizeUrl(
            auth.ClientId!,
            redirectUri,
            state,
            request.LoginHint ?? string.Empty);

        return Ok(new
        {
            saved = true,
            authorizeUrl,
            redirectUri,
            state
        });
    }

    /// <summary>
    /// OAuth redirect target. Google sends the user back here with a code and the state we
    /// issued, which is exchanged for tokens and persisted.
    /// </summary>
    [HttpGet("ytmusic/callback")]
    public async Task<IActionResult> YTMusicCallback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        CancellationToken cancellationToken)
    {
        var gate = EnsureAccess();
        if (gate != null)
        {
            return gate;
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            return RedirectYTMusicResult($"YouTube Music authorization was declined: {error}");
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return RedirectYTMusicResult("YouTube Music did not return an authorization code.");
        }

        if (_youtubeDataApiClient is null)
        {
            return RedirectYTMusicResult("YouTube Music is unavailable.");
        }

        var current = await _authService.LoadAsync();
        var ytmusic = current.YTMusic;
        if (ytmusic is null
            || string.IsNullOrWhiteSpace(ytmusic.ClientId)
            || string.IsNullOrWhiteSpace(ytmusic.ClientSecret))
        {
            return RedirectYTMusicResult("Save the YouTube Music OAuth client before connecting.");
        }

        var token = await _youtubeDataApiClient.ExchangeCodeAsync(
            ytmusic.TokenUrl ?? DeezSpoTag.Integrations.YouTube.YouTubeDataApiClient.DefaultTokenUrl,
            ytmusic.ClientId,
            ytmusic.ClientSecret,
            code,
            BuildYTMusicRedirectUri(),
            cancellationToken);
        if (!token.Success || string.IsNullOrWhiteSpace(token.AccessToken))
        {
            return RedirectYTMusicResult(token.Error ?? "YouTube Music token exchange failed.");
        }

        // A refresh token is only issued on the first consent, so an absent one must not wipe
        // the one already stored.
        var saved = await _authService.UpdateAsync(next =>
        {
            next.YTMusic ??= new YTMusicAuth();
            next.YTMusic.ClientId = ytmusic.ClientId;
            next.YTMusic.ClientSecret = ytmusic.ClientSecret;
            next.YTMusic.AccessToken = token.AccessToken;
            next.YTMusic.AccessTokenExpiresAtUtc = token.ExpiresAtUtc;
            next.YTMusic.RefreshToken = string.IsNullOrWhiteSpace(token.RefreshToken)
                ? ytmusic.RefreshToken
                : token.RefreshToken;
            next.YTMusic.CredentialsValid = true;
            return next.YTMusic;
        });

        return saved is not null && !string.IsNullOrWhiteSpace(saved.RefreshToken)
            ? RedirectYTMusicResult(null)
            : RedirectYTMusicResult("Connected, but Google did not issue a refresh token. Disconnect and reconnect to grant one.");
    }

    [ValidateAntiForgeryToken]
    [HttpPost("ytmusic/disconnect")]
    public async Task<IActionResult> DisconnectYTMusic(CancellationToken cancellationToken)
    {
        var gate = EnsureAccess();
        if (gate != null)
        {
            return gate;
        }

        var state = await _authService.LoadAsync();
        if (state.YTMusic is not null && _youtubeDataApiClient is not null)
        {
            var token = string.IsNullOrWhiteSpace(state.YTMusic.RefreshToken)
                ? state.YTMusic.AccessToken
                : state.YTMusic.RefreshToken;
            if (!string.IsNullOrWhiteSpace(token))
            {
                await _youtubeDataApiClient.RevokeAsync(YouTubeMusicDefaultRevokeUrl, token, cancellationToken);
            }
        }

        await _authService.UpdateAsync(next =>
        {
            next.YTMusic = null;
            return next.YTMusic;
        });

        return Ok(new { disconnected = true });
    }

    private const string YouTubeMusicDefaultRevokeUrl = "https://oauth2.googleapis.com/revoke";

    private string BuildYTMusicRedirectUri()
    {
        var request = Request;
        var origin = string.Equals(request.Headers["X-Forwarded-Proto"], "https", StringComparison.OrdinalIgnoreCase)
            ? "https"
            : request.Scheme;
        return $"{origin}://{request.Host}{request.PathBase}/api/platform-auth/ytmusic/callback";
    }

    private IActionResult RedirectYTMusicResult(string? message)
    {
        var target = message is null
            ? "/Login?loginTab=ytmusic&ytmusicConnected=1"
            : $"/Login?loginTab=ytmusic&ytmusicError={Uri.EscapeDataString(message)}";
        return Redirect(target);
    }

    private static object? ToPublicYTMusic(YTMusicAuth? auth)
    {
        if (auth is null)
        {
            return null;
        }

        return new
        {
            clientId = auth.ClientId,
            clientSecretSaved = !string.IsNullOrWhiteSpace(auth.ClientSecret),
            refreshTokenSaved = !string.IsNullOrWhiteSpace(auth.RefreshToken),
            displayName = auth.DisplayName,
            channelId = auth.ChannelId,
            avatarUrl = auth.AvatarUrl,
            connected = !string.IsNullOrWhiteSpace(auth.RefreshToken)
        };
    }

    public sealed record YTMusicAuthorizeRequest(string? ClientId, string? ClientSecret, string? LoginHint = null);

    [ValidateAntiForgeryToken]
    [HttpPost("navidrome/login")]
    public async Task<IActionResult> LoginNavidrome([FromBody] NavidromeAuth request, CancellationToken cancellationToken)
    {
        var gate = EnsureAccess();
        if (gate != null)
        {
            return gate;
        }

        if (string.IsNullOrWhiteSpace(request.Url)
            || string.IsNullOrWhiteSpace(request.Username)
            || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest("Navidrome URL, username, and password/token are required.");
        }

        var systemInfo = await _navidromeApiClient.PingAsync(
            request.Url,
            request.Username,
            request.Password,
            cancellationToken);
        if (systemInfo is null)
        {
            return BadRequest("Unable to connect to Navidrome with the provided URL and credentials.");
        }

        var navidrome = await _authService.UpdateAsync(state =>
        {
            state.Navidrome = new NavidromeAuth
            {
                Url = request.Url,
                Username = request.Username,
                Password = request.Password,
                ServerName = systemInfo.ServerName,
                Version = systemInfo.Version
            };

            return state.Navidrome;
        });

        return Ok(new
        {
            saved = true,
            navidrome = ToPublicNavidrome(navidrome)
        });
    }

    private static string? BuildPlexAvatarUrl(string? rawThumb)
    {
        if (string.IsNullOrWhiteSpace(rawThumb))
        {
            return null;
        }

        if (rawThumb.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return rawThumb;
        }

        return $"https://plex.tv{rawThumb}";
    }

    private static string? BuildJellyfinAvatarUrl(string? baseUrl, string? userId)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        return $"{baseUrl.TrimEnd('/')}/Users/{userId}/Images/Primary";
    }

    private static object? ToPublicNavidrome(NavidromeAuth? auth)
    {
        if (auth is null)
        {
            return null;
        }

        return new
        {
            url = auth.Url,
            username = auth.Username,
            passwordSaved = !string.IsNullOrWhiteSpace(auth.Password),
            serverName = auth.ServerName,
            version = auth.Version,
            connected = !string.IsNullOrWhiteSpace(auth.Url)
                && !string.IsNullOrWhiteSpace(auth.Username)
                && !string.IsNullOrWhiteSpace(auth.Password)
        };
    }

    private static object? ToPublicLastFm(LastFmAuth? auth)
    {
        if (auth is null)
        {
            return null;
        }

        return new
        {
            username = auth.Username,
            apiKey = auth.ApiKey,
            hasApiKey = !string.IsNullOrWhiteSpace(auth.ApiKey)
        };
    }

    internal static BeatportPlatformStatus ToPublicBeatport(BeatportAuth? auth)
    {
        var configured = !string.IsNullOrWhiteSpace(auth?.ClientId)
            && !string.IsNullOrWhiteSpace(auth.RedirectUri);
        var connected = !string.IsNullOrWhiteSpace(auth?.RefreshToken)
            || (!string.IsNullOrWhiteSpace(auth?.AccessToken)
                && auth.ExpiresAtUtc > DateTimeOffset.UtcNow);
        return new BeatportPlatformStatus(
            configured,
            connected,
            auth?.ClientId,
            !string.IsNullOrWhiteSpace(auth?.ClientSecret),
            auth?.RedirectUri,
            auth?.Scope,
            auth?.ExpiresAtUtc);
    }

    private static object ToPublicQobuz(QobuzAuth? auth)
    {
        var hasAppSecret = !string.IsNullOrWhiteSpace(auth?.AppSecret) || !string.IsNullOrWhiteSpace(auth?.DownloadSecret);
        var hasAuthToken = !string.IsNullOrWhiteSpace(auth?.AuthToken);
        var configured = hasAppSecret && hasAuthToken;
        var connected = configured && auth!.AuthTokenValid != false;
        return new
        {
            appId = auth?.AppId,
            authTokenSaved = hasAuthToken,
            appSecretSaved = hasAppSecret,
            configured,
            userId = auth?.UserId,
            displayName = auth?.DisplayName,
            country = auth?.Country,
            zone = auth?.Zone,
            credentialLabel = auth?.CredentialLabel,
            subscriptionOffer = auth?.SubscriptionOffer,
            authTokenValid = auth?.AuthTokenValid,
            accountRefreshedAt = auth?.AccountRefreshedAt,
            connected
        };
    }

    private static object ToPublicTidal(TidalAuth? auth) => new
    {
        clientId = auth?.ClientId,
        clientSecretSaved = !string.IsNullOrWhiteSpace(auth?.ClientSecret),
        accessTokenSaved = !string.IsNullOrWhiteSpace(auth?.AccessToken),
        refreshTokenSaved = !string.IsNullOrWhiteSpace(auth?.RefreshToken),
        userId = auth?.UserId,
        countryCode = auth?.CountryCode ?? "US",
        credentialsValid = auth?.CredentialsValid == true,
        validatedAt = auth?.ValidatedAt,
        connected = auth?.CredentialsValid == true
    };

    /// <summary>
    ///     Drops the cached Soulseek eligibility so the next admission check asks slskd again.
    /// </summary>
    /// <remarks>
    ///     Called when the details change and when the reader logs out. Both make the previous answer wrong,
    ///     and both happen outside the probe, so nothing else would notice.
    /// </remarks>
    private void InvalidateSoulseekEligibility()
        => _soulseekEligibility?.Invalidate();

    private static object ToPublicSoulseek(SoulseekAuth? auth, SoulseekConnectionStatus? eligibility = null)
    {
        var configured = !string.IsNullOrWhiteSpace(auth?.BaseUrl);
        var status = auth?.LastStatus;
        if (string.IsNullOrWhiteSpace(status))
        {
            status = configured ? "disconnected" : "not_configured";
        }

        var message = auth?.LastError;
        if (auth?.ConnectionValid == true)
        {
            message = "slskd is connected to Soulseek.";
        }
        else if (string.IsNullOrWhiteSpace(message))
        {
            message = configured ? "slskd is not connected." : "Soulseek is not configured.";
        }

        // Two authorities cannot stand in one response. When the service-layer probe ran, it decides every
        // availability field: `active`, `reason`, `connected`, and the status string. The persisted record is
        // still carried for display, but it does not get to disagree with a fresh probe the reader can act on.
        var active = eligibility?.IsUsable ?? (auth?.ConnectionValid == true);
        var reason = eligibility?.Message ?? message;
        var statusOut = eligibility is null ? status : eligibility.State.ToString().ToLowerInvariant();

        return new
        {
            baseUrl = auth?.BaseUrl,
            apiKeySaved = !string.IsNullOrWhiteSpace(auth?.ApiKey),
            configured,
            connected = active,
            active,
            reason,
            username = auth?.Username,
            status = statusOut,
            message = reason,
            checkedAt = auth?.CheckedAt
        };
    }

    /// <summary>
    ///     Builds the public view of the SoundCloud connection.
    /// </summary>
    /// <remarks>
    ///     Deliberately exposes only whether a token is saved and whether it validated. The token itself is
    ///     never returned, because this payload goes to the browser and would otherwise be a credential leak
    ///     into page source, devtools, and any proxy log in between.
    /// </remarks>
    private static object ToPublicSoundCloud(SoundCloudAuth? auth)
    {
        var tokenSaved = !string.IsNullOrWhiteSpace(auth?.OAuthToken);
        var status = auth?.LastStatus;
        if (string.IsNullOrWhiteSpace(status))
        {
            status = tokenSaved ? "disconnected" : "not_configured";
        }

        var message = auth?.LastError;
        if (auth?.CredentialsValid == true)
        {
            message = "SoundCloud is connected.";
        }
        else if (string.IsNullOrWhiteSpace(message))
        {
            message = tokenSaved
                ? "The saved SoundCloud token has not been checked yet."
                : "No SoundCloud token is saved. Public tracks work without one.";
        }

        return new
        {
            tokenSaved,
            connected = auth?.CredentialsValid == true,
            status,
            message,
            checkedAt = auth?.CheckedAt
        };
    }

    private static object ToPublicBoomplay(BoomplayAuth? auth)
    {
        var configured = !string.IsNullOrWhiteSpace(auth?.Cookie);
        var connected = configured
            && auth!.SessionValid == true
            && string.Equals(auth.LastStatus, "session_verified", StringComparison.Ordinal)
            && BoomplaySessionCookie.TryNormalize(auth.Cookie, out var cookie)
            && BoomplaySessionCookie.TryExtractSessionId(cookie, out _);
        var status = auth?.LastStatus;
        if (string.IsNullOrWhiteSpace(status))
        {
            status = configured ? "session_saved" : "not_configured";
        }

        return new
        {
            cookieSaved = configured,
            configured,
            connected,
            status,
            savedAt = auth?.SavedAt
        };
    }


    private static readonly TimeSpan PublicProviderStatusTimeout = TimeSpan.FromSeconds(4);

    private async Task<(string Status, int OnlineCount)> ResolveProviderStatusAsync<TSummary>(
        string provider,
        Func<CancellationToken, Task<TSummary>> load,
        Func<TSummary, (string Status, int OnlineCount)> project,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PublicProviderStatusTimeout);
        try
        {
            return project(await load(timeout.Token));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Public provider status for {Provider} timed out after {TimeoutSeconds}s; reporting it as degraded.",
                provider,
                PublicProviderStatusTimeout.TotalSeconds);
            return (PublicApiDegradedStatus, 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Public provider status for {Provider} failed; reporting it as degraded.", provider);
            return (PublicApiDegradedStatus, 0);
        }
    }

    /// <summary>
    ///     The engine ids used to label a public-provider check, aliased to the one canonical
    ///     definition.
    /// </summary>
    /// <remarks>
    ///     These are download-source ids and are used here only to name which provider's registry is
    ///     being checked, so a drifted copy would mislabel a log line rather than misbehave. The
    ///     account-platform ids in the disconnect switch further down are deliberately NOT aliased:
    ///     that switch speaks a different vocabulary, where Apple is "applemusic" and Amazon is
    ///     "amazonmusic", and pointing those at the source ids would change which case they match.
    /// </remarks>
    private const string QobuzSource = DownloadTagSourceHelper.QobuzSource;

    private const string AmazonSource = DownloadTagSourceHelper.AmazonSource;

    private const string TidalSource = DownloadTagSourceHelper.TidalSource;

    private async Task RunProviderStageAsync(
        string provider,
        Func<CancellationToken, Task> stage,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PublicProviderCheckTimeout);
        try
        {
            await stage(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Public provider check for {Provider} timed out after {TimeoutSeconds}s.",
                provider,
                PublicProviderCheckTimeout.TotalSeconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Public provider check for {Provider} failed.", provider);
        }
    }

    private static readonly TimeSpan PublicProviderCheckTimeout = TimeSpan.FromSeconds(20);
    private const string PublicApiDegradedStatus = "degraded";

    private async Task<QobuzProviderSummary> GetPublicQobuzProvidersAsync(CancellationToken cancellationToken, bool liveSession = false)
    {
        var providers = (await _qobuzPublicProviderRegistry.GetProvidersAsync(cancellationToken)).Select(ToPublicProvider).ToArray();
        var enabledProviders = providers.Where(static provider => provider.Enabled).ToArray();
        var onlineCount = enabledProviders.Count(IsDownloadAvailable);
        var sessionValid = liveSession
            ? await _qobuzDownloadService.HasPublicDownloadSessionAsync(cancellationToken)
            : await _qobuzDownloadService.PeekPublicDownloadSessionAsync(cancellationToken);
        var online = onlineCount > 0 && sessionValid;
        return new QobuzProviderSummary(
            online,
            online ? onlineCount : 0,
            ResolvePublicApiStatus(enabledProviders.Length, online, enabledProviders.All(IsChecked), sessionValid, enabledProviders.Any(IsCoolingDown)),
            sessionValid,
            providers);
    }

    private static QobuzProviderView ToPublicProvider(QobuzPublicProvider provider)
        => new(provider.Id, provider.DisplayName, provider.Enabled, provider.Status, provider.LastCheckedAt, provider.LastSuccessAt, provider.FailureCategory, provider.FailureMessage, provider.ResponseTimeMs, provider.CooldownUntil);

    public sealed record QobuzProviderEnabledRequest(bool? Enabled);
    private sealed record QobuzProviderSummary(bool Online, int OnlineCount, string Status, bool SessionValid, QobuzProviderView[] Providers);
    private sealed record QobuzProviderView(string Id, string Name, bool Enabled, string Status, DateTimeOffset? LastCheckedAt, DateTimeOffset? LastSuccessAt, string? FailureCategory, string? FailureMessage, long? ResponseTimeMs, DateTimeOffset? CooldownUntil);

    private async Task<TidalProviderSummary> GetPublicTidalProvidersAsync(CancellationToken cancellationToken, bool liveSession = false)
    {
        var providers = (await _tidalPublicProviderRegistry.GetProvidersAsync(cancellationToken)).Select(ToPublicTidalProvider).ToArray();
        var enabledProviders = providers.Where(static provider => provider.Enabled).ToArray();
        var onlineCount = enabledProviders.Count(IsDownloadAvailable);
        var sessionValid = liveSession
            ? await _tidalDownloadService.HasPublicDownloadSessionAsync(cancellationToken)
            : await _tidalDownloadService.PeekPublicDownloadSessionAsync(cancellationToken);
        var online = onlineCount > 0 && sessionValid;
        return new TidalProviderSummary(
            online,
            online ? onlineCount : 0,
            ResolvePublicApiStatus(enabledProviders.Length, online, enabledProviders.All(IsChecked), sessionValid, enabledProviders.Any(IsCoolingDown)),
            sessionValid,
            providers);
    }

    private static TidalProviderView ToPublicTidalProvider(TidalPublicProvider provider)
        => new(provider.Id, provider.DisplayName, provider.Enabled, provider.Status, provider.LastCheckedAt, provider.LastSuccessAt, provider.FailureCategory, provider.FailureMessage, provider.ResponseTimeMs, provider.CooldownUntil);

    public sealed record TidalProviderEnabledRequest(bool? Enabled);
    private sealed record TidalProviderSummary(bool Online, int OnlineCount, string Status, bool SessionValid, TidalProviderView[] Providers);
    private sealed record TidalProviderView(string Id, string Name, bool Enabled, string Status, DateTimeOffset? LastCheckedAt, DateTimeOffset? LastSuccessAt, string? FailureCategory, string? FailureMessage, long? ResponseTimeMs, DateTimeOffset? CooldownUntil);

    private static bool IsChecked(QobuzProviderView provider)
        => provider.LastCheckedAt.HasValue && provider.Status != "unknown";

    private static bool IsChecked(TidalProviderView provider)
        => provider.LastCheckedAt.HasValue && provider.Status != "unknown";

    private static bool IsDownloadAvailable(QobuzProviderView provider)
        => provider.Status == "online"
           && (!provider.CooldownUntil.HasValue || provider.CooldownUntil.Value <= DateTimeOffset.UtcNow);

    private static bool IsDownloadAvailable(TidalProviderView provider)
        => provider.Status == "online"
           && (!provider.CooldownUntil.HasValue || provider.CooldownUntil.Value <= DateTimeOffset.UtcNow);

    private static string ResolvePublicApiStatus(
        int enabledProviderCount,
        bool online,
        bool allChecked,
        bool sessionValid,
        bool anyCoolingDown = false)
    {
        if (online)
        {
            return "online";
        }

        if (enabledProviderCount > 0 && (!sessionValid || anyCoolingDown))
        {
            return "offline";
        }

        return enabledProviderCount > 0 && allChecked ? "offline" : "unknown";
    }

    private static bool IsCoolingDown(QobuzProviderView provider)
        => provider.CooldownUntil.HasValue && provider.CooldownUntil.Value > DateTimeOffset.UtcNow;

    private static bool IsCoolingDown(TidalProviderView provider)
        => provider.CooldownUntil.HasValue && provider.CooldownUntil.Value > DateTimeOffset.UtcNow;

    private static bool IsCoolingDown(AmazonProviderView provider)
        => provider.CooldownUntil.HasValue && provider.CooldownUntil.Value > DateTimeOffset.UtcNow;

    private static string? ResolveSubmittedSecret(string? submitted, string? existing)
        => string.IsNullOrWhiteSpace(submitted) ? existing : submitted.Trim();

    private static string NormalizeCountryCode(string? value)
    {
        var normalized = value?.Trim().ToUpperInvariant() ?? string.Empty;
        return normalized.Length == 2 ? normalized : "US";
    }

    private async Task<PlatformAuthState> RefreshQobuzAccountAsync(
        PlatformAuthState state,
        CancellationToken cancellationToken)
    {
        var qobuz = state.Qobuz;
        if (qobuz is null
            || string.IsNullOrWhiteSpace(qobuz.AppId)
            || string.IsNullOrWhiteSpace(qobuz.AuthToken))
        {
            return state;
        }

        var result = await _qobuzAccountProfileService.FetchAsync(qobuz.AppId, qobuz.AuthToken, cancellationToken);
        return await _authService.UpdateAsync(current =>
        {
            if (current.Qobuz is null || result.Status == QobuzAccountProfileStatus.Unavailable)
            {
                return current;
            }

            current.Qobuz.AuthTokenValid = result.IsValid;
            current.Qobuz.AccountRefreshedAt = DateTimeOffset.UtcNow;
            current.Qobuz.UserId = result.Profile?.UserId;
            current.Qobuz.DisplayName = result.Profile?.DisplayName;
            current.Qobuz.Country = result.Profile?.Country;
            current.Qobuz.Zone = result.Profile?.Zone;
            current.Qobuz.CredentialLabel = result.Profile?.CredentialLabel;
            current.Qobuz.SubscriptionOffer = result.Profile?.SubscriptionOffer;
            return current;
        });
    }

    [ValidateAntiForgeryToken]
    [HttpPost("{platform}/disconnect")]
    public async Task<IActionResult> Disconnect(string platform, CancellationToken cancellationToken)
    {
        var gate = EnsureAccess();
        if (gate != null)
        {
            return gate;
        }

        var normalizedPlatform = platform.ToLowerInvariant();
        if (!IsSupportedPlatform(normalizedPlatform))
        {
            return BadRequest("Unknown platform.");
        }

        if (normalizedPlatform == "applemusic")
        {
            var wrapperLogout = await _appleWrapperService.LogoutExternalWrapperSessionAsync(cancellationToken);
            if (!wrapperLogout.Success)
            {
                var message = string.IsNullOrWhiteSpace(wrapperLogout.Error)
                    ? "Apple Music logout failed. Wrapper session may still be active."
                    : wrapperLogout.Error;
                return StatusCode(500, message);
            }
        }

        await _authService.UpdateAsync(state =>
        {
            switch (normalizedPlatform)
            {
                case "spotify":
                    state.Spotify = null;
                    break;
                case "discogs":
                    state.Discogs = null;
                    break;
                case "lastfm":
                    state.LastFm = null;
                    break;
                case "bpmsupreme":
                    state.BpmSupreme = null;
                    break;
                case "plex":
                    state.Plex = null;
                    break;
                case "jellyfin":
                    state.Jellyfin = null;
                    break;
                case "navidrome":
                    state.Navidrome = null;
                    break;
                case "applemusic":
                    state.AppleMusic = null;
                    break;
                case "qobuz":
                    state.Qobuz = null;
                    break;
                case "tidal":
                    state.Tidal = null;
                    break;
                case "amazonmusic":
                    state.AmazonMusic = null;
                    break;
                case "soulseek":
                    state.Soulseek = null;
                    break;
                case "soundcloud":
                    state.SoundCloud = null;
                    break;
                case "boomplay":
                    state.Boomplay = null;
                    break;
            }

            return 0;
        });

        if (normalizedPlatform == "tidal")
        {
            _tidalAccessTokenProvider.Invalidate();
        }

        // Logging out has to take effect at once. Leaving the cached answer in place would keep Soulseek
        // active as a download source for the rest of the cache window, with nothing on screen saying so.
        if (normalizedPlatform == "soulseek")
        {
            InvalidateSoulseekEligibility();
        }

        return Ok(new { disconnected = true });
    }

    private static bool IsSupportedPlatform(string normalizedPlatform)
    {
        return normalizedPlatform is "spotify"
            or "discogs"
            or "lastfm"
            or "bpmsupreme"
            or "plex"
            or "jellyfin"
            or "navidrome"
            or "applemusic"
            or "qobuz"
            or "tidal"
            or "amazonmusic"
            or "soulseek"
            or "soundcloud"
            or "boomplay";
    }

    private async Task<AmazonProviderSummary> GetPublicAmazonProvidersAsync(CancellationToken cancellationToken, bool liveSession = false)
    {
        var providers = (await _amazonPublicProviderRegistry.GetProvidersAsync(cancellationToken)).Select(ToPublicAmazonProvider).ToArray();
        var enabledProviders = providers.Where(static provider => provider.Enabled).ToArray();
        var onlineCount = enabledProviders.Count(IsDownloadAvailable);
        var sessionValid = liveSession
            ? await _amazonDownloadService.HasPublicDownloadSessionAsync(cancellationToken)
            : await _amazonDownloadService.PeekPublicDownloadSessionAsync(cancellationToken);
        var online = onlineCount > 0 && sessionValid;
        return new AmazonProviderSummary(
            online,
            online ? onlineCount : 0,
            ResolvePublicApiStatus(enabledProviders.Length, online, enabledProviders.All(IsChecked), sessionValid, enabledProviders.Any(IsCoolingDown)),
            sessionValid,
            providers);
    }

    private static AmazonProviderView ToPublicAmazonProvider(AmazonPublicProvider provider)
        => new(provider.Id, provider.DisplayName, provider.Enabled, provider.Status, provider.LastCheckedAt, provider.LastSuccessAt, provider.FailureCategory, provider.FailureMessage, provider.ResponseTimeMs, provider.CooldownUntil);

    private static bool IsDownloadAvailable(AmazonProviderView provider)
        => provider.Status == "online"
           && (!provider.CooldownUntil.HasValue || provider.CooldownUntil.Value <= DateTimeOffset.UtcNow);

    public sealed record AmazonProviderEnabledRequest(bool? Enabled);
    private sealed record AmazonProviderSummary(bool Online, int OnlineCount, string Status, bool SessionValid, AmazonProviderView[] Providers);
    private sealed record AmazonProviderView(string Id, string Name, bool Enabled, string Status, DateTimeOffset? LastCheckedAt, DateTimeOffset? LastSuccessAt, string? FailureCategory, string? FailureMessage, long? ResponseTimeMs, DateTimeOffset? CooldownUntil);

    private static bool IsChecked(AmazonProviderView provider)
        => provider.LastCheckedAt.HasValue && provider.Status != "unknown";

    private static object ToPublicAmazonMusic(AmazonMusicAuth? auth)
    {
        var configured = auth is not null
            && (!string.IsNullOrWhiteSpace(auth.Host) || !string.IsNullOrWhiteSpace(auth.Cookie));
        return new
        {
            host = auth?.Host ?? "music.amazon.com",
            locale = auth?.Locale ?? "en_US",
            cookieSaved = !string.IsNullOrWhiteSpace(auth?.Cookie),
            configured,
            connected = configured,
            savedAt = auth?.SavedAt
        };
    }

    private static string NormalizeAmazonHost(string? value)
    {
        var host = (value ?? "music.amazon.com").Trim();
        host = host.Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim('/');
        return string.IsNullOrWhiteSpace(host) ? "music.amazon.com" : host;
    }

    private UnauthorizedObjectResult? EnsureAccess()
    {
        return LocalApiAccess.IsAllowed(HttpContext)
            ? null
            : Unauthorized("Authentication required.");
    }
}
