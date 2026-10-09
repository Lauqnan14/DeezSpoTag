using DeezSpoTag.Integrations.Jellyfin;
using DeezSpoTag.Integrations.Navidrome;
using DeezSpoTag.Integrations.Plex;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using System.Net;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/autoplaylists")]
[Authorize]
public class AutoPlaylistsApiController : ControllerBase
{
    private const string PlexServer = MediaServerTargetServices.Plex;
    private const string JellyfinServer = MediaServerTargetServices.Jellyfin;
    private const string NavidromeServer = MediaServerTargetServices.Navidrome;

    /// <summary>Rendered order for the per-server sections.</summary>
    private static readonly string[] ServerOrder = [PlexServer, JellyfinServer, NavidromeServer];

    private readonly PlatformAuthService _authService;
    private readonly PlexApiClient _plexApiClient;
    private readonly JellyfinApiClient _jellyfinApiClient;
    private readonly NavidromeApiClient _navidromeApiClient;
    private readonly DeezSpoTag.Services.Library.LibraryRepository _libraryRepository;
    private readonly ILocalTrackAmbiguityResolver? _localIdentityResolver;
    private readonly PlaylistSyncService _playlistSyncService;
    private readonly IWatchlistTrackCandidateSource? _watchlistEngine;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AutoPlaylistsApiController>? _logger;

    public AutoPlaylistsApiController(
        PlatformAuthService authService,
        PlexApiClient plexApiClient,
        JellyfinApiClient jellyfinApiClient,
        NavidromeApiClient navidromeApiClient,
        DeezSpoTag.Services.Library.LibraryRepository libraryRepository,
        IHttpClientFactory httpClientFactory,
        PlaylistSyncService playlistSyncService,
        ILogger<AutoPlaylistsApiController>? logger = null,
        ILocalTrackAmbiguityResolver? localIdentityResolver = null,
        IWatchlistTrackCandidateSource? watchlistEngine = null)
    {
        _authService = authService;
        _plexApiClient = plexApiClient;
        _jellyfinApiClient = jellyfinApiClient;
        _navidromeApiClient = navidromeApiClient;
        _libraryRepository = libraryRepository;
        _localIdentityResolver = localIdentityResolver;
        _playlistSyncService = playlistSyncService;
        _watchlistEngine = watchlistEngine;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Returns the library playlists grouped per server. A server contributes a section only
    /// when it is configured and has at least one playlist left after monitored playlists are
    /// removed, so an unconnected or fully monitored server renders nothing at all.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetPlaylists([FromQuery] string? librarySectionId, CancellationToken cancellationToken)
    {
        var state = await _authService.LoadAsync();
        var monitored = await LoadMonitoredPlaylistKeysAsync(cancellationToken);
        var warnings = new List<string>();

        var plexSection = await BuildPlexSectionAsync(state, librarySectionId, monitored, cancellationToken, warnings);
        var jellyfinSection = await BuildJellyfinSectionAsync(state, librarySectionId, monitored, cancellationToken, warnings);
        var navidromeSection = await BuildNavidromeSectionAsync(state, librarySectionId, monitored, cancellationToken, warnings);

        var sections = new[] { plexSection, jellyfinSection, navidromeSection }
            .Where(static section => section is { Playlists.Count: > 0 })
            .Select(static section => section!)
            .ToArray();

        return Ok(new
        {
            sections,
            totalCount = sections.Sum(static section => section.Playlists.Count),
            warning = warnings.Count > 0 ? string.Join(" ", warnings) : null
        });
    }

    private async Task<HashSet<string>> LoadMonitoredPlaylistKeysAsync(CancellationToken cancellationToken)
    {
        var monitored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!_libraryRepository.IsConfigured)
        {
            return monitored;
        }

        var items = await _libraryRepository.GetPlaylistWatchlistAsync(cancellationToken);
        foreach (var key in items
            .Where(item => !string.IsNullOrWhiteSpace(item.Source) && !string.IsNullOrWhiteSpace(item.SourceId))
            .Select(item => BuildPlaylistKey(item.Source, item.SourceId)))
        {
            monitored.Add(key);
        }

        return monitored;
    }

    private static string BuildPlaylistKey(string source, string sourceId) => $"{source}:{sourceId}";

    private async Task<LibraryPlaylistSection?> BuildPlexSectionAsync(
        PlatformAuthState state,
        string? librarySectionId,
        HashSet<string> monitored,
        CancellationToken cancellationToken,
        List<string> warnings)
    {
        var plex = state.Plex;
        if (string.IsNullOrWhiteSpace(plex?.Url) || string.IsNullOrWhiteSpace(plex.Token))
        {
            return null;
        }

        try
        {
            var playlists = await _plexApiClient.GetPlaylistsAsync(plex.Url, plex.Token, cancellationToken);
            var items = playlists
                .Where(playlist => !string.IsNullOrWhiteSpace(playlist.Id) && !string.IsNullOrWhiteSpace(playlist.Title))
                .Where(playlist => string.IsNullOrWhiteSpace(librarySectionId)
                    || string.Equals(playlist.LibrarySectionId, librarySectionId, StringComparison.OrdinalIgnoreCase))
                .Where(playlist => !monitored.Contains(BuildPlaylistKey(PlexServer, playlist.Id!)))
                .Select(playlist => new LibraryPlaylistItem(
                    playlist.Id!,
                    PlexServer,
                    playlist.Title!,
                    playlist.Summary,
                    playlist.TrackCount,
                    FormatDuration(playlist.DurationMs),
                    playlist.UpdatedAt?.ToString("MMM d, yyyy"),
                    BuildPlexImageProxyUrl(playlist.CoverUrl, playlist.UpdatedAt),
                    playlist.LibrarySectionId))
                .OrderBy(playlist => playlist.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new LibraryPlaylistSection(PlexServer, "Plex", true, items, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger?.LogWarning(ex, "Plex library playlist fetch failed.");
            warnings.Add("Plex playlists could not be loaded.");
            return new LibraryPlaylistSection(PlexServer, "Plex", true, [], "Plex playlists could not be loaded.");
        }
    }

    private async Task<LibraryPlaylistSection?> BuildJellyfinSectionAsync(
        PlatformAuthState state,
        string? librarySectionId,
        HashSet<string> monitored,
        CancellationToken cancellationToken,
        List<string> warnings)
    {
        var jellyfin = state.Jellyfin;
        if (string.IsNullOrWhiteSpace(jellyfin?.Url) || string.IsNullOrWhiteSpace(jellyfin.ApiKey))
        {
            return null;
        }

        try
        {
            var playlists = await _jellyfinApiClient.GetPlaylistsAsync(
                jellyfin.Url,
                jellyfin.ApiKey,
                jellyfin.UserId ?? string.Empty,
                cancellationToken);
            var items = playlists
                .Where(playlist => !string.IsNullOrWhiteSpace(playlist.Id) && !string.IsNullOrWhiteSpace(playlist.Name))
                .Where(playlist => !monitored.Contains(BuildPlaylistKey(JellyfinServer, playlist.Id!)))
                .Select(playlist => new LibraryPlaylistItem(
                    playlist.Id!,
                    JellyfinServer,
                    playlist.Name!,
                    playlist.Overview,
                    null,
                    FormatJellyfinRuntime(playlist.RunTimeTicks),
                    null,
                    BuildJellyfinImageProxyUrl(playlist.Id!, playlist.ImageTags),
                    null))
                .OrderBy(playlist => playlist.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new LibraryPlaylistSection(JellyfinServer, "Jellyfin", true, items, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger?.LogWarning(ex, "Jellyfin library playlist fetch failed.");
            warnings.Add("Jellyfin playlists could not be loaded.");
            return new LibraryPlaylistSection(JellyfinServer, "Jellyfin", true, [], "Jellyfin playlists could not be loaded.");
        }
    }

    private async Task<LibraryPlaylistSection?> BuildNavidromeSectionAsync(
        PlatformAuthState state,
        string? librarySectionId,
        HashSet<string> monitored,
        CancellationToken cancellationToken,
        List<string> warnings)
    {
        var navidrome = state.Navidrome;
        if (string.IsNullOrWhiteSpace(navidrome?.Url)
            || string.IsNullOrWhiteSpace(navidrome.Username)
            || string.IsNullOrWhiteSpace(navidrome.Password))
        {
            return null;
        }

        try
        {
            var playlists = await _navidromeApiClient.GetPlaylistsAsync(
                navidrome.Url,
                navidrome.Username,
                navidrome.Password,
                cancellationToken);
            var items = playlists
                .Where(playlist => !string.IsNullOrWhiteSpace(playlist.Id) && !string.IsNullOrWhiteSpace(playlist.Name))
                .Where(playlist => !monitored.Contains(BuildPlaylistKey(NavidromeServer, playlist.Id)))
                .Select(playlist => new LibraryPlaylistItem(
                    playlist.Id,
                    NavidromeServer,
                    playlist.Name,
                    playlist.Comment,
                    playlist.TrackCount,
                    null,
                    null,
                    BuildNavidromeImageProxyUrl(playlist.CoverArt),
                    null))
                .OrderBy(playlist => playlist.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new LibraryPlaylistSection(NavidromeServer, "Navidrome", true, items, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger?.LogWarning(ex, "Navidrome library playlist fetch failed.");
            warnings.Add("Navidrome playlists could not be loaded.");
            return new LibraryPlaylistSection(NavidromeServer, "Navidrome", true, [], "Navidrome playlists could not be loaded.");
        }
    }

    private static string? FormatJellyfinRuntime(long? runTimeTicks)
    {
        if (runTimeTicks is null or <= 0)
        {
            return null;
        }

        // Jellyfin reports durations in 100-nanosecond ticks.
        return FormatDuration(runTimeTicks.Value / 10_000);
    }

    private sealed record LibraryPlaylistItem(
        string Id,
        string Server,
        string Name,
        string? Description,
        int? TrackCount,
        string? Duration,
        string? Updated,
        string? CoverUrl,
        string? LibrarySectionId);

    private sealed record LibraryPlaylistSection(
        string Server,
        string DisplayName,
        bool Configured,
        IReadOnlyList<LibraryPlaylistItem> Playlists,
        string? Warning);

    /// <summary>
    /// Playlist detail for the tracklist page, per server. Each server returns the same track
    /// shape so the page can render any of them through one code path, and each track carries
    /// the local library track id resolved from the index so playback reuses the indexed file.
    /// </summary>
    /// <summary>
    /// Copies one library playlist to other configured servers. The playlist does not have to
    /// be monitored: candidates are resolved live, which is the whole point of listing
    /// unmonitored playlists here.
    /// </summary>
    [HttpPost("{id}/sync")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncPlaylist(
        string id,
        [FromBody] LibraryPlaylistSyncRequest request,
        [FromQuery] string? server,
        CancellationToken cancellationToken)
    {
        if (request is null || request.Targets.Count == 0)
        {
            return BadRequest("Select at least one destination server.");
        }

        var normalizedServer = string.IsNullOrWhiteSpace(server) ? PlexServer : server.Trim().ToLowerInvariant();
        var source = await BuildSyncSourceAsync(normalizedServer, id, cancellationToken);
        if (source is null)
        {
            return NotFound($"The {normalizedServer} playlist could not be read.");
        }

        var candidates = _watchlistEngine is null
            ? Array.Empty<PlaylistTrackCandidate>()
            : await _watchlistEngine.GetPlaylistTrackCandidatesAsync(normalizedServer, id, cancellationToken);
        if (candidates.Count == 0)
        {
            return BadRequest("The playlist has no tracks to sync.");
        }

        var result = await _playlistSyncService.SyncSinglePlaylistAsync(
            new PlaylistSyncService.PlaylistSingleSyncRequest(
                source,
                SourcePreference: null,
                candidates,
                request.Targets,
                request.SyncMode,
                request.ExistingPlexPlaylistId,
                request.ExistingJellyfinPlaylistId,
                request.ExistingNavidromePlaylistId),
            cancellationToken);

        return result.Success || result.Targets.Count > 0
            ? Ok(result)
            : BadRequest(new { message = result.Message });
    }

    /// <summary>
    /// Re-reads the playlist from its own server so the sync copies the live name, description
    /// and track count rather than the lighter summary the section listing holds.
    /// </summary>
    private async Task<PlaylistWatchlistDto?> BuildSyncSourceAsync(string server, string id, CancellationToken cancellationToken)
    {
        var state = await _authService.LoadAsync();
        var now = DateTimeOffset.UtcNow;

        if (string.Equals(server, JellyfinServer, StringComparison.Ordinal))
        {
            var jellyfin = state.Jellyfin;
            if (string.IsNullOrWhiteSpace(jellyfin?.Url) || string.IsNullOrWhiteSpace(jellyfin.ApiKey))
            {
                return null;
            }

            var playlist = await _jellyfinApiClient.GetPlaylistAsync(
                jellyfin.Url,
                jellyfin.ApiKey,
                jellyfin.UserId ?? string.Empty,
                id,
                cancellationToken);
            if (playlist is null)
            {
                return null;
            }

            var items = await _jellyfinApiClient.GetPlaylistItemsAsync(
                jellyfin.Url,
                jellyfin.ApiKey,
                jellyfin.UserId ?? string.Empty,
                id,
                cancellationToken);
            return new PlaylistWatchlistDto(
                0,
                JellyfinServer,
                playlist.Id!,
                playlist.Name ?? "Jellyfin Playlist",
                BuildJellyfinImageProxyUrl(playlist.Id!, playlist.ImageTags),
                playlist.Overview,
                items.Count,
                now);
        }

        if (string.Equals(server, NavidromeServer, StringComparison.Ordinal))
        {
            var navidrome = state.Navidrome;
            if (string.IsNullOrWhiteSpace(navidrome?.Url)
                || string.IsNullOrWhiteSpace(navidrome.Username)
                || string.IsNullOrWhiteSpace(navidrome.Password))
            {
                return null;
            }

            var (playlist, tracks) = await _navidromeApiClient.GetPlaylistWithTracksAsync(
                navidrome.Url,
                navidrome.Username,
                navidrome.Password,
                id,
                cancellationToken);
            return playlist is null
                ? null
                : new PlaylistWatchlistDto(
                    0,
                    NavidromeServer,
                    playlist.Id,
                    playlist.Name,
                    null,
                    playlist.Comment,
                    tracks.Count,
                    now);
        }

        var plex = state.Plex;
        if (string.IsNullOrWhiteSpace(plex?.Url) || string.IsNullOrWhiteSpace(plex.Token))
        {
            return null;
        }

        var plexPlaylist = await _plexApiClient.GetPlaylistAsync(plex.Url, plex.Token, id, cancellationToken);
        if (plexPlaylist is null)
        {
            return null;
        }

        var plexItems = await _plexApiClient.GetPlaylistItemsDetailedAsync(plex.Url, plex.Token, plexPlaylist, cancellationToken);
        return new PlaylistWatchlistDto(
            0,
            PlexServer,
            plexPlaylist.Id,
            plexPlaylist.Title,
            BuildPlexImageProxyUrl(plexPlaylist.CoverUrl, plexPlaylist.UpdatedAt),
            plexPlaylist.Summary,
            plexItems.Tracks.Count,
            now);
    }

    public sealed record LibraryPlaylistSyncRequest(
        IReadOnlyCollection<string> Targets,
        string? SyncMode = null,
        string? ExistingPlexPlaylistId = null,
        string? ExistingJellyfinPlaylistId = null,
        string? ExistingNavidromePlaylistId = null,
        /// <summary>Cover art as a data URL. Square, up to 15MB, JPEG/PNG/WebP/GIF.</summary>
        string? ArtworkDataUrl = null,
        /// <summary>Description to send to each destination.</summary>
        string? Description = null);

    [HttpGet("{id}")]
    public async Task<IActionResult> GetPlaylist(
        string id,
        [FromQuery] string? server,
        CancellationToken cancellationToken)
    {
        var normalizedServer = string.IsNullOrWhiteSpace(server) ? PlexServer : server.Trim().ToLowerInvariant();
        return normalizedServer switch
        {
            JellyfinServer => await GetJellyfinPlaylistAsync(id, cancellationToken),
            NavidromeServer => await GetNavidromePlaylistAsync(id, cancellationToken),
            _ => await GetPlexPlaylistAsync(id, cancellationToken)
        };
    }

    private async Task<IActionResult> GetJellyfinPlaylistAsync(string id, CancellationToken cancellationToken)
    {
        var state = await _authService.LoadAsync();
        var jellyfin = state.Jellyfin;
        if (string.IsNullOrWhiteSpace(jellyfin?.Url) || string.IsNullOrWhiteSpace(jellyfin.ApiKey))
        {
            return Ok(new { playlist = default(object) });
        }

        var userId = jellyfin.UserId ?? string.Empty;
        var playlist = await _jellyfinApiClient.GetPlaylistAsync(jellyfin.Url, jellyfin.ApiKey, userId, id, cancellationToken);
        if (playlist is null)
        {
            return NotFound();
        }

        // The playlist items endpoint returns complete track metadata, so this stays a single
        // request rather than one per entry.
        var items = await _jellyfinApiClient.GetPlaylistItemsAsync(
            jellyfin.Url,
            jellyfin.ApiKey,
            userId,
            id,
            cancellationToken);
        var candidates = items
            .Where(static item => !string.IsNullOrWhiteSpace(item.Id))
            .Select(static item => new LibraryPlaylistTrackCandidate(
                item.Id!,
                item.Name,
                item.Artists is { Count: > 0 } ? string.Join(", ", item.Artists) : null,
                item.Album,
                item.RunTimeTicks.HasValue ? (int?)(item.RunTimeTicks.Value / 10_000) : null,
                item.ImageTags))
            .ToList();

        var localTrackIds = await ResolveLocalTrackIdsAsync(JellyfinServer, candidates, cancellationToken);
        var tracks = BuildLibraryPlaylistTracks(
            candidates, localTrackIds, await ResolveIsrcsAsync(localTrackIds, cancellationToken));

        return Ok(new
        {
            playlist = new
            {
                id = playlist.Id,
                name = playlist.Name ?? "Jellyfin Playlist",
                description = playlist.Overview,
                trackCount = tracks.Count,
                duration = FormatJellyfinRuntime(playlist.RunTimeTicks),
                updated = default(string?),
                coverUrl = BuildJellyfinImageProxyUrl(playlist.Id!, playlist.ImageTags),
                source = JellyfinServer,
                itemLoad = new { success = true },
                tracks
            }
        });
    }

    private async Task<IActionResult> GetNavidromePlaylistAsync(string id, CancellationToken cancellationToken)
    {
        var state = await _authService.LoadAsync();
        var navidrome = state.Navidrome;
        if (string.IsNullOrWhiteSpace(navidrome?.Url)
            || string.IsNullOrWhiteSpace(navidrome.Username)
            || string.IsNullOrWhiteSpace(navidrome.Password))
        {
            return Ok(new { playlist = default(object) });
        }

        var (playlist, tracks) = await _navidromeApiClient.GetPlaylistWithTracksAsync(
            navidrome.Url,
            navidrome.Username,
            navidrome.Password,
            id,
            cancellationToken);
        if (playlist is null)
        {
            return NotFound();
        }

        var candidates = tracks
            .Select(static track => new LibraryPlaylistTrackCandidate(
                track.Id,
                track.Title,
                track.Artist,
                null,
                track.DurationMs,
                null))
            .ToList();

        var localTrackIds = await ResolveLocalTrackIdsAsync(NavidromeServer, candidates, cancellationToken);

        return Ok(new
        {
            playlist = new
            {
                id = playlist.Id,
                name = playlist.Name,
                description = playlist.Comment,
                trackCount = playlist.TrackCount ?? candidates.Count,
                duration = default(string?),
                updated = default(string?),
                coverUrl = default(string?),
                source = NavidromeServer,
                tracks = BuildLibraryPlaylistTracks(
                    candidates,
                    localTrackIds,
                    await ResolveIsrcsAsync(localTrackIds, cancellationToken))
            }
        });
    }

    private sealed record LibraryPlaylistTrackCandidate(
        string ServerItemId,
        string? Title,
        string? Artist,
        string? Album,
        int? DurationMs,
        IReadOnlyDictionary<string, string>? ImageTags,
        string? CoverUrl = null);

    /// <summary>
    /// Maps each server track id onto the app's own library track so playback reuses the
    /// indexed file. This is the same batch resolution the monitored playlist sync uses: the
    /// repository matches on the stored source id first, then ISRC, then metadata, in a single
    /// pass over a temp table, so a 200 track playlist stays one query rather than 200.
    /// </summary>
    private async Task<IReadOnlyList<long?>> ResolveLocalTrackIdsAsync(
        string service,
        IReadOnlyList<LibraryPlaylistTrackCandidate> candidates,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0 || !_libraryRepository.IsConfigured)
        {
            return new long?[candidates.Count];
        }

        var inputs = candidates
            .Select(candidate => new DeezSpoTag.Services.Library.LibraryRepository.LibraryExistenceInput(
                Isrc: null,
                TrackTitle: candidate.Title,
                ArtistName: candidate.Artist,
                DurationMs: candidate.DurationMs,
                Source: service,
                SourceId: candidate.ServerItemId,
                AlbumTitle: candidate.Album,
                Explicit: null))
            .ToList();

        var identities = await _libraryRepository.ResolveLocalTrackIdentitiesAsync(
            inputs,
            cancellationToken,
            audioVariant: "stereo_preferred");
        if (_localIdentityResolver is null)
        {
            return identities.Select(ResolveLocalTrackId).ToList();
        }

        var resolved = new List<long?>(identities.Count);
        for (var index = 0; index < identities.Count; index++)
        {
            var decision = await _localIdentityResolver.ResolveAsync(inputs[index], identities[index], cancellationToken);
            resolved.Add(ResolveLocalTrackId(decision));
        }

        return resolved;
    }

    /// <summary>Ambiguous matches resolve to nothing, matching the monitored sync path.</summary>
    private static long? ResolveLocalTrackId(DeezSpoTag.Services.Library.LibraryRepository.LocalTrackIdentityResult result)
        => result.IsAmbiguous ? null : result.LocalTrackId;

    private List<LibraryPlaylistTrackPayload> BuildLibraryPlaylistTracks(
        IReadOnlyList<LibraryPlaylistTrackCandidate> candidates,
        IReadOnlyList<long?> localTrackIds,
        IReadOnlyDictionary<long, string>? isrcs = null)
    {
        var tracks = new List<LibraryPlaylistTrackPayload>(candidates.Count);
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var localTrackId = index < localTrackIds.Count ? localTrackIds[index] : null;
            tracks.Add(new LibraryPlaylistTrackPayload(
                candidate.ServerItemId,
                candidate.Title ?? "Unknown",
                candidate.Artist ?? "Unknown",
                candidate.Album ?? "Unknown",
                candidate.CoverUrl
                    ?? (candidate.ImageTags is null
                        ? null
                        : BuildJellyfinImageProxyUrl(candidate.ServerItemId, candidate.ImageTags)),
                candidate.DurationMs,
                // 0 means unresolved: the file is not in the library, so the row is listed
                // but falls back to the server stream.
                localTrackId.GetValueOrDefault(),
                null,
                null,
                null,
                // Null rather than empty when the track has no recorded ISRC, so the UI can tell
                // "this recording has no identity" from "we have not looked yet".
                localTrackId is > 0 && isrcs is not null && isrcs.TryGetValue(localTrackId.Value, out var isrc)
                    ? isrc
                    : null));
        }

        return tracks;
    }

    /// <summary>
    /// The recorded ISRCs for the local tracks a playlist resolved to, keyed by local track id.
    /// Empty when the library is not configured, so a caller can always pass the result through
    /// without a null check.
    /// </summary>
    private async Task<IReadOnlyDictionary<long, string>> ResolveIsrcsAsync(
        IReadOnlyList<long?> localTrackIds,
        CancellationToken cancellationToken)
    {
        var resolved = localTrackIds.Where(static id => id is > 0).Select(static id => id!.Value).ToList();
        return resolved.Count == 0
            ? new Dictionary<long, string>()
            : await _libraryRepository.GetIsrcsByLocalTrackIdsAsync(resolved, cancellationToken);
    }

    private sealed record LibraryPlaylistTrackPayload(
        string Id,
        string Title,
        string Artist,
        string Album,
        string? CoverUrl,
        int? DurationMs,
        long LocalTrackId,
        long? AudioFileId,
        string? VariantKey,
        string? StreamUrl,
        string? Isrc);

    private async Task<IActionResult> GetPlexPlaylistAsync(string id, CancellationToken cancellationToken)
    {
        var state = await _authService.LoadAsync();
        var plex = state.Plex;
        if (string.IsNullOrWhiteSpace(plex?.Url) || string.IsNullOrWhiteSpace(plex.Token))
        {
            return Ok(new { playlist = default(object) });
        }

        var playlist = await _plexApiClient.GetPlaylistAsync(plex.Url, plex.Token, id, cancellationToken);
        if (playlist is null)
        {
            return NotFound();
        }

        var items = await _plexApiClient.GetPlaylistItemsDetailedAsync(plex.Url, plex.Token, playlist, cancellationToken);
        var libraryInfo = await ResolveLibraryInfoAsync(items.Tracks, cancellationToken);

        // Same batch index resolution as Jellyfin and Navidrome, so every server's playlist
        // plays the indexed file. The Plex stream proxy is kept as the fallback for a track
        // the library has not indexed.
        var candidates = new List<LibraryPlaylistTrackCandidate>(items.Tracks.Count);
        foreach (var track in items.Tracks)
        {
            if (string.IsNullOrWhiteSpace(track.Id))
            {
                continue;
            }

            // Not static: the cover URL is built through the instance proxy helper.
            candidates.Add(new LibraryPlaylistTrackCandidate(
                track.Id!,
                track.Title,
                track.Artist,
                track.Album,
                (int?)track.DurationMs,
                null,
                BuildPlexImageProxyUrl(track.CoverUrl)));
        }
        var localTrackIds = await ResolveLocalTrackIdsAsync(PlexServer, candidates, cancellationToken);
        var isrcs = await ResolveIsrcsAsync(localTrackIds, cancellationToken);
        var resolvedTracks = BuildLibraryPlaylistTracks(candidates, localTrackIds, isrcs);

        return Ok(new
        {
            playlist = new
            {
                id = playlist.Id,
                key = playlist.Key,
                name = playlist.Title,
                description = playlist.Summary,
                trackCount = playlist.TrackCount,
                duration = FormatDuration(playlist.DurationMs),
                updated = playlist.UpdatedAt?.ToString("MMM d, yyyy"),
                smart = playlist.Smart,
                coverUrl = BuildPlexImageProxyUrl(playlist.CoverUrl, playlist.UpdatedAt),
                librarySectionId = playlist.LibrarySectionId,
                libraryId = libraryInfo?.Id,
                libraryName = libraryInfo?.Name,
                itemLoad = new
                {
                    success = items.Success,
                    statusCode = items.StatusCode,
                    endpoint = items.Endpoint,
                    error = items.Error
                },
                tracks = AttachPlexStreamFallbacks(resolvedTracks, items.Tracks)
            }
        });
    }

    /// <summary>
    /// Re-attaches the Plex per-item stream proxy to the resolved rows, keyed by the server
    /// item id, so an unindexed track still plays while an indexed one uses the library file.
    /// </summary>
    private List<LibraryPlaylistTrackPayload> AttachPlexStreamFallbacks(
        IReadOnlyList<object> resolvedTracks,
        IReadOnlyList<PlexPlaylistTrack> plexTracks)
    {
        var streamUrlsById = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in plexTracks.Where(track => !string.IsNullOrWhiteSpace(track.Id)))
        {
            streamUrlsById[track.Id] = BuildPlexStreamProxyUrl(track.StreamUrl);
        }

        return resolvedTracks
            .Cast<LibraryPlaylistTrackPayload>()
            .Select(track => streamUrlsById.TryGetValue(track.Id, out var streamUrl)
                ? track with { StreamUrl = streamUrl }
                : track)
            .ToList();
    }

    [HttpGet("{id}/diagnostics")]
    public async Task<IActionResult> GetPlaylistDiagnostics(string id, CancellationToken cancellationToken)
    {
        var state = await _authService.LoadAsync();
        var plex = state.Plex;
        if (string.IsNullOrWhiteSpace(plex?.Url) || string.IsNullOrWhiteSpace(plex.Token))
        {
            return Ok(new
            {
                configured = false,
                available = false,
                playlist = default(object),
                itemLoad = default(object)
            });
        }

        var playlist = await _plexApiClient.GetPlaylistAsync(plex.Url, plex.Token, id, cancellationToken);
        if (playlist is null)
        {
            return Ok(new
            {
                configured = true,
                available = false,
                playlist = default(object),
                itemLoad = default(object)
            });
        }

        var items = await _plexApiClient.GetPlaylistItemsDetailedAsync(plex.Url, plex.Token, playlist, cancellationToken);
        return Ok(new
        {
            configured = true,
            available = true,
            playlist = new
            {
                id = playlist.Id,
                key = playlist.Key,
                title = playlist.Title,
                type = playlist.PlaylistType,
                smart = playlist.Smart,
                trackCount = playlist.TrackCount,
                librarySectionId = playlist.LibrarySectionId
            },
            itemLoad = new
            {
                success = items.Success,
                statusCode = items.StatusCode,
                endpoint = items.Endpoint,
                error = items.Error,
                tracksReturned = items.Tracks.Count,
                sampleTracks = items.Tracks.Take(5).Select(static track => new
                {
                    id = track.Id,
                    title = track.Title,
                    artist = track.Artist,
                    album = track.Album,
                    hasFilePath = !string.IsNullOrWhiteSpace(track.FilePath)
                })
            }
        });
    }

    [HttpGet("image")]
    public async Task<IActionResult> GetPlexImage(
        [FromQuery] string? path,
        [FromQuery] string? server,
        [FromQuery] string? tag,
        CancellationToken cancellationToken)
    {
        var proxyContext = await ResolvePlaylistImageProxyContextAsync(server, path, tag);
        if (proxyContext.ErrorResult != null)
        {
            return proxyContext.ErrorResult;
        }

        var client = _httpClientFactory.CreateClient();
        try
        {
            using var response = await client.GetAsync(proxyContext.TargetUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning(
                    "Playlist image proxy returned {StatusCode} for path {Path}.",
                    (int)response.StatusCode,
                    DeezSpoTag.Core.Security.LogSanitizer.OneLine(path));
            }

            return await ImageProxyResponseHelper.CreateImageResultAsync(
                this,
                response,
                cache =>
                {
                    cache.NoStore = true;
                    cache.NoCache = true;
                    cache.MustRevalidate = true;
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger?.LogWarning(ex, "Plex playlist image proxy failed for path {Path}.", DeezSpoTag.Core.Security.LogSanitizer.OneLine(path));
            return StatusCode(StatusCodes.Status502BadGateway);
        }
    }

    [HttpGet("stream")]
    public async Task<IActionResult> GetPlexStream(
        [FromQuery] string? path,
        [FromQuery] string? server,
        [FromHeader(Name = "Range")] string? rangeHeader,
        CancellationToken cancellationToken)
    {
        var proxyContext = await ResolvePlaylistImageProxyContextAsync(server, path, tag: null);
        if (proxyContext.ErrorResult != null)
        {
            return proxyContext.ErrorResult;
        }

        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, proxyContext.TargetUrl);
        if (!string.IsNullOrWhiteSpace(rangeHeader))
        {
            request.Headers.TryAddWithoutValidation("Range", rangeHeader);
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            return StatusCode(StatusCodes.Status416RangeNotSatisfiable);
        }
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.PartialContent)
        {
            return StatusCode((int)response.StatusCode);
        }

        Response.StatusCode = (int)response.StatusCode;
        CopyHeaderIfPresent(response, "Accept-Ranges");
        CopyHeaderIfPresent(response, "Content-Range");
        CopyHeaderIfPresent(response, "Content-Length");
        CopyHeaderIfPresent(response, "Cache-Control");
        CopyHeaderIfPresent(response, "ETag");
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "audio/mpeg";
        Response.ContentType = contentType;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await stream.CopyToAsync(Response.Body, cancellationToken);
        return new EmptyResult();
    }

    private async Task<(string TargetUrl, IActionResult? ErrorResult)> ResolvePlaylistImageProxyContextAsync(
        string? server,
        string? path,
        string? tag)
    {
        var normalizedServer = (server ?? PlexServer).Trim().ToLowerInvariant();
        var state = await _authService.LoadAsync();

        if (string.Equals(normalizedServer, JellyfinServer, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return (string.Empty, BadRequest("Invalid path"));
            }

            var jellyfin = state.Jellyfin;
            if (string.IsNullOrWhiteSpace(jellyfin?.Url) || string.IsNullOrWhiteSpace(jellyfin.ApiKey))
            {
                return (string.Empty, NotFound());
            }

            return (BuildJellyfinImageUrl(jellyfin.Url, jellyfin.ApiKey, path, tag), null);
        }

        if (string.Equals(normalizedServer, NavidromeServer, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return (string.Empty, BadRequest("Invalid path"));
            }

            var navidrome = state.Navidrome;
            if (string.IsNullOrWhiteSpace(navidrome?.Url)
                || string.IsNullOrWhiteSpace(navidrome.Username)
                || string.IsNullOrWhiteSpace(navidrome.Password))
            {
                return (string.Empty, NotFound());
            }

            var targetUrl = _navidromeApiClient.BuildCoverArtRequestUrl(
                navidrome.Url,
                navidrome.Username,
                navidrome.Password,
                path);
            return string.IsNullOrWhiteSpace(targetUrl)
                ? (string.Empty, BadRequest("Invalid path"))
                : (targetUrl, null);
        }

        var normalizedPath = NormalizePlexImagePath(path);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return (string.Empty, BadRequest("Invalid path"));
        }

        var plex = state.Plex;
        if (string.IsNullOrWhiteSpace(plex?.Url) || string.IsNullOrWhiteSpace(plex.Token))
        {
            return (string.Empty, NotFound());
        }

        return (BuildPlexImageUrl(plex.Url, plex.Token, normalizedPath), null);
    }

    /// <summary>
    /// Keeps the Jellyfin API key on the server. The browser only ever sees this proxy URL,
    /// unlike the image URLs that embed <c>api_key</c> elsewhere in the app.
    /// </summary>
    private static string BuildJellyfinImageUrl(string serverUrl, string apiKey, string itemId, string? tag)
    {
        var query = $"maxHeight=420&quality=90&api_key={Uri.EscapeDataString(apiKey)}";
        if (!string.IsNullOrWhiteSpace(tag))
        {
            query += $"&tag={Uri.EscapeDataString(tag)}";
        }

        return $"{serverUrl.TrimEnd('/')}/Items/{Uri.EscapeDataString(itemId)}/Images/Primary?{query}";
    }

    private static string FormatDuration(long durationMs)
    {
        if (durationMs <= 0)
        {
            return "—";
        }

        var totalMinutes = (int)Math.Round(durationMs / 60000.0);
        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        return hours > 0 ? $"{hours} hr {minutes} min" : $"{minutes} min";
    }

    private async Task<DeezSpoTag.Services.Library.LibraryDto?> ResolveLibraryInfoAsync(
        List<PlexPlaylistTrack> tracks,
        CancellationToken cancellationToken)
    {
        if (!_libraryRepository.IsConfigured || tracks.Count == 0)
        {
            return null;
        }

        var firstPath = tracks.Select(t => t.FilePath).FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        if (string.IsNullOrWhiteSpace(firstPath))
        {
            return null;
        }

        var folder = await _libraryRepository.ResolveFolderForPathAsync(firstPath, cancellationToken);
        if (folder is null || folder.LibraryId is null)
        {
            return null;
        }

        return new DeezSpoTag.Services.Library.LibraryDto(folder.LibraryId.Value, folder.LibraryName ?? "Library");
    }

    private string? BuildPlexImageProxyUrl(string? sourceUrl, DateTimeOffset? version = null)
    {
        var path = ExtractPlexImagePath(sourceUrl);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var versionToken = version?.ToUnixTimeSeconds();
        return Url.ActionLink(nameof(GetPlexImage), values: new { server = PlexServer, path, v = versionToken });
    }

    private string? BuildJellyfinImageProxyUrl(string itemId, IReadOnlyDictionary<string, string>? imageTags)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return null;
        }

        var tag = imageTags is not null && imageTags.TryGetValue("Primary", out var primaryTag)
            ? primaryTag
            : null;
        return Url.ActionLink(nameof(GetPlexImage), values: new { server = JellyfinServer, path = itemId, tag });
    }

    private string? BuildNavidromeImageProxyUrl(string? coverArtId)
    {
        if (string.IsNullOrWhiteSpace(coverArtId))
        {
            return null;
        }

        return Url.ActionLink(nameof(GetPlexImage), values: new { server = NavidromeServer, path = coverArtId });
    }

    private string? BuildPlexStreamProxyUrl(string? sourceUrl)
    {
        var path = ExtractPlexImagePath(sourceUrl);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return Url.ActionLink(nameof(GetPlexStream), values: new { path });
    }

    private static string? ExtractPlexImagePath(string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return null;
        }

        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            return NormalizePlexImagePath(sourceUrl);
        }

        var query = ParseQueryString(uri.Query)
            .Where(static kvp => !string.Equals(kvp.Key, "X-Plex-Token", StringComparison.OrdinalIgnoreCase))
            .Select(static kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}");
        var queryString = string.Join("&", query);
        var path = uri.AbsolutePath;
        if (!string.IsNullOrWhiteSpace(queryString))
        {
            path = $"{path}?{queryString}";
        }

        return NormalizePlexImagePath(path);
    }

    private static string? NormalizePlexImagePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var value = path.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
        {
            value = absolute.PathAndQuery;
        }

        if (!value.StartsWith('/'))
        {
            value = "/" + value.TrimStart('/');
        }

        return value.Contains("..", StringComparison.Ordinal) ? null : value;
    }

    private static string BuildPlexImageUrl(string serverUrl, string token, string pathAndQuery)
    {
        var target = new Uri(new Uri(serverUrl.TrimEnd('/')), pathAndQuery);
        var builder = new UriBuilder(target);
        var query = ParseQueryString(builder.Query)
            .Where(static kvp => !string.Equals(kvp.Key, "X-Plex-Token", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(static kvp => kvp.Key, static kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
        query["X-Plex-Token"] = token;
        builder.Query = string.Join("&", query.Select(static kvp =>
            $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
        return builder.Uri.ToString();
    }

    private static IEnumerable<KeyValuePair<string, string>> ParseQueryString(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            yield break;
        }

        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = part.IndexOf('=');
            if (index < 0)
            {
                yield return new KeyValuePair<string, string>(Uri.UnescapeDataString(part), string.Empty);
                continue;
            }

            yield return new KeyValuePair<string, string>(
                Uri.UnescapeDataString(part[..index]),
                Uri.UnescapeDataString(part[(index + 1)..]));
        }
    }

    private void CopyHeaderIfPresent(HttpResponseMessage response, string headerName)
    {
        if (response.Headers.TryGetValues(headerName, out var values) ||
            response.Content.Headers.TryGetValues(headerName, out values))
        {
            Response.Headers[headerName] = values.ToArray();
        }
    }
}
