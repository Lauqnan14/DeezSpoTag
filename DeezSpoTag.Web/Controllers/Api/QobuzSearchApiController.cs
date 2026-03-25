using System.Globalization;
using DeezSpoTag.Core.Models.Qobuz;
using DeezSpoTag.Services.Metadata.Qobuz;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/qobuz/search")]
[Authorize]
public sealed class QobuzSearchApiController : ControllerBase
{
    private const string QobuzSource = "qobuz";
    private readonly IQobuzMetadataService _metadataService;
    private readonly ILogger<QobuzSearchApiController> _logger;

    public QobuzSearchApiController(
        IQobuzMetadataService metadataService,
        ILogger<QobuzSearchApiController> logger)
    {
        _metadataService = metadataService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Search(
        [FromQuery] string query,
        [FromQuery] string? type = null,
        [FromQuery] int limit = 25,
        CancellationToken cancellationToken = default)
    {
        return await ExternalSearchControllerHelpers.RunSearchAsync(
            query,
            type,
            limit,
            _logger,
            failureMessage: "Qobuz search failed.",
            async (normalizedType, normalizedLimit, ct) =>
            {
                var tracks = normalizedType is null or "track"
                    ? (await _metadataService.SearchTracks(query, ct)).Take(normalizedLimit).ToList()
                    : new List<QobuzTrack>();
                var albums = normalizedType is null or "album"
                    ? (await _metadataService.SearchAlbums(query, ct)).Take(normalizedLimit).ToList()
                    : new List<QobuzAlbum>();
                var artists = normalizedType is null or "artist"
                    ? (await _metadataService.SearchArtists(query, ct)).Take(normalizedLimit).ToList()
                    : new List<QobuzArtist>();

                return new
                {
                    available = true,
                    tracks = tracks.Select(MapTrack),
                    albums = albums.Select(MapAlbum),
                    artists = artists.Select(MapArtist),
                    playlists = Array.Empty<object>(),
                    totals = new Dictionary<string, int>
                    {
                        ["tracks"] = tracks.Count,
                        ["albums"] = albums.Count,
                        ["artists"] = artists.Count,
                        ["playlists"] = 0
                    }
                };
            },
            cancellationToken);
    }

    private static object MapTrack(QobuzTrack track)
    {
        var id = track.Id.ToString(CultureInfo.InvariantCulture);
        var performer = track.Performer?.Name
            ?? track.Album?.Artists?.FirstOrDefault()?.Name
            ?? string.Empty;
        var image = track.Album?.Image?.Large
            ?? track.Album?.Image?.ExtraLarge
            ?? track.Album?.Image?.Medium
            ?? track.Album?.Image?.Small
            ?? string.Empty;
        return new
        {
            source = QobuzSource,
            type = "track",
            name = ComposeTitle(track.Title, track.Version),
            artist = performer,
            album = ComposeTitle(track.Album?.Title, track.Album?.Version),
            image,
            duration = track.Duration,
            durationMs = Math.Max(0, track.Duration) * 1000L,
            isrc = track.ISRC ?? string.Empty,
            qobuzId = id,
            qobuzType = "track",
            qobuzUrl = $"https://play.qobuz.com/track/{Uri.EscapeDataString(id)}",
            externalUrl = $"https://play.qobuz.com/track/{Uri.EscapeDataString(id)}",
            hasHiRes = track.HiRes || track.MaximumBitDepth > 16 || track.MaximumSamplingRate > 48d,
            bitDepth = track.MaximumBitDepth,
            sampleRate = track.MaximumSamplingRate
        };
    }

    private static object MapAlbum(QobuzAlbum album)
    {
        var id = album.Id ?? string.Empty;
        var url = string.IsNullOrWhiteSpace(album.Url)
            ? $"https://play.qobuz.com/album/{Uri.EscapeDataString(id)}"
            : album.Url;
        var artist = album.Artists.FirstOrDefault()?.Name ?? string.Empty;
        var image = album.Image?.Large
            ?? album.Image?.ExtraLarge
            ?? album.Image?.Medium
            ?? album.Image?.Small
            ?? string.Empty;
        return new
        {
            source = QobuzSource,
            type = "album",
            name = ComposeTitle(album.Title, album.Version),
            artist,
            image,
            release_date = album.ReleaseDateOriginal
                ?? album.ReleaseDateStream
                ?? album.ReleaseDateDownload
                ?? string.Empty,
            trackCount = album.TracksCount,
            qobuzId = id,
            qobuzType = "album",
            qobuzUrl = url,
            externalUrl = url,
            hasHiRes = album.HiRes || album.HiResStreamable || album.MaximumBitDepth > 16 || album.MaximumSamplingRate > 48d,
            bitDepth = album.MaximumBitDepth,
            sampleRate = album.MaximumSamplingRate
        };
    }

    private static object MapArtist(QobuzArtist artist)
    {
        var id = artist.Id.ToString(CultureInfo.InvariantCulture);
        var image = artist.Image?.Large
            ?? artist.Image?.ExtraLarge
            ?? artist.Image?.Medium
            ?? artist.Image?.Small
            ?? string.Empty;
        var url = $"https://play.qobuz.com/artist/{Uri.EscapeDataString(id)}";
        return new
        {
            source = QobuzSource,
            type = "artist",
            name = artist.Name ?? string.Empty,
            image,
            albumsCount = artist.AlbumsCount,
            qobuzId = id,
            qobuzType = "artist",
            qobuzUrl = url,
            externalUrl = url
        };
    }

    private static string ComposeTitle(string? title, string? version) =>
        ExternalSearchControllerHelpers.ComposeTitle(title, version);

}
