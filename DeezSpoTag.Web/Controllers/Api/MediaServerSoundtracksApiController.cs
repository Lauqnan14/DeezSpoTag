using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/media-server/soundtracks")]
[Authorize]
public sealed class MediaServerSoundtracksApiController : ControllerBase
{
    private const char UrlPathSeparator = '/';
    private const char QuerySeparator = '?';
    private const int LibraryPinMinLength = 4;
    private const int LibraryPinSaltBytes = 16;
    private const int LibraryPinHashBytes = 32;
    private const int LibraryPinPbkdf2Iterations = 120_000;
    private readonly MediaServerSoundtrackService _service;
    private readonly PlatformAuthService _platformAuthService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly UserPreferencesStore _userPreferencesStore;

    public MediaServerSoundtracksApiController(
        MediaServerSoundtrackService service,
        PlatformAuthService platformAuthService,
        IHttpClientFactory httpClientFactory,
        UserPreferencesStore userPreferencesStore)
    {
        _service = service;
        _platformAuthService = platformAuthService;
        _httpClientFactory = httpClientFactory;
        _userPreferencesStore = userPreferencesStore;
    }

    [HttpGet("configuration")]
    public async Task<IActionResult> GetConfiguration([FromQuery] bool refresh = false, CancellationToken cancellationToken = default)
    {
        var configuration = await _service.GetConfigurationAsync(refresh, cancellationToken);
        return Ok(configuration);
    }

    [HttpPost("configuration")]
    public async Task<IActionResult> UpdateConfiguration(
        [FromBody] MediaServerSoundtrackConfigurationUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var configuration = await _service.SaveConfigurationAsync(request, cancellationToken);
        return Ok(configuration);
    }

    [HttpGet("pin/status")]
    public async Task<IActionResult> GetLibraryPinStatus(CancellationToken cancellationToken)
    {
        var prefs = await _userPreferencesStore.LoadAsync();
        return Ok(new MediaServerLibraryPinStatusDto
        {
            Configured = IsLibraryPinConfigured(prefs)
        });
    }

    [HttpPost("pin/unlock")]
    public async Task<IActionResult> UnlockLibrariesWithPin(
        [FromBody] MediaServerLibraryPinUnlockRequest request,
        CancellationToken cancellationToken)
    {
        var pin = NormalizePin(request?.Pin);
        if (string.IsNullOrWhiteSpace(pin))
        {
            return BadRequest(new { error = "Enter a PIN first." });
        }

        var prefs = await _userPreferencesStore.LoadAsync();
        if (!IsLibraryPinConfigured(prefs))
        {
            var confirmation = NormalizePin(request?.ConfirmationPin);
            if (pin.Length < LibraryPinMinLength)
            {
                return BadRequest(new { error = $"PIN must be at least {LibraryPinMinLength} characters." });
            }

            if (string.IsNullOrWhiteSpace(confirmation))
            {
                return BadRequest(new { error = "Confirm your new PIN." });
            }

            if (!string.Equals(pin, confirmation, StringComparison.Ordinal))
            {
                return BadRequest(new { error = "PIN confirmation does not match." });
            }

            var salt = RandomNumberGenerator.GetBytes(LibraryPinSaltBytes);
            var hash = DeriveLibraryPinHash(pin, salt);
            prefs.MediaServerLibraryPinSalt = Convert.ToBase64String(salt);
            prefs.MediaServerLibraryPinHash = Convert.ToBase64String(hash);
            await _userPreferencesStore.SaveAsync(prefs);
            return Ok(new MediaServerLibraryPinUnlockResultDto
            {
                Unlocked = true,
                Created = true
            });
        }

        if (!ValidateLibraryPin(pin, prefs.MediaServerLibraryPinSalt, prefs.MediaServerLibraryPinHash))
        {
            return Unauthorized(new { error = "Invalid PIN." });
        }

        return Ok(new MediaServerLibraryPinUnlockResultDto
        {
            Unlocked = true,
            Created = false
        });
    }

    [HttpPost("pin/change")]
    public async Task<IActionResult> ChangeLibraryPin(
        [FromBody] MediaServerLibraryPinChangeRequest request,
        CancellationToken cancellationToken)
    {
        var currentPin = NormalizePin(request?.CurrentPin);
        if (string.IsNullOrWhiteSpace(currentPin))
        {
            return BadRequest(new { error = "Enter current PIN first." });
        }

        var newPin = NormalizePin(request?.NewPin);
        if (newPin.Length < LibraryPinMinLength)
        {
            return BadRequest(new { error = $"PIN must be at least {LibraryPinMinLength} characters." });
        }

        var confirmation = NormalizePin(request?.ConfirmationPin);
        if (string.IsNullOrWhiteSpace(confirmation))
        {
            return BadRequest(new { error = "Confirm your new PIN." });
        }

        if (!string.Equals(newPin, confirmation, StringComparison.Ordinal))
        {
            return BadRequest(new { error = "PIN confirmation does not match." });
        }

        var prefs = await _userPreferencesStore.LoadAsync();
        if (!IsLibraryPinConfigured(prefs))
        {
            return BadRequest(new { error = "No PIN is configured yet." });
        }

        if (!ValidateLibraryPin(currentPin, prefs.MediaServerLibraryPinSalt, prefs.MediaServerLibraryPinHash))
        {
            return Unauthorized(new { error = "Invalid current PIN." });
        }

        var salt = RandomNumberGenerator.GetBytes(LibraryPinSaltBytes);
        var hash = DeriveLibraryPinHash(newPin, salt);
        prefs.MediaServerLibraryPinSalt = Convert.ToBase64String(salt);
        prefs.MediaServerLibraryPinHash = Convert.ToBase64String(hash);
        await _userPreferencesStore.SaveAsync(prefs);
        return Ok(new MediaServerLibraryPinStatusDto
        {
            Configured = true
        });
    }

    [HttpPost("pin/reset")]
    public async Task<IActionResult> ResetLibraryPin(
        [FromBody] MediaServerLibraryPinResetRequest request,
        CancellationToken cancellationToken)
    {
        var currentPin = NormalizePin(request?.CurrentPin);
        if (string.IsNullOrWhiteSpace(currentPin))
        {
            return BadRequest(new { error = "Enter current PIN first." });
        }

        var prefs = await _userPreferencesStore.LoadAsync();
        if (!IsLibraryPinConfigured(prefs))
        {
            return Ok(new MediaServerLibraryPinStatusDto
            {
                Configured = false
            });
        }

        if (!ValidateLibraryPin(currentPin, prefs.MediaServerLibraryPinSalt, prefs.MediaServerLibraryPinHash))
        {
            return Unauthorized(new { error = "Invalid current PIN." });
        }

        prefs.MediaServerLibraryPinSalt = null;
        prefs.MediaServerLibraryPinHash = null;
        await _userPreferencesStore.SaveAsync(prefs);
        return Ok(new MediaServerLibraryPinStatusDto
        {
            Configured = false
        });
    }

    [HttpPost("sync")]
    public async Task<IActionResult> SyncLibraries(CancellationToken cancellationToken)
    {
        var configuration = await _service.RefreshDiscoveredLibrariesAsync(cancellationToken);
        _service.TriggerPersistentMediaCacheSync(fullRefresh: true);
        return Ok(configuration);
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetSyncStatus(CancellationToken cancellationToken)
    {
        var status = await _service.GetSyncStatusAsync(cancellationToken);
        return Ok(status);
    }

    [HttpGet("items")]
    public async Task<IActionResult> GetItems(
        [FromQuery] string? category,
        [FromQuery] string? serverType,
        [FromQuery] string? libraryId,
        [FromQuery] int? offset,
        [FromQuery] int? limit,
        [FromQuery] bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        var payload = await _service.GetItemsAsync(category, serverType, libraryId, offset, limit, refresh, cancellationToken);
        foreach (var item in payload.Items)
        {
            item.ImageUrl = BuildImageProxyUrl(item.ServerType, item.ImageUrl);
        }
        return Ok(payload);
    }

    [HttpPost("resolve")]
    public async Task<IActionResult> ResolveItem(
        [FromBody] MediaServerSoundtrackResolveRequest request,
        CancellationToken cancellationToken)
    {
        var payload = await _service.ResolveItemSoundtrackAsync(request, cancellationToken);
        if (payload == null)
        {
            return BadRequest(new { error = "Invalid media item payload." });
        }

        payload.ImageUrl = BuildImageProxyUrl(payload.ServerType, payload.ImageUrl);
        return Ok(payload);
    }

    [HttpGet("episodes")]
    public async Task<IActionResult> GetEpisodes(
        [FromQuery] string? serverType,
        [FromQuery] string? libraryId,
        [FromQuery] string? showId,
        [FromQuery] string? seasonId,
        [FromQuery] int? limit,
        CancellationToken cancellationToken)
    {
        var payload = await _service.GetEpisodesAsync(serverType, libraryId, showId, seasonId, limit, cancellationToken);
        payload.ShowImageUrl = BuildImageProxyUrl(payload.ServerType, payload.ShowImageUrl);
        foreach (var season in payload.Seasons)
        {
            season.ImageUrl = BuildImageProxyUrl(payload.ServerType, season.ImageUrl);
        }

        foreach (var episode in payload.Episodes)
        {
            episode.ImageUrl = BuildImageProxyUrl(payload.ServerType, episode.ImageUrl);
        }

        return Ok(payload);
    }

    [HttpGet("image")]
    public async Task<IActionResult> GetImage(
        [FromQuery] string? serverType,
        [FromQuery] string? path,
        CancellationToken cancellationToken)
    {
        var normalizedServerType = NormalizeServerType(serverType);
        if (string.IsNullOrWhiteSpace(normalizedServerType))
        {
            return BadRequest("Invalid server type.");
        }

        var normalizedPath = NormalizeImagePath(path);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return BadRequest("Invalid image path.");
        }

        var authState = await _platformAuthService.LoadAsync();
        var targetUrl = ResolveTargetImageUrl(authState, normalizedServerType, normalizedPath);
        if (string.IsNullOrWhiteSpace(targetUrl))
        {
            return NotFound();
        }

        var client = _httpClientFactory.CreateClient();
        using var response = await client.GetAsync(targetUrl, cancellationToken);
        return await ImageProxyResponseHelper.CreateImageResultAsync(
            this,
            response,
            cache =>
            {
                cache.Private = true;
                cache.MaxAge = TimeSpan.FromMinutes(15);
            },
            cancellationToken);
    }

    private string? BuildImageProxyUrl(string? serverType, string? sourceUrl)
    {
        var normalizedServerType = NormalizeServerType(serverType);
        if (string.IsNullOrWhiteSpace(normalizedServerType))
        {
            return sourceUrl;
        }

        var normalizedPath = NormalizeImagePath(ExtractImagePath(normalizedServerType, sourceUrl));
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return sourceUrl;
        }

        return Url.Action(nameof(GetImage), values: new
        {
            serverType = normalizedServerType,
            path = normalizedPath
        }) ?? $"/api/media-server/soundtracks/image?serverType={Uri.EscapeDataString(normalizedServerType)}&path={Uri.EscapeDataString(normalizedPath)}";
    }

    private static bool IsLibraryPinConfigured(UserPreferencesDto prefs)
    {
        return !string.IsNullOrWhiteSpace(prefs.MediaServerLibraryPinHash)
            && !string.IsNullOrWhiteSpace(prefs.MediaServerLibraryPinSalt);
    }

    private static string NormalizePin(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    private static bool ValidateLibraryPin(string pin, string? encodedSalt, string? encodedHash)
    {
        if (string.IsNullOrWhiteSpace(pin)
            || string.IsNullOrWhiteSpace(encodedSalt)
            || string.IsNullOrWhiteSpace(encodedHash))
        {
            return false;
        }

        byte[] salt;
        byte[] expectedHash;
        try
        {
            salt = Convert.FromBase64String(encodedSalt);
            expectedHash = Convert.FromBase64String(encodedHash);
        }
        catch (FormatException)
        {
            return false;
        }

        var actualHash = DeriveLibraryPinHash(pin, salt);
        return CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
    }

    private static byte[] DeriveLibraryPinHash(string pin, byte[] salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            pin,
            salt,
            LibraryPinPbkdf2Iterations,
            HashAlgorithmName.SHA256,
            LibraryPinHashBytes);
    }

    private static string? ExtractImagePath(string serverType, string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return null;
        }

        var text = sourceUrl.Trim();
        text = UnwrapImageProxyPath(text);
        if (Uri.TryCreate(text, UriKind.Absolute, out var absolute))
        {
            text = absolute.PathAndQuery;
        }

        if (string.Equals(serverType, MediaServerSoundtrackConstants.PlexServer, StringComparison.OrdinalIgnoreCase))
        {
            return RemoveQueryParameter(text, "X-Plex-Token");
        }

        if (string.Equals(serverType, MediaServerSoundtrackConstants.JellyfinServer, StringComparison.OrdinalIgnoreCase))
        {
            return RemoveQueryParameter(text, "api_key");
        }

        return text;
    }

    private static string UnwrapImageProxyPath(string sourceUrl)
    {
        const int maxDepth = 4;
        var current = sourceUrl;
        for (var depth = 0; depth < maxDepth; depth++)
        {
            if (!TryExtractProxyPath(current, out var extracted))
            {
                break;
            }

            current = extracted!;
        }

        return current;
    }

    private static bool TryExtractProxyPath(string value, out string? extractedPath)
    {
        extractedPath = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = DecodePercentEncoded(value.Trim());
        if (Uri.TryCreate(text, UriKind.Absolute, out var absolute))
        {
            text = DecodePercentEncoded(absolute.PathAndQuery);
        }

        if (!text.StartsWith("/api/media-server/soundtracks/image", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var queryIndex = text.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex < 0 || queryIndex >= text.Length - 1)
        {
            return false;
        }

        var query = DecodePercentEncoded(text[(queryIndex + 1)..]);
        var parts = query.Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var equalsIndex = part.IndexOf('=', StringComparison.Ordinal);
            var key = DecodePercentEncoded(equalsIndex >= 0 ? part[..equalsIndex] : part);
            if (!string.Equals(key, "path", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var encoded = equalsIndex >= 0 ? part[(equalsIndex + 1)..] : string.Empty;
            var decoded = DecodePercentEncoded(encoded);
            if (string.IsNullOrWhiteSpace(decoded))
            {
                return false;
            }

            extractedPath = decoded;
            return true;
        }

        return false;
    }

    private static string DecodePercentEncoded(string value)
    {
        var current = value;
        for (var i = 0; i < 4; i++)
        {
            if (!current.Contains('%', StringComparison.Ordinal))
            {
                break;
            }

            var decoded = Uri.UnescapeDataString(current);
            if (string.Equals(decoded, current, StringComparison.Ordinal))
            {
                break;
            }

            current = decoded;
        }

        return current;
    }

    private static string NormalizeServerType(string? serverType)
    {
        var normalized = string.IsNullOrWhiteSpace(serverType) ? string.Empty : serverType.Trim().ToLowerInvariant();
        return normalized switch
        {
            MediaServerSoundtrackConstants.PlexServer => MediaServerSoundtrackConstants.PlexServer,
            MediaServerSoundtrackConstants.JellyfinServer => MediaServerSoundtrackConstants.JellyfinServer,
            _ => string.Empty
        };
    }

    private static string? NormalizeImagePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalized = path.Trim();
        if (normalized.Contains("://", StringComparison.Ordinal))
        {
            return null;
        }

        if (!normalized.StartsWith(UrlPathSeparator))
        {
            normalized = UrlPathSeparator + normalized;
        }

        return normalized;
    }

    private static string RemoveQueryParameter(string pathAndQuery, string parameterName)
    {
        var text = string.IsNullOrWhiteSpace(pathAndQuery) ? string.Empty : pathAndQuery.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var queryIndex = text.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex < 0)
        {
            return text;
        }

        var path = text[..queryIndex];
        var query = text[(queryIndex + 1)..];
        if (string.IsNullOrWhiteSpace(query))
        {
            return path;
        }

        var filtered = query
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part =>
            {
                var equalsIndex = part.IndexOf('=', StringComparison.Ordinal);
                var key = equalsIndex >= 0 ? part[..equalsIndex] : part;
                return !string.Equals(key, parameterName, StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();

        return filtered.Length == 0
            ? path
            : $"{path}?{string.Join("&", filtered)}";
    }

    private static string? ResolveTargetImageUrl(PlatformAuthState authState, string serverType, string normalizedPath)
    {
        if (string.Equals(serverType, MediaServerSoundtrackConstants.PlexServer, StringComparison.OrdinalIgnoreCase))
        {
            var plex = authState.Plex;
            if (string.IsNullOrWhiteSpace(plex?.Url) || string.IsNullOrWhiteSpace(plex.Token))
            {
                return null;
            }

            return BuildUrlWithQueryToken(plex.Url, normalizedPath, "X-Plex-Token", plex.Token);
        }

        if (string.Equals(serverType, MediaServerSoundtrackConstants.JellyfinServer, StringComparison.OrdinalIgnoreCase))
        {
            var jellyfin = authState.Jellyfin;
            if (string.IsNullOrWhiteSpace(jellyfin?.Url) || string.IsNullOrWhiteSpace(jellyfin.ApiKey))
            {
                return null;
            }

            return BuildUrlWithQueryToken(jellyfin.Url, normalizedPath, "api_key", jellyfin.ApiKey);
        }

        return null;
    }

    private static string BuildUrlWithQueryToken(string serverUrl, string pathAndQuery, string key, string token)
    {
        var baseUrl = serverUrl.TrimEnd('/');
        var path = pathAndQuery.StartsWith(UrlPathSeparator)
            ? pathAndQuery
            : UrlPathSeparator + pathAndQuery;

        var separator = path.Contains(QuerySeparator) ? '&' : QuerySeparator;
        var encodedToken = Uri.EscapeDataString(token);
        return $"{baseUrl}{path}{separator}{key}={encodedToken}";
    }
}
