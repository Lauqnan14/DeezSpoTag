using Microsoft.AspNetCore.Mvc;
using DeezSpoTag.Web.Services;
using System.Net;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/boomplay")]
[Authorize]
public sealed class BoomplayApiController : ControllerBase
{
    private const string PlaylistType = "playlist";
    private const string TrendingType = "trending";
    private const string PlaylistIdRequiredMessage = "Playlist id is required.";
    private static readonly Uri BoomplayReferrerUri = new("https://www.boomplay.com");
    private readonly BoomplayMetadataService _boomplayMetadataService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<BoomplayApiController> _logger;

    public BoomplayApiController(
        BoomplayMetadataService boomplayMetadataService,
        IHttpClientFactory httpClientFactory,
        ILogger<BoomplayApiController> logger)
    {
        _boomplayMetadataService = boomplayMetadataService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    [HttpGet("parse-link")]
    public IActionResult ParseLink([FromQuery] string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return Ok(new
            {
                type = string.Empty,
                id = string.Empty,
                error = "URL is required."
            });
        }

        if (BoomplayMetadataService.TryParseBoomplayUrl(url, out var type, out var id))
        {
            return Ok(new
            {
                type,
                id,
                error = string.Empty
            });
        }

        return Ok(new
        {
            type = string.Empty,
            id = string.Empty,
            error = "Link is not recognizable."
        });
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search(
        [FromQuery] string query,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return BadRequest(new { error = "Query is required." });
        }

        limit = Math.Clamp(limit, 1, 30);

        try
        {
            var tracks = await _boomplayMetadataService.SearchSongsAsync(query, limit, cancellationToken);
            var results = tracks
                .Where(static track => track != null)
                .Select(MapSearchTrack)
                .ToList();

            return Ok(new
            {
                query,
                total = results.Count,
                tracks = results
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Boomplay search failed for query Query");
            return StatusCode(500, new { error = "Failed to search Boomplay." });
        }
    }

    private static object MapSearchTrack(BoomplayTrackMetadata track, int index)
    {
        var trackUrl = ResolveTrackUrl(track);
        var genres = track.Genres
            .Where(static genre => !string.IsNullOrWhiteSpace(genre))
            .Select(static genre => genre.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new
        {
            id = track.Id,
            boomplayId = track.Id,
            title = DecodeBoomplayText(track.Title),
            duration = track.DurationMs > 0 ? track.DurationMs / 1000 : 0,
            durationMs = track.DurationMs > 0 ? track.DurationMs : 0,
            isrc = track.Isrc,
            track_position = index + 1,
            artist = MapTrackArtist(track),
            albumArtist = DecodeBoomplayText(track.AlbumArtist),
            album = MapTrackAlbum(track),
            genres,
            genreSource = GetGenreSource(track, genres),
            releaseDate = track.ReleaseDate ?? string.Empty,
            trackNumber = track.TrackNumber > 0 ? track.TrackNumber : (int?)null,
            discNumber = track.DiscNumber > 0 ? track.DiscNumber : (int?)null,
            composer = track.Composer ?? string.Empty,
            publisher = track.Publisher ?? string.Empty,
            bpm = track.Bpm > 0 ? track.Bpm : (int?)null,
            key = track.Key ?? string.Empty,
            language = track.Language ?? string.Empty,
            link = trackUrl,
            sourceUrl = trackUrl
        };
    }

    private static string GetGenreSource(BoomplayTrackMetadata track, List<string> genres)
    {
        if (track.HasStreamGenreMetadata)
        {
            return "stream";
        }

        return genres.Count > 0 ? "html" : "none";
    }

    private static string ResolveTrackUrl(BoomplayTrackMetadata track)
        => !string.IsNullOrWhiteSpace(track.Url)
            ? track.Url
            : $"https://www.boomplay.com/songs/{track.Id}";

    private static string DecodeBoomplayText(string? value)
        => WebUtility.HtmlDecode(value ?? string.Empty).Trim();

    private static object MapTrackArtist(BoomplayTrackMetadata track)
        => new { id = string.Empty, name = DecodeBoomplayText(track.Artist) };

    private static object MapTrackAlbum(BoomplayTrackMetadata track)
        => new
        {
            id = string.Empty,
            title = DecodeBoomplayText(track.Album),
            cover_medium = DecodeBoomplayText(track.CoverUrl)
        };

    [HttpGet("tracklist")]
    public async Task<IActionResult> GetTracklist(
        [FromQuery] string id,
        [FromQuery] string type,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(type))
        {
            return BadRequest(new { error = "ID and type are required." });
        }

        try
        {
            var normalizedType = type.Trim().ToLowerInvariant();
            if (normalizedType == "track")
            {
                var track = await _boomplayMetadataService.GetSongAsync(id, cancellationToken);
                if (track == null)
                {
                    return NotFound(new { error = "Track not found." });
                }

                return Ok(MapSingleTrack(track));
            }

            if (normalizedType == PlaylistType)
            {
                var playlist = await _boomplayMetadataService.GetPlaylistAsync(id, cancellationToken);
                if (playlist == null)
                {
                    return NotFound(new { error = "Playlist not found." });
                }

                return Ok(MapPlaylist(playlist));
            }

            if (normalizedType == TrendingType)
            {
                var playlist = await _boomplayMetadataService.GetTrendingSongsAsync(includeTracks: true, cancellationToken);
                if (playlist == null)
                {
                    return NotFound(new { error = "Trending songs not found." });
                }

                return Ok(MapPlaylist(playlist));
            }

            return BadRequest(new { error = "Unsupported Boomplay type." });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Boomplay tracklist fetch failed for Type:Id");
            return StatusCode(500, new { error = "Failed to load Boomplay tracklist." });
        }
    }

    [HttpGet("playlist/recommendations")]
    public async Task<IActionResult> GetPlaylistRecommendations(
        [FromQuery] string id,
        [FromQuery] string type,
        [FromQuery] int limit = 12,
        CancellationToken cancellationToken = default)
    {
        var normalizedType = (type ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedType != PlaylistType && normalizedType != TrendingType)
        {
            return BadRequest(new { available = false, error = "Unsupported Boomplay type for recommendations." });
        }

        if (normalizedType == PlaylistType && string.IsNullOrWhiteSpace(id))
        {
            return BadRequest(new { available = false, error = PlaylistIdRequiredMessage });
        }

        limit = Math.Clamp(limit, 1, 48);

        try
        {
            var sections = await _boomplayMetadataService.GetPlaylistRecommendationsAsync(
                id,
                isTrending: normalizedType == TrendingType,
                limit,
                cancellationToken);

            return Ok(new
            {
                available = true,
                sections = MapPlaylistRecommendationSections(sections)
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Boomplay recommendations fetch failed for Type:Id");
            return StatusCode(500, new { available = false, error = "Failed to load Boomplay recommendations." });
        }
    }

    [HttpGet("stream/{id}")]
    public async Task<IActionResult> StreamTrack([FromRoute] string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return BadRequest(new { error = "Track id is required." });
        }

        try
        {
            var mediaUrl = await _boomplayMetadataService.ResolveSongStreamUrlAsync(id, cancellationToken);
            if (string.IsNullOrWhiteSpace(mediaUrl))
            {
                return NotFound(new { error = "Boomplay stream unavailable." });
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, mediaUrl);
            request.Headers.TryAddWithoutValidation("x-boomplay-ref", "Boomplay_WEBV1");
            request.Headers.Referrer = BoomplayReferrerUri;

            using var response = await _httpClientFactory
                .CreateClient(nameof(BoomplayMetadataService))
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return StatusCode(StatusCodes.Status502BadGateway, new { error = "Boomplay stream request failed." });
            }

            Response.ContentType = response.Content.Headers.ContentType?.MediaType ?? "audio/mpeg";
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await stream.CopyToAsync(Response.Body, cancellationToken);
            return new EmptyResult();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Boomplay stream failed for track TrackId");
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "Boomplay stream failed." });
        }
    }

    private static object MapSingleTrack(BoomplayTrackMetadata track)
    {
        var title = WebUtility.HtmlDecode(track.Title ?? string.Empty).Trim();
        var artist = WebUtility.HtmlDecode(track.Artist ?? string.Empty).Trim();
        var album = WebUtility.HtmlDecode(track.Album ?? string.Empty).Trim();
        var cover = WebUtility.HtmlDecode(track.CoverUrl ?? string.Empty).Trim();
        var trackUrl = !string.IsNullOrWhiteSpace(track.Url)
            ? track.Url
            : $"https://www.boomplay.com/songs/{track.Id}";

        return new
        {
            id = track.Id,
            boomplayId = track.Id,
            title = title,
            description = string.Empty,
            cover_big = cover,
            cover_xl = cover,
            picture_big = cover,
            picture_xl = cover,
            nb_tracks = 1,
            artist = new { id = string.Empty, name = artist },
            tracks = new[]
            {
                new
                {
                    id = string.Empty,
                    boomplayId = track.Id,
                    title = title,
                    duration = track.DurationMs > 0 ? track.DurationMs / 1000 : 0,
                    durationMs = track.DurationMs > 0 ? track.DurationMs : 0,
                    isrc = track.Isrc,
                    track_position = track.TrackNumber > 0 ? track.TrackNumber : 1,
                    artist = new { id = string.Empty, name = artist },
                    album = new
                    {
                        id = string.Empty,
                        title = album,
                        cover_medium = cover
                    },
                    link = trackUrl,
                    sourceUrl = trackUrl
                }
            }
        };
    }

    private static object MapPlaylist(BoomplayPlaylistMetadata playlist)
    {
        var tracks = playlist.Tracks
            .Select((track, index) =>
            {
                var trackUrl = ResolveTrackUrl(track);
                return new
                {
                    id = track.Id,
                    boomplayId = track.Id,
                    title = DecodeBoomplayText(track.Title),
                    duration = track.DurationMs > 0 ? track.DurationMs / 1000 : 0,
                    durationMs = track.DurationMs > 0 ? track.DurationMs : 0,
                    isrc = track.Isrc,
                    track_position = index + 1,
                    artist = MapTrackArtist(track),
                    album = MapTrackAlbum(track),
                    link = trackUrl,
                    sourceUrl = trackUrl
                };
            })
            .ToList();

        return new
        {
            id = playlist.Id,
            title = playlist.Title,
            description = playlist.Description,
            picture_big = playlist.ImageUrl,
            picture_xl = playlist.ImageUrl,
            cover_big = playlist.ImageUrl,
            cover_xl = playlist.ImageUrl,
            nb_tracks = Math.Max(tracks.Count, playlist.TrackIds.Count),
            creator = new { id = string.Empty, name = "Boomplay" },
            tracks
        };
    }

    private static List<object> MapPlaylistRecommendationSections(IReadOnlyList<BoomplayRecommendationSection> sections)
    {
        return sections
            .Where(static section => section.Items.Count > 0)
            .Select(section => (object)new
            {
                title = section.Title,
                items = section.Items.Select(item => new
                {
                    source = "boomplay",
                    type = "playlist",
                    id = item.Id,
                    url = item.Url,
                    name = item.Name,
                    description = item.Description,
                    imageUrl = item.ImageUrl
                }).ToList()
            })
            .ToList();
    }
}
