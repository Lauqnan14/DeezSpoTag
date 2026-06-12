namespace DeezSpoTag.Services.Download;

public interface ILastFmArtistImageResolver
{
    Task<string?> ResolveArtistImageByNameAsync(string? artistName, CancellationToken cancellationToken);
}
