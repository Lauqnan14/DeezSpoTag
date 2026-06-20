using System.Text.Json;
using DeezSpoTag.Integrations.Jellyfin;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed class JellyfinHistoryImportService
{
    private readonly JellyfinApiClient _jellyfinApiClient;
    private readonly PlatformAuthService _authService;
    private readonly LibraryRepository _libraryRepository;
    private readonly ILogger<JellyfinHistoryImportService> _logger;

    public JellyfinHistoryImportService(
        JellyfinApiClient jellyfinApiClient,
        PlatformAuthService authService,
        LibraryRepository libraryRepository,
        ILogger<JellyfinHistoryImportService> logger)
    {
        _jellyfinApiClient = jellyfinApiClient;
        _authService = authService;
        _libraryRepository = libraryRepository;
        _logger = logger;
    }

    public async Task<int> ImportAsync(CancellationToken cancellationToken = default)
    {
        var state = await _authService.LoadAsync();
        var jellyfin = state.Jellyfin;
        if (jellyfin is null
            || string.IsNullOrWhiteSpace(jellyfin.Url)
            || string.IsNullOrWhiteSpace(jellyfin.ApiKey)
            || string.IsNullOrWhiteSpace(jellyfin.UserId))
        {
            _logger.LogWarning("Jellyfin auth missing; skipping history import.");
            return 0;
        }

        var username = !string.IsNullOrWhiteSpace(jellyfin.Username) ? jellyfin.Username : jellyfin.ServerName;
        var userId = await _libraryRepository.EnsurePlexUserAsync(
            username,
            $"jellyfin:{jellyfin.UserId}",
            jellyfin.Url,
            jellyfin.ServerName,
            cancellationToken);

        var history = await _jellyfinApiClient.GetAudioPlayHistoryAsync(
            jellyfin.Url,
            jellyfin.ApiKey,
            jellyfin.UserId,
            cancellationToken: cancellationToken);

        var stats = new ImportStats();
        var metadataLookupCache = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in history)
        {
            var trackId = await ResolveTrackIdAsync(item, metadataLookupCache, stats, cancellationToken);
            var libraryId = await ResolveLibraryIdAsync(item.FilePath, cancellationToken);
            if (!trackId.HasValue)
            {
                stats.Unresolved++;
            }

            await _libraryRepository.AddPlayHistoryAsync(
                new LibraryRepository.PlayHistoryWriteInput(
                    userId,
                    libraryId,
                    trackId,
                    item.FilePath,
                    item.ItemId,
                    item.PlayedAtUtc,
                    item.DurationMs,
                    JsonSerializer.Serialize(item),
                    "jellyfin"),
                cancellationToken);
            stats.Inserted++;
        }

        _logger.LogInformation(
            "Imported {Count} Jellyfin history entries. resolvedByPath={ResolvedByPath} resolvedByMetadata={ResolvedByMetadata} unresolved={Unresolved}.",
            stats.Inserted,
            stats.ResolvedByFilePath,
            stats.ResolvedByMetadata,
            stats.Unresolved);

        return stats.Inserted;
    }

    private async Task<long?> ResolveTrackIdAsync(
        JellyfinHistoryItem item,
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

    private async Task<long?> ResolveLibraryIdAsync(string? filePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        var folder = await _libraryRepository.ResolveFolderForPathAsync(filePath, cancellationToken);
        return folder?.LibraryId;
    }

    private sealed class ImportStats
    {
        public int Inserted { get; set; }
        public int ResolvedByFilePath { get; set; }
        public int ResolvedByMetadata { get; set; }
        public int Unresolved { get; set; }
    }
}
