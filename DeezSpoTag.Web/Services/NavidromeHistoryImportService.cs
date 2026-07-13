using System.Text.Json;
using DeezSpoTag.Integrations.Navidrome;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed class NavidromeHistoryImportService
{
    private static readonly TimeSpan ImportOverlap = TimeSpan.FromMinutes(1);
    private readonly NavidromeApiClient _navidromeApiClient;
    private readonly PlatformAuthService _authService;
    private readonly LibraryRepository _libraryRepository;
    private readonly ILogger<NavidromeHistoryImportService> _logger;

    public NavidromeHistoryImportService(
        NavidromeApiClient navidromeApiClient,
        PlatformAuthService authService,
        LibraryRepository libraryRepository,
        ILogger<NavidromeHistoryImportService> logger)
    {
        _navidromeApiClient = navidromeApiClient;
        _authService = authService;
        _libraryRepository = libraryRepository;
        _logger = logger;
    }

    public async Task<int> ImportAsync(CancellationToken cancellationToken = default)
    {
        var state = await _authService.LoadAsync();
        var navidrome = state.Navidrome;
        if (navidrome is null
            || string.IsNullOrWhiteSpace(navidrome.Url)
            || string.IsNullOrWhiteSpace(navidrome.Username)
            || string.IsNullOrWhiteSpace(navidrome.Password))
        {
            _logger.LogWarning("Navidrome auth missing; skipping history import.");
            return 0;
        }

        var username = navidrome.Username.Trim();
        var historyUserId = await _libraryRepository.EnsurePlexUserAsync(
            username,
            $"navidrome:{username}",
            navidrome.Url,
            navidrome.ServerName,
            cancellationToken);
        var latestImportedUtc = await _libraryRepository.GetLatestPlayHistoryUtcAsync(
            historyUserId,
            "navidrome",
            cancellationToken);
        var importFromUtc = latestImportedUtc?.Subtract(ImportOverlap);
        var history = await _navidromeApiClient.GetPlayHistoryAsync(
            navidrome.Url,
            username,
            navidrome.Password,
            importFromUtc,
            cancellationToken: cancellationToken);

        var stats = new ImportStats();
        var metadataLookupCache = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in history)
        {
            var trackId = await ResolveTrackIdAsync(item, metadataLookupCache, stats, cancellationToken);
            var libraryId = trackId.HasValue
                ? await _libraryRepository.GetLibraryIdForTrackAsync(trackId.Value, cancellationToken)
                : null;
            if (!trackId.HasValue)
            {
                stats.Unresolved++;
            }

            if (await _libraryRepository.AddPlayHistoryAsync(
                    new LibraryRepository.PlayHistoryWriteInput(
                        historyUserId,
                        libraryId,
                        trackId,
                        item.ItemId,
                        null,
                        item.PlayedAtUtc,
                        item.DurationMs,
                        JsonSerializer.Serialize(item),
                        "navidrome"),
                    cancellationToken))
            {
                stats.Inserted++;
            }
        }

        _logger.LogInformation(
            "Imported {Count} Navidrome history entries from {FetchedCount} fetched. resolvedByPath={ResolvedByPath} resolvedByMetadata={ResolvedByMetadata} unresolved={Unresolved}.",
            stats.Inserted,
            history.Count,
            stats.ResolvedByFilePath,
            stats.ResolvedByMetadata,
            stats.Unresolved);
        return stats.Inserted;
    }

    private async Task<long?> ResolveTrackIdAsync(
        NavidromeHistoryItem item,
        Dictionary<string, long?> metadataLookupCache,
        ImportStats stats,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(item.FilePath))
        {
            var pathMatch = await _libraryRepository.GetTrackIdForFilePathAsync(item.FilePath, cancellationToken);
            if (pathMatch.HasValue)
            {
                stats.ResolvedByFilePath++;
                return pathMatch;
            }
        }

        if (string.IsNullOrWhiteSpace(item.Artist) || string.IsNullOrWhiteSpace(item.Title))
        {
            return null;
        }

        var cacheKey = $"{item.Artist.Trim().ToLowerInvariant()}|{item.Title.Trim().ToLowerInvariant()}|{item.DurationMs}";
        if (metadataLookupCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var resolved = await _libraryRepository.GetLocalTrackIdByTrackMetadataAsync(
            item.Artist,
            item.Title,
            item.DurationMs,
            cancellationToken);
        if (resolved.HasValue)
        {
            stats.ResolvedByMetadata++;
        }

        metadataLookupCache[cacheKey] = resolved;
        return resolved;
    }

    private sealed class ImportStats
    {
        public int Inserted { get; set; }
        public int ResolvedByFilePath { get; set; }
        public int ResolvedByMetadata { get; set; }
        public int Unresolved { get; set; }
    }
}
