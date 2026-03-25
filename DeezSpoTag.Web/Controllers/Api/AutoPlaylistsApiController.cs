using DeezSpoTag.Integrations.Plex;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using System.Net;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/autoplaylists")]
[Authorize]
public class AutoPlaylistsApiController : ControllerBase
{
    private readonly PlatformAuthService _authService;
    private readonly PlexApiClient _plexApiClient;
    private readonly DeezSpoTag.Services.Library.LibraryRepository _libraryRepository;
    private readonly IHttpClientFactory _httpClientFactory;

    public AutoPlaylistsApiController(
        PlatformAuthService authService,
        PlexApiClient plexApiClient,
        DeezSpoTag.Services.Library.LibraryRepository libraryRepository,
        IHttpClientFactory httpClientFactory)
    {
        _authService = authService;
        _plexApiClient = plexApiClient;
        _libraryRepository = libraryRepository;
        _httpClientFactory = httpClientFactory;
    }

    [HttpGet]
    public async Task<IActionResult> GetPlaylists([FromQuery] string? librarySectionId, CancellationToken cancellationToken)
    {
        var state = await _authService.LoadAsync();
        var plex = state.Plex;
        if (string.IsNullOrWhiteSpace(plex?.Url) || string.IsNullOrWhiteSpace(plex.Token))
        {
            return Ok(new
            {
                source = "Plex",
                playlists = Array.Empty<object>(),
                warning = "Plex is not configured. Connect Plex in Login to load auto playlists."
            });
        }

        var playlists = await _plexApiClient.GetPlaylistsAsync(plex.Url, plex.Token, cancellationToken);
        var allowedSectionIds = await GetAllowedMusicSectionIdsAsync(plex.Url, plex.Token, cancellationToken);
        var filtered = playlists
            .Where(p => string.IsNullOrWhiteSpace(p.LibrarySectionId) || allowedSectionIds.Contains(p.LibrarySectionId))
            .Where(p => string.IsNullOrWhiteSpace(librarySectionId) || string.Equals(p.LibrarySectionId, librarySectionId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var payload = await Task.WhenAll(filtered.Select(async p =>
        {
            var libraryInfo = await ResolveLibraryInfoForPlaylistAsync(plex.Url, plex.Token, p.Id, cancellationToken);
            return new
            {
                id = p.Id,
                name = p.Title,
                subtitle = string.IsNullOrWhiteSpace(p.PlaylistType) ? "Plex" : p.PlaylistType,
                description = p.Summary,
                trackCount = p.TrackCount,
                duration = FormatDuration(p.DurationMs),
                updated = p.UpdatedAt?.ToString("MMM d, yyyy"),
                source = "Plex",
                coverUrl = BuildPlexImageProxyUrl(p.CoverUrl, p.UpdatedAt),
                libraryId = libraryInfo?.Id,
                libraryName = libraryInfo?.Name
            };
        }));

        return Ok(new
        {
            source = "Plex",
            playlists = payload
        });
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetPlaylist(string id, CancellationToken cancellationToken)
    {
        var state = await _authService.LoadAsync();
        var plex = state.Plex;
        if (string.IsNullOrWhiteSpace(plex?.Url) || string.IsNullOrWhiteSpace(plex.Token))
        {
            return Ok(new { playlist = default(object) });
        }

        var playlist = await _plexApiClient.GetPlaylistAsync(plex.Url, plex.Token, id, cancellationToken);
        if (playlist is null)
        {
            return NotFound();
        }

        var allowedSectionIds = await GetAllowedMusicSectionIdsAsync(plex.Url, plex.Token, cancellationToken);
        if (!string.IsNullOrWhiteSpace(playlist.LibrarySectionId) && !allowedSectionIds.Contains(playlist.LibrarySectionId))
        {
            return NotFound();
        }

        var tracks = await _plexApiClient.GetPlaylistItemsAsync(plex.Url, plex.Token, id, cancellationToken);
        var libraryInfo = await ResolveLibraryInfoAsync(tracks, cancellationToken);
        return Ok(new
        {
            playlist = new
            {
                id = playlist.Id,
                name = playlist.Title,
                description = playlist.Summary,
                trackCount = playlist.TrackCount,
                duration = FormatDuration(playlist.DurationMs),
                updated = playlist.UpdatedAt?.ToString("MMM d, yyyy"),
                coverUrl = BuildPlexImageProxyUrl(playlist.CoverUrl, playlist.UpdatedAt),
                librarySectionId = playlist.LibrarySectionId,
                libraryId = libraryInfo?.Id,
                libraryName = libraryInfo?.Name,
                tracks = tracks.Select(t => new
                {
                    id = t.Id,
                    title = t.Title,
                    artist = t.Artist,
                    album = t.Album,
                    coverUrl = BuildPlexImageProxyUrl(t.CoverUrl),
                    durationMs = t.DurationMs,
                    streamUrl = BuildPlexStreamProxyUrl(t.StreamUrl),
                    filePath = t.FilePath
                })
            }
        });
    }

    [HttpGet("image")]
    public async Task<IActionResult> GetPlexImage([FromQuery] string? path, CancellationToken cancellationToken)
    {
        var proxyContext = await ResolvePlexProxyContextAsync(path);
        if (proxyContext.ErrorResult != null)
        {
            return proxyContext.ErrorResult;
        }

        var client = _httpClientFactory.CreateClient();
        using var response = await client.GetAsync(proxyContext.TargetUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return StatusCode((int)response.StatusCode);
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "image/jpeg";
        var typedHeaders = Response.GetTypedHeaders();
        typedHeaders.CacheControl = new CacheControlHeaderValue
        {
            NoStore = true,
            NoCache = true,
            MustRevalidate = true
        };
        return File(bytes, contentType);
    }

    [HttpGet("stream")]
    public async Task<IActionResult> GetPlexStream(
        [FromQuery] string? path,
        [FromHeader(Name = "Range")] string? rangeHeader,
        CancellationToken cancellationToken)
    {
        var proxyContext = await ResolvePlexProxyContextAsync(path);
        if (proxyContext.ErrorResult != null)
        {
            return proxyContext.ErrorResult;
        }

        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, proxyContext.TargetUrl);
        if (!string.IsNullOrWhiteSpace(rangeHeader))
        {
            request.Headers.TryAddWithoutValidation("Range", rangeHeader);
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            return StatusCode(StatusCodes.Status416RangeNotSatisfiable);
        }
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.PartialContent)
        {
            return StatusCode((int)response.StatusCode);
        }

        Response.StatusCode = (int)response.StatusCode;
        CopyHeaderIfPresent(response, "Accept-Ranges");
        CopyHeaderIfPresent(response, "Content-Range");
        CopyHeaderIfPresent(response, "Content-Length");
        CopyHeaderIfPresent(response, "Cache-Control");
        CopyHeaderIfPresent(response, "ETag");
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "audio/mpeg";
        Response.ContentType = contentType;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await stream.CopyToAsync(Response.Body, cancellationToken);
        return new EmptyResult();
    }

    private async Task<(string TargetUrl, IActionResult? ErrorResult)> ResolvePlexProxyContextAsync(string? path)
    {
        var normalizedPath = NormalizePlexImagePath(path);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return (string.Empty, BadRequest("Invalid path"));
        }

        var state = await _authService.LoadAsync();
        var plex = state.Plex;
        if (string.IsNullOrWhiteSpace(plex?.Url) || string.IsNullOrWhiteSpace(plex.Token))
        {
            return (string.Empty, NotFound());
        }

        return (BuildPlexImageUrl(plex.Url, plex.Token, normalizedPath), null);
    }

    private async Task<HashSet<string>> GetAllowedMusicSectionIdsAsync(string serverUrl, string token, CancellationToken cancellationToken)
    {
        var sections = await _plexApiClient.GetLibrarySectionsAsync(serverUrl, token, cancellationToken);
        var allowed = sections
            .Where(section => string.Equals(section.Type, "artist", StringComparison.OrdinalIgnoreCase))
            .Where(section => !section.Title.Contains("audiobook", StringComparison.OrdinalIgnoreCase))
            .Select(section => section.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return allowed;
    }

    private static string FormatDuration(long durationMs)
    {
        if (durationMs <= 0)
        {
            return "—";
        }

        var totalMinutes = (int)Math.Round(durationMs / 60000.0);
        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        return hours > 0 ? $"{hours} hr {minutes} min" : $"{minutes} min";
    }

    private async Task<DeezSpoTag.Services.Library.LibraryDto?> ResolveLibraryInfoAsync(
        IReadOnlyList<PlexPlaylistTrack> tracks,
        CancellationToken cancellationToken)
    {
        if (!_libraryRepository.IsConfigured || tracks.Count == 0)
        {
            return null;
        }

        var firstPath = tracks.Select(t => t.FilePath).FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        if (string.IsNullOrWhiteSpace(firstPath))
        {
            return null;
        }

        var folder = await _libraryRepository.ResolveFolderForPathAsync(firstPath, cancellationToken);
        if (folder is null || folder.LibraryId is null)
        {
            return null;
        }

        return new DeezSpoTag.Services.Library.LibraryDto(folder.LibraryId.Value, folder.LibraryName ?? "Library");
    }

    private async Task<DeezSpoTag.Services.Library.LibraryDto?> ResolveLibraryInfoForPlaylistAsync(
        string serverUrl,
        string token,
        string playlistId,
        CancellationToken cancellationToken)
    {
        if (!_libraryRepository.IsConfigured)
        {
            return null;
        }

        var tracks = await _plexApiClient.GetPlaylistItemsAsync(serverUrl, token, playlistId, cancellationToken);
        return await ResolveLibraryInfoAsync(tracks, cancellationToken);
    }

    private string? BuildPlexImageProxyUrl(string? sourceUrl, DateTimeOffset? version = null)
    {
        var path = ExtractPlexImagePath(sourceUrl);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var versionToken = version?.ToUnixTimeSeconds();
        return Url.ActionLink(nameof(GetPlexImage), values: new { path, v = versionToken });
    }

    private string? BuildPlexStreamProxyUrl(string? sourceUrl)
    {
        var path = ExtractPlexImagePath(sourceUrl);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return Url.ActionLink(nameof(GetPlexStream), values: new { path });
    }

    private static string? ExtractPlexImagePath(string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return null;
        }

        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            return NormalizePlexImagePath(sourceUrl);
        }

        var query = ParseQueryString(uri.Query)
            .Where(static kvp => !string.Equals(kvp.Key, "X-Plex-Token", StringComparison.OrdinalIgnoreCase))
            .Select(static kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}");
        var queryString = string.Join("&", query);
        var path = uri.AbsolutePath;
        if (!string.IsNullOrWhiteSpace(queryString))
        {
            path = $"{path}?{queryString}";
        }

        return NormalizePlexImagePath(path);
    }

    private static string? NormalizePlexImagePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var value = path.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
        {
            value = absolute.PathAndQuery;
        }

        if (!value.StartsWith('/'))
        {
            value = "/" + value.TrimStart('/');
        }

        return value.Contains("..", StringComparison.Ordinal) ? null : value;
    }

    private static string BuildPlexImageUrl(string serverUrl, string token, string pathAndQuery)
    {
        var target = new Uri(new Uri(serverUrl.TrimEnd('/')), pathAndQuery);
        var builder = new UriBuilder(target);
        var query = ParseQueryString(builder.Query)
            .Where(static kvp => !string.Equals(kvp.Key, "X-Plex-Token", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(static kvp => kvp.Key, static kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
        query["X-Plex-Token"] = token;
        builder.Query = string.Join("&", query.Select(static kvp =>
            $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
        return builder.Uri.ToString();
    }

    private static IEnumerable<KeyValuePair<string, string>> ParseQueryString(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            yield break;
        }

        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = part.IndexOf('=');
            if (index < 0)
            {
                yield return new KeyValuePair<string, string>(Uri.UnescapeDataString(part), string.Empty);
                continue;
            }

            yield return new KeyValuePair<string, string>(
                Uri.UnescapeDataString(part[..index]),
                Uri.UnescapeDataString(part[(index + 1)..]));
        }
    }

    private void CopyHeaderIfPresent(HttpResponseMessage response, string headerName)
    {
        if (response.Headers.TryGetValues(headerName, out var values) ||
            response.Content.Headers.TryGetValues(headerName, out values))
        {
            Response.Headers[headerName] = values.ToArray();
        }
    }
}
