using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[LocalApiAuthorize]
[Route("api/spotify/tracklist")]
public class SpotifyTrendingTracklistApiController : ControllerBase
{
    private const string PlaylistType = "playlist";
    private const string LibrespotTrackSource = "librespot";
    private const string SpotifyHomeTrendingSourceId = "home-trending-songs";
    private const string SpotifyTrendingSongsSectionUri = "spotify:section:0JQ5DB5E8N831KzFzsBBQ2";
    private readonly SpotifyPathfinderMetadataClient _pathfinderMetadataClient;
    private readonly SpotifyTracklistService _tracklistService;
    private readonly DeezSpoTag.Services.Settings.ISettingsService _settingsService;

    public SpotifyTrendingTracklistApiController(
        SpotifyPathfinderMetadataClient pathfinderMetadataClient,
        SpotifyTracklistService tracklistService,
        DeezSpoTag.Services.Settings.ISettingsService settingsService)
    {
        _pathfinderMetadataClient = pathfinderMetadataClient;
        _tracklistService = tracklistService;
        _settingsService = settingsService;
    }

    [HttpGet("trending")]
    public async Task<IActionResult> Trending(
        [FromQuery] string? id = null,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var sourceId = string.IsNullOrWhiteSpace(id)
            ? SpotifyHomeTrendingSourceId
            : id.Trim();
        if (!string.Equals(sourceId, SpotifyHomeTrendingSourceId, StringComparison.OrdinalIgnoreCase))
        {
            return Ok(new { available = false });
        }

        var boundedLimit = Math.Clamp(limit, 1, 100);
        var summaries = await _pathfinderMetadataClient.FetchBrowseSectionTrackSummariesWithBlobAsync(
            SpotifyTrendingSongsSectionUri,
            0,
            boundedLimit,
            cancellationToken);
        if (summaries.Count == 0)
        {
            return Ok(new { available = false });
        }

        var settings = _settingsService.LoadSettings();
        var strictSpotifyDeezerMode = settings.StrictSpotifyDeezerMode;
        var allowFallbackSearch = !strictSpotifyDeezerMode && (
            settings.FallbackSearch
            || string.Equals(settings.SpotifyPlaylistTrackSource, LibrespotTrackSource, StringComparison.OrdinalIgnoreCase)
            || IsPathfinderTrackSource(settings.SpotifyPlaylistTrackSource));
        var matched = await _tracklistService.BuildMatchedTracksAsync(
            PlaylistType,
            sourceId,
            summaries,
            allowFallbackSearch,
            cancellationToken);

        var cover = summaries
            .Select(track => track.ImageUrl)
            .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url)) ?? string.Empty;

        var tracklist = new SpotifyTracklistResult
        {
            Id = sourceId,
            Title = "Trending songs",
            Description = "Spotify Home Feed",
            Creator = new SpotifyTracklistCreator
            {
                Name = "Spotify",
                Avatar = string.Empty
            },
            Followers = null,
            PictureXl = cover,
            PictureBig = cover,
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

    private static bool IsPathfinderTrackSource(string? value)
    {
        return string.Equals(value, "pathfinder", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "spotiflac", StringComparison.OrdinalIgnoreCase);
    }
}
