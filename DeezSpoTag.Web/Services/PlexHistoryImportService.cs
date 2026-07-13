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

        var stats = new ImportStats();
        var ratingKeyUpserts = new List<PlexTrackMetadataUpsertDto>();
        var fetchedCount = 0;
        var folders = (await _libraryRepository.GetConfiguredEnabledMusicFoldersAsync(cancellationToken))
            .Where(static folder => !string.IsNullOrWhiteSpace(folder.PlexSectionId))
            .ToList();
        foreach (var folder in folders)
        {
            var latest = await _libraryRepository.GetLatestPlayHistoryUtcForFolderAsync(
                plexUserId, "plex", folder.Id, cancellationToken);
            var importFromUtc = latest?.Subtract(ImportOverlap);
            var history = await _plexApiClient.GetHistoryAsync(
                plex.Url, plex.Token, folder.PlexSectionId!, importFromUtc, cancellationToken);
            fetchedCount += history.Count;
            foreach (var item in history.Where(static item => item.ViewedAtUtc.HasValue))
            {
                var trackId = await ResolveTrackIdInFolderAsync(item, folder, stats, cancellationToken);
                if (!trackId.HasValue) stats.Unresolved++;
                if (trackId.HasValue && !string.IsNullOrWhiteSpace(item.RatingKey))
                {
                    ratingKeyUpserts.Add(new PlexTrackMetadataUpsertDto(trackId.Value, item.RatingKey.Trim(), DateTimeOffset.UtcNow));
                }
                if (await WritePlayHistoryAsync(
                        plexUserId, folder.LibraryId, folder.Id, trackId, item, cancellationToken))
                {
                    stats.Inserted++;
                }
            }
        }

        if (ratingKeyUpserts.Count > 0)
        {
            await _libraryRepository.UpsertPlexTrackMetadataAsync(ratingKeyUpserts, cancellationToken);
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Processed {Count} folder-scoped Plex history entries from {FetchedCount} fetched. resolvedByPath={ResolvedByPath} resolvedByMetadata={ResolvedByMetadata} unresolved={Unresolved}.",
                stats.Inserted,
                fetchedCount,
                stats.ResolvedByFilePath,
                stats.ResolvedByMetadata,
                stats.Unresolved);
        }
        return stats.Inserted;
    }

    private async Task<long?> ResolveTrackIdInFolderAsync(
        PlexHistoryItem item,
        FolderDto folder,
        ImportStats stats,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(item.FilePath))
        {
            var pathId = await _libraryRepository.GetTrackIdForFilePathAsync(item.FilePath, cancellationToken);
            if (pathId.HasValue && await _libraryRepository.GetFolderScopeForTrackAsync(
                    pathId.Value, folder.Id, folder.LibraryId, cancellationToken) is not null)
            {
                stats.ResolvedByFilePath++;
                return pathId;
            }
        }

        var resolved = await _libraryRepository.ResolveLocalTrackIdentityAsync(
            new LibraryRepository.LibraryExistenceInput(
                null, item.Title, item.Artist, item.DurationMs > 0 ? (int?)item.DurationMs : null,
                "plex", item.RatingKey, item.Album),
            folder.LibraryId,
            folder.Id,
            cancellationToken);
        if (resolved.LocalTrackId.HasValue) stats.ResolvedByMetadata++;
        return resolved.LocalTrackId;
    }

    private async Task<bool> WritePlayHistoryAsync(
        long plexUserId,
        long? libraryId,
        long folderId,
        long? trackId,
        PlexHistoryItem item,
        CancellationToken cancellationToken)
    {
        return await _libraryRepository.AddPlayHistoryAsync(
            new LibraryRepository.PlayHistoryWriteInput(
                plexUserId,
                libraryId,
                trackId,
                string.IsNullOrWhiteSpace(item.FilePath) ? item.RatingKey : item.FilePath,
                item.RatingKey,
                item.ViewedAtUtc!.Value,
                item.DurationMs > 0 ? (int?)item.DurationMs : null,
                null,
                "plex",
                folderId),
            cancellationToken);
    }

    private sealed class ImportStats
    {
        public int Inserted { get; set; }
        public int ResolvedByFilePath { get; set; }
        public int ResolvedByMetadata { get; set; }
        public int Unresolved { get; set; }
    }
}
