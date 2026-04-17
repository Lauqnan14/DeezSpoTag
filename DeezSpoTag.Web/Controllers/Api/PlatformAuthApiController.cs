using DeezSpoTag.Integrations.Discogs;
using DeezSpoTag.Integrations.Jellyfin;
using DeezSpoTag.Integrations.Plex;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[LocalApiAuthorize]
[Route("api/platform-auth")]
public class PlatformAuthApiController : ControllerBase
{
    private readonly PlatformAuthService _authService;
    private readonly DiscogsApiClient _discogsApiClient;
    private readonly PlexApiClient _plexApiClient;
    private readonly JellyfinApiClient _jellyfinApiClient;
    private readonly AppleMusicWrapperService _appleWrapperService;
    public PlatformAuthApiController(
        PlatformAuthService authService,
        DiscogsApiClient discogsApiClient,
        PlexApiClient plexApiClient,
        JellyfinApiClient jellyfinApiClient,
        AppleMusicWrapperService appleWrapperService)
    {
        _authService = authService;
        _discogsApiClient = discogsApiClient;
        _plexApiClient = plexApiClient;
        _jellyfinApiClient = jellyfinApiClient;
        _appleWrapperService = appleWrapperService;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var gate = EnsureAccess();
        if (gate != null)
        {
            return gate;
        }

        var state = await _authService.LoadAsync();
        var wrapperStatus = _appleWrapperService.GetStatus();
        state.AppleMusic ??= new AppleMusicAuth();
        var apple = state.AppleMusic;
        var changed = false;

        if (apple.WrapperReady != wrapperStatus.WrapperReady)
        {
            apple.WrapperReady = wrapperStatus.WrapperReady;
            changed = true;
        }

        if (!string.Equals(apple.Email, wrapperStatus.Email, StringComparison.Ordinal))
        {
            apple.Email = wrapperStatus.Email;
            changed = true;
        }

        if (!wrapperStatus.WrapperReady && apple.WrapperLoggedInAt is not null)
        {
            apple.WrapperLoggedInAt = null;
            changed = true;
        }

        if (changed)
        {
            state = await _authService.UpdateAsync(current =>
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

        return Ok(new
        {
            spotify = state.Spotify,
            discogs = state.Discogs,
            lastFm = ToPublicLastFm(state.LastFm),
            bpmSupreme = state.BpmSupreme,
            plex = state.Plex,
            jellyfin = state.Jellyfin,
            appleMusic = state.AppleMusic
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
            return BadRequest("Jellyfin API key is valid, but user lookup failed. Enter a Jellyfin username (or user id) and retry.");
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
            // Always clear auth state even if the wrapper helper fails to clean up its session.
            // LogoutExternalWrapperSessionAsync logs any helper errors internally.
            await _appleWrapperService.LogoutExternalWrapperSessionAsync(cancellationToken);
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
                case "applemusic":
                    state.AppleMusic = null;
                    break;
            }

            return 0;
        });

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
            or "applemusic";
    }

    private UnauthorizedObjectResult? EnsureAccess()
    {
        return LocalApiAccess.IsAllowed(HttpContext)
            ? null
            : Unauthorized("Authentication required.");
    }
}
