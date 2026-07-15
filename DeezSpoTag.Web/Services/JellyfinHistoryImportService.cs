using System.Text.Json;
using DeezSpoTag.Integrations.Jellyfin;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed class JellyfinHistoryImportService
{
    private readonly JellyfinApiClient _jellyfinApiClient;
    private readonly PlatformAuthService _authService;
    private readonly LibraryRepository _libraryRepository;
    private readonly MelodayRemoteLibraryCatalog _libraryCatalog;
    private readonly ILogger<JellyfinHistoryImportService> _logger;

    public JellyfinHistoryImportService(
        JellyfinApiClient jellyfinApiClient,
        PlatformAuthService authService,
        LibraryRepository libraryRepository,
        MelodayRemoteLibraryCatalog libraryCatalog,
        ILogger<JellyfinHistoryImportService> logger)
    {
        _jellyfinApiClient = jellyfinApiClient;
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
        var jellyfin = state.Jellyfin;
        if (jellyfin is null
            || string.IsNullOrWhiteSpace(jellyfin.Url)
            || string.IsNullOrWhiteSpace(jellyfin.ApiKey)
            || string.IsNullOrWhiteSpace(jellyfin.UserId))
        {
            _logger.LogWarning("Jellyfin auth missing; skipping history import.");
            return MelodayHistoryImportResult.NotConfigured("jellyfin");
        }

        var username = !string.IsNullOrWhiteSpace(jellyfin.Username) ? jellyfin.Username : jellyfin.ServerName;
        var userId = await _libraryRepository.EnsurePlexUserAsync(
            username,
            $"jellyfin:{jellyfin.UserId}",
            jellyfin.Url,
            jellyfin.ServerName,
            cancellationToken);

        var insertedCount = 0;
        var fetchedCount = 0;
        var resolvedCount = 0;
        var ambiguousCount = 0;
        var unresolvedCount = 0;
        string? importError = null;
        var catalog = await _libraryCatalog.GetJellyfinAsync(jellyfin, forceRefresh: true, cancellationToken);
        if (!catalog.Available)
        {
            return MelodayHistoryImportResult.Unavailable(
                "jellyfin",
                catalog.Error ?? "Jellyfin library discovery failed.");
        }

        foreach (var library in catalog.Libraries)
        {
            List<JellyfinHistoryItem> history;
            try
            {
                history = await _jellyfinApiClient.GetAudioPlayHistoryAsync(
                    jellyfin.Url, jellyfin.ApiKey, jellyfin.UserId, library.Id,
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                importError = $"One or more Jellyfin libraries could not be read: {ex.Message}";
                _logger.LogWarning(ex, "Jellyfin history import failed for remote library {RemoteLibraryId}.", library.Id);
                continue;
            }
            fetchedCount += history.Count;
            foreach (var item in history)
            {
                var resolution = await _libraryRepository.ResolveHistoryTrackScopeAsync(
                    item.FilePath,
                    new LibraryRepository.LibraryExistenceInput(
                        null, item.Title, item.Artist, item.DurationMs,
                        "jellyfin", item.ItemId, item.Album),
                    cancellationToken);
                if (resolution.Resolved) resolvedCount++;
                else if (resolution.Ambiguous) ambiguousCount++;
                else unresolvedCount++;

                if (await _libraryRepository.AddPlayHistoryAsync(
                        new LibraryRepository.PlayHistoryWriteInput(
                            userId, resolution.LibraryId, resolution.TrackId, item.FilePath, item.ItemId,
                            item.PlayedAtUtc, item.DurationMs, JsonSerializer.Serialize(item),
                            "jellyfin", resolution.FolderId, library.Id),
                        cancellationToken))
                {
                    insertedCount++;
                }
            }
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Imported {Count} library-scoped Jellyfin history entries from {FetchedCount} fetched. resolved={Resolved} ambiguous={Ambiguous} unresolved={Unresolved}.",
                insertedCount,
                fetchedCount,
                resolvedCount,
                ambiguousCount,
                unresolvedCount);
        }

        return new MelodayHistoryImportResult(
            "jellyfin",
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
}
