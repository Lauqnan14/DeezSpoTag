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
using DeezSpoTag.Services.Download.Amazon;
using DeezSpoTag.Services.Download.Qobuz;
using DeezSpoTag.Services.Download.Tidal;

namespace DeezSpoTag.Web.Controllers.Api;

public sealed class PlatformAuthApiDependencies
{
    public required PlatformAuthService AuthService { get; init; }
    public required DiscogsApiClient DiscogsApiClient { get; init; }
    public required PlexApiClient PlexApiClient { get; init; }
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
    public required SoulseekConnectionService SoulseekConnectionService { get; init; }
    public required DeezerSessionManager DeezerSessionManager { get; init; }
}

public sealed class BoomplayLoginRequest
{
    public string? Cookie { get; set; }
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
[Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryToken]
public class PlatformAuthApiController : ControllerBase
{
    private readonly PlatformAuthService _authService;
    private readonly DiscogsApiClient _discogsApiClient;
    private readonly PlexApiClient _plexApiClient;
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
    private readonly SoulseekConnectionService _soulseekConnectionService;
    private readonly DeezerSessionManager _deezerSessionManager;
    public PlatformAuthApiController(PlatformAuthApiDependencies dependencies)
    {
        _authService = dependencies.AuthService;
        _discogsApiClient = dependencies.DiscogsApiClient;
        _plexApiClient = dependencies.PlexApiClient;
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
        _deezerSessionManager = dependencies.DeezerSessionManager;
    }

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] bool refresh,
        CancellationToken cancellationToken)
    {
        var gate = EnsureAccess();
        if (gate != null)
        {
            return gate;
        }

        var state = await _authService.LoadAsync();
        if (refresh)
        {
            state = await RefreshAppleWrapperStateAsync(state);
            state = await RefreshQobuzAccountAsync(state, cancellationToken);
            state = await RefreshSoulseekConnectionAsync(state, cancellationToken);
        }
        var amazonProviders = await GetPublicAmazonProvidersAsync(cancellationToken);
        var qobuzProviders = await GetPublicQobuzProvidersAsync(cancellationToken);
        var tidalProviders = await GetPublicTidalProvidersAsync(cancellationToken);

        return Ok(new
        {
            spotify = state.Spotify is null ? null : new { state.Spotify.ActiveAccount, accounts = state.Spotify.Accounts?.Select(a => new { a.Name, a.Region, a.CreatedAt, a.UpdatedAt }) },
            spotifyConnected = HasSpotifyRuntimeCredentials(state.Spotify),
            deezerConnected = _deezerSessionManager.LoggedIn && _deezerSessionManager.CurrentUser is not null,
            discogs = state.Discogs is null ? null : new { state.Discogs.Username, state.Discogs.AvatarUrl, state.Discogs.Location, tokenSaved = !string.IsNullOrWhiteSpace(state.Discogs.Token) },
            lastFm = ToPublicLastFm(state.LastFm),
            bpmSupreme = state.BpmSupreme is null ? null : new { state.BpmSupreme.Email, state.BpmSupreme.Library, passwordSaved = !string.IsNullOrWhiteSpace(state.BpmSupreme.Password) },
            plex = state.Plex is null ? null : new { state.Plex.Url, state.Plex.ServerName, state.Plex.MachineIdentifier, state.Plex.Version, state.Plex.Username, state.Plex.AvatarUrl, tokenSaved = !string.IsNullOrWhiteSpace(state.Plex.Token) },
            jellyfin = state.Jellyfin is null ? null : new { state.Jellyfin.Url, state.Jellyfin.Username, state.Jellyfin.UserId, state.Jellyfin.ServerName, state.Jellyfin.Version, state.Jellyfin.AvatarUrl, apiKeySaved = !string.IsNullOrWhiteSpace(state.Jellyfin.ApiKey) },
            navidrome = ToPublicNavidrome(state.Navidrome),
            appleMusic = state.AppleMusic is null ? null : new { state.AppleMusic.Email, mediaUserTokenSaved = !string.IsNullOrWhiteSpace(state.AppleMusic.MediaUserToken), authorizationTokenSaved = !string.IsNullOrWhiteSpace(state.AppleMusic.AuthorizationToken), state.AppleMusic.WrapperReady, state.AppleMusic.WrapperLoggedInAt },
            qobuz = ToPublicQobuz(state.Qobuz, qobuzProviders),
            tidal = ToPublicTidal(state.Tidal, tidalProviders),
            amazonMusic = ToPublicAmazonMusic(state.AmazonMusic, amazonProviders),
            soulseek = ToPublicSoulseek(state.Soulseek),
            boomplay = ToPublicBoomplay(state.Boomplay)
        });
    }

    [HttpGet("amazonmusic/providers")]
    public async Task<IActionResult> GetAmazonMusicProviders(CancellationToken cancellationToken)
    {
        var gate = EnsureAccess();
        if (gate != null) return gate;
        return Ok(await GetPublicAmazonProvidersAsync(cancellationToken));
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

    [HttpPost("amazonmusic/providers/check")]
    public async Task<IActionResult> CheckAmazonMusicProviders(CancellationToken cancellationToken)
    {
        var gate = EnsureAccess();
        if (gate != null) return gate;
        await _amazonPublicProviderRegistry.CheckEnabledProvidersAsync(cancellationToken);
        return Ok(await GetPublicAmazonProvidersAsync(cancellationToken));
    }

    private async Task<PlatformAuthState> RefreshAppleWrapperStateAsync(PlatformAuthState state)
    {
        var wrapperStatus = _appleWrapperService.GetStatus();
        state.AppleMusic ??= new AppleMusicAuth();
        var apple = state.AppleMusic;
        if (apple.WrapperReady == wrapperStatus.WrapperReady
            && string.Equals(apple.Email, wrapperStatus.Email, StringComparison.Ordinal)
            && (wrapperStatus.WrapperReady || apple.WrapperLoggedInAt is null))
        {
            return state;
        }

        return await _authService.UpdateAsync(current =>
        {
            current.AppleMusic ??= new AppleMusicAuth();
            current.AppleMusic.WrapperReady = wrapperStatus.WrapperReady;
            current.AppleMusic.Email = wrapperStatus.Email;
            if (!wrapperStatus.WrapperReady)
            {
                current.AppleMusic.WrapperLoggedInAt = null;
            }
            return current;
        });
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
        return Ok(await GetPublicTidalProvidersAsync(cancellationToken));
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
        return updated is null ? NotFound("Unknown Tidal provider.") : Ok(ToPublicTidalProvider(updated));
    }

    [HttpPost("tidal/providers/check")]
    public async Task<IActionResult> CheckTidalProviders(CancellationToken cancellationToken)
    {
        var gate = EnsureAccess();
        if (gate != null) return gate;
        await _tidalPublicProviderRegistry.CheckEnabledProvidersAsync(cancellationToken);
        return Ok(await GetPublicTidalProvidersAsync(cancellationToken));
    }

    [HttpGet("qobuz/providers")]
    public async Task<IActionResult> GetQobuzProviders(CancellationToken cancellationToken)
    {
        var gate = EnsureAccess();
        if (gate != null) return gate;
        return Ok(await GetPublicQobuzProvidersAsync(cancellationToken));
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

    [HttpPost("qobuz/providers/check")]
    public async Task<IActionResult> CheckQobuzProviders(CancellationToken cancellationToken)
    {
        var gate = EnsureAccess();
        if (gate != null) return gate;
        await _qobuzPublicProviderRegistry.CheckEnabledProvidersAsync(cancellationToken);
        return Ok(await GetPublicQobuzProvidersAsync(cancellationToken));
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
                _qobuzPublicProviderRegistry.CheckEnabledProvidersAsync(cancellationToken),
                _amazonPublicProviderRegistry.CheckEnabledProvidersAsync(cancellationToken),
                _tidalPublicProviderRegistry.CheckEnabledProvidersAsync(cancellationToken));
        }

        var qobuz = await GetPublicQobuzProvidersAsync(cancellationToken);
        var amazon = await GetPublicAmazonProvidersAsync(cancellationToken);
        var tidal = await GetPublicTidalProvidersAsync(cancellationToken);
        return Ok(new
        {
            qobuz = new { status = qobuz.Status, onlineCount = qobuz.OnlineCount },
            amazonMusic = new { status = amazon.Status, onlineCount = amazon.OnlineCount },
            tidal = new { status = tidal.Status, onlineCount = tidal.OnlineCount }
        });
    }

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

        var providers = await GetPublicQobuzProvidersAsync(cancellationToken);
        return Ok(new { saved = true, qobuz = ToPublicQobuz(qobuz, providers) });
    }

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

        return Ok(new { saved = true, tidal = ToPublicTidal(tidal, await GetPublicTidalProvidersAsync(cancellationToken)) });
    }

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

        if (SoulseekConnectionService.NormalizeBaseUri(baseUrl) is null)
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

        return Ok(new { saved = true, soulseek = ToPublicSoulseek(soulseek) });
    }

    [HttpPost("boomplay")]
    public async Task<IActionResult> SaveBoomplay([FromBody] BoomplayLoginRequest request)
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
            return BadRequest("Boomplay cookie is required.");
        }

        var boomplay = await _authService.UpdateAsync(state =>
        {
            state.Boomplay = new BoomplayAuth
            {
                Cookie = existingCookie,
                SessionValid = true,
                LastStatus = "session_saved",
                SavedAt = DateTimeOffset.UtcNow
            };

            return state.Boomplay;
        });

        return Ok(new { saved = true, boomplay = ToPublicBoomplay(boomplay) });
    }

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

        var amazonProviders = await GetPublicAmazonProvidersAsync(cancellationToken);
        return Ok(new { saved = true, amazonMusic = ToPublicAmazonMusic(amazonMusic, amazonProviders) });
    }

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

    private static object ToPublicQobuz(QobuzAuth? auth, QobuzProviderSummary providers)
    {
        var hasAppSecret = !string.IsNullOrWhiteSpace(auth?.AppSecret) || !string.IsNullOrWhiteSpace(auth?.DownloadSecret);
        var hasAuthToken = !string.IsNullOrWhiteSpace(auth?.AuthToken);
        var configured = hasAppSecret && hasAuthToken;
        var connected = configured && auth?.AuthTokenValid != false;
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
            publicApiOnline = providers.Online,
            publicApiStatus = providers.Status,
            publicApiOnlineCount = providers.OnlineCount,
            publicApiSessionValid = providers.SessionValid,
            connected,
            providers = providers.Providers
        };
    }

    private static object ToPublicTidal(TidalAuth? auth, TidalProviderSummary providers) => new
    {
        clientId = auth?.ClientId,
        clientSecretSaved = !string.IsNullOrWhiteSpace(auth?.ClientSecret),
        accessTokenSaved = !string.IsNullOrWhiteSpace(auth?.AccessToken),
        refreshTokenSaved = !string.IsNullOrWhiteSpace(auth?.RefreshToken),
        userId = auth?.UserId,
        countryCode = auth?.CountryCode ?? "US",
        credentialsValid = auth?.CredentialsValid == true,
        validatedAt = auth?.ValidatedAt,
        publicApiOnline = providers.Online,
        publicApiStatus = providers.Status,
        publicApiOnlineCount = providers.OnlineCount,
        publicApiSessionValid = providers.SessionValid,
        connected = auth?.CredentialsValid == true,
        providers = providers.Providers
    };

    private static object ToPublicSoulseek(SoulseekAuth? auth)
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

        return new
        {
            baseUrl = auth?.BaseUrl,
            apiKeySaved = !string.IsNullOrWhiteSpace(auth?.ApiKey),
            configured,
            connected = auth?.ConnectionValid == true,
            username = auth?.Username,
            status,
            message,
            checkedAt = auth?.CheckedAt
        };
    }

    private static object ToPublicBoomplay(BoomplayAuth? auth)
    {
        var configured = !string.IsNullOrWhiteSpace(auth?.Cookie);
        var status = auth?.LastStatus;
        if (string.IsNullOrWhiteSpace(status))
        {
            status = configured ? "session_saved" : "not_configured";
        }

        return new
        {
            cookieSaved = configured,
            configured,
            connected = configured && auth?.SessionValid != false,
            status,
            savedAt = auth?.SavedAt
        };
    }

    private async Task<QobuzProviderSummary> GetPublicQobuzProvidersAsync(CancellationToken cancellationToken)
    {
        var providers = (await _qobuzPublicProviderRegistry.GetProvidersAsync(cancellationToken)).Select(ToPublicProvider).ToArray();
        var enabledProviders = providers.Where(static provider => provider.Enabled).ToArray();
        var onlineCount = enabledProviders.Count(IsDownloadAvailable);
        var sessionValid = await _qobuzDownloadService.HasPublicDownloadSessionAsync(cancellationToken);
        var online = onlineCount > 0 && sessionValid;
        return new QobuzProviderSummary(
            online,
            online ? onlineCount : 0,
            ResolvePublicApiStatus(enabledProviders.Length, online, enabledProviders.All(IsChecked), sessionValid),
            sessionValid,
            providers);
    }

    private static QobuzProviderView ToPublicProvider(QobuzPublicProvider provider)
        => new(provider.Id, provider.DisplayName, provider.Enabled, provider.Status, provider.LastCheckedAt, provider.LastSuccessAt, provider.FailureCategory, provider.FailureMessage, provider.ResponseTimeMs, provider.CooldownUntil);

    public sealed record QobuzProviderEnabledRequest(bool? Enabled);
    private sealed record QobuzProviderSummary(bool Online, int OnlineCount, string Status, bool SessionValid, QobuzProviderView[] Providers);
    private sealed record QobuzProviderView(string Id, string Name, bool Enabled, string Status, DateTimeOffset? LastCheckedAt, DateTimeOffset? LastSuccessAt, string? FailureCategory, string? FailureMessage, long? ResponseTimeMs, DateTimeOffset? CooldownUntil);

    private async Task<TidalProviderSummary> GetPublicTidalProvidersAsync(CancellationToken cancellationToken)
    {
        var providers = (await _tidalPublicProviderRegistry.GetProvidersAsync(cancellationToken)).Select(ToPublicTidalProvider).ToArray();
        var enabledProviders = providers.Where(static provider => provider.Enabled).ToArray();
        var onlineCount = enabledProviders.Count(IsDownloadAvailable);
        var sessionValid = await _tidalDownloadService.HasPublicDownloadSessionAsync(cancellationToken);
        var online = onlineCount > 0 && sessionValid;
        return new TidalProviderSummary(
            online,
            online ? onlineCount : 0,
            ResolvePublicApiStatus(enabledProviders.Length, online, enabledProviders.All(IsChecked), sessionValid),
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

    private static string ResolvePublicApiStatus(int enabledProviderCount, bool online, bool allChecked, bool sessionValid)
    {
        if (online)
        {
            return "online";
        }

        if (enabledProviderCount > 0 && !sessionValid)
        {
            return "offline";
        }

        return enabledProviderCount > 0 && allChecked ? "offline" : "unknown";
    }

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
            if (current.Qobuz is null)
            {
                return current;
            }

            if (result.Status == QobuzAccountProfileStatus.Unavailable)
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

    private async Task<PlatformAuthState> RefreshSoulseekConnectionAsync(
        PlatformAuthState state,
        CancellationToken cancellationToken)
    {
        var soulseek = state.Soulseek;
        if (soulseek is null || string.IsNullOrWhiteSpace(soulseek.BaseUrl))
        {
            return state;
        }

        var check = await _soulseekConnectionService.CheckAsync(soulseek, cancellationToken);
        return await _authService.UpdateAsync(current =>
        {
            if (current.Soulseek is null)
            {
                return current;
            }

            current.Soulseek.ConnectionValid = check.Connected;
            current.Soulseek.Username = check.Username ?? current.Soulseek.Username;
            current.Soulseek.LastStatus = check.Status;
            current.Soulseek.LastError = check.Connected ? null : check.Message;
            current.Soulseek.CheckedAt = check.CheckedAt;
            return current;
        });
    }

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
            or "boomplay";
    }

    private async Task<AmazonProviderSummary> GetPublicAmazonProvidersAsync(CancellationToken cancellationToken)
    {
        var providers = (await _amazonPublicProviderRegistry.GetProvidersAsync(cancellationToken)).Select(ToPublicAmazonProvider).ToArray();
        var enabledProviders = providers.Where(static provider => provider.Enabled).ToArray();
        var onlineCount = enabledProviders.Count(IsDownloadAvailable);
        var sessionValid = await _amazonDownloadService.HasPublicDownloadSessionAsync(cancellationToken);
        var online = onlineCount > 0 && sessionValid;
        return new AmazonProviderSummary(
            online,
            online ? onlineCount : 0,
            ResolvePublicApiStatus(enabledProviders.Length, online, enabledProviders.All(IsChecked), sessionValid),
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

    private static object ToPublicAmazonMusic(AmazonMusicAuth? auth, AmazonProviderSummary providers)
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
            savedAt = auth?.SavedAt,
            publicApiOnline = providers.Online,
            publicApiStatus = providers.Status,
            publicApiOnlineCount = providers.OnlineCount,
            publicApiSessionValid = providers.SessionValid,
            providers = providers.Providers
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
