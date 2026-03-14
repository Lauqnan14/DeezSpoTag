using DeezSpoTag.Core.Models.Qobuz;
using DeezSpoTag.Integrations.Qobuz;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace DeezSpoTag.Services.Metadata.Qobuz;

public sealed class QobuzArtistService
{
    private readonly IQobuzApiClient _apiClient;
    private readonly IMemoryCache _cache;
    private readonly QobuzApiConfig _config;

    public QobuzArtistService(IQobuzApiClient apiClient, IMemoryCache cache, IOptions<QobuzApiConfig> options)
    {
        _apiClient = apiClient;
        _cache = cache;
        _config = options.Value;
    }

    public async Task<QobuzArtist?> GetArtistWithDiscographyAsync(int artistId, string store, CancellationToken ct)
    {
        var resolvedStore = QobuzStoreManager.NormalizeStore(store, _config.DefaultStore);
        var cacheKey = $"qobuz_artist_{resolvedStore}_{artistId}";
        if (_cache.TryGetValue<QobuzArtist>(cacheKey, out var cached))
        {
            return cached;
        }

        var artist = await FetchArtistDiscographyAsync(artistId, resolvedStore, ct);
        if (artist != null)
        {
            var cacheMinutes = _config.CacheDurationMinutes > 0 ? _config.CacheDurationMinutes : 60;
            _cache.Set(cacheKey, artist, TimeSpan.FromMinutes(cacheMinutes));
        }

        return artist;
    }

    private async Task<QobuzArtist?> FetchArtistDiscographyAsync(int artistId, string store, CancellationToken ct)
    {
        var allAlbums = new List<QobuzAlbum>();
        var offset = 0;
        var limit = _config.PageSize > 0 ? _config.PageSize : 500;
        QobuzArtist? baseArtist = null;

        while (true)
        {
            var response = await _apiClient.GetArtistAsync(artistId, store, offset, limit, ct);
            if (response == null)
            {
                return baseArtist;
            }

            baseArtist ??= response;
            var albums = response.Albums?.Items ?? new List<QobuzAlbum>();
            var primaryAlbums = albums
                .Where(album => album.Artists.Any(ar => ar.Id == artistId))
                .ToList();
            allAlbums.AddRange(primaryAlbums);

            if (albums.Count < limit)
            {
                break;
            }

            offset += limit;
        }

        baseArtist!.Albums = new QobuzAlbumCollection
        {
            Offset = offset,
            Limit = limit,
            Total = allAlbums.Count,
            Items = allAlbums
        };

        return baseArtist;
    }
}
