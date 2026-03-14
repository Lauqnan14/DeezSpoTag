using DeezSpoTag.Integrations.Plex;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed class PlexHistoryImportService
{
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
        var inserted = 0;

        foreach (var item in history)
        {
            if (item.ViewedAtUtc is null)
            {
                continue;
            }

            var trackId = await _libraryRepository.GetTrackIdForFilePathAsync(item.FilePath ?? string.Empty, cancellationToken);
            long? libraryId = null;
            if (!string.IsNullOrWhiteSpace(item.FilePath))
            {
                var folder = await _libraryRepository.ResolveFolderForPathAsync(item.FilePath, cancellationToken);
                libraryId = folder?.LibraryId;
            }

            await _libraryRepository.AddPlayHistoryAsync(
                new LibraryRepository.PlayHistoryWriteInput(
                    plexUserId,
                    libraryId,
                    trackId,
                    item.FilePath,
                    item.RatingKey,
                    item.ViewedAtUtc.Value,
                    item.DurationMs > 0 ? (int?)item.DurationMs : null,
                    null),
                cancellationToken);
            inserted++;
        }

        _logger.LogInformation("Imported {Count} Plex history entries.", inserted);
        return inserted;
    }
}
