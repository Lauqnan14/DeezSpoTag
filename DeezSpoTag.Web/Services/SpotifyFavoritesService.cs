using DeezSpoTag.Web.Services.Favorites;

namespace DeezSpoTag.Web.Services;

/// <summary>
/// Spotify saved content for the Favorites tab.
/// <para>
/// This reads the account library, not the home feed. The home feed carries no "Liked Songs" or
/// "Your playlists" section, so deriving favorites from it always produced an empty shelf while
/// still reporting the account as connected - which reads as "you have no favorites" rather than
/// "the read is broken".
/// </para>
/// </summary>
public sealed class SpotifyFavoritesService : IFavoritesProvider
{
    private const string PlaylistType = "playlist";
    private const string TrackType = "track";

    private readonly PlatformAuthService _platformAuthService;
    private readonly SpotifyBlobService _blobService;
    private readonly SpotifyUserAuthStore _userAuthStore;
    private readonly ISpotifyUserContextAccessor _userContext;
    private readonly SpotifyPathfinderMetadataClient _pathfinderMetadataClient;
    private readonly ILogger<SpotifyFavoritesService> _logger;

    public SpotifyFavoritesService(
        PlatformAuthService platformAuthService,
        SpotifyBlobService blobService,
        SpotifyUserAuthStore userAuthStore,
        ISpotifyUserContextAccessor userContext,
        SpotifyPathfinderMetadataClient pathfinderMetadataClient,
        ILogger<SpotifyFavoritesService> logger)
    {
        _platformAuthService = platformAuthService;
        _blobService = blobService;
        _userAuthStore = userAuthStore;
        _userContext = userContext;
        _pathfinderMetadataClient = pathfinderMetadataClient;
        _logger = logger;
    }

    public string Key => "spotify";

    public string DisplayName => "Spotify";

    public string IconPath => "/images/icons/spotify.png";

    public async Task<FavoritesResult> GetFavoritesAsync(int limit, CancellationToken cancellationToken)
    {
        var webPlayerBlobPath = await ResolveActiveWebPlayerBlobPathAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(webPlayerBlobPath))
        {
            return Unavailable("Spotify account not linked.");
        }

        try
        {
            var resolvedLimit = Math.Clamp(limit, 1, 100);
            var (playlists, tracks) = await LoadPersonalLibraryAsync(resolvedLimit, cancellationToken);

            // An empty read is not a connected account with no favorites. Reporting it as available
            // would hide a broken query behind a plausible empty shelf.
            if (playlists.Count == 0 && tracks.Count == 0)
            {
                return Unavailable("Spotify library unavailable.");
            }

            return new FavoritesResult(true, null, new List<FavoriteItem>(), playlists, tracks);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load Spotify favorites from the personal library.");
            return Unavailable("Spotify favorites unavailable.");
        }
    }

    private async Task<(List<FavoriteItem> Playlists, List<FavoriteItem> Tracks)> LoadPersonalLibraryAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        var tracks = new List<FavoriteItem>();
        var seenTrackIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var summary in await _pathfinderMetadataClient.FetchLibraryLikedTracksAsync(limit, 0, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(summary.Id) || !seenTrackIds.Add(summary.Id))
            {
                continue;
            }

            tracks.Add(new FavoriteItem(
                summary.Id,
                summary.Name,
                TrackType,
                $"https://open.spotify.com/track/{summary.Id}",
                summary.ImageUrl,
                summary.Artists,
                summary.DurationMs));
        }

        var playlists = new List<FavoriteItem>();
        var seenPlaylistIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var playlist in await _pathfinderMetadataClient.FetchLibraryPlaylistsAsync(limit, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(playlist.Id) || !seenPlaylistIds.Add(playlist.Id))
            {
                continue;
            }

            playlists.Add(new FavoriteItem(
                playlist.Id,
                playlist.Name,
                PlaylistType,
                $"https://open.spotify.com/playlist/{playlist.Id}",
                playlist.CoverUrl,
                string.IsNullOrWhiteSpace(playlist.Subtitle) ? "Spotify" : playlist.Subtitle,
                null));
        }

        return (playlists, tracks);
    }

    private async Task<string?> ResolveActiveWebPlayerBlobPathAsync(CancellationToken cancellationToken)
    {
        try
        {
            var userState = await TryLoadUserSpotifyStateAsync();
            if (userState != null)
            {
                var userBlobPath = SpotifyUserAuthStore.ResolveActiveWebPlayerBlobPath(userState);
                if (!string.IsNullOrWhiteSpace(userBlobPath)
                    && _blobService.BlobExists(userBlobPath)
                    && await _blobService.IsWebPlayerBlobAsync(userBlobPath, cancellationToken))
                {
                    return userBlobPath;
                }
            }

            var platformBlobPath = await ResolvePlatformWebPlayerBlobPathAsync();
            if (string.IsNullOrWhiteSpace(platformBlobPath) || !_blobService.BlobExists(platformBlobPath))
            {
                return null;
            }

            return await _blobService.IsWebPlayerBlobAsync(platformBlobPath, cancellationToken)
                ? platformBlobPath
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to resolve Spotify web-player blob path for favorites.");
            return null;
        }
    }

    private async Task<string?> ResolvePlatformWebPlayerBlobPathAsync()
    {
        var state = await _platformAuthService.LoadAsync();
        var spotifyState = state.Spotify;
        if (spotifyState is null || string.IsNullOrWhiteSpace(spotifyState.ActiveAccount))
        {
            return null;
        }

        return spotifyState.Accounts
            .FirstOrDefault(a => a.Name.Equals(spotifyState.ActiveAccount, StringComparison.OrdinalIgnoreCase))
            ?.WebPlayerBlobPath;
    }

    private async Task<SpotifyUserAuthState?> TryLoadUserSpotifyStateAsync()
    {
        var userId = _userContext.UserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        return await _userAuthStore.LoadAsync(userId);
    }

    private static FavoritesResult Unavailable(string message)
        => new(false, message, new List<FavoriteItem>(), new List<FavoriteItem>(), new List<FavoriteItem>());
}
