using DeezSpoTag.Integrations.Plex;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed class MixSyncService
{
    private readonly PlexApiClient _plexApiClient;
    private readonly PlatformAuthService _authService;
    private readonly LibraryRepository _libraryRepository;
    private readonly ILogger<MixSyncService> _logger;

    public MixSyncService(
        PlexApiClient plexApiClient,
        PlatformAuthService authService,
        LibraryRepository libraryRepository,
        ILogger<MixSyncService> logger)
    {
        _plexApiClient = plexApiClient;
        _authService = authService;
        _libraryRepository = libraryRepository;
        _logger = logger;
    }

    public async Task SyncMixAsync(
        MixSummaryDto summary,
        long plexUserId,
        CancellationToken cancellationToken = default)
    {
        var state = await _authService.LoadAsync();
        var plex = state.Plex;
        if (plex is null || string.IsNullOrWhiteSpace(plex.Url) || string.IsNullOrWhiteSpace(plex.Token))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(plex.MachineIdentifier))
        {
            _logger.LogWarning("Plex machine identifier missing; skipping mix sync.");
            return;
        }

        var mixCacheId = await _libraryRepository.GetMixCacheIdAsync(summary.Id, plexUserId, summary.LibraryId, cancellationToken);
        if (mixCacheId is null)
        {
            return;
        }

        var tracks = await _libraryRepository.GetMixTracksAsync(mixCacheId.Value, cancellationToken);
        var trackIds = tracks.Select(track => track.TrackId).ToList();
        var ratingKeys = await _libraryRepository.GetPlexRatingKeysAsync(trackIds, cancellationToken);
        if (ratingKeys.Count == 0)
        {
            _logger.LogWarning("No Plex rating keys found for mix {MixId}.", summary.Id);
            return;
        }

        await _plexApiClient.CreateOrUpdatePlaylistAsync(
            plex.Url,
            plex.Token,
            plex.MachineIdentifier,
            summary.Name,
            ratingKeys,
            cancellationToken: cancellationToken);
    }
}
