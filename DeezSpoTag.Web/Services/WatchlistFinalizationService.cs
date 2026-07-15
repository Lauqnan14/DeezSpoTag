using System.Linq;
using System.Text.Json;
using DeezSpoTag.Core.Utils;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed class WatchlistFinalizationService
{
    private const string AppleSource = "apple";
    private static readonly string[] WatchlistSourcePropertyNames = ["watchlistSource", "watchlist_source", "WatchlistSource"];
    private static readonly string[] WatchlistPlaylistIdPropertyNames = ["watchlistPlaylistId", "watchlist_playlist", "WatchlistPlaylistId"];
    private static readonly string[] WatchlistTrackIdPropertyNames = ["watchlistTrackId", "watchlist_track", "WatchlistTrackId"];
    private static readonly string[] SourceIdsWatchlistSourcePropertyNames = ["watchlist_source", "watchlistSource", "WatchlistSource"];
    private static readonly string[] SourceIdsWatchlistPlaylistIdPropertyNames = ["watchlist_playlist", "watchlistPlaylistId", "WatchlistPlaylistId"];
    private static readonly string[] SourceIdsWatchlistTrackIdPropertyNames = ["watchlist_track", "watchlistTrackId", "WatchlistTrackId"];

    private readonly DownloadQueueRepository _queueRepository;
    private readonly LibraryRepository _libraryRepository;
    private readonly PlaylistWatchReconciler _playlistWatchReconciler;
    private readonly WatchlistRunSignal _runSignal;
    private readonly IWatchlistPostDownloadSyncNotifier _notifier;
    private readonly ILogger<WatchlistFinalizationService> _logger;

    public WatchlistFinalizationService(
        DownloadQueueRepository queueRepository,
        LibraryRepository libraryRepository,
        PlaylistWatchReconciler playlistWatchReconciler,
        WatchlistRunSignal runSignal,
        IWatchlistPostDownloadSyncNotifier notifier,
        ILogger<WatchlistFinalizationService> logger)
    {
        _queueRepository = queueRepository;
        _libraryRepository = libraryRepository;
        _playlistWatchReconciler = playlistWatchReconciler;
        _runSignal = runSignal;
        _notifier = notifier;
        _logger = logger;
    }

    public async Task<int> NotifyQueueItemFinalizedAsync(
        DownloadQueueItem item,
        string? payloadJson,
        IEnumerable<string>? finalFilePaths,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.QueueUuid) || !_libraryRepository.IsConfigured)
        {
            return 0;
        }

        var normalizedFinalPaths = NormalizeFinalFilePaths(finalFilePaths);
        if (normalizedFinalPaths.Count == 0)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Watchlist finalization skipped for queue {QueueUuid} because no final file paths were available.",
                    item.QueueUuid);
            }
            return 0;
        }

        var verifiedAudioPaths = await ResolveVerifiedFinalAudioPathsAsync(
            item.QueueUuid,
            normalizedFinalPaths,
            cancellationToken);
        if (verifiedAudioPaths.Count == 0)
        {
            return 0;
        }

        var notifications = await ResolveNotificationsAsync(item, payloadJson, cancellationToken);
        var localTrackIds = await _libraryRepository.GetTrackIdsByFilePathsAsync(
            verifiedAudioPaths,
            cancellationToken);
        var localTrackId = localTrackIds.Values.FirstOrDefault();
        var persistedIdentity = localTrackId > 0
            ? await _libraryRepository.GetLocalTrackIdentityAsync(localTrackId, cancellationToken)
            : null;
        var identity = persistedIdentity == null
            ? BuildFinalizedTrackIdentity(item, payloadJson)
            : BuildFinalizedTrackIdentity(persistedIdentity);
        notifications = await VerifyNotificationsAsync(
            notifications,
            identity,
            localTrackId > 0 ? localTrackId : null,
            cancellationToken);

        var sent = 0;
        foreach (var notification in notifications)
        {
            await _libraryRepository.EnqueueWatchlistReconciliationRequestAsync(
                "playlist",
                notification.Source,
                notification.PlaylistId,
                cancellationToken);
            await _notifier.NotifyFinalizedAsync(
                notification.Source,
                notification.PlaylistId,
                notification.TrackId,
                item.QueueUuid,
                notification.DestinationFolderId,
                verifiedAudioPaths,
                cancellationToken);
            await _libraryRepository.UpdatePlaylistWatchDownloadClaimStatusAsync(
                item.QueueUuid,
                notification.Source,
                notification.PlaylistId,
                notification.TrackId,
                "completed",
                cancellationToken);
            sent++;
        }

        if (sent > 0)
        {
            _runSignal.Request();
        }

        return sent;
    }

    private async Task<List<WatchlistFinalizedNotification>> VerifyNotificationsAsync(
        IReadOnlyCollection<WatchlistFinalizedNotification> notifications,
        FinalizedTrackIdentity identity,
        long? localTrackId,
        CancellationToken cancellationToken)
    {
        var verified = new List<WatchlistFinalizedNotification>(notifications.Count);
        var playlists = (await _libraryRepository.GetPlaylistWatchlistAsync(cancellationToken))
            .GroupBy(
                static item => $"{NormalizeSource(item.Source)}|{item.SourceId}",
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        var candidatesByPlaylist = new Dictionary<string, IReadOnlyList<PlaylistTrackCandidate>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var notification in notifications)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var playlistKey = $"{NormalizeSource(notification.Source)}|{notification.PlaylistId}";
            playlists.TryGetValue(playlistKey, out var playlist);
            if (!candidatesByPlaylist.TryGetValue(playlistKey, out var candidates))
            {
                candidates = playlist == null
                    ? []
                    : await TryGetPlaylistTrackCandidatesAsync(playlist, cancellationToken);
                candidatesByPlaylist[playlistKey] = candidates;
            }
            var candidate = candidates.FirstOrDefault(item =>
                string.Equals(item.TrackSourceId, notification.TrackId, StringComparison.OrdinalIgnoreCase));
            if (candidate == null || !IsIdentityMatch(NormalizeSource(notification.Source), candidate, identity))
            {
                await _libraryRepository.UpdatePlaylistWatchTrackVerificationAsync(
                    notification.Source,
                    notification.PlaylistId,
                    new PlaylistWatchTrackVerification(
                        notification.TrackId,
                        localTrackId,
                        "review",
                        "Finalized audio identity does not match the monitored playlist track."),
                    cancellationToken);
                continue;
            }

            var finalizedSourceTrackId = identity.GetTrackIdForSource(NormalizeSource(notification.Source));
            var redirected = !string.IsNullOrWhiteSpace(finalizedSourceTrackId)
                && !string.Equals(finalizedSourceTrackId, notification.TrackId, StringComparison.OrdinalIgnoreCase);
            await _libraryRepository.UpdatePlaylistWatchTrackVerificationAsync(
                notification.Source,
                notification.PlaylistId,
                new PlaylistWatchTrackVerification(
                    notification.TrackId,
                    localTrackId,
                    redirected ? "redirected" : "identity_verified",
                    redirected ? "Verified replacement identity." : "Finalized audio identity verified.",
                    redirected ? finalizedSourceTrackId : null,
                    redirected ? "The downloaded source track redirected to a verified equivalent." : null),
                cancellationToken);
            verified.Add(notification);
        }

        return verified;
    }

    private static FinalizedTrackIdentity BuildFinalizedTrackIdentity(LocalTrackIdentityDto identity)
    {
        static string? SourceId(LocalTrackIdentityDto value, string source)
            => value.SourceIds.TryGetValue(source, out var sourceId) ? NormalizeId(sourceId) : null;

        return new FinalizedTrackIdentity(
            SourceId(identity, "spotify"),
            SourceId(identity, "deezer"),
            SourceId(identity, AppleSource) ?? SourceId(identity, "itunes"),
            SourceId(identity, "boomplay"),
            SourceId(identity, "qobuz"),
            SourceId(identity, "tidal"),
            NormalizeIsrc(identity.Isrc),
            NormalizeText(identity.Title),
            NormalizeText(identity.Artist),
            identity.DurationMs);
    }

    private async Task<List<string>> ResolveVerifiedFinalAudioPathsAsync(
        string queueUuid,
        IReadOnlyCollection<string> normalizedFinalPaths,
        CancellationToken cancellationToken)
    {
        var audioPaths = normalizedFinalPaths
            .Where(IsExistingAudioFile)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (audioPaths.Count == 0)
        {
            _logger.LogWarning(
                "Watchlist finalization skipped for queue {QueueUuid} because no existing final audio paths were available.",
                queueUuid);
            return [];
        }

        var trackIds = await _libraryRepository.GetTrackIdsByFilePathsAsync(audioPaths, cancellationToken);
        var missingPaths = audioPaths
            .Where(path => !trackIds.ContainsKey(path))
            .ToList();
        if (missingPaths.Count == 0)
        {
            return audioPaths;
        }

        _logger.LogWarning(
            "Watchlist finalization skipped for queue {QueueUuid} because {MissingCount}/{AudioCount} final audio file(s) are not in the library DB.",
            queueUuid,
            missingPaths.Count,
            audioPaths.Count);
        foreach (var missingPath in missingPaths.Take(10))
        {
            _logger.LogWarning("Watchlist finalization missing library DB file: {Path}", missingPath);
        }

        return [];
    }

    public async Task<int> RepairPlaylistAsync(
        PlaylistWatchlistDto playlist,
        CancellationToken cancellationToken)
        => await RepairPlaylistsAsync([playlist], cancellationToken);

    public async Task<int> RepairPlaylistsAsync(
        IReadOnlyCollection<PlaylistWatchlistDto> playlists,
        CancellationToken cancellationToken)
    {
        if (!_libraryRepository.IsConfigured || playlists.Count == 0)
        {
            return 0;
        }

        var playlistKeys = playlists
            .Where(static playlist => playlist != null)
            .Select(static playlist => $"{NormalizeSource(playlist.Source)}|{playlist.SourceId}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var items = await _queueRepository.GetTasksAsync(cancellationToken: cancellationToken);
        var sent = 0;
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCompletedStatus(item.Status))
            {
                continue;
            }

            var notifications = await ResolveNotificationsAsync(item, item.PayloadJson, cancellationToken);
            if (!notifications.Any(notification => playlistKeys.Contains(
                    $"{NormalizeSource(notification.Source)}|{notification.PlaylistId}")))
            {
                continue;
            }

            sent += await NotifyQueueItemFinalizedAsync(
                item,
                item.PayloadJson,
                DownloadQueueRepository.GetExistingMaterializedFilePaths(item),
                cancellationToken);
        }

        return sent;
    }

    private async Task<List<WatchlistFinalizedNotification>> ResolveNotificationsAsync(
        DownloadQueueItem item,
        string? payloadJson,
        CancellationToken cancellationToken)
    {
        var notifications = new List<WatchlistFinalizedNotification>();
        if (TryReadWatchlistTrackContext(payloadJson, out var source, out var playlistId, out var trackId))
        {
            notifications.Add(new WatchlistFinalizedNotification(
                source,
                playlistId,
                trackId,
                item.DestinationFolderId));
        }

        var claims = await _libraryRepository.GetPlaylistWatchDownloadClaimsAsync(
            item.QueueUuid,
            status: null,
            cancellationToken);
        foreach (var claim in claims)
        {
            notifications.Add(new WatchlistFinalizedNotification(
                claim.Source,
                claim.SourceId,
                claim.TrackSourceId,
                claim.DestinationFolderId ?? item.DestinationFolderId));
        }

        var inferred = await ResolveCrossPlaylistMatchesAsync(item, payloadJson, cancellationToken);
        notifications.AddRange(inferred);

        return notifications
            .Where(static notification =>
                !string.IsNullOrWhiteSpace(notification.Source)
                && !string.IsNullOrWhiteSpace(notification.PlaylistId)
                && !string.IsNullOrWhiteSpace(notification.TrackId))
            .GroupBy(
                static notification => $"{notification.Source.Trim().ToLowerInvariant()}|{notification.PlaylistId.Trim()}|{notification.TrackId.Trim()}",
                StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToList();
    }

    private async Task<List<WatchlistFinalizedNotification>> ResolveCrossPlaylistMatchesAsync(
        DownloadQueueItem item,
        string? payloadJson,
        CancellationToken cancellationToken)
    {
        var identity = BuildFinalizedTrackIdentity(item, payloadJson);
        if (!identity.HasAnyIdentity)
        {
            return new List<WatchlistFinalizedNotification>();
        }

        var playlists = await _libraryRepository.GetPlaylistWatchlistAsync(cancellationToken);
        if (playlists.Count == 0)
        {
            return new List<WatchlistFinalizedNotification>();
        }

        var results = new List<WatchlistFinalizedNotification>();
        foreach (var playlist in playlists)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.AddRange(await ResolveCrossPlaylistMatchesForPlaylistAsync(
                playlist,
                item.DestinationFolderId,
                identity,
                cancellationToken));
        }

        return results;
    }

    private async Task<List<WatchlistFinalizedNotification>> ResolveCrossPlaylistMatchesForPlaylistAsync(
        PlaylistWatchlistDto playlist,
        long? queueDestinationFolderId,
        FinalizedTrackIdentity identity,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(playlist.Source) || string.IsNullOrWhiteSpace(playlist.SourceId))
        {
            return [];
        }

        var candidates = await TryGetPlaylistTrackCandidatesAsync(playlist, cancellationToken);
        if (candidates.Count == 0)
        {
            return [];
        }

        var destinationFolderId = await ResolveFinalizedDestinationFolderIdAsync(
            playlist,
            queueDestinationFolderId,
            cancellationToken);
        var normalizedSource = NormalizeSource(playlist.Source);
        return candidates
            .Where(candidate => IsIdentityMatch(normalizedSource, candidate, identity))
            .Select(candidate => new WatchlistFinalizedNotification(
                normalizedSource,
                playlist.SourceId,
                candidate.TrackSourceId,
                destinationFolderId))
            .ToList();
    }

    private async Task<IReadOnlyList<PlaylistTrackCandidate>> TryGetPlaylistTrackCandidatesAsync(
        PlaylistWatchlistDto playlist,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _playlistWatchReconciler.GetCachedPlaylistTrackCandidatesAsync(
                playlist.Source,
                playlist.SourceId,
                cancellationToken);
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    ex,
                    "Watchlist finalization candidate cache read failed for {Source}:{PlaylistId}.",
                    playlist.Source,
                    playlist.SourceId);
            }

            return [];
        }
    }

    private async Task<long?> ResolveFinalizedDestinationFolderIdAsync(
        PlaylistWatchlistDto playlist,
        long? queueDestinationFolderId,
        CancellationToken cancellationToken)
    {
        if (queueDestinationFolderId.HasValue)
        {
            return queueDestinationFolderId;
        }

        var preference = await _libraryRepository.GetPlaylistWatchPreferenceAsync(
            playlist.Source,
            playlist.SourceId,
            cancellationToken);
        return preference?.DestinationFolderId;
    }

    private static bool IsCompletedStatus(string? status)
        => string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "complete", StringComparison.OrdinalIgnoreCase);

    private static bool IsIdentityMatch(
        string normalizedPlaylistSource,
        PlaylistTrackCandidate candidate,
        FinalizedTrackIdentity identity)
    {
        var sourceTrackId = identity.GetTrackIdForSource(normalizedPlaylistSource);
        if (!string.IsNullOrWhiteSpace(sourceTrackId)
            && string.Equals(candidate.TrackSourceId, sourceTrackId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(identity.Isrc)
            && !string.IsNullOrWhiteSpace(candidate.Isrc)
            && string.Equals(candidate.Isrc.Trim(), identity.Isrc, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(identity.Title)
            || string.IsNullOrWhiteSpace(identity.Artist)
            || string.IsNullOrWhiteSpace(candidate.Title)
            || string.IsNullOrWhiteSpace(candidate.Artist))
        {
            return false;
        }

        if (!TrackTitleMatcher.TitlesMatch(identity.Title, candidate.Title)
            || !TrackTitleMatcher.ArtistsMatch(identity.Artist, candidate.Artist))
        {
            return false;
        }

        if (identity.DurationMs.HasValue
            && candidate.DurationMs.HasValue
            && Math.Abs(identity.DurationMs.Value - candidate.DurationMs.Value) > 3000)
        {
            return false;
        }

        return true;
    }

    private static FinalizedTrackIdentity BuildFinalizedTrackIdentity(DownloadQueueItem item, string? payloadJson)
    {
        string? sourceIdsSpotify = null;
        string? sourceIdsDeezer = null;
        string? sourceIdsApple = null;
        string? sourceIdsBoomplay = null;
        string? sourceIdsQobuz = null;
        string? sourceIdsTidal = null;
        string? payloadIsrc = null;
        string? payloadTitle = null;
        string? payloadArtist = null;
        int? payloadDurationMs = null;

        if (!string.IsNullOrWhiteSpace(payloadJson))
        {
            try
            {
                using var document = JsonDocument.Parse(payloadJson);
                var root = document.RootElement;
                if (TryReadSourceIdsElement(root, out var sourceIds))
                {
                    sourceIdsSpotify = ReadSourceIdAlias(sourceIds, "spotify", "spotify_id", "spotifyTrackId");
                    sourceIdsDeezer = ReadSourceIdAlias(sourceIds, "deezer", "deezer_id", "deezerTrackId");
                    sourceIdsApple = ReadSourceIdAlias(sourceIds, AppleSource, "apple_id", "appleTrackId", "itunes");
                    sourceIdsBoomplay = ReadSourceIdAlias(sourceIds, "boomplay", "boomplay_id");
                    sourceIdsQobuz = ReadSourceIdAlias(sourceIds, "qobuz", "qobuz_id");
                    sourceIdsTidal = ReadSourceIdAlias(sourceIds, "tidal", "tidal_id");
                }

                payloadIsrc = ReadStringAlias(root, "isrc", "ISRC");
                payloadTitle = ReadStringAlias(root, "title", "Title", "trackTitle");
                payloadArtist = ReadStringAlias(root, "artist", "Artist", "artistName");
                payloadDurationMs = ReadIntAlias(root, "durationMs", "DurationMs", "duration");
            }
            catch (JsonException)
            {
                // Ignore malformed payload and continue with queue row metadata.
            }
        }

        return new FinalizedTrackIdentity(
            NormalizeId(item.SpotifyTrackId) ?? NormalizeId(sourceIdsSpotify),
            NormalizeId(item.DeezerTrackId) ?? NormalizeId(sourceIdsDeezer),
            NormalizeId(item.AppleTrackId) ?? NormalizeId(sourceIdsApple),
            NormalizeId(sourceIdsBoomplay),
            NormalizeId(sourceIdsQobuz),
            NormalizeId(sourceIdsTidal),
            NormalizeIsrc(item.Isrc) ?? NormalizeIsrc(payloadIsrc),
            NormalizeText(item.TrackTitle) ?? NormalizeText(payloadTitle),
            NormalizeText(item.ArtistName) ?? NormalizeText(payloadArtist),
            item.DurationMs ?? payloadDurationMs);
    }

    private static string? ReadSourceIdAlias(JsonElement sourceIds, params string[] aliases)
        => ReadStringAlias(sourceIds, aliases);

    private static string? ReadStringAlias(JsonElement source, params string[] aliases)
    {
        foreach (var alias in aliases)
        {
            if (!TryGetPropertyIgnoreCase(source, alias, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text.Trim();
                }
            }
            else if (value.ValueKind == JsonValueKind.Number)
            {
                return value.GetRawText().Trim();
            }
        }

        return null;
    }

    private static int? ReadIntAlias(JsonElement source, params string[] aliases)
    {
        foreach (var alias in aliases)
        {
            if (!TryGetPropertyIgnoreCase(source, alias, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsedNumber))
            {
                return parsedNumber;
            }

            if (value.ValueKind == JsonValueKind.String
                && int.TryParse(value.GetString(), out var parsedString))
            {
                return parsedString;
            }
        }

        return null;
    }

    private static string NormalizeSource(string source)
    {
        var normalized = source.Trim().ToLowerInvariant();
        return normalized switch
        {
            "smarttracks" => "smarttracklist",
            "recommendation" => "recommendations",
            "itunes" => AppleSource,
            "applemusic" => AppleSource,
            _ => normalized
        };
    }

    private static string? NormalizeText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeId(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeIsrc(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    private static List<string> NormalizeFinalFilePaths(IEnumerable<string>? finalFilePaths)
    {
        if (finalFilePaths is null)
        {
            return new List<string>();
        }

        return finalFilePaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => DownloadPathResolver.NormalizeDisplayPath(path))
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsExistingAudioFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return extension is ".mp3" or ".flac" or ".m4a" or ".m4b" or ".wav" or ".ogg" or ".opus" or ".aiff" or ".aif" or ".alac" or ".aac";
    }

    private static bool TryReadWatchlistTrackContext(
        string? payloadJson,
        out string source,
        out string playlistId,
        out string trackId)
    {
        source = string.Empty;
        playlistId = string.Empty;
        trackId = string.Empty;
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (TryReadWatchlistTrackContextFromSourceIds(root, out source, out playlistId, out trackId))
            {
                return true;
            }

            if (!TryReadStringProperty(root, WatchlistSourcePropertyNames, out source)
                || !TryReadStringProperty(root, WatchlistPlaylistIdPropertyNames, out playlistId)
                || !TryReadStringProperty(root, WatchlistTrackIdPropertyNames, out trackId))
            {
                return false;
            }

            return !string.IsNullOrWhiteSpace(source)
                && !string.IsNullOrWhiteSpace(playlistId)
                && !string.IsNullOrWhiteSpace(trackId);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadWatchlistTrackContextFromSourceIds(
        JsonElement payloadRoot,
        out string source,
        out string playlistId,
        out string trackId)
    {
        source = string.Empty;
        playlistId = string.Empty;
        trackId = string.Empty;
        if (!TryReadSourceIdsElement(payloadRoot, out var sourceIds))
        {
            return false;
        }

        if (!TryReadStringProperty(sourceIds, SourceIdsWatchlistSourcePropertyNames, out source)
            || !TryReadStringProperty(sourceIds, SourceIdsWatchlistPlaylistIdPropertyNames, out playlistId)
            || !TryReadStringProperty(sourceIds, SourceIdsWatchlistTrackIdPropertyNames, out trackId))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(source)
            && !string.IsNullOrWhiteSpace(playlistId)
            && !string.IsNullOrWhiteSpace(trackId);
    }

    private static bool TryReadSourceIdsElement(JsonElement payloadRoot, out JsonElement sourceIds)
    {
        if (TryGetPropertyIgnoreCase(payloadRoot, "source_ids", out sourceIds)
            || TryGetPropertyIgnoreCase(payloadRoot, "sourceIds", out sourceIds))
        {
            return sourceIds.ValueKind == JsonValueKind.Object;
        }

        sourceIds = default;
        return false;
    }

    private static bool TryReadStringProperty(JsonElement source, IReadOnlyList<string> propertyNames, out string value)
    {
        var property = propertyNames
            .Select(propertyName => TryGetPropertyIgnoreCase(source, propertyName, out var property) ? property : default)
            .FirstOrDefault(property => property.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(property.GetString()));
        if (property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString()!.Trim();
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var property = element.EnumerateObject()
                .FirstOrDefault(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));
            if (property.Value.ValueKind != JsonValueKind.Undefined)
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private sealed record WatchlistFinalizedNotification(
        string Source,
        string PlaylistId,
        string TrackId,
        long? DestinationFolderId);

    private sealed record FinalizedTrackIdentity(
        string? SpotifyTrackId,
        string? DeezerTrackId,
        string? AppleTrackId,
        string? BoomplayTrackId,
        string? QobuzTrackId,
        string? TidalTrackId,
        string? Isrc,
        string? Title,
        string? Artist,
        int? DurationMs)
    {
        public bool HasAnyIdentity
            => !string.IsNullOrWhiteSpace(SpotifyTrackId)
               || !string.IsNullOrWhiteSpace(DeezerTrackId)
               || !string.IsNullOrWhiteSpace(AppleTrackId)
               || !string.IsNullOrWhiteSpace(BoomplayTrackId)
               || !string.IsNullOrWhiteSpace(QobuzTrackId)
               || !string.IsNullOrWhiteSpace(TidalTrackId)
               || !string.IsNullOrWhiteSpace(Isrc)
               || (!string.IsNullOrWhiteSpace(Title) && !string.IsNullOrWhiteSpace(Artist));

        public string? GetTrackIdForSource(string normalizedSource)
            => normalizedSource switch
            {
                "spotify" => SpotifyTrackId,
                "deezer" => DeezerTrackId,
                AppleSource => AppleTrackId,
                "boomplay" => BoomplayTrackId,
                "qobuz" => QobuzTrackId,
                "tidal" => TidalTrackId,
                _ => null
            };
    }
}
