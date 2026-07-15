using DeezSpoTag.Integrations.Plex;
using DeezSpoTag.Services.Library;
using System.Text.Json;

namespace DeezSpoTag.Web.Services;

public sealed class PlexHistoryImportService
{
    private static readonly TimeSpan ImportOverlap = TimeSpan.FromMinutes(1);
    private readonly PlexApiClient _plexApiClient;
    private readonly PlatformAuthService _authService;
    private readonly LibraryRepository _libraryRepository;
    private readonly MelodayRemoteLibraryCatalog _libraryCatalog;
    private readonly ILogger<PlexHistoryImportService> _logger;

    public PlexHistoryImportService(
        PlexApiClient plexApiClient,
        PlatformAuthService authService,
        LibraryRepository libraryRepository,
        MelodayRemoteLibraryCatalog libraryCatalog,
        ILogger<PlexHistoryImportService> logger)
    {
        _plexApiClient = plexApiClient;
        _authService = authService;
        _libraryRepository = libraryRepository;
        _libraryCatalog = libraryCatalog;
        _logger = logger;
    }

    public async Task<int> ImportAsync(CancellationToken cancellationToken = default)
        => (await ImportDetailedAsync(cancellationToken)).Imported;

    public async Task<MelodayHistoryImportResult> ImportDetailedAsync(CancellationToken cancellationToken = default)
    {
        var state = await _authService.LoadAsync();
        var plex = state.Plex;
        if (plex is null || string.IsNullOrWhiteSpace(plex.Url) || string.IsNullOrWhiteSpace(plex.Token))
        {
            _logger.LogWarning("Plex auth missing; skipping history import.");
            return MelodayHistoryImportResult.NotConfigured("plex");
        }

        var username = !string.IsNullOrWhiteSpace(plex.Username) ? plex.Username : plex.ServerName;
        var plexUserId = await _libraryRepository.EnsurePlexUserAsync(
            username,
            plex.Username,
            plex.Url,
            plex.MachineIdentifier,
            cancellationToken);

        var ratingKeyUpserts = new List<PlexTrackMetadataUpsertDto>();
        var fetchedCount = 0;
        var insertedCount = 0;
        var resolvedCount = 0;
        var ambiguousCount = 0;
        var unresolvedCount = 0;
        string? importError = null;
        var catalog = await _libraryCatalog.GetPlexAsync(plex, forceRefresh: true, cancellationToken);
        if (!catalog.Available)
        {
            return MelodayHistoryImportResult.Unavailable("plex", catalog.Error ?? "Plex library discovery failed.");
        }

        foreach (var library in catalog.Libraries)
        {
            var latest = await _libraryRepository.GetLatestPlayHistoryUtcForRemoteLibraryAsync(
                plexUserId, "plex", library.Id, cancellationToken);
            var importFromUtc = latest?.Subtract(ImportOverlap);
            List<PlexHistoryItem> history;
            try
            {
                history = await _plexApiClient.GetHistoryAsync(
                    plex.Url, plex.Token, library.Id, importFromUtc, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                importError = $"One or more Plex libraries could not be read: {ex.Message}";
                _logger.LogWarning(ex, "Plex history import failed for remote library {RemoteLibraryId}.", library.Id);
                continue;
            }
            fetchedCount += history.Count;
            foreach (var item in history.Where(static item => item.ViewedAtUtc.HasValue))
            {
                var resolution = await _libraryRepository.ResolveHistoryTrackScopeAsync(
                    item.FilePath,
                    new LibraryRepository.LibraryExistenceInput(
                        null,
                        item.Title,
                        item.Artist,
                        item.DurationMs > 0 ? (int?)item.DurationMs : null,
                        "plex",
                        item.RatingKey,
                        item.Album),
                    cancellationToken);
                if (resolution.Resolved)
                {
                    resolvedCount++;
                    if (!string.IsNullOrWhiteSpace(item.RatingKey))
                    {
                        ratingKeyUpserts.Add(new PlexTrackMetadataUpsertDto(
                            resolution.TrackId!.Value,
                            item.RatingKey.Trim(),
                            DateTimeOffset.UtcNow));
                    }
                }
                else if (resolution.Ambiguous)
                {
                    ambiguousCount++;
                }
                else
                {
                    unresolvedCount++;
                }

                if (await WritePlayHistoryAsync(plexUserId, library.Id, resolution, item, cancellationToken))
                {
                    insertedCount++;
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
                "Processed {Count} library-scoped Plex history entries from {FetchedCount} fetched. resolved={Resolved} ambiguous={Ambiguous} unresolved={Unresolved}.",
                insertedCount,
                fetchedCount,
                resolvedCount,
                ambiguousCount,
                unresolvedCount);
        }

        return new MelodayHistoryImportResult(
            "plex",
            true,
            true,
            catalog.Libraries.Count,
            fetchedCount,
            insertedCount,
            resolvedCount,
            ambiguousCount,
            unresolvedCount,
            importError);
    }

    private async Task<bool> WritePlayHistoryAsync(
        long plexUserId,
        string remoteLibraryId,
        HistoryTrackScopeResolution resolution,
        PlexHistoryItem item,
        CancellationToken cancellationToken)
    {
        return await _libraryRepository.AddPlayHistoryAsync(
            new LibraryRepository.PlayHistoryWriteInput(
                plexUserId,
                resolution.LibraryId,
                resolution.TrackId,
                string.IsNullOrWhiteSpace(item.FilePath) ? item.RatingKey : item.FilePath,
                item.RatingKey,
                item.ViewedAtUtc!.Value,
                item.DurationMs > 0 ? (int?)item.DurationMs : null,
                JsonSerializer.Serialize(item),
                "plex",
                resolution.FolderId,
                remoteLibraryId),
            cancellationToken);
    }
}
