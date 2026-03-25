using System.Globalization;
using DeezSpoTag.Web.Services.AutoTag;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/bandcamp/search")]
[Authorize]
public sealed class BandcampSearchApiController : ControllerBase
{
    private const string BandcampSource = "bandcamp";
    private const string TrackType = "track";
    private const string AlbumType = "album";
    private const string ArtistType = "artist";
    private const string PlaylistType = "playlist";
    private const string TrackFilter = "t";
    private const string AlbumFilter = "a";
    private const string ArtistFilter = "b";
    private readonly BandcampClient _bandcampClient;
    private readonly ILogger<BandcampSearchApiController> _logger;

    public BandcampSearchApiController(
        BandcampClient bandcampClient,
        ILogger<BandcampSearchApiController> logger)
    {
        _bandcampClient = bandcampClient;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Search(
        [FromQuery] string query,
        [FromQuery] string? type = null,
        [FromQuery] int limit = 25,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return BadRequest(new { available = false, error = "Query is required." });
        }

        limit = Math.Clamp(limit, 1, 50);
        var normalizedType = NormalizeType(type);
        if (normalizedType == PlaylistType)
        {
            return Ok(new
            {
                available = true,
                tracks = Array.Empty<object>(),
                albums = Array.Empty<object>(),
                artists = Array.Empty<object>(),
                playlists = Array.Empty<object>()
            });
        }

        var filter = normalizedType switch
        {
            AlbumType => AlbumFilter,
            ArtistType => ArtistFilter,
            _ => TrackFilter
        };

        try
        {
            var results = (await _bandcampClient.SearchAsync(query, filter, cancellationToken))
                .Where(item => item != null)
                .Take(limit)
                .ToList();

            if (normalizedType == ArtistType)
            {
                results = results
                    .Where(result => !result.IsLabel)
                    .ToList();
            }

            var tracks = normalizedType is null or TrackType
                ? results.Where(result => string.Equals(result.Type, TrackFilter, StringComparison.OrdinalIgnoreCase))
                    .Select(MapTrack)
                    .ToList()
                : new List<object>();
            var albums = normalizedType is null or AlbumType
                ? results.Where(result => string.Equals(result.Type, AlbumFilter, StringComparison.OrdinalIgnoreCase))
                    .Select(MapAlbum)
                    .ToList()
                : new List<object>();
            var artists = normalizedType is null or ArtistType
                ? results.Where(result => string.Equals(result.Type, ArtistFilter, StringComparison.OrdinalIgnoreCase) && !result.IsLabel)
                    .Select(MapArtist)
                    .ToList()
                : new List<object>();

            return Ok(new
            {
                available = true,
                tracks,
                albums,
                artists,
                playlists = Array.Empty<object>(),
                totals = new Dictionary<string, int>
                {
                    ["tracks"] = tracks.Count,
                    ["albums"] = albums.Count,
                    ["artists"] = artists.Count,
                    ["playlists"] = 0
                }
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Bandcamp search failed for query {Query}", query);
            return StatusCode(500, new { available = false, error = "Bandcamp search failed." });
        }
    }

    private static object MapTrack(BandcampSearchResult result)
    {
        var url = NormalizeBandcampUrl(result);
        return new
        {
            source = BandcampSource,
            type = TrackType,
            name = result.Name ?? string.Empty,
            artist = result.BandName ?? string.Empty,
            album = result.AlbumName ?? string.Empty,
            image = result.ImageUrl ?? string.Empty,
            bandcampId = result.Id.ToString(CultureInfo.InvariantCulture),
            bandcampType = TrackType,
            bandcampUrl = url,
            externalUrl = url
        };
    }

    private static object MapAlbum(BandcampSearchResult result)
    {
        var url = NormalizeBandcampUrl(result);
        return new
        {
            source = BandcampSource,
            type = AlbumType,
            name = result.Name ?? string.Empty,
            artist = result.BandName ?? string.Empty,
            image = result.ImageUrl ?? string.Empty,
            bandcampId = result.Id.ToString(CultureInfo.InvariantCulture),
            bandcampType = AlbumType,
            bandcampUrl = url,
            externalUrl = url
        };
    }

    private static object MapArtist(BandcampSearchResult result)
    {
        var url = NormalizeBandcampUrl(result);
        return new
        {
            source = BandcampSource,
            type = ArtistType,
            name = result.Name ?? string.Empty,
            image = result.ImageUrl ?? string.Empty,
            bandcampId = result.BandId.ToString(CultureInfo.InvariantCulture),
            bandcampType = ArtistType,
            bandcampUrl = url,
            externalUrl = url
        };
    }

    private static string NormalizeBandcampUrl(BandcampSearchResult result)
    {
        if (Uri.TryCreate(result.ItemUrlPath, UriKind.Absolute, out var absolutePath))
        {
            return absolutePath.ToString();
        }

        if (Uri.TryCreate(result.ItemUrlRoot, UriKind.Absolute, out var absoluteRoot))
        {
            if (string.IsNullOrWhiteSpace(result.ItemUrlPath))
            {
                return absoluteRoot.ToString();
            }

            var relative = result.ItemUrlPath.StartsWith('/')
                ? result.ItemUrlPath
                : $"/{result.ItemUrlPath}";
            if (Uri.TryCreate(absoluteRoot, relative, out var combined))
            {
                return combined.ToString();
            }

            return absoluteRoot.ToString();
        }

        return string.Empty;
    }

    private static string? NormalizeType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return null;
        }

        return type.Trim().ToLowerInvariant() switch
        {
            TrackType => TrackType,
            AlbumType => AlbumType,
            ArtistType => ArtistType,
            PlaylistType => PlaylistType,
            _ => null
        };
    }
}
