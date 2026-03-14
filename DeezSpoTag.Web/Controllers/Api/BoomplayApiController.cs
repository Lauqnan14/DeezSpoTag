using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Core.Models.Deezer;
using DeezSpoTag.Services.Download.Utils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Features;
using DeezSpoTag.Web.Services;
using System.Net;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
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
    private static readonly string[] DerivativeMarkers =
    {
        "cover",
        "covers",
        "parody",
        "parodies",
        "karaoke",
        "tribute",
        "instrumental",
        "instrumentals",
        "remix",
        "remake",
        "re recorded",
        "as made famous by",
        "originally performed by",
        "in the style of",
        "made popular by",
        "made famous by",
        "backing track",
        "backing tracks",
        "sing along",
        "singalong",
        "midi",
        "8 bit",
        "8bit",
        "music box",
        "lullaby version",
        "piano version",
        "acoustic cover",
        "sped up",
        "slowed down",
        "nightcore"
    };
    private readonly BoomplayMetadataService _boomplayMetadataService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DeezerClient _deezerClient;
    private readonly SongLinkResolver _songLinkResolver;
    private readonly ILogger<BoomplayApiController> _logger;

    private sealed record DeezerResolvedMetadata(
        string DeezerId,
        string Title,
        string Artist,
        string Album,
        string CoverMedium,
        int? DurationSeconds);

    public BoomplayApiController(
        BoomplayMetadataService boomplayMetadataService,
        IHttpClientFactory httpClientFactory,
        DeezerClient deezerClient,
        SongLinkResolver songLinkResolver,
        ILogger<BoomplayApiController> logger)
    {
        _boomplayMetadataService = boomplayMetadataService;
        _httpClientFactory = httpClientFactory;
        _deezerClient = deezerClient;
        _songLinkResolver = songLinkResolver;
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
        var trackUrl = !string.IsNullOrWhiteSpace(track.Url)
            ? track.Url
            : $"https://www.boomplay.com/songs/{track.Id}";
        var genres = track.Genres
            .Where(static genre => !string.IsNullOrWhiteSpace(genre))
            .Select(static genre => genre.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new
        {
            id = track.Id,
            boomplayId = track.Id,
            title = WebUtility.HtmlDecode(track.Title ?? string.Empty).Trim(),
            duration = track.DurationMs > 0 ? track.DurationMs / 1000 : 0,
            isrc = track.Isrc,
            track_position = index + 1,
            artist = new { id = string.Empty, name = WebUtility.HtmlDecode(track.Artist ?? string.Empty).Trim() },
            albumArtist = WebUtility.HtmlDecode(track.AlbumArtist ?? string.Empty).Trim(),
            album = new
            {
                id = string.Empty,
                title = WebUtility.HtmlDecode(track.Album ?? string.Empty).Trim(),
                cover_medium = WebUtility.HtmlDecode(track.CoverUrl ?? string.Empty).Trim()
            },
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

    private static string GetGenreSource(BoomplayTrackMetadata track, IReadOnlyCollection<string> genres)
    {
        if (track.HasStreamGenreMetadata)
        {
            return "stream";
        }

        return genres.Count > 0 ? "html" : "none";
    }

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
                var playlist = await _boomplayMetadataService.GetPlaylistAsync(id, includeTracks: true, cancellationToken);
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

    [HttpGet("playlist/metadata")]
    public async Task<IActionResult> GetPlaylistMetadata(
        [FromQuery] string id,
        [FromQuery] string type,
        CancellationToken cancellationToken)
    {
        var normalizedType = (type ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedType != PlaylistType && normalizedType != TrendingType)
        {
            return BadRequest(new { error = "Unsupported Boomplay type for staged playlist loading." });
        }

        if (normalizedType == PlaylistType && string.IsNullOrWhiteSpace(id))
        {
            return BadRequest(new { error = PlaylistIdRequiredMessage });
        }

        try
        {
            BoomplayPlaylistMetadata? playlist = normalizedType == PlaylistType
                ? await _boomplayMetadataService.GetPlaylistAsync(id, includeTracks: false, cancellationToken)
                : await _boomplayMetadataService.GetTrendingSongsAsync(includeTracks: false, cancellationToken);

            if (playlist == null)
            {
                return NotFound(new { available = false, error = "Boomplay playlist unavailable." });
            }

            return Ok(new
            {
                available = true,
                tracklist = MapPlaylistMetadata(playlist)
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Boomplay staged metadata fetch failed for Type:Id");
            return StatusCode(500, new { available = false, error = "Failed to load Boomplay playlist metadata." });
        }
    }

    [HttpGet("playlist/tracks")]
    public async Task<IActionResult> GetPlaylistTracks(
        [FromQuery] string id,
        [FromQuery] string type,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 25,
        [FromQuery] bool resolveDeezer = false,
        CancellationToken cancellationToken = default)
    {
        if (TryCreatePlaylistRequestError(
            id,
            type,
            "Unsupported Boomplay type for staged playlist loading.",
            out var normalizedType,
            out var requestError))
        {
            return requestError!;
        }

        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 5, 100);

        try
        {
            var playlist = await LoadPlaylistMetadataAsync(id, normalizedType, cancellationToken);
            if (playlist == null)
            {
                return NotFound(new { available = false, error = "Boomplay playlist unavailable." });
            }

            var total = playlist.TrackIds.Count;
            if (offset >= total)
            {
                return Ok(CreateEmptyPlaylistTracksResponse(offset, total));
            }

            var pageIds = playlist.TrackIds
                .Skip(offset)
                .Take(limit)
                .ToList();

            var fetchedTracks = pageIds.Count == 0
                ? Array.Empty<BoomplayTrackMetadata>()
                : (await _boomplayMetadataService.GetSongsAsync(pageIds, cancellationToken)).ToArray();
            var tracksById = fetchedTracks
                .Where(static track => !string.IsNullOrWhiteSpace(track.Id))
                .GroupBy(static track => track.Id, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
            var tracks = pageIds
                .Select(id =>
                {
                    playlist.TrackHints.TryGetValue(id, out var hint);
                    if (tracksById.TryGetValue(id, out var track))
                    {
                        return ApplyPlaylistHint(track, hint);
                    }

                    return BuildFallbackTrack(id, hint);
                })
                .ToArray();
            var missingCount = pageIds.Count(pageId => !tracksById.ContainsKey(pageId));
            if (missingCount > 0)
            {
                _logger.LogWarning("Boomplay staged tracks missing metadata for MissingCount/PageCount tracks for Type:Id at offset Offset");
            }
            var nextOffset = Math.Min(total, offset + pageIds.Count);
            var deezerMap = resolveDeezer
                ? await ResolveDeezerIdsForTracksAsync(tracks, cancellationToken)
                : new Dictionary<string, DeezerResolvedMetadata>(StringComparer.Ordinal);

            return Ok(new
            {
                available = true,
                offset,
                nextOffset,
                total,
                hasMore = nextOffset < total,
                tracks = MapPlaylistTracksPage(tracks, offset, deezerMap)
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Boomplay staged tracks fetch failed for Type:Id offset=Offset limit=Limit");
            return StatusCode(500, new { available = false, error = "Failed to load Boomplay playlist tracks." });
        }
    }

    [HttpGet("playlist/tracks/stream")]
    public async Task<IActionResult> StreamPlaylistTracks(
        [FromQuery] string id,
        [FromQuery] string type,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 0,
        [FromQuery] bool resolveDeezer = false,
        CancellationToken cancellationToken = default)
    {
        if (TryCreatePlaylistRequestError(
            id,
            type,
            "Unsupported Boomplay type for streaming playlist loading.",
            out var normalizedType,
            out var requestError))
        {
            return requestError!;
        }

        offset = Math.Max(0, offset);

        try
        {
            var playlist = await LoadPlaylistMetadataAsync(id, normalizedType, cancellationToken);
            if (playlist == null)
            {
                return NotFound(new { available = false, error = "Boomplay playlist unavailable." });
            }

            var total = playlist.TrackIds.Count;
            if (offset >= total)
            {
                return Ok(CreateEmptyPlaylistTracksResponse(offset, total));
            }

            var effectiveLimit = limit <= 0
                ? total - offset
                : Math.Clamp(limit, 1, 1000);

            var pageIds = playlist.TrackIds
                .Skip(offset)
                .Take(effectiveLimit)
                .ToList();

            ConfigureSseResponse();

            await WriteSseEventAsync("ready", new
            {
                available = true,
                offset,
                total,
                count = pageIds.Count,
                hasMore = offset + pageIds.Count < total
            }, cancellationToken);

            var emitted = 0;
            for (var relativeIndex = 0; relativeIndex < pageIds.Count; relativeIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var songId = pageIds[relativeIndex];
                var absoluteIndex = offset + relativeIndex;
                var track = await LoadStreamTrackAsync(songId, cancellationToken);

                playlist.TrackHints.TryGetValue(songId, out var hint);
                track = ApplyPlaylistHint(track, hint);

                var deezerMetadata = await ResolveStreamTrackDeezerMetadataAsync(resolveDeezer, track, songId, cancellationToken);

                emitted++;
                await WriteSseEventAsync("track", new
                {
                    track = MapPlaylistTrack(track, absoluteIndex, deezerMetadata),
                    emitted,
                    loaded = absoluteIndex + 1,
                    nextOffset = absoluteIndex + 1,
                    total,
                    hasMore = absoluteIndex + 1 < total
                }, cancellationToken);
            }

            var nextOffset = offset + pageIds.Count;
            await WriteSseEventAsync("complete", new
            {
                offset,
                nextOffset,
                total,
                emitted,
                hasMore = nextOffset < total
            }, cancellationToken);
            return new EmptyResult();
        }
        catch (OperationCanceledException)
        {
            return new EmptyResult();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Boomplay stream playlist fetch failed for Type:Id offset=Offset limit=Limit");
            if (!Response.HasStarted)
            {
                return StatusCode(500, new { available = false, error = "Failed to stream Boomplay playlist tracks." });
            }

            try
            {
                await WriteSseEventAsync("error", new { error = "Failed to stream Boomplay playlist tracks." }, CancellationToken.None);
            }
            catch (Exception streamEx) when (streamEx is not OperationCanceledException) {
                // Best-effort stream error signaling.
            }

            return new EmptyResult();
        }
    }

    private void ConfigureSseResponse()
    {
        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Append("X-Accel-Buffering", "no");
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
    }

    private async Task<BoomplayTrackMetadata> LoadStreamTrackAsync(string songId, CancellationToken cancellationToken)
    {
        try
        {
            return await _boomplayMetadataService.GetSongAsync(songId, cancellationToken)
                ?? BuildFallbackTrack(songId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Boomplay stream track fetch failed for {SongId}", songId);
            return BuildFallbackTrack(songId);
        }
    }

    private async Task<DeezerResolvedMetadata?> ResolveStreamTrackDeezerMetadataAsync(
        bool resolveDeezer,
        BoomplayTrackMetadata track,
        string songId,
        CancellationToken cancellationToken)
    {
        if (!resolveDeezer)
        {
            return null;
        }

        try
        {
            return await ResolveDeezerMetadataForTrackAsync(track, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Boomplay stream Deezer resolve failed for {SongId}", songId);
            return null;
        }
    }

    private static BoomplayTrackMetadata BuildFallbackTrack(string id, BoomplayTrackHint? hint = null)
    {
        var fallback = new BoomplayTrackMetadata
        {
            Id = id,
            Url = $"https://www.boomplay.com/songs/{id}"
        };
        return ApplyPlaylistHint(fallback, hint);
    }

    private bool TryCreatePlaylistRequestError(
        string id,
        string type,
        string unsupportedTypeMessage,
        out string normalizedType,
        out IActionResult? error)
    {
        normalizedType = (type ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedType != PlaylistType && normalizedType != TrendingType)
        {
            error = BadRequest(new { available = false, error = unsupportedTypeMessage });
            return true;
        }

        if (normalizedType == PlaylistType && string.IsNullOrWhiteSpace(id))
        {
            error = BadRequest(new { available = false, error = PlaylistIdRequiredMessage });
            return true;
        }

        error = null;
        return false;
    }

    private Task<BoomplayPlaylistMetadata?> LoadPlaylistMetadataAsync(
        string id,
        string normalizedType,
        CancellationToken cancellationToken)
    {
        return normalizedType == PlaylistType
            ? _boomplayMetadataService.GetPlaylistAsync(id, includeTracks: false, cancellationToken)
            : _boomplayMetadataService.GetTrendingSongsAsync(includeTracks: false, cancellationToken);
    }

    private static object CreateEmptyPlaylistTracksResponse(int offset, int total)
    {
        return new
        {
            available = true,
            offset,
            nextOffset = offset,
            total,
            hasMore = false,
            tracks = Array.Empty<object>()
        };
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
                var trackUrl = !string.IsNullOrWhiteSpace(track.Url)
                    ? track.Url
                    : $"https://www.boomplay.com/songs/{track.Id}";
                return new
                {
                    id = track.Id,
                    boomplayId = track.Id,
                    title = WebUtility.HtmlDecode(track.Title ?? string.Empty).Trim(),
                    duration = track.DurationMs > 0 ? track.DurationMs / 1000 : 0,
                    isrc = track.Isrc,
                    track_position = index + 1,
                    artist = new { id = string.Empty, name = WebUtility.HtmlDecode(track.Artist ?? string.Empty).Trim() },
                    album = new
                    {
                        id = string.Empty,
                        title = WebUtility.HtmlDecode(track.Album ?? string.Empty).Trim(),
                        cover_medium = WebUtility.HtmlDecode(track.CoverUrl ?? string.Empty).Trim()
                    },
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
            nb_tracks = tracks.Count > 0 ? tracks.Count : playlist.TrackIds.Count,
            creator = new { id = string.Empty, name = "Boomplay" },
            tracks
        };
    }

    private static object MapPlaylistMetadata(BoomplayPlaylistMetadata playlist)
    {
        return new
        {
            id = playlist.Id,
            title = playlist.Title,
            description = playlist.Description,
            picture_big = playlist.ImageUrl,
            picture_xl = playlist.ImageUrl,
            cover_big = playlist.ImageUrl,
            cover_xl = playlist.ImageUrl,
            nb_tracks = playlist.TrackIds.Count,
            creator = new { id = string.Empty, name = "Boomplay" },
            tracks = Array.Empty<object>()
        };
    }

    private async Task<Dictionary<string, DeezerResolvedMetadata>> ResolveDeezerIdsForTracksAsync(
        IReadOnlyList<BoomplayTrackMetadata> tracks,
        CancellationToken cancellationToken)
    {
        if (tracks.Count == 0)
        {
            return new Dictionary<string, DeezerResolvedMetadata>(StringComparer.Ordinal);
        }

        var resolved = new System.Collections.Concurrent.ConcurrentDictionary<string, DeezerResolvedMetadata>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(
            tracks,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = 2,
                CancellationToken = cancellationToken
            },
            async (track, token) =>
            {
                var metadata = await ResolveDeezerMetadataForTrackAsync(track, token);
                if (metadata != null)
                {
                    resolved[track.Id] = metadata;
                }
            });

        return resolved.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);
    }

    private async Task<DeezerResolvedMetadata?> ResolveDeezerMetadataForTrackAsync(BoomplayTrackMetadata track, CancellationToken cancellationToken)
    {
        if (track == null || string.IsNullOrWhiteSpace(track.Id))
        {
            return null;
        }

        var summary = BuildTrackSummary(track);

        try
        {
            var deezerId = await ResolveDeezerIdFromCoreMetadataAsync(track, summary, cancellationToken);
            if (string.IsNullOrWhiteSpace(deezerId))
            {
                deezerId = await ResolveDeezerIdFromSongLinkAsync(summary, cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(deezerId))
            {
                return null;
            }

            return await HydrateDeezerMetadataAsync(deezerId, track, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed resolving Deezer ID for Boomplay track {TrackId}", track.Id);
        }

        return null;
    }

    private static SpotifyTrackSummary BuildTrackSummary(BoomplayTrackMetadata track)
    {
        return new SpotifyTrackSummary(
            Id: string.Empty,
            Name: track.Title ?? string.Empty,
            Artists: track.Artist,
            Album: track.Album,
            DurationMs: track.DurationMs > 0 ? track.DurationMs : null,
            SourceUrl: !string.IsNullOrWhiteSpace(track.Url) ? track.Url : $"https://www.boomplay.com/songs/{track.Id}",
            ImageUrl: track.CoverUrl,
            Isrc: string.IsNullOrWhiteSpace(track.Isrc) ? null : track.Isrc);
    }

    private static bool HasCoreMetadata(BoomplayTrackMetadata track)
    {
        return !string.IsNullOrWhiteSpace(track.Title)
               || !string.IsNullOrWhiteSpace(track.Artist)
               || !string.IsNullOrWhiteSpace(track.Album)
               || !string.IsNullOrWhiteSpace(track.Isrc);
    }

    private async Task<string?> ResolveDeezerIdFromCoreMetadataAsync(
        BoomplayTrackMetadata track,
        SpotifyTrackSummary summary,
        CancellationToken cancellationToken)
    {
        if (!HasCoreMetadata(track))
        {
            return null;
        }

        var strictMode = !track.HasStreamTagMetadata;
        var sourceIsrc = track.Isrc;
        var resolved = await ResolveDeezerIdViaSpotifyResolverAsync(
            summary,
            strictMode,
            bypassNegativeCanonicalCache: false,
            sourceIsrc,
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            return resolved;
        }

        if (strictMode)
        {
            resolved = await ResolveDeezerIdViaSpotifyResolverAsync(
                summary,
                strictMode: false,
                bypassNegativeCanonicalCache: true,
                sourceIsrc,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }
        }

        resolved = await ResolveDeezerIdViaDirectMetadataAsync(summary, sourceIsrc, cancellationToken);
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            return resolved;
        }

        return await ResolveFromDeezerSearchFallbackAsync(
            summary.Name,
            summary.Artists,
            summary.Album,
            sourceIsrc,
            summary.DurationMs,
            cancellationToken);
    }

    private async Task<string?> ResolveDeezerIdViaSpotifyResolverAsync(
        SpotifyTrackSummary summary,
        bool strictMode,
        bool bypassNegativeCanonicalCache,
        string? sourceIsrc,
        CancellationToken cancellationToken)
    {
        var resolved = await SpotifyTracklistResolver.ResolveDeezerTrackAsync(
            _deezerClient,
            _songLinkResolver,
            summary,
            new SpotifyTrackResolveOptions(
                AllowFallbackSearch: true,
                PreferIsrcOnly: false,
                UseSongLink: false,
                StrictMode: strictMode,
                BypassNegativeCanonicalCache: bypassNegativeCanonicalCache,
                Logger: _logger,
                CancellationToken: cancellationToken));

        return await ValidateCandidateDeezerIdAsync(
            resolved.DeezerId,
            summary.Name,
            summary.Artists,
            summary.Album,
            sourceIsrc,
            summary.DurationMs,
            cancellationToken);
    }

    private async Task<string?> ResolveDeezerIdViaDirectMetadataAsync(
        SpotifyTrackSummary summary,
        string? sourceIsrc,
        CancellationToken cancellationToken)
    {
        var artist = summary.Artists ?? string.Empty;
        var title = summary.Name ?? string.Empty;
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var album = summary.Album ?? string.Empty;
        var durationMs = summary.DurationMs;
        var directId = await _deezerClient.GetTrackIdFromMetadataAsync(artist, title, album, durationMs);
        var validatedId = await ValidateCandidateDeezerIdAsync(
            directId,
            title,
            artist,
            album,
            sourceIsrc,
            durationMs,
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(validatedId))
        {
            return validatedId;
        }

        var fastId = await _deezerClient.GetTrackIdFromMetadataFastAsync(artist, title, durationMs);
        validatedId = await ValidateCandidateDeezerIdAsync(
            fastId,
            title,
            artist,
            album,
            sourceIsrc,
            durationMs,
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(validatedId))
        {
            return validatedId;
        }

        var primaryArtist = StripFeaturingFromArtist(artist);
        if (string.Equals(primaryArtist, artist, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var strippedId = await _deezerClient.GetTrackIdFromMetadataFastAsync(primaryArtist, title, durationMs);
        return await ValidateCandidateDeezerIdAsync(
            strippedId,
            title,
            primaryArtist,
            album,
            sourceIsrc,
            durationMs,
            cancellationToken);
    }

    private async Task<string?> ResolveDeezerIdFromSongLinkAsync(
        SpotifyTrackSummary summary,
        CancellationToken cancellationToken)
    {
        var songLink = await _songLinkResolver.ResolveByUrlAsync(summary.SourceUrl, cancellationToken);
        if (!HasValidationInputs(summary))
        {
            return null;
        }

        return await ValidateCandidateDeezerIdAsync(
            songLink?.DeezerId,
            summary.Name,
            summary.Artists,
            summary.Album,
            summary.Isrc,
            summary.DurationMs,
            cancellationToken);
    }

    private static bool HasValidationInputs(SpotifyTrackSummary summary)
    {
        return !string.IsNullOrWhiteSpace(summary.Name)
            || !string.IsNullOrWhiteSpace(summary.Artists)
            || !string.IsNullOrWhiteSpace(summary.Album)
            || !string.IsNullOrWhiteSpace(summary.Isrc);
    }

    private async Task<string?> ValidateCandidateDeezerIdAsync(
        string? deezerId,
        string? sourceTitle,
        string? sourceArtist,
        string? sourceAlbum,
        string? sourceIsrc,
        int? sourceDurationMs,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deezerId) || deezerId == "0")
        {
            return null;
        }

        var plausible = await IsPlausibleDeezerCandidateAsync(
            deezerId,
            sourceTitle,
            sourceArtist,
            sourceAlbum,
            sourceIsrc,
            sourceDurationMs,
            cancellationToken);

        return plausible ? deezerId : null;
    }

    private async Task<DeezerResolvedMetadata> HydrateDeezerMetadataAsync(
        string deezerId,
        BoomplayTrackMetadata fallback,
        CancellationToken cancellationToken)
    {
        var title = WebUtility.HtmlDecode(fallback.Title ?? string.Empty).Trim();
        var artist = WebUtility.HtmlDecode(fallback.Artist ?? string.Empty).Trim();
        var album = WebUtility.HtmlDecode(fallback.Album ?? string.Empty).Trim();
        var coverMedium = WebUtility.HtmlDecode(fallback.CoverUrl ?? string.Empty).Trim();
        int? durationSeconds = fallback.DurationMs > 0 ? (int)Math.Round(fallback.DurationMs / 1000d) : null;

        try
        {
            var trackData = await _deezerClient.GetTrackAsync(deezerId, cancellationToken);
            if (trackData != null)
            {
                title = FirstNonEmpty(GetString(trackData, "SNG_TITLE"), title);
                artist = FirstNonEmpty(GetString(trackData, "ART_NAME"), artist);
                album = FirstNonEmpty(GetString(trackData, "ALB_TITLE"), album);

                var pictureHash = GetString(trackData, "ALB_PICTURE");
                if (!string.IsNullOrWhiteSpace(pictureHash))
                {
                    coverMedium = $"https://cdns-images.dzcdn.net/images/cover/{pictureHash}/250x250-000000-80-0-0.jpg";
                }
                var deezerDuration = GetInt(trackData, "DURATION");
                if (deezerDuration > 0)
                {
                    durationSeconds = deezerDuration;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed hydrating Deezer metadata for track {DeezerId}", deezerId);
        }

        return new DeezerResolvedMetadata(
            DeezerId: deezerId,
            Title: title,
            Artist: artist,
            Album: album,
            CoverMedium: coverMedium,
            DurationSeconds: durationSeconds);
    }

    private Task<bool> IsPlausibleDeezerCandidateAsync(
        string deezerId,
        string? sourceTitle,
        string? sourceArtist,
        string? sourceAlbum,
        string? sourceIsrc,
        int? sourceDurationMs,
        CancellationToken cancellationToken)
    {
        var source = new DeezerCandidateSource(
            sourceTitle,
            sourceArtist,
            sourceAlbum,
            sourceIsrc,
            sourceDurationMs);
        return DeezerCandidateMatchHelper.IsPlausibleCandidateAsync(
            deezerId,
            source,
            new DeezerCandidateValidationHandlers(
                TryGetValidationCandidateAsync,
                SourceAllowsDerivative,
                IsDerivativeCandidate,
                IsDerivativeArtistName),
            _logger,
            new DeezerCandidateValidationOptions(
                MinimumArtistScore: 0.36d,
                RejectDerivativeArtistName: true,
                ApplyVeryLowAlbumGuard: true,
                FailureLogMessage: "Failed to validate Boomplay candidate {DeezerId}"),
            cancellationToken);
    }

    private Task<string?> ResolveFromDeezerSearchFallbackAsync(
        string? sourceTitle,
        string? sourceArtist,
        string? sourceAlbum,
        string? sourceIsrc,
        int? sourceDurationMs,
        CancellationToken cancellationToken)
    {
        var source = new DeezerCandidateSource(
            sourceTitle,
            sourceArtist,
            sourceAlbum,
            sourceIsrc,
            sourceDurationMs);
        return DeezerCandidateMatchHelper.ResolveFromSearchFallbackAsync(
            _deezerClient,
            source,
            new DeezerFallbackSearchHandlers(
                (candidateId, token) => IsPlausibleDeezerCandidateAsync(
                    candidateId,
                    source.SourceTitle,
                    source.SourceArtist,
                    source.SourceAlbum,
                    source.SourceIsrc,
                    source.SourceDurationMs,
                    token),
                (candidateId, album, token) => GetAlbumMatchScoreAsync(candidateId, album, token),
                TryGetValidationCandidateAsync,
                SourceAllowsDerivative,
                IsDerivativeArtistName),
            _logger,
            new DeezerFallbackSearchOptions(
                ExcludeDerivativeArtistCandidates: true,
                PreferBestAlbumMatch: true,
                SearchFailureLogMessage: "Boomplay fallback Deezer search failed for query {Query}"),
            cancellationToken);
    }

    private Task<double> GetAlbumMatchScoreAsync(
        string deezerId,
        string? sourceAlbum,
        CancellationToken cancellationToken)
    {
        return DeezerCandidateMatchHelper.GetAlbumMatchScoreAsync(
            deezerId,
            sourceAlbum,
            TryGetValidationCandidateAsync,
            cancellationToken);
    }

    private Task<(bool fetched, ApiTrack? track)> TryGetValidationCandidateAsync(
        string deezerId,
        CancellationToken cancellationToken)
    {
        return DeezerCandidateMatchHelper.TryGetValidationCandidateAsync(
            _deezerClient,
            _logger,
            deezerId,
            "Failed to load Deezer candidate {DeezerId} for Boomplay validation",
            cancellationToken);
    }

    private static string NormalizeGuardToken(string? value)
        => ResolveDeezerApiController.NormalizeGuardToken(value);

    private static bool SourceAllowsDerivative(string? title, string? artist, string? album)
    {
        var combined = NormalizeGuardToken($"{title} {artist} {album}");
        if (string.IsNullOrWhiteSpace(combined))
        {
            return false;
        }

        return DerivativeMarkers.Any(marker => ContainsWholeMarker(combined, marker));
    }

    private static bool IsDerivativeCandidate(ApiTrack candidate)
    {
        if (candidate == null)
        {
            return false;
        }

        var combined = NormalizeGuardToken($"{candidate.Title} {candidate.TitleVersion} {candidate.Album?.Title} {candidate.Artist?.Name}");
        if (string.IsNullOrWhiteSpace(combined))
        {
            return false;
        }

        return DerivativeMarkers.Any(marker => ContainsWholeMarker(combined, marker));
    }

    private static readonly string[] DerivativeArtistKeywords =
    {
        "karaoke", "tribute", "cover", "covers", "instrumental",
        "backing track", "sing along", "singalong", "midi",
        "music box", "lullaby", "8 bit", "8bit", "nightcore",
        "sped up", "originally performed"
    };

    private static bool IsDerivativeArtistName(string? artistName)
    {
        if (string.IsNullOrWhiteSpace(artistName))
        {
            return false;
        }

        var normalized = NormalizeGuardToken(artistName);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return DerivativeArtistKeywords.Any(keyword =>
            $" {normalized} ".Contains($" {keyword} ", StringComparison.Ordinal));
    }

    private static bool ContainsWholeMarker(string text, string marker)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(marker))
        {
            return false;
        }

        var normalizedMarker = NormalizeGuardToken(marker);
        if (string.IsNullOrWhiteSpace(normalizedMarker))
        {
            return false;
        }

        return $" {text} ".Contains($" {normalizedMarker} ", StringComparison.Ordinal);
    }

    private static List<object> MapPlaylistTracksPage(
        IReadOnlyList<BoomplayTrackMetadata> tracks,
        int offset,
        IReadOnlyDictionary<string, DeezerResolvedMetadata>? deezerMap = null)
    {
        return tracks.Select((track, index) =>
        {
            DeezerResolvedMetadata? deezerMetadata = null;
            if (deezerMap != null)
            {
                deezerMap.TryGetValue(track.Id, out deezerMetadata);
            }

            return MapPlaylistTrack(track, offset + index, deezerMetadata);
        }).ToList();
    }

    private static object MapPlaylistTrack(
        BoomplayTrackMetadata track,
        int absoluteIndex,
        DeezerResolvedMetadata? deezerMetadata = null)
    {
        var trackUrl = !string.IsNullOrWhiteSpace(track.Url)
            ? track.Url
            : $"https://www.boomplay.com/songs/{track.Id}";
        var deezerId = deezerMetadata?.DeezerId ?? string.Empty;
        var title = deezerMetadata?.Title ?? WebUtility.HtmlDecode(track.Title ?? string.Empty).Trim();
        var artist = deezerMetadata?.Artist ?? WebUtility.HtmlDecode(track.Artist ?? string.Empty).Trim();
        var album = deezerMetadata?.Album ?? WebUtility.HtmlDecode(track.Album ?? string.Empty).Trim();
        var coverMedium = deezerMetadata?.CoverMedium
            ?? WebUtility.HtmlDecode(track.CoverUrl ?? string.Empty).Trim();
        var durationSeconds = deezerMetadata?.DurationSeconds
            ?? (track.DurationMs > 0 ? (int)Math.Round(track.DurationMs / 1000d) : 0);

        return new
        {
            index = absoluteIndex,
            id = track.Id,
            boomplayId = track.Id,
            deezerId = deezerId ?? string.Empty,
            title,
            duration = durationSeconds,
            isrc = track.Isrc,
            track_position = absoluteIndex + 1,
            artist = new { id = string.Empty, name = artist },
            album = new
            {
                id = string.Empty,
                title = album,
                cover_medium = coverMedium
            },
            link = trackUrl,
            sourceUrl = trackUrl
        };
    }

    private async Task WriteSseEventAsync(string eventName, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload);
        await Response.WriteAsync($"event: {eventName}\n", cancellationToken);
        await Response.WriteAsync($"data: {json}\n\n", cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }

    private static BoomplayTrackMetadata ApplyPlaylistHint(BoomplayTrackMetadata source, BoomplayTrackHint? hint)
    {
        var track = CloneTrack(source);
        if (hint == null)
        {
            return track;
        }

        if (IsBlankOrPlaceholder(track.Title) && !string.IsNullOrWhiteSpace(hint.Title))
        {
            track.Title = hint.Title.Trim();
        }
        if (IsBlankOrPlaceholder(track.Artist) && !string.IsNullOrWhiteSpace(hint.Artist))
        {
            track.Artist = hint.Artist.Trim();
        }
        if (IsBlankOrPlaceholder(track.Album) && !string.IsNullOrWhiteSpace(hint.Album))
        {
            track.Album = hint.Album.Trim();
        }
        if (string.IsNullOrWhiteSpace(track.CoverUrl) && !string.IsNullOrWhiteSpace(hint.CoverUrl))
        {
            track.CoverUrl = hint.CoverUrl.Trim();
        }

        return track;
    }

    private static BoomplayTrackMetadata CloneTrack(BoomplayTrackMetadata source)
    {
        return new BoomplayTrackMetadata
        {
            Id = source.Id,
            Url = source.Url,
            Title = source.Title,
            Artist = source.Artist,
            Album = source.Album,
            CoverUrl = source.CoverUrl,
            Isrc = source.Isrc,
            DurationMs = source.DurationMs,
            TrackNumber = source.TrackNumber,
            ReleaseDate = source.ReleaseDate,
            Genres = source.Genres?.ToList() ?? new List<string>(),
            HasStreamTagMetadata = source.HasStreamTagMetadata,
            HasStreamGenreMetadata = source.HasStreamGenreMetadata
        };
    }

    private static bool IsBlankOrPlaceholder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var normalized = WebUtility.HtmlDecode(value).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return true;
        }

        return normalized.Equals("unknown", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("boomplay", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("boomplay music", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetString(Dictionary<string, object> values, string key)
    {
        if (!values.TryGetValue(key, out var raw) || raw == null)
        {
            return string.Empty;
        }

        return raw.ToString()?.Trim() ?? string.Empty;
    }

    private static int GetInt(Dictionary<string, object> values, string key)
    {
        if (!values.TryGetValue(key, out var raw) || raw == null)
        {
            return 0;
        }

        if (raw is int intValue)
        {
            return intValue;
        }

        if (raw is long longValue)
        {
            return (int)longValue;
        }

        if (int.TryParse(raw.ToString(), out var parsed))
        {
            return parsed;
        }

        return 0;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static string StripFeaturingFromArtist(string? artist)
        => ResolveDeezerApiController.StripFeaturingFromArtist(artist);

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
