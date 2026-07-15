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
    private readonly MelodayRemoteLibraryCatalog _libraryCatalog;
    private readonly ILogger<NavidromeHistoryImportService> _logger;

    public NavidromeHistoryImportService(
        NavidromeApiClient navidromeApiClient,
        PlatformAuthService authService,
        LibraryRepository libraryRepository,
        MelodayRemoteLibraryCatalog libraryCatalog,
        ILogger<NavidromeHistoryImportService> logger)
    {
        _navidromeApiClient = navidromeApiClient;
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
        var navidrome = state.Navidrome;
        if (navidrome is null
            || string.IsNullOrWhiteSpace(navidrome.Url)
            || string.IsNullOrWhiteSpace(navidrome.Username)
            || string.IsNullOrWhiteSpace(navidrome.Password))
        {
            _logger.LogWarning("Navidrome auth missing; skipping history import.");
            return MelodayHistoryImportResult.NotConfigured("navidrome");
        }

        var username = navidrome.Username.Trim();
        var historyUserId = await _libraryRepository.EnsurePlexUserAsync(
            username,
            $"navidrome:{username}",
            navidrome.Url,
            navidrome.ServerName,
            cancellationToken);
        var insertedCount = 0;
        var fetchedCount = 0;
        var resolvedCount = 0;
        var ambiguousCount = 0;
        var unresolvedCount = 0;
        string? importError = null;
        var catalog = await _libraryCatalog.GetNavidromeAsync(navidrome, forceRefresh: true, cancellationToken);
        if (!catalog.Available)
        {
            return MelodayHistoryImportResult.Unavailable(
                "navidrome",
                catalog.Error ?? "Navidrome library discovery failed.");
        }

        foreach (var library in catalog.Libraries)
        {
            var latest = await _libraryRepository.GetLatestPlayHistoryUtcForRemoteLibraryAsync(
                historyUserId, "navidrome", library.Id, cancellationToken);
            List<NavidromeHistoryItem> history;
            try
            {
                history = await _navidromeApiClient.GetPlayHistoryAsync(
                    navidrome.Url, username, navidrome.Password, library.Id,
                    latest?.Subtract(ImportOverlap), cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                importError = $"One or more Navidrome libraries could not be read: {ex.Message}";
                _logger.LogWarning(ex, "Navidrome history import failed for remote library {RemoteLibraryId}.", library.Id);
                continue;
            }
            fetchedCount += history.Count;
            foreach (var item in history)
            {
                var resolution = await _libraryRepository.ResolveHistoryTrackScopeAsync(
                    item.FilePath,
                    new LibraryRepository.LibraryExistenceInput(
                        null, item.Title, item.Artist, item.DurationMs,
                        "navidrome", item.ItemId),
                    cancellationToken);
                if (resolution.Resolved) resolvedCount++;
                else if (resolution.Ambiguous) ambiguousCount++;
                else unresolvedCount++;

                if (await _libraryRepository.AddPlayHistoryAsync(
                        new LibraryRepository.PlayHistoryWriteInput(
                            historyUserId, resolution.LibraryId, resolution.TrackId,
                            string.IsNullOrWhiteSpace(item.FilePath) ? item.ItemId : item.FilePath,
                            item.ItemId,
                            item.PlayedAtUtc, item.DurationMs, JsonSerializer.Serialize(item),
                            "navidrome", resolution.FolderId, library.Id),
                        cancellationToken))
                {
                    insertedCount++;
                }
            }
        }

        _logger.LogInformation(
            "Imported {Count} library-scoped Navidrome history entries from {FetchedCount} fetched. resolved={Resolved} ambiguous={Ambiguous} unresolved={Unresolved}.",
            insertedCount,
            fetchedCount,
            resolvedCount,
            ambiguousCount,
            unresolvedCount);
        return new MelodayHistoryImportResult(
            "navidrome",
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
