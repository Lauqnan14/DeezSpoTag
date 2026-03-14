namespace DeezSpoTag.Services.Download;

public interface ISpotifyArtworkResolver
{
    Task<string?> ResolveAlbumCoverUrlAsync(string? spotifyTrackId, CancellationToken cancellationToken, string? requestedAlbumTitle = null);
    Task<string?> ResolveArtistImageUrlAsync(string? spotifyTrackId, CancellationToken cancellationToken);
    Task<string?> ResolveArtistImageByNameAsync(string? artistName, CancellationToken cancellationToken);
}
