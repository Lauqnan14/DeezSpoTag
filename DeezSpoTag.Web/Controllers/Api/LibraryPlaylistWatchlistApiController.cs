using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using System;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Net.Http.Headers;

namespace DeezSpoTag.Web.Controllers.Api;

[Route("api/library/playlists")]
[ApiController]
[Authorize]
public class LibraryPlaylistWatchlistApiController : ControllerBase
{
    private readonly LibraryRepository _repository;
    private readonly LibraryConfigStore _configStore;
    private readonly PlaylistWatchService _playlistWatchService;
    private readonly PlaylistSyncService _playlistSyncService;
    private readonly PlaylistVisualService _playlistVisualService;

    public LibraryPlaylistWatchlistApiController(
        LibraryRepository repository,
        LibraryConfigStore configStore,
        PlaylistWatchService playlistWatchService,
        PlaylistSyncService playlistSyncService,
        PlaylistVisualService playlistVisualService)
    {
        _repository = repository;
        _configStore = configStore;
        _playlistWatchService = playlistWatchService;
        _playlistSyncService = playlistSyncService;
        _playlistVisualService = playlistVisualService;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var items = await _repository.GetPlaylistWatchlistAsync(cancellationToken);
        var hydrated = items
            .Select(item =>
            {
                var visual = _playlistVisualService.GetStoredVisual(item.Source, item.SourceId);
                if (visual is null || string.IsNullOrWhiteSpace(visual.Url))
                {
                    if (IsLocalPlaylistVisualUrl(item.ImageUrl))
                    {
                        return item with { ImageUrl = null };
                    }

                    return item;
                }

                return item with
                {
                    ImageUrl = visual.Url
                };
            })
            .ToList();

        return Ok(hydrated);
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
            request.Name,
            request.ImageUrl,
            request.Description,
            request.TrackCount,
            cancellationToken);

        if (added is null)
        {
            return StatusCode(500, "Failed to add playlist watchlist entry.");
        }

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"Playlist watchlist added: {request.Name}."));

        try
        {
            await _playlistWatchService.CheckPlaylistWatchItemAsync(
                added,
                cancellationToken,
                forceMediaServerSync: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            // Keep add endpoint resilient; background monitor will retry.
        }

        return Ok(added);
    }

    public sealed record PlaylistWatchPreferenceRequest(
        string Source,
        string SourceId,
        long? FolderId,
        string? Service,
        string? PreferredEngine,
        string? DownloadVariantMode,
        string? SyncMode,
        string? AutotagProfile,
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
        return Ok(item);
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

        var results = new List<object>(requests.Count);
        foreach (var request in requests)
        {
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
        return await _repository.UpsertPlaylistWatchPreferenceAsync(
            new LibraryRepository.PlaylistWatchPreferenceUpsertInput(
                normalizedSource,
                request.SourceId,
                request.FolderId,
                string.IsNullOrWhiteSpace(request.Service) ? null : request.Service.Trim(),
                string.IsNullOrWhiteSpace(request.PreferredEngine) ? null : request.PreferredEngine.Trim().ToLowerInvariant(),
                string.IsNullOrWhiteSpace(request.DownloadVariantMode) ? existing?.DownloadVariantMode : request.DownloadVariantMode.Trim().ToLowerInvariant(),
                string.IsNullOrWhiteSpace(request.SyncMode) ? existing?.SyncMode : request.SyncMode.Trim().ToLowerInvariant(),
                string.IsNullOrWhiteSpace(request.AutotagProfile) ? null : request.AutotagProfile.Trim(),
                request.UpdateArtwork ?? existing?.UpdateArtwork ?? true,
                request.ReuseSavedArtwork ?? existing?.ReuseSavedArtwork ?? false,
                request.RoutingRules ?? existing?.RoutingRules,
                request.BlockRules ?? existing?.IgnoreRules),
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
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _playlistWatchService.CheckPlaylistWatchItemAsync(
                item,
                cancellationToken,
                forceMediaServerSync: true);
        }

        return Ok(new { triggered = items.Count });
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

        await _playlistWatchService.CheckPlaylistWatchItemAsync(
            item,
            cancellationToken,
            forceMediaServerSync: true);
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

        var preference = await _repository.GetPlaylistWatchPreferenceAsync(item.Source, item.SourceId, cancellationToken);
        var candidates = await _playlistWatchService.GetPlaylistTrackCandidatesAsync(
            item.Source,
            item.SourceId,
            cancellationToken);
        var result = await _playlistSyncService.SyncPlaylistAsync(
            item,
            preference,
            candidates,
            force: true,
            cancellationToken);
        return Ok(new { result.Success, result.Message, result.PlaylistId, result.SyncedTracks });
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
            .Where(selection => !string.IsNullOrWhiteSpace(selection?.Source)
                && !string.IsNullOrWhiteSpace(selection?.SourceId))
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
            forceMediaServerSync: true);
        return Ok(new { refreshed = true });
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
                null,
                activeVisual.Url,
                null,
                null,
                cancellationToken);
        }

        return Ok(new { updated = true, imageUrl = activeVisual?.Url });
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

        await UpsertWatchPreferenceRulesAsync(source, sourceId, rules, ignoreRules: null, cancellationToken);

        return Ok(new { saved = rules?.Count ?? 0 });
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

        await UpsertWatchPreferenceRulesAsync(source, sourceId, routingRules: null, rules, cancellationToken);

        return Ok(new { saved = rules?.Count ?? 0 });
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

    private async Task UpsertWatchPreferenceRulesAsync(
        string source,
        string sourceId,
        IReadOnlyList<PlaylistTrackRoutingRule>? routingRules,
        IReadOnlyList<PlaylistTrackBlockRule>? ignoreRules,
        CancellationToken cancellationToken)
    {
        var normalizedSource = NormalizePlaylistSource(source);
        var existing = await _repository.GetPlaylistWatchPreferenceAsync(normalizedSource, sourceId, cancellationToken);
        await _repository.UpsertPlaylistWatchPreferenceAsync(
            new LibraryRepository.PlaylistWatchPreferenceUpsertInput(
                normalizedSource,
                sourceId,
                existing?.DestinationFolderId,
                existing?.Service,
                existing?.PreferredEngine,
                existing?.DownloadVariantMode,
                existing?.SyncMode,
                existing?.AutotagProfile,
                existing?.UpdateArtwork ?? true,
                existing?.ReuseSavedArtwork ?? false,
                routingRules ?? existing?.RoutingRules,
                ignoreRules ?? existing?.IgnoreRules),
            cancellationToken);
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
