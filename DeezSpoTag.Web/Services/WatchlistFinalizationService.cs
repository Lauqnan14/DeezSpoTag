using System.Text.Json;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed class WatchlistFinalizationService
{
    private static readonly string[] WatchlistSourcePropertyNames = ["watchlistSource", "watchlist_source", "WatchlistSource"];
    private static readonly string[] WatchlistPlaylistIdPropertyNames = ["watchlistPlaylistId", "watchlist_playlist", "WatchlistPlaylistId"];
    private static readonly string[] WatchlistTrackIdPropertyNames = ["watchlistTrackId", "watchlist_track", "WatchlistTrackId"];
    private static readonly string[] SourceIdsWatchlistSourcePropertyNames = ["watchlist_source", "watchlistSource", "WatchlistSource"];
    private static readonly string[] SourceIdsWatchlistPlaylistIdPropertyNames = ["watchlist_playlist", "watchlistPlaylistId", "WatchlistPlaylistId"];
    private static readonly string[] SourceIdsWatchlistTrackIdPropertyNames = ["watchlist_track", "watchlistTrackId", "WatchlistTrackId"];

    private readonly DownloadQueueRepository _queueRepository;
    private readonly LibraryRepository _libraryRepository;
    private readonly IWatchlistPostDownloadSyncNotifier _notifier;
    private readonly ILogger<WatchlistFinalizationService> _logger;

    public WatchlistFinalizationService(
        DownloadQueueRepository queueRepository,
        LibraryRepository libraryRepository,
        IWatchlistPostDownloadSyncNotifier notifier,
        ILogger<WatchlistFinalizationService> logger)
    {
        _queueRepository = queueRepository;
        _libraryRepository = libraryRepository;
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
            normalizedFinalPaths = NormalizeFinalFilePaths(ReadFinalDestinationPaths(payloadJson));
        }

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

        var notifications = await ResolveNotificationsAsync(item, payloadJson, cancellationToken);
        var sent = 0;
        foreach (var notification in notifications)
        {
            await _notifier.NotifyFinalizedAsync(
                notification.Source,
                notification.PlaylistId,
                notification.TrackId,
                notification.DestinationFolderId,
                normalizedFinalPaths,
                cancellationToken);
            sent++;
        }

        return sent;
    }

    public async Task<int> RepairPlaylistAsync(
        PlaylistWatchlistDto playlist,
        CancellationToken cancellationToken)
    {
        if (!_libraryRepository.IsConfigured)
        {
            return 0;
        }

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
            if (!notifications.Any(notification =>
                    string.Equals(notification.Source, playlist.Source, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(notification.PlaylistId, playlist.SourceId, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            sent += await NotifyQueueItemFinalizedAsync(
                item,
                item.PayloadJson,
                finalFilePaths: null,
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

    private static bool IsCompletedStatus(string? status)
        => string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "complete", StringComparison.OrdinalIgnoreCase);

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

    private static IReadOnlyList<string> ReadFinalDestinationPaths(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return Array.Empty<string>();
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (!TryGetPropertyIgnoreCase(document.RootElement, "finalDestinations", out var finalDestinations)
                || finalDestinations.ValueKind != JsonValueKind.Object)
            {
                return Array.Empty<string>();
            }

            var paths = new List<string>();
            foreach (var value in finalDestinations.EnumerateObject().Select(static property => property.Value))
            {
                if (value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(value.GetString()))
                {
                    paths.Add(value.GetString()!);
                }
            }

            return paths;
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
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
        foreach (var propertyName in propertyNames)
        {
            if (TryGetPropertyIgnoreCase(source, propertyName, out var property)
                && property.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(property.GetString()))
            {
                value = property.GetString()!.Trim();
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
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
}
