using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using System;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Net.Http.Headers;
using System.Text.Json;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/library/playlists")]
[ApiController]
[Authorize]
[Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryToken]
public class LibraryPlaylistWatchlistApiController : ControllerBase
{
    private const string ExplicitField = "explicit";
    private const string GlobalRoutingTemplateSource = "global";
    private const string GlobalRoutingTemplateSourceId = "__playlist_routing_rules_template__";
    private readonly LibraryRepository _repository;
    private readonly LibraryConfigStore _configStore;
    private readonly PlaylistWatchService _playlistWatchService;
    private readonly PlaylistSyncService _playlistSyncService;
    private readonly PlaylistVisualService _playlistVisualService;
    private readonly WatchlistFinalizationService? _watchlistFinalizationService;
    private readonly PlaylistWatchHostedService? _playlistWatchHostedService;

    public LibraryPlaylistWatchlistApiController(
        LibraryRepository repository,
        LibraryConfigStore configStore,
        PlaylistWatchService playlistWatchService,
        PlaylistSyncService playlistSyncService,
        PlaylistVisualService playlistVisualService,
        WatchlistFinalizationService? watchlistFinalizationService = null,
        PlaylistWatchHostedService? playlistWatchHostedService = null)
    {
        _repository = repository;
        _configStore = configStore;
        _playlistWatchService = playlistWatchService;
        _playlistSyncService = playlistSyncService;
        _playlistVisualService = playlistVisualService;
        _watchlistFinalizationService = watchlistFinalizationService;
        _playlistWatchHostedService = playlistWatchHostedService;
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
        var hydrated = new List<PlaylistWatchlistDto>(items.Count);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hydratedItem = HydratePlaylistVisual(item);
            hydrated.Add(hydratedItem);
        }

        return Ok(hydrated);
    }

    [HttpGet("watch-runtime")]
    public async Task<IActionResult> GetWatchRuntime(CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var scheduler = await _repository.GetWatchlistSchedulerStateAsync("playlist", cancellationToken);
        var playlists = await _repository.GetPlaylistWatchlistAsync(cancellationToken);
        var sources = playlists
            .Select(item => NormalizePlaylistSource(item.Source))
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(source => source, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var circuits = new List<object>(sources.Count);
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var circuit = await _repository.GetWatchlistSourceCircuitStateAsync("playlist", source, cancellationToken);
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

        var normalizedSource = NormalizePlaylistSource(source);
        var watching = await _repository.IsPlaylistWatchlistedAsync(normalizedSource, sourceId, cancellationToken);
        return Ok(new { watching });
    }

    public sealed record PlaylistWatchlistRequest(string Source, string SourceId, string Name, string? ImageUrl, string? Description, int? TrackCount);

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

        var normalizedSource = NormalizePlaylistSource(request.Source);
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

        if (_playlistWatchService != null)
        {
            await _playlistWatchService.ReconcilePlaylistAsync(
                added,
                CancellationToken.None,
                forceMediaServerSync: false);
        }

        return Ok(added);
    }

    public sealed record PlaylistWatchPreferenceRequest(
        string Source,
        string SourceId,
        long? FolderId,
        long? AtmosFolderId,
        string? Service,
        string? PreferredEngine,
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

        var normalizedSource = NormalizePlaylistSource(source);
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

        var normalizedSource = NormalizePlaylistSource(request.Source);
        var existing = await _repository.GetPlaylistWatchPreferenceAsync(normalizedSource, request.SourceId, cancellationToken);
        var normalizedArtwork = NormalizeArtworkPreference(
            request.ReuseSavedArtwork ?? existing?.ReuseSavedArtwork ?? false);
        var routingRules = request.RoutingRules == null
            ? existing?.RoutingRules
            : NormalizeRoutingRules(request.RoutingRules);
        var blockRules = request.BlockRules == null
            ? existing?.IgnoreRules
            : NormalizeBlockRules(request.BlockRules);

        return await _repository.UpsertPlaylistWatchPreferenceAsync(
            new LibraryRepository.PlaylistWatchPreferenceUpsertInput(
                normalizedSource,
                request.SourceId,
                request.FolderId,
                NormalizeIncomingText(request.Service),
                NormalizePreferredEngine(request.PreferredEngine),
                string.IsNullOrWhiteSpace(request.DownloadVariantMode)
                    ? existing?.DownloadVariantMode
                    : NormalizeDownloadVariantMode(request.DownloadVariantMode),
                string.IsNullOrWhiteSpace(request.SyncMode)
                    ? existing?.SyncMode
                    : NormalizeSyncMode(request.SyncMode),
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

        var normalizedSource = NormalizePlaylistSource(source);
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
            return NotFound("Playlist watchlist entry not found.");
        }

        await SetPlaylistWatchSchedulerFocusAsync(item.Source, item.SourceId, cancellationToken);
        await _playlistWatchService.CheckPlaylistWatchItemAsync(
            item,
            cancellationToken,
            forceMediaServerSync: false);
        return Ok(new { triggered = 1 });
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
            return NotFound("Playlist watchlist entry not found.");
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
            forceMediaServerSync: false);
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
        string? SyncMode,
        bool? SyncToPlex,
        bool? SyncToJellyfin);

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
                Source = NormalizePlaylistSource(selection.Source),
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
        if (!syncToPlex && !syncToJellyfin)
        {
            return BadRequest("Select Plex, Jellyfin, or both as merge targets.");
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
                sourceUserName,
                request.SyncMode,
                syncToPlex,
                syncToJellyfin),
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
            return NotFound("Playlist watchlist entry not found.");
        }

        await _playlistWatchService.CheckPlaylistWatchItemAsync(
            item,
            cancellationToken,
            forceMediaServerSync: false);
        var refreshedItem = await FindWatchlistItemAsync(source, sourceId, cancellationToken) ?? item;
        var artworkSync = await SyncArtworkForWatchlistItemAsync(refreshedItem, cancellationToken);
        return Ok(new { refreshed = true, artworkSync });
    }

    [HttpGet("{source}/{sourceId}/visual")]
    public IActionResult GetVisual(string source, string sourceId, [FromQuery] string? file = null)
    {
        var normalizedSource = NormalizePlaylistSource(source);
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
        var normalizedSource = NormalizePlaylistSource(source);
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

        var normalizedSource = NormalizePlaylistSource(source);
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

        var normalizedSource = NormalizePlaylistSource(source);
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

        var normalizedRules = NormalizeRoutingRules(rules);
        var validFolderIds = await GetValidFolderIdsAsync(cancellationToken);
        if (normalizedRules?.Any(rule => !validFolderIds.Contains(rule.DestinationFolderId)) == true)
        {
            return BadRequest("Routing destination folder was not found or is disabled.");
        }

        await UpsertWatchPreferenceRulesAsync(source, sourceId, normalizedRules, ignoreRules: null, cancellationToken);

        return Ok(new { saved = normalizedRules?.Count ?? 0 });
    }

    [HttpPost("{source}/{sourceId}/routing-rules/apply-globally")]
    public async Task<IActionResult> ApplyRoutingRulesGlobally(string source, string sourceId, [FromBody] List<PlaylistTrackRoutingRule> rules, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var normalizedRules = NormalizeRoutingRules(rules);
        var validFolderIds = await GetValidFolderIdsAsync(cancellationToken);
        if (normalizedRules?.Any(rule => !validFolderIds.Contains(rule.DestinationFolderId)) == true)
        {
            return BadRequest("Routing destination folder was not found or is disabled.");
        }

        await SaveGlobalRoutingTemplateAsync(normalizedRules, cancellationToken);

        var watchlist = await _repository.GetPlaylistWatchlistAsync(cancellationToken);
        foreach (var item in watchlist)
        {
            await UpsertWatchPreferenceRulesAsync(
                item.Source,
                item.SourceId,
                normalizedRules,
                ignoreRules: null,
                cancellationToken);
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
                PreferredEngine: null,
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
        if (existing?.RoutingRules is { Count: > 0 })
        {
            return;
        }

        await UpsertWatchPreferenceRulesAsync(
            source,
            sourceId,
            templateRules,
            ignoreRules: null,
            cancellationToken);
    }

    [HttpGet("{source}/{sourceId}/ignore-rules")]
    public async Task<IActionResult> GetIgnoreRules(string source, string sourceId, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var normalizedSource = NormalizePlaylistSource(source);
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

        var normalizedRules = NormalizeBlockRules(rules);
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

        var normalizedSource = NormalizePlaylistSource(source);
        var candidates = await _playlistWatchService.GetPlaylistTrackCandidatesAsync(
            normalizedSource,
            sourceId,
            cancellationToken);
        return Ok(candidates);
    }

    public sealed record PlaylistWatchIgnoreRequest(string TrackSourceId, string? Isrc);

    [HttpGet("{source}/{sourceId}/ignore")]
    public async Task<IActionResult> GetIgnoreList(string source, string sourceId, CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var normalizedSource = NormalizePlaylistSource(source);
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

        var normalizedSource = NormalizePlaylistSource(source);
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
            NormalizePlaylistSource(source),
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

        var removed = await _repository.RemovePlaylistWatchIgnoredTrackAsync(NormalizePlaylistSource(source), sourceId, trackSourceId, cancellationToken);
        return Ok(new { removed });
    }

    private ObjectResult DatabaseNotConfigured()
    {
        return StatusCode(503, new { error = "Library DB not configured." });
    }

    private async Task<PlaylistWatchlistDto?> FindWatchlistItemAsync(string source, string sourceId, CancellationToken cancellationToken)
    {
        var normalizedSource = NormalizePlaylistSource(source);
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
            NormalizePlaylistSource(item.Source),
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
        CancellationToken cancellationToken)
    {
        var normalizedSource = NormalizePlaylistSource(source);
        var existing = await _repository.GetPlaylistWatchPreferenceAsync(normalizedSource, sourceId, cancellationToken);
        var normalizedArtwork = NormalizeArtworkPreference(
            existing?.ReuseSavedArtwork ?? false);
        await _repository.UpsertPlaylistWatchPreferenceAsync(
            new LibraryRepository.PlaylistWatchPreferenceUpsertInput(
                normalizedSource,
                sourceId,
                existing?.DestinationFolderId,
                existing?.Service,
                existing?.PreferredEngine,
                existing?.DownloadVariantMode,
                existing?.SyncMode,
                normalizedArtwork.UpdateArtwork,
                normalizedArtwork.ReuseSavedArtwork,
                routingRules ?? existing?.RoutingRules,
                ignoreRules ?? existing?.IgnoreRules,
                existing?.AtmosDestinationFolderId),
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
    {
        return await _repository.GetWatchlistEligibleDestinationFolderIdsAsync(cancellationToken);
    }

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

        var routingRules = NormalizeRoutingRules(request.RoutingRules);
        if (routingRules?.Any(rule => !validFolderIds.Contains(rule.DestinationFolderId)) == true)
        {
            return "Routing destination folder was not found or is disabled.";
        }

        return null;
    }

    private static string? NormalizeIncomingText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizePreferredEngine(string? value)
    {
        var normalized = NormalizeIncomingText(value)?.ToLowerInvariant();
        return normalized switch
        {
            "auto" or "amazon" or "apple" or "deezer" or "qobuz" or "tidal" => normalized,
            _ => null
        };
    }

    private async Task SetPlaylistWatchSchedulerFocusAsync(
        string source,
        string sourceId,
        CancellationToken cancellationToken)
    {
        var normalizedSource = NormalizePlaylistSource(source);
        if (string.IsNullOrWhiteSpace(normalizedSource) || string.IsNullOrWhiteSpace(sourceId))
        {
            return;
        }

        await _repository.UpsertWatchlistSchedulerStateAsync(
            new LibraryRepository.WatchlistSchedulerStateUpsertInput(
                WatchType: "playlist",
                ActiveSource: normalizedSource,
                ActiveSourceId: sourceId.Trim(),
                ActiveStartedUtc: DateTimeOffset.UtcNow,
                LastProgressUtc: null,
                ZeroQueueStreak: 0),
            cancellationToken);
    }

    private static string? NormalizeDownloadVariantMode(string? value)
    {
        var normalized = NormalizeIncomingText(value)?.ToLowerInvariant();
        return normalized switch
        {
            "dual_quality" or "atmos_only" => normalized,
            "standard" => "standard",
            _ => null
        };
    }

    private static string? NormalizeSyncMode(string? value)
    {
        var normalized = NormalizeIncomingText(value)?.ToLowerInvariant();
        return normalized switch
        {
            "append" or "mirror" => normalized,
            _ => null
        };
    }

    private static List<PlaylistTrackRoutingRule>? NormalizeRoutingRules(List<PlaylistTrackRoutingRule>? rules)
    {
        if (rules == null || rules.Count == 0)
        {
            return null;
        }

        return rules
            .Where(static rule => rule.DestinationFolderId > 0)
            .Select(static (rule, index) =>
            {
                var field = NormalizeRoutingField(rule.ConditionField);
                return rule with
                {
                    ConditionField = field,
                    ConditionOperator = NormalizeRoutingOperator(field, rule.ConditionOperator),
                    ConditionValue = rule.ConditionValue?.Trim() ?? string.Empty,
                    Order = index
                };
            })
            .Where(static rule => !string.IsNullOrWhiteSpace(rule.ConditionField)
                && (string.Equals(rule.ConditionField, ExplicitField, StringComparison.OrdinalIgnoreCase)
                    || !string.IsNullOrWhiteSpace(rule.ConditionValue)))
            .ToList();
    }

    private static List<PlaylistTrackBlockRule>? NormalizeBlockRules(List<PlaylistTrackBlockRule>? rules)
    {
        if (rules == null || rules.Count == 0)
        {
            return null;
        }

        return rules
            .Select(static (rule, index) =>
            {
                var field = NormalizeRoutingField(rule.ConditionField);
                return rule with
                {
                    ConditionField = field,
                    ConditionOperator = NormalizeRoutingOperator(field, rule.ConditionOperator),
                    ConditionValue = rule.ConditionValue?.Trim() ?? string.Empty,
                    Order = index
                };
            })
            .Where(static rule => !string.IsNullOrWhiteSpace(rule.ConditionField)
                && (string.Equals(rule.ConditionField, ExplicitField, StringComparison.OrdinalIgnoreCase)
                    || !string.IsNullOrWhiteSpace(rule.ConditionValue)))
            .ToList();
    }

    private static string NormalizeRoutingField(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "artist" or "title" or "album" or "genre" or "year" or ExplicitField => normalized,
            _ => string.Empty
        };
    }

    private static string NormalizeRoutingOperator(string? field, string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        if (string.Equals(field, ExplicitField, StringComparison.OrdinalIgnoreCase))
        {
            return normalized == "is_false" ? "is_false" : "is_true";
        }

        if (string.Equals(field, "year", StringComparison.OrdinalIgnoreCase))
        {
            return normalized is "gte" or "lte" ? normalized : "equals";
        }

        return normalized is "equals" or "starts_with" ? normalized : "contains";
    }

    private static string NormalizePlaylistSource(string? source)
    {
        var normalized = source?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized switch
        {
            "smarttracks" => "smarttracklist",
            "recommendation" => "recommendations",
            "itunes" => "apple",
            "applemusic" => "apple",
            _ => string.IsNullOrWhiteSpace(normalized) ? "deezer" : normalized
        };
    }
}
