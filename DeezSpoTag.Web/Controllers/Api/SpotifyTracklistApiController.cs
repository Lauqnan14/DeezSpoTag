using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using System.Linq;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[LocalApiAuthorize]
[Route("api/spotify/tracklist")]
public class SpotifyTracklistApiController : ControllerBase
{
    private const string ArtistType = "artist";
    private const string UrlRequiredMessage = "URL is required.";
    private readonly SpotifyTracklistService _tracklistService;
    private readonly SpotifyArtistService _spotifyArtistService;
    private readonly DeezSpoTag.Services.Settings.ISettingsService _settingsService;

    public SpotifyTracklistApiController(
        SpotifyTracklistService tracklistService,
        SpotifyArtistService spotifyArtistService,
        DeezSpoTag.Services.Settings.ISettingsService settingsService)
    {
        _tracklistService = tracklistService;
        _spotifyArtistService = spotifyArtistService;
        _settingsService = settingsService;
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return BadRequest(new { error = UrlRequiredMessage });
        }

        if (SpotifyMetadataService.TryParseSpotifyUrl(url, out var parsedType, out var parsedId)
            && string.Equals(parsedType, "artist", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(parsedId))
        {
            var topSongs = await BuildArtistTopSongsTracklistAsync(parsedId, cancellationToken);
            if (topSongs == null)
            {
                return Ok(new { available = false });
            }

            var settings = _settingsService.LoadSettings();
            var strictSpotifyDeezerMode = settings.StrictSpotifyDeezerMode;
            var allowFallbackSearch = !strictSpotifyDeezerMode && (
                settings.FallbackSearch
                || string.Equals(settings.SpotifyPlaylistTrackSource, "librespot", StringComparison.OrdinalIgnoreCase)
                || IsPathfinderTrackSource(settings.SpotifyPlaylistTrackSource));
            var matched = await _tracklistService.BuildMatchedTracksAsync(
                ArtistType,
                parsedId,
                topSongs.Tracks,
                allowFallbackSearch,
                cancellationToken);
            var tracklist = new SpotifyTracklistResult
            {
                Id = topSongs.Tracklist.Id,
                Title = topSongs.Tracklist.Title,
                Description = topSongs.Tracklist.Description,
                Creator = topSongs.Tracklist.Creator,
                Followers = topSongs.Tracklist.Followers,
                PictureXl = topSongs.Tracklist.PictureXl,
                PictureBig = topSongs.Tracklist.PictureBig,
                NbTracks = matched.Tracks.Count,
                Tracks = matched.Tracks
            };

            return Ok(new
            {
                available = true,
                tracklist,
                matching = matched.PendingCount > 0
                    ? new { token = matched.MatchToken, pending = matched.PendingCount }
                    : null
            });
        }

        var payload = await _tracklistService.GetTracklistAsync(url, cancellationToken);
        if (payload == null)
        {
            return Ok(new { available = false });
        }

        return Ok(new
        {
            available = true,
            tracklist = payload.Tracklist,
            matching = payload.PendingCount > 0
                ? new { token = payload.MatchToken, pending = payload.PendingCount }
                : null
        });
    }


    private static bool IsPathfinderTrackSource(string? value)
    {
        return string.Equals(value, "pathfinder", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "spotiflac", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<SpotifyArtistTopSongsPayload?> BuildArtistTopSongsTracklistAsync(
        string spotifyArtistId,
        CancellationToken cancellationToken)
    {
        var artistPage = await _spotifyArtistService.GetArtistPageBySpotifyIdAsync(
            spotifyArtistId,
            spotifyArtistId,
            forceRefresh: false,
            cancellationToken);

        if (artistPage == null || artistPage.Artist == null)
        {
            return null;
        }

        var artistName = string.IsNullOrWhiteSpace(artistPage.Artist.Name)
            ? "Spotify Artist"
            : artistPage.Artist.Name;

        var artistCover = SelectImageUrl(artistPage.Artist.Images);
        var artistId = string.IsNullOrWhiteSpace(artistPage.Artist.Id) ? spotifyArtistId : artistPage.Artist.Id;
        var trackSummaries = (artistPage.TopTracks ?? new List<SpotifyTrack>())
            .Select(track => BuildArtistTopTrackSummary(track, artistName, artistId))
            .ToList();

        var tracklist = new SpotifyTracklistResult
        {
            Id = spotifyArtistId,
            Title = $"{artistName} - Top Songs",
            Description = "Top Songs",
            Creator = new SpotifyTracklistCreator
            {
                Name = "Spotify",
                Avatar = string.Empty
            },
            Followers = artistPage.Artist.Followers,
            PictureXl = artistCover ?? string.Empty,
            PictureBig = artistCover ?? string.Empty,
            NbTracks = trackSummaries.Count,
            Tracks = new List<SpotifyTracklistTrack>()
        };

        return new SpotifyArtistTopSongsPayload(tracklist, trackSummaries);
    }

    private static SpotifyTrackSummary BuildArtistTopTrackSummary(
        SpotifyTrack track,
        string artistName,
        string artistId)
    {
        var spotifyTrackId = string.IsNullOrWhiteSpace(track.Id) ? string.Empty : track.Id.Trim();
        var sourceUrl = ResolveTrackSourceUrl(track.SourceUrl, spotifyTrackId);
        var albumCover = SelectImageUrl(track.AlbumImages);
        return new SpotifyTrackSummary(
            Id: spotifyTrackId,
            Name: track.Name ?? string.Empty,
            Artists: artistName,
            Album: track.AlbumName ?? string.Empty,
            DurationMs: track.DurationMs > 0 ? track.DurationMs : null,
            SourceUrl: sourceUrl,
            ImageUrl: albumCover ?? string.Empty,
            Isrc: track.Isrc ?? string.Empty,
            ReleaseDate: track.ReleaseDate,
            Explicit: track.Explicit ?? false)
        {
            AlbumId = track.AlbumId ?? string.Empty,
            AlbumArtist = artistName,
            ArtistIds = string.IsNullOrWhiteSpace(artistId) ? Array.Empty<string>() : new[] { artistId },
            PreviewUrl = track.PreviewUrl,
            Popularity = track.Popularity,
            HasLyrics = track.HasLyrics
        };
    }

    private static string ResolveTrackSourceUrl(string? sourceUrl, string spotifyTrackId)
    {
        if (!string.IsNullOrWhiteSpace(sourceUrl))
        {
            return sourceUrl;
        }

        if (string.IsNullOrWhiteSpace(spotifyTrackId))
        {
            return string.Empty;
        }

        return $"https://open.spotify.com/track/{spotifyTrackId}";
    }

    private static string? SelectImageUrl(List<SpotifyImage>? images)
    {
        if (images == null || images.Count == 0)
        {
            return null;
        }

        return images
            .OrderByDescending(image => image.Width ?? 0)
            .ThenByDescending(image => image.Height ?? 0)
            .Select(image => image.Url)
            .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));
    }

    private sealed record SpotifyArtistTopSongsPayload(
        SpotifyTracklistResult Tracklist,
        List<SpotifyTrackSummary> Tracks);
}
