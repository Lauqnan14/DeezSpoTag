using System.Collections.Concurrent;
using System.Text.Json;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared.Models;
using HtmlAgilityPack;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using ApiPlaylist = DeezSpoTag.Core.Models.Deezer.ApiPlaylist;
using ApiTrack = DeezSpoTag.Core.Models.Deezer.ApiTrack;
using GwTrack = DeezSpoTag.Core.Models.Deezer.GwTrack;

namespace DeezSpoTag.Web.Services;

public sealed class PlaylistWatchService
{
    private sealed record QueueWatchRuleSet(
        IReadOnlyList<PlaylistTrackRoutingRule>? RoutingRules,
        IReadOnlyList<PlaylistTrackBlockRule>? BlockRules);

    private readonly record struct AtmosQueueRequest(string SourceLabel, string TrackId, bool AfterPrimarySkip);
    private readonly record struct QueueWatchResult(int QueuedCount, int CompletedCount, int FailedCount, bool Deferred);

    private readonly record struct QueueWatchTrackResult(int QueuedCount, bool Completed, bool Failed);
    private readonly record struct PlaylistTrackSelection(
        List<WatchIntentTrack> MissingTracks,
        int IgnoredCount,
        int LocalCount);
    private readonly record struct QueuedWatchIntentContext(
        DownloadIntentService IntentService,
        QueueWatchOptions Options,
        string NormalizedDownloadVariantMode);

    private readonly record struct WatchQueueCapacity(int Limit, int ActiveCount)
    {
        public int Remaining => Math.Max(0, Limit - ActiveCount);
    }

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
    private const int MaxPlaylistCandidateFetchCount = 1000;
    private static readonly string[] JsonStringObjectPropertyNames = ["standard", "short", "text"];
    private static readonly TimeSpan PlaylistMediaSyncCooldown = TimeSpan.FromMinutes(2);
    private readonly LibraryRepository _libraryRepository;
    private readonly SpotifyMetadataService _spotifyMetadataService;
    private readonly SpotifyPathfinderMetadataClient _spotifyPathfinderMetadataClient;
    private readonly SpotifyArtistService _spotifyArtistService;
    private readonly DeezerClient _deezerClient;
    private readonly DeezerGatewayService _deezerGatewayService;
    private readonly AppleMusicCatalogService _appleCatalogService;
    private readonly BoomplayMetadataService _boomplayMetadataService;
    private readonly LibraryRecommendationService _libraryRecommendationService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ITidalAccessTokenProvider _tidalAccessTokenProvider;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly IServiceProvider _serviceProvider;
    private readonly PlaylistSyncService _playlistSyncService;
    private readonly ActivitiesRealtimeService _activitiesRealtime;
    private readonly ILogger<PlaylistWatchService> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastPlaylistMediaSyncUtc = new(StringComparer.OrdinalIgnoreCase);

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

    private sealed class QueueWatchOptionsInput
    {
        public required string SourceLabel { get; init; }
        public string? WatchlistSource { get; init; }
        public string? WatchlistPlaylistId { get; init; }
        public string? PreferredEngine { get; init; }
        public string? DownloadVariantMode { get; init; }
        public long? AtmosDestinationFolderId { get; init; }
        public QueueWatchRuleSet? RuleSet { get; init; }
        public string? WatchlistOrigin { get; init; }
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
        public required LibraryRecommendationService LibraryRecommendationService { get; init; }
        public required IHttpClientFactory HttpClientFactory { get; init; }
        public required ITidalAccessTokenProvider TidalAccessTokenProvider { get; init; }
    }

    public PlaylistWatchService(
        LibraryRepository libraryRepository,
        PlaylistWatchPlatformServices platformServices,
        DeezSpoTagSettingsService settingsService,
        IServiceProvider serviceProvider,
        PlaylistSyncService playlistSyncService,
        ActivitiesRealtimeService activitiesRealtime,
        ILogger<PlaylistWatchService> logger)
    {
        _libraryRepository = libraryRepository;
        _spotifyMetadataService = platformServices.SpotifyMetadataService;
        _spotifyPathfinderMetadataClient = platformServices.SpotifyPathfinderMetadataClient;
        _spotifyArtistService = platformServices.SpotifyArtistService;
        _deezerClient = platformServices.DeezerClient;
        _deezerGatewayService = platformServices.DeezerGatewayService;
        _appleCatalogService = platformServices.AppleCatalogService;
        _boomplayMetadataService = platformServices.BoomplayMetadataService;
        _libraryRecommendationService = platformServices.LibraryRecommendationService;
        _httpClientFactory = platformServices.HttpClientFactory;
        _tidalAccessTokenProvider = platformServices.TidalAccessTokenProvider;
        _settingsService = settingsService;
        _serviceProvider = serviceProvider;
        _playlistSyncService = playlistSyncService;
        _activitiesRealtime = activitiesRealtime;
        _logger = logger;
    }

    public async Task CheckWatchlistAsync(CancellationToken cancellationToken)
    {
        if (!_libraryRepository.IsConfigured)
        {
            _logger.LogDebug("Playlist watchlist skipped - library DB not configured.");
            return;
        }

        var playlists = await _libraryRepository.GetPlaylistWatchlistAsync(cancellationToken);
        if (playlists.Count == 0)
        {
            return;
        }

        foreach (var playlist in playlists)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await CheckPlaylistAsync(playlist, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Playlist watch failed for {Source}:{SourceId}", playlist.Source, playlist.SourceId);
            }
        }
    }

    public Task CheckPlaylistWatchItemAsync(
        PlaylistWatchlistDto playlist,
        CancellationToken cancellationToken,
        bool forceMediaServerSync = false)
    {
        if (playlist == null)
        {
            return Task.CompletedTask;
        }

        return CheckPlaylistAsync(playlist, cancellationToken, forceMediaServerSync);
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
        IReadOnlyList<string> Genres);

    private sealed record LivePlaylistSnapshot(
        IReadOnlyList<PlaylistTrackCandidate> Candidates,
        string? SnapshotId,
        string? Name,
        string? Description,
        string? ImageUrl,
        int? TrackCount,
        bool IsComplete);

    public sealed record PlaylistReconciliationResult(
        bool Success,
        string Message,
        int SourceTracks,
        int IgnoredTracks,
        int LocalTracks,
        int QueuedTracks,
        int CompletedTracks,
        int FailedTracks,
        PlaylistSyncResult? SyncResult);

    public async Task<PlaylistReconciliationResult> ReconcilePlaylistAsync(
        PlaylistWatchlistDto playlist,
        CancellationToken cancellationToken,
        bool forceMediaServerSync = false)
    {
        if (playlist == null)
        {
            return new PlaylistReconciliationResult(false, "Playlist not available.", 0, 0, 0, 0, 0, 0, null);
        }

        var source = NormalizeWatchSource(playlist.Source);
        var sourceId = (playlist.SourceId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(sourceId))
        {
            return new PlaylistReconciliationResult(false, "Playlist source is not available.", 0, 0, 0, 0, 0, 0, null);
        }

        var maxCandidates = MaxPlaylistCandidateFetchCount;
        var preference = await _libraryRepository.GetPlaylistWatchPreferenceAsync(source, sourceId, cancellationToken);
        var globalBlockRules = await GetGlobalPlaylistBlockRulesAsync(cancellationToken);
        var effectiveBlockRules = PlaylistTrackBlockRuleHelper.MergeRules(preference?.IgnoreRules, globalBlockRules);
        var liveSnapshot = await FetchLivePlaylistSnapshotAsync(source, sourceId, maxCandidates, cancellationToken);
        var candidates = liveSnapshot.Candidates;
        var liveTrackCount = liveSnapshot.TrackCount ?? candidates.Count;
        await TouchPlaylistWatchStateAsync(source, sourceId, liveTrackCount, liveSnapshot.SnapshotId, cancellationToken);
        await _libraryRepository.UpdatePlaylistWatchlistMetadataAsync(
            source,
            sourceId,
            liveSnapshot.Name,
            liveSnapshot.ImageUrl,
            liveSnapshot.Description,
            liveTrackCount,
            cancellationToken);
        if (candidates.Count == 0)
        {
            return new PlaylistReconciliationResult(false, "No playlist tracks were available to reconcile.", 0, 0, 0, 0, 0, 0, null);
        }

        await _libraryRepository.UpsertPlaylistTrackCandidateCacheAsync(
            source,
            sourceId,
            liveSnapshot.SnapshotId,
            JsonSerializer.Serialize(candidates),
            cancellationToken);
        await _libraryRepository.AddPlaylistWatchTracksAsync(
            source,
            sourceId,
            candidates.Select(candidate => new PlaylistWatchTrackInsert(candidate.TrackSourceId, candidate.Isrc)).ToList(),
            cancellationToken);
        if (liveSnapshot.IsComplete)
        {
            await _libraryRepository.RemovePlaylistWatchTracksNotInAsync(
                source,
                sourceId,
                candidates.Select(candidate => candidate.TrackSourceId).ToList(),
                cancellationToken);
        }

        var selection = await SelectMissingPlaylistTracksAsync(source, sourceId, candidates, cancellationToken);

        var queueResult = await QueueWatchIntentTracksAsync(
            selection.MissingTracks,
            preference?.DestinationFolderId,
            BuildQueueWatchOptions(new QueueWatchOptionsInput
            {
                SourceLabel = ResolveSourceLabel(source),
                WatchlistSource = source,
                WatchlistPlaylistId = sourceId,
                PreferredEngine = preference?.PreferredEngine,
                DownloadVariantMode = preference?.DownloadVariantMode,
                AtmosDestinationFolderId = preference?.AtmosDestinationFolderId,
                RuleSet = new QueueWatchRuleSet(preference?.RoutingRules, effectiveBlockRules),
                WatchlistOrigin = PlaylistWatchOrigin
            }),
            cancellationToken);
        await AddPlaylistWatchHistoryAsync(source, sourceId, playlist.Name, queueResult.QueuedCount, cancellationToken);

        PlaylistSyncResult? syncResult = null;
        if (forceMediaServerSync)
        {
            syncResult = await _playlistSyncService.SyncPlaylistAsync(
                playlist,
                preference,
                candidates,
                force: true,
                cancellationToken);
        }
        else
        {
            await TrySyncPlaylistToMediaServerAsync(playlist, preference, false, cancellationToken);
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Playlist watch reconciled {Source}:{SourceId}. sourceTracks={SourceTracks}, ignored={IgnoredTracks}, local={LocalTracks}, queued={QueuedTracks}, completed={CompletedTracks}, failed={FailedTracks}, deferred={Deferred}",
                source,
                sourceId,
                liveTrackCount,
                selection.IgnoredCount,
                selection.LocalCount,
                queueResult.QueuedCount,
                queueResult.CompletedCount,
                queueResult.FailedCount,
                queueResult.Deferred);
        }

        var success = !queueResult.Deferred && queueResult.FailedCount == 0;
        return new PlaylistReconciliationResult(
            success,
            ResolveReconciliationMessage(queueResult, success),
            liveTrackCount,
            selection.IgnoredCount,
            selection.LocalCount,
            queueResult.QueuedCount,
            queueResult.CompletedCount,
            queueResult.FailedCount,
            syncResult);
    }

    private async Task<PlaylistTrackSelection> SelectMissingPlaylistTracksAsync(
        string source,
        string sourceId,
        IReadOnlyList<PlaylistTrackCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var ignored = await _libraryRepository.GetPlaylistWatchIgnoredTrackIdsAsync(source, sourceId, cancellationToken);
        var localTrackIds = await ResolveLocalCandidateIdsAsync(source, candidates, cancellationToken);
        var missingTracks = new List<WatchIntentTrack>();
        var ignoredCount = 0;
        var localCount = 0;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await TryHandleKnownPlaylistTrackAsync(source, sourceId, candidate, ignored, localTrackIds, cancellationToken))
            {
                if (ignored.Contains(candidate.TrackSourceId))
                {
                    ignoredCount++;
                }
                else
                {
                    localCount++;
                }
                continue;
            }

            var watchTrack = BuildWatchIntentTrackFromCandidate(source, candidate);
            if (watchTrack != null)
            {
                missingTracks.Add(watchTrack);
            }
        }

        return new PlaylistTrackSelection(missingTracks, ignoredCount, localCount);
    }

    private async Task<bool> TryHandleKnownPlaylistTrackAsync(
        string source,
        string sourceId,
        PlaylistTrackCandidate candidate,
        HashSet<string> ignored,
        HashSet<string> localTrackIds,
        CancellationToken cancellationToken)
    {
        if (!ignored.Contains(candidate.TrackSourceId) && !localTrackIds.Contains(candidate.TrackSourceId))
        {
            return false;
        }

        await TryMarkWatchTrackCompletedAsync(source, sourceId, candidate.TrackSourceId, cancellationToken);
        return true;
    }

    private static string ResolveReconciliationMessage(QueueWatchResult queueResult, bool success)
    {
        if (queueResult.Deferred)
        {
            return "Playlist queue deferred.";
        }

        return success
            ? "Playlist reconciled."
            : "Playlist reconciled with queue failures.";
    }

    private async Task AddPlaylistWatchHistoryAsync(
        string source,
        string sourceId,
        string? playlistName,
        int queuedCount,
        CancellationToken cancellationToken)
    {
        if (queuedCount <= 0)
        {
            return;
        }

        var entry = await _libraryRepository.AddWatchlistHistoryAsync(
            new WatchlistHistoryInsert(
                source,
                PlaylistWatchType,
                sourceId,
                string.IsNullOrWhiteSpace(playlistName) ? "Playlist" : playlistName,
                PlaylistWatchType,
                queuedCount,
                QueuedStatus,
                ArtistName: null),
            cancellationToken);
        if (entry != null)
        {
            _activitiesRealtime.PublishWatchlistHistoryChanged(entry);
        }
    }

    private async Task TouchPlaylistWatchStateAsync(
        string source,
        string sourceId,
        int trackCount,
        string? snapshotId,
        CancellationToken cancellationToken)
    {
        var state = await _libraryRepository.GetPlaylistWatchStateAsync(source, sourceId, cancellationToken);
        await _libraryRepository.UpsertPlaylistWatchStateAsync(
            new LibraryRepository.PlaylistWatchStateUpsertInput(
                source,
                sourceId,
                NormalizeSnapshotId(snapshotId) ?? state?.SnapshotId,
                trackCount,
                state?.BatchNextOffset,
                state?.BatchProcessingSnapshotId,
                DateTimeOffset.UtcNow),
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
            return (await FetchLivePlaylistSnapshotAsync(normalizedSource, normalizedSourceId, MaxPlaylistCandidateFetchCount, cancellationToken)).Candidates;
        }

        var isMonitored = await _libraryRepository.IsPlaylistWatchlistedAsync(normalizedSource, normalizedSourceId, cancellationToken);
        if (!isMonitored)
        {
            return (await FetchLivePlaylistSnapshotAsync(normalizedSource, normalizedSourceId, MaxPlaylistCandidateFetchCount, cancellationToken)).Candidates;
        }

        var settings = _settingsService.LoadSettings();
        if (!settings.WatchUseSnapshotIdChecking)
        {
            return (await FetchLivePlaylistSnapshotAsync(normalizedSource, normalizedSourceId, MaxPlaylistCandidateFetchCount, cancellationToken)).Candidates;
        }

        var watchState = await _libraryRepository.GetPlaylistWatchStateAsync(normalizedSource, normalizedSourceId, cancellationToken);
        var currentSnapshotId = NormalizeSnapshotId(watchState?.SnapshotId);
        if (string.IsNullOrWhiteSpace(currentSnapshotId)
            && (string.Equals(normalizedSource, QobuzSource, StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedSource, TidalSource, StringComparison.OrdinalIgnoreCase)))
        {
            return (await FetchLivePlaylistSnapshotAsync(normalizedSource, normalizedSourceId, MaxPlaylistCandidateFetchCount, cancellationToken)).Candidates;
        }

        var cached = await _libraryRepository.GetPlaylistTrackCandidateCacheAsync(normalizedSource, normalizedSourceId, cancellationToken);
        if (cached is not null)
        {
            var cachedSnapshotId = NormalizeSnapshotId(cached.SnapshotId);
            if (string.Equals(cachedSnapshotId, currentSnapshotId, StringComparison.Ordinal))
            {
                var cachedCandidates = TryDeserializePlaylistTrackCandidates(cached.CandidatesJson);
                if (cachedCandidates is not null)
                {
                    return cachedCandidates;
                }

                _logger.LogWarning("Playlist candidate cache JSON invalid. Regenerating for Source:SourceId.");
            }
        }

        var freshSnapshot = await FetchLivePlaylistSnapshotAsync(normalizedSource, normalizedSourceId, MaxPlaylistCandidateFetchCount, cancellationToken);
        var freshCandidates = freshSnapshot.Candidates;
        await _libraryRepository.UpsertPlaylistTrackCandidateCacheAsync(
            normalizedSource,
            normalizedSourceId,
            freshSnapshot.SnapshotId ?? currentSnapshotId,
            JsonSerializer.Serialize(freshCandidates),
            cancellationToken);
        return freshCandidates;
    }

    private async Task<IReadOnlyList<PlaylistTrackCandidate>> FetchLivePlaylistTrackCandidatesAsync(
        string normalizedSource,
        string normalizedSourceId,
        int maxCandidates,
        CancellationToken cancellationToken)
        => (await FetchLivePlaylistSnapshotAsync(normalizedSource, normalizedSourceId, maxCandidates, cancellationToken)).Candidates;

    private async Task<LivePlaylistSnapshot> FetchLivePlaylistSnapshotAsync(
        string normalizedSource,
        string normalizedSourceId,
        int maxCandidates,
        CancellationToken cancellationToken)
    {
        var snapshot = normalizedSource switch
        {
            SpotifySource => await GetSpotifyPlaylistSnapshotAsync(normalizedSourceId, maxCandidates, cancellationToken),
            DeezerSource => BuildLivePlaylistSnapshot(await GetDeezerTrackCandidatesAsync(normalizedSourceId, cancellationToken)),
            SmartTracklistSource => await GetSmartTracklistSnapshotAsync(normalizedSourceId, cancellationToken),
            AppleSource => await GetAppleSnapshotAsync(normalizedSourceId, cancellationToken),
            BoomplaySource => await GetBoomplaySnapshotAsync(normalizedSourceId, cancellationToken),
            RecommendationsSource => BuildLivePlaylistSnapshot(await GetRecommendationTrackCandidatesAsync(normalizedSourceId, cancellationToken)),
            QobuzSource => BuildLivePlaylistSnapshot(await GetQobuzTrackCandidatesAsync(normalizedSourceId, cancellationToken)),
            TidalSource => await GetTidalSnapshotAsync(normalizedSourceId, cancellationToken),
            _ => BuildLivePlaylistSnapshot(Array.Empty<PlaylistTrackCandidate>())
        };
        return LimitLivePlaylistSnapshot(snapshot, maxCandidates);
    }

    private static LivePlaylistSnapshot BuildLivePlaylistSnapshot(
        IReadOnlyList<PlaylistTrackCandidate> candidates,
        string? snapshotId = null,
        string? name = null,
        string? description = null,
        string? imageUrl = null,
        int? trackCount = null,
        bool isComplete = true)
        => new(
            candidates,
            NormalizeSnapshotId(snapshotId),
            EmptyToNull(name),
            EmptyToNull(description),
            EmptyToNull(imageUrl),
            trackCount ?? candidates.Count,
            isComplete);

    private static LivePlaylistSnapshot LimitLivePlaylistSnapshot(LivePlaylistSnapshot snapshot, int maxCandidates)
    {
        var limitedCandidates = snapshot.Candidates.Count <= maxCandidates
            ? snapshot.Candidates
            : snapshot.Candidates.Take(maxCandidates).ToList();
        return snapshot with
        {
            Candidates = limitedCandidates,
            IsComplete = snapshot.IsComplete && limitedCandidates.Count == snapshot.Candidates.Count
        };
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

    private static int ResolveWatchCandidateLimit(int configuredLimit)
    {
        return Math.Clamp(configuredLimit, 1, MaxPlaylistCandidateFetchCount);
    }

    private async Task<LivePlaylistSnapshot> GetSpotifyPlaylistSnapshotAsync(
        string sourceId,
        int maxCandidates,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<PlaylistTrackCandidate>();

        if (IsSpotifyHomeTrendingSourceId(sourceId))
        {
            await AddSpotifyHomeTrendingCandidatesAsync(candidates, seen, maxCandidates, cancellationToken);
            return BuildLivePlaylistSnapshot(candidates);
        }

        if (TryGetSpotifyArtistTopTracksSourceId(sourceId, out var artistId))
        {
            await AddSpotifyArtistTopTrackCandidatesAsync(candidates, seen, artistId, cancellationToken);
            return BuildLivePlaylistSnapshot(candidates);
        }

        string? snapshotId = null;
        string? name = null;
        string? description = null;
        string? imageUrl = null;
        int? totalTracks = null;
        var pageSize = Math.Min(100, maxCandidates);
        var offset = 0;
        var isComplete = true;

        while (candidates.Count < maxCandidates)
        {
            var page = await _spotifyMetadataService.FetchPlaylistPageAsync(
                sourceId,
                offset,
                pageSize,
                cancellationToken);

            if (page == null)
            {
                isComplete = false;
                break;
            }

            snapshotId ??= page.SnapshotId;
            name ??= page.Name;
            description ??= page.Description;
            imageUrl ??= page.ImageUrl;
            totalTracks ??= page.TotalTracks;

            if (page.Tracks.Count == 0)
            {
                break;
            }

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
                        track.Genres));
                if (candidates.Count >= maxCandidates)
                {
                    isComplete = !page.HasMore
                        && (!page.TotalTracks.HasValue || candidates.Count >= page.TotalTracks.Value);
                    break;
                }
            }

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

        if (totalTracks.HasValue && candidates.Count < totalTracks.Value)
        {
            isComplete = false;
        }

        return BuildLivePlaylistSnapshot(candidates, snapshotId, name, description, imageUrl, totalTracks, isComplete);
    }

    private async Task<IReadOnlyList<PlaylistTrackCandidate>> GetSpotifyTrackCandidatesAsync(
        string sourceId,
        int maxCandidates,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<PlaylistTrackCandidate>();

        if (IsSpotifyHomeTrendingSourceId(sourceId))
        {
            await AddSpotifyHomeTrendingCandidatesAsync(candidates, seen, maxCandidates, cancellationToken);
            return candidates;
        }

        if (TryGetSpotifyArtistTopTracksSourceId(sourceId, out var artistId))
        {
            await AddSpotifyArtistTopTrackCandidatesAsync(candidates, seen, artistId, cancellationToken);
            return candidates;
        }

        await AddSpotifyPlaylistCandidatesAsync(candidates, seen, sourceId, maxCandidates, cancellationToken);
        return candidates;
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
                    track.Genres));
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
                    artistPage?.Artist?.Name,
                    track.AlbumName,
                    track.ReleaseDate,
                    track.DurationMs,
                    ExplicitFlag: null,
                    Genres: null));
        }
    }

    private async Task AddSpotifyPlaylistCandidatesAsync(
        List<PlaylistTrackCandidate> candidates,
        HashSet<string> seen,
        string sourceId,
        int maxCandidates,
        CancellationToken cancellationToken)
    {
        var pageSize = Math.Min(100, maxCandidates);
        var offset = 0;

        while (candidates.Count < maxCandidates)
        {
            var page = await _spotifyMetadataService.FetchPlaylistPageAsync(
                sourceId,
                offset,
                pageSize,
                cancellationToken);

            if (page == null || page.Tracks.Count == 0)
            {
                break;
            }

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
                        track.Genres));
                if (candidates.Count >= maxCandidates)
                {
                    break;
                }
            }

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
            NormalizeGenres(seed.Genres)));
    }

    private async Task<IReadOnlyList<PlaylistTrackCandidate>> GetDeezerTrackCandidatesAsync(
        string sourceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_deezerClient.LoggedIn)
        {
            return Array.Empty<PlaylistTrackCandidate>();
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
                Array.Empty<string>()));
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
        IReadOnlyList<string>? Genres);

    private async Task<IReadOnlyList<PlaylistTrackCandidate>> GetSmartTracklistTrackCandidatesAsync(
        string sourceId,
        CancellationToken cancellationToken)
    {
        if (!_deezerClient.LoggedIn)
        {
            return Array.Empty<PlaylistTrackCandidate>();
        }

        var playlistData = await GetSmartTracklistWatchDataAsync(sourceId, cancellationToken);
        return MapWatchIntentTrackCandidates(playlistData?.Tracks);
    }

    private async Task<LivePlaylistSnapshot> GetSmartTracklistSnapshotAsync(
        string sourceId,
        CancellationToken cancellationToken)
    {
        if (!_deezerClient.LoggedIn)
        {
            return BuildLivePlaylistSnapshot(Array.Empty<PlaylistTrackCandidate>());
        }

        var playlistData = await GetSmartTracklistWatchDataAsync(sourceId, cancellationToken);
        var candidates = MapWatchIntentTrackCandidates(playlistData?.Tracks);
        return BuildLivePlaylistSnapshot(
            candidates,
            name: playlistData?.Name,
            description: playlistData?.Description,
            imageUrl: playlistData?.ImageUrl,
            trackCount: playlistData?.TrackCount);
    }

    private async Task<IReadOnlyList<PlaylistTrackCandidate>> GetAppleTrackCandidatesAsync(
        string sourceId,
        CancellationToken cancellationToken)
    {
        var playlistData = await GetApplePlaylistWatchDataAsync(sourceId, cancellationToken);
        return MapWatchIntentTrackCandidates(playlistData?.Tracks);
    }

    private async Task<LivePlaylistSnapshot> GetAppleSnapshotAsync(
        string sourceId,
        CancellationToken cancellationToken)
    {
        var playlistData = await GetApplePlaylistWatchDataAsync(sourceId, cancellationToken);
        var candidates = MapWatchIntentTrackCandidates(playlistData?.Tracks);
        return BuildLivePlaylistSnapshot(
            candidates,
            name: playlistData?.Name,
            description: playlistData?.Description,
            imageUrl: playlistData?.ImageUrl,
            trackCount: playlistData?.TrackCount);
    }

    private async Task<IReadOnlyList<PlaylistTrackCandidate>> GetBoomplayTrackCandidatesAsync(
        string sourceId,
        CancellationToken cancellationToken)
    {
        var playlistData = await GetBoomplayPlaylistWatchDataAsync(sourceId, cancellationToken);
        return MapWatchIntentTrackCandidates(playlistData?.Tracks);
    }

    private async Task<LivePlaylistSnapshot> GetBoomplaySnapshotAsync(
        string sourceId,
        CancellationToken cancellationToken)
    {
        var playlistData = await GetBoomplayPlaylistWatchDataAsync(sourceId, cancellationToken);
        var candidates = MapWatchIntentTrackCandidates(playlistData?.Tracks);
        return BuildLivePlaylistSnapshot(
            candidates,
            name: playlistData?.Name,
            description: playlistData?.Description,
            imageUrl: playlistData?.ImageUrl,
            trackCount: playlistData?.TrackCount);
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

    private async Task<IReadOnlyList<PlaylistTrackCandidate>> GetTidalTrackCandidatesAsync(
        string sourceId,
        CancellationToken cancellationToken)
        => (await GetTidalSnapshotAsync(sourceId, cancellationToken)).Candidates;

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
        while (offset < total)
        {
            var page = await FetchTidalPlaylistItemsPageAsync(client, playlistId, token, offset, cancellationToken);
            if (page.Items.ValueKind != JsonValueKind.Array || page.Items.GetArrayLength() == 0)
            {
                break;
            }

            total = page.Total > 0 ? page.Total : total;
            foreach (var wrapper in page.Items.EnumerateArray())
            {
                TryAddTidalTrackCandidate(wrapper, seen, candidates);
            }

            offset += page.Items.GetArrayLength();
        }

        return BuildLivePlaylistSnapshot(candidates, trackCount: total == int.MaxValue ? candidates.Count : total);
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
                NormalizeGenres(track.Intent.Genres)));
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

    private async Task CheckPlaylistAsync(
        PlaylistWatchlistDto playlist,
        CancellationToken cancellationToken,
        bool forceMediaServerSync = false)
    {
        await ReconcilePlaylistAsync(
            playlist,
            cancellationToken,
            forceMediaServerSync);
    }

    private async Task<IReadOnlyList<PlaylistTrackBlockRule>> GetGlobalPlaylistBlockRulesAsync(CancellationToken cancellationToken)
    {
        var preferences = await _libraryRepository.GetPlaylistWatchPreferencesAsync(cancellationToken);
        return PlaylistTrackBlockRuleHelper.BuildGlobalRules(preferences);
    }

    private async Task TrySyncPlaylistToMediaServerAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        bool forceMediaServerSync,
        CancellationToken cancellationToken)
    {
        var source = NormalizeWatchSource(playlist.Source);
        var sourceId = (playlist.SourceId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(sourceId))
        {
            return;
        }

        var serviceToken = string.IsNullOrWhiteSpace(preference?.Service)
            ? "auto"
            : preference.Service.Trim().ToLowerInvariant();
        if (string.Equals(serviceToken, "none", StringComparison.OrdinalIgnoreCase))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Playlist media-server sync disabled for {Source}:{SourceId} by preference.",
                    source,
                    sourceId);
            }
            return;
        }
        var syncKey = $"{source}:{sourceId}:{serviceToken}";
        if (!forceMediaServerSync
            && _lastPlaylistMediaSyncUtc.TryGetValue(syncKey, out var lastSyncedUtc)
            && (DateTimeOffset.UtcNow - lastSyncedUtc) < PlaylistMediaSyncCooldown)
        {
            return;
        }

        var candidates = await GetPlaylistTrackCandidatesAsync(source, sourceId, cancellationToken);
        var result = await _playlistSyncService.SyncPlaylistAsync(
            playlist,
            preference,
            candidates,
            force: forceMediaServerSync,
            cancellationToken);

        if (!result.Success)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Playlist media-server sync skipped for {Source}:{SourceId}: {Message}",
                    source,
                    sourceId,
                    result.Message);
            }
            return;
        }

        _lastPlaylistMediaSyncUtc[syncKey] = DateTimeOffset.UtcNow;

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Playlist media-server sync completed for {Source}:{SourceId}. TargetPlaylistId={PlaylistId}, SyncedTracks={SyncedTracks}",
                source,
                sourceId,
                result.PlaylistId ?? string.Empty,
                result.SyncedTracks);
        }
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
            ? await _boomplayMetadataService.GetTrendingSongsAsync(includeTracks: true, cancellationToken)
            : await _boomplayMetadataService.GetPlaylistAsync(playlistId, includeTracks: true, cancellationToken);

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
                SourceUrl = $"https://www.boomplay.com/songs/{trackId}",
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
        return result.QueuedCount;
    }

    public async Task<int> QueueDeezerWatchTracksAsync(
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
        return result.QueuedCount;
    }

    public async Task<int> QueueAppleWatchIntentsAsync(
        IReadOnlyCollection<DownloadIntent> intents,
        ArtistWatchQueueOptions options,
        CancellationToken cancellationToken)
    {
        if (intents.Count == 0)
        {
            return 0;
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
        return result.QueuedCount;
    }

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
                    Danceability = track.Danceability,
                    Energy = track.Energy,
                    Valence = track.Valence,
                    Acousticness = track.Acousticness,
                    Instrumentalness = track.Instrumentalness,
                    Speechiness = track.Speechiness,
                    Loudness = track.Loudness,
                    Tempo = track.Tempo,
                    TimeSignature = track.TimeSignature,
                    Liveness = track.Liveness,
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

    private async Task<HashSet<string>> ResolveLocalCandidateIdsAsync(
        string source,
        IReadOnlyCollection<PlaylistTrackCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var local = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var isrcs = candidates
            .Select(static candidate => candidate.Isrc)
            .Where(static isrc => !string.IsNullOrWhiteSpace(isrc))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var byIsrc = await _libraryRepository.GetTrackIdsBySourceIdsAsync("isrc", isrcs, cancellationToken);
        foreach (var candidate in candidates.Where(candidate =>
                     !string.IsNullOrWhiteSpace(candidate.Isrc) && byIsrc.ContainsKey(candidate.Isrc)))
        {
            local.Add(candidate.TrackSourceId);
        }

        foreach (var candidate in candidates.Where(candidate => !local.Contains(candidate.TrackSourceId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var localTrackId = await _libraryRepository.FindLocalTrackIdByMetadataAsync(
                candidate.Title,
                candidate.Artist,
                candidate.Album,
                candidate.DurationMs,
                cancellationToken);
            if (localTrackId.HasValue)
            {
                local.Add(candidate.TrackSourceId);
            }
        }

        var sourceIds = candidates
            .Where(candidate => !local.Contains(candidate.TrackSourceId))
            .Select(static candidate => candidate.TrackSourceId)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var bySource = await _libraryRepository.GetTrackIdsBySourceIdsAsync(source, sourceIds, cancellationToken);
        foreach (var candidate in candidates.Where(candidate => bySource.ContainsKey(candidate.TrackSourceId)))
        {
            local.Add(candidate.TrackSourceId);
        }

        return local;
    }

    private static WatchIntentTrack? BuildWatchIntentTrackFromCandidate(string source, PlaylistTrackCandidate candidate)
    {
        var trackId = (candidate.TrackSourceId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trackId))
        {
            return null;
        }

        var sourceService = source == RecommendationsSource ? DeezerSource : source;
        var intent = new DownloadIntent
        {
            SourceService = sourceService,
            SourceUrl = BuildCandidateSourceUrl(source, trackId),
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
                intent.DeezerId = trackId;
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

        return new WatchIntentTrack(trackId, candidate.Isrc, intent);
    }

    private static string BuildCandidateSourceUrl(string source, string trackId)
        => source switch
        {
            SpotifySource => BuildSpotifyTrackUrl(trackId, null),
            DeezerSource or RecommendationsSource => BuildDeezerTrackUrl(trackId),
            AppleSource => $"https://music.apple.com/song/{Uri.EscapeDataString(trackId)}",
            QobuzSource => BuildQobuzTrackUrl(trackId),
            TidalSource => BuildTidalTrackUrl(trackId),
            _ => string.Empty
        };

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

    private static QueueWatchOptions BuildQueueWatchOptions(QueueWatchOptionsInput input)
    {
        return new QueueWatchOptions(
            input.SourceLabel,
            input.WatchlistSource,
            input.WatchlistPlaylistId,
            input.PreferredEngine,
            input.DownloadVariantMode,
            input.AtmosDestinationFolderId,
            input.RuleSet?.RoutingRules,
            input.RuleSet?.BlockRules,
            input.WatchlistOrigin);
    }

    private static long? ResolveRoutingFolderId(DownloadIntent intent, IReadOnlyList<PlaylistTrackRoutingRule>? rules, long? defaultFolderId)
    {
        if (rules is null || rules.Count == 0)
        {
            return defaultFolderId;
        }

        var matchedRule = rules
            .OrderBy(static r => r.Order)
            .FirstOrDefault(rule => RuleMatches(intent, rule.ConditionField, rule.ConditionOperator, rule.ConditionValue));

        return matchedRule?.DestinationFolderId ?? defaultFolderId;
    }

    private static bool ShouldBlockTrack(DownloadIntent intent, IReadOnlyList<PlaylistTrackBlockRule>? rules)
    {
        if (rules is null || rules.Count == 0)
        {
            return false;
        }

        return rules
            .OrderBy(static r => r.Order)
            .Any(rule => RuleMatches(intent, rule.ConditionField, rule.ConditionOperator, rule.ConditionValue));
    }

    private static bool RuleMatches(
        DownloadIntent intent,
        string conditionField,
        string conditionOperator,
        string conditionValue)
    {
        return conditionField switch
        {
            ArtistField => EvalStringCondition(intent.Artist, conditionOperator, conditionValue),
            "title" => EvalStringCondition(intent.Title, conditionOperator, conditionValue),
            AlbumField => EvalStringCondition(intent.Album, conditionOperator, conditionValue),
            "genre" => EvalGenreCondition(intent.Genres, conditionOperator, conditionValue),
            "explicit" => conditionOperator == "is_true" ? (intent.Explicit == true) : (intent.Explicit != true),
            "year" => EvalYearCondition(intent.ReleaseDate, conditionOperator, conditionValue),
            _ => false
        };
    }

    private static bool EvalStringCondition(string value, string op, string conditionValue) => op switch
    {
        "contains" => value.Contains(conditionValue, StringComparison.OrdinalIgnoreCase),
        "equals" => string.Equals(value, conditionValue, StringComparison.OrdinalIgnoreCase),
        "starts_with" => value.StartsWith(conditionValue, StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    private static bool EvalGenreCondition(List<string>? genres, string op, string conditionValue)
    {
        if (genres is null || genres.Count == 0)
        {
            return false;
        }

        var normalizedCondition = (conditionValue ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedCondition))
        {
            return false;
        }

        return genres
            .Where(static genre => !string.IsNullOrWhiteSpace(genre))
            .Select(static genre => genre.Trim())
            .Any(genre => EvalStringCondition(genre, op, normalizedCondition));
    }

    private static bool EvalYearCondition(string? releaseDate, string op, string conditionValue)
    {
        if (!TryParseReleaseYear(releaseDate, out var trackYear)
            || !int.TryParse((conditionValue ?? string.Empty).Trim(), out var ruleYear))
        {
            return false;
        }

        return op switch
        {
            "gte" => trackYear >= ruleYear,
            "lte" => trackYear <= ruleYear,
            _ => trackYear == ruleYear
        };
    }

    private static bool TryParseReleaseYear(string? releaseDate, out int year)
    {
        year = 0;
        var value = (releaseDate ?? string.Empty).Trim();
        if (value.Length < 4)
        {
            return false;
        }

        return int.TryParse(value[..4], out year);
    }

    private static string? NormalizePreferredEngine(string? engine)
    {
        if (string.IsNullOrWhiteSpace(engine))
        {
            return null;
        }

        var normalized = engine.Trim().ToLowerInvariant();
        return normalized is "auto" or DeezerSource or AppleSource or "qobuz" or "tidal" or "amazon"
            ? normalized
            : null;
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

        using var scope = _serviceProvider.CreateScope();
        var intentService = scope.ServiceProvider.GetRequiredService<DownloadIntentService>();
        var queueRepository = scope.ServiceProvider.GetRequiredService<DownloadQueueRepository>();
        var orchestrationService = scope.ServiceProvider.GetRequiredService<DownloadOrchestrationService>();
        var normalizedPreferredEngine = NormalizePreferredEngine(options.PreferredEngine);
        var normalizedDownloadVariantMode = NormalizeDownloadVariantMode(options.DownloadVariantMode);
        var capacity = await TryResolveWatchQueueCapacityAsync(queueRepository, orchestrationService, options, cancellationToken);
        if (capacity is null)
        {
            return new QueueWatchResult(0, 0, 0, Deferred: true);
        }

        var queueContext = new QueuedWatchIntentContext(intentService, options, normalizedDownloadVariantMode);
        var queuedCount = 0;
        var completedCount = 0;
        var failedCount = 0;
        var deferred = false;
        foreach (var track in tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (queuedCount >= capacity.Value.Remaining)
            {
                LogWatchQueueCapacityFilled(options, queuedCount, capacity.Value);
                break;
            }

            var intent = track.Intent;
            if (await HandleBlockedWatchIntentAsync(intent, track, options, cancellationToken))
            {
                completedCount++;
                continue;
            }

            intent = PrepareWatchIntent(
                intent,
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
                cancellationToken);
            if (result is null)
            {
                await TryMarkWatchTrackStatusAsync(
                    options.WatchlistSource,
                    options.WatchlistPlaylistId,
                    track.TrackId,
                    "failed",
                    cancellationToken);
                failedCount++;
                continue;
            }

            if (ShouldDeferWatchTrack(result))
            {
                LogWatchTrackDeferred(options.SourceLabel, track.TrackId, result.Message);
                deferred = true;
                break;
            }

            var trackResult = await HandleQueuedWatchIntentResultAsync(
                queueContext,
                result,
                track,
                intent,
                remainingCapacity: capacity.Value.Remaining - queuedCount,
                cancellationToken);
            queuedCount += trackResult.QueuedCount;
            if (trackResult.Completed)
            {
                completedCount++;
            }

            if (trackResult.Failed)
            {
                failedCount++;
            }
        }

        return new QueueWatchResult(queuedCount, completedCount, failedCount, Deferred: deferred);
    }

    private async Task<WatchQueueCapacity?> TryResolveWatchQueueCapacityAsync(
        DownloadQueueRepository queueRepository,
        DownloadOrchestrationService orchestrationService,
        QueueWatchOptions options,
        CancellationToken cancellationToken)
    {
        var settings = _settingsService.LoadSettings();
        var capacity = await ResolveWatchQueueCapacityAsync(queueRepository, settings.WatchMaxTracksPerPlaylistCheck, cancellationToken);
        var unfinishedWatchlistCount = await queueRepository.GetUnfinishedWatchlistDownloadCountAsync(cancellationToken);
        if (unfinishedWatchlistCount > 0 && capacity.ActiveCount > 0)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "{Source} watch queue deferred because previous watchlist downloads have not finished moving to destination. unfinished={UnfinishedWatchlistDownloads}",
                    options.SourceLabel,
                    unfinishedWatchlistCount);
            }

            return null;
        }

        if (unfinishedWatchlistCount > 0 && capacity.ActiveCount <= 0 && _logger.IsEnabled(LogLevel.Warning))
        {
            _logger.LogWarning(
                "{Source} watch queue found unfinished watchlist rows but no active downloads. Continuing queue flow to avoid stale watch deadlock. unfinished={UnfinishedWatchlistDownloads}",
                options.SourceLabel,
                unfinishedWatchlistCount);
        }

        if (capacity.Remaining <= 0)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "{Source} watch queue deferred because active downloads already meet the watchlist cap. active={ActiveCount}, cap={QueueCap}",
                    options.SourceLabel,
                    capacity.ActiveCount,
                    capacity.Limit);
            }

            return null;
        }

        var downloadGate = await orchestrationService.EvaluateDownloadGateAsync(cancellationToken);
        if (downloadGate.Allowed)
        {
            return capacity;
        }

        LogDownloadGateDeferred(options.SourceLabel, downloadGate.Message);
        return null;
    }

    private void LogWatchQueueCapacityFilled(QueueWatchOptions options, int queuedCount, WatchQueueCapacity capacity)
    {
        if (!_logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        _logger.LogInformation(
            "{Source} watch queue deferred remaining tracks after filling watchlist capacity. queuedThisRun={QueuedThisRun}, activeAtStart={ActiveCount}, cap={QueueCap}",
            options.SourceLabel,
            queuedCount,
            capacity.ActiveCount,
            capacity.Limit);
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

    private static async Task<WatchQueueCapacity> ResolveWatchQueueCapacityAsync(
        DownloadQueueRepository queueRepository,
        int configuredLimit,
        CancellationToken cancellationToken)
    {
        var limit = Math.Max(1, configuredLimit);
        var activeCount = await queueRepository.GetActiveDownloadCountAsync(cancellationToken);
        return new WatchQueueCapacity(limit, activeCount);
    }

    private async Task<QueueWatchTrackResult> HandleQueuedWatchIntentResultAsync(
        QueuedWatchIntentContext context,
        DownloadIntentResult result,
        WatchIntentTrack track,
        DownloadIntent intent,
        int remainingCapacity,
        CancellationToken cancellationToken)
    {
        var queuedCount = 0;
        if (result.Success)
        {
            queuedCount++;
            if (remainingCapacity - queuedCount > 0)
            {
                queuedCount += await TryQueueAtmosIntentAsync(
                    context.IntentService,
                    context.NormalizedDownloadVariantMode,
                    intent,
                    new AtmosQueueRequest(context.Options.SourceLabel, track.TrackId, AfterPrimarySkip: false),
                    context.Options,
                    cancellationToken);
            }
            return new QueueWatchTrackResult(queuedCount, Completed: false, Failed: false);
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
            if (remainingCapacity > 0)
            {
                queuedCount += await TryQueueAtmosIntentAsync(
                    context.IntentService,
                    context.NormalizedDownloadVariantMode,
                    intent,
                    new AtmosQueueRequest(context.Options.SourceLabel, track.TrackId, AfterPrimarySkip: true),
                    context.Options,
                    cancellationToken);
            }
            await TryMarkWatchTrackCompletedAsync(
                context.Options.WatchlistSource,
                context.Options.WatchlistPlaylistId,
                track.TrackId,
                cancellationToken);
            return new QueueWatchTrackResult(queuedCount, Completed: true, Failed: false);
        }

        LogWatchEnqueueFailure(context.Options, track, result);
        await TryMarkWatchTrackStatusAsync(
            context.Options.WatchlistSource,
            context.Options.WatchlistPlaylistId,
            track.TrackId,
            "failed",
            cancellationToken);
        return new QueueWatchTrackResult(queuedCount, Completed: false, Failed: true);
    }

    private async Task<bool> HandleBlockedWatchIntentAsync(
        DownloadIntent intent,
        WatchIntentTrack track,
        QueueWatchOptions options,
        CancellationToken cancellationToken)
    {
        if (!ShouldBlockTrack(intent, options.BlockRules))
        {
            return false;
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "{Source} watch skipped blocked track {TrackId} ({Title} - {Artist}).",
                options.SourceLabel,
                track.TrackId,
                intent.Title,
                intent.Artist);
        }
        await TryPersistWatchTrackIgnoreAsync(
            options.WatchlistSource,
            options.WatchlistPlaylistId,
            track,
            cancellationToken);
        await TryMarkWatchTrackCompletedAsync(
            options.WatchlistSource,
            options.WatchlistPlaylistId,
            track.TrackId,
            cancellationToken);
        return true;
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
        else if (!string.IsNullOrWhiteSpace(normalizedPreferredEngine))
        {
            intent.PreferredEngine = normalizedPreferredEngine;
        }

        if (HasWatchlistContext(options.WatchlistSource, options.WatchlistPlaylistId))
        {
            intent.WatchlistSource = options.WatchlistSource!;
            intent.WatchlistPlaylistId = options.WatchlistPlaylistId!;
            intent.WatchlistTrackId = trackId;
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
            HasAtmos = intent.HasAtmos,
            HasAppleDigitalMaster = intent.HasAppleDigitalMaster,
            AllowQualityUpgrade = intent.AllowQualityUpgrade
        };
    }

    private static bool HasResolvableSourceIdentity(DownloadIntent intent)
    {
        return !string.IsNullOrWhiteSpace(intent.SourceUrl)
               || !string.IsNullOrWhiteSpace(intent.SpotifyId)
               || !string.IsNullOrWhiteSpace(intent.DeezerId)
               || !string.IsNullOrWhiteSpace(intent.AppleId);
    }

    private async Task<DownloadIntentResult?> TryQueuePrimaryIntentAsync(
        DownloadIntentService intentService,
        DownloadIntent intent,
        string sourceLabel,
        string trackId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await intentService.EnqueueAsync(intent, cancellationToken);
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

        var atmosIntent = CreateAtmosOnlyIntent(baseIntent, options.AtmosDestinationFolderId);
        try
        {
            var atmosResult = await intentService.EnqueueAsync(atmosIntent, cancellationToken);
            return atmosResult.Success ? 1 : 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
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
                case "queue_duplicate":
                case "queue_insert_ignored":
                case "queue_quality_not_higher":
                case "queue_upgrade_in_progress":
                case "blocklist_match":
                    return true;
            }
        }

        return false;
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
        foreach (var propertyName in JsonStringObjectPropertyNames)
        {
            if (value.TryGetProperty(propertyName, out var candidate)
                && candidate.ValueKind == JsonValueKind.String)
            {
                return candidate.GetString();
            }
        }
        return null;
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
        string? DownloadVariantMode,
        long? AtmosDestinationFolderId,
        IReadOnlyList<PlaylistTrackRoutingRule>? RoutingRules,
        IReadOnlyList<PlaylistTrackBlockRule>? BlockRules,
        string? WatchlistOrigin);

    private sealed record WatchIntentTrack(string TrackId, string? Isrc, DownloadIntent Intent);

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
