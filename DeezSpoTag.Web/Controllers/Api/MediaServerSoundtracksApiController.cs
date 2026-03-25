using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/media-server/soundtracks")]
[Authorize]
public sealed class MediaServerSoundtracksApiController : ControllerBase
{
    private readonly MediaServerSoundtrackService _service;
    private readonly PlatformAuthService _platformAuthService;
    private readonly IHttpClientFactory _httpClientFactory;

    public MediaServerSoundtracksApiController(
        MediaServerSoundtrackService service,
        PlatformAuthService platformAuthService,
        IHttpClientFactory httpClientFactory)
    {
        _service = service;
        _platformAuthService = platformAuthService;
        _httpClientFactory = httpClientFactory;
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

    [HttpPost("sync")]
    public async Task<IActionResult> SyncLibraries(CancellationToken cancellationToken)
    {
        var configuration = await _service.RefreshDiscoveredLibrariesAsync(cancellationToken);
        return Ok(configuration);
    }

    [HttpGet("items")]
    public async Task<IActionResult> GetItems(
        [FromQuery] string? category,
        [FromQuery] string? serverType,
        [FromQuery] string? libraryId,
        [FromQuery] int? offset,
        [FromQuery] int? limit,
        CancellationToken cancellationToken)
    {
        var payload = await _service.GetItemsAsync(category, serverType, libraryId, offset, limit, cancellationToken);
        foreach (var item in payload.Items)
        {
            item.ImageUrl = BuildImageProxyUrl(item.ServerType, item.ImageUrl);
        }
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
        if (!response.IsSuccessStatusCode)
        {
            return StatusCode((int)response.StatusCode);
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "image/jpeg";
        var typedHeaders = Response.GetTypedHeaders();
        typedHeaders.CacheControl = new CacheControlHeaderValue
        {
            Private = true,
            MaxAge = TimeSpan.FromMinutes(15)
        };

        return File(bytes, contentType);
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

    private static string? ExtractImagePath(string serverType, string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return null;
        }

        var text = sourceUrl.Trim();
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

        if (!normalized.StartsWith("/", StringComparison.Ordinal))
        {
            normalized = "/" + normalized;
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
        var path = pathAndQuery.StartsWith("/", StringComparison.Ordinal)
            ? pathAndQuery
            : "/" + pathAndQuery;

        var separator = path.Contains("?", StringComparison.Ordinal) ? '&' : '?';
        var encodedToken = Uri.EscapeDataString(token);
        return $"{baseUrl}{path}{separator}{key}={encodedToken}";
    }
}
