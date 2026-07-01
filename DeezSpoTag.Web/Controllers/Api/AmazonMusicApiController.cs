using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Authorize]
[Route("api/amazon")]
public sealed class AmazonMusicApiController : ControllerBase
{
    private readonly AmazonMusicMetadataService _amazonMusicMetadataService;

    public AmazonMusicApiController(AmazonMusicMetadataService amazonMusicMetadataService)
    {
        _amazonMusicMetadataService = amazonMusicMetadataService;
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
