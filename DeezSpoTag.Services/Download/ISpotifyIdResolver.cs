namespace DeezSpoTag.Services.Download;

public interface ISpotifyIdResolver
{
    Task<string?> ResolveTrackIdAsync(
        string title,
        string artist,
        string? album,
        string? isrc,
        CancellationToken cancellationToken);
}
