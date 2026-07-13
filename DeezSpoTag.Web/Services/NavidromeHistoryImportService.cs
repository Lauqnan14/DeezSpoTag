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
        var stats = new ImportStats();
        var fetchedCount = 0;
        var folders = (await _libraryRepository.GetConfiguredEnabledMusicFoldersAsync(cancellationToken))
            .Where(static folder => !string.IsNullOrWhiteSpace(folder.NavidromeLibraryId))
            .ToList();
        foreach (var folder in folders)
        {
            var latest = await _libraryRepository.GetLatestPlayHistoryUtcForFolderAsync(
                historyUserId, "navidrome", folder.Id, cancellationToken);
            var history = await _navidromeApiClient.GetPlayHistoryAsync(
                navidrome.Url, username, navidrome.Password, folder.NavidromeLibraryId!,
                latest?.Subtract(ImportOverlap), cancellationToken: cancellationToken);
            fetchedCount += history.Count;
            foreach (var item in history)
            {
                var trackId = await ResolveTrackIdAsync(item, folder, stats, cancellationToken);
                if (!trackId.HasValue) stats.Unresolved++;
                if (await _libraryRepository.AddPlayHistoryAsync(
                        new LibraryRepository.PlayHistoryWriteInput(
                            historyUserId, folder.LibraryId, trackId, item.ItemId, null,
                            item.PlayedAtUtc, item.DurationMs, JsonSerializer.Serialize(item),
                            "navidrome", folder.Id),
                        cancellationToken))
                {
                    stats.Inserted++;
                }
            }
        }

        _logger.LogInformation(
            "Imported {Count} Navidrome history entries from {FetchedCount} fetched. resolvedByPath={ResolvedByPath} resolvedByMetadata={ResolvedByMetadata} unresolved={Unresolved}.",
            stats.Inserted,
            fetchedCount,
            stats.ResolvedByFilePath,
            stats.ResolvedByMetadata,
            stats.Unresolved);
        return stats.Inserted;
    }

    private async Task<long?> ResolveTrackIdAsync(
        NavidromeHistoryItem item,
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
                null, item.Title, item.Artist, item.DurationMs, "navidrome", item.ItemId),
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
