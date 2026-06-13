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
    private static readonly TimeSpan DeezerTrackResolveTimeBudget = TimeSpan.FromSeconds(10);
    private static readonly Uri BoomplayReferrerUri = new("https://www.boomplay.com");
    private readonly BoomplayMetadataService _boomplayMetadataService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly BoomplayDeezerMatchService _boomplayDeezerMatchService;
    private readonly ILogger<BoomplayApiController> _logger;

    public BoomplayApiController(
        BoomplayMetadataService boomplayMetadataService,
        IHttpClientFactory httpClientFactory,
        BoomplayDeezerMatchService boomplayDeezerMatchService,
        ILogger<BoomplayApiController> logger)
    {
        _boomplayMetadataService = boomplayMetadataService;
        _httpClientFactory = httpClientFactory;
        _boomplayDeezerMatchService = boomplayDeezerMatchService;
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

            var page = await MaterializePlaylistTrackPageAsync(playlist, pageIds, cancellationToken);

            if (page.MissingCount > 0)
            {
                _logger.LogWarning("Boomplay staged tracks missing metadata for MissingCount/PageCount tracks for Type:Id at offset Offset");
            }
            var nextOffset = Math.Min(total, offset + pageIds.Count);
            var deezerMap = resolveDeezer
                ? await ResolveDeezerIdsForTracksAsync(page.Tracks, cancellationToken)
                : new Dictionary<string, BoomplayDeezerMatchResult>(StringComparer.Ordinal);

            return Ok(new
            {
                available = true,
                offset,
                nextOffset,
                total,
                hasMore = nextOffset < total,
                tracks = MapPlaylistTracksPage(page.Tracks, offset, deezerMap)
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

    private sealed record BoomplayTrackPage(BoomplayTrackMetadata[] Tracks, int MissingCount);

    private async Task<BoomplayTrackPage> MaterializePlaylistTrackPageAsync(
        BoomplayPlaylistMetadata playlist,
        List<string> pageIds,
        CancellationToken cancellationToken)
    {
        if (CanUsePlaylistTrackHints(playlist, pageIds))
        {
            return new BoomplayTrackPage(BuildHintTracks(playlist, pageIds), 0);
        }

        var fetchedTracks = pageIds.Count == 0
            ? Array.Empty<BoomplayTrackMetadata>()
            : (await _boomplayMetadataService.GetSongsAsync(pageIds, cancellationToken)).ToArray();
        var tracksById = fetchedTracks
            .Where(static track => !string.IsNullOrWhiteSpace(track.Id))
            .GroupBy(static track => track.Id, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var tracks = pageIds
            .Select(id => BuildPlaylistTrackFromFetchOrHint(playlist, tracksById, id))
            .ToArray();
        var missingCount = pageIds.Count(pageId => !tracksById.ContainsKey(pageId));
        return new BoomplayTrackPage(tracks, missingCount);
    }

    private static bool CanUsePlaylistTrackHints(BoomplayPlaylistMetadata playlist, List<string> pageIds)
        => pageIds.Count > 0
           && pageIds.All(pageId =>
               playlist.TrackHints.TryGetValue(pageId, out var hint)
               && hint != null
               && !IsBlankOrPlaceholder(hint.Title)
               && !IsBlankOrPlaceholder(hint.Artist));

    private static BoomplayTrackMetadata[] BuildHintTracks(BoomplayPlaylistMetadata playlist, IEnumerable<string> pageIds)
        => pageIds
            .Select(pageId =>
            {
                playlist.TrackHints.TryGetValue(pageId, out var hint);
                return BuildFallbackTrack(pageId, hint);
            })
            .ToArray();

    private static BoomplayTrackMetadata BuildPlaylistTrackFromFetchOrHint(
        BoomplayPlaylistMetadata playlist,
        Dictionary<string, BoomplayTrackMetadata> tracksById,
        string id)
    {
        playlist.TrackHints.TryGetValue(id, out var hint);
        return tracksById.TryGetValue(id, out var track)
            ? ApplyPlaylistHint(track, hint)
            : BuildFallbackTrack(id, hint);
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

    private async Task<Dictionary<string, BoomplayDeezerMatchResult>> ResolveDeezerIdsForTracksAsync(
        BoomplayTrackMetadata[] tracks,
        CancellationToken cancellationToken)
    {
        if (tracks.Length == 0)
        {
            return new Dictionary<string, BoomplayDeezerMatchResult>(StringComparer.Ordinal);
        }

        var resolved = new System.Collections.Concurrent.ConcurrentDictionary<string, BoomplayDeezerMatchResult>(StringComparer.Ordinal);
        var unresolved = new List<BoomplayTrackMetadata>(tracks.Length);
        foreach (var track in tracks)
        {
            unresolved.Add(track);
        }

        if (unresolved.Count == 0)
        {
            return resolved.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);
        }

        var maxParallel = Math.Clamp(Environment.ProcessorCount / 2, 3, 8);
        await Parallel.ForEachAsync(
            unresolved,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = maxParallel,
                CancellationToken = cancellationToken
            },
            async (track, token) =>
            {
                BoomplayDeezerMatchResult? metadata = null;
                try
                {
                    using var perTrackBudgetCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    perTrackBudgetCts.CancelAfter(DeezerTrackResolveTimeBudget);
                    metadata = await _boomplayDeezerMatchService.ResolveTrackAsync(track, perTrackBudgetCts.Token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    // Per-track timeout budget elapsed; treat as unresolved and continue.
                }

                if (metadata != null)
                {
                    resolved[track.Id] = metadata;
                }
            });

        return resolved.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);
    }

    private static List<object> MapPlaylistTracksPage(
        IReadOnlyList<BoomplayTrackMetadata> tracks,
        int offset,
        Dictionary<string, BoomplayDeezerMatchResult>? deezerMap = null)
    {
        return tracks.Select((track, index) =>
        {
            BoomplayDeezerMatchResult? deezerMetadata = null;
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
        BoomplayDeezerMatchResult? deezerMetadata = null)
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
            durationMs = durationSeconds > 0 ? durationSeconds * 1000 : 0,
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
