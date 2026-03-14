namespace DeezSpoTag.Services.Download;

public sealed class NullSpotifyArtworkResolver : ISpotifyArtworkResolver
{
    public Task<string?> ResolveAlbumCoverUrlAsync(string? spotifyTrackId, CancellationToken cancellationToken, string? requestedAlbumTitle = null)
        => Task.FromResult<string?>(null);

    public Task<string?> ResolveArtistImageUrlAsync(string? spotifyTrackId, CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);

    public Task<string?> ResolveArtistImageByNameAsync(string? artistName, CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);
}
