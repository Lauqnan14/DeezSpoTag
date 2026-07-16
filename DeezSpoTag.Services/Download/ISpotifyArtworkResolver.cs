namespace DeezSpoTag.Services.Download;

public interface ISpotifyArtworkResolver
{
    Task<string?> ResolveAlbumCoverUrlAsync(
        string? spotifyTrackId,
        CancellationToken cancellationToken,
        string? requestedAlbumTitle = null,
        bool rejectCompilationAlbumCandidate = false);
    Task<string?> ResolveArtistImageUrlAsync(string? spotifyTrackId, CancellationToken cancellationToken);
    Task<string?> ResolveArtistImageByArtistIdAsync(string? spotifyArtistId, CancellationToken cancellationToken);
    Task<string?> ResolveArtistImageByNameAsync(string? artistName, CancellationToken cancellationToken);
}
