using DeezSpoTag.Web.Services;
using DeezSpoTag.Services.Download.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Authorize]
[Route("api/amazon")]
public sealed class AmazonMusicApiController : ControllerBase
{
    private readonly AmazonMusicMetadataService _amazonMusicMetadataService;
    private readonly DownloadIntentService _downloadIntentService;

    public AmazonMusicApiController(
        AmazonMusicMetadataService amazonMusicMetadataService,
        DownloadIntentService downloadIntentService)
    {
        _amazonMusicMetadataService = amazonMusicMetadataService;
        _downloadIntentService = downloadIntentService;
    }

    [HttpPost("deezer-matches")]
    public async Task<IActionResult> ResolveDeezerMatches(
        [FromBody] AmazonDeezerMatchBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Tracks.Count == 0)
        {
            return Ok(new { matches = Array.Empty<object>() });
        }

        using var gate = new SemaphoreSlim(2, 2);
        var tasks = request.Tracks
            .Take(50)
            .Select(async (track, index) =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    var deezerId = await _downloadIntentService.ResolveAmazonDeezerIdAsync(
                        new DownloadIntent
                        {
                            SourceService = "amazon",
                            SourceUrl = track.SourceUrl ?? string.Empty,
                            AmazonId = track.AmazonId ?? string.Empty,
                            Title = track.Title ?? string.Empty,
                            Artist = track.Artist ?? string.Empty,
                            Album = track.Album ?? string.Empty,
                            DurationMs = track.DurationMs
                        },
                        cancellationToken);
                    return new
                    {
                        index,
                        amazonId = track.AmazonId ?? string.Empty,
                        deezerId = deezerId ?? string.Empty
                    };
                }
                finally
                {
                    gate.Release();
                }
            });

        return Ok(new { matches = await Task.WhenAll(tasks) });
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search(
        [FromQuery] string term,
        [FromQuery] string type = "track",
        [FromQuery] int limit = 25,
        CancellationToken cancellationToken = default)
    {
        var payload = await _amazonMusicMetadataService.SearchAsync(term, type, Math.Clamp(limit, 1, 50), cancellationToken);
        return Ok(new
        {
            tracks = payload.Tracks.Select(ToSearchResult),
            albums = payload.Albums.Select(ToSearchResult),
            artists = payload.Artists.Select(ToSearchResult),
            playlists = payload.Playlists.Select(ToSearchResult)
        });
    }

    [HttpGet("tracklist")]
    public async Task<IActionResult> Tracklist(
        [FromQuery] string id,
        [FromQuery] string type = "playlist",
        [FromQuery] string? url = null,
        CancellationToken cancellationToken = default)
    {
        var payload = await _amazonMusicMetadataService.GetTracklistAsync(id, type, url, cancellationToken);
        if (payload is null)
        {
            return NotFound(new { available = false, error = "Amazon Music tracklist unavailable." });
        }

        return Ok(new
        {
            available = true,
            tracklist = new
            {
                id = payload.Collection.Id,
                title = payload.Collection.Title,
                name = payload.Collection.Title,
                artist = new
                {
                    id = string.Empty,
                    name = payload.Collection.Artist
                },
                creator = new
                {
                    name = payload.Collection.Artist
                },
                source = "amazon",
                link = payload.Collection.Url,
                sourceUrl = payload.Collection.Url,
                cover_big = payload.Collection.CoverUrl,
                cover_xl = payload.Collection.CoverUrl,
                picture_big = payload.Collection.CoverUrl,
                picture_xl = payload.Collection.CoverUrl,
                type = payload.Collection.Type,
                nb_tracks = payload.Tracks.Count,
                tracks = payload.Tracks.Select(track => new
                {
                    id = track.Id,
                    amazonId = track.AmazonId,
                    title = track.Title,
                    artist = new
                    {
                        id = string.Empty,
                        name = track.Artist
                    },
                    album = new
                    {
                        id = payload.Collection.Id,
                        title = track.Album,
                        cover = track.Cover,
                        cover_medium = track.Cover,
                        cover_big = track.Cover,
                        cover_xl = track.Cover
                    },
                    source = "amazon",
                    link = track.SourceUrl,
                    sourceUrl = track.SourceUrl,
                    durationMs = track.DurationMs,
                    duration = track.DurationMs > 0 ? track.DurationMs / 1000 : 0,
                    durationSeconds = track.DurationMs > 0 ? track.DurationMs / 1000 : 0,
                    position = track.Position,
                    track_position = track.Position,
                    isrc = track.Isrc
                })
            }
        });
    }

    private static object ToSearchResult(AmazonCatalogItem item) => new
    {
        id = item.Id,
        amazonId = item.Id,
        title = item.Title,
        name = item.Title,
        artist = item.Artist,
        album = item.Album,
        url = item.Url,
        sourceUrl = item.Url,
        cover = item.CoverUrl,
        image = item.CoverUrl,
        durationMs = item.DurationMs,
        isrc = item.Isrc,
        type = item.Type,
        source = "amazon"
    };
}

public sealed class AmazonDeezerMatchBatchRequest
{
    public List<AmazonDeezerMatchTrack> Tracks { get; set; } = [];
}

public sealed class AmazonDeezerMatchTrack
{
    public string? AmazonId { get; set; }
    public string? SourceUrl { get; set; }
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public int DurationMs { get; set; }
}
