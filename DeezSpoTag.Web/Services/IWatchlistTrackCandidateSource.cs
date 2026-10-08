namespace DeezSpoTag.Web.Services;

/// <summary>
/// The candidate lookup the Library Playlists sync needs. Declared here so a controller does
/// not have to take a dependency on the whole <see cref="WatchlistEngine"/>, which is
/// internal. <see cref="WatchlistEngine"/> implements it.
/// </summary>
public interface IWatchlistTrackCandidateSource
{
    /// <summary>
    /// Resolves a playlist's track candidates. Playlists the app is not monitoring are fetched
    /// live, so this works for the unmonitored playlists the Library Playlists tab lists.
    /// </summary>
    Task<IReadOnlyList<PlaylistTrackCandidate>> GetPlaylistTrackCandidatesAsync(
        string source,
        string sourceId,
        CancellationToken cancellationToken = default);
}
