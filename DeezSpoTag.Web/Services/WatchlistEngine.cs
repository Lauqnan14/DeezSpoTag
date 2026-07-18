using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Core.Security;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Integrations.Tidal;
using DeezSpoTag.Services.Download.Shared.Models;
using HtmlAgilityPack;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using ApiPlaylist = DeezSpoTag.Core.Models.Deezer.ApiPlaylist;
using ApiTrack = DeezSpoTag.Core.Models.Deezer.ApiTrack;
using GwTrack = DeezSpoTag.Core.Models.Deezer.GwTrack;

namespace DeezSpoTag.Web.Services;

public sealed class ArtistWatchQueueOptions
{
    public required string CollectionName { get; init; }
    public required string CollectionType { get; init; }
    public long? DestinationFolderId { get; init; }
    public string? PreferredEngine { get; init; }
    public IReadOnlyList<PlaylistTrackRoutingRule>? RoutingRules { get; init; }
    public string? DownloadVariantMode { get; init; }
    public long? AtmosDestinationFolderId { get; init; }
    public IReadOnlyList<PlaylistTrackBlockRule>? BlockRules { get; init; }
}

public sealed record ArtistWatchQueueOutcome(
    int Requested,
    int Queued,
    int AlreadyHandled,
    int Unavailable,
    int Deferred,
    int Failed)
{
    public int Settled => Queued + AlreadyHandled + Unavailable;
    public bool IsSettled => Settled >= Requested && Deferred == 0 && Failed == 0;
}

public sealed record PlaylistTrackCandidate(
    string TrackSourceId,
    string? Isrc,
    string Title,
    string Artist,
    string Album,
    int? ReleaseYear,
    int? DurationMs,
    bool? Explicit,
    IReadOnlyList<string> Genres,
    string? CoverUrl = null,
    string? DeezerId = null,
    string? MappingStatus = null,
    string? MappingError = null,
    string? SourceUrl = null);

public sealed record PlaylistReconciliationResult(
    bool Success,
    string Message,
    int SourceTracks,
    int MissingTracks,
    int IgnoredTracks,
    int LocalTracks,
    int QueuedTracks,
    int CompletedTracks,
    int FailedTracks,
    PlaylistSyncResult? SyncResult,
    bool Deferred = false,
    int AttemptedTracks = 0,
    int SystemicFailures = 0,
    string? FailureFingerprint = null,
    string? FailureMessage = null,
    string? QueueStopReason = null,
    int RemainingQueueableTracks = 0);

[SuppressMessage("Major Code Smell", "S1192", Justification = "Watch state/status literals are shared with persisted runtime values and external diagnostics.")]
internal sealed class WatchlistEngine
{
    private sealed record QueueWatchRuleSet(
        IReadOnlyList<PlaylistTrackRoutingRule>? RoutingRules,
        IReadOnlyList<PlaylistTrackBlockRule>? BlockRules);

    private readonly record struct AtmosQueueRequest(string SourceLabel, string TrackId, bool AfterPrimarySkip);
    private readonly record struct QueueWatchResult(
        int QueuedCount,
        int CompletedCount,
        int FailedCount,
        bool Deferred,
        int AttemptedCount,
        int SystemicFailureCount,
        string? FirstSystemicFailureFingerprint,
        string? FirstFailureMessage,
        WatchQueueStopReason StopReason,
        int RemainingQueueableCount,
        int UnavailableCount);

    private readonly record struct QueueWatchTrackResult(int QueuedCount, bool Completed, bool Failed, bool Unavailable);
    private readonly record struct WatchFailureClassification(bool IsSystemic, string? Fingerprint, string? Message);
    private readonly record struct PlaylistTrackSelection(
        List<WatchIntentTrack> MissingTracks,
        int IgnoredCount,
        int LocalCount,
        int ClaimedCount,
        int BlockedCount,
        int RecoveredClaimCount,
        int UnavailableCount);
    private readonly record struct QueuedWatchIntentContext(
        DownloadIntentService IntentService,
        QueueWatchOptions Options,
        string NormalizedDownloadVariantMode);

    private const string SpotifySource = "spotify";
    private const string DeezerSource = "deezer";
    private const string SmartTracklistSource = "smarttracklist";
    private const string AppleSource = "apple";
    private const string BoomplaySource = "boomplay";
    private const string RecommendationsSource = "recommendations";
    private const string QobuzSource = "qobuz";
    private const string TidalSource = "tidal";
    private const string PlaylistWatchType = "playlist";
    private const string QueuedStatus = "queued";
    private const string CompletedStatus = "completed";
    private const string FailedStatus = "failed";
    private const string UnavailableStatus = "unavailable";
    private const int UnavailableRecheckDays = 7;
    private const string ArtistWatchOrigin = "artist";
    private const string PlaylistWatchOrigin = "playlist";
    private const string AlbumField = "album";
    private const string ArtistField = "artist";
    private const string JsonTitleProperty = "title";
    private const string JsonItemsProperty = "items";
    private const string JsonAlbumProperty = "album";
    private const string JsonArtistProperty = "artist";
    private const string SpotifyLabel = "Spotify";
    private const string DeezerLabel = "Deezer";
    private const string SpotifyHomeTrendingSourceId = "home-trending-songs";
    private const string SpotifyTrendingSongsSectionUri = "spotify:section:0JQ5DB5E8N831KzFzsBBQ2";
    private const int CompletePlaylistCandidateFetchCount = int.MaxValue;
    private const int SpotifyVirtualPlaylistCandidateLimit = 1000;
    private const int MaximumProviderPlaylistPages = 10000;
    private static readonly string[] JsonStringObjectPropertyNames = ["standard", "short", "text"];
    private readonly LibraryRepository _libraryRepository;
    private readonly SpotifyMetadataService _spotifyMetadataService;
    private readonly SpotifyPathfinderMetadataClient _spotifyPathfinderMetadataClient;
    private readonly SpotifyArtistService _spotifyArtistService;
    private readonly DeezerClient _deezerClient;
    private readonly DeezerGatewayService _deezerGatewayService;
    private readonly AppleMusicCatalogService _appleCatalogService;
    private readonly BoomplayMetadataService _boomplayMetadataService;
    private readonly BoomplayWatchlistMappingService _boomplayWatchlistMappingService;
    private readonly PlatformAuthService? _platformAuthService;
    private readonly LibraryRecommendationService _libraryRecommendationService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ITidalAccessTokenProvider _tidalAccessTokenProvider;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly IServiceProvider _serviceProvider;
    private readonly PlaylistVisualService _playlistVisualService;
    private readonly WatchlistQueueAdmissionService _queueAdmission;
    private readonly WatchlistStateService _watchlistState;
    private readonly WatchlistHistoryService _watchlistHistory;
    private readonly IReadOnlyDictionary<string, IPlaylistSourceAdapter> _sourceAdapters;
    private readonly ILogger<WatchlistEngine> _logger;

    private sealed class QueueWatchOptionsInput
    {
        public required string SourceLabel { get; init; }
        public string? WatchlistSource { get; init; }
        public string? WatchlistPlaylistId { get; init; }
        public string? PreferredEngine { get; init; }
        public DownloadEngineOrderSettings? DownloadEngineOrder { get; init; }
        public string? DownloadVariantMode { get; init; }
        public long? AtmosDestinationFolderId { get; init; }
        public QueueWatchRuleSet? RuleSet { get; init; }
        public string? WatchlistOrigin { get; init; }
        public string? CandidateIdentityRevision { get; init; }
        public string? ProviderReadinessRevision { get; init; }
    }

    public sealed class PlaylistWatchPlatformServices
    {
        public required SpotifyMetadataService SpotifyMetadataService { get; init; }
        public required SpotifyPathfinderMetadataClient SpotifyPathfinderMetadataClient { get; init; }
        public required SpotifyArtistService SpotifyArtistService { get; init; }
        public required DeezerClient DeezerClient { get; init; }
        public required DeezerGatewayService DeezerGatewayService { get; init; }
        public required AppleMusicCatalogService AppleCatalogService { get; init; }
        public required BoomplayMetadataService BoomplayMetadataService { get; init; }
        internal required BoomplayWatchlistMappingService BoomplayWatchlistMappingService { get; init; }
        internal PlatformAuthService? PlatformAuthService { get; init; }
        public required LibraryRecommendationService LibraryRecommendationService { get; init; }
        public required IHttpClientFactory HttpClientFactory { get; init; }
        public required ITidalAccessTokenProvider TidalAccessTokenProvider { get; init; }
    }

    public sealed class PlaylistWatchRuntimeServices
    {
        public required PlaylistVisualService PlaylistVisualService { get; init; }
        public required WatchlistQueueAdmissionService? WatchlistQueueAdmissionService { get; init; }
        public required ActivitiesRealtimeService ActivitiesRealtimeService { get; init; }
        public WatchlistStateService? WatchlistStateService { get; init; }
        public WatchlistHistoryService? WatchlistHistoryService { get; init; }
    }

    public WatchlistEngine(
        LibraryRepository libraryRepository,
        PlaylistWatchPlatformServices platformServices,
        PlaylistWatchRuntimeServices runtimeServices,
        DeezSpoTagSettingsService settingsService,
        IServiceProvider serviceProvider,
        ILogger<WatchlistEngine> logger)
    {
        _libraryRepository = libraryRepository;
        _spotifyMetadataService = platformServices.SpotifyMetadataService;
        _spotifyPathfinderMetadataClient = platformServices.SpotifyPathfinderMetadataClient;
        _spotifyArtistService = platformServices.SpotifyArtistService;
        _deezerClient = platformServices.DeezerClient;
        _deezerGatewayService = platformServices.DeezerGatewayService;
        _appleCatalogService = platformServices.AppleCatalogService;
        _boomplayMetadataService = platformServices.BoomplayMetadataService;
        _boomplayWatchlistMappingService = platformServices.BoomplayWatchlistMappingService;
        _platformAuthService = platformServices.PlatformAuthService;
        _libraryRecommendationService = platformServices.LibraryRecommendationService;
        _httpClientFactory = platformServices.HttpClientFactory;
        _tidalAccessTokenProvider = platformServices.TidalAccessTokenProvider;
        _settingsService = settingsService;
        _serviceProvider = serviceProvider;
        _playlistVisualService = runtimeServices.PlaylistVisualService;
        _queueAdmission = runtimeServices.WatchlistQueueAdmissionService ?? new WatchlistQueueAdmissionService();
        _watchlistState = runtimeServices.WatchlistStateService ?? new WatchlistStateService(libraryRepository);
        _watchlistHistory = runtimeServices.WatchlistHistoryService
            ?? new WatchlistHistoryService(libraryRepository, runtimeServices.ActivitiesRealtimeService);
        _sourceAdapters = CreateSourceAdapters();
        _logger = logger;
    }

    private sealed record LivePlaylistSnapshot(
        IReadOnlyList<PlaylistTrackCandidate> Candidates,
        string? SnapshotId,
        string? Name,
        string? Description,
        string? ImageUrl,
        int? TrackCount,
        bool IsComplete,
        bool IsAuthoritativeEmpty,
        bool CanClearImageUrl,
        string? OwnerName);

    private sealed record LivePlaylistSnapshotMetadata(
        string? SnapshotId = null,
        string? Name = null,
        string? Description = null,
        string? ImageUrl = null,
        int? TrackCount = null,
        bool IsComplete = true,
        bool IsAuthoritativeEmpty = false,
        bool CanClearImageUrl = false,
        string? OwnerName = null);

    [SuppressMessage("Major Code Smell", "S3776", Justification = "Playlist reconciliation intentionally preserves a linear execution flow for state persistence and queue/sync ordering.")]
    public async Task<PlaylistReconciliationResult> ReconcilePlaylistAsync(
        PlaylistWatchlistDto playlist,
        CancellationToken cancellationToken,
        bool forceMediaServerSync = false)
    {
        if (playlist == null)
        {
            return new PlaylistReconciliationResult(false, "Playlist not available.", 0, 0, 0, 0, 0, 0, 0, null);
        }

        var source = NormalizeWatchSource(playlist.Source);
        var sourceId = (playlist.SourceId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(sourceId))
        {
            return new PlaylistReconciliationResult(false, "Playlist source is not available.", 0, 0, 0, 0, 0, 0, 0, null);
        }
        await UpdatePlaylistStateAsync(
            source,
            sourceId,
            playlist.TrackCount ?? 0,
            playlist.SnapshotId,
            WatchlistPlaylistState.HeadFetching,
            "Fetching source snapshot head.",
            nextAttemptUtc: null,
            consecutiveFailures: 0,
            cancellationToken);

        var maxCandidates = CompletePlaylistCandidateFetchCount;
        var preference = await _libraryRepository.GetPlaylistWatchPreferenceAsync(source, sourceId, cancellationToken);
        var globalBlockRules = await GetGlobalPlaylistBlockRulesAsync(cancellationToken);
        var effectiveBlockRules = PlaylistTrackBlockRuleHelper.MergeRules(preference?.IgnoreRules, globalBlockRules);
        var existingCandidateCache = await _libraryRepository.GetPlaylistTrackCandidateCacheAsync(source, sourceId, cancellationToken);
        var providerReadinessRevision = await BuildProviderReadinessRevisionAsync(source);
        var settings = _settingsService.LoadSettings();

        var headSnapshot = await FetchLivePlaylistHeadAsync(source, sourceId, cancellationToken);
        var liveSnapshot = headSnapshot;
        var liveTrackCount = liveSnapshot.TrackCount ?? playlist.TrackCount ?? 0;

        var currentPlaylist = BuildCurrentPlaylistDto(playlist, source, sourceId, liveSnapshot, liveTrackCount);
        var managedImageUrl = _playlistVisualService == null
            ? currentPlaylist.ImageUrl
            : await _playlistVisualService.ResolveManagedVisualUrlAsync(
                source,
                sourceId,
                currentPlaylist.Name,
                currentPlaylist.ImageUrl,
                preference?.ReuseSavedArtwork == true,
                cancellationToken);
        if (!string.Equals(managedImageUrl, currentPlaylist.ImageUrl, StringComparison.Ordinal))
        {
            currentPlaylist = currentPlaylist with { ImageUrl = managedImageUrl };
        }

        var existingSnapshotId = NormalizeSnapshotId(existingCandidateCache?.SnapshotId);
        var currentSnapshotId = NormalizeSnapshotId(liveSnapshot.SnapshotId);
        var tryUseCachedCandidates = settings.WatchUseSnapshotIdChecking
            && SupportsStrictSnapshotReuse(source)
            && !string.IsNullOrWhiteSpace(currentSnapshotId)
            && string.Equals(existingSnapshotId, currentSnapshotId, StringComparison.Ordinal);
        if (forceMediaServerSync)
        {
            tryUseCachedCandidates = false;
        }

        IReadOnlyList<PlaylistTrackCandidate> candidates;
        if (tryUseCachedCandidates)
        {
            var cachedCandidates = existingCandidateCache == null
                ? null
                : TryDeserializePlaylistTrackCandidates(existingCandidateCache.CandidatesJson);
            var cachedCandidatesComplete = PlaylistCandidateContract.IsReusableCache(
                    source,
                    existingCandidateCache?.SchemaVersion ?? 0,
                    cachedCandidates,
                    liveSnapshot.TrackCount,
                    existingCandidateCache?.IsComplete == true)
                && string.Equals(
                    existingCandidateCache?.ProviderReadinessRevision,
                    providerReadinessRevision,
                    StringComparison.Ordinal);
            if (cachedCandidatesComplete)
            {
                candidates = cachedCandidates!;
                liveSnapshot = liveSnapshot with
                {
                    Candidates = candidates,
                    TrackCount = liveSnapshot.TrackCount ?? candidates.Count,
                    IsComplete = true
                };
                liveTrackCount = liveSnapshot.TrackCount ?? candidates.Count;
                await UpdatePlaylistStateAsync(
                    source,
                    sourceId,
                    liveTrackCount,
                    liveSnapshot.SnapshotId,
                    WatchlistPlaylistState.Unchanged,
                    "Snapshot unchanged. Reusing cached candidates.",
                    nextAttemptUtc: null,
                    consecutiveFailures: 0,
                    cancellationToken);
            }
            else
            {
                await UpdatePlaylistStateAsync(
                    source,
                    sourceId,
                    liveTrackCount,
                    liveSnapshot.SnapshotId,
                    WatchlistPlaylistState.Expanding,
                    "Snapshot unchanged but cached candidates are incomplete. Refreshing candidates.",
                    nextAttemptUtc: null,
                    consecutiveFailures: 0,
                    cancellationToken);
                liveSnapshot = await FetchLivePlaylistSnapshotAsync(source, sourceId, maxCandidates, cancellationToken);
                candidates = liveSnapshot.Candidates;
                liveTrackCount = liveSnapshot.TrackCount ?? candidates.Count;
            }
        }
        else
        {
            await UpdatePlaylistStateAsync(
                source,
                sourceId,
                liveTrackCount,
                liveSnapshot.SnapshotId,
                WatchlistPlaylistState.DeltaDetected,
                "Snapshot changed. Expanding candidates.",
                nextAttemptUtc: null,
                consecutiveFailures: 0,
                cancellationToken);
            liveSnapshot = await FetchLivePlaylistSnapshotAsync(source, sourceId, maxCandidates, cancellationToken);
            candidates = liveSnapshot.Candidates;
            liveTrackCount = liveSnapshot.TrackCount ?? candidates.Count;
        }

        if (!liveSnapshot.IsComplete || (candidates.Count == 0 && !liveSnapshot.IsAuthoritativeEmpty))
        {
            var sourceFailureMessage = candidates.Count == 0
                ? "Playlist source returned an unverified empty snapshot; the previous valid snapshot was preserved and reconciliation will retry."
                : "Playlist source returned a partial snapshot; the previous valid snapshot was preserved and reconciliation will retry.";
            await UpdatePlaylistStateAsync(
                source,
                sourceId,
                playlist.TrackCount ?? liveTrackCount,
                existingCandidateCache?.SnapshotId ?? liveSnapshot.SnapshotId,
                WatchlistPlaylistState.SourceFailure,
                sourceFailureMessage,
                nextAttemptUtc: null,
                consecutiveFailures: null,
                cancellationToken);
            return new PlaylistReconciliationResult(
                false,
                sourceFailureMessage,
                liveTrackCount,
                0,
                0,
                0,
                0,
                0,
                1,
                null,
                SystemicFailures: 1,
                FailureFingerprint: candidates.Count == 0 ? "source_unverified_empty" : "source_partial_snapshot",
                FailureMessage: sourceFailureMessage,
                QueueStopReason: WatchQueueStopReason.SystemicFailure.ToString(),
                RemainingQueueableTracks: playlist.TrackCount ?? liveTrackCount);
        }

        await _libraryRepository.UpdatePlaylistWatchlistMetadataAsync(
            source,
            sourceId,
            new PlaylistWatchlistMetadataInput(
                currentPlaylist.Name,
                currentPlaylist.ImageUrl,
                currentPlaylist.Description,
                liveTrackCount,
                liveSnapshot.CanClearImageUrl,
                currentPlaylist.OwnerName),
            cancellationToken);

        var candidatesJson = JsonSerializer.Serialize(candidates);
        var candidateIdentityRevision = PlaylistCandidateContract.BuildIdentityRevision(source, candidates);
        var candidateCacheComplete = liveSnapshot.IsComplete
            && candidates.All(candidate => PlaylistCandidateContract.IsResolvable(source, candidate));
        var sourceChanged = HasPlaylistSourceChanged(existingCandidateCache, liveSnapshot, candidatesJson);
        await UpdatePlaylistStateAsync(
            source,
            sourceId,
            liveTrackCount,
            liveSnapshot.SnapshotId,
            WatchlistPlaylistState.Reconciling,
            "Reconciling source snapshot.",
            nextAttemptUtc: null,
            consecutiveFailures: 0,
            cancellationToken);
        if (sourceChanged)
        {
            await AddPlaylistWatchHistoryStageAsync(
                source,
                sourceId,
                currentPlaylist.Name,
                liveTrackCount,
                WatchlistHistoryStatus.MetadataRefreshed,
                cancellationToken);
            await AddPlaylistWatchHistoryStageAsync(
                source,
                sourceId,
                currentPlaylist.Name,
                liveTrackCount,
                WatchlistHistoryStatus.SourceUpdated,
                cancellationToken);
        }
        if (!HasDownloadDestination(preference))
        {
            const string configurationMessage = "Select a destination folder before Watchlist can plan downloads.";
            await UpdatePlaylistStateAsync(
                source,
                sourceId,
                liveTrackCount,
                liveSnapshot.SnapshotId,
                WatchlistPlaylistState.ConfigurationRequired,
                configurationMessage,
                nextAttemptUtc: null,
                consecutiveFailures: 0,
                cancellationToken);
            return new PlaylistReconciliationResult(
                false,
                configurationMessage,
                liveTrackCount,
                0,
                0,
                0,
                0,
                0,
                0,
                null,
                QueueStopReason: WatchQueueStopReason.TrackDeferred.ToString(),
                RemainingQueueableTracks: liveTrackCount);
        }

        if (candidates.Count == 0)
        {
            if (!liveSnapshot.IsComplete)
            {
                const string sourceIncompleteMessage = "Playlist source did not return a complete snapshot; reconciliation will retry.";
                await UpdatePlaylistStateAsync(
                    source,
                    sourceId,
                    liveTrackCount,
                    liveSnapshot.SnapshotId,
                    WatchlistPlaylistState.SourceFailure,
                    sourceIncompleteMessage,
                    nextAttemptUtc: null,
                    consecutiveFailures: null,
                    cancellationToken);
                return new PlaylistReconciliationResult(
                    false,
                    sourceIncompleteMessage,
                    liveTrackCount,
                    0,
                    0,
                    0,
                    0,
                    0,
                    1,
                    null,
                    SystemicFailures: 1,
                    FailureFingerprint: "source_incomplete",
                    FailureMessage: sourceIncompleteMessage,
                    QueueStopReason: WatchQueueStopReason.SystemicFailure.ToString(),
                    RemainingQueueableTracks: liveTrackCount);
            }
            await UpdatePlaylistStateAsync(
                source,
                sourceId,
                liveTrackCount,
                liveSnapshot.SnapshotId,
                WatchlistPlaylistState.Completed,
                "No playlist tracks were available to reconcile.",
                nextAttemptUtc: null,
                consecutiveFailures: null,
                cancellationToken);
            return new PlaylistReconciliationResult(
                true,
                "No playlist tracks were available to reconcile.",
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                null,
                QueueStopReason: WatchQueueStopReason.Completed.ToString(),
                RemainingQueueableTracks: 0);
        }

        await _libraryRepository.UpsertPlaylistTrackCandidateCacheAsync(
            source,
            sourceId,
            liveSnapshot.SnapshotId,
            candidatesJson,
            PlaylistCandidateContract.CurrentCacheSchemaVersion,
            candidateIdentityRevision,
            providerReadinessRevision,
            candidateCacheComplete,
            cancellationToken);
        if (liveSnapshot.IsComplete)
        {
            await _libraryRepository.RemovePlaylistWatchTracksNotInAsync(
                source,
                sourceId,
                candidates.Select(candidate => candidate.TrackSourceId).ToList(),
                cancellationToken);
        }

        var queueOptions = BuildQueueWatchOptions(new QueueWatchOptionsInput
        {
            SourceLabel = ResolveSourceLabel(source),
            WatchlistSource = source,
            WatchlistPlaylistId = sourceId,
            PreferredEngine = preference?.PreferredEngine,
            DownloadEngineOrder = preference?.DownloadEngineOrder,
            DownloadVariantMode = preference?.DownloadVariantMode,
            AtmosDestinationFolderId = preference?.AtmosDestinationFolderId,
            RuleSet = new QueueWatchRuleSet(preference?.RoutingRules, effectiveBlockRules),
            WatchlistOrigin = PlaylistWatchOrigin,
            CandidateIdentityRevision = candidateIdentityRevision,
            ProviderReadinessRevision = providerReadinessRevision
        });

        var selection = await SelectMissingPlaylistTracksAsync(
            source,
            sourceId,
            candidates,
            preference?.DestinationFolderId,
            queueOptions,
            cancellationToken);
        await _libraryRepository.AddPlaylistWatchTracksAsync(
            source,
            sourceId,
            selection.MissingTracks
                .Select(track => new PlaylistWatchTrackInsert(track.TrackId, track.Isrc))
                .ToList(),
            cancellationToken);
        var queueResult = await QueueWatchIntentTracksAsync(
            selection.MissingTracks,
            preference?.DestinationFolderId,
            queueOptions,
            cancellationToken);
        await RecordPlaylistSelectionHistoryAsync(
            source,
            sourceId,
            currentPlaylist.Name,
            selection,
            cancellationToken);
        await UpdatePlaylistStateAsync(
            source,
            sourceId,
            liveTrackCount,
            liveSnapshot.SnapshotId,
            WatchlistPlaylistState.Queued,
            queueResult.QueuedCount > 0
                ? $"Queued {queueResult.QueuedCount} track(s)."
                : queueResult.UnavailableCount > 0
                    ? $"{queueResult.UnavailableCount} track(s) unavailable from enabled sources."
                    : "No tracks queued.",
            nextAttemptUtc: null,
            consecutiveFailures: 0,
            cancellationToken);
        await AddPlaylistWatchHistoryAsync(source, sourceId, currentPlaylist.Name, queueResult, cancellationToken);

        var targetJobs = await _libraryRepository.EnqueueWatchlistPlaylistSyncJobsAsync(
            source,
            sourceId,
            cancellationToken);
        if (targetJobs.Count > 0)
        {
            if (_serviceProvider.GetService<WatchlistPostDownloadSyncService>() is { } targetWorker)
            {
                await targetWorker.ResumePendingJobsAsync(cancellationToken);
            }
            await AddPlaylistWatchHistoryStageAsync(
                source,
                sourceId,
                currentPlaylist.Name,
                candidates.Count,
                WatchlistHistoryStatus.MediaSyncWaiting,
                cancellationToken);
        }

        if (queueResult.QueuedCount > 0)
        {
            await AddPlaylistWatchHistoryStageAsync(
                source,
                sourceId,
                currentPlaylist.Name,
                queueResult.QueuedCount,
                WatchlistHistoryStatus.MissingTracksQueued,
                cancellationToken);
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Playlist watch reconciled {Source}:{SourceId}. sourceTracks={SourceTracks}, ignored={IgnoredTracks}, local={LocalTracks}, unavailable={UnavailableTracks}, queued={QueuedTracks}, completed={CompletedTracks}, failed={FailedTracks}, deferred={Deferred}",
                source,
                sourceId,
                liveTrackCount,
                selection.IgnoredCount,
                selection.LocalCount,
                selection.UnavailableCount + queueResult.UnavailableCount,
                queueResult.QueuedCount,
                queueResult.CompletedCount,
                queueResult.FailedCount,
                queueResult.Deferred);
        }

        var success = queueResult.FailedCount == 0;
        var runStatus = ResolvePlaylistRunStatus(queueResult, success);
        var reconciliationMessage = ResolveReconciliationMessage(queueResult);
        await UpdatePlaylistStateAsync(
            source,
            sourceId,
            liveTrackCount,
            liveSnapshot.SnapshotId,
            WatchlistStateService.Parse(runStatus),
            reconciliationMessage,
            nextAttemptUtc: null,
            consecutiveFailures: null,
            cancellationToken);
        await _libraryRepository.UpdatePlaylistWatchPresentationSummaryAsync(
            source,
            sourceId,
            selection.IgnoredCount + selection.BlockedCount,
            CountReroutedTracks(candidates, preference?.RoutingRules),
            cancellationToken);
        return new PlaylistReconciliationResult(
            success,
            reconciliationMessage,
            liveTrackCount,
            selection.MissingTracks.Count,
            selection.IgnoredCount,
            selection.LocalCount,
            queueResult.QueuedCount,
            queueResult.CompletedCount,
            queueResult.FailedCount,
            null,
            Deferred: queueResult.Deferred,
            AttemptedTracks: queueResult.AttemptedCount,
            SystemicFailures: queueResult.SystemicFailureCount,
            FailureFingerprint: queueResult.FirstSystemicFailureFingerprint,
            FailureMessage: queueResult.FirstFailureMessage,
            QueueStopReason: queueResult.StopReason.ToString(),
            RemainingQueueableTracks: queueResult.RemainingQueueableCount);
    }

    private async Task RecordPlaylistSelectionHistoryAsync(
        string source,
        string sourceId,
        string playlistName,
        PlaylistTrackSelection selection,
        CancellationToken cancellationToken)
    {
        var stages = new (int Count, WatchlistHistoryStatus Status)[]
        {
            (selection.LocalCount, WatchlistHistoryStatus.SkippedAlreadyAvailable),
            (selection.ClaimedCount, WatchlistHistoryStatus.SkippedAlreadyQueued),
            (selection.RecoveredClaimCount, WatchlistHistoryStatus.StaleClaimRecovered),
            (selection.BlockedCount, WatchlistHistoryStatus.SkippedBlocked),
            (selection.UnavailableCount, WatchlistHistoryStatus.SkippedUnavailableRecheckWindow)
        };
        foreach (var (count, stage) in stages)
        {
            if (count > 0)
            {
                await AddPlaylistWatchHistoryStageAsync(
                    source,
                    sourceId,
                    playlistName,
                    count,
                    stage,
                    cancellationToken);
            }
        }
    }

    private static int CountReroutedTracks(
        IReadOnlyList<PlaylistTrackCandidate> candidates,
        IReadOnlyList<PlaylistTrackRoutingRule>? routingRules)
    {
        if (routingRules is not { Count: > 0 })
        {
            return 0;
        }

        return candidates.Count(candidate => routingRules.Any(rule =>
            PlaylistTrackBlockRuleMatcher.RuleMatches(
                new PlaylistTrackBlockRuleMatcher.TrackRuleMatchInput(
                    candidate.Title,
                    candidate.Artist,
                    candidate.Album,
                    candidate.Genres,
                    candidate.Explicit,
                    candidate.ReleaseYear?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                rule.ConditionField,
                rule.ConditionOperator,
                rule.ConditionValue)));
    }

    public async Task<PlaylistWatchlistDto> RefreshPlaylistMetadataOnlyAsync(
        PlaylistWatchlistDto playlist,
        CancellationToken cancellationToken,
        bool forceArtworkRefresh = false)
    {
        ArgumentNullException.ThrowIfNull(playlist);

        var source = NormalizeWatchSource(playlist.Source);
        var sourceId = (playlist.SourceId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(sourceId))
        {
            throw new InvalidOperationException("Playlist source is not available.");
        }

        var liveSnapshot = await FetchLivePlaylistSnapshotAsync(
            source,
            sourceId,
            CompletePlaylistCandidateFetchCount,
            cancellationToken);
        if (!HasUsableMetadataRefresh(playlist, liveSnapshot))
        {
            throw new InvalidOperationException($"Playlist source refresh returned no usable data for {source}:{sourceId}.");
        }

        var liveTrackCount = liveSnapshot.TrackCount ?? liveSnapshot.Candidates.Count;
        var currentPlaylist = BuildCurrentPlaylistDto(playlist, source, sourceId, liveSnapshot, liveTrackCount);
        var preference = await _libraryRepository.GetPlaylistWatchPreferenceAsync(source, sourceId, cancellationToken);
        var managedImageUrl = _playlistVisualService == null
            ? currentPlaylist.ImageUrl
            : await _playlistVisualService.ResolveManagedVisualUrlAsync(
                source,
                sourceId,
                currentPlaylist.Name,
                currentPlaylist.ImageUrl,
                preference?.ReuseSavedArtwork == true,
                cancellationToken,
                forceArtworkRefresh);
        if (!string.Equals(managedImageUrl, currentPlaylist.ImageUrl, StringComparison.Ordinal))
        {
            currentPlaylist = currentPlaylist with { ImageUrl = managedImageUrl };
        }

        await UpdatePlaylistStateAsync(
            source,
            sourceId,
            liveTrackCount,
            liveSnapshot.SnapshotId,
            WatchlistPlaylistState.MetadataRefreshed,
            "Metadata refreshed from source.",
            nextAttemptUtc: null,
            consecutiveFailures: 0,
            cancellationToken);
        await _libraryRepository.UpdatePlaylistWatchlistMetadataAsync(
            source,
            sourceId,
            new PlaylistWatchlistMetadataInput(
                currentPlaylist.Name,
                currentPlaylist.ImageUrl,
                currentPlaylist.Description,
                liveTrackCount,
                liveSnapshot.CanClearImageUrl,
                currentPlaylist.OwnerName),
            cancellationToken);

        if (liveSnapshot.Candidates.Count > 0)
        {
            await _libraryRepository.UpsertPlaylistTrackCandidateCacheAsync(
                source,
                sourceId,
                liveSnapshot.SnapshotId,
                JsonSerializer.Serialize(liveSnapshot.Candidates),
                cancellationToken);
        }

        return currentPlaylist;
    }

    private static bool HasUsableMetadataRefresh(PlaylistWatchlistDto playlist, LivePlaylistSnapshot liveSnapshot)
    {
        if (liveSnapshot.Candidates.Count > 0)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(liveSnapshot.Name)
            || !string.IsNullOrWhiteSpace(liveSnapshot.Description)
            || !string.IsNullOrWhiteSpace(liveSnapshot.ImageUrl)
            || !string.IsNullOrWhiteSpace(liveSnapshot.SnapshotId))
        {
            return true;
        }

        return playlist.TrackCount.GetValueOrDefault() == 0;
    }

    private static bool HasDownloadDestination(PlaylistWatchPreferenceDto? preference)
        => preference?.DestinationFolderId is > 0
            || preference?.RoutingRules?.Any(static rule => rule.DestinationFolderId > 0) == true;

    private static PlaylistWatchlistDto BuildCurrentPlaylistDto(
        PlaylistWatchlistDto playlist,
        string source,
        string sourceId,
        LivePlaylistSnapshot liveSnapshot,
        int liveTrackCount)
    {
        var trustedMetadata = HasTrustedLivePlaylistMetadata(source, liveSnapshot);
        return playlist with
        {
            Source = source,
            SourceId = sourceId,
            Name = trustedMetadata && !string.IsNullOrWhiteSpace(liveSnapshot.Name) ? liveSnapshot.Name! : playlist.Name,
            ImageUrl = ResolveCurrentPlaylistImageUrl(playlist, liveSnapshot, trustedMetadata),
            Description = trustedMetadata && !string.IsNullOrWhiteSpace(liveSnapshot.Description) ? liveSnapshot.Description : playlist.Description,
            TrackCount = liveTrackCount,
            OwnerName = trustedMetadata && !string.IsNullOrWhiteSpace(liveSnapshot.OwnerName) ? liveSnapshot.OwnerName : playlist.OwnerName
        };
    }

    private static string? ResolveCurrentPlaylistImageUrl(
        PlaylistWatchlistDto playlist,
        LivePlaylistSnapshot liveSnapshot,
        bool trustedMetadata)
    {
        if (!trustedMetadata)
        {
            return playlist.ImageUrl;
        }

        if (liveSnapshot.CanClearImageUrl)
        {
            return EmptyToNull(liveSnapshot.ImageUrl);
        }

        return string.IsNullOrWhiteSpace(liveSnapshot.ImageUrl) ? playlist.ImageUrl : liveSnapshot.ImageUrl;
    }

    private static bool HasTrustedLivePlaylistMetadata(string source, LivePlaylistSnapshot liveSnapshot)
    {
        if (!string.Equals(source, SpotifySource, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (SpotifyMetadataService.IsGenericSpotifyPlaylistName(liveSnapshot.Name))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(liveSnapshot.ImageUrl)
            || !string.IsNullOrWhiteSpace(liveSnapshot.Description)
            || liveSnapshot.TrackCount is > 0
            || !string.IsNullOrWhiteSpace(liveSnapshot.SnapshotId);
    }

    private static bool HasPlaylistSourceChanged(
        PlaylistTrackCandidateCacheDto? existingCandidateCache,
        LivePlaylistSnapshot liveSnapshot,
        string candidatesJson)
    {
        if (existingCandidateCache is null)
        {
            return true;
        }

        var previousSnapshotId = NormalizeSnapshotId(existingCandidateCache.SnapshotId);
        var currentSnapshotId = NormalizeSnapshotId(liveSnapshot.SnapshotId);
        if (!string.IsNullOrWhiteSpace(currentSnapshotId)
            && !string.Equals(previousSnapshotId, currentSnapshotId, StringComparison.Ordinal))
        {
            return true;
        }

        return !string.Equals(existingCandidateCache.CandidatesJson, candidatesJson, StringComparison.Ordinal);
    }

    private async Task<PlaylistTrackSelection> SelectMissingPlaylistTracksAsync(
        string source,
        string sourceId,
        IReadOnlyList<PlaylistTrackCandidate> candidates,
        long? destinationFolderId,
        QueueWatchOptions queueOptions,
        CancellationToken cancellationToken)
    {
        var recoveredClaimCount = await RecoverInvalidPendingWatchClaimsAsync(source, sourceId, cancellationToken);
        var ignored = await _libraryRepository.GetPlaylistWatchIgnoredTrackIdsAsync(source, sourceId, cancellationToken);
        var unavailableSettingsFingerprint = BuildUnavailableSettingsFingerprint(queueOptions);
        await _libraryRepository.ClearPlaylistWatchUnavailableStatusesWithDifferentFingerprintAsync(
            source,
            sourceId,
            unavailableSettingsFingerprint,
            cancellationToken);
        var statusByTrackId = (await _libraryRepository.GetPlaylistWatchTrackStatusesAsync(source, sourceId, cancellationToken))
            .Where(static item => !string.IsNullOrWhiteSpace(item.TrackSourceId))
            .GroupBy(static item => item.TrackSourceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
        var dedupeService = _serviceProvider.GetRequiredService<DownloadDedupeService>();
        var normalizedPreferredEngine = NormalizePreferredEngine(queueOptions.PreferredEngine);
        var normalizedDownloadVariantMode = NormalizeDownloadVariantMode(queueOptions.DownloadVariantMode);
        var globalUnavailableStatuses = await _libraryRepository.GetGlobalPlaylistWatchTrackUnavailableStatusesAsync(
            source,
            candidates.Select(static candidate => new PlaylistWatchTrackInsert(candidate.TrackSourceId, candidate.Isrc)).ToList(),
            unavailableSettingsFingerprint,
            cancellationToken);
        var globalUnavailableByTrackId = globalUnavailableStatuses
            .Where(static item => !string.IsNullOrWhiteSpace(item.TrackSourceId))
            .GroupBy(static item => item.TrackSourceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
        var globalUnavailableByIsrc = globalUnavailableStatuses
            .Where(static item => !string.IsNullOrWhiteSpace(item.Isrc))
            .GroupBy(static item => item.Isrc!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
        var nowUtc = DateTimeOffset.UtcNow;
        var missingTracks = new List<WatchIntentTrack>();
        var ignoredCount = 0;
        var localCount = 0;
        var claimedCount = 0;
        var blockedCount = 0;
        var unavailableCount = 0;
        var candidateIndex = 0;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidateIndex++;
            await _libraryRepository.UpdatePlaylistWatchProgressAsync(
                source,
                sourceId,
                "selecting_tracks",
                candidateIndex,
                candidates.Count,
                cancellationToken);
            if (!PlaylistCandidateContract.IsResolvable(source, candidate))
            {
                continue;
            }
            if (ignored.Contains(candidate.TrackSourceId))
            {
                await TryMarkWatchTrackCompletedAsync(source, sourceId, candidate.TrackSourceId, cancellationToken);
                ignoredCount++;
                continue;
            }

            if (string.Equals(source, BoomplaySource, StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(candidate.DeezerId))
            {
                // A Boomplay membership is not a downloadable identity. Leave it pending so the
                // durable mapper can retry instead of recording a false source-unavailable result.
                continue;
            }

            if (statusByTrackId.TryGetValue(candidate.TrackSourceId, out var persistedStatus)
                && IsAvailabilityRecheckWindowActive(persistedStatus, unavailableSettingsFingerprint, nowUtc))
            {
                unavailableCount++;
                continue;
            }

            var candidateIsrc = candidate.Isrc ?? string.Empty;
            if ((globalUnavailableByTrackId.TryGetValue(candidate.TrackSourceId, out var globalUnavailableStatus)
                    || (!string.IsNullOrWhiteSpace(candidateIsrc)
                        && globalUnavailableByIsrc.TryGetValue(candidateIsrc, out globalUnavailableStatus)))
                && IsAvailabilityRecheckWindowActive(globalUnavailableStatus, unavailableSettingsFingerprint, nowUtc))
            {
                await TryMarkWatchTrackUnavailableAsync(
                    queueOptions,
                    candidate.TrackSourceId,
                    candidate.Isrc,
                    globalUnavailableStatus.UnavailableReason,
                    globalUnavailableStatus.UnavailableNextRecheckUtc,
                    cancellationToken);
                unavailableCount++;
                continue;
            }

            var watchTrack = BuildWatchIntentTrackFromCandidate(source, candidate);
            if (watchTrack != null)
            {
                var preparedIntent = PrepareWatchIntent(
                    watchTrack.Intent,
                    watchTrack.TrackId,
                    queueOptions,
                    destinationFolderId,
                    normalizedDownloadVariantMode,
                    normalizedPreferredEngine);
                var dedupeDecision = await dedupeService.CheckAsync(
                    DownloadDedupeService.FromDownloadIntent(
                        preparedIntent,
                        blockRules: queueOptions.BlockRules),
                    cancellationToken);
                if (!dedupeDecision.Allowed)
                {
                    var handled = await HandlePreQueueDedupeDecisionAsync(
                        source,
                        sourceId,
                        watchTrack,
                        preparedIntent,
                        queueOptions,
                        dedupeDecision,
                        cancellationToken);
                    localCount += handled.LocalCount;
                    claimedCount += handled.ClaimedCount;
                    blockedCount += handled.BlockedCount;
                    ignoredCount += handled.IgnoredCount;
                    if (handled.Handled)
                    {
                        continue;
                    }
                }

                missingTracks.Add(watchTrack);
            }
            else
            {
                await TryMarkWatchTrackUnavailableAsync(
                    queueOptions,
                    candidate.TrackSourceId,
                    candidate.Isrc,
                    "The source candidate did not contain a resolvable download identity.",
                    DateTimeOffset.UtcNow.AddDays(1),
                    cancellationToken);
                unavailableCount++;
            }
        }

        return new PlaylistTrackSelection(missingTracks, ignoredCount, localCount, claimedCount, blockedCount, recoveredClaimCount, unavailableCount);
    }

    private readonly record struct PreQueueDedupeHandledResult(
        bool Handled,
        int LocalCount,
        int ClaimedCount,
        int BlockedCount,
        int IgnoredCount);

    private static bool IsAvailabilityRecheckWindowActive(
        PlaylistWatchTrackStatusDto status,
        string currentSettingsFingerprint,
        DateTimeOffset nowUtc)
    {
        if (!string.Equals(status.Status, UnavailableStatus, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(
                status.UnavailableSettingsFingerprint ?? string.Empty,
                currentSettingsFingerprint,
                StringComparison.Ordinal))
        {
            return false;
        }

        return status.UnavailableNextRecheckUtc.HasValue
            && status.UnavailableNextRecheckUtc.Value > nowUtc;
    }

    private static string BuildUnavailableSettingsFingerprint(QueueWatchOptions options)
    {
        var sourceOrder = options.DownloadEngineOrder?.Engines?
            .Select(static engine => new
            {
                key = engine.Engine?.Trim().ToLowerInvariant() ?? string.Empty,
                enabled = engine.Enabled,
                qualities = engine.Qualities?
                    .Select(static quality => new
                    {
                        key = quality.Quality?.Trim().ToLowerInvariant() ?? string.Empty,
                        enabled = quality.Enabled
                    })
                    .ToList()
            })
            .ToList();
        var payload = JsonSerializer.Serialize(new
        {
            preferredEngine = NormalizePreferredEngine(options.PreferredEngine),
            downloadVariantMode = NormalizeDownloadVariantMode(options.DownloadVariantMode),
            atmosDestinationFolderId = options.AtmosDestinationFolderId,
            candidateIdentityRevision = options.CandidateIdentityRevision ?? PlaylistCandidateContract.ValidationRevision,
            providerReadinessRevision = options.ProviderReadinessRevision ?? string.Empty,
            sourceOrder
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private async Task<string> BuildProviderReadinessRevisionAsync(string source)
    {
        PlatformAuthState? auth = null;
        if (_platformAuthService != null)
        {
            try
            {
                auth = await _platformAuthService.LoadAsync();
            }
            catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
            {
                // A failed readiness read is itself a revision. It must not reuse an unavailable
                // decision produced under a known-good provider state.
            }
        }

        var normalizedSource = NormalizeWatchSource(source);
        var readiness = normalizedSource switch
        {
            SpotifySource => new
            {
                configured = auth?.Spotify != null,
                active = auth?.Spotify?.ActiveAccount ?? string.Empty,
                accountRevision = auth?.Spotify?.Accounts
                    .Where(account => string.Equals(account.Name, auth.Spotify.ActiveAccount, StringComparison.OrdinalIgnoreCase))
                    .Select(account => account.UpdatedAt.ToString("O"))
                    .FirstOrDefault() ?? string.Empty
            },
            BoomplaySource => new
            {
                configured = auth?.Boomplay != null,
                active = auth?.Boomplay?.SessionValid == true ? "valid" : "not_valid",
                accountRevision = auth?.Boomplay?.SavedAt?.ToString("O") ?? BoomplayWatchlistMappingService.MatcherVersion
            },
            AppleSource => new
            {
                configured = auth?.AppleMusic != null,
                active = auth?.AppleMusic?.WrapperReady == true ? "ready" : "not_ready",
                accountRevision = auth?.AppleMusic?.WrapperLoggedInAt?.ToString("O") ?? string.Empty
            },
            QobuzSource => new
            {
                configured = auth?.Qobuz != null,
                active = auth?.Qobuz?.AuthTokenValid == true ? "valid" : "not_valid",
                accountRevision = auth?.Qobuz?.AccountRefreshedAt?.ToString("O") ?? string.Empty
            },
            TidalSource => new
            {
                configured = auth?.Tidal != null,
                active = auth?.Tidal?.CredentialsValid == true ? "valid" : "not_valid",
                accountRevision = auth?.Tidal?.ValidatedAt?.ToString("O") ?? string.Empty
            },
            DeezerSource or SmartTracklistSource or RecommendationsSource => new
            {
                configured = _deezerClient.LoggedIn,
                active = _deezerClient.LoggedIn ? "logged_in" : "logged_out",
                accountRevision = string.Empty
            },
            _ => new { configured = true, active = "static", accountRevision = string.Empty }
        };
        var payload = JsonSerializer.Serialize(new { source = normalizedSource, readiness });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private async Task<PreQueueDedupeHandledResult> HandlePreQueueDedupeDecisionAsync(
        string source,
        string sourceId,
        WatchIntentTrack track,
        DownloadIntent intent,
        QueueWatchOptions queueOptions,
        DownloadDedupeDecision decision,
        CancellationToken cancellationToken)
    {
        var reasonCode = NormalizeReasonCode(decision.ReasonCode);
        switch (reasonCode)
        {
            case "queue_duplicate":
            case "queue_insert_ignored":
                return await TryHandleQueueDuplicateForWatchlistAsync(
                    source,
                    sourceId,
                    track,
                    intent,
                    queueOptions,
                    decision,
                    cancellationToken);

            case "library_duplicate":
            case "library_quality_not_higher":
                await TryMarkWatchTrackCompletedAsync(source, sourceId, track.TrackId, cancellationToken);
                return new PreQueueDedupeHandledResult(true, 1, 0, 0, 0);

            case "blocklist_match":
                await TryPersistWatchTrackIgnoreAsync(source, sourceId, track, cancellationToken);
                await TryMarkWatchTrackCompletedAsync(source, sourceId, track.TrackId, cancellationToken);
                return new PreQueueDedupeHandledResult(true, 0, 0, 1, 1);

            default:
                return new PreQueueDedupeHandledResult(false, 0, 0, 0, 0);
        }
    }

    private async Task<int> RecoverInvalidPendingWatchClaimsAsync(
        string source,
        string sourceId,
        CancellationToken cancellationToken)
    {
        var pendingClaims = await _libraryRepository.GetPlaylistWatchDownloadClaimsForPlaylistAsync(
            source,
            sourceId,
            status: "pending",
            cancellationToken);
        return await RecoverInvalidPendingWatchClaimsAsync(pendingClaims, cancellationToken);
    }

    public async Task<int> RecoverInvalidPendingWatchClaimsAsync(CancellationToken cancellationToken = default)
    {
        var pendingClaims = await _libraryRepository.GetAllPlaylistWatchDownloadClaimsAsync("pending", cancellationToken);
        var recovered = await RecoverInvalidPendingWatchClaimsAsync(pendingClaims, cancellationToken);
        await _libraryRepository.DeleteTerminalPlaylistWatchDownloadClaimsOlderThanAsync(
            DateTimeOffset.UtcNow.AddDays(-30),
            cancellationToken);
        return recovered;
    }

    private async Task<int> RecoverInvalidPendingWatchClaimsAsync(
        IReadOnlyList<PlaylistWatchDownloadClaimDto> pendingClaims,
        CancellationToken cancellationToken)
    {
        if (pendingClaims.Count == 0)
        {
            return 0;
        }

        var queueRepository = _serviceProvider.GetRequiredService<DownloadQueueRepository>();
        var recoveredTrackIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var claim in pendingClaims)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var queueItem = await queueRepository.GetByUuidAsync(claim.QueueUuid, cancellationToken);
            if (IsPendingWatchClaimStillOwnedByQueue(queueItem))
            {
                continue;
            }

            await _libraryRepository.UpdatePlaylistWatchDownloadClaimStatusAsync(
                claim.QueueUuid,
                claim.Source,
                claim.SourceId,
                claim.TrackSourceId,
                FailedStatus,
                cancellationToken);
            await TryMarkWatchTrackStatusAsync(
                claim.Source,
                claim.SourceId,
                claim.TrackSourceId,
                FailedStatus,
                cancellationToken);
            recoveredTrackIds.Add(claim.TrackSourceId);
        }

        if (recoveredTrackIds.Count > 0 && _logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Recovered {Count} stale pending watchlist download claim(s).",
                recoveredTrackIds.Count);
        }

        return recoveredTrackIds.Count;
    }

    private async Task<PreQueueDedupeHandledResult> TryHandleQueueDuplicateForWatchlistAsync(
        string source,
        string sourceId,
        WatchIntentTrack track,
        DownloadIntent intent,
        QueueWatchOptions queueOptions,
        DownloadDedupeDecision decision,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(decision.QueueUuid))
        {
            return new PreQueueDedupeHandledResult(false, 0, 0, 0, 0);
        }

        var queueRepository = _serviceProvider.GetRequiredService<DownloadQueueRepository>();
        var queueItem = await queueRepository.GetByUuidAsync(decision.QueueUuid, cancellationToken);
        if (IsPendingWatchClaimStillOwnedByQueue(queueItem))
        {
            await TryRecordWatchDownloadClaimsAsync(
                queueOptions,
                track.TrackId,
                new[] { decision.QueueUuid },
                intent.DestinationFolderId,
                cancellationToken);
            return new PreQueueDedupeHandledResult(true, 0, 1, 0, 0);
        }

        if (IsCompletedQueueStatus(queueItem?.Status))
        {
            var dedupeService = _serviceProvider.GetRequiredService<DownloadDedupeService>();
            var libraryDecision = await dedupeService.CheckLibraryPresenceAsync(
                DownloadDedupeService.FromDownloadIntent(
                    intent,
                    blockRules: queueOptions.BlockRules),
                cancellationToken);
            if (!libraryDecision.Allowed)
            {
                await TryMarkWatchTrackCompletedAsync(source, sourceId, track.TrackId, cancellationToken);
                await _libraryRepository.UpdatePlaylistWatchDownloadClaimStatusAsync(
                    decision.QueueUuid,
                    source,
                    sourceId,
                    track.TrackId,
                    CompletedStatus,
                    cancellationToken);
                return new PreQueueDedupeHandledResult(true, 1, 0, 0, 0);
            }
        }

        await _libraryRepository.UpdatePlaylistWatchDownloadClaimStatusAsync(
            decision.QueueUuid,
            source,
            sourceId,
            track.TrackId,
            FailedStatus,
            cancellationToken);
        await TryMarkWatchTrackStatusAsync(source, sourceId, track.TrackId, FailedStatus, cancellationToken);
        return new PreQueueDedupeHandledResult(false, 0, 0, 0, 0);
    }

    private static bool IsPendingWatchClaimStillOwnedByQueue(DownloadQueueItem? queueItem)
        => DownloadQueueRecoveryPolicy.IsWatchlistClaimOwnedByQueue(queueItem, DateTimeOffset.UtcNow);

    private static bool IsCompletedQueueStatus(string? status)
    {
        var normalized = NormalizeReasonCode(status);
        return normalized is "completed" or "complete";
    }

    private static string NormalizeReasonCode(string? reasonCode)
        => string.IsNullOrWhiteSpace(reasonCode) ? string.Empty : reasonCode.Trim().ToLowerInvariant();

    private static string ResolveReconciliationMessage(QueueWatchResult queueResult)
    {
        if (queueResult.QueuedCount > 0)
        {
            return queueResult.StopReason switch
            {
                WatchQueueStopReason.RunBudget => $"Queued {queueResult.QueuedCount} track(s); run budget reached.",
                _ => $"Queued {queueResult.QueuedCount} missing track(s)."
            };
        }

        if (queueResult.FailedCount > 0)
        {
            return ResolveQueueFailureMessage(queueResult);
        }

        if (queueResult.Deferred)
        {
            return queueResult.StopReason switch
            {
                WatchQueueStopReason.WatchlistDisabled => "Watchlist is disabled.",
                WatchQueueStopReason.DownloadGate => "Waiting for download/enrichment gate to clear.",
                WatchQueueStopReason.RunBudget => "Watchlist run budget reached.",
                WatchQueueStopReason.TrackDeferred => "Track queueing deferred by download gate.",
                _ => "Playlist queue deferred."
            };
        }

        if (queueResult.CompletedCount > 0 && queueResult.FailedCount == 0)
        {
            return "Playlist reconciled; missing tracks were already available, blocked, or claimed.";
        }

        if (queueResult.UnavailableCount > 0 && queueResult.FailedCount == 0)
        {
            return $"{queueResult.UnavailableCount} track(s) unavailable from enabled sources; availability recheck scheduled.";
        }

        return "Playlist reconciled.";
    }

    private static string ResolveQueueFailureMessage(QueueWatchResult queueResult)
    {
        if (!string.IsNullOrWhiteSpace(queueResult.FirstFailureMessage))
        {
            return queueResult.FirstFailureMessage!;
        }

        return queueResult.StopReason switch
        {
            WatchQueueStopReason.SystemicFailure => "Playlist queue stopped because the source returned a systemic failure.",
            WatchQueueStopReason.TrackFailures => "Playlist queue stopped because tracks could not be queued.",
            _ => $"Playlist queue stopped with reason: {queueResult.StopReason}."
        };
    }

    private async Task AddPlaylistWatchHistoryAsync(
        string source,
        string sourceId,
        string? playlistName,
        QueueWatchResult queueResult,
        CancellationToken cancellationToken)
    {
        var status = ResolveWatchHistoryStatus(queueResult);
        var trackCount = queueResult.QueuedCount > 0
            ? queueResult.QueuedCount
            : queueResult.CompletedCount + queueResult.FailedCount + queueResult.UnavailableCount;
        if (trackCount <= 0 && !queueResult.Deferred)
        {
            return;
        }

        await _watchlistHistory.RecordAsync(
            new WatchlistHistoryWrite(
                source,
                PlaylistWatchType,
                sourceId,
                WatchlistHistoryService.PlaylistItemKey(source, sourceId),
                string.IsNullOrWhiteSpace(playlistName) ? "Playlist" : playlistName,
                PlaylistWatchType,
                Math.Max(0, trackCount),
                status,
                ArtistName: null),
            cancellationToken);
    }

    private async Task AddPlaylistWatchHistoryStageAsync(
        string source,
        string sourceId,
        string? playlistName,
        int trackCount,
        WatchlistHistoryStatus status,
        CancellationToken cancellationToken)
    {
        await _watchlistHistory.RecordAsync(
            new WatchlistHistoryWrite(
                source,
                PlaylistWatchType,
                sourceId,
                WatchlistHistoryService.PlaylistItemKey(source, sourceId),
                string.IsNullOrWhiteSpace(playlistName) ? "Playlist" : playlistName,
                PlaylistWatchType,
                Math.Max(0, trackCount),
                status,
                ArtistName: null),
            cancellationToken);
    }

    private static WatchlistHistoryStatus ResolveWatchHistoryStatus(QueueWatchResult queueResult)
    {
        if (queueResult.Deferred)
        {
            return ResolveQueueStopHistoryStatus(queueResult.StopReason);
        }

        if (queueResult.FailedCount > 0)
        {
            return ResolveQueueStopHistoryStatus(queueResult.StopReason);
        }

        if (queueResult.UnavailableCount > 0)
        {
            return WatchlistHistoryStatus.Unavailable;
        }

        if (queueResult.QueuedCount > 0)
        {
            return WatchlistHistoryStatus.Queued;
        }

        return WatchlistHistoryStatus.Completed;
    }

    private static WatchlistHistoryStatus ResolveQueueStopHistoryStatus(WatchQueueStopReason stopReason)
        => stopReason switch
        {
            WatchQueueStopReason.WatchlistDisabled => WatchlistHistoryStatus.WatchlistDisabled,
            WatchQueueStopReason.DownloadGate => WatchlistHistoryStatus.MediaSyncDeferredQueueActive,
            WatchQueueStopReason.RunBudget => WatchlistHistoryStatus.QueueBudgetReached,
            WatchQueueStopReason.TrackDeferred => WatchlistHistoryStatus.TrackQueueDeferred,
            WatchQueueStopReason.SystemicFailure => WatchlistHistoryStatus.SourceFailure,
            WatchQueueStopReason.TrackFailures => WatchlistHistoryStatus.Failed,
            _ => WatchlistHistoryStatus.Deferred
        };

    private static string ResolvePlaylistRunStatus(QueueWatchResult queueResult, bool success)
    {
        if (!success)
        {
            if (queueResult.FailedCount > 0)
            {
                return FailedStatus;
            }

            return ResolveQueueStopStatus(queueResult.StopReason);
        }

        if (queueResult.Deferred)
        {
            return ResolveQueueStopStatus(queueResult.StopReason);
        }

        if (queueResult.QueuedCount > 0)
        {
            return QueuedStatus;
        }

        if (queueResult.UnavailableCount > 0)
        {
            return UnavailableStatus;
        }

        return CompletedStatus;
    }

    private static string ResolveQueueStopStatus(WatchQueueStopReason stopReason)
        => stopReason switch
        {
            WatchQueueStopReason.WatchlistDisabled => "watchlist_disabled",
            WatchQueueStopReason.DownloadGate => "media_sync_deferred_queue_active",
            WatchQueueStopReason.RunBudget => "queue_budget_reached",
            WatchQueueStopReason.TrackDeferred => "track_queue_deferred",
            WatchQueueStopReason.SystemicFailure => "source_failure",
            WatchQueueStopReason.TrackFailures => FailedStatus,
            _ => "deferred"
        };

    [SuppressMessage("Major Code Smell", "S107", Justification = "State touch requires explicit persisted fields to avoid accidental partial updates.")]
    public async Task UpdatePlaylistStateAsync(
        string source,
        string sourceId,
        int? trackCount,
        string? snapshotId,
        WatchlistPlaylistState state,
        string? lastRunMessage,
        DateTimeOffset? nextAttemptUtc,
        int? consecutiveFailures,
        CancellationToken cancellationToken,
        bool touchLastChecked = true)
    {
        await _watchlistState.TransitionPlaylistAsync(
            new WatchlistPlaylistStateTransition(
                source,
                sourceId,
                state,
                lastRunMessage,
                trackCount,
                snapshotId,
                nextAttemptUtc,
                consecutiveFailures,
                touchLastChecked),
            cancellationToken);
    }

    public async Task<IReadOnlyList<PlaylistTrackCandidate>> GetPlaylistTrackCandidatesAsync(
        string source,
        string sourceId,
        CancellationToken cancellationToken)
    {
        var normalizedSource = NormalizeWatchSource(source);
        var normalizedSourceId = (sourceId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedSource) || string.IsNullOrWhiteSpace(normalizedSourceId))
        {
            return Array.Empty<PlaylistTrackCandidate>();
        }

        if (!_libraryRepository.IsConfigured)
        {
            return (await FetchLivePlaylistSnapshotAsync(normalizedSource, normalizedSourceId, CompletePlaylistCandidateFetchCount, cancellationToken)).Candidates;
        }

        var isMonitored = await _libraryRepository.IsPlaylistWatchlistedAsync(normalizedSource, normalizedSourceId, cancellationToken);
        if (!isMonitored)
        {
            return (await FetchLivePlaylistSnapshotAsync(normalizedSource, normalizedSourceId, CompletePlaylistCandidateFetchCount, cancellationToken)).Candidates;
        }

        var settings = _settingsService.LoadSettings();
        if (!settings.WatchUseSnapshotIdChecking)
        {
            return (await FetchLivePlaylistSnapshotAsync(normalizedSource, normalizedSourceId, CompletePlaylistCandidateFetchCount, cancellationToken)).Candidates;
        }

        var watchState = await _libraryRepository.GetPlaylistWatchStateAsync(normalizedSource, normalizedSourceId, cancellationToken);
        var currentSnapshotId = NormalizeSnapshotId(watchState?.SnapshotId);
        if (string.IsNullOrWhiteSpace(currentSnapshotId)
            && (string.Equals(normalizedSource, QobuzSource, StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedSource, TidalSource, StringComparison.OrdinalIgnoreCase)))
        {
            return (await FetchLivePlaylistSnapshotAsync(normalizedSource, normalizedSourceId, CompletePlaylistCandidateFetchCount, cancellationToken)).Candidates;
        }

        var cached = await _libraryRepository.GetPlaylistTrackCandidateCacheAsync(normalizedSource, normalizedSourceId, cancellationToken);
        if (cached is not null)
        {
            var cachedSnapshotId = NormalizeSnapshotId(cached.SnapshotId);
            if (string.Equals(cachedSnapshotId, currentSnapshotId, StringComparison.Ordinal))
            {
                var cachedCandidates = TryDeserializePlaylistTrackCandidates(cached.CandidatesJson);
                var providerReadinessRevision = await BuildProviderReadinessRevisionAsync(normalizedSource);
                if (PlaylistCandidateContract.IsReusableCache(
                        normalizedSource,
                        cached.SchemaVersion,
                        cachedCandidates,
                        watchState?.TrackCount,
                        cached.IsComplete)
                    && string.Equals(cached.ProviderReadinessRevision, providerReadinessRevision, StringComparison.Ordinal))
                {
                    return cachedCandidates!;
                }

                _logger.LogWarning("Playlist candidate cache is incomplete or invalid. Regenerating for Source:SourceId.");
            }
        }

        var freshSnapshot = await FetchLivePlaylistSnapshotAsync(normalizedSource, normalizedSourceId, CompletePlaylistCandidateFetchCount, cancellationToken);
        var freshCandidates = freshSnapshot.Candidates;
        var freshComplete = freshSnapshot.IsComplete
                            && freshCandidates.All(candidate => PlaylistCandidateContract.IsResolvable(normalizedSource, candidate));
        if (!freshComplete)
        {
            return [];
        }
        var providerRevision = await BuildProviderReadinessRevisionAsync(normalizedSource);
        await _libraryRepository.UpsertPlaylistTrackCandidateCacheAsync(
            normalizedSource,
            normalizedSourceId,
            freshSnapshot.SnapshotId ?? currentSnapshotId,
            JsonSerializer.Serialize(freshCandidates),
            PlaylistCandidateContract.CurrentCacheSchemaVersion,
            PlaylistCandidateContract.BuildIdentityRevision(normalizedSource, freshCandidates),
            providerRevision,
            isComplete: true,
            cancellationToken);
        return freshCandidates;
    }

    public async Task<IReadOnlyList<PlaylistTrackCandidate>> GetCachedPlaylistTrackCandidatesAsync(
        string source,
        string sourceId,
        CancellationToken cancellationToken)
    {
        var normalizedSource = NormalizeWatchSource(source);
        var normalizedSourceId = (sourceId ?? string.Empty).Trim();
        if (!_libraryRepository.IsConfigured
            || string.IsNullOrWhiteSpace(normalizedSource)
            || string.IsNullOrWhiteSpace(normalizedSourceId))
        {
            return [];
        }

        var cached = await _libraryRepository.GetPlaylistTrackCandidateCacheAsync(
            normalizedSource,
            normalizedSourceId,
            cancellationToken);
        if (cached == null)
        {
            return [];
        }
        var candidates = TryDeserializePlaylistTrackCandidates(cached.CandidatesJson);
        var providerReadinessRevision = await BuildProviderReadinessRevisionAsync(normalizedSource);
        return PlaylistCandidateContract.IsReusableCache(
                   normalizedSource,
                   cached.SchemaVersion,
                   candidates,
                   expectedTrackCount: null,
                   cached.IsComplete)
               && string.Equals(cached.ProviderReadinessRevision, providerReadinessRevision, StringComparison.Ordinal)
            ? candidates!
            : [];
    }

    private async Task<LivePlaylistSnapshot> FetchLivePlaylistSnapshotAsync(
        string normalizedSource,
        string normalizedSourceId,
        int maxCandidates,
        CancellationToken cancellationToken)
    {
        if (!_sourceAdapters.TryGetValue(normalizedSource, out var adapter))
        {
            return BuildLivePlaylistSnapshot(Array.Empty<PlaylistTrackCandidate>());
        }

        return await adapter.FetchAsync(normalizedSourceId, maxCandidates, cancellationToken);
    }

    private async Task<LivePlaylistSnapshot> FetchLivePlaylistHeadAsync(
        string normalizedSource,
        string normalizedSourceId,
        CancellationToken cancellationToken)
    {
        if (!_sourceAdapters.TryGetValue(normalizedSource, out var adapter))
        {
            return BuildLivePlaylistSnapshot(Array.Empty<PlaylistTrackCandidate>(), new LivePlaylistSnapshotMetadata(IsComplete: false));
        }

        var metadata = await adapter.FetchHeadAsync(normalizedSourceId, cancellationToken);

        return BuildLivePlaylistSnapshot(Array.Empty<PlaylistTrackCandidate>(), metadata with { IsComplete = false });
    }

    private IReadOnlyDictionary<string, IPlaylistSourceAdapter> CreateSourceAdapters()
        => new Dictionary<string, IPlaylistSourceAdapter>(StringComparer.OrdinalIgnoreCase)
        {
            [SpotifySource] = new PlaylistSourceAdapter(
                SpotifySource,
                GetSpotifyPlaylistSnapshotAsync,
                GetSpotifySnapshotHeadAsync),
            [DeezerSource] = new PlaylistSourceAdapter(
                DeezerSource,
                async (id, _, token) => BuildLivePlaylistSnapshot(await GetDeezerTrackCandidatesAsync(id, token)),
                GetDeezerSnapshotHeadAsync),
            [SmartTracklistSource] = new PlaylistSourceAdapter(
                SmartTracklistSource,
                async (id, _, token) => await GetSmartTracklistSnapshotAsync(id, token),
                GetSmartTracklistSnapshotHeadAsync),
            [AppleSource] = new PlaylistSourceAdapter(
                AppleSource,
                async (id, _, token) => await GetAppleSnapshotAsync(id, token),
                GetAppleSnapshotHeadAsync),
            [BoomplaySource] = new PlaylistSourceAdapter(
                BoomplaySource,
                async (id, _, token) => await GetBoomplaySnapshotAsync(id, token),
                GetBoomplaySnapshotHeadAsync),
            [RecommendationsSource] = new PlaylistSourceAdapter(
                RecommendationsSource,
                async (id, _, token) => BuildLivePlaylistSnapshot(await GetRecommendationTrackCandidatesAsync(id, token)),
                static (_, _) => Task.FromResult(new LivePlaylistSnapshotMetadata())),
            [QobuzSource] = new PlaylistSourceAdapter(
                QobuzSource,
                async (id, _, token) => BuildLivePlaylistSnapshot(await GetQobuzTrackCandidatesAsync(id, token)),
                static (_, _) => Task.FromResult(new LivePlaylistSnapshotMetadata())),
            [TidalSource] = new PlaylistSourceAdapter(
                TidalSource,
                async (id, _, token) => await GetTidalSnapshotAsync(id, token),
                static (_, _) => Task.FromResult(new LivePlaylistSnapshotMetadata()))
        };

    private async Task<LivePlaylistSnapshotMetadata> GetSpotifySnapshotHeadAsync(
        string sourceId,
        CancellationToken cancellationToken)
    {
        var metadata = await _spotifyMetadataService.FetchPlaylistMetadataAsync(sourceId, cancellationToken);
        if (metadata == null)
        {
            return new LivePlaylistSnapshotMetadata();
        }

        return new LivePlaylistSnapshotMetadata(
            SnapshotId: NormalizeSnapshotId(metadata.SnapshotId),
            Name: EmptyToNull(metadata.Name),
            Description: EmptyToNull(metadata.Subtitle),
            ImageUrl: EmptyToNull(metadata.ImageUrl),
            TrackCount: metadata.TotalTracks,
            CanClearImageUrl: true,
            OwnerName: EmptyToNull(metadata.OwnerName));
    }

    private async Task<LivePlaylistSnapshotMetadata> GetDeezerSnapshotHeadAsync(
        string sourceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_deezerClient.LoggedIn)
        {
            return new LivePlaylistSnapshotMetadata();
        }

        var playlist = await _deezerClient.GetPlaylistAsync(sourceId);
        return new LivePlaylistSnapshotMetadata(
            SnapshotId: NormalizeSnapshotId(playlist?.Checksum),
            Name: EmptyToNull(playlist?.Title),
            Description: EmptyToNull(playlist?.Description),
            ImageUrl: EmptyToNull(playlist?.PictureXl ?? playlist?.PictureBig ?? playlist?.PictureMedium ?? playlist?.PictureSmall ?? playlist?.Picture),
            TrackCount: playlist?.NbTracks,
            CanClearImageUrl: true);
    }

    private static Task<LivePlaylistSnapshotMetadata> GetSmartTracklistSnapshotHeadAsync(
        string sourceId,
        CancellationToken cancellationToken)
    {
        _ = sourceId;
        _ = cancellationToken;
        // Strict correctness: no trustworthy native snapshot token exposed.
        // Force candidate expansion path every run for this provider.
        return Task.FromResult(new LivePlaylistSnapshotMetadata());
    }

    private async Task<LivePlaylistSnapshotMetadata> GetAppleSnapshotHeadAsync(
        string sourceId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return new LivePlaylistSnapshotMetadata();
        }

        var settings = _settingsService.LoadSettings();
        var storefront = await _appleCatalogService.ResolveStorefrontAsync(
            settings.AppleMusic?.Storefront,
            settings.AppleMusic?.MediaUserToken,
            cancellationToken);

        using var doc = await _appleCatalogService.GetPlaylistAsync(
            sourceId,
            storefront,
            language: "en-US",
            cancellationToken,
            includeTracks: false);

        var root = doc.RootElement;
        if (!root.TryGetProperty("data", out var dataArr)
            || dataArr.ValueKind != JsonValueKind.Array
            || dataArr.GetArrayLength() == 0)
        {
            return new LivePlaylistSnapshotMetadata();
        }

        var playlist = dataArr[0];
        if (!playlist.TryGetProperty("attributes", out var attributes)
            || attributes.ValueKind != JsonValueKind.Object)
        {
            return new LivePlaylistSnapshotMetadata();
        }

        var versionHash = GetJsonString(attributes, "versionHash");
        var lastModified = GetJsonString(attributes, "lastModifiedDate");
        var trackCount = GetJsonInt(attributes, "trackCount");
        var snapshotId = NormalizeSnapshotId(
            !string.IsNullOrWhiteSpace(versionHash)
                ? versionHash
                : $"{lastModified}|{trackCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0"}");

        return new LivePlaylistSnapshotMetadata(
            SnapshotId: snapshotId,
            Name: EmptyToNull(GetJsonString(attributes, "name")),
            Description: EmptyToNull(GetJsonString(attributes, "description")),
            ImageUrl: EmptyToNull(ResolveAppleArtworkUrl(attributes)),
            TrackCount: trackCount,
            CanClearImageUrl: true);
    }

    private async Task<LivePlaylistSnapshotMetadata> GetBoomplaySnapshotHeadAsync(
        string sourceId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return new LivePlaylistSnapshotMetadata();
        }

        var playlist = string.Equals(sourceId, "trending-songs", StringComparison.OrdinalIgnoreCase)
            ? await _boomplayMetadataService.GetTrendingSongsAsync(includeTracks: false, cancellationToken)
            : await _boomplayMetadataService.GetPlaylistAsync(sourceId, cancellationToken);
        if (playlist == null)
        {
            return new LivePlaylistSnapshotMetadata();
        }

        var joinedTrackIds = playlist.TrackIds.Count == 0
            ? string.Empty
            : string.Join('|', playlist.TrackIds.Where(static id => !string.IsNullOrWhiteSpace(id)).Select(static id => id.Trim()));
        var hashPayload = $"{playlist.Id}|{playlist.TrackIds.Count}|{joinedTrackIds}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashPayload)));

        return new LivePlaylistSnapshotMetadata(
            SnapshotId: NormalizeSnapshotId(hash),
            Name: EmptyToNull(playlist.Title),
            Description: EmptyToNull(playlist.Description),
            ImageUrl: EmptyToNull(playlist.ImageUrl),
            TrackCount: playlist.TrackIds.Count > 0 ? playlist.TrackIds.Count : (int?)null,
            CanClearImageUrl: true);
    }

    private static LivePlaylistSnapshot BuildLivePlaylistSnapshot(
        IReadOnlyList<PlaylistTrackCandidate> candidates,
        LivePlaylistSnapshotMetadata? metadata = null)
    {
        metadata ??= new LivePlaylistSnapshotMetadata();
        return new(
            candidates,
            NormalizeSnapshotId(metadata.SnapshotId),
            EmptyToNull(metadata.Name),
            EmptyToNull(metadata.Description),
            EmptyToNull(metadata.ImageUrl),
            metadata.TrackCount ?? candidates.Count,
            metadata.IsComplete,
            metadata.IsAuthoritativeEmpty,
            metadata.CanClearImageUrl,
            EmptyToNull(metadata.OwnerName));
    }

    private static IReadOnlyList<PlaylistTrackCandidate>? TryDeserializePlaylistTrackCandidates(string candidatesJson)
    {
        if (string.IsNullOrWhiteSpace(candidatesJson))
        {
            return Array.Empty<PlaylistTrackCandidate>();
        }

        try
        {
            var candidates = JsonSerializer.Deserialize<List<PlaylistTrackCandidate>>(candidatesJson);
            return candidates is null
                ? Array.Empty<PlaylistTrackCandidate>()
                : (IReadOnlyList<PlaylistTrackCandidate>)candidates;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? NormalizeSnapshotId(string? snapshotId)
    {
        return string.IsNullOrWhiteSpace(snapshotId) ? null : snapshotId.Trim();
    }

    private static bool SupportsStrictSnapshotReuse(string source)
    {
        return string.Equals(source, SpotifySource, StringComparison.OrdinalIgnoreCase)
               || string.Equals(source, DeezerSource, StringComparison.OrdinalIgnoreCase)
               || string.Equals(source, AppleSource, StringComparison.OrdinalIgnoreCase)
               || string.Equals(source, BoomplaySource, StringComparison.OrdinalIgnoreCase);
    }

    [SuppressMessage("Major Code Smell", "S3776", Justification = "Pagination and partial-failure handling are intentionally explicit for deterministic snapshot behavior.")]
    private async Task<LivePlaylistSnapshot> GetSpotifyPlaylistSnapshotAsync(
        string sourceId,
        int maxCandidates,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<PlaylistTrackCandidate>();

        var virtualSnapshot = await TryGetSpotifyVirtualPlaylistSnapshotAsync(
            sourceId,
            candidates,
            seen,
            maxCandidates,
            cancellationToken);
        if (virtualSnapshot is not null)
        {
            return virtualSnapshot;
        }

        var sourceMetadata = await _spotifyMetadataService.FetchPlaylistMetadataAsync(sourceId, cancellationToken);
        var metadata = new SpotifyPlaylistPageMetadata(
            sourceMetadata?.SnapshotId,
            sourceMetadata?.Name,
            sourceMetadata?.Subtitle,
            sourceMetadata?.ImageUrl,
            sourceMetadata?.TotalTracks,
            sourceMetadata?.OwnerName);
        var pageSize = Math.Min(100, maxCandidates);
        var offset = 0;
        var isComplete = true;
        var isAuthoritativeEmpty = false;
        var pageCount = 0;
        var seenPageFingerprints = new HashSet<string>(StringComparer.Ordinal);
        var safeSourceId = LogSanitizer.OneLine(sourceId, maxLength: 128);
        var spotifyTrackSource = _settingsService.LoadSettings().SpotifyPlaylistTrackSource;

        while (candidates.Count < maxCandidates)
        {
            if (++pageCount > MaximumProviderPlaylistPages)
            {
                isComplete = false;
                break;
            }
            SpotifyPlaylistPage? page;
            try
            {
                page = await _spotifyMetadataService.FetchPlaylistTrackPageAsync(
                    sourceId,
                    offset,
                    pageSize,
                    spotifyTrackSource,
                    hydrate: false,
                    cancellationToken);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning(
                        ex,
                        "Spotify playlist page fetch timed out for playlist {PlaylistId} at offset {Offset}; returning partial snapshot.",
                        safeSourceId,
                        offset);
                }
                isComplete = false;
                break;
            }
            catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning(
                        ex,
                        "Spotify playlist page fetch failed for playlist {PlaylistId} at offset {Offset}; returning partial snapshot.",
                        safeSourceId,
                        offset);
                }
                isComplete = false;
                break;
            }

            if (page == null)
            {
                isComplete = false;
                break;
            }

            metadata = ApplySpotifyPlaylistPageMetadata(page, metadata);

            if (page.Tracks.Count == 0)
            {
                isAuthoritativeEmpty = IsAuthoritativeEmptySpotifyPage(
                    offset,
                    page.Tracks.Count,
                    page.TotalTracks,
                    metadata.TotalTracks,
                    metadata.SnapshotId);
                if (!isAuthoritativeEmpty)
                {
                    isComplete = false;
                }
                break;
            }
            var pageFingerprint = string.Join('|',
                page.Tracks.Count,
                page.Tracks.FirstOrDefault()?.Id,
                page.Tracks.LastOrDefault()?.Id);
            if (!seenPageFingerprints.Add(pageFingerprint))
            {
                isComplete = false;
                break;
            }

            isComplete = AddSpotifyPlaylistPageCandidates(page, candidates, seen, maxCandidates, isComplete);

            if (!page.HasMore)
            {
                break;
            }

            offset += page.Tracks.Count;
            if (page.TotalTracks.HasValue && offset >= page.TotalTracks.Value)
            {
                break;
            }
        }

        if (metadata.TotalTracks.HasValue && candidates.Count < metadata.TotalTracks.Value)
        {
            isComplete = false;
        }

        return BuildLivePlaylistSnapshot(
            candidates,
            new LivePlaylistSnapshotMetadata(
                SnapshotId: metadata.SnapshotId,
                Name: metadata.Name,
                Description: metadata.Description,
                ImageUrl: metadata.ImageUrl,
                TrackCount: metadata.TotalTracks,
                IsComplete: isComplete,
                IsAuthoritativeEmpty: isAuthoritativeEmpty,
                CanClearImageUrl: true,
                OwnerName: metadata.OwnerName));
    }

    internal static bool IsAuthoritativeEmptySpotifyPage(
        int offset,
        int pageTrackCount,
        int? pageTotalTracks,
        int? metadataTotalTracks,
        string? snapshotId)
        => offset == 0
           && pageTrackCount == 0
           && pageTotalTracks == 0
           && metadataTotalTracks == 0
           && !string.IsNullOrWhiteSpace(snapshotId);

    private async Task<LivePlaylistSnapshot?> TryGetSpotifyVirtualPlaylistSnapshotAsync(
        string sourceId,
        List<PlaylistTrackCandidate> candidates,
        HashSet<string> seen,
        int maxCandidates,
        CancellationToken cancellationToken)
    {
        if (IsSpotifyHomeTrendingSourceId(sourceId))
        {
            await AddSpotifyHomeTrendingCandidatesAsync(
                candidates,
                seen,
                Math.Min(maxCandidates, SpotifyVirtualPlaylistCandidateLimit),
                cancellationToken);
            return BuildLivePlaylistSnapshot(candidates);
        }

        if (TryGetSpotifyArtistTopTracksSourceId(sourceId, out var artistId))
        {
            await AddSpotifyArtistTopTrackCandidatesAsync(candidates, seen, artistId, cancellationToken);
            return BuildLivePlaylistSnapshot(candidates);
        }

        return null;
    }

    private static SpotifyPlaylistPageMetadata ApplySpotifyPlaylistPageMetadata(
        SpotifyPlaylistPage page,
        SpotifyPlaylistPageMetadata metadata)
    {
        return new SpotifyPlaylistPageMetadata(
            PreferSpotifyPlaylistMetadataValue(metadata.SnapshotId, page.SnapshotId, static value => !string.IsNullOrWhiteSpace(value)),
            PreferSpotifyPlaylistMetadataValue(metadata.Name, page.Name, static value => !SpotifyMetadataService.IsGenericSpotifyPlaylistName(value)),
            PreferSpotifyPlaylistMetadataValue(metadata.Description, page.Description, static value => !string.IsNullOrWhiteSpace(value)),
            PreferSpotifyPlaylistMetadataValue(metadata.ImageUrl, page.ImageUrl, static value => !string.IsNullOrWhiteSpace(value)),
            metadata.TotalTracks is > 0 ? metadata.TotalTracks : page.TotalTracks,
            metadata.OwnerName);
    }

    private static string? PreferSpotifyPlaylistMetadataValue(
        string? current,
        string? candidate,
        Func<string?, bool> isTrusted)
    {
        if (isTrusted(current))
        {
            return current;
        }

        return isTrusted(candidate) ? candidate : current;
    }

    private readonly record struct SpotifyPlaylistPageMetadata(
        string? SnapshotId,
        string? Name,
        string? Description,
        string? ImageUrl,
        int? TotalTracks,
        string? OwnerName);

    private static bool AddSpotifyPlaylistPageCandidates(
        SpotifyPlaylistPage page,
        List<PlaylistTrackCandidate> candidates,
        HashSet<string> seen,
        int maxCandidates,
        bool isComplete)
    {
        foreach (var track in page.Tracks)
        {
            AddSpotifyTrackCandidate(
                seen,
                candidates,
                new SpotifyTrackSeed(
                    track.Id,
                    track.Isrc,
                    track.Name,
                    track.Artists,
                    track.Album,
                    track.ReleaseDate,
                    track.DurationMs,
                    track.Explicit,
                    track.Genres,
                    track.ImageUrl));
            if (candidates.Count >= maxCandidates)
            {
                return !page.HasMore
                    && (!page.TotalTracks.HasValue || candidates.Count >= page.TotalTracks.Value);
            }
        }

        return isComplete;
    }

    private async Task AddSpotifyHomeTrendingCandidatesAsync(
        List<PlaylistTrackCandidate> candidates,
        HashSet<string> seen,
        int maxCandidates,
        CancellationToken cancellationToken)
    {
        var tracks = await _spotifyPathfinderMetadataClient.FetchBrowseSectionTrackSummariesWithBlobAsync(
            SpotifyTrendingSongsSectionUri,
            0,
            maxCandidates,
            cancellationToken);

        foreach (var track in tracks)
        {
            AddSpotifyTrackCandidate(
                seen,
                candidates,
                new SpotifyTrackSeed(
                    track.Id,
                    track.Isrc,
                    track.Name,
                    track.Artists,
                    track.Album,
                    track.ReleaseDate,
                    track.DurationMs,
                    track.Explicit,
                    track.Genres,
                    track.ImageUrl));
        }
    }

    private async Task AddSpotifyArtistTopTrackCandidatesAsync(
        List<PlaylistTrackCandidate> candidates,
        HashSet<string> seen,
        string artistId,
        CancellationToken cancellationToken)
    {
        var artistPage = await _spotifyArtistService.GetArtistPageBySpotifyIdAsync(
            artistId,
            artistId,
            forceRefresh: true,
            cancellationToken);

        var topTracks = artistPage?.TopTracks;
        if (topTracks == null)
        {
            return;
        }

        for (var i = 0; i < topTracks.Count; i++)
        {
            var track = topTracks[i];
            AddSpotifyTrackCandidate(
                seen,
                candidates,
                new SpotifyTrackSeed(
                    track.Id,
                    track.Isrc,
                    track.Name,
                    artistPage!.Artist?.Name,
                    track.AlbumName,
                    track.ReleaseDate,
                    track.DurationMs,
                    ExplicitFlag: null,
                    Genres: null));
        }
    }

    private static void AddSpotifyTrackCandidate(
        HashSet<string> seen,
        List<PlaylistTrackCandidate> candidates,
        SpotifyTrackSeed seed)
    {
        var normalizedId = (seed.TrackId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedId) || !seen.Add(normalizedId))
        {
            return;
        }

        int? releaseYear = null;
        if (TryParseReleaseYear(seed.ReleaseDate, out var parsedYear))
        {
            releaseYear = parsedYear;
        }

        candidates.Add(new PlaylistTrackCandidate(
            normalizedId,
            string.IsNullOrWhiteSpace(seed.Isrc) ? null : seed.Isrc.Trim(),
            seed.Title?.Trim() ?? string.Empty,
            seed.Artist?.Trim() ?? string.Empty,
            seed.Album?.Trim() ?? string.Empty,
            releaseYear,
            seed.DurationMs,
            seed.ExplicitFlag,
            NormalizeGenres(seed.Genres),
            EmptyToNull(seed.CoverUrl)));
    }

    private async Task<IReadOnlyList<PlaylistTrackCandidate>> GetDeezerTrackCandidatesAsync(
        string sourceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_deezerClient.LoggedIn)
        {
            throw new InvalidOperationException("Deezer login is required to read the monitored playlist.");
        }

        var tracks = await _deezerClient.GetPlaylistTracksAsync(sourceId);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<PlaylistTrackCandidate>(tracks.Count);

        foreach (var track in tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (track.SngId <= 0)
            {
                continue;
            }

            var trackId = track.SngId.ToString();
            if (!seen.Add(trackId))
            {
                continue;
            }

            candidates.Add(new PlaylistTrackCandidate(
                trackId,
                string.IsNullOrWhiteSpace(track.Isrc) ? null : track.Isrc.Trim(),
                track.SngTitle?.Trim() ?? string.Empty,
                track.ArtName?.Trim() ?? string.Empty,
                track.AlbTitle?.Trim() ?? string.Empty,
                ParseFirstYear(track.PhysicalReleaseDate, track.DigitalReleaseDate),
                track.Duration > 0 ? track.Duration * 1000 : null,
                track.ExplicitLyrics,
                Array.Empty<string>(),
                EmptyToNull(BuildDeezerCoverUrl(track.AlbPicture))));
        }

        return candidates;
    }

    private sealed record SpotifyTrackSeed(
        string? TrackId,
        string? Isrc,
        string? Title,
        string? Artist,
        string? Album,
        string? ReleaseDate,
        int? DurationMs,
        bool? ExplicitFlag,
        IReadOnlyList<string>? Genres,
        string? CoverUrl = null);

    private async Task<LivePlaylistSnapshot> GetSmartTracklistSnapshotAsync(
        string sourceId,
        CancellationToken cancellationToken)
    {
        if (!_deezerClient.LoggedIn)
        {
            throw new InvalidOperationException("Deezer login is required to read the monitored smart tracklist.");
        }

        var playlistData = await GetSmartTracklistWatchDataAsync(sourceId, cancellationToken);
        var candidates = MapWatchIntentTrackCandidates(playlistData?.Tracks);
        return BuildLivePlaylistSnapshot(
            candidates,
            new LivePlaylistSnapshotMetadata(
                Name: playlistData?.Name,
                Description: playlistData?.Description,
                ImageUrl: playlistData?.ImageUrl,
                TrackCount: playlistData?.TrackCount,
                IsComplete: playlistData != null,
                CanClearImageUrl: true));
    }

    private async Task<LivePlaylistSnapshot> GetAppleSnapshotAsync(
        string sourceId,
        CancellationToken cancellationToken)
    {
        var playlistData = await GetApplePlaylistWatchDataAsync(sourceId, cancellationToken);
        var candidates = MapWatchIntentTrackCandidates(playlistData?.Tracks);
        return BuildLivePlaylistSnapshot(
            candidates,
            new LivePlaylistSnapshotMetadata(
                Name: playlistData?.Name,
                Description: playlistData?.Description,
                ImageUrl: playlistData?.ImageUrl,
                TrackCount: playlistData?.TrackCount,
                IsComplete: playlistData != null,
                CanClearImageUrl: true));
    }

    private async Task<LivePlaylistSnapshot> GetBoomplaySnapshotAsync(
        string sourceId,
        CancellationToken cancellationToken)
    {
        var playlistData = await GetBoomplayPlaylistWatchDataAsync(sourceId, cancellationToken);
        var candidates = await MapBoomplayWatchIntentTrackCandidatesAsync(playlistData?.Tracks, cancellationToken);
        return BuildLivePlaylistSnapshot(
            candidates,
            new LivePlaylistSnapshotMetadata(
                Name: playlistData?.Name,
                Description: playlistData?.Description,
                ImageUrl: playlistData?.ImageUrl,
                TrackCount: playlistData?.TrackCount,
                IsComplete: playlistData != null,
                CanClearImageUrl: true));
    }

    private async Task<IReadOnlyList<PlaylistTrackCandidate>> MapBoomplayWatchIntentTrackCandidatesAsync(
        IReadOnlyCollection<WatchIntentTrack>? tracks,
        CancellationToken cancellationToken)
    {
        if (tracks == null || tracks.Count == 0)
        {
            return Array.Empty<PlaylistTrackCandidate>();
        }

        var uniqueTracks = tracks
            .Where(static track => !string.IsNullOrWhiteSpace(track.TrackId))
            .GroupBy(static track => track.TrackId.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToList();
        var mapped = await _boomplayWatchlistMappingService.ResolveTracksAsync(
            uniqueTracks.Select(static track => new BoomplayWatchlistTrackInput(
                track.TrackId.Trim(),
                EmptyToNull(track.Intent.SourceUrl),
                EmptyToNull(track.Intent.Title),
                EmptyToNull(track.Intent.Artist),
                EmptyToNull(track.Intent.Album),
                EmptyToNull(track.Isrc),
                track.Intent.DurationMs > 0 ? track.Intent.DurationMs : null,
                EmptyToNull(track.Intent.Cover))).ToList(),
            cancellationToken);

        return mapped.Select((track, index) => new PlaylistTrackCandidate(
                track.BoomplayTrackId,
                EmptyToNull(track.Isrc),
                track.Title,
                track.Artist,
                track.Album,
                ReleaseYear: null,
                track.DurationMs,
                Explicit: null,
                Genres: Array.Empty<string>(),
                track.CoverUrl,
                track.DeezerTrackId,
                track.MappingStatus,
                track.MappingError,
                EmptyToNull(uniqueTracks[index].Intent.SourceUrl)))
            .ToList();
    }

    private async Task<IReadOnlyList<PlaylistTrackCandidate>> GetRecommendationTrackCandidatesAsync(
        string sourceId,
        CancellationToken cancellationToken)
    {
        var resolvedLibraryId = 0L;
        if (!TryParseRecommendationLibraryId(sourceId, out resolvedLibraryId))
        {
            var libraries = await _libraryRepository.GetLibrariesAsync(cancellationToken);
            resolvedLibraryId = libraries.Count > 0 ? libraries[0].Id : 0;
        }

        if (resolvedLibraryId <= 0)
        {
            return Array.Empty<PlaylistTrackCandidate>();
        }

        var detail = await _libraryRecommendationService.GetRecommendationsAsync(
            resolvedLibraryId,
            stationId: sourceId,
            limit: 200,
            cancellationToken: cancellationToken);
        if (detail == null || detail.Tracks.Count == 0)
        {
            return Array.Empty<PlaylistTrackCandidate>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<PlaylistTrackCandidate>(detail.Tracks.Count);
        foreach (var track in detail.Tracks)
        {
            var trackId = (track.Id ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trackId) || !seen.Add(trackId))
            {
                continue;
            }

            candidates.Add(new PlaylistTrackCandidate(
                trackId,
                string.IsNullOrWhiteSpace(track.Isrc) ? null : track.Isrc.Trim(),
                track.Title?.Trim() ?? string.Empty,
                track.Artist?.Name?.Trim() ?? string.Empty,
                track.Album?.Title?.Trim() ?? string.Empty,
                null,
                track.Duration > 0 ? track.Duration * 1000 : null,
                null,
                Array.Empty<string>()));
        }

        return candidates;
    }

    private async Task<LivePlaylistSnapshot> GetTidalSnapshotAsync(
        string sourceId,
        CancellationToken cancellationToken)
    {
        var playlistId = ResolveTidalPlaylistId(sourceId);
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return BuildLivePlaylistSnapshot(Array.Empty<PlaylistTrackCandidate>());
        }

        var token = await _tidalAccessTokenProvider.GetAccessTokenAsync(cancellationToken);
        var client = _httpClientFactory.CreateClient();
        var candidates = new List<PlaylistTrackCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var offset = 0;
        var total = int.MaxValue;
        var pageCount = 0;
        var isComplete = true;
        var seenPageFingerprints = new HashSet<string>(StringComparer.Ordinal);
        while (offset < total)
        {
            if (++pageCount > MaximumProviderPlaylistPages)
            {
                isComplete = false;
                break;
            }
            var page = await FetchTidalPlaylistItemsPageAsync(client, playlistId, token, offset, cancellationToken);
            if (page.Items.ValueKind != JsonValueKind.Array || page.Items.GetArrayLength() == 0)
            {
                break;
            }
            var pageFingerprint = string.Concat(
                page.Items.GetArrayLength(),
                ":",
                page.Items[0].GetRawText(),
                ":",
                page.Items[page.Items.GetArrayLength() - 1].GetRawText());
            if (!seenPageFingerprints.Add(pageFingerprint))
            {
                isComplete = false;
                break;
            }

            total = page.Total > 0 ? page.Total : total;
            foreach (var wrapper in page.Items.EnumerateArray())
            {
                TryAddTidalTrackCandidate(wrapper, seen, candidates);
            }

            offset += page.Items.GetArrayLength();
        }

        return BuildLivePlaylistSnapshot(
            candidates,
            new LivePlaylistSnapshotMetadata(
                TrackCount: total == int.MaxValue ? candidates.Count : total,
                IsComplete: isComplete && (total == int.MaxValue || offset >= total)));
    }

    private static async Task<TidalPlaylistItemsPage> FetchTidalPlaylistItemsPageAsync(
        HttpClient client,
        string playlistId,
        string token,
        int offset,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.tidal.com/v1/playlists/{Uri.EscapeDataString(playlistId)}/items?countryCode=US&limit=100&offset={offset}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return default;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = doc.RootElement;
        var total = root.TryGetProperty("totalNumberOfItems", out var totalElement)
            && totalElement.TryGetInt32(out var parsedTotal)
            ? parsedTotal
            : 0;
        var items = root.TryGetProperty(JsonItemsProperty, out var itemsElement)
            ? itemsElement.Clone()
            : default;
        return new TidalPlaylistItemsPage(items, total);
    }

    private static void TryAddTidalTrackCandidate(
        JsonElement wrapper,
        HashSet<string> seen,
        List<PlaylistTrackCandidate> candidates)
    {
        var track = wrapper.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object
            ? item
            : wrapper;
        var trackId = GetJsonString(track, "id");
        if (string.IsNullOrWhiteSpace(trackId) || !seen.Add(trackId))
        {
            return;
        }

        var album = track.TryGetProperty(JsonAlbumProperty, out var albumElement) && albumElement.ValueKind == JsonValueKind.Object
            ? albumElement
            : default;
        var duration = GetJsonInt(track, "duration");
        candidates.Add(new PlaylistTrackCandidate(
            trackId,
            EmptyToNull(GetJsonString(track, "isrc")),
            GetJsonString(track, JsonTitleProperty) ?? string.Empty,
            ResolveTidalArtistName(track),
            album.ValueKind == JsonValueKind.Object ? GetJsonString(album, JsonTitleProperty) ?? string.Empty : string.Empty,
            null,
            duration > 0 ? duration * 1000 : null,
            null,
            Array.Empty<string>()));
    }

    private async Task<IReadOnlyList<PlaylistTrackCandidate>> GetQobuzTrackCandidatesAsync(
        string sourceId,
        CancellationToken cancellationToken)
    {
        var playlistUrl = ResolveQobuzPlaylistUrl(sourceId);
        if (string.IsNullOrWhiteSpace(playlistUrl))
        {
            return Array.Empty<PlaylistTrackCandidate>();
        }

        var client = _httpClientFactory.CreateClient();
        using var response = await client.GetAsync(playlistUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return Array.Empty<PlaylistTrackCandidate>();
        }

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(html))
        {
            return Array.Empty<PlaylistTrackCandidate>();
        }

        var document = new HtmlDocument();
        document.LoadHtml(html);
        var rows = document.DocumentNode.SelectNodes("//div[contains(@class,'track') and @data-track]");
        if (rows == null || rows.Count == 0)
        {
            return Array.Empty<PlaylistTrackCandidate>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<PlaylistTrackCandidate>(rows.Count);
        foreach (var row in rows)
        {
            var trackId = row.GetAttributeValue("data-track", string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trackId) || !seen.Add(trackId))
            {
                continue;
            }

            var title = GetHtmlText(row, ".//div[contains(@class,'track__item--name')]");
            var artist = GetHtmlText(row, ".//span[contains(@class,'track__item--artist')]");
            var album = GetHtmlText(row, ".//span[contains(@class,'track__item--album')]");
            var durationText = GetHtmlText(row, ".//span[contains(@class,'track__item--duration')]");
            var durationSeconds = ParseClockDurationSeconds(durationText);
            candidates.Add(new PlaylistTrackCandidate(
                trackId,
                null,
                title,
                string.IsNullOrWhiteSpace(artist) ? "Unknown Artist" : artist,
                album,
                null,
                durationSeconds > 0 ? durationSeconds * 1000 : null,
                null,
                Array.Empty<string>()));
        }

        return candidates;
    }

    private static IReadOnlyList<PlaylistTrackCandidate> MapWatchIntentTrackCandidates(
        IReadOnlyCollection<WatchIntentTrack>? tracks)
    {
        if (tracks == null || tracks.Count == 0)
        {
            return Array.Empty<PlaylistTrackCandidate>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<PlaylistTrackCandidate>(tracks.Count);
        foreach (var track in tracks)
        {
            var trackId = (track.TrackId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trackId) || !seen.Add(trackId))
            {
                continue;
            }

            candidates.Add(new PlaylistTrackCandidate(
                trackId,
                string.IsNullOrWhiteSpace(track.Isrc) ? null : track.Isrc.Trim(),
                track.Intent.Title?.Trim() ?? string.Empty,
                track.Intent.Artist?.Trim() ?? string.Empty,
                track.Intent.Album?.Trim() ?? string.Empty,
                ParseFirstYear(track.Intent.ReleaseDate),
                track.Intent.DurationMs > 0 ? track.Intent.DurationMs : null,
                track.Intent.Explicit,
                NormalizeGenres(track.Intent.Genres),
                EmptyToNull(track.Intent.Cover)));
        }

        return candidates;
    }

    private static int? ParseFirstYear(params string?[] values)
    {
        return values
            .Select(TryParseReleaseYearNullable)
            .FirstOrDefault(static year => year.HasValue);
    }

    private static int? TryParseReleaseYearNullable(string? value)
        => TryParseReleaseYear(value, out var year) ? year : null;

    private static bool TryParseReleaseYear(string? value, out int year)
    {
        year = 0;
        var normalized = value?.Trim();
        return normalized is { Length: >= 4 } && int.TryParse(normalized[..4], out year);
    }

    private static IReadOnlyList<string> NormalizeGenres(IReadOnlyCollection<string>? genres)
    {
        if (genres == null || genres.Count == 0)
        {
            return Array.Empty<string>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>(genres.Count);
        foreach (var value in genres.Select(static genre => (genre ?? string.Empty).Trim()))
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (seen.Add(value))
            {
                normalized.Add(value);
            }
        }

        return normalized;
    }

    private async Task<IReadOnlyList<PlaylistTrackBlockRule>> GetGlobalPlaylistBlockRulesAsync(CancellationToken cancellationToken)
    {
        var preferences = await _libraryRepository.GetPlaylistWatchPreferencesAsync(cancellationToken);
        return PlaylistTrackBlockRuleHelper.BuildGlobalRules(preferences);
    }

    private async Task<SmartTracklistWatchData?> GetSmartTracklistWatchDataAsync(
        string smartTracklistId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(smartTracklistId))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var page = await _deezerGatewayService.GetSmartTracklistPageAsync(smartTracklistId);
        var results = page["results"] as JObject ?? page;
        var data = results["DATA"] as JObject ?? results["data"] as JObject;
        var songs = results["SONGS"] as JObject ?? results["songs"] as JObject;
        var songsData = songs?["data"] as JArray ?? songs?["DATA"] as JArray;
        if (data == null || songsData == null)
        {
            return null;
        }

        var title = data.Value<string>("TITLE")?.Trim();
        var description = data.Value<string>("DESCRIPTION");
        var cover = data["COVER"] as JObject;
        var coverMd5 = cover?.Value<string>("MD5")
            ?? cover?.Value<string>("md5")
            ?? data.Value<string>("COVER");
        var imageUrl = BuildDeezerCoverUrl(coverMd5);
        var tracks = new List<WatchIntentTrack>();

        foreach (var token in songsData)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (token is not JObject track)
            {
                continue;
            }

            var trackId = (track.Value<string>("SNG_ID")
                          ?? track.Value<string>("id")
                          ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trackId))
            {
                continue;
            }

            var isrc = track.Value<string>("ISRC")
                ?? track.Value<string>("isrc")
                ?? string.Empty;
            var artistName = track.Value<string>("ART_NAME")
                ?? track[JsonArtistProperty]?.Value<string>("name")
                ?? string.Empty;
            var albumTitle = track.Value<string>("ALB_TITLE")
                ?? track[AlbumField]?.Value<string>(JsonTitleProperty)
                ?? string.Empty;
            var albumCoverId = track.Value<string>("ALB_PICTURE")
                ?? track[AlbumField]?.Value<string>("md5_image")
                ?? track[AlbumField]?.Value<string>("cover");
            var durationSeconds = track.Value<int?>("DURATION")
                ?? track.Value<int?>("duration")
                ?? 0;
            var position = track.Value<int?>("TRACK_NUMBER")
                ?? track.Value<int?>("POSITION")
                ?? tracks.Count + 1;
            var coverUrl = BuildDeezerCoverUrl(albumCoverId);
            if (string.IsNullOrWhiteSpace(coverUrl))
            {
                coverUrl = imageUrl;
            }
            var intent = new DownloadIntent
            {
                SourceService = DeezerSource,
                SourceUrl = BuildDeezerTrackUrl(trackId),
                DeezerId = trackId,
                Isrc = isrc,
                Title = track.Value<string>("SNG_TITLE")
                    ?? track.Value<string>(JsonTitleProperty)
                    ?? string.Empty,
                Artist = artistName,
                Album = albumTitle,
                AlbumArtist = artistName,
                Cover = coverUrl,
                DurationMs = durationSeconds > 0 ? durationSeconds * 1000 : 0,
                Position = position,
                TrackNumber = position
            };

            tracks.Add(new WatchIntentTrack(trackId, isrc, intent));
        }

        var trackCount = data.Value<int?>("NB_SONG")
            ?? songsData.Count;
        return new SmartTracklistWatchData(
            string.IsNullOrWhiteSpace(title) ? "Smart Tracklist" : title,
            description,
            imageUrl,
            trackCount,
            tracks);
    }

private async Task<ApplePlaylistWatchData?> GetApplePlaylistWatchDataAsync(
        string playlistId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return null;
        }

        var settings = _settingsService.LoadSettings();
        var storefront = await _appleCatalogService.ResolveStorefrontAsync(
            settings.AppleMusic?.Storefront,
            settings.AppleMusic?.MediaUserToken,
            cancellationToken);

        using var doc = await _appleCatalogService.GetPlaylistAsync(
            playlistId,
            storefront,
            language: "en-US",
            cancellationToken);

        var root = doc.RootElement;
        if (!root.TryGetProperty("data", out var dataArr)
            || dataArr.ValueKind != JsonValueKind.Array
            || dataArr.GetArrayLength() == 0)
        {
            return null;
        }

        var playlist = dataArr[0];
        if (!playlist.TryGetProperty("attributes", out var attributes)
            || attributes.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var name = GetJsonString(attributes, "name") ?? "Apple Playlist";
        var description = GetJsonString(attributes, "description");
        var imageUrl = ResolveAppleArtworkUrl(attributes);
        int? trackCount = GetJsonInt(attributes, "trackCount");
        var tracks = new List<WatchIntentTrack>();

        if (TryGetApplePlaylistTracksData(playlist, out var tracksData))
        {
            foreach (var track in tracksData.EnumerateArray())
            {
                var watchTrack = BuildApplePlaylistWatchTrack(track, storefront, imageUrl);
                if (watchTrack is not null)
                {
                    tracks.Add(watchTrack);
                }
            }
        }

        if (!trackCount.HasValue)
        {
            trackCount = tracks.Count;
        }

        return new ApplePlaylistWatchData(name, description, imageUrl, trackCount, tracks);
    }

    private static bool TryGetApplePlaylistTracksData(JsonElement playlist, out JsonElement tracksData)
    {
        tracksData = default;
        return playlist.TryGetProperty("relationships", out var relationships)
               && relationships.ValueKind == JsonValueKind.Object
               && relationships.TryGetProperty("tracks", out var tracksRel)
               && tracksRel.ValueKind == JsonValueKind.Object
               && tracksRel.TryGetProperty("data", out tracksData)
               && tracksData.ValueKind == JsonValueKind.Array;
    }

    private static WatchIntentTrack? BuildApplePlaylistWatchTrack(
        JsonElement track,
        string storefront,
        string? fallbackImageUrl)
    {
        if (track.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var trackId = GetJsonString(track, "id")?.Trim();
        if (string.IsNullOrWhiteSpace(trackId))
        {
            return null;
        }

        if (!track.TryGetProperty("attributes", out var trackAttributes)
            || trackAttributes.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var sourceUrl = GetJsonString(trackAttributes, "url");
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            sourceUrl = $"https://music.apple.com/{storefront}/song/{trackId}?i={trackId}";
        }

        var intent = new DownloadIntent
        {
            SourceService = AppleSource,
            SourceUrl = sourceUrl ?? string.Empty,
            AppleId = trackId,
            Isrc = GetJsonString(trackAttributes, "isrc") ?? string.Empty,
            Title = GetJsonString(trackAttributes, "name") ?? string.Empty,
            Artist = GetJsonString(trackAttributes, "artistName") ?? string.Empty,
            Album = GetJsonString(trackAttributes, "albumName") ?? string.Empty,
            AlbumArtist = GetJsonString(trackAttributes, "artistName") ?? string.Empty,
            Cover = ResolveAppleArtworkUrl(trackAttributes) ?? fallbackImageUrl ?? string.Empty,
            DurationMs = GetJsonInt(trackAttributes, "durationInMillis") ?? 0,
            TrackNumber = GetJsonInt(trackAttributes, "trackNumber") ?? 0,
            DiscNumber = GetJsonInt(trackAttributes, "discNumber") ?? 0,
            ReleaseDate = GetJsonString(trackAttributes, "releaseDate") ?? string.Empty,
            Explicit = string.Equals(GetJsonString(trackAttributes, "contentRating"), "explicit", StringComparison.OrdinalIgnoreCase)
                ? true
                : null,
            Composer = GetJsonString(trackAttributes, "composerName") ?? string.Empty,
            Genres = ReadJsonStringArray(trackAttributes, "genreNames")
        };

        return new WatchIntentTrack(trackId, intent.Isrc, intent);
    }

    private async Task<BoomplayPlaylistWatchData?> GetBoomplayPlaylistWatchDataAsync(
        string playlistId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return null;
        }

        BoomplayPlaylistMetadata? playlist = string.Equals(playlistId, "trending-songs", StringComparison.OrdinalIgnoreCase)
            ? await _boomplayMetadataService.GetTrendingSongsAsync(includeTracks: false, cancellationToken)
            : await _boomplayMetadataService.GetPlaylistAsync(playlistId, cancellationToken);

        if (playlist == null)
        {
            return null;
        }

        var tracks = playlist.Tracks.Count > 0
            ? BuildBoomplayWatchTracksFromPlaylistItems(playlist.Tracks)
            : BuildBoomplayWatchTracksFromHints(playlist.TrackIds, playlist.TrackHints);

        var trackCount = playlist.TrackIds.Count > 0
            ? playlist.TrackIds.Count
            : tracks.Count;

        return new BoomplayPlaylistWatchData(
            string.IsNullOrWhiteSpace(playlist.Title) ? "Boomplay Playlist" : playlist.Title,
            playlist.Description,
            playlist.ImageUrl,
            trackCount,
            tracks);
    }

    private static List<WatchIntentTrack> BuildBoomplayWatchTracksFromPlaylistItems(
        List<BoomplayTrackMetadata> tracks)
    {
        var watchTracks = new List<WatchIntentTrack>(tracks.Count);
        foreach (var track in tracks)
        {
            if (string.IsNullOrWhiteSpace(track.Id))
            {
                continue;
            }

            var trackId = track.Id.Trim();
            var sourceUrl = string.IsNullOrWhiteSpace(track.Url)
                ? $"https://www.boomplay.com/songs/{trackId}"
                : track.Url;

            var intent = new DownloadIntent
            {
                SourceService = BoomplaySource,
                SourceUrl = sourceUrl,
                Isrc = track.Isrc ?? string.Empty,
                Title = track.Title ?? string.Empty,
                Artist = track.Artist ?? string.Empty,
                Album = track.Album ?? string.Empty,
                AlbumArtist = string.IsNullOrWhiteSpace(track.AlbumArtist)
                    ? track.Artist ?? string.Empty
                    : track.AlbumArtist,
                Cover = track.CoverUrl ?? string.Empty,
                DurationMs = track.DurationMs,
                TrackNumber = track.TrackNumber,
                DiscNumber = track.DiscNumber,
                ReleaseDate = track.ReleaseDate ?? string.Empty,
                Composer = track.Composer ?? string.Empty,
                Genres = track.Genres?
                    .Where(static genre => !string.IsNullOrWhiteSpace(genre))
                    .Select(static genre => genre.Trim())
                    .ToList() ?? new List<string>()
            };

            watchTracks.Add(new WatchIntentTrack(trackId, intent.Isrc, intent));
        }

        return watchTracks;
    }

    private static List<WatchIntentTrack> BuildBoomplayWatchTracksFromHints(
        List<string> trackIds,
        Dictionary<string, BoomplayTrackHint> trackHints)
    {
        var watchTracks = new List<WatchIntentTrack>(trackIds.Count);
        foreach (var trackId in trackIds.Select(static trackIdRaw => trackIdRaw?.Trim()))
        {
            if (string.IsNullOrWhiteSpace(trackId))
            {
                continue;
            }

            trackHints.TryGetValue(trackId, out var hint);
            var intent = new DownloadIntent
            {
                SourceService = BoomplaySource,
                SourceUrl = string.IsNullOrWhiteSpace(hint?.Url)
                    ? $"https://www.boomplay.com/songs/{trackId}"
                    : hint.Url,
                Title = hint?.Title ?? string.Empty,
                Artist = hint?.Artist ?? string.Empty,
                Album = hint?.Album ?? string.Empty,
                AlbumArtist = hint?.Artist ?? string.Empty,
                Cover = hint?.CoverUrl ?? string.Empty
            };

            watchTracks.Add(new WatchIntentTrack(trackId, null, intent));
        }

        return watchTracks;
    }

    public async Task<int> QueueSpotifyWatchTracksAsync(
        IReadOnlyCollection<SpotifyTrackSummary> tracks,
        ArtistWatchQueueOptions options,
        CancellationToken cancellationToken)
    {
        return (await QueueSpotifyWatchTracksWithOutcomeAsync(tracks, options, cancellationToken)).Queued;
    }

    public async Task<ArtistWatchQueueOutcome> QueueSpotifyWatchTracksWithOutcomeAsync(
        IReadOnlyCollection<SpotifyTrackSummary> tracks,
        ArtistWatchQueueOptions options,
        CancellationToken cancellationToken)
    {
        var sourceLabel = BuildQueueSourceLabel(SpotifyLabel, options.CollectionType, options.CollectionName);
        var result = await QueueSpotifyTracksAsync(
            tracks,
            options.DestinationFolderId,
            BuildQueueWatchOptions(new QueueWatchOptionsInput
            {
                SourceLabel = sourceLabel,
                PreferredEngine = options.PreferredEngine,
                DownloadVariantMode = options.DownloadVariantMode,
                AtmosDestinationFolderId = options.AtmosDestinationFolderId,
                RuleSet = new QueueWatchRuleSet(options.RoutingRules, options.BlockRules),
                WatchlistOrigin = ArtistWatchOrigin
            }),
            cancellationToken);
        return ToArtistWatchQueueOutcome(tracks.Count, result);
    }

    public async Task<int> QueueDeezerWatchTracksAsync(
        IReadOnlyCollection<GwTrack> tracks,
        ArtistWatchQueueOptions options,
        CancellationToken cancellationToken)
    {
        return (await QueueDeezerWatchTracksWithOutcomeAsync(tracks, options, cancellationToken)).Queued;
    }

    public async Task<ArtistWatchQueueOutcome> QueueDeezerWatchTracksWithOutcomeAsync(
        IReadOnlyCollection<GwTrack> tracks,
        ArtistWatchQueueOptions options,
        CancellationToken cancellationToken)
    {
        var sourceLabel = BuildQueueSourceLabel(DeezerLabel, options.CollectionType, options.CollectionName);
        var result = await QueueDeezerTracksAsync(
            tracks,
            options.DestinationFolderId,
            BuildQueueWatchOptions(new QueueWatchOptionsInput
            {
                SourceLabel = sourceLabel,
                PreferredEngine = options.PreferredEngine,
                DownloadVariantMode = options.DownloadVariantMode,
                AtmosDestinationFolderId = options.AtmosDestinationFolderId,
                RuleSet = new QueueWatchRuleSet(options.RoutingRules, options.BlockRules),
                WatchlistOrigin = ArtistWatchOrigin
            }),
            cancellationToken);
        return ToArtistWatchQueueOutcome(tracks.Count, result);
    }

    public async Task<int> QueueAppleWatchIntentsAsync(
        IReadOnlyCollection<DownloadIntent> intents,
        ArtistWatchQueueOptions options,
        CancellationToken cancellationToken)
    {
        return (await QueueAppleWatchIntentsWithOutcomeAsync(intents, options, cancellationToken)).Queued;
    }

    public async Task<ArtistWatchQueueOutcome> QueueAppleWatchIntentsWithOutcomeAsync(
        IReadOnlyCollection<DownloadIntent> intents,
        ArtistWatchQueueOptions options,
        CancellationToken cancellationToken)
    {
        if (intents.Count == 0)
        {
            return new ArtistWatchQueueOutcome(0, 0, 0, 0, 0, 0);
        }

        var watchTracks = intents
            .Select(intent =>
            {
                var trackId = ResolveIntentTrackId(intent);
                if (string.IsNullOrWhiteSpace(trackId))
                {
                    return null;
                }

                return new WatchIntentTrack(trackId, intent.Isrc, intent);
            })
            .Where(static track => track is not null)
            .Select(static track => track!)
            .ToList();

        var sourceLabel = BuildQueueSourceLabel("Apple Music", options.CollectionType, options.CollectionName);
        var result = await QueueWatchIntentTracksAsync(
            watchTracks,
            options.DestinationFolderId,
            BuildQueueWatchOptions(new QueueWatchOptionsInput
            {
                SourceLabel = sourceLabel,
                PreferredEngine = options.PreferredEngine,
                DownloadVariantMode = options.DownloadVariantMode,
                AtmosDestinationFolderId = options.AtmosDestinationFolderId,
                RuleSet = new QueueWatchRuleSet(options.RoutingRules, options.BlockRules),
                WatchlistOrigin = ArtistWatchOrigin
            }),
            cancellationToken);
        return ToArtistWatchQueueOutcome(intents.Count, result);
    }

    private static ArtistWatchQueueOutcome ToArtistWatchQueueOutcome(int requested, QueueWatchResult result)
        => new(
            requested,
            result.QueuedCount,
            result.CompletedCount,
            result.UnavailableCount,
            result.Deferred ? Math.Max(1, result.RemainingQueueableCount) : result.RemainingQueueableCount,
            result.FailedCount);

    private async Task<QueueWatchResult> QueueSpotifyTracksAsync(
        IReadOnlyCollection<SpotifyTrackSummary> tracks,
        long? destinationFolderId,
        QueueWatchOptions options,
        CancellationToken cancellationToken)
    {
        if (tracks.Count == 0)
        {
            return default;
        }

        var watchTracks = tracks
            .Where(track => !string.IsNullOrWhiteSpace(track.Id))
            .Select(track =>
            {
                var trackId = track.Id.Trim();
                var intent = new DownloadIntent
                {
                    SourceService = SpotifySource,
                    SourceUrl = BuildSpotifyTrackUrl(trackId, track.SourceUrl),
                    SpotifyId = trackId,
                    Isrc = track.Isrc ?? string.Empty,
                    Title = track.Name ?? string.Empty,
                    Artist = track.Artists ?? string.Empty,
                    Album = track.Album ?? string.Empty,
                    AlbumArtist = track.AlbumArtist ?? track.Artists ?? string.Empty,
                    Cover = track.ImageUrl ?? string.Empty,
                    DurationMs = track.DurationMs ?? 0,
                    Position = track.TrackNumber ?? 0,
                    ReleaseDate = track.ReleaseDate ?? string.Empty,
                    TrackNumber = track.TrackNumber ?? 0,
                    DiscNumber = track.DiscNumber ?? 0,
                    TrackTotal = track.TrackTotal ?? 0,
                    Explicit = track.Explicit,
                    Label = track.Label ?? string.Empty,
                    Genres = track.Genres?
                        .Where(static genre => !string.IsNullOrWhiteSpace(genre))
                        .Select(static genre => genre.Trim())
                        .ToList() ?? new List<string>()
                };
                return new WatchIntentTrack(trackId, track.Isrc, intent);
            })
            .ToList();

        return await QueueWatchIntentTracksAsync(
            watchTracks,
            destinationFolderId,
            options,
            cancellationToken);
    }

    private async Task<QueueWatchResult> QueueDeezerTracksAsync(
        IReadOnlyCollection<GwTrack> tracks,
        long? destinationFolderId,
        QueueWatchOptions options,
        CancellationToken cancellationToken)
    {
        if (tracks.Count == 0)
        {
            return default;
        }

        var watchTracks = tracks
            .Where(track => track.SngId > 0)
            .Select(track =>
            {
                var trackId = track.SngId.ToString();
                var durationMs = track.Duration > 0 ? track.Duration * 1000 : 0;
                var intent = new DownloadIntent
                {
                    SourceService = DeezerSource,
                    SourceUrl = BuildDeezerTrackUrl(trackId),
                    DeezerId = trackId,
                    Isrc = track.Isrc ?? string.Empty,
                    Title = track.SngTitle ?? string.Empty,
                    Artist = track.ArtName ?? string.Empty,
                    Album = track.AlbTitle ?? string.Empty,
                    AlbumArtist = track.ArtName ?? string.Empty,
                    Cover = BuildDeezerCoverUrl(track.AlbPicture),
                    DurationMs = durationMs,
                    Position = track.Position > 0 ? track.Position : track.TrackNumber
                };
                return new WatchIntentTrack(trackId, track.Isrc, intent);
            })
            .ToList();

        return await QueueWatchIntentTracksAsync(
            watchTracks,
            destinationFolderId,
            options,
            cancellationToken);
    }

    private static WatchIntentTrack? BuildWatchIntentTrackFromCandidate(string source, PlaylistTrackCandidate candidate)
    {
        var intent = BuildWatchDownloadIntentFromCandidate(source, candidate);
        var trackId = (candidate.TrackSourceId ?? string.Empty).Trim();
        return intent == null || string.IsNullOrWhiteSpace(trackId)
            ? null
            : new WatchIntentTrack(trackId, candidate.Isrc, intent);
    }

    internal static DownloadIntent? BuildWatchDownloadIntentFromCandidate(
        string source,
        PlaylistTrackCandidate candidate)
    {
        if (!IsDownloadableWatchSource(source))
        {
            return null;
        }

        var trackId = (candidate.TrackSourceId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trackId))
        {
            return null;
        }

        var canonicalTrackId = string.Equals(source, BoomplaySource, StringComparison.OrdinalIgnoreCase)
            ? (candidate.DeezerId ?? string.Empty).Trim()
            : trackId;
        if (string.IsNullOrWhiteSpace(canonicalTrackId))
        {
            return null;
        }

        var sourceService = source switch
        {
            RecommendationsSource => DeezerSource,
            SmartTracklistSource => DeezerSource,
            BoomplaySource => DeezerSource,
            _ => source
        };
        var intent = new DownloadIntent
        {
            SourceService = sourceService,
            SourceUrl = BuildCandidateSourceUrl(sourceService, canonicalTrackId),
            Isrc = candidate.Isrc ?? string.Empty,
            Title = candidate.Title ?? string.Empty,
            Artist = candidate.Artist ?? string.Empty,
            Album = candidate.Album ?? string.Empty,
            AlbumArtist = candidate.Artist ?? string.Empty,
            DurationMs = candidate.DurationMs ?? 0,
            Explicit = candidate.Explicit,
            ReleaseDate = candidate.ReleaseYear?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            Genres = candidate.Genres?
                .Where(static genre => !string.IsNullOrWhiteSpace(genre))
                .Select(static genre => genre.Trim())
                .ToList() ?? new List<string>()
        };

        switch (source)
        {
            case SpotifySource:
                intent.SpotifyId = trackId;
                break;
            case DeezerSource:
            case RecommendationsSource:
            case SmartTracklistSource:
                intent.DeezerId = trackId;
                break;
            case BoomplaySource:
                intent.DeezerId = canonicalTrackId;
                break;
            case AppleSource:
                intent.AppleId = trackId;
                break;
            case QobuzSource:
                intent.PreferredEngine = QobuzSource;
                break;
            case TidalSource:
                intent.PreferredEngine = TidalSource;
                break;
        }

        return intent;
    }

    private static string BuildCandidateSourceUrl(string source, string trackId)
        => source switch
        {
            SpotifySource => BuildSpotifyTrackUrl(trackId, null),
            DeezerSource or RecommendationsSource or SmartTracklistSource => BuildDeezerTrackUrl(trackId),
            AppleSource => $"https://music.apple.com/song/{Uri.EscapeDataString(trackId)}",
            BoomplaySource => $"https://www.boomplay.com/songs/{Uri.EscapeDataString(trackId)}",
            QobuzSource => BuildQobuzTrackUrl(trackId),
            TidalSource => BuildTidalTrackUrl(trackId),
            _ => string.Empty
        };

    private static bool IsDownloadableWatchSource(string source)
    {
        return source switch
        {
            SpotifySource => true,
            DeezerSource => true,
            RecommendationsSource => true,
            SmartTracklistSource => true,
            AppleSource => true,
            BoomplaySource => true,
            QobuzSource => true,
            TidalSource => true,
            _ => false
        };
    }

    private static string BuildQueueSourceLabel(string defaultLabel, string collectionType, string collectionName)
    {
        var normalizedType = (collectionType ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedType))
        {
            return defaultLabel;
        }

        var normalizedName = (collectionName ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalizedName)
            ? $"{defaultLabel} {normalizedType}"
            : $"{defaultLabel} {normalizedType}:{normalizedName}";
    }

    private QueueWatchOptions BuildQueueWatchOptions(QueueWatchOptionsInput input)
    {
        var globalSettings = _settingsService.LoadSettings();
        var preferredEngine = ResolveAutomaticPreferredEngine(input.PreferredEngine, globalSettings);
        return new QueueWatchOptions(
            input.SourceLabel,
            input.WatchlistSource,
            input.WatchlistPlaylistId,
            preferredEngine,
            ResolveDownloadEngineOrder(preferredEngine, input.DownloadEngineOrder, globalSettings),
            input.DownloadVariantMode,
            input.AtmosDestinationFolderId,
            input.RuleSet?.RoutingRules,
            input.RuleSet?.BlockRules,
            input.WatchlistOrigin,
            input.CandidateIdentityRevision,
            input.ProviderReadinessRevision);
    }

    private static DownloadEngineOrderSettings? ResolveDownloadEngineOrder(
        string? resolvedPreferredEngine,
        DownloadEngineOrderSettings? itemDownloadEngineOrder,
        DeezSpoTagSettings globalSettings)
    {
        if (!string.Equals(resolvedPreferredEngine, DownloadSourceCatalog.Custom, StringComparison.Ordinal))
        {
            return null;
        }

        return itemDownloadEngineOrder ?? globalSettings.DownloadEngineOrder ?? DownloadEngineOrderSettings.CreateDefault();
    }

    private static string? ResolveAutomaticPreferredEngine(string? itemPreferredEngine, DeezSpoTagSettings globalSettings)
    {
        var normalizedItemEngine = NormalizePreferredEngine(itemPreferredEngine);
        if (!string.IsNullOrWhiteSpace(normalizedItemEngine))
        {
            return normalizedItemEngine;
        }

        return ManualDownloadPreferenceResolver.ResolvePreferredEngine(globalSettings);
    }

    private static long? ResolveRoutingFolderId(DownloadIntent intent, IReadOnlyList<PlaylistTrackRoutingRule>? rules, long? defaultFolderId)
    {
        if (rules is null || rules.Count == 0)
        {
            return defaultFolderId;
        }

        var matchedRule = WatchlistSelectionPolicy.ResolveRoutingRule(intent, rules);

        return matchedRule?.DestinationFolderId ?? defaultFolderId;
    }

    private static string? NormalizePreferredEngine(string? engine)
    {
        if (string.IsNullOrWhiteSpace(engine))
        {
            return null;
        }

        return DownloadSourceCatalog.NormalizeSourcePolicy(engine);
    }

    private static string NormalizeDownloadVariantMode(string? mode)
    {
        var normalized = string.IsNullOrWhiteSpace(mode)
            ? "standard"
            : mode.Trim().ToLowerInvariant();

        return normalized is "dual_quality" or "atmos_only"
            ? normalized
            : "standard";
    }

    private DownloadIntent CreateAtmosOnlyIntent(DownloadIntent sourceIntent, long? atmosDestinationFolderId = null)
    {
        atmosDestinationFolderId ??= _settingsService.LoadSettings().MultiQuality?.SecondaryDestinationFolderId;

        return new DownloadIntent
        {
            SourceService = sourceIntent.SourceService,
            SourceUrl = sourceIntent.SourceUrl,
            SpotifyId = sourceIntent.SpotifyId,
            DeezerId = sourceIntent.DeezerId,
            DeezerAlbumId = sourceIntent.DeezerAlbumId,
            DeezerArtistId = sourceIntent.DeezerArtistId,
            Isrc = sourceIntent.Isrc,
            Title = sourceIntent.Title,
            Artist = sourceIntent.Artist,
            Album = sourceIntent.Album,
            AlbumArtist = sourceIntent.AlbumArtist,
            Cover = sourceIntent.Cover,
            DurationMs = sourceIntent.DurationMs,
            Position = sourceIntent.Position,
            Genres = new List<string>(sourceIntent.Genres ?? []),
            Label = sourceIntent.Label,
            Copyright = sourceIntent.Copyright,
            Explicit = sourceIntent.Explicit,
            Composer = sourceIntent.Composer,
            ReleaseDate = sourceIntent.ReleaseDate,
            TrackNumber = sourceIntent.TrackNumber,
            DiscNumber = sourceIntent.DiscNumber,
            TrackTotal = sourceIntent.TrackTotal,
            DiscTotal = sourceIntent.DiscTotal,
            Url = sourceIntent.Url,
            Barcode = sourceIntent.Barcode,
            PreferredEngine = AppleSource,
            Quality = "atmos",
            ContentType = DownloadContentTypes.Atmos,
            DestinationFolderId = atmosDestinationFolderId,
            SecondaryDestinationFolderId = null,
            AppleId = sourceIntent.AppleId,
            WatchlistSource = sourceIntent.WatchlistSource,
            WatchlistPlaylistId = sourceIntent.WatchlistPlaylistId,
            WatchlistTrackId = sourceIntent.WatchlistTrackId,
            WatchlistOrigin = sourceIntent.WatchlistOrigin,
            WatchlistUnavailableSettingsFingerprint = sourceIntent.WatchlistUnavailableSettingsFingerprint,
            HasAtmos = sourceIntent.HasAtmos,
            HasAppleDigitalMaster = sourceIntent.HasAppleDigitalMaster,
            Danceability = sourceIntent.Danceability,
            Energy = sourceIntent.Energy,
            Valence = sourceIntent.Valence,
            Acousticness = sourceIntent.Acousticness,
            Instrumentalness = sourceIntent.Instrumentalness,
            Speechiness = sourceIntent.Speechiness,
            Loudness = sourceIntent.Loudness,
            Tempo = sourceIntent.Tempo,
            TimeSignature = sourceIntent.TimeSignature,
            Liveness = sourceIntent.Liveness,
            MusicKey = sourceIntent.MusicKey,
            AllowQualityUpgrade = sourceIntent.AllowQualityUpgrade
        };
    }

    [SuppressMessage("Major Code Smell", "S3776", Justification = "Queue orchestration keeps guardrails and accounting in one flow to preserve strict enqueue semantics.")]
    private async Task<QueueWatchResult> QueueWatchIntentTracksAsync(
        IReadOnlyCollection<WatchIntentTrack> tracks,
        long? destinationFolderId,
        QueueWatchOptions options,
        CancellationToken cancellationToken)
    {
        if (tracks.Count == 0)
        {
            return default;
        }

        var watchSettings = _settingsService.LoadSettings();
        if (!watchSettings.WatchEnabled)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "{Source} watch queue skipped because watchlist is disabled.",
                    options.SourceLabel);
            }

            return new QueueWatchResult(
                0,
                0,
                0,
                Deferred: true,
                AttemptedCount: 0,
                SystemicFailureCount: 0,
                FirstSystemicFailureFingerprint: null,
                FirstFailureMessage: null,
                StopReason: WatchQueueStopReason.WatchlistDisabled,
                RemainingQueueableCount: tracks.Count,
                UnavailableCount: 0);
        }

        using var scope = _serviceProvider.CreateScope();
        var intentService = scope.ServiceProvider.GetRequiredService<DownloadIntentService>();
        var orchestrationService = scope.ServiceProvider.GetRequiredService<DownloadOrchestrationService>();
        var normalizedPreferredEngine = NormalizePreferredEngine(options.PreferredEngine);
        var normalizedDownloadVariantMode = NormalizeDownloadVariantMode(options.DownloadVariantMode);
        if (!await CanQueueWatchItemsAsync(orchestrationService, options, cancellationToken))
        {
            return new QueueWatchResult(
                0,
                0,
                0,
                Deferred: true,
                AttemptedCount: 0,
                SystemicFailureCount: 0,
                FirstSystemicFailureFingerprint: null,
                FirstFailureMessage: null,
                StopReason: WatchQueueStopReason.DownloadGate,
                RemainingQueueableCount: tracks.Count,
                UnavailableCount: 0);
        }

        var queueContext = new QueuedWatchIntentContext(intentService, options, normalizedDownloadVariantMode);
        var trackList = tracks as IReadOnlyList<WatchIntentTrack> ?? tracks.ToList();
        var queuedCount = 0;
        var completedCount = 0;
        var failedCount = 0;
        var unavailableCount = 0;
        var attemptedCount = 0;
        var systemicFailureCount = 0;
        string? firstSystemicFailureFingerprint = null;
        string? firstFailureMessage = null;
        var deferred = false;
        var stopReason = WatchQueueStopReason.None;
        var localBudgetToken = _queueAdmission.BeginRunIfInactive(watchSettings.WatchMaxItemsPerRun);
        try
        {
            for (var index = 0; index < trackList.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var track = trackList[index];
                var admission = _queueAdmission.TryAdmitTrack();
                if (!admission.Allowed)
                {
                    LogWatchRunQueueAdmissionDeferred(options, queuedCount);
                    deferred = true;
                    stopReason = admission.Reason;
                    break;
                }

                var intent = PrepareWatchIntent(
                    track.Intent,
                    track.TrackId,
                    options,
                    destinationFolderId,
                    normalizedDownloadVariantMode,
                    normalizedPreferredEngine);

                var result = await TryQueuePrimaryIntentAsync(
                    intentService,
                    intent,
                    options.SourceLabel,
                    track.TrackId,
                    options.BlockRules,
                    allowAutomaticSecondaryQuality: false,
                    cancellationToken);
                attemptedCount++;
                if (result is null)
                {
                    _queueAdmission.Release(1);
                    await TryMarkWatchTrackStatusAsync(
                        options.WatchlistSource,
                        options.WatchlistPlaylistId,
                        track.TrackId,
                        FailedStatus,
                        cancellationToken);
                    failedCount++;
                    if (string.IsNullOrWhiteSpace(firstFailureMessage))
                    {
                        firstFailureMessage = "Primary enqueue failed without result.";
                    }
                    continue;
                }

                var primaryQueuedCount = result.Success ? result.Queued.Count : 0;
                _queueAdmission.Release(1 - primaryQueuedCount);

                if (ShouldDeferWatchTrack(result))
                {
                    LogWatchTrackDeferred(options.SourceLabel, track.TrackId, result.Message);
                    deferred = true;
                    stopReason = WatchQueueStopReason.TrackDeferred;
                    break;
                }

                var trackResult = await HandleQueuedWatchIntentResultAsync(
                    queueContext,
                    result,
                    track,
                    intent,
                    primaryQueuedCount,
                    cancellationToken);
                queuedCount += trackResult.QueuedCount;
                if (trackResult.Completed)
                {
                    completedCount++;
                }

                if (trackResult.Failed)
                {
                    failedCount++;
                    var failureClassification = ClassifyWatchFailure(result);
                    if (string.IsNullOrWhiteSpace(firstFailureMessage))
                    {
                        firstFailureMessage = failureClassification.Message;
                    }
                    if (failureClassification.IsSystemic)
                    {
                        systemicFailureCount++;
                        firstSystemicFailureFingerprint ??= failureClassification.Fingerprint;
                    }
                }
                else if (trackResult.Unavailable)
                {
                    unavailableCount++;
                }
            }
        }
        finally
        {
            if (localBudgetToken != 0)
            {
                _queueAdmission.EndRun(localBudgetToken);
            }
        }

        if (stopReason == WatchQueueStopReason.None)
        {
            stopReason = ResolveCompletedStopReason(systemicFailureCount, failedCount, queuedCount);
        }

        return new QueueWatchResult(
            queuedCount,
            completedCount,
            failedCount,
            Deferred: deferred,
            AttemptedCount: attemptedCount,
            SystemicFailureCount: systemicFailureCount,
            FirstSystemicFailureFingerprint: firstSystemicFailureFingerprint,
            FirstFailureMessage: firstFailureMessage,
            StopReason: stopReason,
            RemainingQueueableCount: Math.Max(0, trackList.Count - attemptedCount),
            UnavailableCount: unavailableCount);
    }

    private static WatchQueueStopReason ResolveCompletedStopReason(
        int systemicFailureCount,
        int failedCount,
        int queuedCount)
    {
        if (systemicFailureCount > 0)
        {
            return WatchQueueStopReason.SystemicFailure;
        }

        return failedCount > 0 && queuedCount == 0
            ? WatchQueueStopReason.TrackFailures
            : WatchQueueStopReason.Completed;
    }

    private async Task<bool> CanQueueWatchItemsAsync(
        DownloadOrchestrationService orchestrationService,
        QueueWatchOptions options,
        CancellationToken cancellationToken)
    {
        var admission = await _queueAdmission.EvaluateDownloadGateAsync(
            orchestrationService,
            cancellationToken);
        if (admission.Allowed)
        {
            return true;
        }

        LogDownloadGateDeferred(options.SourceLabel, admission.Message);
        return false;
    }

    private void LogWatchRunQueueAdmissionDeferred(
        QueueWatchOptions options,
        int queuedCount)
    {
        if (!_logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        _logger.LogInformation(
            "{Source} watch queue reached per-run budget. queuedThisRun={QueuedThisRun}",
            options.SourceLabel,
            queuedCount);
    }

    private void LogDownloadGateDeferred(string sourceLabel, string? message)
    {
        if (!_logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        _logger.LogInformation(
            "{Source} watch queue deferred because downloads are currently gated: {Reason}",
            sourceLabel,
            ResolveDeferredDownloadReason(message));
    }

    private static string ResolveDeferredDownloadReason(string? message)
        => string.IsNullOrWhiteSpace(message) ? "downloads paused" : message;

    private void LogWatchTrackDeferred(string sourceLabel, string trackId, string? message)
    {
        if (!_logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        _logger.LogInformation(
            "{Source} watch queue deferred for track {TrackId}: {Reason}",
            sourceLabel,
            trackId,
            ResolveDeferredDownloadReason(message));
    }

    private async Task<QueueWatchTrackResult> HandleQueuedWatchIntentResultAsync(
        QueuedWatchIntentContext context,
        DownloadIntentResult result,
        WatchIntentTrack track,
        DownloadIntent intent,
        int primaryQueuedCount,
        CancellationToken cancellationToken)
    {
        var queuedCount = primaryQueuedCount;
        if (result.Success)
        {
            await TryRecordWatchDownloadClaimsAsync(
                context.Options,
                track.TrackId,
                result.Queued,
                intent.DestinationFolderId,
                cancellationToken);
            queuedCount += await TryQueueAtmosIntentAsync(
                context.IntentService,
                context.NormalizedDownloadVariantMode,
                intent,
                new AtmosQueueRequest(context.Options.SourceLabel, track.TrackId, AfterPrimarySkip: false),
                context.Options,
                cancellationToken);
            await TryMarkWatchTrackStatusAsync(
                context.Options.WatchlistSource,
                context.Options.WatchlistPlaylistId,
                track.TrackId,
                QueuedStatus,
                cancellationToken);
            return new QueueWatchTrackResult(queuedCount, Completed: false, Failed: false, Unavailable: false);
        }

        if (ShouldKeepSharedQueueClaimPending(result))
        {
            await TryRecordWatchDownloadClaimsAsync(
                context.Options,
                track.TrackId,
                result.RelatedQueueUuids,
                intent.DestinationFolderId,
                cancellationToken);
            await AddPlaylistWatchHistoryStageAsync(
                context.Options.WatchlistSource ?? string.Empty,
                context.Options.WatchlistPlaylistId ?? string.Empty,
                context.Options.WatchlistPlaylistId,
                1,
                WatchlistHistoryStatus.DuplicateSharedTrackLinked,
                cancellationToken);
            await TryMarkWatchTrackStatusAsync(
                context.Options.WatchlistSource,
                context.Options.WatchlistPlaylistId,
                track.TrackId,
                QueuedStatus,
                cancellationToken);
            return new QueueWatchTrackResult(queuedCount, Completed: false, Failed: false, Unavailable: false);
        }

        if (ShouldMarkWatchTrackAsCompleted(result))
        {
            if (ShouldPersistBlockedTrackIgnore(result))
            {
                await TryPersistWatchTrackIgnoreAsync(
                    context.Options.WatchlistSource,
                    context.Options.WatchlistPlaylistId,
                    track,
                    cancellationToken);
            }
            queuedCount += await TryQueueAtmosIntentAsync(
                context.IntentService,
                context.NormalizedDownloadVariantMode,
                intent,
                new AtmosQueueRequest(context.Options.SourceLabel, track.TrackId, AfterPrimarySkip: true),
                context.Options,
                cancellationToken);
            await TryMarkWatchTrackCompletedAsync(
                context.Options.WatchlistSource,
                context.Options.WatchlistPlaylistId,
                track.TrackId,
                cancellationToken);
            return new QueueWatchTrackResult(queuedCount, Completed: true, Failed: false, Unavailable: false);
        }

        if (ShouldMarkWatchTrackUnavailable(result))
        {
            await TryMarkWatchTrackUnavailableAsync(
                context.Options,
                track.TrackId,
                track.Isrc,
                result.Message,
                nextRecheckUtcOverride: null,
                cancellationToken);
            return new QueueWatchTrackResult(queuedCount, Completed: false, Failed: false, Unavailable: true);
        }

        LogWatchEnqueueFailure(context.Options, track, result);
        await TryMarkWatchTrackStatusAsync(
            context.Options.WatchlistSource,
            context.Options.WatchlistPlaylistId,
            track.TrackId,
            FailedStatus,
            cancellationToken);
        return new QueueWatchTrackResult(queuedCount, Completed: false, Failed: true, Unavailable: false);
    }

    private DownloadIntent PrepareWatchIntent(
        DownloadIntent intent,
        string trackId,
        QueueWatchOptions options,
        long? destinationFolderId,
        string normalizedDownloadVariantMode,
        string? normalizedPreferredEngine)
    {
        intent.DestinationFolderId = ResolveRoutingFolderId(intent, options.RoutingRules, destinationFolderId);
        if (normalizedDownloadVariantMode == "atmos_only")
        {
            intent = CreateAtmosOnlyIntent(intent, options.AtmosDestinationFolderId);
        }
        else if (string.Equals(normalizedPreferredEngine, DownloadSourceCatalog.Custom, StringComparison.Ordinal))
        {
            intent.PreferredEngine = DownloadSourceCatalog.Custom;
            intent.DownloadEngineOrder = options.DownloadEngineOrder ?? DownloadEngineOrderSettings.CreateDefault();
        }
        else if (!string.IsNullOrWhiteSpace(normalizedPreferredEngine))
        {
            intent.PreferredEngine = normalizedPreferredEngine;
        }

        if (HasWatchlistContext(options.WatchlistSource, options.WatchlistPlaylistId))
        {
            intent.WatchlistSource = options.WatchlistSource!;
            intent.WatchlistPlaylistId = options.WatchlistPlaylistId!;
            intent.WatchlistTrackId = trackId;
            intent.WatchlistUnavailableSettingsFingerprint = BuildUnavailableSettingsFingerprint(options);
        }

        intent.WatchlistOrigin = options.WatchlistOrigin ?? string.Empty;

        return CreateManualParityQueueIntent(intent);
    }

    private static DownloadIntent CreateManualParityQueueIntent(DownloadIntent intent)
    {
        if (!HasResolvableSourceIdentity(intent))
        {
            return intent;
        }

        return new DownloadIntent
        {
            SourceService = intent.SourceService,
            SourceUrl = intent.SourceUrl,
            SpotifyId = intent.SpotifyId,
            DeezerId = intent.DeezerId,
            QobuzId = intent.QobuzId,
            TidalId = intent.TidalId,
            AmazonId = intent.AmazonId,
            DeezerAlbumId = intent.DeezerAlbumId,
            DeezerArtistId = intent.DeezerArtistId,
            Isrc = intent.Isrc,
            Title = intent.Title,
            Artist = intent.Artist,
            Album = intent.Album,
            AlbumArtist = intent.AlbumArtist,
            Cover = intent.Cover,
            DurationMs = intent.DurationMs,
            Position = intent.Position,
            Genres = intent.Genres.ToList(),
            Label = intent.Label,
            Copyright = intent.Copyright,
            Explicit = intent.Explicit,
            Composer = intent.Composer,
            ReleaseDate = intent.ReleaseDate,
            TrackNumber = intent.TrackNumber,
            DiscNumber = intent.DiscNumber,
            TrackTotal = intent.TrackTotal,
            DiscTotal = intent.DiscTotal,
            Url = intent.Url,
            Barcode = intent.Barcode,
            PreferredEngine = intent.PreferredEngine,
            Quality = intent.Quality,
            ContentType = intent.ContentType,
            DestinationFolderId = intent.DestinationFolderId,
            SecondaryDestinationFolderId = intent.SecondaryDestinationFolderId,
            AppleId = intent.AppleId,
            WatchlistSource = intent.WatchlistSource,
            WatchlistPlaylistId = intent.WatchlistPlaylistId,
            WatchlistTrackId = intent.WatchlistTrackId,
            WatchlistOrigin = intent.WatchlistOrigin,
            WatchlistUnavailableSettingsFingerprint = intent.WatchlistUnavailableSettingsFingerprint,
            HasAtmos = intent.HasAtmos,
            HasAppleDigitalMaster = intent.HasAppleDigitalMaster,
            AllowQualityUpgrade = intent.AllowQualityUpgrade,
            DownloadEngineOrder = intent.DownloadEngineOrder
        };
    }

    private static bool HasResolvableSourceIdentity(DownloadIntent intent)
    {
        return !string.IsNullOrWhiteSpace(intent.SourceUrl)
               || !string.IsNullOrWhiteSpace(intent.SpotifyId)
               || !string.IsNullOrWhiteSpace(intent.DeezerId)
               || !string.IsNullOrWhiteSpace(intent.AppleId)
               || !string.IsNullOrWhiteSpace(intent.QobuzId)
               || !string.IsNullOrWhiteSpace(intent.TidalId)
               || !string.IsNullOrWhiteSpace(intent.AmazonId);
    }

    private async Task<DownloadIntentResult?> TryQueuePrimaryIntentAsync(
        DownloadIntentService intentService,
        DownloadIntent intent,
        string sourceLabel,
        string trackId,
        IReadOnlyList<PlaylistTrackBlockRule>? blockRules,
        bool allowAutomaticSecondaryQuality,
        CancellationToken cancellationToken)
    {
        try
        {
            return await intentService.EnqueueAsync(
                intent,
                cancellationToken,
                blockRules: blockRules,
                allowAutomaticSecondaryQuality: allowAutomaticSecondaryQuality);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "{Source} watch queue failed for track {TrackId}", sourceLabel, trackId);
            return null;
        }
    }

    private void LogWatchEnqueueFailure(
        QueueWatchOptions options,
        WatchIntentTrack track,
        DownloadIntentResult result)
    {
        var message = string.IsNullOrWhiteSpace(result.Message)
            ? "No item was queued."
            : result.Message.Trim();
        var reasonCodes = result.SkipReasonCodes is { Count: > 0 }
            ? string.Join(",", result.SkipReasonCodes.Where(static code => !string.IsNullOrWhiteSpace(code)))
            : "";
        var reasons = result.SkipReasons is { Count: > 0 }
            ? string.Join(" | ", result.SkipReasons.Where(static reason => !string.IsNullOrWhiteSpace(reason)))
            : "";

        _logger.LogWarning(
            "{Source} watch enqueue failed for playlist {PlaylistId}, track {TrackId} ({Title} - {Artist}). engine={Engine}, message={Message}, reasonCodes={ReasonCodes}, reasons={Reasons}",
            options.SourceLabel,
            options.WatchlistPlaylistId ?? "",
            track.TrackId,
            track.Intent.Title,
            track.Intent.Artist,
            result.Engine,
            message,
            reasonCodes,
            reasons);
    }

    private async Task<int> TryQueueAtmosIntentAsync(
        DownloadIntentService intentService,
        string normalizedDownloadVariantMode,
        DownloadIntent baseIntent,
        AtmosQueueRequest request,
        QueueWatchOptions options,
        CancellationToken cancellationToken)
    {
        if (normalizedDownloadVariantMode != "dual_quality")
        {
            return 0;
        }

        if (!_queueAdmission.TryAdmitTrack().Allowed)
        {
            return 0;
        }

        var atmosIntent = CreateAtmosOnlyIntent(baseIntent, options.AtmosDestinationFolderId);
        try
        {
            var atmosResult = await intentService.EnqueueAsync(
                atmosIntent,
                cancellationToken,
                blockRules: options.BlockRules,
                allowAutomaticSecondaryQuality: false);
            if (atmosResult.Success)
            {
                return 1;
            }

            _queueAdmission.Release(1);
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _queueAdmission.Release(1);
            var messageSuffix = request.AfterPrimarySkip ? " after primary skip" : string.Empty;
            _logger.LogWarning(
                ex,
                "{Source} watch Atmos queue failed{Suffix} for track {TrackId}",
                request.SourceLabel,
                messageSuffix,
                request.TrackId);
            return 0;
        }
    }

    private static bool HasWatchlistContext(string? watchlistSource, string? watchlistPlaylistId)
    {
        return !string.IsNullOrWhiteSpace(watchlistSource)
               && !string.IsNullOrWhiteSpace(watchlistPlaylistId);
    }

    private async Task TryMarkWatchTrackCompletedAsync(
        string? watchlistSource,
        string? watchlistPlaylistId,
        string trackId,
        CancellationToken cancellationToken)
        => await TryMarkWatchTrackStatusAsync(
            watchlistSource,
            watchlistPlaylistId,
            trackId,
            "completed",
            cancellationToken);

    private async Task TryMarkWatchTrackStatusAsync(
        string? watchlistSource,
        string? watchlistPlaylistId,
        string trackId,
        string status,
        CancellationToken cancellationToken)
    {
        if (!HasWatchlistContext(watchlistSource, watchlistPlaylistId)
            || string.IsNullOrWhiteSpace(trackId))
        {
            return;
        }

        try
        {
            await _libraryRepository.UpdatePlaylistWatchTrackStatusAsync(
                watchlistSource!,
                watchlistPlaylistId!,
                trackId,
                status,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to mark watch track as {Status}: {Source}:{PlaylistId}:{TrackId}", status, watchlistSource, watchlistPlaylistId, trackId);
            }
        }
    }

    private async Task TryMarkWatchTrackUnavailableAsync(
        QueueWatchOptions options,
        string trackId,
        string? isrc,
        string? reason,
        DateTimeOffset? nextRecheckUtcOverride,
        CancellationToken cancellationToken)
    {
        if (!HasWatchlistContext(options.WatchlistSource, options.WatchlistPlaylistId)
            || string.IsNullOrWhiteSpace(trackId))
        {
            return;
        }

        var nextRecheckUtc = nextRecheckUtcOverride ?? DateTimeOffset.UtcNow.AddDays(UnavailableRecheckDays);
        var message = string.IsNullOrWhiteSpace(reason)
            ? "Track unavailable from enabled sources."
            : reason.Trim();
        try
        {
            await _libraryRepository.MarkPlaylistWatchTrackUnavailableAsync(
                options.WatchlistSource!,
                options.WatchlistPlaylistId!,
                trackId,
                isrc,
                message,
                BuildUnavailableSettingsFingerprint(options),
                nextRecheckUtc,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    ex,
                    "Failed to mark watch track unavailable: {Source}:{PlaylistId}:{TrackId}",
                    options.WatchlistSource,
                    options.WatchlistPlaylistId,
                    trackId);
            }
        }
    }

    private async Task TryPersistWatchTrackIgnoreAsync(
        string? watchlistSource,
        string? watchlistPlaylistId,
        WatchIntentTrack track,
        CancellationToken cancellationToken)
    {
        if (!HasWatchlistContext(watchlistSource, watchlistPlaylistId)
            || string.IsNullOrWhiteSpace(track.TrackId))
        {
            return;
        }

        try
        {
            await _libraryRepository.AddPlaylistWatchIgnoredTracksAsync(
                watchlistSource!,
                watchlistPlaylistId!,
                new List<PlaylistWatchIgnoreInsert> { new(track.TrackId, track.Isrc) },
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    ex,
                    "Failed to persist watch ignore entry: {Source}:{PlaylistId}:{TrackId}",
                    watchlistSource,
                    watchlistPlaylistId,
                    track.TrackId);
            }
        }
    }

    private static bool ShouldMarkWatchTrackAsCompleted(DownloadIntentResult result)
    {
        if (result?.SkipReasonCodes == null || result.SkipReasonCodes.Count == 0)
        {
            return false;
        }

        foreach (var reasonCode in result.SkipReasonCodes)
        {
            switch (reasonCode?.Trim().ToLowerInvariant())
            {
                case "library_duplicate":
                case "library_quality_not_higher":
                case "blocklist_match":
                    return true;
            }
        }

        return false;
    }

    private static bool ShouldKeepSharedQueueClaimPending(DownloadIntentResult result)
    {
        if (result?.SkipReasonCodes == null || result.SkipReasonCodes.Count == 0)
        {
            return false;
        }

        foreach (var reasonCode in result.SkipReasonCodes)
        {
            switch (reasonCode?.Trim().ToLowerInvariant())
            {
                case "queue_duplicate":
                case "queue_insert_ignored":
                    return true;
            }
        }

        return false;
    }

    private static bool ShouldMarkWatchTrackUnavailable(DownloadIntentResult result)
    {
        if (ClassifyWatchFailure(result).IsSystemic)
        {
            return false;
        }

        var normalizedCodes = result.SkipReasonCodes?
            .Where(static code => !string.IsNullOrWhiteSpace(code))
            .Select(NormalizeReasonCode)
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (normalizedCodes.Overlaps(new[]
            {
                "track_unavailable",
                "engine_identity_unavailable",
                "track_identity_unavailable",
                "source_identity_unavailable",
                "qobuz_identity_unavailable",
                "tidal_identity_unavailable",
                "amazon_identity_unavailable",
                "download_url_unavailable",
                "track_not_found",
                "not_found",
                "no_match",
                "no_source_match",
                "metadata_unresolved",
                "source_unavailable_for_track"
            }))
        {
            return true;
        }

        var message = result.Message?.Trim().ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        if (message.Contains("credentials", StringComparison.Ordinal)
            || message.Contains("login required", StringComparison.Ordinal)
            || message.Contains("unauthorized", StringComparison.Ordinal)
            || message.Contains("forbidden", StringComparison.Ordinal)
            || message.Contains("rate limit", StringComparison.Ordinal)
            || message.Contains("too many requests", StringComparison.Ordinal)
            || message.Contains("timed out", StringComparison.Ordinal)
            || message.Contains("timeout", StringComparison.Ordinal))
        {
            return false;
        }

        return message.Contains("track not found", StringComparison.Ordinal)
            || message.Contains("track id unavailable", StringComparison.Ordinal)
            || message.Contains("track unavailable", StringComparison.Ordinal)
            || message.Contains("identity unavailable", StringComparison.Ordinal)
            || message.Contains("download url not available", StringComparison.Ordinal)
            || message.Contains("not available from enabled", StringComparison.Ordinal)
            || message.Contains("not found for isrc or metadata", StringComparison.Ordinal)
            || message.Contains("could not be resolved for this download", StringComparison.Ordinal);
    }

    private async Task TryRecordWatchDownloadClaimsAsync(
        QueueWatchOptions options,
        string trackId,
        IReadOnlyCollection<string>? queueUuids,
        long? destinationFolderId,
        CancellationToken cancellationToken)
    {
        if (queueUuids is not { Count: > 0 }
            || !HasWatchlistContext(options.WatchlistSource, options.WatchlistPlaylistId))
        {
            return;
        }

        await _libraryRepository.UpsertPlaylistWatchDownloadClaimsAsync(
            options.WatchlistSource!,
            options.WatchlistPlaylistId!,
            trackId,
            queueUuids,
            destinationFolderId,
            cancellationToken);
    }

    private static bool ShouldDeferWatchTrack(DownloadIntentResult result)
    {
        if (result?.SkipReasonCodes == null || result.SkipReasonCodes.Count == 0)
        {
            return false;
        }

        return result.SkipReasonCodes.Any(
            reasonCode => string.Equals(reasonCode?.Trim(), "download_gate_paused", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldPersistBlockedTrackIgnore(DownloadIntentResult result)
    {
        if (result?.SkipReasonCodes == null || result.SkipReasonCodes.Count == 0)
        {
            return false;
        }

        return result.SkipReasonCodes.Any(
            reasonCode => string.Equals(reasonCode?.Trim(), "blocklist_match", StringComparison.OrdinalIgnoreCase));
    }

    private static WatchFailureClassification ClassifyWatchFailure(DownloadIntentResult? result)
    {
        var message = result?.Message?.Trim();
        var normalizedMessage = string.IsNullOrWhiteSpace(message)
            ? string.Empty
            : message.ToLowerInvariant();
        var reasonCodes = result?.SkipReasonCodes ?? new List<string>();
        var normalizedCodes = reasonCodes
            .Where(static code => !string.IsNullOrWhiteSpace(code))
            .Select(static code => code.Trim().ToLowerInvariant())
            .ToList();

        if (normalizedCodes.Contains("download_gate_paused"))
        {
            return new WatchFailureClassification(false, "download_gate_paused", message);
        }

        if (normalizedCodes.Contains("queue_duplicate")
            || normalizedCodes.Contains("queue_insert_ignored")
            || normalizedCodes.Contains("library_duplicate")
            || normalizedCodes.Contains("library_quality_not_higher")
            || normalizedCodes.Contains("blocklist_match"))
        {
            return new WatchFailureClassification(false, normalizedCodes.FirstOrDefault(), message);
        }

        if (normalizedCodes.Contains("provider_timeout")
            || normalizedCodes.Contains("provider_http_429")
            || normalizedCodes.Contains("provider_http_5xx")
            || normalizedCodes.Contains("provider_auth")
            || normalizedCodes.Contains("provider_captcha"))
        {
            return new WatchFailureClassification(true, normalizedCodes.FirstOrDefault(), message);
        }

        if (normalizedMessage.Contains("captcha", StringComparison.Ordinal)
            || normalizedMessage.Contains("http 403", StringComparison.Ordinal)
            || normalizedMessage.Contains("http 401", StringComparison.Ordinal)
            || normalizedMessage.Contains("forbidden", StringComparison.Ordinal)
            || normalizedMessage.Contains("unauthorized", StringComparison.Ordinal)
            || normalizedMessage.Contains("too many requests", StringComparison.Ordinal)
            || normalizedMessage.Contains("429", StringComparison.Ordinal)
            || normalizedMessage.Contains("rate limit", StringComparison.Ordinal)
            || normalizedMessage.Contains("timed out", StringComparison.Ordinal)
            || normalizedMessage.Contains("timeout", StringComparison.Ordinal)
            || normalizedMessage.Contains("httpclient.timeout", StringComparison.Ordinal)
            || normalizedMessage.Contains("service unavailable", StringComparison.Ordinal)
            || normalizedMessage.Contains("gateway timeout", StringComparison.Ordinal)
            || normalizedMessage.Contains("http 500", StringComparison.Ordinal)
            || normalizedMessage.Contains("http 502", StringComparison.Ordinal)
            || normalizedMessage.Contains("http 503", StringComparison.Ordinal)
            || normalizedMessage.Contains("http 504", StringComparison.Ordinal)
            || normalizedMessage.Contains("login required", StringComparison.Ordinal))
        {
            return new WatchFailureClassification(true, BuildSystemicFingerprint(normalizedMessage), message);
        }

        return new WatchFailureClassification(false, normalizedCodes.FirstOrDefault(), message);
    }

    private static string BuildSystemicFingerprint(string normalizedMessage)
    {
        if (normalizedMessage.Contains("captcha", StringComparison.Ordinal))
        {
            return "provider_captcha";
        }
        if (normalizedMessage.Contains("401", StringComparison.Ordinal)
            || normalizedMessage.Contains("unauthorized", StringComparison.Ordinal)
            || normalizedMessage.Contains("login required", StringComparison.Ordinal))
        {
            return "provider_auth_401";
        }
        if (normalizedMessage.Contains("403", StringComparison.Ordinal)
            || normalizedMessage.Contains("forbidden", StringComparison.Ordinal))
        {
            return "provider_auth_403";
        }
        if (normalizedMessage.Contains("429", StringComparison.Ordinal)
            || normalizedMessage.Contains("rate limit", StringComparison.Ordinal)
            || normalizedMessage.Contains("too many requests", StringComparison.Ordinal))
        {
            return "provider_http_429";
        }
        if (normalizedMessage.Contains("500", StringComparison.Ordinal)
            || normalizedMessage.Contains("502", StringComparison.Ordinal)
            || normalizedMessage.Contains("503", StringComparison.Ordinal)
            || normalizedMessage.Contains("504", StringComparison.Ordinal)
            || normalizedMessage.Contains("service unavailable", StringComparison.Ordinal)
            || normalizedMessage.Contains("gateway timeout", StringComparison.Ordinal))
        {
            return "provider_http_5xx";
        }
        if (normalizedMessage.Contains("timeout", StringComparison.Ordinal)
            || normalizedMessage.Contains("timed out", StringComparison.Ordinal))
        {
            return "provider_timeout";
        }

        return "provider_systemic";
    }

    private static string BuildSpotifyTrackUrl(string trackId, string? sourceUrl)
    {
        return string.IsNullOrWhiteSpace(sourceUrl)
            ? $"https://open.spotify.com/track/{trackId}"
            : sourceUrl;
    }

    private static string BuildDeezerTrackUrl(string trackId)
    {
        return $"https://www.deezer.com/track/{trackId}";
    }

    private static string BuildQobuzTrackUrl(string trackId)
    {
        return $"https://open.qobuz.com/track/{Uri.EscapeDataString(trackId)}";
    }

    private static string BuildTidalTrackUrl(string trackId)
    {
        return $"https://tidal.com/browse/track/{Uri.EscapeDataString(trackId)}";
    }

    private static string ResolveSourceLabel(string source)
        => source switch
        {
            SpotifySource => SpotifyLabel,
            DeezerSource => DeezerLabel,
            AppleSource => "Apple Music",
            BoomplaySource => "Boomplay",
            SmartTracklistSource => "Smart Tracklist",
            RecommendationsSource => "Recommendations",
            QobuzSource => "Qobuz",
            TidalSource => "Tidal",
            _ => source
        };

    private static string? EmptyToNull(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string ResolveTidalPlaylistId(string sourceId)
    {
        var value = (sourceId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return value;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("playlist", StringComparison.OrdinalIgnoreCase))
            {
                return segments[i + 1];
            }
        }

        return segments.Length > 0 ? segments[^1] : string.Empty;
    }

    private static string ResolveQobuzPlaylistUrl(string sourceId)
    {
        var value = (sourceId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out _)
            ? value
            : $"https://open.qobuz.com/playlist/{Uri.EscapeDataString(value)}";
    }

    private static string ResolveTidalArtistName(JsonElement track)
    {
        if (track.TryGetProperty(JsonArtistProperty, out var artist) && artist.ValueKind == JsonValueKind.Object)
        {
            var name = GetJsonString(artist, "name");
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name.Trim();
            }
        }

        if (track.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
        {
            var names = artists.EnumerateArray()
                .Select(artist => artist.ValueKind == JsonValueKind.Object ? GetJsonString(artist, "name") : null)
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Select(static name => name!.Trim())
                .ToList();
            if (names.Count > 0)
            {
                return string.Join(", ", names);
            }
        }

        return string.Empty;
    }

    private static string GetHtmlText(HtmlNode node, string xpath)
    {
        var text = node.SelectSingleNode(xpath)?.InnerText ?? string.Empty;
        return HtmlEntity.DeEntitize(text).Trim();
    }

    private static int ParseClockDurationSeconds(string? value)
    {
        var parts = (value ?? string.Empty)
            .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return 0;
        }

        var total = 0;
        foreach (var part in parts)
        {
            if (!int.TryParse(part, out var parsed))
            {
                return 0;
            }

            total = (total * 60) + parsed;
        }

        return total;
    }

    private static string BuildDeezerCoverUrl(string? coverId)
    {
        if (string.IsNullOrWhiteSpace(coverId))
        {
            return string.Empty;
        }

        return $"https://cdns-images.dzcdn.net/images/cover/{coverId}/1000x1000-000000-80-0-0.jpg";
    }

    private static string? ResolveIntentTrackId(DownloadIntent intent)
    {
        if (!string.IsNullOrWhiteSpace(intent.SpotifyId))
        {
            return intent.SpotifyId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(intent.DeezerId))
        {
            return intent.DeezerId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(intent.AppleId))
        {
            return intent.AppleId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(intent.SourceUrl))
        {
            return intent.SourceUrl.Trim();
        }

        return null;
    }

    private static bool TryParseRecommendationLibraryId(string? stationId, out long libraryId)
    {
        libraryId = 0;
        if (string.IsNullOrWhiteSpace(stationId))
        {
            return false;
        }

        var value = stationId.Trim();
        if (!value.StartsWith("daily-rotation:l", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = value.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
        {
            return false;
        }

        var libraryPart = parts[1];
        if (!libraryPart.StartsWith("l", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return long.TryParse(libraryPart[1..], out libraryId) && libraryId > 0;
    }

    private static string NormalizeWatchSource(string? source)
    {
        var normalized = source?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized switch
        {
            "smarttracks" => SmartTracklistSource,
            "recommendation" => RecommendationsSource,
            "itunes" => AppleSource,
            "applemusic" => AppleSource,
            _ => string.IsNullOrWhiteSpace(normalized) ? DeezerSource : normalized
        };
    }

    private static bool TryGetSpotifyArtistTopTracksSourceId(string? sourceId, out string artistId)
    {
        artistId = string.Empty;
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return false;
        }

        const string prefix = "artist-top:";
        if (!sourceId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        artistId = sourceId.Substring(prefix.Length).Trim();
        return !string.IsNullOrWhiteSpace(artistId);
    }

    private static bool IsSpotifyHomeTrendingSourceId(string? sourceId)
    {
        return !string.IsNullOrWhiteSpace(sourceId)
               && string.Equals(
                   sourceId.Trim(),
                   SpotifyHomeTrendingSourceId,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetJsonString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.GetRawText();
        }

        if (value.ValueKind == JsonValueKind.True)
        {
            return bool.TrueString;
        }

        if (value.ValueKind == JsonValueKind.False)
        {
            return bool.FalseString;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            return GetJsonStringFromObject(value);
        }

        return null;
    }

    private static string? GetJsonStringFromObject(JsonElement value)
    {
        var candidate = JsonStringObjectPropertyNames
            .Select(propertyName => value.TryGetProperty(propertyName, out var candidate) ? candidate : default)
            .FirstOrDefault(candidate => candidate.ValueKind == JsonValueKind.String);
        return candidate.ValueKind == JsonValueKind.String ? candidate.GetString() : null;
    }

    private static int? GetJsonInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static List<string> ReadJsonStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return new List<string>();
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!.Trim())
            .ToList();
    }

    private static string? ResolveAppleArtworkUrl(JsonElement attributes)
    {
        if (!attributes.TryGetProperty("artwork", out var artwork)
            || artwork.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!artwork.TryGetProperty("url", out var urlValue)
            || urlValue.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var url = urlValue.GetString();
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var width = GetJsonInt(artwork, "width") ?? 1000;
        var height = GetJsonInt(artwork, "height") ?? 1000;

        return url
            .Replace("{w}", width.ToString(), StringComparison.Ordinal)
            .Replace("{h}", height.ToString(), StringComparison.Ordinal)
            .Replace("{f}", "jpg", StringComparison.Ordinal);
    }

    private sealed record QueueWatchOptions(
        string SourceLabel,
        string? WatchlistSource,
        string? WatchlistPlaylistId,
        string? PreferredEngine,
        DownloadEngineOrderSettings? DownloadEngineOrder,
        string? DownloadVariantMode,
        long? AtmosDestinationFolderId,
        IReadOnlyList<PlaylistTrackRoutingRule>? RoutingRules,
        IReadOnlyList<PlaylistTrackBlockRule>? BlockRules,
        string? WatchlistOrigin,
        string? CandidateIdentityRevision,
        string? ProviderReadinessRevision);

    private sealed record WatchIntentTrack(string TrackId, string? Isrc, DownloadIntent Intent);

    private interface IPlaylistSourceAdapter
    {
        string Source { get; }
        Task<LivePlaylistSnapshot> FetchAsync(string sourceId, int maxCandidates, CancellationToken cancellationToken);
        Task<LivePlaylistSnapshotMetadata> FetchHeadAsync(string sourceId, CancellationToken cancellationToken);
    }

    private sealed class PlaylistSourceAdapter : IPlaylistSourceAdapter
    {
        private readonly Func<string, int, CancellationToken, Task<LivePlaylistSnapshot>> _fetch;
        private readonly Func<string, CancellationToken, Task<LivePlaylistSnapshotMetadata>> _fetchHead;

        public PlaylistSourceAdapter(
            string source,
            Func<string, int, CancellationToken, Task<LivePlaylistSnapshot>> fetch,
            Func<string, CancellationToken, Task<LivePlaylistSnapshotMetadata>> fetchHead)
        {
            Source = source;
            _fetch = fetch;
            _fetchHead = fetchHead;
        }

        public string Source { get; }

        public Task<LivePlaylistSnapshot> FetchAsync(
            string sourceId,
            int maxCandidates,
            CancellationToken cancellationToken)
            => _fetch(sourceId, maxCandidates, cancellationToken);

        public Task<LivePlaylistSnapshotMetadata> FetchHeadAsync(
            string sourceId,
            CancellationToken cancellationToken)
            => _fetchHead(sourceId, cancellationToken);
    }

    private readonly record struct TidalPlaylistItemsPage(JsonElement Items, int Total);

    private sealed record ApplePlaylistWatchData(
        string Name,
        string? Description,
        string? ImageUrl,
        int? TrackCount,
        IReadOnlyCollection<WatchIntentTrack> Tracks);

    private sealed record BoomplayPlaylistWatchData(
        string Name,
        string? Description,
        string? ImageUrl,
        int? TrackCount,
        IReadOnlyCollection<WatchIntentTrack> Tracks);

    private sealed record SmartTracklistWatchData(
        string Name,
        string? Description,
        string? ImageUrl,
        int? TrackCount,
        IReadOnlyCollection<WatchIntentTrack> Tracks);
}

public sealed class PlaylistWatchReconciler
{
    private readonly WatchlistEngine _engine;

    internal PlaylistWatchReconciler(WatchlistEngine engine)
    {
        _engine = engine;
    }

    public Task<PlaylistReconciliationResult> ReconcilePlaylistAsync(
        PlaylistWatchlistDto playlist,
        CancellationToken cancellationToken,
        bool forceMediaServerSync = false)
        => _engine.ReconcilePlaylistAsync(playlist, cancellationToken, forceMediaServerSync);

    public Task<PlaylistWatchlistDto> RefreshPlaylistMetadataOnlyAsync(
        PlaylistWatchlistDto playlist,
        CancellationToken cancellationToken,
        bool forceArtworkRefresh = false)
        => _engine.RefreshPlaylistMetadataOnlyAsync(playlist, cancellationToken, forceArtworkRefresh);

    public Task<IReadOnlyList<PlaylistTrackCandidate>> GetPlaylistTrackCandidatesAsync(
        string source,
        string sourceId,
        CancellationToken cancellationToken)
        => _engine.GetPlaylistTrackCandidatesAsync(source, sourceId, cancellationToken);

    public Task<IReadOnlyList<PlaylistTrackCandidate>> GetCachedPlaylistTrackCandidatesAsync(
        string source,
        string sourceId,
        CancellationToken cancellationToken)
        => _engine.GetCachedPlaylistTrackCandidatesAsync(source, sourceId, cancellationToken);

    public Task<int> RecoverInvalidPendingWatchClaimsAsync(CancellationToken cancellationToken = default)
        => _engine.RecoverInvalidPendingWatchClaimsAsync(cancellationToken);

    public Task UpdatePlaylistStateAsync(
        string source,
        string sourceId,
        int? trackCount,
        string? snapshotId,
        WatchlistPlaylistState state,
        string? lastRunMessage,
        DateTimeOffset? nextAttemptUtc,
        int? consecutiveFailures,
        CancellationToken cancellationToken,
        bool touchLastChecked = true)
        => _engine.UpdatePlaylistStateAsync(
            source,
            sourceId,
            trackCount,
            snapshotId,
            state,
            lastRunMessage,
            nextAttemptUtc,
            consecutiveFailures,
            cancellationToken,
            touchLastChecked);
}

public sealed class WatchlistQueueService
{
    private readonly WatchlistEngine _engine;

    internal WatchlistQueueService(WatchlistEngine engine)
    {
        _engine = engine;
    }

    public Task<int> QueueSpotifyWatchTracksAsync(
        IReadOnlyCollection<SpotifyTrackSummary> tracks,
        ArtistWatchQueueOptions options,
        CancellationToken cancellationToken)
        => _engine.QueueSpotifyWatchTracksAsync(tracks, options, cancellationToken);

    public Task<ArtistWatchQueueOutcome> QueueSpotifyWatchTracksWithOutcomeAsync(
        IReadOnlyCollection<SpotifyTrackSummary> tracks,
        ArtistWatchQueueOptions options,
        CancellationToken cancellationToken)
        => _engine.QueueSpotifyWatchTracksWithOutcomeAsync(tracks, options, cancellationToken);

    public Task<int> QueueDeezerWatchTracksAsync(
        IReadOnlyCollection<GwTrack> tracks,
        ArtistWatchQueueOptions options,
        CancellationToken cancellationToken)
        => _engine.QueueDeezerWatchTracksAsync(tracks, options, cancellationToken);

    public Task<ArtistWatchQueueOutcome> QueueDeezerWatchTracksWithOutcomeAsync(
        IReadOnlyCollection<GwTrack> tracks,
        ArtistWatchQueueOptions options,
        CancellationToken cancellationToken)
        => _engine.QueueDeezerWatchTracksWithOutcomeAsync(tracks, options, cancellationToken);

    public Task<int> QueueAppleWatchIntentsAsync(
        IReadOnlyCollection<DownloadIntent> intents,
        ArtistWatchQueueOptions options,
        CancellationToken cancellationToken)
        => _engine.QueueAppleWatchIntentsAsync(intents, options, cancellationToken);

    public Task<ArtistWatchQueueOutcome> QueueAppleWatchIntentsWithOutcomeAsync(
        IReadOnlyCollection<DownloadIntent> intents,
        ArtistWatchQueueOptions options,
        CancellationToken cancellationToken)
        => _engine.QueueAppleWatchIntentsWithOutcomeAsync(intents, options, cancellationToken);
}

public static class WatchlistSelectionPolicy
{
    public static PlaylistTrackRoutingRule? ResolveRoutingRule(
        DownloadIntent intent,
        IReadOnlyList<PlaylistTrackRoutingRule> rules)
        => rules
            .OrderBy(static rule => rule.Order)
            .FirstOrDefault(rule => RuleMatches(intent, rule));

    private static bool RuleMatches(DownloadIntent intent, PlaylistTrackRoutingRule rule)
        => rule.ConditionField switch
        {
            "artist" => EvalString(intent.Artist, rule.ConditionOperator, rule.ConditionValue),
            "title" => EvalString(intent.Title, rule.ConditionOperator, rule.ConditionValue),
            "album" => EvalString(intent.Album, rule.ConditionOperator, rule.ConditionValue),
            "genre" => EvalGenres(intent.Genres, rule.ConditionOperator, rule.ConditionValue),
            "explicit" => rule.ConditionOperator == "is_true" ? intent.Explicit == true : intent.Explicit != true,
            "year" => EvalYear(intent.ReleaseDate, rule.ConditionOperator, rule.ConditionValue),
            _ => false
        };

    private static bool EvalString(string value, string operation, string conditionValue)
        => operation switch
        {
            "contains" => value.Contains(conditionValue, StringComparison.OrdinalIgnoreCase),
            "equals" => string.Equals(value, conditionValue, StringComparison.OrdinalIgnoreCase),
            "starts_with" => value.StartsWith(conditionValue, StringComparison.OrdinalIgnoreCase),
            _ => false
        };

    private static bool EvalGenres(List<string>? genres, string operation, string conditionValue)
    {
        var normalized = conditionValue?.Trim();
        return !string.IsNullOrWhiteSpace(normalized)
            && genres?.Any(genre => !string.IsNullOrWhiteSpace(genre) && EvalString(genre.Trim(), operation, normalized)) == true;
    }

    private static bool EvalYear(string? releaseDate, string operation, string conditionValue)
    {
        var value = releaseDate?.Trim();
        if (value is not { Length: >= 4 }
            || !int.TryParse(value[..4], out var trackYear)
            || !int.TryParse(conditionValue?.Trim(), out var ruleYear))
        {
            return false;
        }

        return operation switch
        {
            "gte" => trackYear >= ruleYear,
            "lte" => trackYear <= ruleYear,
            _ => trackYear == ruleYear
        };
    }
}
