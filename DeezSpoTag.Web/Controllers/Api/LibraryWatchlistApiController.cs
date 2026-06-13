using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/library/watchlist")]
[ApiController]
[Authorize]
[Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryToken]
public class LibraryWatchlistApiController : ControllerBase
{
    private const string SpotifySource = "spotify";
    private const string AppleSource = "apple";
    private const string DeezerSource = "deezer";
    private const string AddWatchlistFailedMessage = "Failed to add watchlist entry.";
    private readonly LibraryRepository _repository;
    private readonly LibraryConfigStore _configStore;
    private readonly AutoTagProfileResolutionService _profileResolutionService;
    private readonly PlaylistWatchHostedService? _playlistWatchHostedService;

    public LibraryWatchlistApiController(
        LibraryRepository repository,
        LibraryConfigStore configStore,
        AutoTagProfileResolutionService profileResolutionService,
        PlaylistWatchHostedService? playlistWatchHostedService = null)
    {
        _repository = repository;
        _configStore = configStore;
        _profileResolutionService = profileResolutionService;
        _playlistWatchHostedService = playlistWatchHostedService;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var items = await _repository.GetWatchlistAsync(cancellationToken);
        return Ok(items);
    }

    [HttpGet("{artistId:long}")]
    public async Task<IActionResult> GetStatus(long artistId, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var watching = await _repository.IsWatchlistedAsync(artistId, cancellationToken);
        return Ok(new { watching });
    }

    [HttpGet("spotify/{spotifyId}")]
    public async Task<IActionResult> GetSpotifyStatus(string spotifyId, CancellationToken cancellationToken)
    {
        var normalizedSpotifyId = WatchlistPreferenceNormalizer.SpotifyId(spotifyId);
        if (string.IsNullOrWhiteSpace(normalizedSpotifyId))
        {
            return BadRequest("Spotify ID is required.");
        }

        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var watching = await _repository.IsWatchlistedBySpotifyIdAsync(normalizedSpotifyId, cancellationToken);
        if (!watching)
        {
            var artistId = await _repository.GetArtistIdBySourceIdAsync(SpotifySource, normalizedSpotifyId, cancellationToken);
            if (artistId.HasValue)
            {
                watching = await _repository.IsWatchlistedAsync(artistId.Value, cancellationToken);
            }
        }

        return Ok(new { watching });
    }

    [HttpGet("apple/{appleId}")]
    public async Task<IActionResult> GetAppleStatus(string appleId, CancellationToken cancellationToken)
    {
        var normalizedAppleId = WatchlistPreferenceNormalizer.IncomingId(appleId);
        if (string.IsNullOrWhiteSpace(normalizedAppleId))
        {
            return BadRequest("Apple ID is required.");
        }

        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var artistId = await _repository.GetArtistIdBySourceIdAsync(AppleSource, normalizedAppleId, cancellationToken);
        var watching = artistId.HasValue && await _repository.IsWatchlistedAsync(artistId.Value, cancellationToken);
        return Ok(new { watching });
    }

    [HttpGet("deezer/{deezerId}")]
    public async Task<IActionResult> GetDeezerStatus(string deezerId, CancellationToken cancellationToken)
    {
        var normalizedDeezerId = WatchlistPreferenceNormalizer.IncomingId(deezerId);
        if (string.IsNullOrWhiteSpace(normalizedDeezerId))
        {
            return BadRequest("Deezer ID is required.");
        }

        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var artistId = await _repository.GetArtistIdBySourceIdAsync(DeezerSource, normalizedDeezerId, cancellationToken);
        var watching = artistId.HasValue && await _repository.IsWatchlistedAsync(artistId.Value, cancellationToken);
        return Ok(new { watching });
    }

    public sealed record WatchlistRequest(long? ArtistId, string ArtistName);
    public sealed record SpotifyWatchlistRequest(string SpotifyId, string ArtistName, string? DeezerId);
    public sealed record AppleWatchlistRequest(string AppleId, string ArtistName, string? SpotifyId, string? DeezerId);
    public sealed record DeezerWatchlistRequest(string DeezerId, string ArtistName, string? SpotifyId);
    public sealed record ArtistWatchlistPreferenceRequest(
        long? DestinationFolderId,
        IReadOnlyList<string>? WatchedArtistAlbumGroup,
        bool? WatchArtistTopSongsEnabled,
        bool? WatchArtistLatestReleasesOnly,
        string? PreferredEngine,
        IReadOnlyList<PlaylistTrackRoutingRule>? RoutingRules,
        long? AtmosDestinationFolderId,
        string? DownloadVariantMode,
        string? TopSongsSyncMode,
        bool? DownloadDiscographyEnabled,
        IReadOnlyList<PlaylistTrackBlockRule>? BlockRules);

    [HttpPost]
    public async Task<IActionResult> Add([FromBody] WatchlistRequest request, CancellationToken cancellationToken)
    {
        if (request is null || !request.ArtistId.HasValue || request.ArtistId.Value <= 0 || string.IsNullOrWhiteSpace(request.ArtistName))
        {
            return BadRequest("Artist ID and name are required.");
        }

        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var artistId = request.ArtistId.Value;
        var spotifyId = await _repository.GetArtistSourceIdAsync(artistId, SpotifySource, cancellationToken);
        var deezerId = await _repository.GetArtistSourceIdAsync(artistId, DeezerSource, cancellationToken);
        var added = await _repository.AddWatchlistAsync(
            artistId,
            request.ArtistName,
            spotifyId,
            deezerId,
            cancellationToken);
        return CreateAddedResponse(request.ArtistName, added);
    }

    [HttpPost("spotify")]
    public async Task<IActionResult> AddSpotify([FromBody] SpotifyWatchlistRequest request, CancellationToken cancellationToken)
    {
        var normalizedSpotifyId = WatchlistPreferenceNormalizer.SpotifyId(request?.SpotifyId);
        var normalizedArtistName = WatchlistPreferenceNormalizer.IncomingText(request?.ArtistName);
        var normalizedDeezerId = WatchlistPreferenceNormalizer.IncomingId(request?.DeezerId);
        if (request is null || string.IsNullOrWhiteSpace(normalizedSpotifyId) || string.IsNullOrWhiteSpace(normalizedArtistName))
        {
            return BadRequest("Spotify ID and artist name are required.");
        }

        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var artistId = await ResolveArtistIdForSpotifyAsync(normalizedSpotifyId, normalizedDeezerId, cancellationToken);
        var added = await _repository.AddWatchlistAsync(
            artistId,
            normalizedArtistName,
            normalizedSpotifyId,
            normalizedDeezerId,
            cancellationToken);
        return CreateAddedResponse(normalizedArtistName, added);
    }

    [HttpPost("apple")]
    public async Task<IActionResult> AddApple([FromBody] AppleWatchlistRequest request, CancellationToken cancellationToken)
    {
        var normalizedAppleId = WatchlistPreferenceNormalizer.IncomingId(request?.AppleId);
        var normalizedArtistName = WatchlistPreferenceNormalizer.IncomingText(request?.ArtistName);
        var normalizedSpotifyId = WatchlistPreferenceNormalizer.SpotifyId(request?.SpotifyId);
        var normalizedDeezerId = WatchlistPreferenceNormalizer.IncomingId(request?.DeezerId);
        if (request is null || string.IsNullOrWhiteSpace(normalizedAppleId) || string.IsNullOrWhiteSpace(normalizedArtistName))
        {
            return BadRequest("Apple ID and artist name are required.");
        }

        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var artistId = await ResolveArtistIdForAppleAsync(normalizedAppleId, normalizedDeezerId, normalizedSpotifyId, cancellationToken);
        await _repository.UpsertArtistSourceIdAsync(artistId, AppleSource, normalizedAppleId, cancellationToken);

        if (!string.IsNullOrWhiteSpace(normalizedSpotifyId))
        {
            await _repository.UpsertArtistSourceIdAsync(artistId, SpotifySource, normalizedSpotifyId, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(normalizedDeezerId))
        {
            await _repository.UpsertArtistSourceIdAsync(artistId, DeezerSource, normalizedDeezerId, cancellationToken);
        }

        var spotifyId = !string.IsNullOrWhiteSpace(normalizedSpotifyId)
            ? normalizedSpotifyId
            : await _repository.GetArtistSourceIdAsync(artistId, SpotifySource, cancellationToken);
        var deezerId = !string.IsNullOrWhiteSpace(normalizedDeezerId)
            ? normalizedDeezerId
            : await _repository.GetArtistSourceIdAsync(artistId, DeezerSource, cancellationToken);

        var added = await _repository.AddWatchlistAsync(
            artistId,
            normalizedArtistName,
            spotifyId,
            deezerId,
            cancellationToken);
        return CreateAddedResponse(normalizedArtistName, added);
    }

    [HttpPost("deezer")]
    public async Task<IActionResult> AddDeezer([FromBody] DeezerWatchlistRequest request, CancellationToken cancellationToken)
    {
        var normalizedDeezerId = WatchlistPreferenceNormalizer.IncomingId(request?.DeezerId);
        var normalizedArtistName = WatchlistPreferenceNormalizer.IncomingText(request?.ArtistName);
        var normalizedSpotifyId = WatchlistPreferenceNormalizer.SpotifyId(request?.SpotifyId);
        if (request is null || string.IsNullOrWhiteSpace(normalizedDeezerId) || string.IsNullOrWhiteSpace(normalizedArtistName))
        {
            return BadRequest("Deezer ID and artist name are required.");
        }

        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var artistId = await ResolveArtistIdForDeezerAsync(normalizedDeezerId, normalizedSpotifyId, cancellationToken);
        await _repository.UpsertArtistSourceIdAsync(artistId, DeezerSource, normalizedDeezerId, cancellationToken);

        if (!string.IsNullOrWhiteSpace(normalizedSpotifyId))
        {
            await _repository.UpsertArtistSourceIdAsync(artistId, SpotifySource, normalizedSpotifyId, cancellationToken);
        }

        var spotifyId = !string.IsNullOrWhiteSpace(normalizedSpotifyId)
            ? normalizedSpotifyId
            : await _repository.GetArtistSourceIdAsync(artistId, SpotifySource, cancellationToken);

        var added = await _repository.AddWatchlistAsync(
            artistId,
            normalizedArtistName,
            spotifyId,
            normalizedDeezerId,
            cancellationToken);
        return CreateAddedResponse(normalizedArtistName, added);
    }

    [HttpDelete("{artistId:long}")]
    public async Task<IActionResult> Remove(long artistId, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var removed = await _repository.RemoveWatchlistAsync(artistId, cancellationToken);
        return Ok(new { removed });
    }

    [HttpPost("{artistId:long}/preferences")]
    public async Task<IActionResult> SavePreferences(
        long artistId,
        [FromBody] ArtistWatchlistPreferenceRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest("Artist watchlist preference request is required.");
        }

        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var validFolderIds = await GetValidFolderIdsAsync(cancellationToken);

        if (request.DestinationFolderId is long folderId && !validFolderIds.Contains(folderId))
        {
            return BadRequest("Destination folder was not found or is disabled.");
        }

        if (request.AtmosDestinationFolderId is long atmosFolderId && !validFolderIds.Contains(atmosFolderId))
        {
            return BadRequest("Atmos destination folder was not found or is disabled.");
        }

        IReadOnlyList<string>? normalizedAlbumGroups = null;
        if (request.WatchedArtistAlbumGroup != null)
        {
            normalizedAlbumGroups = ArtistWatchService.NormalizeAlbumGroups(request.WatchedArtistAlbumGroup);
            if (normalizedAlbumGroups.Count == 0 && request.WatchArtistTopSongsEnabled != true)
            {
                return BadRequest("Select at least one artist watch option.");
            }
        }

        var preferredEngine = WatchlistPreferenceNormalizer.PreferredEngine(request.PreferredEngine);
        var downloadVariantMode = WatchlistPreferenceNormalizer.DownloadVariantMode(request.DownloadVariantMode);
        var topSongsSyncMode = WatchlistPreferenceNormalizer.TopSongsSyncMode(request.TopSongsSyncMode);
        var routingRules = WatchlistPreferenceNormalizer.RoutingRules(request.RoutingRules);
        if (routingRules?.Any(rule => !validFolderIds.Contains(rule.DestinationFolderId)) == true)
        {
            return BadRequest("Routing destination folder was not found or is disabled.");
        }
        var blockRules = WatchlistPreferenceNormalizer.BlockRules(request.BlockRules);

        var updated = await _repository.UpdateWatchlistPreferencesAsync(
            new LibraryRepository.ArtistWatchPreferenceUpdateInput(
                artistId,
                request.DestinationFolderId,
                normalizedAlbumGroups,
                request.WatchArtistTopSongsEnabled,
                request.WatchArtistLatestReleasesOnly,
                preferredEngine,
                routingRules,
                request.AtmosDestinationFolderId,
                downloadVariantMode,
                topSongsSyncMode,
                request.DownloadDiscographyEnabled,
                blockRules),
            cancellationToken);
        if (!updated)
        {
            return NotFound("Artist watchlist entry not found.");
        }

        return Ok(new
        {
            artistId,
            destinationFolderId = request.DestinationFolderId,
            watchedArtistAlbumGroup = normalizedAlbumGroups,
            watchArtistTopSongsEnabled = request.WatchArtistTopSongsEnabled,
            watchArtistLatestReleasesOnly = request.WatchArtistLatestReleasesOnly,
            preferredEngine,
            routingRules,
            atmosDestinationFolderId = request.AtmosDestinationFolderId,
            downloadVariantMode,
            topSongsSyncMode,
            downloadDiscographyEnabled = request.DownloadDiscographyEnabled,
            blockRules
        });
    }

    [HttpDelete("spotify/{spotifyId}")]
    public async Task<IActionResult> RemoveSpotify(string spotifyId, CancellationToken cancellationToken)
    {
        var normalizedSpotifyId = WatchlistPreferenceNormalizer.SpotifyId(spotifyId);
        if (string.IsNullOrWhiteSpace(normalizedSpotifyId))
        {
            return BadRequest("Spotify ID is required.");
        }

        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var removed = await _repository.RemoveWatchlistBySpotifyIdAsync(normalizedSpotifyId, cancellationToken);
        if (!removed)
        {
            var artistId = await _repository.GetArtistIdBySourceIdAsync(SpotifySource, normalizedSpotifyId, cancellationToken);
            if (artistId.HasValue)
            {
                removed = await _repository.RemoveWatchlistAsync(artistId.Value, cancellationToken);
            }
        }

        return Ok(new { removed });
    }

    [HttpDelete("apple/{appleId}")]
    public async Task<IActionResult> RemoveApple(string appleId, CancellationToken cancellationToken)
    {
        var normalizedAppleId = WatchlistPreferenceNormalizer.IncomingId(appleId);
        if (string.IsNullOrWhiteSpace(normalizedAppleId))
        {
            return BadRequest("Apple ID is required.");
        }

        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var artistId = await _repository.GetArtistIdBySourceIdAsync(AppleSource, normalizedAppleId, cancellationToken);
        var removed = artistId.HasValue && await _repository.RemoveWatchlistAsync(artistId.Value, cancellationToken);
        return Ok(new { removed });
    }

    [HttpDelete("deezer/{deezerId}")]
    public async Task<IActionResult> RemoveDeezer(string deezerId, CancellationToken cancellationToken)
    {
        var normalizedDeezerId = WatchlistPreferenceNormalizer.IncomingId(deezerId);
        if (string.IsNullOrWhiteSpace(normalizedDeezerId))
        {
            return BadRequest("Deezer ID is required.");
        }

        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var artistId = await _repository.GetArtistIdBySourceIdAsync(DeezerSource, normalizedDeezerId, cancellationToken);
        var removed = artistId.HasValue && await _repository.RemoveWatchlistAsync(artistId.Value, cancellationToken);
        return Ok(new { removed });
    }

    [HttpPost("trigger-check")]
    public async Task<IActionResult> TriggerAll(CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var items = await _repository.GetWatchlistAsync(cancellationToken);
        var queued = _playlistWatchHostedService != null;
        if (_playlistWatchHostedService != null)
        {
            _ = _playlistWatchHostedService.TriggerRunOnceAsync(CancellationToken.None);
        }

        return Ok(new { triggered = queued ? items.Count : 0 });
    }

    [HttpPost("trigger-check/{artistId:long}")]
    public async Task<IActionResult> TriggerOne(long artistId, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var items = await _repository.GetWatchlistAsync(cancellationToken);
        var item = items.FirstOrDefault(entry => entry.ArtistId == artistId);
        if (item == null)
        {
            return NotFound("Artist watchlist entry not found.");
        }

        var triggered = _playlistWatchHostedService != null;
        if (_playlistWatchHostedService != null)
        {
            _ = _playlistWatchHostedService.TriggerRunOnceAsync(CancellationToken.None);
        }

        return Ok(new { triggered = triggered ? 1 : 0 });
    }

    private ObjectResult DatabaseNotConfigured()
    {
        return StatusCode(503, new { error = "Library DB not configured." });
    }

    private async Task<HashSet<long>> GetValidFolderIdsAsync(CancellationToken cancellationToken)
    {
        var state = await _profileResolutionService.LoadNormalizedStateAsync(includeFolders: true, cancellationToken);
        return state.FoldersById.Values
            .Where(folder => IsWatchlistMusicDestinationFolder(folder)
                && AutoTagProfileResolutionService.ResolveFolderProfile(state, folder.Id, folder.AutoTagProfileId) != null)
            .Select(folder => folder.Id)
            .ToHashSet();
    }

    private static bool IsWatchlistMusicDestinationFolder(FolderDto folder)
    {
        if (!folder.Enabled || string.IsNullOrWhiteSpace(folder.RootPath))
        {
            return false;
        }

        var desiredQuality = folder.DesiredQuality?.Trim().ToLowerInvariant() ?? string.Empty;
        return !desiredQuality.Contains("video", StringComparison.Ordinal)
            && !desiredQuality.Contains("podcast", StringComparison.Ordinal);
    }

    private IActionResult CreateAddedResponse(string artistName, object? addedEntry)
    {
        if (addedEntry is null)
        {
            return StatusCode(500, AddWatchlistFailedMessage);
        }

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"Watchlist added: {artistName}."));

        return Ok(addedEntry);
    }

    private async Task<long> ResolveArtistIdForSpotifyAsync(string spotifyId, string? deezerId, CancellationToken cancellationToken)
    {
        var existing = await _repository.GetArtistIdBySourceIdAsync(SpotifySource, spotifyId, cancellationToken);
        if (existing.HasValue)
        {
            return existing.Value;
        }

        if (!string.IsNullOrWhiteSpace(deezerId))
        {
            var byDeezer = await _repository.GetArtistIdBySourceIdAsync(DeezerSource, deezerId, cancellationToken);
            if (byDeezer.HasValue)
            {
                return byDeezer.Value;
            }

            if (long.TryParse(deezerId, out var parsed))
            {
                return parsed;
            }
        }

        return GetSyntheticArtistId(SpotifySource, spotifyId);
    }

    private async Task<long> ResolveArtistIdForAppleAsync(string appleId, string? deezerId, string? spotifyId, CancellationToken cancellationToken)
    {
        var existing = await _repository.GetArtistIdBySourceIdAsync(AppleSource, appleId, cancellationToken);
        if (existing.HasValue)
        {
            return existing.Value;
        }

        if (!string.IsNullOrWhiteSpace(spotifyId))
        {
            var bySpotify = await _repository.GetArtistIdBySourceIdAsync(SpotifySource, spotifyId, cancellationToken);
            if (bySpotify.HasValue)
            {
                return bySpotify.Value;
            }
        }

        if (!string.IsNullOrWhiteSpace(deezerId))
        {
            var byDeezer = await _repository.GetArtistIdBySourceIdAsync(DeezerSource, deezerId, cancellationToken);
            if (byDeezer.HasValue)
            {
                return byDeezer.Value;
            }

            if (long.TryParse(deezerId, out var parsed))
            {
                return parsed;
            }
        }

        return GetSyntheticArtistId(AppleSource, appleId);
    }

    private async Task<long> ResolveArtistIdForDeezerAsync(string deezerId, string? spotifyId, CancellationToken cancellationToken)
    {
        var existing = await _repository.GetArtistIdBySourceIdAsync(DeezerSource, deezerId, cancellationToken);
        if (existing.HasValue)
        {
            return existing.Value;
        }

        if (long.TryParse(deezerId, out var parsed))
        {
            return parsed;
        }

        if (!string.IsNullOrWhiteSpace(spotifyId))
        {
            var bySpotify = await _repository.GetArtistIdBySourceIdAsync(SpotifySource, spotifyId, cancellationToken);
            if (bySpotify.HasValue)
            {
                return bySpotify.Value;
            }
        }

        return GetSyntheticArtistId(DeezerSource, deezerId);
    }

    private static long GetSyntheticArtistId(string source, string sourceId)
    {
        const ulong offset = 1469598103934665603;
        const ulong prime = 1099511628211;
        ulong hash = offset;
        var input = $"{source}:{sourceId}";
        foreach (var ch in input)
        {
            hash ^= (byte)ch;
            hash *= prime;
        }

        var value = unchecked((long)hash);
        if (value >= 0)
        {
            value = -value - 1;
        }
        if (value == 0)
        {
            value = long.MinValue + 1;
        }
        return value;
    }

}
