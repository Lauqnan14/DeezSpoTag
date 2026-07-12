using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using System;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Net.Http.Headers;
using System.Text.Json;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Queue;

namespace DeezSpoTag.Web.Controllers.Api;

public sealed class LibraryPlaylistWatchlistDependencies
{
    public required LibraryRepository Repository { get; init; }
    public required LibraryConfigStore ConfigStore { get; init; }
    public required PlaylistWatchService PlaylistWatchService { get; init; }
    public required PlaylistSyncService PlaylistSyncService { get; init; }
    public required PlaylistVisualService PlaylistVisualService { get; init; }
    public required DownloadQueueRepository QueueRepository { get; init; }
    public required AutoTagProfileResolutionService ProfileResolutionService { get; init; }
    public WatchlistFinalizationService? WatchlistFinalizationService { get; init; }
    public PlaylistWatchHostedService? PlaylistWatchHostedService { get; init; }
}

[Route("api/library/playlists")]
[ApiController]
[Authorize]
[Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryToken]
public class LibraryPlaylistWatchlistApiController : ControllerBase
{
    private const string GlobalRoutingTemplateSource = "global";
    private const string GlobalRoutingTemplateSourceId = "__playlist_routing_rules_template__";
    private const string PlaylistWatchType = "playlist";
    private const string PlaylistWatchlistEntryNotFoundMessage = "Playlist watchlist entry not found.";
    private const string ManualUnavailableImageUrl = "/images/unavailable/unavailable.jpg";
    private readonly LibraryRepository _repository;
    private readonly LibraryConfigStore _configStore;
    private readonly PlaylistWatchService _playlistWatchService;
    private readonly PlaylistSyncService _playlistSyncService;
    private readonly PlaylistVisualService _playlistVisualService;
    private readonly DownloadQueueRepository _queueRepository;
    private readonly AutoTagProfileResolutionService _profileResolutionService;
    private readonly WatchlistFinalizationService? _watchlistFinalizationService;
    private readonly PlaylistWatchHostedService? _playlistWatchHostedService;

    public LibraryPlaylistWatchlistApiController(LibraryPlaylistWatchlistDependencies dependencies)
    {
        _repository = dependencies.Repository;
        _configStore = dependencies.ConfigStore;
        _playlistWatchService = dependencies.PlaylistWatchService;
        _playlistSyncService = dependencies.PlaylistSyncService;
        _playlistVisualService = dependencies.PlaylistVisualService;
        _profileResolutionService = dependencies.ProfileResolutionService;
        _queueRepository = dependencies.QueueRepository;
        _watchlistFinalizationService = dependencies.WatchlistFinalizationService;
        _playlistWatchHostedService = dependencies.PlaylistWatchHostedService;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken, [FromQuery] bool refreshFromSource = false)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        if (refreshFromSource)
        {
            return BadRequest("Monitored playlist listing is cache-only. Use trigger-check to schedule a rate-limited refresh.");
        }

        var items = await _repository.GetPlaylistWatchlistAsync(cancellationToken);
        var hydrated = items.Select(HydratePlaylistVisual).ToList();
        return Ok(hydrated);
    }

    [HttpGet("watch-runtime")]
    public async Task<IActionResult> GetWatchRuntime(CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var scheduler = await _repository.GetWatchlistSchedulerStateAsync(PlaylistWatchType, cancellationToken);
        var playlists = await _repository.GetPlaylistWatchlistAsync(cancellationToken);
        var sources = playlists
            .Select(item => WatchlistPreferenceNormalizer.PlaylistSource(item.Source))
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(source => source, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var circuits = new List<object>(sources.Count);
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var circuit = await _repository.GetWatchlistSourceCircuitStateAsync(PlaylistWatchType, source, cancellationToken);
            if (circuit == null)
            {
                continue;
            }

            circuits.Add(new
            {
                source = circuit.Source,
                isOpen = circuit.IsOpen,
                openUntilUtc = circuit.OpenUntilUtc,
                reason = circuit.Reason,
                fingerprint = circuit.Fingerprint,
                failureCount = circuit.FailureCount
            });
        }

        return Ok(new
        {
            scheduler = scheduler == null
                ? null
                : new
                {
                    watchType = scheduler.WatchType,
                    activeSource = scheduler.ActiveSource,
                    activeSourceId = scheduler.ActiveSourceId,
                    activeStartedUtc = scheduler.ActiveStartedUtc,
                    lastProgressUtc = scheduler.LastProgressUtc,
                    zeroQueueStreak = scheduler.ZeroQueueStreak
                },
            circuits,
            utcNow = DateTimeOffset.UtcNow
        });
    }

    [HttpGet("manual-unavailable")]
    public async Task<IActionResult> GetManualUnavailable(CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var tracks = await _repository.GetManualUnavailableTracksAsync(cancellationToken);
        return Ok(new
        {
            imageUrl = ManualUnavailableImageUrl,
            count = tracks.Count,
            tracks
        });
    }

    [HttpGet("manual-unavailable/tracklist")]
    public async Task<IActionResult> GetManualUnavailableTracklist(CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var tracks = await _repository.GetManualUnavailableTracksAsync(cancellationToken);
        return Ok(new
        {
            title = "Unavailable Tracks",
            name = "Unavailable Tracks",
            description = "Manual downloads that were unavailable from enabled sources.",
            creator = new { name = "DeezSpoTag" },
            cover_big = ManualUnavailableImageUrl,
            cover_xl = ManualUnavailableImageUrl,
            picture = ManualUnavailableImageUrl,
            nb_tracks = tracks.Count,
            tracks = tracks.Select(MapManualUnavailableTrack)
        });
    }

    [HttpDelete("manual-unavailable/{id:long}")]
    public async Task<IActionResult> DeleteManualUnavailable(long id, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var deleted = await _repository.DeleteManualUnavailableTrackAsync(id, cancellationToken);
        return deleted
            ? Ok(new { success = true, deleted = true })
            : NotFound("Unavailable track record not found.");
    }

    private static object MapManualUnavailableTrack(ManualUnavailableTrackDto track, int index)
    {
        var coverUrl = ManualUnavailableImageUrl;
        return new
        {
            id = track.DeezerId ?? track.QueueUuid,
            manualUnavailableId = track.Id,
            queueUuid = track.QueueUuid,
            title = track.Title,
            artist = new { name = track.Artist },
            artistName = track.Artist,
            album = new
            {
                title = string.IsNullOrWhiteSpace(track.Album) ? "Unknown" : track.Album,
                cover_medium = coverUrl,
                cover_big = coverUrl
            },
            albumArtist = track.AlbumArtist,
            duration = 0,
            durationMs = 0,
            preview = string.Empty,
            link = track.SourceUrl ?? string.Empty,
            sourceUrl = track.SourceUrl ?? string.Empty,
            sourceTrackId = track.QueueUuid,
            track_position = index + 1,
            deezerId = track.DeezerId,
            spotifyId = track.SpotifyId,
            appleId = track.AppleId,
            qobuzId = track.QobuzId,
            tidalId = track.TidalId,
            amazonId = track.AmazonId,
            isrc = track.Isrc,
            quality = track.Quality,
            contentType = string.IsNullOrWhiteSpace(track.ContentType) ? "music" : track.ContentType,
            destinationFolderId = track.DestinationFolderId,
            expectedFinalPath = track.ExpectedFinalPath,
            reason = track.Reason,
            firstUnavailableAtUtc = track.FirstUnavailableAtUtc,
            addedAtUtc = track.AddedAtUtc,
            nextRetryAtUtc = track.NextRetryAtUtc,
            retryInUtc = track.NextRetryAtUtc
        };
    }

    private PlaylistWatchlistDto HydratePlaylistVisual(PlaylistWatchlistDto item)
    {
        var visual = _playlistVisualService.GetStoredVisual(item.Source, item.SourceId);
        if (visual is null || string.IsNullOrWhiteSpace(visual.Url))
        {
            return IsLocalPlaylistVisualUrl(item.ImageUrl)
                ? item with { ImageUrl = null }
                : item;
        }

        return item with { ImageUrl = visual.Url };
    }

    private static bool IsLocalPlaylistVisualUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("/api/library/playlists/", StringComparison.OrdinalIgnoreCase)
            && value.Contains("/visual", StringComparison.OrdinalIgnoreCase);
    }

    [HttpGet("{source}/{sourceId}")]
    public async Task<IActionResult> GetStatus(string source, string sourceId, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var normalizedSource = WatchlistPreferenceNormalizer.PlaylistSource(source);
        var watching = await _repository.IsPlaylistWatchlistedAsync(normalizedSource, sourceId, cancellationToken);
        return Ok(new { watching });
    }

    public sealed record PlaylistWatchlistRequest(string Source, string SourceId, string Name, string? ImageUrl, string? Description, int? TrackCount);
    public sealed record PlaylistWatchlistPriorityRequest(string Source, string SourceId);

    [HttpPost]
    public async Task<IActionResult> Add([FromBody] PlaylistWatchlistRequest request, CancellationToken cancellationToken)
    {
        if (request is null
            || string.IsNullOrWhiteSpace(request.Source)
            || string.IsNullOrWhiteSpace(request.SourceId)
            || string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest("Playlist source, id, and name are required.");
        }

        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var normalizedSource = WatchlistPreferenceNormalizer.PlaylistSource(request.Source);
        var added = await _repository.AddPlaylistWatchlistAsync(
            normalizedSource,
            request.SourceId,
            new PlaylistWatchlistMetadataInput(
                request.Name,
                request.ImageUrl,
                request.Description,
                request.TrackCount),
            cancellationToken);

        if (added is null)
        {
            return StatusCode(500, "Failed to add playlist watchlist entry.");
        }

        await ApplyGlobalRoutingTemplateToPlaylistAsync(
            normalizedSource,
            request.SourceId,
            cancellationToken);

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"Playlist watchlist added: {request.Name}."));

        if (_playlistWatchHostedService != null)
        {
            await SetPlaylistWatchSchedulerFocusAsync(added.Source, added.SourceId, cancellationToken);
            _ = _playlistWatchHostedService.TriggerRunOnceAsync(CancellationToken.None);
        }

        return Ok(added);
    }

    [HttpPost("priority-order")]
    public async Task<IActionResult> UpdatePriorityOrder([FromBody] IReadOnlyList<PlaylistWatchlistPriorityRequest>? request, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        if (request == null || request.Count == 0)
        {
            return BadRequest("Playlist priority order is required.");
        }

        var watchlist = await _repository.GetPlaylistWatchlistAsync(cancellationToken);
        var existingKeys = watchlist
            .Select(item => $"{WatchlistPreferenceNormalizer.PlaylistSource(item.Source)}:{item.SourceId}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (request.Count != existingKeys.Count)
        {
            return BadRequest("Playlist priority order must include every monitored playlist.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var priorities = new List<(string Source, string SourceId, int SyncPriority)>(request.Count);
        for (var index = 0; index < request.Count; index++)
        {
            var item = request[index];
            if (item == null
                || string.IsNullOrWhiteSpace(item.Source)
                || string.IsNullOrWhiteSpace(item.SourceId))
            {
                return BadRequest("Playlist source and id are required.");
            }

            var normalizedSource = WatchlistPreferenceNormalizer.PlaylistSource(item.Source);
            var key = $"{normalizedSource}:{item.SourceId}";
            if (!existingKeys.Contains(key))
            {
                return BadRequest("Playlist priority order contains a playlist that is not monitored.");
            }

            if (!seen.Add(key))
            {
                return BadRequest("Playlist priority order contains duplicate playlists.");
            }

            priorities.Add((normalizedSource, item.SourceId, index + 1));
        }

        await _repository.UpdatePlaylistWatchlistPrioritiesAsync(priorities, cancellationToken);
        var updated = await _repository.GetPlaylistWatchlistAsync(cancellationToken);
        _playlistWatchHostedService?.ResetPlaylistRuntimeStateForAll(updated);
        var first = priorities[0];
        await SetPlaylistWatchSchedulerFocusAsync(first.Source, first.SourceId, cancellationToken);
        _ = _playlistWatchHostedService?.TriggerRunOnceAsync(CancellationToken.None);
        return Ok(new
        {
            updated = priorities.Count
        });
    }

    public sealed record PlaylistWatchPreferenceRequest(
        string Source,
        string SourceId,
        long? FolderId,
        long? AtmosFolderId,
        string? Service,
        List<string>? SyncTargets,
        string? PreferredEngine,
        DownloadEngineOrderSettings? DownloadEngineOrder,
        string? DownloadVariantMode,
        string? SyncMode,
        bool? UpdateArtwork,
        bool? ReuseSavedArtwork,
        List<PlaylistTrackRoutingRule>? RoutingRules = null,
        List<PlaylistTrackBlockRule>? BlockRules = null);

    [HttpGet("preferences")]
    public async Task<IActionResult> GetPreferences(CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var items = await _repository.GetPlaylistWatchPreferencesAsync(cancellationToken);
        return Ok(items);
    }

    [HttpGet("preferences/{source}/{sourceId}")]
    public async Task<IActionResult> GetPreference(string source, string sourceId, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var normalizedSource = WatchlistPreferenceNormalizer.PlaylistSource(source);
        var item = await _repository.GetPlaylistWatchPreferenceAsync(normalizedSource, sourceId, cancellationToken);
        return item is null ? Ok(new { }) : Ok(item);
    }

    [HttpPost("preferences")]
    public async Task<IActionResult> SavePreferences([FromBody] List<PlaylistWatchPreferenceRequest> requests, CancellationToken cancellationToken)
    {
        if (requests is null || requests.Count == 0)
        {
            return BadRequest("No playlist preferences provided.");
        }

        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var validFolderIds = await GetValidFolderIdsAsync(cancellationToken);
        var results = new List<object>(requests.Count);
        foreach (var request in requests)
        {
            var validationError = ValidatePlaylistPreferenceRequest(request, validFolderIds);
            if (validationError != null)
            {
                return BadRequest(validationError);
            }

            var saved = await SaveSinglePreferenceAsync(request, cancellationToken);
            if (saved is null)
            {
                continue;
            }

            results.Add(saved);
        }

        return Ok(results);
    }

    private async Task<object?> SaveSinglePreferenceAsync(
        PlaylistWatchPreferenceRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Source) || string.IsNullOrWhiteSpace(request.SourceId))
        {
            return null;
        }

        var normalizedSource = WatchlistPreferenceNormalizer.PlaylistSource(request.Source);
        var existing = await _repository.GetPlaylistWatchPreferenceAsync(normalizedSource, request.SourceId, cancellationToken);
        var normalizedArtwork = NormalizeArtworkPreference(
            request.ReuseSavedArtwork ?? existing?.ReuseSavedArtwork ?? false);
        var routingRules = request.RoutingRules == null
            ? existing?.RoutingRules
            : WatchlistPreferenceNormalizer.RoutingRules(request.RoutingRules);
        var blockRules = request.BlockRules == null
            ? existing?.IgnoreRules
            : WatchlistPreferenceNormalizer.BlockRules(request.BlockRules);
        var preferredEngine = WatchlistPreferenceNormalizer.PreferredEngine(request.PreferredEngine);
        var downloadEngineOrder = string.Equals(preferredEngine, DownloadSourceCatalog.Custom, StringComparison.Ordinal)
            ? NormalizePlaylistDownloadEngineOrder(request.DownloadEngineOrder ?? existing?.DownloadEngineOrder)
            : null;
        var syncTargets = NormalizePlaylistSyncTargets(request.SyncTargets, request.Service, existing);
        var legacyService = syncTargets.Count > 0
            ? syncTargets[0]
            : "none";

        return await _repository.UpsertPlaylistWatchPreferenceAsync(
            new LibraryRepository.PlaylistWatchPreferenceUpsertInput(
                normalizedSource,
                request.SourceId,
                request.FolderId,
                legacyService,
                syncTargets,
                preferredEngine,
                downloadEngineOrder,
                string.IsNullOrWhiteSpace(request.DownloadVariantMode)
                    ? existing?.DownloadVariantMode
                    : WatchlistPreferenceNormalizer.DownloadVariantMode(request.DownloadVariantMode),
                string.IsNullOrWhiteSpace(request.SyncMode)
                    ? existing?.SyncMode
                    : WatchlistPreferenceNormalizer.SyncMode(request.SyncMode),
                normalizedArtwork.UpdateArtwork,
                normalizedArtwork.ReuseSavedArtwork,
                routingRules,
                blockRules,
                request.AtmosFolderId),
            cancellationToken);
    }

    [HttpDelete("{source}/{sourceId}")]
    public async Task<IActionResult> Remove(string source, string sourceId, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var normalizedSource = WatchlistPreferenceNormalizer.PlaylistSource(source);
        var removed = await _repository.RemovePlaylistWatchlistAsync(normalizedSource, sourceId, cancellationToken);
        return Ok(new { removed });
    }

    [HttpPost("trigger-check")]
    public async Task<IActionResult> TriggerAll(CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var items = await _repository.GetPlaylistWatchlistAsync(cancellationToken);
        var queued = false;
        if (_playlistWatchHostedService != null)
        {
            _ = _playlistWatchHostedService.TriggerRunOnceAsync(CancellationToken.None);
            queued = true;
        }

        return Ok(new { queued, pending = items.Count });
    }

    [HttpPost("trigger-check/{source}/{sourceId}")]
    public async Task<IActionResult> TriggerOne(string source, string sourceId, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var item = await FindWatchlistItemAsync(source, sourceId, cancellationToken);
        if (item == null)
        {
            return NotFound(PlaylistWatchlistEntryNotFoundMessage);
        }

        await SetPlaylistWatchSchedulerFocusAsync(item.Source, item.SourceId, cancellationToken);
        var triggered = _playlistWatchHostedService != null;
        if (_playlistWatchHostedService != null)
        {
            _ = _playlistWatchHostedService.TriggerRunOnceAsync(CancellationToken.None);
        }

        return Ok(new { triggered = triggered ? 1 : 0 });
    }

    [HttpPost("reset-runtime")]
    public async Task<IActionResult> ResetRuntime(CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var watchlist = await _repository.GetPlaylistWatchlistAsync(cancellationToken);
        var playlistsReset = 0;
        var circuitsReset = 0;

        foreach (var item in watchlist)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ResetPlaylistPersistentStateAsync(item.Source, item.SourceId, cancellationToken);
            playlistsReset++;
        }

        var distinctSources = watchlist
            .Select(item => WatchlistPreferenceNormalizer.PlaylistSource(item.Source))
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var source in distinctSources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ResetSourceCircuitAsync(source, cancellationToken);
            circuitsReset++;
        }

        await _repository.UpsertWatchlistSchedulerStateAsync(
            new LibraryRepository.WatchlistSchedulerStateUpsertInput(
                WatchType: PlaylistWatchType,
                ActiveSource: null,
                ActiveSourceId: null,
                ActiveStartedUtc: null,
                LastProgressUtc: DateTimeOffset.UtcNow,
                ZeroQueueStreak: 0),
            cancellationToken);

        _playlistWatchHostedService?.ResetPlaylistRuntimeStateForAll(watchlist);
        if (_playlistWatchHostedService != null)
        {
            _ = _playlistWatchHostedService.TriggerRunOnceAsync(CancellationToken.None);
        }

        return Ok(new
        {
            reset = true,
            playlistsReset,
            circuitsReset,
            triggered = _playlistWatchHostedService != null
        });
    }

    [HttpPost("{source}/{sourceId}/reset-runtime")]
    public async Task<IActionResult> ResetPlaylistRuntime(string source, string sourceId, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var item = await FindWatchlistItemAsync(source, sourceId, cancellationToken);
        if (item == null)
        {
            return NotFound(PlaylistWatchlistEntryNotFoundMessage);
        }

        await ResetPlaylistPersistentStateAsync(item.Source, item.SourceId, cancellationToken);
        await ResetSourceCircuitAsync(item.Source, cancellationToken);
        _playlistWatchHostedService?.ResetPlaylistRuntimeState(item.Source, item.SourceId);

        if (_playlistWatchHostedService != null)
        {
            _ = _playlistWatchHostedService.TriggerRunOnceAsync(CancellationToken.None);
        }

        return Ok(new
        {
            reset = true,
            source = item.Source,
            sourceId = item.SourceId,
            triggered = _playlistWatchHostedService != null
        });
    }

    [HttpPost("{source}/{sourceId}/reset-and-skip")]
    public async Task<IActionResult> ResetPlaylistAndSkip(string source, string sourceId, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var item = await FindWatchlistItemAsync(source, sourceId, cancellationToken);
        if (item == null)
        {
            return NotFound(PlaylistWatchlistEntryNotFoundMessage);
        }

        var watchlist = await _repository.GetPlaylistWatchlistAsync(cancellationToken);
        if (watchlist.Count == 0)
        {
            return Ok(new { reset = true, skipped = false, reason = "No monitored playlists." });
        }

        await ResetPlaylistPersistentStateAsync(item.Source, item.SourceId, cancellationToken);
        await ResetSourceCircuitAsync(item.Source, cancellationToken);
        _playlistWatchHostedService?.ResetPlaylistRuntimeState(item.Source, item.SourceId);

        var normalizedSource = WatchlistPreferenceNormalizer.PlaylistSource(item.Source);
        var currentIndex = -1;
        for (var index = 0; index < watchlist.Count; index++)
        {
            var entry = watchlist[index];
            if (string.Equals(entry.Source, normalizedSource, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.SourceId, item.SourceId, StringComparison.OrdinalIgnoreCase))
            {
                currentIndex = index;
                break;
            }
        }

        PlaylistWatchlistDto? next = null;
        if (currentIndex >= 0 && watchlist.Count > 1)
        {
            next = watchlist[(currentIndex + 1) % watchlist.Count];
            if (string.Equals(next.Source, normalizedSource, StringComparison.OrdinalIgnoreCase)
                && string.Equals(next.SourceId, item.SourceId, StringComparison.OrdinalIgnoreCase))
            {
                next = null;
            }
        }

        if (next != null)
        {
            await SetPlaylistWatchSchedulerFocusAsync(next.Source, next.SourceId, cancellationToken);
        }
        else
        {
            await _repository.UpsertWatchlistSchedulerStateAsync(
                new LibraryRepository.WatchlistSchedulerStateUpsertInput(
                    WatchType: PlaylistWatchType,
                    ActiveSource: null,
                    ActiveSourceId: null,
                    ActiveStartedUtc: null,
                    LastProgressUtc: DateTimeOffset.UtcNow,
                    ZeroQueueStreak: 0),
                cancellationToken);
        }

        if (_playlistWatchHostedService != null)
        {
            _ = _playlistWatchHostedService.TriggerRunOnceAsync(CancellationToken.None);
        }

        return Ok(new
        {
            reset = true,
            skipped = next != null,
            nextSource = next?.Source,
            nextSourceId = next?.SourceId,
            triggered = _playlistWatchHostedService != null
        });
    }

    [HttpPost("{source}/{sourceId}/sync")]
    public async Task<IActionResult> Sync(string source, string sourceId, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var item = await FindWatchlistItemAsync(source, sourceId, cancellationToken);
        if (item == null)
        {
            return NotFound(PlaylistWatchlistEntryNotFoundMessage);
        }

        await SetPlaylistWatchSchedulerFocusAsync(item.Source, item.SourceId, cancellationToken);
        var repairNotifications = _watchlistFinalizationService == null
            ? 0
            : await _watchlistFinalizationService.RepairPlaylistAsync(
                item,
                cancellationToken);
        var reconciliation = await _playlistWatchService.ReconcilePlaylistAsync(
            item,
            CancellationToken.None,
            forceMediaServerSync: true);
        var result = reconciliation.SyncResult;
        return Ok(new
        {
            RepairNotifications = repairNotifications,
            reconciliation.Success,
            reconciliation.Message,
            reconciliation.SourceTracks,
            reconciliation.IgnoredTracks,
            reconciliation.LocalTracks,
            reconciliation.QueuedTracks,
            reconciliation.CompletedTracks,
            reconciliation.FailedTracks,
            PlaylistId = result?.PlaylistId,
            SyncedTracks = result?.SyncedTracks ?? 0,
            LocalMatches = result?.LocalMatches ?? 0,
            TargetMatches = result?.TargetMatches ?? 0,
            MissingTracks = result?.MissingTracks ?? 0,
            MetadataMatches = result?.MetadataMatches ?? 0,
            SearchMatches = result?.SearchMatches ?? 0,
            SyncMessage = result?.Message
        });
    }

    public sealed record PlaylistMergeSourceRequest(string Source, string SourceId);

    public sealed record PlaylistMergeRequest(
        List<PlaylistMergeSourceRequest> Playlists,
        string? Name,
        string? Description,
        string? ArtworkDataUrl,
        string? ArtworkSource,
        string? ArtworkSourceId,
        string? SyncMode,
        bool? SyncToPlex,
        bool? SyncToJellyfin,
        bool? SyncToNavidrome,
        string? ExistingPlexPlaylistId,
        string? ExistingJellyfinPlaylistId,
        string? ExistingNavidromePlaylistId);

    [HttpGet("merge-target-playlists")]
    public async Task<IActionResult> GetMergeTargetPlaylists([FromQuery] string? target, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var normalizedTarget = string.IsNullOrWhiteSpace(target)
            ? string.Empty
            : target.Trim().ToLowerInvariant();
        if (!string.Equals(normalizedTarget, "plex", StringComparison.Ordinal)
            && !string.Equals(normalizedTarget, "jellyfin", StringComparison.Ordinal)
            && !string.Equals(normalizedTarget, "navidrome", StringComparison.Ordinal))
        {
            return BadRequest("target must be 'plex', 'jellyfin', or 'navidrome'.");
        }

        var playlists = await _playlistSyncService.GetTargetPlaylistsAsync(normalizedTarget, cancellationToken);
        return Ok(playlists);
    }

    [HttpPost("merge-sync")]
    public async Task<IActionResult> MergeSync([FromBody] PlaylistMergeRequest request, CancellationToken cancellationToken)
    {
        if (request is null || request.Playlists is null || request.Playlists.Count < 2)
        {
            return BadRequest("Select at least two monitored playlists to merge.");
        }

        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var normalizedSelections = request.Playlists
            .Where(selection => selection is not null)
            .Select(selection => selection!)
            .Where(selection => !string.IsNullOrWhiteSpace(selection.Source)
                && !string.IsNullOrWhiteSpace(selection.SourceId))
            .Select(selection => new
            {
                Source = WatchlistPreferenceNormalizer.PlaylistSource(selection.Source),
                SourceId = selection.SourceId.Trim()
            })
            .Distinct()
            .ToList();
        if (normalizedSelections.Count < 2)
        {
            return BadRequest("Select at least two valid monitored playlists to merge.");
        }

        var syncToPlex = request.SyncToPlex == true;
        var syncToJellyfin = request.SyncToJellyfin == true;
        var syncToNavidrome = request.SyncToNavidrome == true;
        if (!syncToPlex && !syncToJellyfin && !syncToNavidrome)
        {
            return BadRequest("Select Plex, Jellyfin, or Navidrome as a merge target.");
        }

        var allItems = await _repository.GetPlaylistWatchlistAsync(cancellationToken);
        var selectedSources = new List<PlaylistSyncService.PlaylistMergeSourceInput>(normalizedSelections.Count);
        var missingSelections = new List<string>();

        foreach (var selection in normalizedSelections)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = allItems.FirstOrDefault(entry =>
                string.Equals(entry.Source, selection.Source, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.SourceId, selection.SourceId, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                missingSelections.Add($"{selection.Source}:{selection.SourceId}");
                continue;
            }

            var preference = await _repository.GetPlaylistWatchPreferenceAsync(
                item.Source,
                item.SourceId,
                cancellationToken);
            var candidates = await _playlistWatchService.GetPlaylistTrackCandidatesAsync(
                item.Source,
                item.SourceId,
                cancellationToken);
            selectedSources.Add(new PlaylistSyncService.PlaylistMergeSourceInput(item, preference, candidates));
        }

        if (missingSelections.Count > 0)
        {
            return NotFound(new
            {
                message = "One or more selected playlists are no longer monitored.",
                missing = missingSelections
            });
        }

        var sourceUserName = User?.Identity?.Name?.Trim();
        var result = await _playlistSyncService.MergeAndSyncPlaylistsAsync(
            selectedSources,
            new PlaylistSyncService.PlaylistMergeSyncRequest(
                request.Name,
                request.Description,
                request.ArtworkDataUrl,
                request.ArtworkSource,
                request.ArtworkSourceId,
                sourceUserName,
                request.SyncMode,
                syncToPlex,
                syncToJellyfin,
                syncToNavidrome,
                request.ExistingPlexPlaylistId,
                request.ExistingJellyfinPlaylistId,
                request.ExistingNavidromePlaylistId),
            cancellationToken);

        if (!result.Success)
        {
            return BadRequest(result);
        }

        return Ok(result);
    }

    [HttpPost("{source}/{sourceId}/refresh-artwork")]
    public async Task<IActionResult> RefreshArtwork(string source, string sourceId, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var item = await FindWatchlistItemAsync(source, sourceId, cancellationToken);
        if (item == null)
        {
            return NotFound(PlaylistWatchlistEntryNotFoundMessage);
        }

        var refreshedItem = await _playlistWatchService.RefreshPlaylistMetadataOnlyAsync(
            item,
            cancellationToken,
            forceArtworkRefresh: true);
        var artworkSync = await SyncArtworkForWatchlistItemAsync(refreshedItem, cancellationToken);
        return Ok(new { refreshed = true, artworkSync });
    }

    [HttpGet("{source}/{sourceId}/visual")]
    public IActionResult GetVisual(string source, string sourceId, [FromQuery] string? file = null)
    {
        var normalizedSource = WatchlistPreferenceNormalizer.PlaylistSource(source);
        var visual = string.IsNullOrWhiteSpace(file)
            ? _playlistVisualService.GetStoredVisual(normalizedSource, sourceId)
            : _playlistVisualService.GetStoredVisuals(normalizedSource, sourceId)
                .FirstOrDefault(item => string.Equals(Path.GetFileName(item.FilePath), file, StringComparison.OrdinalIgnoreCase));
        if (visual == null || !System.IO.File.Exists(visual.FilePath))
        {
            return NotFound();
        }

        Response.GetTypedHeaders().CacheControl = new CacheControlHeaderValue
        {
            Public = true,
            MaxAge = TimeSpan.FromHours(1)
        };
        return PhysicalFile(visual.FilePath, visual.ContentType);
    }

    [HttpGet("{source}/{sourceId}/visuals")]
    public IActionResult GetVisuals(string source, string sourceId)
    {
        var normalizedSource = WatchlistPreferenceNormalizer.PlaylistSource(source);
        var items = _playlistVisualService.GetStoredVisuals(normalizedSource, sourceId)
            .Select(item => new
            {
                fileName = Path.GetFileName(item.FilePath),
                url = item.Url,
                isActive = item.IsActive
            })
            .ToList();
        return Ok(items);
    }

    public sealed record PlaylistVisualSelectRequest(string FileName);

    [HttpPost("{source}/{sourceId}/visuals/select")]
    public async Task<IActionResult> SelectVisual(string source, string sourceId, [FromBody] PlaylistVisualSelectRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.FileName))
        {
            return BadRequest("FileName is required.");
        }

        var normalizedSource = WatchlistPreferenceNormalizer.PlaylistSource(source);
        var updated = _playlistVisualService.SetActiveVisual(normalizedSource, sourceId, request.FileName);
        if (!updated)
        {
            return NotFound("Playlist visual not found.");
        }

        var activeVisual = _playlistVisualService.GetStoredVisual(normalizedSource, sourceId);
        if (activeVisual != null)
        {
            await _repository.UpdatePlaylistWatchlistMetadataAsync(
                normalizedSource,
                sourceId,
                new PlaylistWatchlistMetadataInput(
                    null,
                    activeVisual.Url,
                    null,
                    null),
                cancellationToken);
        }

        var item = await FindWatchlistItemAsync(normalizedSource, sourceId, cancellationToken);
        var artworkSync = item != null && activeVisual != null
            ? await SyncArtworkForWatchlistItemAsync(item with { ImageUrl = activeVisual.Url }, cancellationToken)
            : null;

        return Ok(new { updated = true, imageUrl = activeVisual?.Url, artworkSync });
    }

    [HttpGet("{source}/{sourceId}/routing-rules")]
    public async Task<IActionResult> GetRoutingRules(string source, string sourceId, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var normalizedSource = WatchlistPreferenceNormalizer.PlaylistSource(source);
        var pref = await _repository.GetPlaylistWatchPreferenceAsync(normalizedSource, sourceId, cancellationToken);
        return Ok(pref?.RoutingRules ?? Array.Empty<PlaylistTrackRoutingRule>());
    }

    [HttpPost("{source}/{sourceId}/routing-rules")]
    public async Task<IActionResult> SaveRoutingRules(string source, string sourceId, [FromBody] List<PlaylistTrackRoutingRule> rules, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var normalizedRules = WatchlistPreferenceNormalizer.RoutingRules(rules);
        var validFolderIds = await GetValidFolderIdsAsync(cancellationToken);
        if (normalizedRules?.Any(rule => !validFolderIds.Contains(rule.DestinationFolderId)) == true)
        {
            return BadRequest("Routing destination folder was not found or is disabled.");
        }

        await UpsertWatchPreferenceRulesAsync(
            source,
            sourceId,
            normalizedRules,
            ignoreRules: null,
            cancellationToken,
            replaceRoutingRules: true);

        return Ok(new { saved = normalizedRules?.Count ?? 0 });
    }

    [HttpPost("{source}/{sourceId}/routing-rules/apply-globally")]
    public async Task<IActionResult> ApplyRoutingRulesGlobally(string source, string sourceId, [FromBody] List<PlaylistTrackRoutingRule> rules, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var normalizedRules = WatchlistPreferenceNormalizer.RoutingRules(rules);
        var validFolderIds = await GetValidFolderIdsAsync(cancellationToken);
        if (normalizedRules?.Any(rule => !validFolderIds.Contains(rule.DestinationFolderId)) == true)
        {
            return BadRequest("Routing destination folder was not found or is disabled.");
        }

        await SaveGlobalRoutingTemplateAsync(normalizedRules, cancellationToken);

        var watchlist = await _repository.GetPlaylistWatchlistAsync(cancellationToken);
        foreach (var item in watchlist)
        {
            var existing = await _repository.GetPlaylistWatchPreferenceAsync(
                WatchlistPreferenceNormalizer.PlaylistSource(item.Source),
                item.SourceId,
                cancellationToken);
            var applicableRules = FilterGlobalRoutingRulesForPreference(normalizedRules, existing);
            await UpsertWatchPreferenceRulesAsync(
                item.Source,
                item.SourceId,
                applicableRules,
                ignoreRules: null,
                cancellationToken,
                replaceRoutingRules: true);
        }

        return Ok(new
        {
            appliedRules = normalizedRules?.Count ?? 0,
            playlistsUpdated = watchlist.Count
        });
    }

    private async Task SaveGlobalRoutingTemplateAsync(
        IReadOnlyList<PlaylistTrackRoutingRule>? routingRules,
        CancellationToken cancellationToken)
    {
        var existing = await _repository.GetPlaylistWatchPreferenceAsync(
            GlobalRoutingTemplateSource,
            GlobalRoutingTemplateSourceId,
            cancellationToken);
        var normalizedArtwork = NormalizeArtworkPreference(existing?.ReuseSavedArtwork ?? false);
        await _repository.UpsertPlaylistWatchPreferenceAsync(
            new LibraryRepository.PlaylistWatchPreferenceUpsertInput(
                GlobalRoutingTemplateSource,
                GlobalRoutingTemplateSourceId,
                DestinationFolderId: null,
                Service: null,
                SyncTargets: null,
                PreferredEngine: null,
                DownloadEngineOrder: null,
                DownloadVariantMode: null,
                SyncMode: null,
                UpdateArtwork: normalizedArtwork.UpdateArtwork,
                ReuseSavedArtwork: normalizedArtwork.ReuseSavedArtwork,
                RoutingRules: routingRules,
                IgnoreRules: existing?.IgnoreRules,
                AtmosDestinationFolderId: null),
            cancellationToken);
    }

    private async Task ApplyGlobalRoutingTemplateToPlaylistAsync(
        string source,
        string sourceId,
        CancellationToken cancellationToken)
    {
        var template = await _repository.GetPlaylistWatchPreferenceAsync(
            GlobalRoutingTemplateSource,
            GlobalRoutingTemplateSourceId,
            cancellationToken);
        var templateRules = template?.RoutingRules;
        if (templateRules == null || templateRules.Count == 0)
        {
            return;
        }

        var existing = await _repository.GetPlaylistWatchPreferenceAsync(source, sourceId, cancellationToken);
        var applicableRules = FilterGlobalRoutingRulesForPreference(templateRules, existing);
        if (applicableRules == null || applicableRules.Count == 0)
        {
            return;
        }

        await UpsertWatchPreferenceRulesAsync(
            source,
            sourceId,
            applicableRules,
            ignoreRules: null,
            cancellationToken,
            replaceRoutingRules: true);
    }

    private static List<PlaylistTrackRoutingRule>? FilterGlobalRoutingRulesForPreference(
        IReadOnlyList<PlaylistTrackRoutingRule>? rules,
        PlaylistWatchPreferenceDto? preference)
    {
        if (rules == null || rules.Count == 0)
        {
            return null;
        }

        var configuredDestinationFolderIds = new HashSet<long>();
        if (preference?.DestinationFolderId is long destinationFolderId && destinationFolderId > 0)
        {
            configuredDestinationFolderIds.Add(destinationFolderId);
        }

        if (preference?.AtmosDestinationFolderId is long atmosDestinationFolderId && atmosDestinationFolderId > 0)
        {
            configuredDestinationFolderIds.Add(atmosDestinationFolderId);
        }

        var filtered = rules
            .Where(rule => !configuredDestinationFolderIds.Contains(rule.DestinationFolderId))
            .Select((rule, index) => rule with { Order = index })
            .ToList();

        return filtered.Count == 0 ? null : filtered;
    }

    [HttpGet("{source}/{sourceId}/ignore-rules")]
    public async Task<IActionResult> GetIgnoreRules(string source, string sourceId, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var normalizedSource = WatchlistPreferenceNormalizer.PlaylistSource(source);
        var pref = await _repository.GetPlaylistWatchPreferenceAsync(normalizedSource, sourceId, cancellationToken);
        return Ok(pref?.IgnoreRules ?? Array.Empty<PlaylistTrackBlockRule>());
    }

    [HttpPost("{source}/{sourceId}/ignore-rules")]
    public async Task<IActionResult> SaveIgnoreRules(string source, string sourceId, [FromBody] List<PlaylistTrackBlockRule> rules, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var normalizedRules = WatchlistPreferenceNormalizer.BlockRules(rules);
        await UpsertWatchPreferenceRulesAsync(source, sourceId, routingRules: null, normalizedRules, cancellationToken);

        return Ok(new { saved = normalizedRules?.Count ?? 0 });
    }

    [HttpGet("{source}/{sourceId}/tracks")]
    public async Task<IActionResult> GetTrackCandidates(string source, string sourceId, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return BadRequest("Playlist source id is required.");
        }

        var normalizedSource = WatchlistPreferenceNormalizer.PlaylistSource(source);
        var normalizedSourceId = sourceId.Trim();
        var cache = await _repository.GetPlaylistTrackCandidateCacheAsync(normalizedSource, normalizedSourceId, cancellationToken);
        var candidates = DeserializeCachedPlaylistCandidates(cache?.CandidatesJson);
        var playlist = (await _repository.GetPlaylistWatchlistAsync(cancellationToken))
            .FirstOrDefault(item =>
                string.Equals(item.Source, normalizedSource, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.SourceId, normalizedSourceId, StringComparison.Ordinal));
        if (playlist == null)
        {
            return Ok(candidates);
        }

        var persistedStatuses = await _repository.GetPlaylistWatchTrackStatusesAsync(normalizedSource, normalizedSourceId, cancellationToken);
        var statusByTrackId = persistedStatuses
            .Where(static item => !string.IsNullOrWhiteSpace(item.TrackSourceId))
            .GroupBy(static item => item.TrackSourceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderByDescending(item => item.UpdatedAt).First(),
                StringComparer.OrdinalIgnoreCase);
        var ignoredTrackIds = await _repository.GetPlaylistWatchIgnoredTrackIdsAsync(normalizedSource, normalizedSourceId, cancellationToken);
        var claims = await _repository.GetPlaylistWatchDownloadClaimsForPlaylistAsync(normalizedSource, normalizedSourceId, status: null, cancellationToken);
        var claimsByTrackId = claims
            .Where(static item => !string.IsNullOrWhiteSpace(item.TrackSourceId))
            .GroupBy(static item => item.TrackSourceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var queueTasks = await _queueRepository.GetTasksAsync(cancellationToken: cancellationToken);
        var queueTasksByUuid = queueTasks.ToDictionary(static item => item.QueueUuid, StringComparer.OrdinalIgnoreCase);

        return Ok(candidates.Select(candidate =>
        {
            var trackSourceId = candidate.TrackSourceId ?? string.Empty;
            statusByTrackId.TryGetValue(trackSourceId, out var persistedStatus);
            claimsByTrackId.TryGetValue(trackSourceId, out var trackClaims);
            var queueTask = ResolveCurrentQueueTask(
                normalizedSource,
                candidate,
                trackClaims,
                queueTasks,
                queueTasksByUuid);
            var locationStatus = ResolvePlaylistTrackLocationStatus(
                ignoredTrackIds.Contains(trackSourceId),
                persistedStatus,
                queueTask);
            return new
            {
                candidate.TrackSourceId,
                candidate.Isrc,
                candidate.Title,
                candidate.Artist,
                candidate.Album,
                candidate.ReleaseYear,
                candidate.Explicit,
                candidate.Genres,
                candidate.DurationMs,
                LocationStatus = locationStatus.Status,
                LocationStatusLabel = locationStatus.Label,
                LocationStatusDetail = locationStatus.Detail,
                InLocalLibrary = persistedStatus?.LocalTrackId.HasValue == true,
                InTargetServer = string.Equals(
                    persistedStatus?.SyncStatus,
                    "playlist_synced",
                    StringComparison.OrdinalIgnoreCase),
                TargetService = persistedStatus?.TargetService ?? string.Empty,
                TargetItemId = persistedStatus?.TargetItemId ?? string.Empty,
                IdentityStatus = persistedStatus?.IdentityStatus ?? string.Empty,
                RedirectTrackSourceId = persistedStatus?.RedirectTrackSourceId ?? string.Empty,
                RedirectReason = persistedStatus?.RedirectReason ?? string.Empty,
                WatchStatus = queueTask?.Status ?? string.Empty
            };
        }).ToList());
    }

    private static PlaylistTrackLocationStatus ResolvePlaylistTrackLocationStatus(
        bool ignored,
        PlaylistWatchTrackStatusDto? persistedStatus,
        DownloadQueueItem? queueTask)
    {
        if (ignored)
        {
            return new PlaylistTrackLocationStatus("blocked", "Blocked", "Ignored or blocked by monitored playlist rules.");
        }

        var queueState = ResolveQueueLocationStatus(NormalizeStatusText(queueTask?.Status));
        if (queueState != null)
        {
            return queueState;
        }

        var persistedState = ResolveCachedTrackLocationStatus(persistedStatus);
        if (persistedState.Status != "missing")
        {
            return persistedState;
        }

        return persistedState;
    }

    private static string NormalizeStatusText(string? status)
        => string.IsNullOrWhiteSpace(status) ? string.Empty : status.Trim().ToLowerInvariant();

    private static DownloadQueueItem? ResolveCurrentQueueTask(
        string source,
        PlaylistWatchService.PlaylistTrackCandidate candidate,
        IReadOnlyList<PlaylistWatchDownloadClaimDto>? claims,
        IReadOnlyList<DownloadQueueItem> queueTasks,
        IReadOnlyDictionary<string, DownloadQueueItem> queueTasksByUuid)
    {
        var claimedTasks = claims?
            .Select(claim => queueTasksByUuid.GetValueOrDefault(claim.QueueUuid))
            .Where(static task => task != null)
            .Cast<DownloadQueueItem>()
            .ToList();
        var matchingTasks = claimedTasks is { Count: > 0 }
            ? claimedTasks
            : queueTasks.Where(task => QueueTaskMatchesCandidate(source, candidate, task)).ToList();

        return matchingTasks
            .Where(static task => ResolveQueueLocationStatus(NormalizeStatusText(task.Status)) != null)
            .OrderBy(static task => QueueStatusPriority(task.Status))
            .ThenByDescending(static task => task.UpdatedAt)
            .FirstOrDefault();
    }

    private static bool QueueTaskMatchesCandidate(
        string source,
        PlaylistWatchService.PlaylistTrackCandidate candidate,
        DownloadQueueItem task)
    {
        if (!string.IsNullOrWhiteSpace(candidate.Isrc)
            && string.Equals(candidate.Isrc.Trim(), task.Isrc?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var trackSourceId = candidate.TrackSourceId?.Trim();
        if (string.IsNullOrWhiteSpace(trackSourceId))
        {
            return false;
        }

        var queueSourceId = source switch
        {
            "deezer" => task.DeezerTrackId,
            "spotify" => task.SpotifyTrackId,
            "apple" or "applemusic" => task.AppleTrackId,
            "qobuz" => ReadQueuePayloadId(task.PayloadJson, "QobuzId", "qobuzId"),
            "tidal" => ReadQueuePayloadId(task.PayloadJson, "TidalId", "tidalId"),
            "amazon" or "amazonmusic" => ReadQueuePayloadId(task.PayloadJson, "AmazonId", "amazonId"),
            _ => null
        };
        return string.Equals(trackSourceId, queueSourceId?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadQueuePayloadId(string? payloadJson, string pascalName, string camelName)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            if (root.TryGetProperty(pascalName, out var pascalValue) && pascalValue.ValueKind == JsonValueKind.String)
            {
                return pascalValue.GetString();
            }

            return root.TryGetProperty(camelName, out var camelValue) && camelValue.ValueKind == JsonValueKind.String
                ? camelValue.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int QueueStatusPriority(string? status)
        => NormalizeStatusText(status) switch
        {
            "running" or "downloading" => 0,
            "queued" or "inqueue" or "pending" or "paused" or "retrying" => 1,
            "failed" or "canceled" or "cancelled" => 2,
            _ => 3
        };

    private static PlaylistTrackLocationStatus? ResolveQueueLocationStatus(string? status)
    {
        return status switch
        {
            "queued" or "inqueue" or "pending" => new PlaylistTrackLocationStatus("queued", "Queued", "Waiting in the download queue."),
            "running" or "downloading" => new PlaylistTrackLocationStatus("downloading", "Downloading", "Currently downloading."),
            "paused" => new PlaylistTrackLocationStatus("paused", "Paused", "Queued download is paused."),
            "retrying" => new PlaylistTrackLocationStatus("retrying", "Retrying", "Queued download is waiting for retry."),
            "failed" => new PlaylistTrackLocationStatus("failed", "Failed", "Queued download failed."),
            "canceled" or "cancelled" => new PlaylistTrackLocationStatus("cancelled", "Cancelled", "Queued download was cancelled."),
            _ => null
        };
    }

    private sealed record PlaylistTrackLocationStatus(
        string Status,
        string Label,
        string Detail);

    private static IReadOnlyList<PlaylistWatchService.PlaylistTrackCandidate> DeserializeCachedPlaylistCandidates(string? candidatesJson)
    {
        if (string.IsNullOrWhiteSpace(candidatesJson))
        {
            return Array.Empty<PlaylistWatchService.PlaylistTrackCandidate>();
        }

        try
        {
            var candidates = JsonSerializer.Deserialize<List<PlaylistWatchService.PlaylistTrackCandidate>>(candidatesJson);
            return candidates ?? (IReadOnlyList<PlaylistWatchService.PlaylistTrackCandidate>)Array.Empty<PlaylistWatchService.PlaylistTrackCandidate>();
        }
        catch (JsonException)
        {
            return Array.Empty<PlaylistWatchService.PlaylistTrackCandidate>();
        }
    }

    private static PlaylistTrackLocationStatus ResolveCachedTrackLocationStatus(PlaylistWatchTrackStatusDto? status)
    {
        var syncStatus = NormalizeStatusText(status?.SyncStatus);
        var identityStatus = NormalizeStatusText(status?.IdentityStatus);
        if (identityStatus == "review" || syncStatus == "review")
        {
            return new PlaylistTrackLocationStatus(
                "review",
                "Review",
                status?.IdentityReason ?? "Downloaded audio identity requires review.");
        }

        if (syncStatus == "playlist_synced")
        {
            return identityStatus == "redirected"
                ? new PlaylistTrackLocationStatus(
                    "redirected",
                    "Redirected",
                    status?.RedirectReason ?? "A verified equivalent track was synced.")
                : new PlaylistTrackLocationStatus(
                    "synced",
                    "Synced",
                    $"Verified in {status?.TargetService ?? "the target server"} playlist.");
        }

        if (syncStatus is "target_visible" or "waiting_for_target")
        {
            return new PlaylistTrackLocationStatus(
                "waiting_for_target",
                $"Waiting for {status?.TargetService ?? "target"}",
                "Available locally but not verified in the target playlist.");
        }

        var normalized = NormalizeStatusText(status?.Status);
        if (normalized == "downloaded")
        {
            return new PlaylistTrackLocationStatus(
                "downloaded",
                "Downloaded",
                "Downloaded and waiting for enrichment/final library verification.");
        }

        if (normalized is "completed" or "complete")
        {
            if (status?.LocalTrackId.HasValue != true)
            {
                return new PlaylistTrackLocationStatus(
                    "downloaded",
                    "Downloaded",
                    "Downloaded but not yet verified in the local library.");
            }

            return new PlaylistTrackLocationStatus(
                "library",
                "In Library",
                "Available locally but not yet verified in the target playlist.");
        }

        if (normalized == "unavailable")
        {
            var detail = status?.UnavailableNextRecheckUtc is { } nextRecheck
                ? $"Unavailable from enabled sources. Recheck after {nextRecheck.ToLocalTime():g}."
                : "Unavailable from enabled sources. Availability recheck scheduled.";
            return new PlaylistTrackLocationStatus("unavailable", "Unavailable", detail);
        }

        return new PlaylistTrackLocationStatus("missing", "Missing", "Not downloaded and not currently queued.");
    }

    private static object MapCachedPlaylistTrack(
        string source,
        PlaylistWatchService.PlaylistTrackCandidate candidate,
        int index,
        IReadOnlyDictionary<string, PlaylistWatchTrackStatusDto> statusByTrackId)
    {
        var trackSourceId = candidate.TrackSourceId ?? string.Empty;
        statusByTrackId.TryGetValue(trackSourceId, out var watchStatus);
        var locationStatus = ResolveCachedTrackLocationStatus(watchStatus);
        var sourceUrl = BuildSourceTrackUrl(source, trackSourceId);
        return new
        {
            id = trackSourceId,
            sourceTrackId = trackSourceId,
            title = candidate.Title,
            artist = candidate.Artist,
            artists = candidate.Artist,
            album = new
            {
                title = candidate.Album,
                cover_medium = candidate.CoverUrl ?? string.Empty
            },
            isrc = candidate.Isrc ?? string.Empty,
            durationMs = candidate.DurationMs ?? 0,
            explicit_lyrics = candidate.Explicit == true,
            genres = candidate.Genres,
            track_position = index + 1,
            link = sourceUrl,
            sourceUrl,
            spotifyId = string.Equals(source, "spotify", StringComparison.OrdinalIgnoreCase) ? trackSourceId : string.Empty,
            appleId = string.Equals(source, "apple", StringComparison.OrdinalIgnoreCase) ? trackSourceId : string.Empty,
            tidalId = string.Equals(source, "tidal", StringComparison.OrdinalIgnoreCase) ? trackSourceId : string.Empty,
            qobuzId = string.Equals(source, "qobuz", StringComparison.OrdinalIgnoreCase) ? trackSourceId : string.Empty,
            amazonId = string.Equals(source, "amazon", StringComparison.OrdinalIgnoreCase) || string.Equals(source, "amazonmusic", StringComparison.OrdinalIgnoreCase) ? trackSourceId : string.Empty,
            deezerId = string.Equals(source, "deezer", StringComparison.OrdinalIgnoreCase) ? trackSourceId : string.Empty,
            locationStatus = new
            {
                status = locationStatus.Status,
                label = locationStatus.Label,
                detail = locationStatus.Detail
            },
            watchStatus = watchStatus?.Status ?? string.Empty
        };
    }

    private static string BuildSourceTrackUrl(string source, string trackSourceId)
    {
        if (string.IsNullOrWhiteSpace(trackSourceId))
        {
            return string.Empty;
        }

        var escaped = Uri.EscapeDataString(trackSourceId);
        return source.Trim().ToLowerInvariant() switch
        {
            "deezer" => $"https://www.deezer.com/track/{escaped}",
            "spotify" => $"https://open.spotify.com/track/{escaped}",
            "tidal" => $"https://tidal.com/track/{escaped}",
            "qobuz" => $"https://www.qobuz.com/track/{escaped}",
            "apple" => $"https://music.apple.com/song/{escaped}",
            "boomplay" => $"https://www.boomplay.com/songs/{escaped}",
            _ => string.Empty
        };
    }

    public sealed record PlaylistWatchIgnoreRequest(string TrackSourceId, string? Isrc);

    [HttpGet("{source}/{sourceId}/ignore")]
    public async Task<IActionResult> GetIgnoreList(string source, string sourceId, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var normalizedSource = WatchlistPreferenceNormalizer.PlaylistSource(source);
        var ignored = await _repository.GetPlaylistWatchIgnoredTrackIdsAsync(normalizedSource, sourceId, cancellationToken);
        return Ok(ignored);
    }

    [HttpGet("{source}/{sourceId}/ignore-details")]
    public async Task<IActionResult> GetIgnoreListDetails(string source, string sourceId, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var normalizedSource = WatchlistPreferenceNormalizer.PlaylistSource(source);
        var ignored = await _repository.GetPlaylistWatchIgnoredTrackIdsAsync(normalizedSource, sourceId, cancellationToken);
        if (ignored.Count == 0)
        {
            return Ok(Array.Empty<object>());
        }

        var cache = await _repository.GetPlaylistTrackCandidateCacheAsync(normalizedSource, sourceId, cancellationToken);
        var candidates = TryBuildCachedCandidateLookup(cache?.CandidatesJson);

        var rows = ignored.Select(trackSourceId =>
        {
            candidates.TryGetValue(trackSourceId, out var candidate);
            return new
            {
                trackSourceId,
                title = string.IsNullOrWhiteSpace(candidate?.Title) ? trackSourceId : candidate.Title,
                artist = candidate?.Artist ?? string.Empty,
                album = candidate?.Album ?? string.Empty,
                isrc = candidate?.Isrc ?? string.Empty
            };
        }).ToList();
        return Ok(rows);
    }

    private static Dictionary<string, PlaylistWatchService.PlaylistTrackCandidate> TryBuildCachedCandidateLookup(string? candidatesJson)
    {
        if (string.IsNullOrWhiteSpace(candidatesJson))
        {
            return new Dictionary<string, PlaylistWatchService.PlaylistTrackCandidate>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var cached = JsonSerializer.Deserialize<List<PlaylistWatchService.PlaylistTrackCandidate>>(candidatesJson);
            if (cached is not { Count: > 0 })
            {
                return new Dictionary<string, PlaylistWatchService.PlaylistTrackCandidate>(StringComparer.OrdinalIgnoreCase);
            }

            return cached
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate.TrackSourceId))
                .ToDictionary(candidate => candidate.TrackSourceId, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new Dictionary<string, PlaylistWatchService.PlaylistTrackCandidate>(StringComparer.OrdinalIgnoreCase);
        }
    }

    [HttpPost("{source}/{sourceId}/ignore")]
    public async Task<IActionResult> AddIgnore(string source, string sourceId, [FromBody] PlaylistWatchIgnoreRequest request, CancellationToken cancellationToken)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.TrackSourceId))
        {
            return BadRequest("TrackSourceId is required.");
        }

        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        await _repository.AddPlaylistWatchIgnoredTracksAsync(
            WatchlistPreferenceNormalizer.PlaylistSource(source),
            sourceId,
            new List<PlaylistWatchIgnoreInsert> { new(request.TrackSourceId, request.Isrc) },
            cancellationToken);

        return Ok(new { added = 1 });
    }

    [HttpDelete("{source}/{sourceId}/ignore/{trackSourceId}")]
    public async Task<IActionResult> RemoveIgnore(string source, string sourceId, string trackSourceId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(trackSourceId))
        {
            return BadRequest("TrackSourceId is required.");
        }

        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var removed = await _repository.RemovePlaylistWatchIgnoredTrackAsync(WatchlistPreferenceNormalizer.PlaylistSource(source), sourceId, trackSourceId, cancellationToken);
        return Ok(new { removed });
    }

    private ObjectResult DatabaseNotConfigured()
    {
        return StatusCode(503, new { error = "Library DB not configured." });
    }

    private async Task<PlaylistWatchlistDto?> FindWatchlistItemAsync(string source, string sourceId, CancellationToken cancellationToken)
    {
        var normalizedSource = WatchlistPreferenceNormalizer.PlaylistSource(source);
        var items = await _repository.GetPlaylistWatchlistAsync(cancellationToken);
        return items.FirstOrDefault(entry =>
            string.Equals(entry.Source, normalizedSource, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entry.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<PlaylistSyncResult?> SyncArtworkForWatchlistItemAsync(
        PlaylistWatchlistDto item,
        CancellationToken cancellationToken)
    {
        var preference = await _repository.GetPlaylistWatchPreferenceAsync(
            WatchlistPreferenceNormalizer.PlaylistSource(item.Source),
            item.SourceId,
            cancellationToken);
        return await _playlistSyncService.SyncPlaylistArtworkOnlyAsync(
            item,
            preference,
            cancellationToken);
    }

    private async Task UpsertWatchPreferenceRulesAsync(
        string source,
        string sourceId,
        IReadOnlyList<PlaylistTrackRoutingRule>? routingRules,
        IReadOnlyList<PlaylistTrackBlockRule>? ignoreRules,
        CancellationToken cancellationToken,
        bool replaceRoutingRules = false)
    {
        var normalizedSource = WatchlistPreferenceNormalizer.PlaylistSource(source);
        var existing = await _repository.GetPlaylistWatchPreferenceAsync(normalizedSource, sourceId, cancellationToken);
        var normalizedArtwork = NormalizeArtworkPreference(
            existing?.ReuseSavedArtwork ?? false);
        await _repository.UpsertPlaylistWatchPreferenceAsync(
            new LibraryRepository.PlaylistWatchPreferenceUpsertInput(
                normalizedSource,
                sourceId,
                existing?.DestinationFolderId,
                existing?.Service,
                existing?.SyncTargets,
                existing?.PreferredEngine,
                existing?.DownloadEngineOrder,
                existing?.DownloadVariantMode,
                existing?.SyncMode,
                normalizedArtwork.UpdateArtwork,
                normalizedArtwork.ReuseSavedArtwork,
                replaceRoutingRules ? routingRules : routingRules ?? existing?.RoutingRules,
                ignoreRules ?? existing?.IgnoreRules,
                existing?.AtmosDestinationFolderId),
            cancellationToken);
    }

    private static DownloadEngineOrderSettings NormalizePlaylistDownloadEngineOrder(DownloadEngineOrderSettings? configured)
    {
        var normalized = DownloadSourceOrder.NormalizeDownloadEngineOrderSettings(configured);
        normalized.Enabled = true;
        return normalized;
    }

    private async Task ResetPlaylistPersistentStateAsync(
        string source,
        string sourceId,
        CancellationToken cancellationToken)
    {
        var normalizedSource = WatchlistPreferenceNormalizer.PlaylistSource(source);
        var state = await _repository.GetPlaylistWatchStateAsync(normalizedSource, sourceId, cancellationToken);
        await _repository.UpsertPlaylistWatchStateAsync(
            new LibraryRepository.PlaylistWatchStateUpsertInput(
                normalizedSource,
                sourceId,
                state?.SnapshotId,
                state?.TrackCount,
                state?.BatchNextOffset,
                state?.BatchProcessingSnapshotId,
                state?.LastCheckedUtc,
                LastRunStatus: "pending",
                LastRunMessage: "Manual runtime reset.",
                NextAttemptUtc: null,
                ConsecutiveFailures: 0),
            cancellationToken);
    }

    private async Task ResetSourceCircuitAsync(string source, CancellationToken cancellationToken)
    {
        var normalizedSource = WatchlistPreferenceNormalizer.PlaylistSource(source);
        await _repository.UpsertWatchlistSourceCircuitStateAsync(
            new LibraryRepository.WatchlistSourceCircuitStateUpsertInput(
                WatchType: PlaylistWatchType,
                Source: normalizedSource,
                IsOpen: false,
                OpenUntilUtc: null,
                Reason: null,
                Fingerprint: null,
                FailureCount: 0),
            cancellationToken);
    }

    private static (bool UpdateArtwork, bool ReuseSavedArtwork) NormalizeArtworkPreference(bool reuseSavedArtwork)
    {
        if (reuseSavedArtwork)
        {
            return (false, true);
        }

        // Keep exactly one option selected when reuse is not selected.
        return (true, false);
    }

    private async Task<HashSet<long>> GetValidFolderIdsAsync(CancellationToken cancellationToken)
        => await WatchlistDestinationFolderResolver.GetValidFolderIdsAsync(_profileResolutionService, cancellationToken);

    private static string? ValidatePlaylistPreferenceRequest(
        PlaylistWatchPreferenceRequest request,
        HashSet<long> validFolderIds)
    {
        if (request is null)
        {
            return "Playlist preference request is required.";
        }

        if (request.FolderId is long folderId && !validFolderIds.Contains(folderId))
        {
            return "Destination folder was not found or is disabled.";
        }

        if (request.AtmosFolderId is long atmosFolderId && !validFolderIds.Contains(atmosFolderId))
        {
            return "Atmos destination folder was not found or is disabled.";
        }

        var routingRules = WatchlistPreferenceNormalizer.RoutingRules(request.RoutingRules);
        if (routingRules?.Any(rule => !validFolderIds.Contains(rule.DestinationFolderId)) == true)
        {
            return "Routing destination folder was not found or is disabled.";
        }

        var preferredEngine = WatchlistPreferenceNormalizer.PreferredEngine(request.PreferredEngine);
        if (string.Equals(preferredEngine, DownloadSourceCatalog.Custom, StringComparison.Ordinal)
            && request.DownloadEngineOrder != null)
        {
            request.DownloadEngineOrder.Enabled = true;
            var orderValidation = DownloadSourceOrder.ValidateDownloadEngineOrderSettings(request.DownloadEngineOrder);
            if (!orderValidation.IsValid)
            {
                return orderValidation.Error ?? "Custom download source order is invalid.";
            }
        }

        return null;
    }

    private static IReadOnlyList<string> NormalizePlaylistSyncTargets(
        IReadOnlyList<string>? requestedTargets,
        string? requestedService,
        PlaylistWatchPreferenceDto? existing)
    {
        var candidates = requestedTargets is { Count: > 0 }
            ? requestedTargets
            : BuildLegacyPlaylistSyncTargetCandidates(requestedService, existing);
        var normalized = new List<string>(3);
        foreach (var candidate in candidates)
        {
            var service = WatchlistPreferenceNormalizer.IncomingText(candidate);
            if (string.IsNullOrWhiteSpace(service)
                || string.Equals(service, "none", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains(service, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(service, "plex", StringComparison.OrdinalIgnoreCase)
                || string.Equals(service, "jellyfin", StringComparison.OrdinalIgnoreCase)
                || string.Equals(service, "navidrome", StringComparison.OrdinalIgnoreCase))
            {
                normalized.Add(service);
            }
        }

        return normalized;
    }

    private static IReadOnlyList<string> BuildLegacyPlaylistSyncTargetCandidates(
        string? requestedService,
        PlaylistWatchPreferenceDto? existing)
    {
        if (!string.IsNullOrWhiteSpace(requestedService))
        {
            return [requestedService];
        }

        if (existing?.SyncTargets is { Count: > 0 })
        {
            return existing.SyncTargets;
        }

        if (!string.IsNullOrWhiteSpace(existing?.Service))
        {
            return [existing.Service];
        }

        return ["plex"];
    }

    private async Task SetPlaylistWatchSchedulerFocusAsync(
        string source,
        string sourceId,
        CancellationToken cancellationToken)
    {
        var normalizedSource = WatchlistPreferenceNormalizer.PlaylistSource(source);
        if (string.IsNullOrWhiteSpace(normalizedSource) || string.IsNullOrWhiteSpace(sourceId))
        {
            return;
        }

        await _repository.UpsertWatchlistSchedulerStateAsync(
            new LibraryRepository.WatchlistSchedulerStateUpsertInput(
                WatchType: PlaylistWatchType,
                ActiveSource: normalizedSource,
                ActiveSourceId: sourceId.Trim(),
                ActiveStartedUtc: DateTimeOffset.UtcNow,
                LastProgressUtc: null,
                ZeroQueueStreak: 0),
            cancellationToken);
    }
}
