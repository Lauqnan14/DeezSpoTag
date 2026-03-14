using System.Text.Json;
using System.Linq;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Core.Models.Deezer;

namespace DeezSpoTag.Web.Services;

public sealed class DeezSpoTagSearchService
{
    private const string SpotifySource = "spotify";
    private const string AppleSource = "apple";
    private const string DeezerSource = "deezer";
    private const string ArtistType = "artist";
    private const string TrackType = "track";
    private const string AlbumType = "album";
    private const string VideoType = "video";
    private const string StationType = "station";
    private const string AlbumResultsType = "albums";
    private const string ArtistResultsType = "artists";
    private const string PlaylistResultsType = "playlists";
    private const string PlaylistType = "playlist";
    private const string SongsType = "songs";
    private const string StationsType = "stations";
    private const string MusicVideosType = "music-videos";
    private const string AttributesField = "attributes";
    private const string AtmosTrait = "atmos";
    private const string SpatialTrait = "spatial";
    private const string ReleaseDateField = "releaseDate";
    private const string AudioTraitsField = "audioTraits";
    private const string AppleIdField = "appleId";
    private const string AppleUrlField = "appleUrl";
    private const string HasAtmosField = "hasAtmos";
    private const string AtmosDetectionField = "atmosDetection";
    private const string CatalogAtmosDetection = "catalog";
    private const string UnavailableAtmosDetection = "unavailable";
    private const string HasAtmosCatalogField = "hasAtmosCatalog";
    private const string ArtistField = "artist";
    private readonly SpotifySearchService _spotifySearch;
    private readonly AppleMusicCatalogService _appleCatalog;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly DeezerClient _deezerClient;
    private readonly AppleCatalogVideoAtmosEnricher _appleCatalogVideoAtmosEnricher;
    private readonly ILogger<DeezSpoTagSearchService> _logger;

    public DeezSpoTagSearchService(
        SpotifySearchService spotifySearch,
        AppleMusicCatalogService appleCatalog,
        DeezSpoTagSettingsService settingsService,
        DeezerClient deezerClient,
        AppleCatalogVideoAtmosEnricher appleCatalogVideoAtmosEnricher,
        ILogger<DeezSpoTagSearchService> logger)
    {
        _spotifySearch = spotifySearch;
        _appleCatalog = appleCatalog;
        _settingsService = settingsService;
        _deezerClient = deezerClient;
        _appleCatalogVideoAtmosEnricher = appleCatalogVideoAtmosEnricher;
        _logger = logger;
    }

    public async Task<DeezSpoTagSearchResponse?> SearchAsync(
        DeezSpoTagSearchRequest request,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeEngine(request.Engine);
        switch (normalized)
        {
            case SpotifySource:
                return await SearchSpotifyAsync(request.Query, request.Limit, cancellationToken);
            case AppleSource:
                var filters = ParseAudioTraitFilters(request.AudioTraits);
                return await SearchAppleAsync(request.Query, request.Limit, request.Offset, request.Types, filters, cancellationToken);
            case DeezerSource:
                var signal = new DeezerSearchSignal(request.Title, request.Artist, request.Album, request.Isrc, request.DurationMs);
                return await SearchDeezerAsync(request.Query, request.Limit, request.Offset, signal, cancellationToken);
            default:
                return null;
        }
    }

    public async Task<DeezSpoTagSearchTypeResponse?> SearchByTypeAsync(
        string engine,
        string query,
        string type,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeEngine(engine);
        var typeNormalized = NormalizeType(type);
        if (string.IsNullOrWhiteSpace(typeNormalized))
        {
            return null;
        }

        switch (normalized)
        {
            case SpotifySource:
                return await SearchSpotifyByTypeAsync(query, typeNormalized, limit, offset, cancellationToken);
            case AppleSource:
                return await SearchAppleByTypeAsync(query, typeNormalized, limit, offset, cancellationToken);
            case DeezerSource:
                return await SearchDeezerByTypeAsync(query, typeNormalized, limit, offset, cancellationToken);
            default:
                return null;
        }
    }

    private async Task<DeezSpoTagSearchResponse?> SearchSpotifyAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var result = await _spotifySearch.SearchAsync(query, Math.Clamp(limit, 1, 50), cancellationToken);
        if (result == null)
        {
            return null;
        }

        return new DeezSpoTagSearchResponse(
            Source: SpotifySource,
            Tracks: result.Tracks.Select(MapSpotifyItem).ToList<object>(),
            Albums: result.Albums.Select(MapSpotifyItem).ToList<object>(),
            Artists: result.Artists.Select(MapSpotifyItem).ToList<object>(),
            Playlists: new List<object>(),
            Videos: Array.Empty<object>(),
            Stations: Array.Empty<object>(),
            HasMoreVideos: false,
            Totals: result.Totals);
    }

    private async Task<DeezSpoTagSearchTypeResponse?> SearchSpotifyByTypeAsync(
        string query,
        string type,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        if (string.Equals(type, PlaylistType, StringComparison.OrdinalIgnoreCase))
        {
            return new DeezSpoTagSearchTypeResponse(
                Source: SpotifySource,
                Type: PlaylistType,
                Items: new List<object>(),
                Total: 0);
        }

        var result = await _spotifySearch.SearchByTypeAsync(query, type, Math.Clamp(limit, 1, 50), Math.Max(0, offset), cancellationToken);
        if (result == null)
        {
            return null;
        }

        return new DeezSpoTagSearchTypeResponse(
            Source: SpotifySource,
            Type: result.Type,
            Items: result.Items.Select(MapSpotifyItem).ToList<object>(),
            Total: result.Total);
    }

    private static object MapSpotifyItem(SpotifySearchItem item)
    {
        var (artist, album) = ParseSpotifySubtitle(item);
        var followers = ParseSpotifyFollowers(item);

        return new
        {
            source = SpotifySource,
            spotifyId = item.Id,
            spotifyUrl = item.SourceUrl,
            type = item.Type,
            name = item.Name,
            artist,
            album,
            image = item.ImageUrl ?? string.Empty,
            followers,
            owner = item.Type == PlaylistType ? artist : string.Empty,
            release_date = string.Empty,
            durationMs = item.DurationMs
        };
    }

    private static (string artist, string album) ParseSpotifySubtitle(SpotifySearchItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Subtitle))
        {
            return (string.Empty, string.Empty);
        }

        var parts = item.Subtitle.Split(" • ", StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            return (parts[0].Trim(), parts[1].Trim());
        }

        return (item.Subtitle.Trim(), string.Empty);
    }

    private static int? ParseSpotifyFollowers(SpotifySearchItem item)
    {
        if (!string.Equals(item.Type, ArtistType, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(item.Subtitle))
        {
            return null;
        }

        var token = "followers";
        var index = item.Subtitle.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var raw = item.Subtitle[..index].Replace("Followers", "", StringComparison.OrdinalIgnoreCase)
            .Trim()
            .Replace(",", "");
        return int.TryParse(raw, out var value) ? value : null;
    }

    private async Task<DeezSpoTagSearchResponse?> SearchAppleAsync(
        string term,
        int limit,
        int offset,
        string? typesOverride,
        string[]? audioTraitFilters,
        CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, 50);
        offset = Math.Max(offset, 0);

        var resolvedTypes = string.IsNullOrWhiteSpace(typesOverride)
            ? $"songs,albums,artists,playlists,{MusicVideosType}"
            : typesOverride;
        var root = await SearchViaCatalogAsync(term, limit, offset, resolvedTypes, cancellationToken);
        var tracks = ReadCatalogSongs(root, audioTraitFilters);
        var albums = ReadCatalogAlbums(root, audioTraitFilters);
        var artists = ReadCatalogArtists(root);
        var playlists = ReadCatalogPlaylists(root);
        var stations = ReadCatalogStations(root);
        var videos = ReadCatalogMusicVideos(root);
        var hasMoreVideos = CatalogHasNext(root, MusicVideosType);
        if (videos.Count == 0
            && TypesInclude(resolvedTypes, MusicVideosType)
            && !string.Equals(resolvedTypes.Trim(), MusicVideosType, StringComparison.OrdinalIgnoreCase))
        {
            var videoRoot = await SearchViaCatalogAsync(term, limit, offset, MusicVideosType, cancellationToken);
            videos = ReadCatalogMusicVideos(videoRoot);
            hasMoreVideos = CatalogHasNext(videoRoot, MusicVideosType);
        }

        try
        {
            await _appleCatalogVideoAtmosEnricher.EnrichAsync(
                videos,
                "Apple video details Atmos lookup failed for {AppleId}",
                cancellationToken);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Apple video Atmos enrichment timed out during search.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Apple video Atmos enrichment failed during search.");
        }

        return new DeezSpoTagSearchResponse(
            Source: AppleSource,
            Tracks: tracks,
            Albums: albums,
            Artists: artists,
            Playlists: playlists,
            Videos: videos.Cast<object>().ToList(),
            Stations: stations,
            HasMoreVideos: hasMoreVideos,
            Totals: null);
    }

    private async Task<DeezSpoTagSearchTypeResponse?> SearchAppleByTypeAsync(
        string term,
        string type,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        var types = type switch
        {
            TrackType => SongsType,
            AlbumType => AlbumResultsType,
            ArtistType => ArtistResultsType,
            PlaylistType => PlaylistResultsType,
            VideoType => MusicVideosType,
            StationType => StationsType,
            _ => null
        };

        var result = await SearchAppleAsync(term, limit, offset, types, null, cancellationToken);
        if (result == null)
        {
            return null;
        }

        var items = type switch
        {
            TrackType => result.Tracks,
            AlbumType => result.Albums,
            ArtistType => result.Artists,
            PlaylistType => result.Playlists,
            VideoType => result.Videos,
            StationType => result.Stations,
            _ => Array.Empty<object>()
        };

        return new DeezSpoTagSearchTypeResponse(
            Source: AppleSource,
            Type: type,
            Items: items,
            Total: items.Count);
    }


    private async Task<JsonElement> SearchViaCatalogAsync(
        string term,
        int limit,
        int offset,
        string? typesOverride,
        CancellationToken cancellationToken)
    {
        var settings = _settingsService.LoadSettings();
        var storefront = await _appleCatalog.ResolveStorefrontAsync(
            settings.AppleMusic?.Storefront,
            settings.AppleMusic?.MediaUserToken,
            cancellationToken);
        var includeRelationships = ShouldIncludeRelationshipsTracks(typesOverride);
        using var doc = await _appleCatalog.SearchAsync(
            term,
            limit,
            storefront: storefront,
            language: "en-US",
            cancellationToken,
            options: new AppleMusicCatalogService.AppleSearchOptions(
                TypesOverride: typesOverride,
                Offset: offset,
                IncludeRelationshipsTracks: includeRelationships));
        return doc.RootElement.Clone();
    }

    private static bool ShouldIncludeRelationshipsTracks(string? typesOverride)
    {
        if (string.IsNullOrWhiteSpace(typesOverride))
        {
            return true;
        }

        var normalized = typesOverride.Trim().ToLowerInvariant();
        return normalized != MusicVideosType && normalized != ArtistResultsType;
    }

    private static bool CatalogHasNext(JsonElement root, string key)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!results.TryGetProperty(key, out var node) || node.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (node.TryGetProperty("next", out var nextEl) && nextEl.ValueKind == JsonValueKind.String)
        {
            return !string.IsNullOrWhiteSpace(nextEl.GetString());
        }

        return false;
    }

    private static List<object> ReadCatalogSongs(JsonElement root, IReadOnlyCollection<string>? audioTraitFilters)
    {
        var tracks = new List<object>();
        if (!TryGetCatalogResults(root, SongsType, out var results))
        {
            return tracks;
        }

        foreach (var item in results.EnumerateArray())
        {
            var attrs = GetAttributes(item);
            var audioTraits = MergeAudioTraits(attrs, item);
            if (audioTraitFilters != null && !MatchesAudioTraits(audioTraits, audioTraitFilters))
            {
                continue;
            }

            tracks.Add(BuildCatalogSong(item, attrs, audioTraits));
        }

        return tracks;
    }

    private static object BuildCatalogSong(JsonElement item, JsonElement attrs, List<string> audioTraits)
    {
        var hasAtmos = HasAudioTrait(audioTraits, AtmosTrait);
        var hasSpatial = HasAudioTrait(audioTraits, SpatialTrait);
        return new
        {
            source = AppleSource,
            type = SongsType,
            appleId = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "",
            appleUrl = attrs.TryGetProperty("url", out var urlEl) ? urlEl.GetString() ?? "" : "",
            name = attrs.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "",
            artist = attrs.TryGetProperty("artistName", out var artistEl) ? artistEl.GetString() ?? "" : "",
            album = attrs.TryGetProperty("albumName", out var albumEl) ? albumEl.GetString() ?? "" : "",
            image = GetArtworkUrl(item, attrs),
            hasAtmos,
            hasSpatial,
            audioTraits,
            hasLyrics = attrs.TryGetProperty("hasLyrics", out var hasLyricsEl)
                && hasLyricsEl.ValueKind is JsonValueKind.True or JsonValueKind.False
                && hasLyricsEl.GetBoolean(),
            hasTimeSyncedLyrics = attrs.TryGetProperty("hasTimeSyncedLyrics", out var hasSyncedEl)
                && hasSyncedEl.ValueKind is JsonValueKind.True or JsonValueKind.False
                && hasSyncedEl.GetBoolean(),
            durationMs = attrs.TryGetProperty("durationInMillis", out var durationEl) ? durationEl.GetInt64() : 0,
            releaseDate = attrs.TryGetProperty(ReleaseDateField, out var releaseEl) ? releaseEl.GetString() ?? "" : "",
            isrc = attrs.TryGetProperty("isrc", out var isrcEl) ? isrcEl.GetString() ?? "" : "",
            previewUrl = TryGetPreviewUrl(attrs)
        };
    }

    private static List<object> ReadCatalogAlbums(JsonElement root, IReadOnlyCollection<string>? audioTraitFilters)
    {
        var albums = new List<object>();
        if (!TryGetCatalogResults(root, AlbumResultsType, out var results))
        {
            return albums;
        }

        foreach (var item in results.EnumerateArray())
        {
            var attrs = GetAttributes(item);
            var audioTraits = MergeAudioTraits(attrs, item);
            if (audioTraitFilters != null && !MatchesAudioTraits(audioTraits, audioTraitFilters))
            {
                continue;
            }

            albums.Add(BuildCatalogAlbum(item, attrs, audioTraits));
        }

        return albums;
    }

    private static object BuildCatalogAlbum(JsonElement item, JsonElement attrs, List<string> audioTraits)
    {
        var hasAtmos = HasAudioTrait(audioTraits, AtmosTrait);
        var hasSpatial = HasAudioTrait(audioTraits, SpatialTrait);
        return new
        {
            source = AppleSource,
            type = AlbumResultsType,
            appleId = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "",
            appleUrl = attrs.TryGetProperty("url", out var urlEl) ? urlEl.GetString() ?? "" : "",
            name = attrs.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "",
            artist = attrs.TryGetProperty("artistName", out var artistEl) ? artistEl.GetString() ?? "" : "",
            image = GetArtworkUrl(item, attrs),
            hasAtmos,
            hasSpatial,
            atmosTrackCount = CountAtmosTracks(item),
            audioTraits,
            releaseDate = attrs.TryGetProperty(ReleaseDateField, out var releaseEl) ? releaseEl.GetString() ?? "" : "",
            trackCount = attrs.TryGetProperty("trackCount", out var countEl) ? countEl.GetInt32() : 0
        };
    }

    private static List<object> ReadCatalogArtists(JsonElement root)
    {
        var artists = new List<object>();
        if (!TryGetCatalogResults(root, ArtistResultsType, out var results))
        {
            return artists;
        }

        foreach (var item in results.EnumerateArray())
        {
            var attrs = GetAttributes(item);
            artists.Add(new
            {
                source = AppleSource,
                type = ArtistResultsType,
                appleId = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "",
                appleUrl = attrs.TryGetProperty("url", out var urlEl) ? urlEl.GetString() ?? "" : "",
                name = attrs.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "",
                image = GetArtworkUrl(item, attrs)
            });
        }

        return artists;
    }

    private static List<Dictionary<string, object?>> ReadCatalogMusicVideos(JsonElement root)
    {
        var videos = new List<Dictionary<string, object?>>();
        if (!TryGetCatalogResults(root, MusicVideosType, out var results))
        {
            return videos;
        }

        foreach (var item in results.EnumerateArray())
        {
            var attrs = GetAttributes(item);
            videos.Add(BuildCatalogVideo(item, attrs));
        }

        return videos;
    }

    private static Dictionary<string, object?> BuildCatalogVideo(JsonElement item, JsonElement attrs)
    {
        var audioTraits = GetStringArray(attrs, AudioTraitsField);
        var hasAtmosCatalog = HasAudioTrait(audioTraits, AtmosTrait);
        return new Dictionary<string, object?>
        {
            ["source"] = AppleSource,
            ["type"] = MusicVideosType,
            [AppleIdField] = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "",
            [AppleUrlField] = attrs.TryGetProperty("url", out var urlEl) ? urlEl.GetString() ?? "" : "",
            ["name"] = attrs.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "",
            [ArtistField] = attrs.TryGetProperty("artistName", out var artistEl) ? artistEl.GetString() ?? "" : "",
            ["image"] = GetArtworkUrl(item, attrs),
            ["isVideo"] = true,
            ["previewUrl"] = TryGetPreviewUrl(attrs),
            ["durationMs"] = attrs.TryGetProperty("durationInMillis", out var durationEl) ? durationEl.GetInt64() : 0,
            [ReleaseDateField] = attrs.TryGetProperty(ReleaseDateField, out var releaseEl) ? releaseEl.GetString() ?? "" : "",
            [AudioTraitsField] = audioTraits,
            [HasAtmosCatalogField] = hasAtmosCatalog,
            [HasAtmosField] = hasAtmosCatalog,
            [AtmosDetectionField] = hasAtmosCatalog ? CatalogAtmosDetection : UnavailableAtmosDetection
        };
    }

    private static List<object> ReadCatalogStations(JsonElement root)
    {
        var stations = new List<object>();
        if (!TryGetCatalogResults(root, StationsType, out var results))
        {
            return stations;
        }

        foreach (var item in results.EnumerateArray())
        {
            var attrs = GetAttributes(item);
            stations.Add(new
            {
                source = AppleSource,
                type = StationsType,
                appleId = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "",
                appleUrl = attrs.TryGetProperty("url", out var urlEl) ? urlEl.GetString() ?? "" : "",
                name = attrs.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "",
                image = GetArtworkUrl(item, attrs),
                isStation = true
            });
        }

        return stations;
    }

    private static List<object> ReadCatalogPlaylists(JsonElement root)
    {
        var playlists = new List<object>();
        if (!TryGetCatalogResults(root, PlaylistResultsType, out var results))
        {
            return playlists;
        }

        foreach (var item in results.EnumerateArray())
        {
            var attrs = GetAttributes(item);
            playlists.Add(new
            {
                source = AppleSource,
                type = PlaylistResultsType,
                appleId = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "",
                appleUrl = attrs.TryGetProperty("url", out var urlEl) ? urlEl.GetString() ?? "" : "",
                name = attrs.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "",
                curator = attrs.TryGetProperty("curatorName", out var curatorEl) ? curatorEl.GetString() ?? "" : "",
                curatorName = attrs.TryGetProperty("curatorName", out var curatorNameEl) ? curatorNameEl.GetString() ?? "" : "",
                image = GetArtworkUrl(item, attrs)
            });
        }

        return playlists;
    }

    private static JsonElement GetAttributes(JsonElement item)
    {
        return item.TryGetProperty(AttributesField, out var attrs) ? attrs : default;
    }

    private static bool HasAudioTrait(IReadOnlyCollection<string> audioTraits, string trait)
    {
        return audioTraits.Any(value => value.Contains(trait, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryGetCatalogResults(JsonElement root, string key, out JsonElement results)
    {
        results = default;
        if (!root.TryGetProperty("results", out var res) || res.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!res.TryGetProperty(key, out var node) || node.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!node.TryGetProperty("data", out results) || results.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return true;
    }

    private static bool TypesInclude(string? types, string target)
    {
        if (string.IsNullOrWhiteSpace(types) || string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        return types.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(t => string.Equals(t, target, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> GetStringArray(JsonElement attrs, string property)
    {
        var values = new List<string>();
        if (attrs.ValueKind != JsonValueKind.Object ||
            !attrs.TryGetProperty(property, out var arr) ||
            arr.ValueKind != JsonValueKind.Array)
        {
            return values;
        }

        values.AddRange(arr.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString())
            .Where(static value => !string.IsNullOrWhiteSpace(value))!);

        return values;
    }

    private static List<string> MergeAudioTraits(JsonElement attrs, JsonElement item)
    {
        var traits = GetStringArray(attrs, AudioTraitsField);
        if (TryGetRelationshipAudioTraits(item, out var relationshipTraits))
        {
            traits.AddRange(relationshipTraits);
        }

        return traits.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool MatchesAudioTraits(IReadOnlyCollection<string> availableTraits, IReadOnlyCollection<string> requestedTraits)
    {
        foreach (var requested in requestedTraits)
        {
            if (string.Equals(requested, AtmosTrait, StringComparison.OrdinalIgnoreCase))
            {
                if (availableTraits.Any(trait => trait.Contains(AtmosTrait, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
                continue;
            }

            if (string.Equals(requested, SpatialTrait, StringComparison.OrdinalIgnoreCase))
            {
                if (availableTraits.Any(trait => trait.Contains(SpatialTrait, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
                continue;
            }

            if (availableTraits.Any(trait => trait.Contains(requested, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static int CountAtmosTracks(JsonElement item)
    {
        if (!item.TryGetProperty("relationships", out var rel) || rel.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        if (!rel.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        if (!tracks.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        var count = 0;
        foreach (var entry in data.EnumerateArray())
        {
            if (!entry.TryGetProperty(AttributesField, out var attrs) || attrs.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var values = GetStringArray(attrs, AudioTraitsField);
            if (values.Any(t => t.Contains(AtmosTrait, StringComparison.OrdinalIgnoreCase)))
            {
                count++;
            }
        }

        return count;
    }

    private static bool TryGetRelationshipAudioTraits(JsonElement item, out List<string> traits)
    {
        traits = new List<string>();
        if (!item.TryGetProperty("relationships", out var rel) || rel.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!rel.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!tracks.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var entry in data.EnumerateArray())
        {
            if (!entry.TryGetProperty(AttributesField, out var attrs) || attrs.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var values = GetStringArray(attrs, AudioTraitsField);
            if (values.Count > 0)
            {
                traits.AddRange(values);
            }
        }

        traits = traits.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return traits.Count > 0;
    }

    private static string TryGetPreviewUrl(JsonElement attrs)
    {
        if (attrs.ValueKind == JsonValueKind.Object
            && attrs.TryGetProperty("previews", out var previews)
            && previews.ValueKind == JsonValueKind.Array)
        {
            var first = previews.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object
                && first.TryGetProperty("url", out var previewEl)
                && previewEl.ValueKind == JsonValueKind.String)
            {
                return previewEl.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static string GetArtworkUrl(JsonElement item, JsonElement attrs)
    {
        if (TryBuildArtworkUrlFromNode(attrs, out var artworkUrl))
        {
            return artworkUrl;
        }

        if (item.TryGetProperty(AttributesField, out var attrsNode)
            && TryBuildArtworkUrlFromNode(attrsNode, out artworkUrl))
        {
            return artworkUrl;
        }

        return string.Empty;
    }

    private static bool TryBuildArtworkUrlFromNode(JsonElement node, out string artworkUrl)
    {
        artworkUrl = string.Empty;
        if (node.ValueKind != JsonValueKind.Object
            || !node.TryGetProperty("artwork", out var artworkNode)
            || artworkNode.ValueKind != JsonValueKind.Object
            || !artworkNode.TryGetProperty("url", out var urlEl)
            || urlEl.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var template = urlEl.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(template))
        {
            return false;
        }

        var width = artworkNode.TryGetProperty("width", out var widthEl) && widthEl.TryGetInt32(out var w)
            ? w
            : 0;
        var height = artworkNode.TryGetProperty("height", out var heightEl) && heightEl.TryGetInt32(out var h)
            ? h
            : 0;
        artworkUrl = AppleArtworkRenderHelper.BuildArtworkUrl(template, width, height);
        return true;
    }

    private async Task<DeezSpoTagSearchResponse?> SearchDeezerAsync(
        string query,
        int limit,
        int offset,
        DeezerSearchSignal signal,
        CancellationToken cancellationToken)
    {
        var options = new ApiOptions { Limit = Math.Clamp(limit, 1, 50), Index = Math.Max(0, offset) };
        var trackItems = new List<object>();
        var albumItems = new List<object>();
        var artistItems = new List<object>();
        var playlistItems = new List<object>();

        var totals = new Dictionary<string, int>();
        var hadSearchSignal = false;

        cancellationToken.ThrowIfCancellationRequested();
        var resolvedTrack = await TryResolveDeezerTrackAsync(signal, cancellationToken);
        if (resolvedTrack != null)
        {
            trackItems.Add(resolvedTrack);
            hadSearchSignal = true;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var trackResult = await _deezerClient.Api.SearchTrackAsync(query, options);
            trackItems.AddRange(ToObjectList(trackResult.Data));
            totals["tracks"] = trackResult.Total;
            hadSearchSignal = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Deezer track search failed: Query");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var albumResult = await _deezerClient.Api.SearchAlbumAsync(query, options);
            albumItems.AddRange(ToObjectList(albumResult.Data));
            totals[AlbumResultsType] = albumResult.Total;
            hadSearchSignal = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Deezer album search failed: Query");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var artistResult = await _deezerClient.Api.SearchArtistAsync(query, options);
            artistItems.AddRange(ToObjectList(artistResult.Data));
            totals[ArtistResultsType] = artistResult.Total;
            hadSearchSignal = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Deezer artist search failed: Query");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var playlistResult = await _deezerClient.Api.SearchPlaylistAsync(query, options);
            playlistItems.AddRange(ToObjectList(playlistResult.Data));
            totals[PlaylistResultsType] = playlistResult.Total;
            hadSearchSignal = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Deezer playlist search failed: Query");
        }

        if (!hadSearchSignal)
        {
            _logger.LogWarning("Deezer search failed across all categories: Query");
            return null;
        }

        trackItems = DeduplicateDeezerItems(trackItems);
        return new DeezSpoTagSearchResponse(
            Source: DeezerSource,
            Tracks: trackItems,
            Albums: albumItems,
            Artists: artistItems,
            Playlists: playlistItems,
            Videos: Array.Empty<object>(),
            Stations: Array.Empty<object>(),
            HasMoreVideos: false,
            Totals: totals);
    }

    private async Task<ApiTrack?> TryResolveDeezerTrackAsync(
        DeezerSearchSignal signal,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(signal.Isrc))
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var byIsrc = await _deezerClient.GetTrackByIsrcAsync(signal.Isrc.Trim());
                if (IsValidDeezerTrack(byIsrc))
                {
                    return byIsrc;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Deezer metadata ISRC resolution failed for Isrc.");
            }
        }

        var normalizedArtist = (signal.Artist ?? string.Empty).Trim();
        var normalizedTitle = (signal.Title ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedArtist) || string.IsNullOrWhiteSpace(normalizedTitle))
        {
            return null;
        }

        var normalizedAlbum = (signal.Album ?? string.Empty).Trim();
        var normalizedDuration = signal.DurationMs.HasValue && signal.DurationMs.Value > 0
            ? (int?)Math.Clamp(signal.DurationMs.Value, 1L, int.MaxValue)
            : null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var trackId = await _deezerClient.GetTrackIdFromMetadataAsync(
                normalizedArtist,
                normalizedTitle,
                normalizedAlbum,
                normalizedDuration);
            if (string.IsNullOrWhiteSpace(trackId) || trackId == "0")
            {
                cancellationToken.ThrowIfCancellationRequested();
                trackId = await _deezerClient.GetTrackIdFromMetadataFastAsync(
                    normalizedArtist,
                    normalizedTitle,
                    normalizedDuration);
            }

            if (string.IsNullOrWhiteSpace(trackId) || trackId == "0")
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var resolved = await _deezerClient.GetTrackAsync(trackId);
            return IsValidDeezerTrack(resolved) ? resolved : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Deezer metadata resolution failed for Artist - Title.");
            return null;
        }
    }

    private static bool IsValidDeezerTrack(ApiTrack? track)
    {
        return track != null
            && !string.IsNullOrWhiteSpace(track.Id)
            && !string.Equals(track.Id, "0", StringComparison.OrdinalIgnoreCase);
    }

    private static List<object> DeduplicateDeezerItems(IEnumerable<object> items)
    {
        var output = new List<object>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var id = ExtractDeezerItemId(item);
            if (!string.IsNullOrWhiteSpace(id) && !seenIds.Add(id))
            {
                continue;
            }

            output.Add(item);
        }

        return output;
    }

    private static string ExtractDeezerItemId(object item)
    {
        if (item is ApiTrack apiTrack)
        {
            return apiTrack.Id ?? string.Empty;
        }

        if (item is JsonElement element
            && element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("id", out var idElement))
        {
            return idElement.ToString();
        }

        return string.Empty;
    }

    private async Task<DeezSpoTagSearchTypeResponse?> SearchDeezerByTypeAsync(
        string query,
        string type,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var options = new ApiOptions { Limit = Math.Clamp(limit, 1, 50), Index = Math.Max(0, offset) };

        try
        {
            var result = await _deezerClient.Api.SearchAsync(query, type, options);
            var items = ToObjectList(result.Data);
            return new DeezSpoTagSearchTypeResponse(
                Source: DeezerSource,
                Type: type,
                Items: items,
                Total: result.Total);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Deezer search failed for type {Type}", type);
            return null;
        }
    }

    private static List<object> ToObjectList(object[]? data)
    {
        return data == null ? new List<object>() : data.ToList<object>();
    }

    private static string NormalizeType(string type)
    {
        var normalized = (type ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            TrackType or AlbumType or ArtistType or PlaylistType or StationType or VideoType => normalized,
            _ => string.Empty
        };
    }

    private static string NormalizeEngine(string engine)
    {
        return (engine ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static string[]? ParseAudioTraitFilters(string? audioTraits)
    {
        if (string.IsNullOrWhiteSpace(audioTraits))
        {
            return null;
        }

        var filters = audioTraits.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Where(t => t is not ("alac" or "lossless" or "hi-res-lossless" or "hires" or "hi-res"))
            .ToArray();
        return filters.Length == 0 ? null : filters;
    }

    private sealed record DeezerSearchSignal(
        string? Title,
        string? Artist,
        string? Album,
        string? Isrc,
        long? DurationMs);
}

public sealed record DeezSpoTagSearchRequest(
    string Engine,
    string Query,
    int Limit,
    int Offset,
    string? Types = null,
    string? AudioTraits = null,
    string? Title = null,
    string? Artist = null,
    string? Album = null,
    string? Isrc = null,
    long? DurationMs = null);

public sealed record DeezSpoTagSearchResponse(
    string Source,
    IReadOnlyList<object> Tracks,
    IReadOnlyList<object> Albums,
    IReadOnlyList<object> Artists,
    IReadOnlyList<object> Playlists,
    IReadOnlyList<object> Videos,
    IReadOnlyList<object> Stations,
    bool HasMoreVideos,
    Dictionary<string, int>? Totals);

public sealed record DeezSpoTagSearchTypeResponse(
    string Source,
    string Type,
    IReadOnlyList<object> Items,
    int Total);
