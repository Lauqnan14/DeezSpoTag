using System.Collections.Concurrent;
using System.Linq;
using DeezSpoTag.Services.Download;

namespace DeezSpoTag.Web.Services;

public sealed class SpotifyArtworkResolver : ISpotifyArtworkResolver
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new();
    private readonly SpotifyMetadataService _metadataService;
    private readonly SpotifyPathfinderMetadataClient _pathfinderMetadataClient;
    private readonly ILogger<SpotifyArtworkResolver> _logger;

    public SpotifyArtworkResolver(
        SpotifyMetadataService metadataService,
        SpotifyPathfinderMetadataClient pathfinderMetadataClient,
        ILogger<SpotifyArtworkResolver> logger)
    {
        _metadataService = metadataService;
        _pathfinderMetadataClient = pathfinderMetadataClient;
        _logger = logger;
    }

    public async Task<string?> ResolveAlbumCoverUrlAsync(string? spotifyTrackId, CancellationToken cancellationToken, string? requestedAlbumTitle = null)
    {
        var artwork = await ResolveArtworkAsync(spotifyTrackId, requestedAlbumTitle, cancellationToken);
        return artwork?.AlbumCoverUrl;
    }

    public async Task<string?> ResolveArtistImageUrlAsync(string? spotifyTrackId, CancellationToken cancellationToken)
    {
        var artwork = await ResolveArtworkAsync(spotifyTrackId, null, cancellationToken);
        return artwork?.ArtistImageUrl;
    }

    public async Task<string?> ResolveArtistImageByNameAsync(string? artistName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artistName))
        {
            return null;
        }

        var candidates = await _pathfinderMetadataClient.SearchArtistsAsync(artistName, 1, cancellationToken);
        if (candidates.Count == 0)
        {
            return null;
        }

        var artist = await _pathfinderMetadataClient.FetchArtistOverviewAsync(candidates[0].Id, cancellationToken);
        var imageUrl = artist?.ImageUrl;
        if (!string.IsNullOrWhiteSpace(imageUrl) && _logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Spotify artist image resolved by name: {Artist}", artistName);
        }

        return imageUrl;
    }

    private async Task<SpotifyTrackArtwork?> ResolveArtworkAsync(string? spotifyTrackId, string? requestedAlbumTitle, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(spotifyTrackId))
        {
            return null;
        }

        var cacheKey = string.IsNullOrWhiteSpace(requestedAlbumTitle)
            ? spotifyTrackId
            : $"{spotifyTrackId}|{requestedAlbumTitle.Trim()}";

        if (Cache.TryGetValue(cacheKey, out var cached) &&
            DateTimeOffset.UtcNow - cached.Stamp < CacheTtl)
        {
            return cached.Artwork;
        }

        var artwork = await _metadataService.FetchTrackArtworkAsync(spotifyTrackId, cancellationToken, requestedAlbumTitle);
        if (artwork == null)
        {
            return null;
        }

        Cache[cacheKey] = new CacheEntry(DateTimeOffset.UtcNow, artwork);
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Spotify artwork resolved for track {SpotifyTrackId}", spotifyTrackId);
        }
        return artwork;
    }

    private sealed record CacheEntry(DateTimeOffset Stamp, SpotifyTrackArtwork Artwork);
}
