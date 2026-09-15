using System.Net.Http.Headers;
using System.Text.Json;
using DeezSpoTag.Integrations.Tidal;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;

namespace DeezSpoTag.Web.Services;

public sealed class ArtistMediaExtrasCacheService
{
    public const string CacheSource = "artist-media";
    private const string AppleProvider = "apple";
    private const string TidalProvider = "tidal";
    private const string DefaultStorefront = "us";
    private const string DefaultLanguage = "en-US";
    private readonly ArtistPageCacheRepository _cache;
    private readonly LibraryRepository _repository;
    private readonly AppleMusicCatalogService _appleCatalog;
    private readonly AppleArtistBiographyService _appleBiography;
    private readonly ITidalAccessTokenProvider _tidalTokens;
    private readonly IHttpClientFactory _httpClients;
    private readonly DeezSpoTagSettingsService _settings;
    private readonly ILogger<ArtistMediaExtrasCacheService> _logger;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public ArtistMediaExtrasCacheService(
        ArtistPageCacheRepository cache,
        LibraryRepository repository,
        AppleMusicCatalogService appleCatalog,
        AppleArtistBiographyService appleBiography,
        ITidalAccessTokenProvider tidalTokens,
        IHttpClientFactory httpClients,
        DeezSpoTagSettingsService settings,
        ILogger<ArtistMediaExtrasCacheService> logger)
    {
        _cache = cache;
        _repository = repository;
        _appleCatalog = appleCatalog;
        _appleBiography = appleBiography;
        _tidalTokens = tidalTokens;
        _httpClients = httpClients;
        _settings = settings;
        _logger = logger;
    }

    public async Task<ArtistMediaExtrasPayload?> TryGetAsync(
        long artistId,
        string provider,
        CancellationToken cancellationToken)
    {
        if (artistId <= 0)
        {
            return null;
        }

        var cached = await _cache.TryGetAsync(CacheSource, BuildCacheId(artistId, provider), cancellationToken);
        if (cached is null || string.IsNullOrWhiteSpace(cached.PayloadJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ArtistMediaExtrasPayload>(cached.PayloadJson, _json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task RefreshAppleAsync(long artistId, string artistName, CancellationToken cancellationToken)
    {
        if (artistId <= 0 || string.IsNullOrWhiteSpace(artistName) || AppleCatalogJsonHelper.IsAppleDisabledByEnvironment())
        {
            return;
        }

        var appleId = await _repository.GetArtistSourceIdAsync(artistId, AppleProvider, cancellationToken);
        if (string.IsNullOrWhiteSpace(appleId))
        {
            var tracks = await _repository.GetArtistTrackTitlesAsync(artistId, 8, cancellationToken);
            var resolved = await _appleBiography.ResolveByExactArtistNameAndTracksAsync(artistName, tracks, cancellationToken);
            appleId = resolved?.AppleId;
            if (!string.IsNullOrWhiteSpace(appleId))
            {
                await _repository.UpsertArtistSourceIdAsync(artistId, AppleProvider, appleId, cancellationToken);
            }
        }

        if (string.IsNullOrWhiteSpace(appleId))
        {
            await PersistAsync(
                artistId,
                AppleProvider,
                new ArtistMediaExtrasPayload(null, null, Array.Empty<JsonElement>(), Array.Empty<JsonElement>(), false),
                cancellationToken);
            return;
        }

        var storefront = GetStorefront();
        var atmos = await LoadAppleAtmosAsync(appleId, storefront, cancellationToken);
        var (videos, hasMore) = await LoadAppleVideosAsync(appleId, storefront, cancellationToken);
        await PersistAsync(
            artistId,
            AppleProvider,
            new ArtistMediaExtrasPayload(appleId, null, atmos, videos, hasMore),
            cancellationToken);
    }

    public async Task RefreshTidalAsync(long artistId, string artistName, CancellationToken cancellationToken)
    {
        if (artistId <= 0 || string.IsNullOrWhiteSpace(artistName))
        {
            return;
        }

        try
        {
            var tidalId = await _repository.GetArtistSourceIdAsync(artistId, TidalProvider, cancellationToken);
            var token = await _tidalTokens.GetAccessTokenAsync(cancellationToken);
            var country = await _tidalTokens.GetCountryCodeAsync(cancellationToken) ?? "US";
            var tracks = await SearchTidalAsync("tracks", artistName, 50, token, country, cancellationToken);
            var albums = await SearchTidalAsync("albums", artistName, 50, token, country, cancellationToken);
            var videos = await SearchTidalAsync("videos", artistName, 50, token, country, cancellationToken);

            var atmosTracks = MapTidalAtmos(tracks, "track", tidalId, artistName);
            var atmosAlbums = MapTidalAtmos(albums, "album", tidalId, artistName);
            var mappedVideos = MapTidalVideos(videos, tidalId, artistName);
            await PersistAsync(
                artistId,
                TidalProvider,
                new ArtistMediaExtrasPayload(
                    null,
                    tidalId,
                    atmosTracks.Concat(atmosAlbums).ToList(),
                    mappedVideos,
                    false),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Tidal extras cache failed for artist {ArtistId}.", artistId);
        }
    }

    private async Task PersistAsync(
        long artistId,
        string provider,
        ArtistMediaExtrasPayload payload,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, _json);
        await _cache.UpsertAsync(CacheSource, BuildCacheId(artistId, provider), json, DateTimeOffset.UtcNow, cancellationToken);
    }

    private async Task<IReadOnlyList<JsonElement>> LoadAppleAtmosAsync(
        string appleId,
        string storefront,
        CancellationToken cancellationToken)
    {
        try
        {
            using var doc = await _appleCatalog.GetArtistAlbumsAsync(appleId, storefront, DefaultLanguage, 100, 0, cancellationToken);
            if (!AppleCatalogJsonHelper.TryGetDataArray(doc.RootElement, out var data))
            {
                return Array.Empty<JsonElement>();
            }

            var items = new List<JsonElement>();
            foreach (var item in data.EnumerateArray())
            {
                var attrs = item.TryGetProperty("attributes", out var attributes) ? attributes : default;
                if (!AppleCatalogJsonHelper.HasAtmos(attrs))
                {
                    continue;
                }

                items.Add(JsonSerializer.SerializeToElement(new
                {
                    source = AppleProvider,
                    appleId = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "",
                    appleUrl = attrs.TryGetProperty("url", out var urlEl) ? urlEl.GetString() ?? "" : "",
                    name = attrs.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "",
                    artist = attrs.TryGetProperty("artistName", out var artistEl) ? artistEl.GetString() ?? "" : "",
                    image = AppleCatalogJsonHelper.ResolveArtwork(attrs),
                    hasAtmos = true,
                    __kind = "album"
                }, _json));
            }

            return items;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Apple Atmos extras cache failed for {AppleId}.", appleId);
            return Array.Empty<JsonElement>();
        }
    }

    private async Task<(IReadOnlyList<JsonElement> Videos, bool HasMore)> LoadAppleVideosAsync(
        string appleId,
        string storefront,
        CancellationToken cancellationToken)
    {
        try
        {
            using var doc = await _appleCatalog.GetArtistMusicVideosAsync(appleId, storefront, DefaultLanguage, 50, 0, cancellationToken);
            if (!AppleCatalogJsonHelper.TryGetDataArray(doc.RootElement, out var data))
            {
                return (Array.Empty<JsonElement>(), false);
            }

            var videos = new List<JsonElement>();
            foreach (var item in data.EnumerateArray())
            {
                var attrs = item.TryGetProperty("attributes", out var attributes) ? attributes : default;
                var audioTraits = AppleCatalogJsonHelper.ReadStringArray(attrs, "audioTraits");
                var hasAtmos = audioTraits.Any(trait => trait.Contains("atmos", StringComparison.OrdinalIgnoreCase));
                videos.Add(JsonSerializer.SerializeToElement(new
                {
                    source = AppleProvider,
                    appleId = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "",
                    appleUrl = attrs.TryGetProperty("url", out var urlEl) ? urlEl.GetString() ?? "" : "",
                    name = attrs.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "",
                    artist = attrs.TryGetProperty("artistName", out var artistEl) ? artistEl.GetString() ?? "" : "",
                    image = AppleCatalogJsonHelper.ResolveArtwork(attrs),
                    type = "music-videos",
                    isVideo = true,
                    previewUrl = AppleCatalogJsonHelper.ReadPreviewUrl(attrs),
                    durationMs = attrs.TryGetProperty("durationInMillis", out var durationEl) && durationEl.TryGetInt64(out var duration) ? duration : 0,
                    releaseDate = attrs.TryGetProperty("releaseDate", out var releaseEl) ? releaseEl.GetString() ?? "" : "",
                    audioTraits,
                    hasAtmos,
                    hasAtmosCatalog = hasAtmos
                }, _json));
            }

            return (videos, AppleCatalogJsonHelper.RootHasNext(doc.RootElement));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Apple video extras cache failed for {AppleId}.", appleId);
            return (Array.Empty<JsonElement>(), false);
        }
    }

    private string GetStorefront()
    {
        var settings = _settings.LoadSettings();
        return string.IsNullOrWhiteSpace(settings.AppleMusic?.Storefront)
            ? DefaultStorefront
            : settings.AppleMusic!.Storefront;
    }

    private static string BuildCacheId(long artistId, string provider)
        => $"{artistId}:{provider.Trim().ToLowerInvariant()}";

    private async Task<List<JsonElement>> SearchTidalAsync(
        string endpointType,
        string query,
        int limit,
        string token,
        string country,
        CancellationToken cancellationToken)
    {
        var currentToken = token;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var url =
                $"https://api.tidal.com/v1/search/{endpointType}?query={Uri.EscapeDataString(query)}&limit={limit}&offset=0&countryCode={Uri.EscapeDataString(country)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", currentToken);
            using var response = await _httpClients.CreateClient().SendAsync(request, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && attempt == 0)
            {
                _tidalTokens.Invalidate();
                currentToken = await _tidalTokens.GetAccessTokenAsync(cancellationToken);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return ExtractTidalItems(doc.RootElement, endpointType);
        }

        return [];
    }

    private static List<JsonElement> ExtractTidalItems(JsonElement root, string endpointType)
    {
        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            return CloneItems(items);
        }

        if (root.TryGetProperty(endpointType, out var typed)
            && typed.TryGetProperty("items", out var typedItems)
            && typedItems.ValueKind == JsonValueKind.Array)
        {
            return CloneItems(typedItems);
        }

        return [];
    }

    private static List<JsonElement> CloneItems(JsonElement items)
        => items.EnumerateArray().Select(item => item.Clone()).ToList();

    private List<JsonElement> MapTidalAtmos(
        IEnumerable<JsonElement> items,
        string kind,
        string? tidalArtistId,
        string artistName)
    {
        var mapped = new List<JsonElement>();
        foreach (var raw in items)
        {
            var item = UnwrapTidalItem(raw);
            if (!TidalHasAtmos(item) || !MatchesTidalArtist(item, tidalArtistId, artistName))
            {
                continue;
            }

            mapped.Add(JsonSerializer.SerializeToElement(new
            {
                source = TidalProvider,
                __kind = kind,
                name = ReadTidalString(item, "title"),
                artist = ReadTidalArtistName(item),
                artistId = ReadTidalArtistId(item),
                image = BuildTidalImage(item),
                tidalId = ReadTidalId(item),
                hasAtmos = true
            }, _json));
        }

        return mapped;
    }

    private List<JsonElement> MapTidalVideos(
        IEnumerable<JsonElement> items,
        string? tidalArtistId,
        string artistName)
    {
        var mapped = new List<JsonElement>();
        foreach (var raw in items)
        {
            var item = UnwrapTidalItem(raw);
            if (!MatchesTidalArtist(item, tidalArtistId, artistName))
            {
                continue;
            }

            mapped.Add(JsonSerializer.SerializeToElement(new
            {
                source = TidalProvider,
                type = "video",
                isVideo = true,
                name = ReadTidalString(item, "title"),
                artist = ReadTidalArtistName(item),
                artistId = ReadTidalArtistId(item),
                image = BuildTidalImage(item),
                tidalId = ReadTidalId(item),
                hasAtmos = TidalHasAtmos(item)
            }, _json));
        }

        return mapped;
    }

    private static JsonElement UnwrapTidalItem(JsonElement element)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty("item", out var inner)
           && inner.ValueKind == JsonValueKind.Object
            ? inner
            : element;

    private static bool MatchesTidalArtist(JsonElement item, string? tidalArtistId, string artistName)
    {
        if (!string.IsNullOrWhiteSpace(tidalArtistId))
        {
            return ReadTidalArtistIds(item).Contains(tidalArtistId, StringComparer.OrdinalIgnoreCase);
        }

        var name = ReadTidalArtistName(item);
        return name.Equals(artistName, StringComparison.OrdinalIgnoreCase)
            || name.Contains(artistName, StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> ReadTidalArtistIds(JsonElement item)
    {
        var ids = new List<string>();
        var primary = ReadTidalArtistId(item);
        if (!string.IsNullOrWhiteSpace(primary))
        {
            ids.Add(primary);
        }

        if (item.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
        {
            foreach (var artist in artists.EnumerateArray())
            {
                var id = ReadTidalId(artist);
                if (!string.IsNullOrWhiteSpace(id))
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }

    private static string ReadTidalArtistId(JsonElement item)
    {
        if (item.TryGetProperty("artist", out var artist) && artist.ValueKind == JsonValueKind.Object)
        {
            return ReadTidalId(artist);
        }

        return string.Empty;
    }

    private static string ReadTidalArtistName(JsonElement item)
    {
        if (item.TryGetProperty("artist", out var artist) && artist.ValueKind == JsonValueKind.Object
            && artist.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
        {
            return name.GetString() ?? string.Empty;
        }

        return ReadTidalString(item, "artistName");
    }

    private static string ReadTidalId(JsonElement item)
    {
        if (!item.TryGetProperty("id", out var id))
        {
            return string.Empty;
        }

        return id.ValueKind switch
        {
            JsonValueKind.String => id.GetString() ?? string.Empty,
            JsonValueKind.Number => id.ToString(),
            _ => string.Empty
        };
    }

    private static string ReadTidalString(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string BuildTidalImage(JsonElement item)
    {
        var cover = ReadTidalString(item, "cover");
        if (string.IsNullOrWhiteSpace(cover) && item.TryGetProperty("album", out var album))
        {
            cover = ReadTidalString(album, "cover");
        }

        if (string.IsNullOrWhiteSpace(cover))
        {
            cover = ReadTidalString(item, "imageId");
        }

        if (string.IsNullOrWhiteSpace(cover))
        {
            return string.Empty;
        }

        var normalized = cover.Replace("-", "/", StringComparison.Ordinal).Trim('/');
        return $"https://resources.tidal.com/images/{normalized}/750x750.jpg";
    }

    private static bool TidalHasAtmos(JsonElement item)
    {
        if (ReadTidalString(item, "audioQuality").Contains("ATMOS", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var propertyName in new[] { "audioModes", "audioMode", "mediaMetadata", "tags" })
        {
            if (item.TryGetProperty(propertyName, out var property) && JsonContainsAtmos(property))
            {
                return true;
            }
        }

        return false;
    }

    private static bool JsonContainsAtmos(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.String => (element.GetString() ?? string.Empty).Contains("ATMOS", StringComparison.OrdinalIgnoreCase),
            JsonValueKind.Array => element.EnumerateArray().Any(JsonContainsAtmos),
            JsonValueKind.Object => element.EnumerateObject().Any(property => JsonContainsAtmos(property.Value)),
            _ => false
        };
}

public sealed record ArtistMediaExtrasPayload(
    string? AppleId,
    string? TidalId,
    IReadOnlyList<JsonElement> Atmos,
    IReadOnlyList<JsonElement> Videos,
    bool HasMoreVideos);
