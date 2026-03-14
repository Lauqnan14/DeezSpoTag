using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[LocalApiAuthorize]
[Route("api/spotify/tracklist")]
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

    [HttpGet("playlist/metadata")]
    public async Task<IActionResult> PlaylistMetadata([FromQuery] string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return BadRequest(new { error = UrlRequiredMessage });
        }

        if (!SpotifyMetadataService.TryParseSpotifyUrl(url, out var type, out var playlistId)
            || !string.Equals(type, PlaylistType, StringComparison.OrdinalIgnoreCase))
        {
            return Ok(new { available = false });
        }

        var metadata = await _metadataService.FetchPlaylistMetadataAsync(playlistId, cancellationToken);
        if (metadata == null)
        {
            return Ok(new { available = false });
        }

        var settings = _settingsService.LoadSettings();
        var tracklist = new SpotifyTracklistResult
        {
            Id = metadata.Id,
            Title = metadata.Name,
            Description = metadata.Subtitle ?? string.Empty,
            Creator = new SpotifyTracklistCreator
            {
                Name = metadata.OwnerName ?? "Spotify",
                Avatar = metadata.OwnerImageUrl ?? string.Empty
            },
            Followers = metadata.Followers,
            PictureXl = metadata.ImageUrl ?? string.Empty,
            PictureBig = metadata.ImageUrl ?? string.Empty,
            NbTracks = metadata.TotalTracks ?? 0,
            Tracks = new List<SpotifyTracklistTrack>()
        };

        return Ok(new
        {
            available = true,
            tracklist,
            trackSource = NormalizeTrackSource(settings.SpotifyPlaylistTrackSource)
        });
    }

    [HttpGet("playlist/tracks")]
    public async Task<IActionResult> PlaylistTracks(
        [FromQuery] string url,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        [FromQuery] bool hydrate = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return BadRequest(new { error = UrlRequiredMessage });
        }

        if (!SpotifyMetadataService.TryParseSpotifyUrl(url, out var type, out var playlistId)
            || !string.Equals(type, PlaylistType, StringComparison.OrdinalIgnoreCase))
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
        if (page == null)
        {
            return Ok(new { available = false });
        }

        var allowFallbackSearch = settings.FallbackSearch
            || string.Equals(settings.SpotifyPlaylistTrackSource, LibrespotTrackSource, StringComparison.OrdinalIgnoreCase)
            || IsPathfinderTrackSource(settings.SpotifyPlaylistTrackSource);
        var tracks = await _tracklistService.ResolveVisibleTracksAsync(
            page.Tracks,
            offset,
            page.SnapshotId,
            allowFallbackSearch,
            cancellationToken);
        if (IsPathfinderTrackSource(settings.SpotifyPlaylistTrackSource))
        {
            var token = $"spotify:playlist:{playlistId}";
            tracks = _tracklistService.ApplyStoredMatchesToTracks(token, tracks);
        }

        object? matching = null;
        if (IsPathfinderTrackSource(settings.SpotifyPlaylistTrackSource))
        {
            var token = $"spotify:playlist:{playlistId}";
            var snapshot = _matchStore.GetSnapshot(token);
            if (snapshot is { Pending: > 0 })
            {
                matching = new { token, pending = snapshot.Pending };
            }
        }

        return Ok(new
        {
            available = true,
            offset,
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

    [HttpPost("playlist/match")]
    public async Task<IActionResult> PlaylistMatch([FromQuery] string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return BadRequest(new { error = UrlRequiredMessage });
        }

        if (!SpotifyMetadataService.TryParseSpotifyUrl(url, out var type, out _)
            || !string.Equals(type, PlaylistType, StringComparison.OrdinalIgnoreCase))
        {
            return Ok(new { available = false });
        }

        var result = await _tracklistService.StartPlaylistMatchingAsync(url, cancellationToken);
        if (result == null)
        {
            return Ok(new { available = false });
        }

        return Ok(new
        {
            available = true,
            matching = result.Pending > 0
                ? new { token = result.Token, pending = result.Pending }
                : null
        });
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
}
