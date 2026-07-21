using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[LocalApiAuthorize]
[Route("api/spotify/tracklist")]
[Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryToken]
public class SpotifyPlaylistTracklistApiController : ControllerBase
{
    private const string PlaylistType = "playlist";
    private const string LibrespotTrackSource = "librespot";
    private const string PathfinderTrackSource = "pathfinder";
    private const string UrlRequiredMessage = "URL is required.";
    private readonly SpotifyTracklistService _tracklistService;
    private readonly ISpotifyTracklistMatchStore _matchStore;
    private readonly SpotifyMetadataService _metadataService;
    private readonly DeezSpoTag.Services.Settings.ISettingsService _settingsService;

    public SpotifyPlaylistTracklistApiController(
        SpotifyTracklistService tracklistService,
        ISpotifyTracklistMatchStore matchStore,
        SpotifyMetadataService metadataService,
        DeezSpoTag.Services.Settings.ISettingsService settingsService)
    {
        _tracklistService = tracklistService;
        _matchStore = matchStore;
        _metadataService = metadataService;
        _settingsService = settingsService;
    }

    [HttpGet("playlist/tracks")]
    public async Task<IActionResult> PlaylistTracks(
        [FromQuery] string url,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        [FromQuery] bool hydrate = true,
        CancellationToken cancellationToken = default)
    {
        var playlistId = ParsePlaylistId(url, out var validationError);
        if (validationError != null)
        {
            return validationError;
        }

        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return Ok(new { available = false });
        }

        var settings = _settingsService.LoadSettings();
        var normalizedTrackSource = NormalizeTrackSource(settings.SpotifyPlaylistTrackSource);
        if (string.Equals(settings.SpotifyPlaylistTrackSource, LibrespotTrackSource, StringComparison.OrdinalIgnoreCase))
        {
            hydrate = true;
        }

        var page = await _metadataService.FetchPlaylistTrackPageAsync(
            playlistId,
            offset,
            limit,
            normalizedTrackSource,
            hydrate,
            cancellationToken);
        if (page == null || !page.IsComplete)
        {
            return Ok(new
            {
                available = false,
                failureCode = page?.FailureCode ?? "spotify_page_failed"
            });
        }

        // Render Spotify rows immediately and resolve only this visible page in the shared match queue.
        var tracks = SpotifyTracklistMapper.MapTracks(page.Tracks.ToList(), offset);

        var tracklist = new SpotifyTracklistResult
        {
            Id = playlistId,
            Title = string.IsNullOrWhiteSpace(page.Name) ? "Spotify Playlist" : page.Name,
            Description = page.Description ?? string.Empty,
            Creator = new SpotifyTracklistCreator
            {
                Name = string.IsNullOrWhiteSpace(page.OwnerName) ? "Spotify" : page.OwnerName,
                Avatar = page.OwnerImageUrl ?? string.Empty
            },
            Followers = page.Followers,
            PictureXl = page.ImageUrl ?? string.Empty,
            PictureBig = page.ImageUrl ?? string.Empty,
            NbTracks = page.TotalTracks ?? page.Tracks.Count,
            Tracks = new List<SpotifyTracklistTrack>()
        };

        var token = $"spotify:playlist:{playlistId}";
        var visibleMatch = _tracklistService.StartVisibleTrackMatching(
            token,
            offset,
            page.Tracks,
            allowFallbackSearch: false);
        if (IsPathfinderTrackSource(settings.SpotifyPlaylistTrackSource))
        {
            tracks = _tracklistService.ApplyStoredMatchesToTracks(token, tracks);
        }

        object? matching = null;
        if (visibleMatch is { Pending: > 0 })
        {
            matching = new { token = visibleMatch.Token, pending = visibleMatch.Pending };
        }

        return Ok(new
        {
            available = true,
            tracklist,
            trackSource = normalizedTrackSource,
            offset,
            nextOffset = page.NextOffset,
            limit,
            total = page.TotalTracks,
            hasMore = page.HasMore,
            tracks,
            matching
        });
    }

    [HttpGet("librespot/tracks")]
    public async Task<IActionResult> LibrespotTracks([FromQuery] string ids, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ids))
        {
            return BadRequest(new { error = "ids are required." });
        }

        var idList = ids
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (idList.Count == 0)
        {
            return Ok(new { available = true, tracks = Array.Empty<SpotifyTrackSummary>() });
        }

        var tracks = await _metadataService.FetchLibrespotTracksAsync(idList, cancellationToken);
        return Ok(new { available = true, tracks });
    }

    private static bool IsPathfinderTrackSource(string? value)
    {
        return string.Equals(value, PathfinderTrackSource, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "spotiflac", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeTrackSource(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return PathfinderTrackSource;
        }

        return IsPathfinderTrackSource(value) ? PathfinderTrackSource : value.Trim().ToLowerInvariant();
    }

    private static string? ParsePlaylistId(string? url, out IActionResult? validationError)
    {
        validationError = null;
        if (string.IsNullOrWhiteSpace(url))
        {
            validationError = new BadRequestObjectResult(new { error = UrlRequiredMessage });
            return null;
        }

        if (!SpotifyMetadataService.TryParseSpotifyUrl(url, out var type, out var playlistId)
            || !string.Equals(type, PlaylistType, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return playlistId;
    }
}
