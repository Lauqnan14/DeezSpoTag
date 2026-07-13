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

        var stats = new ImportStats();
        var fetchedCount = 0;
        var folders = (await _libraryRepository.GetConfiguredEnabledMusicFoldersAsync(cancellationToken))
            .Where(static folder => !string.IsNullOrWhiteSpace(folder.JellyfinLibraryId))
            .ToList();
        foreach (var folder in folders)
        {
            var history = await _jellyfinApiClient.GetAudioPlayHistoryAsync(
                jellyfin.Url, jellyfin.ApiKey, jellyfin.UserId, folder.JellyfinLibraryId!,
                cancellationToken: cancellationToken);
            fetchedCount += history.Count;
            foreach (var item in history)
            {
                var trackId = await ResolveTrackIdAsync(item, folder, stats, cancellationToken);
                if (!trackId.HasValue) stats.Unresolved++;
                if (await _libraryRepository.AddPlayHistoryAsync(
                        new LibraryRepository.PlayHistoryWriteInput(
                            userId, folder.LibraryId, trackId, item.FilePath, item.ItemId,
                            item.PlayedAtUtc, item.DurationMs, JsonSerializer.Serialize(item),
                            "jellyfin", folder.Id),
                        cancellationToken))
                {
                    stats.Inserted++;
                }
            }
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Imported {Count} folder-scoped Jellyfin history entries from {FetchedCount} fetched. resolvedByPath={ResolvedByPath} resolvedByMetadata={ResolvedByMetadata} unresolved={Unresolved}.",
                stats.Inserted,
                fetchedCount,
                stats.ResolvedByFilePath,
                stats.ResolvedByMetadata,
                stats.Unresolved);
        }

        return stats.Inserted;
    }

    private async Task<long?> ResolveTrackIdAsync(
        JellyfinHistoryItem item,
        FolderDto folder,
        ImportStats stats,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(item.FilePath))
        {
            var pathMatch = await _libraryRepository.GetTrackIdForFilePathAsync(item.FilePath, cancellationToken);
            if (pathMatch.HasValue && await _libraryRepository.GetFolderScopeForTrackAsync(
                    pathMatch.Value, folder.Id, folder.LibraryId, cancellationToken) is not null)
            {
                stats.ResolvedByFilePath++;
                return pathMatch;
            }
        }

        var resolved = await _libraryRepository.ResolveLocalTrackIdentityAsync(
            new LibraryRepository.LibraryExistenceInput(
                null, item.Title, item.Artist, item.DurationMs, "jellyfin", item.ItemId, item.Album),
            folder.LibraryId,
            folder.Id,
            cancellationToken);
        if (resolved.LocalTrackId.HasValue)
        {
            stats.ResolvedByMetadata++;
        }
        return resolved.LocalTrackId;
    }

    private sealed class ImportStats
    {
        public int Inserted { get; set; }
        public int ResolvedByFilePath { get; set; }
        public int ResolvedByMetadata { get; set; }
        public int Unresolved { get; set; }
    }
}
