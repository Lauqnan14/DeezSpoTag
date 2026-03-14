namespace DeezSpoTag.Services.Download;

public sealed class NullSpotifyIdResolver : ISpotifyIdResolver
{
    public Task<string?> ResolveTrackIdAsync(
        string title,
        string artist,
        string? album,
        string? isrc,
        CancellationToken cancellationToken)
    {
        return Task.FromResult<string?>(null);
    }
}
