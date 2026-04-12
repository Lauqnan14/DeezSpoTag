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

    private const string SpotifySource = "spotify";
    private const string IsrcSource = "isrc";
    private const string PlexService = "plex";
    private const string JellyfinService = "jellyfin";
    private const string SyncModeMirror = "mirror";
    private const string SyncModeAppend = "append";
    private const int DurationToleranceMs = 2000;
    private readonly LibraryRepository _libraryRepository;
    private readonly SpotifyMetadataService _spotifyMetadataService;
    private readonly PlexApiClient _plexApiClient;
    private readonly JellyfinApiClient _jellyfinApiClient;
    private readonly PlatformAuthService _authService;
    private readonly ILogger<PlaylistSyncService> _logger;

    public PlaylistSyncService(
        LibraryRepository libraryRepository,
        SpotifyMetadataService spotifyMetadataService,
        PlexApiClient plexApiClient,
        JellyfinApiClient jellyfinApiClient,
        PlatformAuthService authService,
        ILogger<PlaylistSyncService> logger)
    {
        _libraryRepository = libraryRepository;
        _spotifyMetadataService = spotifyMetadataService;
        _plexApiClient = plexApiClient;
        _jellyfinApiClient = jellyfinApiClient;
        _authService = authService;
        _logger = logger;
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
            AutotagProfile: null,
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
            return new PlaylistSyncResult(false, "Playlist not available.");
        }

        var service = await ResolveTargetServiceAsync(preference, cancellationToken);
        if (string.IsNullOrWhiteSpace(service))
        {
            return new PlaylistSyncResult(false, "No target server selected.");
        }

        var loadResult = await LoadTracksForSyncAsync(playlist, trackCandidates, cancellationToken);
        if (!string.IsNullOrWhiteSpace(loadResult.ErrorMessage))
        {
            return new PlaylistSyncResult(false, loadResult.ErrorMessage);
        }

        var tracks = await FilterTracksForSyncAsync(
            playlist,
            preference,
            loadResult.Tracks,
            cancellationToken);
        if (tracks.Count == 0)
        {
            return new PlaylistSyncResult(false, "No eligible tracks after blocked/ignored filtering.");
        }

        return service switch
        {
            PlexService => await SyncToPlexAsync(playlist, preference, tracks, cancellationToken),
            JellyfinService => await SyncToJellyfinAsync(playlist, preference, tracks, cancellationToken),
            _ => new PlaylistSyncResult(false, "Unsupported playlist sync target.")
        };
    }

    private async Task<(IReadOnlyList<SyncTrackSummary> Tracks, string? ErrorMessage)> LoadTracksForSyncAsync(
        PlaylistWatchlistDto playlist,
        IReadOnlyList<PlaylistWatchService.PlaylistTrackCandidate>? trackCandidates,
        CancellationToken cancellationToken)
    {
        var source = NormalizeSource(playlist.Source);
        if (string.Equals(source, SpotifySource, StringComparison.OrdinalIgnoreCase))
        {
            var snapshot = await _spotifyMetadataService.FetchPlaylistSnapshotAsync(playlist.SourceId, cancellationToken);
            if (snapshot != null && snapshot.Tracks.Count > 0)
            {
                return (snapshot.Tracks.Select(ToSyncTrackSummary).ToList(), null);
            }

            if (trackCandidates is { Count: > 0 })
            {
                _logger.LogDebug("Spotify snapshot unavailable for {SourceId}; using cached track candidates.", playlist.SourceId);
                return (trackCandidates.Select(ToSyncTrackSummary).ToList(), null);
            }

            return (Array.Empty<SyncTrackSummary>(), "Spotify playlist could not be loaded.");
        }

        if (trackCandidates is { Count: > 0 })
        {
            return (trackCandidates.Select(ToSyncTrackSummary).ToList(), null);
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
            return new PlaylistSyncResult(false, "Plex is not configured.");
        }

        var playlistName = ResolvePlaylistName(playlist);
        var orderedTrackIds = await ResolveOrderedTrackIdsAsync(playlist.Source, tracks, cancellationToken);
        var ratingKeys = await ResolvePlexRatingKeysAsync(plex, tracks, orderedTrackIds, cancellationToken);
        if (ratingKeys.Count == 0)
        {
            _logger.LogWarning("No Plex matches found for playlist {Source}:{SourceId}.", playlist.Source, playlist.SourceId);
            return new PlaylistSyncResult(false, "No Plex matches found for this playlist.");
        }

        var syncMode = NormalizeSyncMode(preference?.SyncMode);
        var appendMissingOnly = string.Equals(syncMode, SyncModeAppend, StringComparison.OrdinalIgnoreCase);
        var playlistId = await _plexApiClient.CreateOrUpdatePlaylistAsync(
            plex.Url,
            plex.Token,
            plex.MachineIdentifier,
            playlistName,
            ratingKeys,
            options: new PlexApiClient.PlaylistUpsertOptions(
                AppendMissingOnly: appendMissingOnly),
            cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return new PlaylistSyncResult(false, "Failed to create Plex playlist.");
        }

        await _plexApiClient.UpdatePlaylistMetadataAsync(
            plex.Url,
            plex.Token,
            playlistId,
            playlistName,
            playlist.Description,
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(playlist.ImageUrl) && preference?.UpdateArtwork != false)
        {
            await _plexApiClient.UpdatePlaylistPosterFromUrlAsync(
                plex.Url,
                plex.Token,
                playlistId,
                playlist.ImageUrl,
                cancellationToken);
        }

        var modeLabel = appendMissingOnly ? "append" : "mirror";
        return new PlaylistSyncResult(true, $"Playlist synced ({modeLabel}).", playlistId, ratingKeys.Count);
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
            return new PlaylistSyncResult(false, "Jellyfin is not configured.");
        }

        var playlistName = ResolvePlaylistName(playlist);
        var itemIds = await ResolveJellyfinItemIdsAsync(jellyfin, tracks, cancellationToken);
        if (itemIds.Count == 0)
        {
            _logger.LogWarning("No Jellyfin matches found for playlist {Source}:{SourceId}.", playlist.Source, playlist.SourceId);
            return new PlaylistSyncResult(false, "No Jellyfin matches found for this playlist.");
        }

        var syncMode = NormalizeSyncMode(preference?.SyncMode);
        var appendMissingOnly = string.Equals(syncMode, SyncModeAppend, StringComparison.OrdinalIgnoreCase);
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
                return new PlaylistSyncResult(false, "Failed to create Jellyfin playlist.");
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
                return new PlaylistSyncResult(false, syncItemsResult.ErrorMessage ?? "Failed to sync Jellyfin playlist.");
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

        if (!string.IsNullOrWhiteSpace(playlist.ImageUrl) && preference?.UpdateArtwork != false)
        {
            await _jellyfinApiClient.UpdateItemPrimaryImageFromUrlAsync(
                jellyfin.Url,
                jellyfin.ApiKey,
                playlistId,
                playlist.ImageUrl,
                cancellationToken);
        }

        var modeLabel = appendMissingOnly ? "append" : "mirror";
        return new PlaylistSyncResult(true, $"Playlist synced ({modeLabel}).", playlistId, syncedTracks);
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
        IReadOnlyList<JellyfinPlaylistEntry> entries,
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

    private static bool ShouldBlockTrack(SyncTrackSummary track, IReadOnlyList<PlaylistTrackBlockRule>? rules)
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
        var source = NormalizeSource(playlistSource);
        var trackIdBySource = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(source))
        {
            var sourceIds = tracks
                .Select(track => track.SourceTrackId)
                .Where(static trackId => !string.IsNullOrWhiteSpace(trackId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var sourceLookup = await _libraryRepository.GetTrackIdsBySourceIdsAsync(source, sourceIds, cancellationToken);
            foreach (var pair in sourceLookup)
            {
                trackIdBySource[pair.Key] = pair.Value;
            }
        }

        var isrcs = tracks
            .Select(track => track.Isrc)
            .Where(static isrc => !string.IsNullOrWhiteSpace(isrc))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var trackIdByIsrc = await _libraryRepository.GetTrackIdsBySourceIdsAsync(IsrcSource, isrcs, cancellationToken);

        return tracks
            .Select(track =>
            {
                if (!string.IsNullOrWhiteSpace(track.SourceTrackId)
                    && trackIdBySource.TryGetValue(track.SourceTrackId, out var sourceTrackId))
                {
                    return sourceTrackId;
                }

                return !string.IsNullOrWhiteSpace(track.Isrc)
                    && trackIdByIsrc.TryGetValue(track.Isrc, out var isrcTrackId)
                        ? isrcTrackId
                        : 0L;
            })
            .ToList();
    }

    private async Task<List<string>> ResolvePlexRatingKeysAsync(
        PlexConnection plex,
        IReadOnlyList<SyncTrackSummary> tracks,
        IReadOnlyList<long> orderedTrackIds,
        CancellationToken cancellationToken)
    {
        var ratingKeyByTrackId = await _libraryRepository.GetPlexRatingKeysByTrackIdsAsync(
            orderedTrackIds.Where(id => id > 0).Distinct().ToList(),
            cancellationToken);

        var ratingKeys = new List<string>(tracks.Count);
        var searchCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            var trackId = orderedTrackIds[i];
            if (trackId > 0 && ratingKeyByTrackId.TryGetValue(trackId, out var ratingKey))
            {
                ratingKeys.Add(ratingKey);
                continue;
            }

            var resolved = await ResolvePlexRatingKeyAsync(plex, track, searchCache, cancellationToken);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                ratingKeys.Add(resolved);
            }
        }

        return ratingKeys;
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
            null);
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
    int SyncedTracks = 0);
