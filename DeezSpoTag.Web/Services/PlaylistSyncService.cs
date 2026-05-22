using DeezSpoTag.Integrations.Jellyfin;
using DeezSpoTag.Integrations.Plex;
using DeezSpoTag.Services.Library;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DeezSpoTag.Web.Services;

public sealed class PlaylistSyncService
{
    private sealed record PlexConnection(string Url, string Token, string MachineIdentifier);
    private sealed record JellyfinConnection(string Url, string ApiKey, string UserId);

    private sealed record SyncTrackSummary(
        string SourceTrackId,
        string? Isrc,
        string Name,
        string Artists,
        string Album,
        string? ReleaseDate,
        bool? Explicit,
        IReadOnlyList<string> Genres,
        int? DurationMs);

    private sealed record SyncMatchSummary(
        List<string> TargetIds,
        int SourceTracks,
        int LocalMatches,
        int TargetMatches,
        int MissingTracks,
        int MetadataMatches,
        int SearchMatches);

    private const string SpotifySource = "spotify";
    private const string IsrcSource = "isrc";
    private const string PlexService = "plex";
    private const string JellyfinService = "jellyfin";
    private const string SyncModeMirror = "mirror";
    private const string SyncModeAppend = "append";
    private const int DurationToleranceMs = 2000;
    private const int PlexSequentialSearchFallbackLimit = 25;
    private readonly LibraryRepository _libraryRepository;
    private readonly SpotifyMetadataService _spotifyMetadataService;
    private readonly PlexApiClient _plexApiClient;
    private readonly JellyfinApiClient _jellyfinApiClient;
    private readonly PlatformAuthService _authService;
    private readonly PlaylistVisualService _playlistVisualService;
    private readonly MediaServerLibraryRefreshService _mediaServerRefreshService;
    private readonly ILogger<PlaylistSyncService> _logger;

    public PlaylistSyncService(PlaylistSyncDependencies dependencies)
    {
        _libraryRepository = dependencies.LibraryRepository;
        _spotifyMetadataService = dependencies.SpotifyMetadataService;
        _plexApiClient = dependencies.PlexApiClient;
        _jellyfinApiClient = dependencies.JellyfinApiClient;
        _authService = dependencies.AuthService;
        _playlistVisualService = dependencies.PlaylistVisualService;
        _mediaServerRefreshService = dependencies.MediaServerRefreshService;
        _logger = dependencies.Logger;
    }

    public sealed class PlaylistSyncDependencies
    {
        public required LibraryRepository LibraryRepository { get; init; }
        public required SpotifyMetadataService SpotifyMetadataService { get; init; }
        public required PlexApiClient PlexApiClient { get; init; }
        public required JellyfinApiClient JellyfinApiClient { get; init; }
        public required PlatformAuthService AuthService { get; init; }
        public required PlaylistVisualService PlaylistVisualService { get; init; }
        public required MediaServerLibraryRefreshService MediaServerRefreshService { get; init; }
        public required ILogger<PlaylistSyncService> Logger { get; init; }
    }

    public Task<PlaylistSyncResult> SyncSpotifyPlaylistAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        bool force,
        CancellationToken cancellationToken)
    {
        return SyncPlaylistAsync(playlist, preference, trackCandidates: null, force, cancellationToken);
    }

    public sealed record PlaylistMergeSourceInput(
        PlaylistWatchlistDto Playlist,
        PlaylistWatchPreferenceDto? Preference,
        IReadOnlyList<PlaylistWatchService.PlaylistTrackCandidate> TrackCandidates);

    public sealed record PlaylistMergeSyncRequest(
        string? PlaylistName,
        string? Description,
        string? SourceUsername,
        string? SyncMode,
        bool SyncToPlex,
        bool SyncToJellyfin);

    public sealed record PlaylistMergeTargetResult(
        string Target,
        bool Success,
        string Message,
        string? PlaylistId,
        int SyncedTracks);

    public sealed record PlaylistMergeSyncResult(
        bool Success,
        string Message,
        int SourcePlaylists,
        int CandidateTracks,
        int MergedTracks,
        IReadOnlyList<PlaylistMergeTargetResult> Targets);

    public sealed record PlaylistTrackSyncReadiness(
        bool Ready,
        bool Terminal,
        string Message,
        string? Service = null,
        long? LocalTrackId = null,
        string? TargetId = null);

    public async Task<PlaylistMergeSyncResult> MergeAndSyncPlaylistsAsync(
        IReadOnlyList<PlaylistMergeSourceInput> mergeSources,
        PlaylistMergeSyncRequest request,
        CancellationToken cancellationToken)
    {
        var selectedSources = BuildValidMergeSourceList(mergeSources);
        var validationFailure = ValidateMergeRequest(request, selectedSources.Count);
        if (validationFailure is not null)
        {
            return validationFailure;
        }

        var (candidateTrackCount, mergedTracks) = await BuildMergedTracksAsync(selectedSources, cancellationToken);

        if (mergedTracks.Count == 0)
        {
            return new PlaylistMergeSyncResult(
                false,
                "No eligible tracks remained after blocked/ignored filtering.",
                selectedSources.Count,
                candidateTrackCount,
                0,
                Array.Empty<PlaylistMergeTargetResult>());
        }

        var now = DateTimeOffset.UtcNow;
        var mergedPlaylist = new PlaylistWatchlistDto(
            Id: 0,
            Source: "merged",
            SourceId: Guid.NewGuid().ToString("N"),
            Name: ResolveMergedPlaylistName(request.PlaylistName),
            ImageUrl: selectedSources
                .Select(source => source.Playlist.ImageUrl)
                .FirstOrDefault(static imageUrl => !string.IsNullOrWhiteSpace(imageUrl)),
            Description: BuildMergedPlaylistDescription(
                request.Description,
                selectedSources.Select(source => source.Playlist),
                request.SourceUsername),
            TrackCount: mergedTracks.Count,
            CreatedAt: now);

        var syncMode = NormalizeSyncMode(request.SyncMode);
        var targets = await SyncMergedPlaylistTargetsAsync(
            request,
            mergedPlaylist,
            mergedTracks,
            syncMode,
            now,
            cancellationToken);

        var anySucceeded = targets.Any(static target => target.Success);
        var allSucceeded = targets.Count > 0 && targets.All(static target => target.Success);
        string message;
        if (allSucceeded)
        {
            message = "Merged playlist synced successfully.";
        }
        else if (anySucceeded)
        {
            message = "Merged playlist synced to some targets. Review target results.";
        }
        else
        {
            message = "Merged playlist sync failed on all selected targets.";
        }
        return new PlaylistMergeSyncResult(
            anySucceeded,
            message,
            selectedSources.Count,
            candidateTrackCount,
            mergedTracks.Count,
            targets);
    }

    private static List<PlaylistMergeSourceInput> BuildValidMergeSourceList(IReadOnlyList<PlaylistMergeSourceInput> mergeSources)
    {
        return (mergeSources ?? Array.Empty<PlaylistMergeSourceInput>())
            .Where(source => source?.Playlist is not null
                && !string.IsNullOrWhiteSpace(source.Playlist.Source)
                && !string.IsNullOrWhiteSpace(source.Playlist.SourceId))
            .ToList();
    }

    private static PlaylistMergeSyncResult? ValidateMergeRequest(
        PlaylistMergeSyncRequest? request,
        int selectedSourceCount)
    {
        if (request == null)
        {
            return new PlaylistMergeSyncResult(
                false,
                "Merge request is required.",
                0,
                0,
                0,
                Array.Empty<PlaylistMergeTargetResult>());
        }

        if (selectedSourceCount < 2)
        {
            return new PlaylistMergeSyncResult(
                false,
                "Select at least two monitored playlists to merge.",
                selectedSourceCount,
                0,
                0,
                Array.Empty<PlaylistMergeTargetResult>());
        }

        if (!request.SyncToPlex && !request.SyncToJellyfin)
        {
            return new PlaylistMergeSyncResult(
                false,
                "Select at least one destination server (Plex or Jellyfin).",
                selectedSourceCount,
                0,
                0,
                Array.Empty<PlaylistMergeTargetResult>());
        }

        return null;
    }

    private async Task<(int CandidateTrackCount, List<SyncTrackSummary> MergedTracks)> BuildMergedTracksAsync(
        IReadOnlyList<PlaylistMergeSourceInput> selectedSources,
        CancellationToken cancellationToken)
    {
        var candidateTrackCount = 0;
        var dedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mergedTracks = new List<SyncTrackSummary>();
        foreach (var source in selectedSources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var candidates = (source.TrackCandidates ?? Array.Empty<PlaylistWatchService.PlaylistTrackCandidate>())
                .Select(ToSyncTrackSummary)
                .ToList();
            candidateTrackCount += candidates.Count;

            var filteredTracks = await FilterTracksForSyncAsync(
                source.Playlist,
                source.Preference,
                candidates,
                cancellationToken);

            foreach (var track in filteredTracks)
            {
                var dedupeKey = BuildMergeTrackDedupKey(track);
                if (dedupe.Add(dedupeKey))
                {
                    mergedTracks.Add(track);
                }
            }
        }

        return (candidateTrackCount, mergedTracks);
    }

    private async Task<List<PlaylistMergeTargetResult>> SyncMergedPlaylistTargetsAsync(
        PlaylistMergeSyncRequest request,
        PlaylistWatchlistDto mergedPlaylist,
        IReadOnlyList<SyncTrackSummary> mergedTracks,
        string syncMode,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var targets = new List<PlaylistMergeTargetResult>();
        if (request.SyncToPlex)
        {
            var result = await SyncToPlexAsync(
                mergedPlaylist,
                CreateMergedPlaylistPreference(mergedPlaylist, PlexService, syncMode, now),
                mergedTracks,
                cancellationToken);
            targets.Add(new PlaylistMergeTargetResult(
                PlexService,
                result.Success,
                result.Message,
                result.PlaylistId,
                result.SyncedTracks));
        }

        if (request.SyncToJellyfin)
        {
            var result = await SyncToJellyfinAsync(
                mergedPlaylist,
                CreateMergedPlaylistPreference(mergedPlaylist, JellyfinService, syncMode, now),
                mergedTracks,
                cancellationToken);
            targets.Add(new PlaylistMergeTargetResult(
                JellyfinService,
                result.Success,
                result.Message,
                result.PlaylistId,
                result.SyncedTracks));
        }

        return targets;
    }

    private static PlaylistWatchPreferenceDto CreateMergedPlaylistPreference(
        PlaylistWatchlistDto mergedPlaylist,
        string service,
        string syncMode,
        DateTimeOffset now)
    {
        return new PlaylistWatchPreferenceDto(
            Source: "merged",
            SourceId: mergedPlaylist.SourceId,
            DestinationFolderId: null,
            Service: service,
            PreferredEngine: null,
            DownloadVariantMode: null,
            SyncMode: syncMode,
            UpdateArtwork: true,
            ReuseSavedArtwork: false,
            CreatedAt: now,
            UpdatedAt: now);
    }

    public async Task<PlaylistSyncResult> SyncPlaylistAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        IReadOnlyList<PlaylistWatchService.PlaylistTrackCandidate>? trackCandidates,
        bool force,
        CancellationToken cancellationToken)
    {
        if (playlist == null || string.IsNullOrWhiteSpace(playlist.SourceId))
        {
            return PlaylistSyncResult.Failed("Playlist not available.");
        }

        var service = await ResolveTargetServiceAsync(preference, cancellationToken);
        if (string.IsNullOrWhiteSpace(service))
        {
            return PlaylistSyncResult.Failed("No target server selected.");
        }

        if (force)
        {
            await _mediaServerRefreshService.RefreshAsync(service, cancellationToken);
        }

        var loadResult = await LoadTracksForSyncAsync(playlist, trackCandidates, cancellationToken);
        if (!string.IsNullOrWhiteSpace(loadResult.ErrorMessage))
        {
            return PlaylistSyncResult.Failed(loadResult.ErrorMessage);
        }

        var tracks = await FilterTracksForSyncAsync(
            playlist,
            preference,
            loadResult.Tracks,
            cancellationToken);
        if (tracks.Count == 0)
        {
            return PlaylistSyncResult.Failed("No eligible tracks after blocked/ignored filtering.");
        }

        return service switch
        {
            PlexService => await SyncToPlexAsync(playlist, preference, tracks, cancellationToken),
            JellyfinService => await SyncToJellyfinAsync(playlist, preference, tracks, cancellationToken),
            _ => PlaylistSyncResult.Failed("Unsupported playlist sync target.")
        };
    }

    public async Task<PlaylistTrackSyncReadiness> CheckTrackReadyForAutomaticSyncAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        PlaylistWatchService.PlaylistTrackCandidate candidate,
        CancellationToken cancellationToken)
    {
        if (playlist == null || candidate == null)
        {
            return new PlaylistTrackSyncReadiness(false, true, "Playlist or track candidate is unavailable.");
        }

        var service = await ResolveTargetServiceAsync(preference, cancellationToken);
        if (string.IsNullOrWhiteSpace(service))
        {
            return new PlaylistTrackSyncReadiness(false, true, "No target server selected.");
        }

        if (string.Equals(service, "none", StringComparison.OrdinalIgnoreCase))
        {
            return new PlaylistTrackSyncReadiness(false, true, "Playlist sync target is disabled.", service);
        }

        var track = ToSyncTrackSummary(candidate);
        var localTrackId = await ResolveLocalTrackIdAsync(playlist.Source, track, cancellationToken);
        if (!localTrackId.HasValue)
        {
            return new PlaylistTrackSyncReadiness(
                false,
                false,
                "Track is not visible in the DeezSpoTag library yet.",
                service);
        }

        return service switch
        {
            PlexService => await CheckPlexTrackReadyAsync(localTrackId.Value, track, cancellationToken),
            JellyfinService => await CheckJellyfinTrackReadyAsync(localTrackId.Value, track, cancellationToken),
            _ => new PlaylistTrackSyncReadiness(false, true, "Unsupported playlist sync target.", service, localTrackId)
        };
    }

    public async Task<PlaylistTrackSyncReadiness> CheckPlaylistReadyForAutomaticSyncAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        IReadOnlyList<PlaylistWatchService.PlaylistTrackCandidate> candidates,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return new PlaylistTrackSyncReadiness(false, true, "Playlist has no track candidates.");
        }

        var eligibleTracks = await FilterTracksForSyncAsync(
            playlist,
            preference,
            candidates.Select(ToSyncTrackSummary).ToList(),
            cancellationToken);
        var eligibleIds = eligibleTracks
            .Select(static track => track.SourceTrackId)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (eligibleIds.Count == 0)
        {
            return new PlaylistTrackSyncReadiness(false, true, "No eligible tracks after blocked/ignored filtering.");
        }

        var checkedLocalTracks = 0;
        foreach (var candidate in candidates.Where(candidate => eligibleIds.Contains(candidate.TrackSourceId)))
        {
            var track = ToSyncTrackSummary(candidate);
            var localTrackId = await ResolveLocalTrackIdAsync(playlist.Source, track, cancellationToken);
            if (!localTrackId.HasValue)
            {
                continue;
            }

            checkedLocalTracks++;
            var readiness = await CheckTargetTrackReadyAsync(preference, track, localTrackId.Value, cancellationToken);
            if (!readiness.Ready)
            {
                return readiness;
            }
        }

        if (checkedLocalTracks == 0)
        {
            return new PlaylistTrackSyncReadiness(
                false,
                false,
                "No eligible playlist tracks are visible in the DeezSpoTag library yet.");
        }

        return new PlaylistTrackSyncReadiness(true, false, "All locally available playlist tracks are visible in the target server.");
    }

    private async Task<PlaylistTrackSyncReadiness> CheckTargetTrackReadyAsync(
        PlaylistWatchPreferenceDto? preference,
        SyncTrackSummary track,
        long localTrackId,
        CancellationToken cancellationToken)
    {
        var service = await ResolveTargetServiceAsync(preference, cancellationToken);
        if (string.IsNullOrWhiteSpace(service))
        {
            return new PlaylistTrackSyncReadiness(false, true, "No target server selected.");
        }

        if (string.Equals(service, "none", StringComparison.OrdinalIgnoreCase))
        {
            return new PlaylistTrackSyncReadiness(false, true, "Playlist sync target is disabled.", service, localTrackId);
        }

        return service switch
        {
            PlexService => await CheckPlexTrackReadyAsync(localTrackId, track, cancellationToken),
            JellyfinService => await CheckJellyfinTrackReadyAsync(localTrackId, track, cancellationToken),
            _ => new PlaylistTrackSyncReadiness(false, true, "Unsupported playlist sync target.", service, localTrackId)
        };
    }

    private async Task<(IReadOnlyList<SyncTrackSummary> Tracks, string? ErrorMessage)> LoadTracksForSyncAsync(
        PlaylistWatchlistDto playlist,
        IReadOnlyList<PlaylistWatchService.PlaylistTrackCandidate>? trackCandidates,
        CancellationToken cancellationToken)
    {
        var source = NormalizeSource(playlist.Source);
        if (trackCandidates is { Count: > 0 })
        {
            return (trackCandidates.Select(ToSyncTrackSummary).ToList(), null);
        }

        if (string.Equals(source, SpotifySource, StringComparison.OrdinalIgnoreCase))
        {
            var snapshot = await _spotifyMetadataService.FetchPlaylistSnapshotAsync(playlist.SourceId, cancellationToken);
            if (snapshot != null && snapshot.Tracks.Count > 0)
            {
                return (snapshot.Tracks.Select(ToSyncTrackSummary).ToList(), null);
            }

            return (Array.Empty<SyncTrackSummary>(), "Spotify playlist could not be loaded.");
        }

        return (Array.Empty<SyncTrackSummary>(), "Track candidates are unavailable for this source. Open playlist settings once and retry sync.");
    }

    private async Task<PlaylistSyncResult> SyncToPlexAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        IReadOnlyList<SyncTrackSummary> tracks,
        CancellationToken cancellationToken)
    {
        var (plex, configurationError) = await TryLoadConfiguredPlexAsync();
        if (configurationError != null)
        {
            return configurationError;
        }

        if (plex == null)
        {
            return PlaylistSyncResult.Failed("Plex is not configured.");
        }

        var playlistName = ResolvePlaylistName(playlist);
        var orderedTrackIds = await ResolveOrderedTrackIdsAsync(playlist.Source, tracks, cancellationToken);
        var matchSummary = await ResolvePlexRatingKeysAsync(plex, tracks, orderedTrackIds, cancellationToken);
        if (matchSummary.TargetIds.Count == 0)
        {
            _logger.LogWarning(
                "No Plex matches found for playlist {Source}:{SourceId}. sourceTracks={SourceTracks}, localMatches={LocalMatches}, missingTracks={MissingTracks}",
                playlist.Source,
                playlist.SourceId,
                matchSummary.SourceTracks,
                matchSummary.LocalMatches,
                matchSummary.MissingTracks);
            return BuildFailedResult(
                BuildSyncMessage("No Plex matches found for this playlist.", matchSummary),
                matchSummary);
        }

        var syncMode = NormalizeSyncMode(preference?.SyncMode);
        var appendMissingOnly = string.Equals(syncMode, SyncModeAppend, StringComparison.OrdinalIgnoreCase);
        if (!appendMissingOnly && ShouldBlockUnsafeMirrorSync(matchSummary))
        {
            _logger.LogWarning(
                "Blocked unsafe Plex mirror sync for playlist {Source}:{SourceId}. sourceTracks={SourceTracks}, localMatches={LocalMatches}, targetMatches={TargetMatches}",
                playlist.Source,
                playlist.SourceId,
                matchSummary.SourceTracks,
                matchSummary.LocalMatches,
                matchSummary.TargetMatches);
            return BuildFailedResult(
                BuildSyncMessage("Mirror sync blocked because the target server sees fewer tracks than the local library.", matchSummary),
                matchSummary);
        }

        var playlistId = await _plexApiClient.CreateOrUpdatePlaylistAsync(
            plex.Url,
            plex.Token,
            plex.MachineIdentifier,
            playlistName,
            matchSummary.TargetIds,
            options: new PlexApiClient.PlaylistUpsertOptions(
                AppendMissingOnly: appendMissingOnly),
            cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return BuildFailedResult(
                BuildSyncMessage("Failed to create or update Plex playlist.", matchSummary),
                matchSummary);
        }

        await _plexApiClient.UpdatePlaylistMetadataAsync(
            plex.Url,
            plex.Token,
            playlistId,
            playlistName,
            playlist.Description,
            cancellationToken);

        await SyncPlexPlaylistArtworkAsync(plex, playlist, preference, playlistId, cancellationToken);

        var modeLabel = appendMissingOnly ? "append" : "mirror";
        return BuildSuccessResult(
            BuildSyncMessage($"Playlist synced ({modeLabel}).", matchSummary),
            playlistId,
            matchSummary,
            matchSummary.TargetMatches);
    }

    private async Task<PlaylistSyncResult> SyncToJellyfinAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        IReadOnlyList<SyncTrackSummary> tracks,
        CancellationToken cancellationToken)
    {
        var (jellyfin, configurationError) = await TryLoadConfiguredJellyfinAsync();
        if (configurationError != null)
        {
            return configurationError;
        }

        if (jellyfin == null)
        {
            return PlaylistSyncResult.Failed("Jellyfin is not configured.");
        }

        var playlistName = ResolvePlaylistName(playlist);
        var orderedTrackIds = await ResolveOrderedTrackIdsAsync(playlist.Source, tracks, cancellationToken);
        var itemIds = await ResolveJellyfinItemIdsAsync(jellyfin, tracks, cancellationToken);
        var matchSummary = new SyncMatchSummary(
            itemIds,
            SourceTracks: tracks.Count,
            LocalMatches: orderedTrackIds.Count(static id => id > 0),
            TargetMatches: itemIds.Count,
            MissingTracks: Math.Max(0, tracks.Count - itemIds.Count),
            MetadataMatches: 0,
            SearchMatches: itemIds.Count);
        if (itemIds.Count == 0)
        {
            _logger.LogWarning("No Jellyfin matches found for playlist {Source}:{SourceId}.", playlist.Source, playlist.SourceId);
            return BuildFailedResult(
                BuildSyncMessage("No Jellyfin matches found for this playlist.", matchSummary),
                matchSummary);
        }

        var syncMode = NormalizeSyncMode(preference?.SyncMode);
        var appendMissingOnly = string.Equals(syncMode, SyncModeAppend, StringComparison.OrdinalIgnoreCase);
        if (!appendMissingOnly && ShouldBlockUnsafeMirrorSync(matchSummary))
        {
            _logger.LogWarning(
                "Blocked unsafe Jellyfin mirror sync for playlist {Source}:{SourceId}. sourceTracks={SourceTracks}, localMatches={LocalMatches}, targetMatches={TargetMatches}",
                playlist.Source,
                playlist.SourceId,
                matchSummary.SourceTracks,
                matchSummary.LocalMatches,
                matchSummary.TargetMatches);
            return BuildFailedResult(
                BuildSyncMessage("Mirror sync blocked because the target server sees fewer tracks than the local library.", matchSummary),
                matchSummary);
        }

        var playlistId = await _jellyfinApiClient.FindPlaylistIdByNameAsync(
            jellyfin.Url,
            jellyfin.ApiKey,
            jellyfin.UserId,
            playlistName,
            cancellationToken);

        var syncedTracks = 0;
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            var createdPlaylistId = await _jellyfinApiClient.CreatePlaylistAsync(
                jellyfin.Url,
                jellyfin.ApiKey,
                jellyfin.UserId,
                playlistName,
                itemIds,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(createdPlaylistId))
            {
                return BuildFailedResult(
                    BuildSyncMessage("Failed to create Jellyfin playlist.", matchSummary),
                    matchSummary);
            }

            playlistId = createdPlaylistId;
            syncedTracks = itemIds.Count;
        }
        else
        {
            var syncItemsResult = await SyncExistingJellyfinPlaylistItemsAsync(
                jellyfin.Url,
                jellyfin.ApiKey,
                jellyfin.UserId,
                playlistId,
                itemIds,
                appendMissingOnly,
                cancellationToken);
            if (!syncItemsResult.Success)
            {
                return BuildFailedResult(
                    BuildSyncMessage(syncItemsResult.ErrorMessage ?? "Failed to sync Jellyfin playlist.", matchSummary),
                    matchSummary);
            }

            syncedTracks = syncItemsResult.SyncedTracks;
        }

        if (!string.IsNullOrWhiteSpace(playlist.Description))
        {
            await _jellyfinApiClient.UpdateItemOverviewAsync(
                jellyfin.Url,
                jellyfin.ApiKey,
                playlistId,
                playlist.Description,
                cancellationToken);
        }

        await SyncJellyfinPlaylistArtworkAsync(jellyfin, playlist, preference, playlistId, cancellationToken);

        var modeLabel = appendMissingOnly ? "append" : "mirror";
        return BuildSuccessResult(
            BuildSyncMessage($"Playlist synced ({modeLabel}).", matchSummary),
            playlistId,
            matchSummary with { TargetIds = itemIds },
            syncedTracks);
    }

    private async Task<(bool Success, string? ErrorMessage, int SyncedTracks)> SyncExistingJellyfinPlaylistItemsAsync(
        string url,
        string apiKey,
        string userId,
        string playlistId,
        IReadOnlyList<string> itemIds,
        bool appendMissingOnly,
        CancellationToken cancellationToken)
    {
        var entries = await _jellyfinApiClient.GetPlaylistEntriesAsync(
            url,
            apiKey,
            userId,
            playlistId,
            cancellationToken);
        if (appendMissingOnly)
        {
            return await AppendMissingJellyfinItemsAsync(url, apiKey, userId, playlistId, itemIds, entries, cancellationToken);
        }

        return await ReplaceJellyfinPlaylistItemsAsync(url, apiKey, userId, playlistId, itemIds, entries, cancellationToken);
    }

    private static bool ShouldBlockUnsafeMirrorSync(SyncMatchSummary matchSummary)
    {
        return matchSummary.LocalMatches > 0
               && matchSummary.TargetMatches > 0
               && matchSummary.TargetMatches < matchSummary.LocalMatches;
    }

    private async Task<(bool Success, string? ErrorMessage, int SyncedTracks)> AppendMissingJellyfinItemsAsync(
        string url,
        string apiKey,
        string userId,
        string playlistId,
        IReadOnlyList<string> itemIds,
        IReadOnlyList<JellyfinPlaylistEntry> entries,
        CancellationToken cancellationToken)
    {
        var existingItemIds = entries
            .Select(static entry => entry.ItemId)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = itemIds
            .Where(trackId => !existingItemIds.Contains(trackId))
            .ToList();
        if (pending.Count == 0)
        {
            return (true, null, 0);
        }

        var appended = await _jellyfinApiClient.AddPlaylistItemsAsync(
            url,
            apiKey,
            userId,
            playlistId,
            pending,
            cancellationToken);
        if (!appended)
        {
            return (false, "Failed to append tracks to Jellyfin playlist.", 0);
        }

        return (true, null, pending.Count);
    }

    private async Task<(bool Success, string? ErrorMessage, int SyncedTracks)> ReplaceJellyfinPlaylistItemsAsync(
        string url,
        string apiKey,
        string userId,
        string playlistId,
        IReadOnlyList<string> itemIds,
        List<JellyfinPlaylistEntry> entries,
        CancellationToken cancellationToken)
    {
        if (entries.Count > 0)
        {
            var cleared = await _jellyfinApiClient.RemovePlaylistEntriesAsync(
                url,
                apiKey,
                userId,
                playlistId,
                entries.Select(static entry => entry.PlaylistEntryId).ToList(),
                cancellationToken);
            if (!cleared)
            {
                return (false, "Failed to clear existing Jellyfin playlist items.", 0);
            }
        }

        var added = await _jellyfinApiClient.AddPlaylistItemsAsync(
            url,
            apiKey,
            userId,
            playlistId,
            itemIds,
            cancellationToken);
        if (!added)
        {
            return (false, "Failed to add tracks to Jellyfin playlist.", 0);
        }

        return (true, null, itemIds.Count);
    }

    private async Task SyncPlexPlaylistArtworkAsync(
        PlexConnection plex,
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        string playlistId,
        CancellationToken cancellationToken)
    {
        if (preference?.UpdateArtwork == false)
        {
            return;
        }

        if (preference?.ReuseSavedArtwork == true)
        {
            var visual = _playlistVisualService.GetStoredVisual(playlist.Source, playlist.SourceId);
            if (visual != null && File.Exists(visual.FilePath))
            {
                await _plexApiClient.UpdatePlaylistPosterFromFileAsync(
                    plex.Url,
                    plex.Token,
                    playlistId,
                    visual.FilePath,
                    visual.ContentType,
                    cancellationToken);
                return;
            }
        }

        if (IsAbsoluteHttpUrl(playlist.ImageUrl))
        {
            await _plexApiClient.UpdatePlaylistPosterFromUrlAsync(
                plex.Url,
                plex.Token,
                playlistId,
                playlist.ImageUrl!,
                cancellationToken);
            return;
        }

        LogSkippedRelativeArtworkUrl("Plex", playlist);
    }

    private async Task SyncJellyfinPlaylistArtworkAsync(
        JellyfinConnection jellyfin,
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        string playlistId,
        CancellationToken cancellationToken)
    {
        if (preference?.UpdateArtwork == false)
        {
            return;
        }

        if (preference?.ReuseSavedArtwork == true)
        {
            var visual = _playlistVisualService.GetStoredVisual(playlist.Source, playlist.SourceId);
            if (visual != null && File.Exists(visual.FilePath))
            {
                var updated = await _jellyfinApiClient.UpdateItemPrimaryImageFromFileAsync(
                    jellyfin.Url,
                    jellyfin.ApiKey,
                    playlistId,
                    visual.FilePath,
                    visual.ContentType,
                    cancellationToken);
                if (!updated)
                {
                    _logger.LogWarning("Failed to update Jellyfin playlist artwork for {Source}:{SourceId} from local file {ImagePath}.", playlist.Source, playlist.SourceId, visual.FilePath);
                }

                return;
            }
        }

        if (IsAbsoluteHttpUrl(playlist.ImageUrl))
        {
            var updated = await _jellyfinApiClient.UpdateItemPrimaryImageFromUrlAsync(
                jellyfin.Url,
                jellyfin.ApiKey,
                playlistId,
                playlist.ImageUrl!,
                cancellationToken);
            if (!updated)
            {
                _logger.LogWarning("Failed to update Jellyfin playlist artwork for {Source}:{SourceId} from URL {ImageUrl}.", playlist.Source, playlist.SourceId, playlist.ImageUrl);
            }

            return;
        }

        LogSkippedRelativeArtworkUrl("Jellyfin", playlist);
    }

    private void LogSkippedRelativeArtworkUrl(string target, PlaylistWatchlistDto playlist)
    {
        if (string.IsNullOrWhiteSpace(playlist.ImageUrl))
        {
            return;
        }

        _logger.LogWarning(
            "Skipped {Target} playlist artwork sync for {Source}:{SourceId} because image URL is relative and no stored visual file was found: {ImageUrl}",
            target,
            playlist.Source,
            playlist.SourceId,
            playlist.ImageUrl);
    }

    private static bool IsAbsoluteHttpUrl(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
               && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<List<SyncTrackSummary>> FilterTracksForSyncAsync(
        PlaylistWatchlistDto playlist,
        PlaylistWatchPreferenceDto? preference,
        IReadOnlyList<SyncTrackSummary> tracks,
        CancellationToken cancellationToken)
    {
        if (tracks.Count == 0)
        {
            return new List<SyncTrackSummary>();
        }

        var normalizedSource = NormalizeSource(playlist.Source);
        var ignoredTrackIds = await _libraryRepository.GetPlaylistWatchIgnoredTrackIdsAsync(
            normalizedSource,
            playlist.SourceId,
            cancellationToken);
        var globalRules = await GetGlobalPlaylistBlockRulesAsync(cancellationToken);
        var effectiveBlockRules = PlaylistTrackBlockRuleHelper.MergeRules(preference?.IgnoreRules, globalRules);
        if (ignoredTrackIds.Count == 0 && (effectiveBlockRules == null || effectiveBlockRules.Count == 0))
        {
            return tracks.ToList();
        }

        return tracks
            .Where(track => !string.IsNullOrWhiteSpace(track.SourceTrackId))
            .Where(track => !ignoredTrackIds.Contains(track.SourceTrackId))
            .Where(track => !ShouldBlockTrack(track, effectiveBlockRules))
            .ToList();
    }

    private async Task<IReadOnlyList<PlaylistTrackBlockRule>> GetGlobalPlaylistBlockRulesAsync(CancellationToken cancellationToken)
    {
        var preferences = await _libraryRepository.GetPlaylistWatchPreferencesAsync(cancellationToken);
        return PlaylistTrackBlockRuleHelper.BuildGlobalRules(preferences);
    }

    private static bool ShouldBlockTrack(SyncTrackSummary track, List<PlaylistTrackBlockRule>? rules)
    {
        if (rules is null || rules.Count == 0)
        {
            return false;
        }

        return rules.Any(rule =>
            RuleMatches(track, rule.ConditionField, rule.ConditionOperator, rule.ConditionValue));
    }

    private static bool RuleMatches(
        SyncTrackSummary track,
        string conditionField,
        string conditionOperator,
        string conditionValue)
    {
        return (conditionField ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "artist" => EvalStringCondition(track.Artists, conditionOperator, conditionValue),
            "title" => EvalStringCondition(track.Name, conditionOperator, conditionValue),
            "album" => EvalStringCondition(track.Album, conditionOperator, conditionValue),
            "genre" => EvalGenreCondition(track.Genres, conditionOperator, conditionValue),
            "explicit" => conditionOperator == "is_true" ? (track.Explicit == true) : (track.Explicit != true),
            "year" => EvalYearCondition(track.ReleaseDate, conditionOperator, conditionValue),
            _ => false
        };
    }

    private static bool EvalStringCondition(string? value, string? op, string? conditionValue)
    {
        var candidate = (value ?? string.Empty).Trim();
        var rule = (conditionValue ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(rule))
        {
            return false;
        }

        return (op ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "contains" => candidate.Contains(rule, StringComparison.OrdinalIgnoreCase),
            "equals" => string.Equals(candidate, rule, StringComparison.OrdinalIgnoreCase),
            "starts_with" => candidate.StartsWith(rule, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static bool EvalGenreCondition(IReadOnlyList<string>? genres, string? op, string? conditionValue)
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

    private static bool EvalYearCondition(string? releaseDate, string? op, string? conditionValue)
    {
        if (!TryParseReleaseYear(releaseDate, out var trackYear)
            || !int.TryParse((conditionValue ?? string.Empty).Trim(), out var ruleYear))
        {
            return false;
        }

        return (op ?? string.Empty).Trim().ToLowerInvariant() switch
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

    private static string NormalizeSyncMode(string? mode)
    {
        var normalized = (mode ?? string.Empty).Trim().ToLowerInvariant();
        return normalized == SyncModeAppend ? SyncModeAppend : SyncModeMirror;
    }

    private static string NormalizeService(string? service)
    {
        return (service ?? string.Empty).Trim().ToLowerInvariant();
    }

    private async Task<string> ResolveTargetServiceAsync(
        PlaylistWatchPreferenceDto? preference,
        CancellationToken cancellationToken)
    {
        var configuredService = NormalizeService(preference?.Service);
        if (string.Equals(configuredService, "none", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }
        if (!string.IsNullOrWhiteSpace(configuredService))
        {
            return configuredService;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var state = await _authService.LoadAsync();
        if (state.Plex is not null
            && !string.IsNullOrWhiteSpace(state.Plex.Url)
            && !string.IsNullOrWhiteSpace(state.Plex.Token)
            && !string.IsNullOrWhiteSpace(state.Plex.MachineIdentifier))
        {
            return PlexService;
        }

        if (state.Jellyfin is not null
            && !string.IsNullOrWhiteSpace(state.Jellyfin.Url)
            && !string.IsNullOrWhiteSpace(state.Jellyfin.ApiKey)
            && !string.IsNullOrWhiteSpace(state.Jellyfin.UserId))
        {
            return JellyfinService;
        }

        return string.Empty;
    }

    private static string NormalizeSource(string? source)
    {
        return (source ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static string ResolvePlaylistName(PlaylistWatchlistDto playlist)
    {
        var name = (playlist.Name ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(name) ? "Playlist" : name;
    }

    private async Task<(PlexConnection? Plex, PlaylistSyncResult? Error)> TryLoadConfiguredPlexAsync()
    {
        var state = await _authService.LoadAsync();
        var plex = state.Plex;
        if (plex is null || string.IsNullOrWhiteSpace(plex.Url) || string.IsNullOrWhiteSpace(plex.Token))
        {
            return (null, new PlaylistSyncResult(false, "Plex is not configured."));
        }

        if (string.IsNullOrWhiteSpace(plex.MachineIdentifier))
        {
            return (null, new PlaylistSyncResult(false, "Plex machine identifier missing."));
        }

        return (new PlexConnection(plex.Url, plex.Token, plex.MachineIdentifier), null);
    }

    private async Task<(JellyfinConnection? Jellyfin, PlaylistSyncResult? Error)> TryLoadConfiguredJellyfinAsync()
    {
        var state = await _authService.LoadAsync();
        var jellyfin = state.Jellyfin;
        if (jellyfin is null || string.IsNullOrWhiteSpace(jellyfin.Url) || string.IsNullOrWhiteSpace(jellyfin.ApiKey))
        {
            return (null, new PlaylistSyncResult(false, "Jellyfin is not configured."));
        }

        if (string.IsNullOrWhiteSpace(jellyfin.UserId))
        {
            return (null, new PlaylistSyncResult(false, "Jellyfin user id is missing."));
        }

        return (new JellyfinConnection(jellyfin.Url, jellyfin.ApiKey, jellyfin.UserId), null);
    }

    private async Task<List<long>> ResolveOrderedTrackIdsAsync(
        string playlistSource,
        IReadOnlyList<SyncTrackSummary> tracks,
        CancellationToken cancellationToken)
    {
        var orderedTrackIds = new List<long>(tracks.Count);
        foreach (var track in tracks)
        {
            orderedTrackIds.Add(await ResolveLocalTrackIdAsync(playlistSource, track, cancellationToken) ?? 0L);
        }

        return orderedTrackIds;
    }

    private async Task<long?> ResolveLocalTrackIdAsync(
        string playlistSource,
        SyncTrackSummary track,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(track.Isrc))
        {
            var byIsrc = await _libraryRepository.GetTrackIdsBySourceIdsAsync(
                IsrcSource,
                new[] { track.Isrc },
                cancellationToken);
            if (byIsrc.TryGetValue(track.Isrc, out var isrcTrackId))
            {
                return isrcTrackId;
            }
        }

        var byMetadata = await _libraryRepository.FindLocalTrackIdByMetadataAsync(
            track.Name,
            track.Artists,
            track.Album,
            track.DurationMs,
            cancellationToken);
        if (byMetadata.HasValue)
        {
            return byMetadata;
        }

        var source = NormalizeSource(playlistSource);
        if (!string.IsNullOrWhiteSpace(source) && !string.IsNullOrWhiteSpace(track.SourceTrackId))
        {
            var bySource = await _libraryRepository.GetTrackIdsBySourceIdsAsync(
                source,
                new[] { track.SourceTrackId },
                cancellationToken);
            if (bySource.TryGetValue(track.SourceTrackId, out var sourceTrackId))
            {
                return sourceTrackId;
            }
        }

        return null;
    }

    private async Task<PlaylistTrackSyncReadiness> CheckPlexTrackReadyAsync(
        long localTrackId,
        SyncTrackSummary track,
        CancellationToken cancellationToken)
    {
        var (plex, configurationError) = await TryLoadConfiguredPlexAsync();
        if (configurationError != null)
        {
            return new PlaylistTrackSyncReadiness(false, true, configurationError.Message, PlexService, localTrackId);
        }

        if (plex == null)
        {
            return new PlaylistTrackSyncReadiness(false, true, "Plex is not configured.", PlexService, localTrackId);
        }

        var mapped = await _libraryRepository.GetPlexRatingKeysByTrackIdsAsync(
            new[] { localTrackId },
            cancellationToken);
        if (mapped.TryGetValue(localTrackId, out var mappedRatingKey)
            && !string.IsNullOrWhiteSpace(mappedRatingKey))
        {
            return new PlaylistTrackSyncReadiness(
                true,
                false,
                "Track is visible in Plex metadata mapping.",
                PlexService,
                localTrackId,
                mappedRatingKey);
        }

        var ratingKey = await ResolvePlexRatingKeyAsync(
            plex,
            track,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
            cancellationToken);
        if (string.IsNullOrWhiteSpace(ratingKey))
        {
            return new PlaylistTrackSyncReadiness(
                false,
                false,
                "Track is not visible in Plex yet.",
                PlexService,
                localTrackId);
        }

        await _libraryRepository.UpsertPlexTrackMetadataAsync(
            new[]
            {
                new PlexTrackMetadataUpsertDto(localTrackId, ratingKey, DateTimeOffset.UtcNow)
            },
            cancellationToken);
        return new PlaylistTrackSyncReadiness(
            true,
            false,
            "Track is visible in Plex search.",
            PlexService,
            localTrackId,
            ratingKey);
    }

    private async Task<PlaylistTrackSyncReadiness> CheckJellyfinTrackReadyAsync(
        long localTrackId,
        SyncTrackSummary track,
        CancellationToken cancellationToken)
    {
        var (jellyfin, configurationError) = await TryLoadConfiguredJellyfinAsync();
        if (configurationError != null)
        {
            return new PlaylistTrackSyncReadiness(false, true, configurationError.Message, JellyfinService, localTrackId);
        }

        if (jellyfin == null)
        {
            return new PlaylistTrackSyncReadiness(false, true, "Jellyfin is not configured.", JellyfinService, localTrackId);
        }

        var itemId = await ResolveJellyfinItemIdAsync(
            jellyfin,
            track,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
            cancellationToken);
        return string.IsNullOrWhiteSpace(itemId)
            ? new PlaylistTrackSyncReadiness(false, false, "Track is not visible in Jellyfin yet.", JellyfinService, localTrackId)
            : new PlaylistTrackSyncReadiness(true, false, "Track is visible in Jellyfin search.", JellyfinService, localTrackId, itemId);
    }

    private async Task<SyncMatchSummary> ResolvePlexRatingKeysAsync(
        PlexConnection plex,
        IReadOnlyList<SyncTrackSummary> tracks,
        List<long> orderedTrackIds,
        CancellationToken cancellationToken)
    {
        var ratingKeyByTrackId = await _libraryRepository.GetPlexRatingKeysByTrackIdsAsync(
            orderedTrackIds.Where(id => id > 0).Distinct().ToList(),
            cancellationToken);

        var ratingKeysByIndex = new string?[tracks.Count];
        var searchCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var metadataMatches = 0;
        var searchMatches = 0;
        var unresolvedSearchIndexes = new List<int>();
        for (var i = 0; i < tracks.Count; i++)
        {
            var trackId = orderedTrackIds[i];
            if (trackId > 0 && ratingKeyByTrackId.TryGetValue(trackId, out var ratingKey))
            {
                ratingKeysByIndex[i] = ratingKey;
                metadataMatches++;
                continue;
            }

            unresolvedSearchIndexes.Add(i);
        }

        if (unresolvedSearchIndexes.Count <= PlexSequentialSearchFallbackLimit)
        {
            foreach (var index in unresolvedSearchIndexes)
            {
                var track = tracks[index];
                var resolved = await ResolvePlexRatingKeyAsync(plex, track, searchCache, cancellationToken);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    ratingKeysByIndex[index] = resolved;
                    searchMatches++;
                }
            }
        }
        else if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Skipped sequential Plex search fallback for {MissingCount} unresolved tracks. Populate track_plex_metadata by refreshing Plex library mapping.",
                unresolvedSearchIndexes.Count);
        }

        var ratingKeys = ratingKeysByIndex
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .ToList();
        return new SyncMatchSummary(
            ratingKeys,
            SourceTracks: tracks.Count,
            LocalMatches: orderedTrackIds.Count(static id => id > 0),
            TargetMatches: ratingKeys.Count,
            MissingTracks: Math.Max(0, tracks.Count - ratingKeys.Count),
            MetadataMatches: metadataMatches,
            SearchMatches: searchMatches);
    }

    private async Task<string?> ResolvePlexRatingKeyAsync(
        PlexConnection plex,
        SyncTrackSummary track,
        Dictionary<string, string?> cache,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(track.Name))
        {
            return null;
        }

        var query = $"{track.Name} {track.Artists}".Trim();
        if (cache.TryGetValue(query, out var cached))
        {
            return cached;
        }

        var results = await _plexApiClient.SearchTracksAsync(
            plex.Url,
            plex.Token,
            query,
            cancellationToken);

        var match = results.FirstOrDefault(result =>
            IsTitleArtistMatch(track, result)
            && IsDurationMatch(track.DurationMs, result.DurationMs));
        if (match == null)
        {
            match = results.FirstOrDefault(result => IsTitleLooseMatch(track, result.Title));
        }

        var ratingKey = match?.RatingKey;
        cache[query] = ratingKey;
        return ratingKey;
    }

    private async Task<List<string>> ResolveJellyfinItemIdsAsync(
        JellyfinConnection jellyfin,
        IReadOnlyList<SyncTrackSummary> tracks,
        CancellationToken cancellationToken)
    {
        var itemIds = new List<string>(tracks.Count);
        var searchCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in tracks)
        {
            var resolved = await ResolveJellyfinItemIdAsync(jellyfin, track, searchCache, cancellationToken);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                itemIds.Add(resolved);
            }
        }

        return itemIds;
    }

    private async Task<string?> ResolveJellyfinItemIdAsync(
        JellyfinConnection jellyfin,
        SyncTrackSummary track,
        Dictionary<string, string?> cache,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(track.Name))
        {
            return null;
        }

        var query = $"{track.Name} {track.Artists}".Trim();
        if (cache.TryGetValue(query, out var cached))
        {
            return cached;
        }

        var results = await _jellyfinApiClient.SearchTracksAsync(
            jellyfin.Url,
            jellyfin.ApiKey,
            jellyfin.UserId,
            query,
            cancellationToken);

        var match = results.FirstOrDefault(result =>
            IsTitleArtistMatch(track, result)
            && IsDurationMatch(track.DurationMs, result.DurationMs));
        if (match == null)
        {
            match = results.FirstOrDefault(result => IsTitleLooseMatch(track, result.Name));
        }

        var itemId = match?.Id;
        cache[query] = itemId;
        return itemId;
    }

    private static PlaylistSyncResult BuildFailedResult(string message, SyncMatchSummary matchSummary)
        => new(
            false,
            message,
            PlaylistId: null,
            SyncedTracks: 0,
            SourceTracks: matchSummary.SourceTracks,
            LocalMatches: matchSummary.LocalMatches,
            TargetMatches: matchSummary.TargetMatches,
            MissingTracks: matchSummary.MissingTracks,
            MetadataMatches: matchSummary.MetadataMatches,
            SearchMatches: matchSummary.SearchMatches);

    private static PlaylistSyncResult BuildSuccessResult(
        string message,
        string? playlistId,
        SyncMatchSummary matchSummary,
        int syncedTracks)
        => new(
            true,
            message,
            playlistId,
            syncedTracks,
            matchSummary.SourceTracks,
            matchSummary.LocalMatches,
            matchSummary.TargetMatches,
            matchSummary.MissingTracks,
            matchSummary.MetadataMatches,
            matchSummary.SearchMatches);

    private static string BuildSyncMessage(string baseMessage, SyncMatchSummary matchSummary)
    {
        return string.Concat(
            baseMessage,
            " Source tracks: ",
            matchSummary.SourceTracks.ToString(CultureInfo.InvariantCulture),
            ". Local matches: ",
            matchSummary.LocalMatches.ToString(CultureInfo.InvariantCulture),
            ". Target matches: ",
            matchSummary.TargetMatches.ToString(CultureInfo.InvariantCulture),
            ". Missing tracks: ",
            matchSummary.MissingTracks.ToString(CultureInfo.InvariantCulture),
            ".");
    }

    private static bool IsTitleArtistMatch(SyncTrackSummary track, PlexTrack result)
    {
        var leftTitle = Normalize(track.Name);
        var rightTitle = Normalize(result.Title);
        var leftArtist = Normalize(track.Artists);
        var rightArtist = Normalize(result.Artist);
        return leftTitle == rightTitle && leftArtist == rightArtist;
    }

    private static bool IsTitleArtistMatch(SyncTrackSummary track, JellyfinAudioTrack result)
    {
        var leftTitle = Normalize(track.Name);
        var rightTitle = Normalize(result.Name);
        if (leftTitle != rightTitle)
        {
            return false;
        }

        var leftArtist = Normalize(track.Artists);
        var rightArtist = Normalize(result.Artist);
        if (string.IsNullOrWhiteSpace(leftArtist) || string.IsNullOrWhiteSpace(rightArtist))
        {
            return true;
        }

        return leftArtist == rightArtist
               || rightArtist.Contains(leftArtist, StringComparison.OrdinalIgnoreCase)
               || leftArtist.Contains(rightArtist, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTitleLooseMatch(SyncTrackSummary track, string candidateTitle)
    {
        var leftTitle = Normalize(track.Name);
        var rightTitle = Normalize(candidateTitle);
        return !string.IsNullOrWhiteSpace(leftTitle)
               && !string.IsNullOrWhiteSpace(rightTitle)
               && (leftTitle == rightTitle
                   || rightTitle.Contains(leftTitle, StringComparison.OrdinalIgnoreCase)
                   || leftTitle.Contains(rightTitle, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDurationMatch(int? durationMs, long durationCandidate)
    {
        if (!durationMs.HasValue || durationCandidate <= 0)
        {
            return true;
        }

        var delta = Math.Abs(durationMs.Value - durationCandidate);
        return delta <= DurationToleranceMs;
    }

    private static bool IsDurationMatch(int? durationMs, int? durationCandidateMs)
    {
        if (!durationMs.HasValue || !durationCandidateMs.HasValue || durationCandidateMs <= 0)
        {
            return true;
        }

        var delta = Math.Abs(durationMs.Value - durationCandidateMs.Value);
        return delta <= DurationToleranceMs;
    }

    private static string BuildMergeTrackDedupKey(SyncTrackSummary track)
    {
        if (!string.IsNullOrWhiteSpace(track.Isrc))
        {
            return $"isrc:{Normalize(track.Isrc)}";
        }

        var year = TryParseReleaseYear(track.ReleaseDate, out var parsedYear)
            ? parsedYear.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        var durationBucket = track.DurationMs.HasValue && track.DurationMs.Value > 0
            ? (track.DurationMs.Value / DurationToleranceMs).ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        var fallbackId = string.IsNullOrWhiteSpace(track.SourceTrackId)
            ? string.Empty
            : Normalize(track.SourceTrackId);
        return string.Join(
            "\u001F",
            Normalize(track.Name),
            Normalize(track.Artists),
            Normalize(track.Album),
            year,
            durationBucket,
            fallbackId);
    }

    private static string ResolveMergedPlaylistName(string? requestedName)
    {
        var trimmed = (requestedName ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? "Merged Monitored Playlist" : trimmed;
    }

    private static string? BuildMergedPlaylistDescription(
        string? userDescription,
        IEnumerable<PlaylistWatchlistDto> selectedPlaylists,
        string? sourceUsername)
    {
        var values = new List<string>();
        var trimmedDescription = (userDescription ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(trimmedDescription))
        {
            values.Add(trimmedDescription);
        }

        var sourceSummary = BuildMergeSourceSummary(selectedPlaylists);
        if (!string.IsNullOrWhiteSpace(sourceSummary))
        {
            values.Add(sourceSummary);
        }

        var trimmedUser = (sourceUsername ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(trimmedUser))
        {
            values.Add($"Source user: {trimmedUser}");
        }

        return values.Count == 0 ? null : string.Join(" | ", values);
    }

    private static string? BuildMergeSourceSummary(IEnumerable<PlaylistWatchlistDto> selectedPlaylists)
    {
        var sources = selectedPlaylists
            .Select(static playlist => NormalizeMergeSourceLabel(playlist.Source))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return sources.Count == 0 ? null : $"Sources: {string.Join(", ", sources)}";
    }

    private static string NormalizeMergeSourceLabel(string? source)
    {
        var normalized = NormalizeSource(source);
        return normalized switch
        {
            "spotify" => "Spotify",
            "deezer" => "Deezer",
            "apple" => "Apple Music",
            "boomplay" => "Boomplay",
            "recommendations" => "Recommendations",
            "smarttracklist" => "Smart Tracklist",
            _ => string.IsNullOrWhiteSpace(normalized)
                ? "Unknown"
                : char.ToUpperInvariant(normalized[0]) + normalized[1..]
        };
    }

    private static string Normalize(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static SyncTrackSummary ToSyncTrackSummary(SpotifyTrackSummary track)
    {
        return new SyncTrackSummary(
            (track.Id ?? string.Empty).Trim(),
            string.IsNullOrWhiteSpace(track.Isrc) ? null : track.Isrc.Trim(),
            track.Name?.Trim() ?? string.Empty,
            track.Artists?.Trim() ?? string.Empty,
            track.Album?.Trim() ?? string.Empty,
            track.ReleaseDate,
            track.Explicit,
            NormalizeGenres(track.Genres),
            track.DurationMs);
    }

    private static SyncTrackSummary ToSyncTrackSummary(PlaylistWatchService.PlaylistTrackCandidate track)
    {
        return new SyncTrackSummary(
            (track.TrackSourceId ?? string.Empty).Trim(),
            string.IsNullOrWhiteSpace(track.Isrc) ? null : track.Isrc.Trim(),
            track.Title?.Trim() ?? string.Empty,
            track.Artist?.Trim() ?? string.Empty,
            track.Album?.Trim() ?? string.Empty,
            track.ReleaseYear?.ToString(CultureInfo.InvariantCulture),
            track.Explicit,
            NormalizeGenres(track.Genres),
            track.DurationMs);
    }

    private static IReadOnlyList<string> NormalizeGenres(IReadOnlyList<string>? genres)
    {
        if (genres is null || genres.Count == 0)
        {
            return Array.Empty<string>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>(genres.Count);
        foreach (var genre in genres)
        {
            var value = (genre ?? string.Empty).Trim();
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
}

public sealed record PlaylistSyncResult(
    bool Success,
    string Message,
    string? PlaylistId = null,
    int SyncedTracks = 0,
    int SourceTracks = 0,
    int LocalMatches = 0,
    int TargetMatches = 0,
    int MissingTracks = 0,
    int MetadataMatches = 0,
    int SearchMatches = 0)
{
    public static PlaylistSyncResult Failed(string message)
        => new(false, message);
}
