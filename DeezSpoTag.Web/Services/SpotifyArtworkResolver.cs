using System.Collections.Concurrent;
using System.Linq;
using DeezSpoTag.Services.Download;

namespace DeezSpoTag.Web.Services;

public sealed class SpotifyArtworkResolver : ISpotifyArtworkResolver
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);
    private const int CacheLimit = 4096;
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

    public async Task<string?> ResolveAlbumCoverUrlAsync(
        string? spotifyTrackId,
        CancellationToken cancellationToken,
        string? requestedAlbumTitle = null,
        bool rejectCompilationAlbumCandidate = false)
    {
        var artwork = await ResolveArtworkAsync(
            spotifyTrackId,
            requestedAlbumTitle,
            rejectCompilationAlbumCandidate,
            cancellationToken);
        return artwork?.AlbumCoverUrl;
    }

    public async Task<string?> ResolveArtistImageUrlAsync(string? spotifyTrackId, CancellationToken cancellationToken)
    {
        var artwork = await ResolveArtworkAsync(
            spotifyTrackId,
            requestedAlbumTitle: null,
            rejectCompilationAlbumCandidate: false,
            cancellationToken);
        return artwork?.ArtistImageUrl;
    }

    public async Task<string?> ResolveArtistImageByArtistIdAsync(string? spotifyArtistId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(spotifyArtistId))
        {
            return null;
        }

        var artist = await _pathfinderMetadataClient.FetchArtistOverviewAsync(spotifyArtistId, cancellationToken);
        return artist?.ImageUrl;
    }

    public async Task<string?> ResolveArtistImageByNameAsync(string? artistName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artistName))
        {
            return null;
        }

        var candidates = await _pathfinderMetadataClient.SearchArtistsAsync(artistName, 8, cancellationToken);
        var normalizedArtist = NormalizeArtistIdentity(artistName);
        var candidate = candidates.FirstOrDefault(item =>
            string.Equals(NormalizeArtistIdentity(item.Name), normalizedArtist, StringComparison.Ordinal));
        if (candidate == null)
        {
            return null;
        }

        var artist = await _pathfinderMetadataClient.FetchArtistOverviewAsync(candidate.Id, cancellationToken);
        if (artist == null
            || !string.Equals(NormalizeArtistIdentity(artist.Name), normalizedArtist, StringComparison.Ordinal))
        {
            return null;
        }
        var imageUrl = artist?.ImageUrl;
        if (!string.IsNullOrWhiteSpace(imageUrl) && _logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Spotify artist image resolved by name: {Artist}", artistName);
        }

        return imageUrl;
    }

    private static string NormalizeArtistIdentity(string value)
        => new(value
            .Normalize(System.Text.NormalizationForm.FormD)
            .Where(character => char.IsLetterOrDigit(character))
            .Select(char.ToLowerInvariant)
            .ToArray());

    private async Task<SpotifyTrackArtwork?> ResolveArtworkAsync(
        string? spotifyTrackId,
        string? requestedAlbumTitle,
        bool rejectCompilationAlbumCandidate,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(spotifyTrackId))
        {
            return null;
        }

        var strictSuffix = rejectCompilationAlbumCandidate ? "|strict-non-compilation" : string.Empty;
        var cacheKey = string.IsNullOrWhiteSpace(requestedAlbumTitle)
            ? $"{spotifyTrackId}{strictSuffix}"
            : $"{spotifyTrackId}|{requestedAlbumTitle.Trim()}{strictSuffix}";

        if (Cache.TryGetValue(cacheKey, out var cached) &&
            DateTimeOffset.UtcNow - cached.Stamp < CacheTtl)
        {
            return cached.Artwork;
        }

        var artwork = await _metadataService.FetchTrackArtworkAsync(
            spotifyTrackId,
            cancellationToken,
            requestedAlbumTitle,
            rejectCompilationAlbumCandidate);
        if (artwork == null)
        {
            return null;
        }

        Cache[cacheKey] = new CacheEntry(DateTimeOffset.UtcNow, artwork);
        TrimCache();
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Spotify artwork resolved for track {SpotifyTrackId}", spotifyTrackId);
        }
        return artwork;
    }

    private sealed record CacheEntry(DateTimeOffset Stamp, SpotifyTrackArtwork Artwork);

    private static void TrimCache()
    {
        var cutoff = DateTimeOffset.UtcNow - CacheTtl;
        foreach (var entry in Cache.Where(entry => entry.Value.Stamp < cutoff))
        {
            Cache.TryRemove(entry.Key, out _);
        }

        foreach (var key in Cache.Keys.Take(Math.Max(0, Cache.Count - CacheLimit)))
        {
            Cache.TryRemove(key, out _);
        }
    }
}
