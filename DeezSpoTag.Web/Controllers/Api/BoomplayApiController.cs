using Microsoft.AspNetCore.Mvc;
using DeezSpoTag.Web.Services;
using System.Net;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Microsoft.AspNetCore.Authorization;
using DeezSpoTag.Services.Library;

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
    private readonly LibraryRepository? _libraryRepository;
    private readonly BoomplayWatchlistMappingService? _boomplayWatchlistMappingService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<BoomplayApiController> _logger;

    public BoomplayApiController(
        BoomplayMetadataService boomplayMetadataService,
        LibraryRepository? libraryRepository,
        BoomplayWatchlistMappingService? boomplayWatchlistMappingService,
        IHttpClientFactory httpClientFactory,
        ILogger<BoomplayApiController> logger)
    {
        _boomplayMetadataService = boomplayMetadataService;
        _libraryRepository = libraryRepository;
        _boomplayWatchlistMappingService = boomplayWatchlistMappingService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    [HttpGet("parse-link")]
    public async Task<IActionResult> ParseLink(
        [FromQuery] string url,
        CancellationToken cancellationToken)
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
            string? resolvedId;
            try
            {
                resolvedId = await _boomplayMetadataService.ResolveContentIdAsync(type, id, cancellationToken);
            }
            catch (BoomplaySourceException ex)
            {
                return Ok(new
                {
                    type = string.Empty,
                    id = string.Empty,
                    error = ex.FailureCode,
                    message = ResolveSourceFailureMessage(ex.FailureCode)
                });
            }

            if (string.IsNullOrWhiteSpace(resolvedId))
            {
                return Ok(new
                {
                    type = string.Empty,
                    id = string.Empty,
                    error = BoomplayFailureCodes.ItemUnresolved,
                    message = "Boomplay item could not be resolved."
                });
            }

            return Ok(new
            {
                type,
                id = resolvedId,
                canonicalUrl = url.Trim(),
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

    private static string ResolveSourceFailureMessage(string failureCode)
        => failureCode switch
        {
            BoomplayFailureCodes.SessionMissing => "A verified Boomplay browser session is required.",
            BoomplayFailureCodes.SessionChallenged => "Boomplay challenged the saved browser session. Save a fresh cookie and try again.",
            _ => "Boomplay item could not be resolved."
        };

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
            moods = NormalizeValues(track.Moods),
            tags = track.Tags,
            fieldSources = track.FieldSources,
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

    private static List<string> NormalizeValues(IEnumerable<string> values)
        => values.Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

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

                var mapping = await GetMappingsAsync([track], cancellationToken);
                return Ok(MapSingleTrack(track, mapping));
            }

            if (normalizedType == PlaylistType)
            {
                var resolvedId = await _boomplayMetadataService.ResolveContentIdAsync(
                    PlaylistType,
                    id,
                    cancellationToken);
                if (string.IsNullOrWhiteSpace(resolvedId))
                {
                    return NotFound(new { error = "Playlist not found." });
                }

                var playlist = await _boomplayMetadataService.GetPlaylistAsync(resolvedId, cancellationToken);
                if (playlist == null)
                {
                    return NotFound(new { error = "Playlist not found." });
                }

                var mappings = await GetMappingsAsync(playlist.Tracks, cancellationToken);
                return Ok(MapPlaylist(playlist, mappings));
            }

            if (normalizedType == TrendingType)
            {
                var playlist = await _boomplayMetadataService.GetTrendingSongsAsync(includeTracks: true, cancellationToken);
                if (playlist == null)
                {
                    return NotFound(new { error = "Trending songs not found." });
                }

                var mappings = await GetMappingsAsync(playlist.Tracks, cancellationToken);
                return Ok(MapPlaylist(playlist, mappings));
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

    [HttpGet("resolve-deezer")]
    public async Task<IActionResult> ResolveDeezer(
        [FromQuery] string boomplayId,
        [FromQuery] string? url,
        [FromQuery] string? title,
        [FromQuery] string? artist,
        [FromQuery] string? album,
        [FromQuery] string? isrc,
        [FromQuery] int? durationMs,
        [FromQuery] string? coverUrl,
        CancellationToken cancellationToken)
    {
        var normalizedId = boomplayId?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedId)
            && BoomplayMetadataService.TryParseBoomplayUrl(url ?? string.Empty, out var type, out var parsedId)
            && string.Equals(type, "track", StringComparison.OrdinalIgnoreCase))
        {
            normalizedId = parsedId;
        }

        if (string.IsNullOrWhiteSpace(normalizedId))
        {
            return BadRequest(new { available = false, reasonCode = "missing_boomplay_id" });
        }

        if (_boomplayWatchlistMappingService == null)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { available = false, reasonCode = "mapping_service_unavailable" });
        }

        var mapped = AssertSingle(await _boomplayWatchlistMappingService.ResolveTracksAsync(
            [
                new BoomplayWatchlistTrackInput(
                    normalizedId,
                    url,
                    title,
                    artist,
                    album,
                    isrc,
                    durationMs,
                    coverUrl)
            ],
            cancellationToken));

        return Ok(new
        {
            available = mapped.IsMatched,
            deezerId = mapped.DeezerTrackId ?? string.Empty,
            mappingStatus = mapped.MappingStatus,
            reasonCode = mapped.IsMatched ? string.Empty : "no_match"
        });
    }

    private static BoomplayWatchlistMappedTrack AssertSingle(
        IReadOnlyList<BoomplayWatchlistMappedTrack> mappings)
        => mappings.Count == 1
            ? mappings[0]
            : throw new InvalidOperationException("Boomplay mapping returned an invalid result count.");

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

    private async Task<IReadOnlyDictionary<string, BoomplayDeezerTrackMappingDto>> GetMappingsAsync(
        IEnumerable<BoomplayTrackMetadata> tracks,
        CancellationToken cancellationToken)
        => _libraryRepository?.IsConfigured == true
            ? await _libraryRepository.GetBoomplayDeezerTrackMappingsAsync(
                tracks.Select(static track => track.Id),
                cancellationToken)
            : new Dictionary<string, BoomplayDeezerTrackMappingDto>(StringComparer.Ordinal);

    private static object MapSingleTrack(
        BoomplayTrackMetadata track,
        IReadOnlyDictionary<string, BoomplayDeezerTrackMappingDto> mappings)
    {
        var title = WebUtility.HtmlDecode(track.Title ?? string.Empty).Trim();
        var artist = WebUtility.HtmlDecode(track.Artist ?? string.Empty).Trim();
        var album = WebUtility.HtmlDecode(track.Album ?? string.Empty).Trim();
        var cover = WebUtility.HtmlDecode(track.CoverUrl ?? string.Empty).Trim();
        var trackUrl = !string.IsNullOrWhiteSpace(track.Url)
            ? track.Url
            : $"https://www.boomplay.com/songs/{track.Id}";

        mappings.TryGetValue(track.Id, out var mapping);
        var deezerId = GetVerifiedDeezerId(mapping);
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
                    id = track.Id,
                    deezerId,
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
                    genres = NormalizeValues(track.Genres),
                    moods = NormalizeValues(track.Moods),
                    tags = track.Tags,
                    fieldSources = track.FieldSources,
                    link = trackUrl,
                    sourceUrl = trackUrl
                }
            }
        };
    }

    private static object MapPlaylist(
        BoomplayPlaylistMetadata playlist,
        IReadOnlyDictionary<string, BoomplayDeezerTrackMappingDto> mappings)
    {
        var tracks = playlist.Tracks
            .Select((track, index) =>
            {
                var trackUrl = ResolveTrackUrl(track);
                mappings.TryGetValue(track.Id, out var mapping);
                var deezerId = GetVerifiedDeezerId(mapping);
                return new
                {
                    id = track.Id,
                    deezerId,
                    boomplayId = track.Id,
                    title = DecodeBoomplayText(track.Title),
                    duration = track.DurationMs > 0 ? track.DurationMs / 1000 : 0,
                    durationMs = track.DurationMs > 0 ? track.DurationMs : 0,
                    isrc = track.Isrc,
                    track_position = index + 1,
                    artist = MapTrackArtist(track),
                    album = MapTrackAlbum(track),
                    genres = NormalizeValues(track.Genres),
                    moods = NormalizeValues(track.Moods),
                    tags = track.Tags,
                    fieldSources = track.FieldSources,
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
            creator = new
            {
                id = string.Empty,
                name = string.IsNullOrWhiteSpace(playlist.CreatorName) ? "Boomplay" : playlist.CreatorName
            },
            tracks
        };
    }

    private static string GetVerifiedDeezerId(BoomplayDeezerTrackMappingDto? mapping)
        => mapping != null
           && string.Equals(
               mapping.Status,
               BoomplayWatchlistMappingService.MatchedStatus,
               StringComparison.OrdinalIgnoreCase)
           && !string.IsNullOrWhiteSpace(mapping.DeezerTrackId)
            ? mapping.DeezerTrackId.Trim()
            : string.Empty;

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
