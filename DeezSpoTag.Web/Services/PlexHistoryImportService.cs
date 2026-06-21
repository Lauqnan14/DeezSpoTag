using DeezSpoTag.Integrations.Plex;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed class PlexHistoryImportService
{
    private static readonly TimeSpan ImportOverlap = TimeSpan.FromMinutes(1);
    private readonly PlexApiClient _plexApiClient;
    private readonly PlatformAuthService _authService;
    private readonly LibraryRepository _libraryRepository;
    private readonly ILogger<PlexHistoryImportService> _logger;

    public PlexHistoryImportService(
        PlexApiClient plexApiClient,
        PlatformAuthService authService,
        LibraryRepository libraryRepository,
        ILogger<PlexHistoryImportService> logger)
    {
        _plexApiClient = plexApiClient;
        _authService = authService;
        _libraryRepository = libraryRepository;
        _logger = logger;
    }

    public async Task<int> ImportAsync(CancellationToken cancellationToken = default)
    {
        var state = await _authService.LoadAsync();
        var plex = state.Plex;
        if (plex is null || string.IsNullOrWhiteSpace(plex.Url) || string.IsNullOrWhiteSpace(plex.Token))
        {
            _logger.LogWarning("Plex auth missing; skipping history import.");
            return 0;
        }

        var username = !string.IsNullOrWhiteSpace(plex.Username) ? plex.Username : plex.ServerName;
        var plexUserId = await _libraryRepository.EnsurePlexUserAsync(
            username,
            plex.Username,
            plex.Url,
            plex.MachineIdentifier,
            cancellationToken);

        var history = await _plexApiClient.GetHistoryAsync(plex.Url, plex.Token, cancellationToken);
        var latestImportedUtc = await _libraryRepository.GetLatestPlayHistoryUtcAsync(
            plexUserId,
            "plex",
            cancellationToken);
        var importFromUtc = latestImportedUtc?.Subtract(ImportOverlap);
        var pendingHistory = history
            .Where(static item => item.ViewedAtUtc is not null)
            .Where(item => !importFromUtc.HasValue || item.ViewedAtUtc!.Value >= importFromUtc.Value)
            .ToList();
        var knownRatingKeys = pendingHistory
            .Select(item => item.RatingKey?.Trim())
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToList();
        var trackIdsByRatingKey = new Dictionary<string, long>(
            await _libraryRepository.GetTrackIdsByPlexRatingKeysAsync(
                knownRatingKeys,
                cancellationToken),
            StringComparer.OrdinalIgnoreCase);
        var stats = new ImportStats();
        var metadataLookupCache = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
        var ratingKeyUpserts = new List<PlexTrackMetadataUpsertDto>();

        foreach (var item in pendingHistory)
        {
            var trackId = await ResolveTrackIdAsync(item, trackIdsByRatingKey, metadataLookupCache, stats, cancellationToken);
            var libraryId = await ResolveLibraryIdAsync(item.FilePath, cancellationToken);
            if (!trackId.HasValue) stats.Unresolved++;

            AddRatingKeyUpsertIfAvailable(item.RatingKey, trackId, trackIdsByRatingKey, ratingKeyUpserts);
            await WritePlayHistoryAsync(plexUserId, libraryId, trackId, item, cancellationToken);
            stats.Inserted++;
        }

        if (ratingKeyUpserts.Count > 0)
        {
            await _libraryRepository.UpsertPlexTrackMetadataAsync(ratingKeyUpserts, cancellationToken);
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Processed {Count} incremental Plex history entries from {FetchedCount} fetched. resolvedByPath={ResolvedByPath} resolvedByRatingKey={ResolvedByRatingKey} resolvedByMetadata={ResolvedByMetadata} unresolved={Unresolved}.",
                stats.Inserted,
                history.Count,
                stats.ResolvedByFilePath,
                stats.ResolvedByRatingKey,
                stats.ResolvedByMetadata,
                stats.Unresolved);
        }
        return stats.Inserted;
    }

    private async Task<long?> ResolveTrackIdAsync(
        PlexHistoryItem item,
        Dictionary<string, long> trackIdsByRatingKey,
        Dictionary<string, long?> metadataLookupCache,
        ImportStats stats,
        CancellationToken cancellationToken)
    {
        var trackId = await TryResolveTrackIdByFilePathAsync(item.FilePath, cancellationToken);
        if (trackId.HasValue)
        {
            stats.ResolvedByFilePath++;
            return trackId;
        }

        if (TryResolveTrackIdByRatingKey(item.RatingKey, trackIdsByRatingKey, out var mappedTrackId))
        {
            stats.ResolvedByRatingKey++;
            return mappedTrackId;
        }

        trackId = await TryResolveTrackIdByMetadataAsync(item, metadataLookupCache, cancellationToken);
        if (trackId.HasValue)
        {
            stats.ResolvedByMetadata++;
        }

        return trackId;
    }

    private async Task<long?> TryResolveTrackIdByFilePathAsync(string? filePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        return await _libraryRepository.GetTrackIdForFilePathAsync(filePath, cancellationToken);
    }

    private static bool TryResolveTrackIdByRatingKey(
        string? ratingKey,
        Dictionary<string, long> trackIdsByRatingKey,
        out long trackId)
    {
        trackId = default;
        if (string.IsNullOrWhiteSpace(ratingKey))
        {
            return false;
        }

        return trackIdsByRatingKey.TryGetValue(ratingKey, out trackId);
    }

    private async Task<long?> ResolveLibraryIdAsync(string? filePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        var folder = await _libraryRepository.ResolveFolderForPathAsync(filePath, cancellationToken);
        return folder?.LibraryId;
    }

    private static void AddRatingKeyUpsertIfAvailable(
        string? ratingKey,
        long? trackId,
        Dictionary<string, long> trackIdsByRatingKey,
        List<PlexTrackMetadataUpsertDto> ratingKeyUpserts)
    {
        if (!trackId.HasValue || string.IsNullOrWhiteSpace(ratingKey))
        {
            return;
        }

        var normalizedKey = ratingKey.Trim();
        trackIdsByRatingKey[normalizedKey] = trackId.Value;
        ratingKeyUpserts.Add(new PlexTrackMetadataUpsertDto(
            trackId.Value,
            normalizedKey,
            DateTimeOffset.UtcNow));
    }

    private async Task WritePlayHistoryAsync(
        long plexUserId,
        long? libraryId,
        long? trackId,
        PlexHistoryItem item,
        CancellationToken cancellationToken)
    {
        await _libraryRepository.AddPlayHistoryAsync(
            new LibraryRepository.PlayHistoryWriteInput(
                plexUserId,
                libraryId,
                trackId,
                string.IsNullOrWhiteSpace(item.FilePath) ? item.RatingKey : item.FilePath,
                item.RatingKey,
                item.ViewedAtUtc!.Value,
                item.DurationMs > 0 ? (int?)item.DurationMs : null,
                null),
            cancellationToken);
    }

    private async Task<long?> TryResolveTrackIdByMetadataAsync(
        PlexHistoryItem item,
        Dictionary<string, long?> cache,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.Artist) || string.IsNullOrWhiteSpace(item.Title))
        {
            return null;
        }

        var cacheKey = $"{item.Artist.Trim().ToLowerInvariant()}|{item.Title.Trim().ToLowerInvariant()}|{item.DurationMs}";
        if (cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var durationMs = item.DurationMs > 0 ? (int?)item.DurationMs : null;
        var resolved = await _libraryRepository.GetLocalTrackIdByTrackMetadataAsync(
            item.Artist,
            item.Title,
            durationMs,
            cancellationToken);
        cache[cacheKey] = resolved;
        return resolved;
    }

    private sealed class ImportStats
    {
        public int Inserted { get; set; }
        public int ResolvedByFilePath { get; set; }
        public int ResolvedByRatingKey { get; set; }
        public int ResolvedByMetadata { get; set; }
        public int Unresolved { get; set; }
    }
}
