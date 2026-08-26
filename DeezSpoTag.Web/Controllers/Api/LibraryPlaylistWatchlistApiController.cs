using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using System;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Net.Http.Headers;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Queue;

namespace DeezSpoTag.Web.Controllers.Api;

public sealed class LibraryPlaylistWatchlistDependencies
{
    public required LibraryRepository Repository { get; init; }
    public required LibraryConfigStore ConfigStore { get; init; }
    public required PlaylistWatchReconciler PlaylistWatchReconciler { get; init; }
    public required PlaylistSyncService PlaylistSyncService { get; init; }
    public required PlaylistVisualService PlaylistVisualService { get; init; }
    public required DownloadQueueRepository QueueRepository { get; init; }
    public required AutoTagProfileResolutionService ProfileResolutionService { get; init; }
    public required BoomplayMetadataService BoomplayMetadataService { get; init; }
    public WatchlistFinalizationService? WatchlistFinalizationService { get; init; }
    public WatchlistRunCoordinator? WatchlistRunCoordinator { get; init; }
}

[Route("api/library/playlists")]
[ApiController]
[Authorize]
[Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryToken]
public partial class WatchlistApiController : ControllerBase
{
    private const string GlobalRoutingTemplateSource = "global";
    private const string GlobalRoutingTemplateSourceId = "__playlist_routing_rules_template__";
    private const string PlaylistWatchType = "playlist";
    private const string PlaylistWatchlistEntryNotFoundMessage = "Playlist watchlist entry not found.";
    private readonly LibraryRepository _repository;
    private readonly LibraryConfigStore _configStore;
    private readonly PlaylistWatchReconciler _playlistWatchReconciler;
    private readonly PlaylistSyncService _playlistSyncService;
    private readonly PlaylistVisualService _playlistVisualService;
    private readonly DownloadQueueRepository _queueRepository;
    private readonly AutoTagProfileResolutionService _profileResolutionService;
    private readonly BoomplayMetadataService _boomplayMetadataService;
    private readonly WatchlistFinalizationService? _watchlistFinalizationService;
    private readonly WatchlistRunCoordinator? _watchlistCoordinator;

    public WatchlistApiController(LibraryPlaylistWatchlistDependencies dependencies)
    {
        _repository = dependencies.Repository;
        _configStore = dependencies.ConfigStore;
        _playlistWatchReconciler = dependencies.PlaylistWatchReconciler;
        _playlistSyncService = dependencies.PlaylistSyncService;
        _playlistVisualService = dependencies.PlaylistVisualService;
        _profileResolutionService = dependencies.ProfileResolutionService;
        _boomplayMetadataService = dependencies.BoomplayMetadataService;
        _queueRepository = dependencies.QueueRepository;
        _watchlistFinalizationService = dependencies.WatchlistFinalizationService;
        _watchlistCoordinator = dependencies.WatchlistRunCoordinator;
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
        var summarized = await HydrateQueuePresentationSummaryAsync(items, cancellationToken);
        var hydrated = summarized.Select(HydratePlaylistVisual).ToList();
        return Ok(hydrated);
    }

    private async Task<IReadOnlyList<PlaylistWatchlistDto>> HydrateQueuePresentationSummaryAsync(
        IReadOnlyList<PlaylistWatchlistDto> playlists,
        CancellationToken cancellationToken)
    {
        if (playlists.Count == 0)
        {
            return playlists;
        }

        if (_queueRepository == null)
        {
            return playlists;
        }

        var claims = await _repository.GetAllPlaylistWatchDownloadClaimsAsync("pending", cancellationToken);
        var queueItems = await _queueRepository.GetTasksAsync(cancellationToken: cancellationToken);
        var queueByUuid = queueItems.ToDictionary(item => item.QueueUuid, StringComparer.OrdinalIgnoreCase);
        var claimsByPlaylist = claims.GroupBy(
            claim => $"{claim.Source}:{claim.SourceId}",
            StringComparer.OrdinalIgnoreCase);
        var counts = claimsByPlaylist.ToDictionary(
            group => group.Key,
            group =>
            {
                var active = group
                    .Select(claim => new { claim.TrackSourceId, Task = queueByUuid.GetValueOrDefault(claim.QueueUuid) })
                    .Where(entry => entry.Task != null)
                    .ToList();
                var downloading = active
                    .Where(entry => NormalizeStatusText(entry.Task!.Status) is "running" or "downloading")
                    .Select(entry => entry.TrackSourceId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                var queued = active
                    .Where(entry => NormalizeStatusText(entry.Task!.Status) is "queued" or "inqueue" or "pending" or "paused" or "retrying")
                    .Select(entry => entry.TrackSourceId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                return (Queued: queued, Downloading: downloading);
            },
            StringComparer.OrdinalIgnoreCase);

        return playlists
            .Select(item => counts.TryGetValue($"{item.Source}:{item.SourceId}", out var count)
                ? item with { QueuedTrackCount = count.Queued, DownloadingTrackCount = count.Downloading }
                : item with { QueuedTrackCount = 0, DownloadingTrackCount = 0 })
            .ToList();
    }

    [HttpGet("watch-runtime")]
    public async Task<IActionResult> GetWatchRuntime(CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var scheduler = await _repository.GetWatchlistSchedulerStateAsync(PlaylistWatchType, cancellationToken);
        var pendingClaims = await _repository.GetAllPlaylistWatchDownloadClaimsAsync("pending", cancellationToken);
        var queueItems = _queueRepository == null
            ? []
            : await _queueRepository.GetTasksAsync(cancellationToken: cancellationToken);
        var queueByUuid = queueItems.ToDictionary(item => item.QueueUuid, StringComparer.OrdinalIgnoreCase);
        var orphanedPendingClaims = pendingClaims.Count(claim =>
            !queueByUuid.TryGetValue(claim.QueueUuid, out var queueItem)
            || !DownloadQueueRecoveryPolicy.IsWatchlistClaimOwnedByQueue(queueItem, DateTimeOffset.UtcNow));
        var syncJobs = await _repository.GetWatchlistSyncJobStatusCountsAsync(cancellationToken);
        var pendingReconciliationRequests = await _repository.GetWatchlistReconciliationRequestCountAsync(cancellationToken);
        var runtime = _watchlistCoordinator?.GetRuntimeHealth();
        if (runtime != null)
        {
            runtime = runtime with { PendingReconciliationRequests = pendingReconciliationRequests };
        }
        var playlists = await HydrateQueuePresentationSummaryAsync(
            await _repository.GetPlaylistWatchlistAsync(cancellationToken),
            cancellationToken);
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

        await _repository.CloseExpiredWatchlistTargetCircuitsAsync(cancellationToken);
        var drift = await _repository.DetectWatchlistStateDriftAsync(
            WatchlistPostDownloadSyncService.MaxSyncAttempts,
            cancellationToken);
        var targetCircuits = new List<object>();
        foreach (var target in new[] { "plex", "jellyfin", "navidrome" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetCircuit = await _repository.GetWatchlistTargetCircuitStateAsync(target, cancellationToken);
            if (targetCircuit == null)
            {
                continue;
            }

            targetCircuits.Add(new
            {
                targetService = targetCircuit.TargetService,
                isOpen = targetCircuit.IsOpen,
                openUntilUtc = targetCircuit.OpenUntilUtc,
                reason = targetCircuit.Reason,
                failureCount = targetCircuit.FailureCount
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
                    lastProgressUtc = scheduler.LastProgressUtc
                },
            circuits,
            targetCircuits,
            stateDrift = new
            {
                hasDrift = drift.HasDrift,
                total = drift.Total,
                appliedWithoutMembership = drift.AppliedWithoutMembership,
                membershipWithoutApplied = drift.MembershipWithoutApplied,
                orphanedMembership = drift.OrphanedMembership,
                membershipForUnconfiguredTarget = drift.MembershipForUnconfiguredTarget,
                blockedBelowAttemptCap = drift.BlockedBelowAttemptCap
            },
            runtime,
            claims = new
            {
                pending = pendingClaims.Count,
                orphanedPending = orphanedPendingClaims
            },
            targetSyncJobs = syncJobs,
            presentation = new
            {
                review = playlists.Sum(static item => item.ReviewTrackCount ?? 0),
                missing = playlists.Sum(static item => item.MissingTrackCount ?? 0),
                mappingRetry = playlists.Sum(static item => item.MappingRetryCount ?? 0),
                blocked = playlists.Sum(static item => item.BlockedTrackCount ?? 0),
                failed = playlists.Sum(static item => item.FailedTrackCount ?? 0),
                queued = playlists.Sum(static item => item.QueuedTrackCount ?? 0),
                downloading = playlists.Sum(static item => item.DownloadingTrackCount ?? 0)
            },
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

        var normalizedSource = WatchlistPreferenceNormalizer.PlaylistSource(source);
        var normalizedSourceId = (sourceId ?? string.Empty).Trim();
        var playlist = await _repository.GetPlaylistWatchlistEntryAsync(
            normalizedSource,
            normalizedSourceId,
            cancellationToken);
        return Ok(new
        {
            watching = playlist != null,
            sourceUrl = playlist?.SourceUrl,
            sourceStorefront = playlist?.SourceStorefront
        });
    }

    [HttpGet("{source}/{sourceId}/sync-jobs")]
    public async Task<IActionResult> GetTargetSyncJobs(
        string source,
        string sourceId,
        CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        var normalizedSource = WatchlistPreferenceNormalizer.PlaylistSource(source);
        if (!await _repository.IsPlaylistWatchlistedAsync(normalizedSource, sourceId, cancellationToken))
        {
            return NotFound(PlaylistWatchlistEntryNotFoundMessage);
        }

        var preference = await _repository.GetPlaylistWatchPreferenceAsync(
            normalizedSource,
            sourceId,
            cancellationToken);
        var jobs = await _repository.GetWatchlistSyncJobsAsync(
            normalizedSource,
            sourceId,
            cancellationToken);
        var jobsByTarget = jobs
            .GroupBy(job => job.TargetService, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(job => new
                {
                    job.TrackId,
                    job.Status,
                    job.AttemptCount,
                    job.NextAttemptUtc,
                    job.LastError
                }).ToList(),
                StringComparer.OrdinalIgnoreCase);
        var configuredTargets = preference?.SyncTargets is { Count: > 0 }
            ? preference.SyncTargets
            : string.IsNullOrWhiteSpace(preference?.Service)
                ? []
                : [preference.Service];

        return Ok(configuredTargets
            .Select(target => target.Trim().ToLowerInvariant())
            .Where(target => target is "plex" or "jellyfin" or "navidrome")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(target => new
            {
                Target = target,
                PlaylistId = target switch
                {
                    "plex" => preference?.PlexPlaylistId,
                    "jellyfin" => preference?.JellyfinPlaylistId,
                    "navidrome" => preference?.NavidromePlaylistId,
                    _ => null
                },
                State = jobsByTarget.TryGetValue(target, out var targetJobs)
                    ? targetJobs.Any(job => string.Equals(job.Status, "processing", StringComparison.OrdinalIgnoreCase))
                        ? "processing"
                        : targetJobs.Any(job => string.Equals(job.Status, "blocked", StringComparison.OrdinalIgnoreCase))
                            ? "blocked"
                            : "waiting"
                    : target switch
                    {
                        "plex" when !string.IsNullOrWhiteSpace(preference?.PlexPlaylistId) => "completed",
                        "jellyfin" when !string.IsNullOrWhiteSpace(preference?.JellyfinPlaylistId) => "completed",
                        "navidrome" when !string.IsNullOrWhiteSpace(preference?.NavidromePlaylistId) => "completed",
                        _ => "not_scheduled"
                    },
                Jobs = jobsByTarget.GetValueOrDefault(target, [])
            })
            .ToList());
    }

    public sealed record PlaylistWatchlistRequest(
        string Source,
        string SourceId,
        string Name,
        string? ImageUrl,
        string? Description,
        int? TrackCount,
        string? SourceUrl = null,
        string? SourceStorefront = null);
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
        var normalizedSourceId = request.SourceId.Trim();
        var sourceUrl = string.IsNullOrWhiteSpace(request.SourceUrl) ? null : request.SourceUrl.Trim();
        var sourceStorefront = string.IsNullOrWhiteSpace(request.SourceStorefront)
            ? null
            : request.SourceStorefront.Trim().ToLowerInvariant();
        if (string.Equals(normalizedSource, "apple", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(sourceUrl))
            {
                if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var appleUri)
                    || !string.Equals(appleUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(appleUri.Host, "music.apple.com", StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest("Apple playlist URL must use https://music.apple.com.");
                }

                sourceStorefront = appleUri.AbsolutePath
                    .Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault()?
                    .Trim()
                    .ToLowerInvariant();
            }

            if (string.IsNullOrWhiteSpace(sourceStorefront))
            {
                return BadRequest("Apple playlist storefront is required.");
            }

            if (string.IsNullOrWhiteSpace(sourceUrl))
            {
                sourceUrl = $"https://music.apple.com/{sourceStorefront}/playlist/{Uri.EscapeDataString(normalizedSourceId)}";
            }
        }
        else
        {
            sourceUrl = null;
            sourceStorefront = null;
        }

        if (string.Equals(normalizedSource, "boomplay", StringComparison.OrdinalIgnoreCase))
        {
            var resolvedSourceId = await _boomplayMetadataService.ResolveContentIdAsync(
                "playlist",
                normalizedSourceId,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(resolvedSourceId))
            {
                return BadRequest("Boomplay playlist id could not be resolved.");
            }

            normalizedSourceId = resolvedSourceId;
        }

        var added = await _repository.AddPlaylistWatchlistAsync(
            normalizedSource,
            normalizedSourceId,
            new PlaylistWatchlistMetadataInput(
                request.Name,
                request.ImageUrl,
                request.Description,
                request.TrackCount,
                SourceUrl: sourceUrl,
                SourceStorefront: sourceStorefront),
            cancellationToken);

        if (added is null)
        {
            return StatusCode(500, "Failed to add playlist watchlist entry.");
        }

        await ApplyGlobalRoutingTemplateToPlaylistAsync(
            normalizedSource,
            normalizedSourceId,
            cancellationToken);

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"Playlist watchlist added: {request.Name}."));

        if (_watchlistCoordinator != null)
        {
            await _watchlistCoordinator.TriggerPlaylistOnceAsync(added.Source, added.SourceId, cancellationToken);
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
        _watchlistCoordinator?.ResetPlaylistRuntimeStateForAll(updated);
        var first = priorities[0];
        if (_watchlistCoordinator != null)
        {
            await _watchlistCoordinator.TriggerPlaylistOnceAsync(first.Source, first.SourceId, cancellationToken);
        }
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

        var saved = await _repository.UpsertPlaylistWatchPreferenceAsync(
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
        if (saved != null)
        {
            // Create the destination playlist container (name/description/artwork) on every
            // newly-configured target immediately, instead of waiting for the first batch of
            // tracks to download and the periodic reconciliation pass to happen to run.
            var item = await FindWatchlistItemAsync(normalizedSource, request.SourceId, cancellationToken);
            if (item != null)
            {
                try
                {
                    await _playlistSyncService.EnsureTargetPlaylistContainersAsync(item, saved, cancellationToken);
                }
                catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
                {
                    // Best-effort -- the normal reconciliation/target-sync pipeline will retry
                    // container creation on its own schedule if this attempt fails.
                }
            }

            if (_watchlistCoordinator != null)
            {
                await _watchlistCoordinator.TriggerPlaylistOnceAsync(
                    normalizedSource,
                    request.SourceId,
                    cancellationToken);
            }
        }
        return saved;
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
        if (removed)
        {
            _watchlistCoordinator?.ResetPlaylistRuntimeState(normalizedSource, sourceId);
            _playlistVisualService.DeleteStoredVisuals(normalizedSource, sourceId);
        }
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
        var trigger = _watchlistCoordinator == null
            ? null
            : await _watchlistCoordinator.TriggerRunOnceAsync(cancellationToken);

        return Ok(new { queued = trigger?.Scheduled == true, pending = items.Count, status = trigger?.Status.ToString() });
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

        var trigger = _watchlistCoordinator == null
            ? null
            : await _watchlistCoordinator.TriggerPlaylistOnceAsync(item.Source, item.SourceId, cancellationToken);

        return Ok(new { triggered = trigger?.Scheduled == true ? 1 : 0, status = trigger?.Status.ToString() });
    }

    [HttpPost("reset-runtime")]
    public async Task<IActionResult> ResetRuntime(CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DatabaseNotConfigured();
        }

        if (_watchlistCoordinator == null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Watchlist coordinator is unavailable.");
        }

        var result = await _watchlistCoordinator.ResetRuntimeAsync(cancellationToken);
        var cleanup = result.Cleanup;

        return Ok(new
        {
            reset = true,
            reconciliationRequestsCleared = cleanup.ReconciliationRequestsDeleted,
            targetSyncJobsCleared = cleanup.SyncJobsDeleted,
            finalizationRowsCleared = cleanup.FinalizationOutboxDeleted,
            claimsCleared = cleanup.ClaimsDeleted,
            schedulerRowsCleared = cleanup.SchedulerRowsDeleted,
            sourceCircuitsCleared = cleanup.SourceCircuitsDeleted,
            targetCircuitsCleared = cleanup.TargetCircuitsDeleted,
            playlistStatesCleared = cleanup.PlaylistStatesDeleted,
            artistStatesCleared = cleanup.ArtistStatesDeleted,
            triggered = result.TriggerStatus is not WatchlistTriggerStatus.Disabled,
            triggerStatus = result.TriggerStatus.ToString()
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
        var repairedFinalizations = _watchlistFinalizationService == null
            ? 0
            : await _watchlistFinalizationService.RepairPlaylistAsync(item, cancellationToken);
        if (_watchlistCoordinator != null)
        {
            await _watchlistCoordinator.ResetSourceCircuitAsync(item.Source, cancellationToken);
        }
        _watchlistCoordinator?.ResetPlaylistRuntimeState(item.Source, item.SourceId);
        var recoveredClaims = await _playlistWatchReconciler.RecoverInvalidPendingWatchClaimsAsync(cancellationToken);

        WatchlistTriggerResult? trigger = null;
        if (_watchlistCoordinator != null)
        {
            trigger = await _watchlistCoordinator.TriggerPlaylistOnceAsync(item.Source, item.SourceId, cancellationToken);
        }

        return Ok(new
        {
            reset = true,
            source = item.Source,
            sourceId = item.SourceId,
            recoveredClaims,
            repairedFinalizations,
            triggered = trigger?.Scheduled == true,
            triggerStatus = trigger?.Status.ToString()
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
        if (_watchlistCoordinator != null)
        {
            await _watchlistCoordinator.ResetSourceCircuitAsync(item.Source, cancellationToken);
        }
        _watchlistCoordinator?.ResetPlaylistRuntimeState(item.Source, item.SourceId);

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

        WatchlistTriggerResult? trigger = null;
        if (_watchlistCoordinator != null)
        {
            if (next != null)
            {
                trigger = await _watchlistCoordinator.TriggerPlaylistOnceAsync(next.Source, next.SourceId, cancellationToken);
            }
            else
            {
                await _watchlistCoordinator.ResetSchedulerStateAsync(cancellationToken);
                trigger = await _watchlistCoordinator.TriggerRunOnceAsync(cancellationToken);
            }
        }

        return Ok(new
        {
            reset = true,
            skipped = next != null,
            nextSource = next?.Source,
            nextSourceId = next?.SourceId,
            triggered = trigger?.Scheduled == true,
            triggerStatus = trigger?.Status.ToString()
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

        var repairNotifications = _watchlistFinalizationService == null
            ? 0
            : await _watchlistFinalizationService.RepairPlaylistAsync(
                item,
                cancellationToken);
        var reconciliation = await _playlistWatchReconciler.ReconcilePlaylistAsync(
            item,
            CancellationToken.None,
            forceMediaServerSync: true);
        var preference = await _repository.GetPlaylistWatchPreferenceAsync(item.Source, item.SourceId, cancellationToken);
        var candidates = await _playlistWatchReconciler.GetCachedPlaylistTrackCandidatesAsync(
            item.Source,
            item.SourceId,
            cancellationToken);
        var result = await _playlistSyncService.SyncAvailablePlaylistTracksAsync(
            item,
            preference,
            candidates,
            force: true,
            cancellationToken);
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
            PlaylistId = result.PlaylistId,
            SyncedTracks = result.SyncedTracks,
            LocalMatches = result.LocalMatches,
            TargetMatches = result.TargetMatches,
            MissingTracks = result.MissingTracks,
            MetadataMatches = result.MetadataMatches,
            SearchMatches = result.SearchMatches,
            SyncMessage = result.Message
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
            var candidates = await _playlistWatchReconciler.GetPlaylistTrackCandidatesAsync(
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

        var previousRevision = _playlistVisualService.GetActiveArtworkRevision(item.Source, item.SourceId);
        var refreshedItem = await _playlistWatchReconciler.RefreshPlaylistMetadataOnlyAsync(
            item,
            cancellationToken,
            forceArtworkRefresh: true);
        var currentRevision = _playlistVisualService.GetActiveArtworkRevision(item.Source, item.SourceId);
        var artworkChanged = !string.IsNullOrWhiteSpace(currentRevision)
            && !string.Equals(previousRevision, currentRevision, StringComparison.OrdinalIgnoreCase);
        var artworkSync = artworkChanged
            ? await SyncArtworkForWatchlistItemAsync(refreshedItem, cancellationToken)
            : null;
        return Ok(new { refreshed = true, artworkChanged, artworkSync });
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
        var previousRevision = _playlistVisualService.GetActiveArtworkRevision(normalizedSource, sourceId);
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
        var currentRevision = _playlistVisualService.GetActiveArtworkRevision(normalizedSource, sourceId);
        var artworkChanged = !string.IsNullOrWhiteSpace(currentRevision)
            && !string.Equals(previousRevision, currentRevision, StringComparison.OrdinalIgnoreCase);
        var artworkSync = artworkChanged && item != null && activeVisual != null
            ? await SyncArtworkForWatchlistItemAsync(item with { ImageUrl = activeVisual.Url }, cancellationToken)
            : null;

        return Ok(new { updated = true, imageUrl = activeVisual?.Url, artworkChanged, artworkSync });
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
        var candidates = await _playlistWatchReconciler.GetCachedPlaylistTrackCandidatesAsync(
            normalizedSource,
            normalizedSourceId,
            cancellationToken);
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
                trackClaims,
                queueTasksByUuid);
            var locationStatus = ResolvePlaylistTrackLocationStatus(
                ignoredTrackIds.Contains(trackSourceId),
                persistedStatus,
                queueTask?.Status);
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
                candidate.DeezerId,
                candidate.MappingStatus,
                candidate.MappingError,
                Resolvable = PlaylistCandidateContract.IsResolvable(normalizedSource, candidate),
                LocationStatus = locationStatus.Status,
                LocationStatusLabel = locationStatus.Label,
                LocationStatusDetail = locationStatus.Detail,
                InLocalLibrary = persistedStatus?.LocalTrackId.HasValue == true,
                InTargetServer = string.Equals(
                    persistedStatus?.SyncStatus,
                    "playlist_synced",
                    StringComparison.OrdinalIgnoreCase),
                TargetService = persistedStatus?.TargetService ?? string.Empty,
                SyncedTargetServices = persistedStatus?.SyncedTargetServices ?? string.Empty,
                MissingTargetServices = persistedStatus?.MissingTargetServices ?? string.Empty,
                TargetItemId = persistedStatus?.TargetItemId ?? string.Empty,
                IdentityStatus = persistedStatus?.IdentityStatus ?? string.Empty,
                RedirectTrackSourceId = persistedStatus?.RedirectTrackSourceId ?? string.Empty,
                RedirectReason = persistedStatus?.RedirectReason ?? string.Empty,
                WatchStatus = queueTask?.Status ?? string.Empty
            };
        }).ToList());
    }

    internal static PlaylistTrackLocationStatus ResolvePlaylistTrackLocationStatus(
        bool ignored,
        PlaylistWatchTrackStatusDto? persistedStatus,
        string? liveQueueStatus)
    {
        if (ignored)
        {
            return new PlaylistTrackLocationStatus("blocked", "Blocked", "Ignored or blocked by monitored playlist rules.");
        }

        var queueStatus = NormalizeStatusText(liveQueueStatus);
        var queueState = ResolveQueueLocationStatus(queueStatus);
        if (queueState != null && IsActiveQueueStatus(queueStatus))
        {
            return queueState;
        }

        var persistedState = ResolveCachedTrackLocationStatus(persistedStatus);
        if (persistedState.Status != "missing")
        {
            return persistedState;
        }

        return queueState ?? persistedState;
    }

    private static bool IsActiveQueueStatus(string status)
        => status is "queued" or "inqueue" or "pending" or "running" or "downloading" or "paused" or "retrying";

    private static string NormalizeStatusText(string? status)
        => string.IsNullOrWhiteSpace(status) ? string.Empty : status.Trim().ToLowerInvariant();

    private static DownloadQueueItem? ResolveCurrentQueueTask(
        IReadOnlyList<PlaylistWatchDownloadClaimDto>? claims,
        IReadOnlyDictionary<string, DownloadQueueItem> queueTasksByUuid)
    {
        var claimedTasks = claims?
            .Select(claim => queueTasksByUuid.GetValueOrDefault(claim.QueueUuid))
            .Where(static task => task != null)
            .Cast<DownloadQueueItem>()
            .ToList();
        return (claimedTasks ?? [])
            .Where(static task => ResolveQueueLocationStatus(NormalizeStatusText(task.Status)) != null)
            .OrderBy(static task => QueueStatusPriority(task.Status))
            .ThenByDescending(static task => task.UpdatedAt)
            .FirstOrDefault();
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

    internal sealed record PlaylistTrackLocationStatus(
        string Status,
        string Label,
        string Detail);

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

        if (status?.LocalTrackId.HasValue == true)
        {
            return new PlaylistTrackLocationStatus(
                "library",
                "In Library",
                "Available in the local library.");
        }

        if (syncStatus == "mapping_retry")
        {
            return new PlaylistTrackLocationStatus(
                "mapping_retry",
                "Mapping retry",
                status?.IdentityReason ?? "Waiting for a Deezer mapping before this track can be queued.");
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
                    "missing",
                    "Missing",
                    "Not present in the indexed library.");
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

        var queueState = ResolveQueueLocationStatus(normalized);
        if (queueState != null)
        {
            return queueState;
        }

        return new PlaylistTrackLocationStatus("missing", "Missing", "Not downloaded and not currently queued.");
    }

    private static object MapCachedPlaylistTrack(
        string source,
        PlaylistTrackCandidate candidate,
        int index,
        IReadOnlyDictionary<string, PlaylistWatchTrackStatusDto> statusByTrackId)
    {
        var trackSourceId = candidate.TrackSourceId ?? string.Empty;
        statusByTrackId.TryGetValue(trackSourceId, out var watchStatus);
        var locationStatus = ResolveCachedTrackLocationStatus(watchStatus);
        var sourceUrl = !string.IsNullOrWhiteSpace(candidate.SourceUrl)
            ? candidate.SourceUrl
            : BuildSourceTrackUrl(source, trackSourceId);
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

        var candidates = (await _playlistWatchReconciler.GetCachedPlaylistTrackCandidatesAsync(
                normalizedSource,
                sourceId,
                cancellationToken))
            .ToDictionary(candidate => candidate.TrackSourceId, StringComparer.OrdinalIgnoreCase);

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
        await _playlistWatchReconciler.UpdatePlaylistStateAsync(
            normalizedSource,
            sourceId,
            trackCount: null,
            snapshotId: null,
            state: WatchlistPlaylistState.Pending,
            lastRunMessage: "Manual runtime reset.",
            nextAttemptUtc: null,
            consecutiveFailures: 0,
            cancellationToken,
            touchLastChecked: false);
        await _repository.ClearPlaylistWatchTargetSyncStateAsync(normalizedSource, sourceId, cancellationToken);
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

}
