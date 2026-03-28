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

        var service = NormalizeService(preference?.Service);
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
            appendMissingOnly: appendMissingOnly,
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
            playlistId = await _jellyfinApiClient.CreatePlaylistAsync(
                jellyfin.Url,
                jellyfin.ApiKey,
                jellyfin.UserId,
                playlistName,
                itemIds,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(playlistId))
            {
                return new PlaylistSyncResult(false, "Failed to create Jellyfin playlist.");
            }

            syncedTracks = itemIds.Count;
        }
        else
        {
            var entries = await _jellyfinApiClient.GetPlaylistEntriesAsync(
                jellyfin.Url,
                jellyfin.ApiKey,
                jellyfin.UserId,
                playlistId,
                cancellationToken);

            if (appendMissingOnly)
            {
                var existingItemIds = entries
                    .Select(static entry => entry.ItemId)
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var pending = itemIds
                    .Where(trackId => !existingItemIds.Contains(trackId))
                    .ToList();

                if (pending.Count > 0)
                {
                    var appended = await _jellyfinApiClient.AddPlaylistItemsAsync(
                        jellyfin.Url,
                        jellyfin.ApiKey,
                        jellyfin.UserId,
                        playlistId,
                        pending,
                        cancellationToken);
                    if (!appended)
                    {
                        return new PlaylistSyncResult(false, "Failed to append tracks to Jellyfin playlist.");
                    }
                }

                syncedTracks = pending.Count;
            }
            else
            {
                if (entries.Count > 0)
                {
                    var cleared = await _jellyfinApiClient.RemovePlaylistEntriesAsync(
                        jellyfin.Url,
                        jellyfin.ApiKey,
                        jellyfin.UserId,
                        playlistId,
                        entries.Select(static entry => entry.PlaylistEntryId).ToList(),
                        cancellationToken);
                    if (!cleared)
                    {
                        return new PlaylistSyncResult(false, "Failed to clear existing Jellyfin playlist items.");
                    }
                }

                var added = await _jellyfinApiClient.AddPlaylistItemsAsync(
                    jellyfin.Url,
                    jellyfin.ApiKey,
                    jellyfin.UserId,
                    playlistId,
                    itemIds,
                    cancellationToken);
                if (!added)
                {
                    return new PlaylistSyncResult(false, "Failed to add tracks to Jellyfin playlist.");
                }

                syncedTracks = itemIds.Count;
            }
        }

        var modeLabel = appendMissingOnly ? "append" : "mirror";
        return new PlaylistSyncResult(true, $"Playlist synced ({modeLabel}).", playlistId, syncedTracks);
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
        var effectiveBlockRules = MergeBlockRules(preference?.IgnoreRules, globalRules);
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
        if (preferences.Count == 0)
        {
            return Array.Empty<PlaylistTrackBlockRule>();
        }

        var rules = new List<PlaylistTrackBlockRule>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in preferences.SelectMany(static preference => preference.IgnoreRules ?? []))
        {
            var field = (rule.ConditionField ?? string.Empty).Trim();
            var op = (rule.ConditionOperator ?? string.Empty).Trim();
            var value = (rule.ConditionValue ?? string.Empty).Trim();
            var isExplicitRule = string.Equals(field, "explicit", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(field)
                || string.IsNullOrWhiteSpace(op)
                || (!isExplicitRule && string.IsNullOrWhiteSpace(value)))
            {
                continue;
            }

            var dedupeKey = $"{field}\u001F{op}\u001F{value}";
            if (!seen.Add(dedupeKey))
            {
                continue;
            }

            rules.Add(new PlaylistTrackBlockRule(field, op, value, rules.Count));
        }

        return rules;
    }

    private static List<PlaylistTrackBlockRule>? MergeBlockRules(
        IReadOnlyList<PlaylistTrackBlockRule>? playlistRules,
        IReadOnlyList<PlaylistTrackBlockRule> globalRules)
    {
        var hasPlaylistRules = playlistRules is { Count: > 0 };
        var hasGlobalRules = globalRules.Count > 0;
        if (!hasPlaylistRules && !hasGlobalRules)
        {
            return null;
        }

        var merged = new List<PlaylistTrackBlockRule>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (hasPlaylistRules)
        {
            foreach (var rule in playlistRules!)
            {
                AppendRuleIfUnique(rule, merged, seen);
            }
        }

        if (hasGlobalRules)
        {
            foreach (var rule in globalRules)
            {
                AppendRuleIfUnique(rule, merged, seen);
            }
        }

        return merged;
    }

    private static void AppendRuleIfUnique(
        PlaylistTrackBlockRule rule,
        List<PlaylistTrackBlockRule> merged,
        HashSet<string> seen)
    {
        var field = (rule.ConditionField ?? string.Empty).Trim();
        var op = (rule.ConditionOperator ?? string.Empty).Trim();
        var value = (rule.ConditionValue ?? string.Empty).Trim();
        var isExplicitRule = string.Equals(field, "explicit", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(field)
            || string.IsNullOrWhiteSpace(op)
            || (!isExplicitRule && string.IsNullOrWhiteSpace(value)))
        {
            return;
        }

        var dedupeKey = $"{field}\u001F{op}\u001F{value}";
        if (!seen.Add(dedupeKey))
        {
            return;
        }

        merged.Add(new PlaylistTrackBlockRule(field, op, value, merged.Count));
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
        IReadOnlyDictionary<string, long> trackIdBySource = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(source))
        {
            var sourceIds = tracks
                .Select(track => track.SourceTrackId)
                .Where(static trackId => !string.IsNullOrWhiteSpace(trackId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            trackIdBySource = await _libraryRepository.GetTrackIdsBySourceIdsAsync(source, sourceIds, cancellationToken);
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
