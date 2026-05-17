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
        var knownRatingKeys = history
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
        var inserted = 0;
        var resolvedByFilePath = 0;
        var resolvedByRatingKey = 0;
        var resolvedByMetadata = 0;
        var unresolved = 0;
        var metadataLookupCache = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
        var ratingKeyUpserts = new List<PlexTrackMetadataUpsertDto>();

        foreach (var item in history)
        {
            if (item.ViewedAtUtc is null)
            {
                continue;
            }

            long? trackId = null;

            if (!string.IsNullOrWhiteSpace(item.FilePath))
            {
                trackId = await _libraryRepository.GetTrackIdForFilePathAsync(item.FilePath, cancellationToken);
                if (trackId.HasValue)
                {
                    resolvedByFilePath++;
                }
            }

            if (!trackId.HasValue
                && !string.IsNullOrWhiteSpace(item.RatingKey)
                && trackIdsByRatingKey.TryGetValue(item.RatingKey, out var mappedTrackId))
            {
                trackId = mappedTrackId;
                resolvedByRatingKey++;
            }

            if (!trackId.HasValue)
            {
                trackId = await TryResolveTrackIdByMetadataAsync(item, metadataLookupCache, cancellationToken);
                if (trackId.HasValue)
                {
                    resolvedByMetadata++;
                }
            }

            long? libraryId = null;
            if (!string.IsNullOrWhiteSpace(item.FilePath))
            {
                var folder = await _libraryRepository.ResolveFolderForPathAsync(item.FilePath, cancellationToken);
                libraryId = folder?.LibraryId;
            }
            if (!trackId.HasValue)
            {
                unresolved++;
            }

            if (trackId.HasValue && !string.IsNullOrWhiteSpace(item.RatingKey))
            {
                var normalizedKey = item.RatingKey.Trim();
                trackIdsByRatingKey[normalizedKey] = trackId.Value;
                ratingKeyUpserts.Add(new PlexTrackMetadataUpsertDto(
                    trackId.Value,
                    normalizedKey,
                    DateTimeOffset.UtcNow));
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

        if (ratingKeyUpserts.Count > 0)
        {
            await _libraryRepository.UpsertPlexTrackMetadataAsync(ratingKeyUpserts, cancellationToken);
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Imported {Count} Plex history entries. resolvedByPath={ResolvedByPath} resolvedByRatingKey={ResolvedByRatingKey} resolvedByMetadata={ResolvedByMetadata} unresolved={Unresolved}.",
                inserted,
                resolvedByFilePath,
                resolvedByRatingKey,
                resolvedByMetadata,
                unresolved);
        }
        return inserted;
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
}
