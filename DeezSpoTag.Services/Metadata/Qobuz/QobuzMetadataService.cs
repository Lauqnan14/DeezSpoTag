using DeezSpoTag.Core.Models.Qobuz;
using DeezSpoTag.Integrations.Qobuz;
using Microsoft.Extensions.Options;
namespace DeezSpoTag.Services.Metadata.Qobuz;

public sealed class QobuzMetadataService : IQobuzMetadataService
{
    private const string ArtistProperty = "artist";
    private readonly IQobuzApiClient _apiClient;
    private readonly QobuzArtistService _artistService;
    private readonly QobuzApiConfig _config;
    public QobuzMetadataService(IQobuzApiClient apiClient, QobuzArtistService artistService, IOptions<QobuzApiConfig> options)
    {
        _apiClient = apiClient;
        _artistService = artistService;
        _config = options.Value;
    }

    public async Task<QobuzTrack?> FindTrackByISRC(string isrc, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(isrc))
        {
            return null;
        }

        var query = $"isrc:{isrc.Trim()}";
        var response = await _apiClient.SearchTracksAsync(query, limit: 20, offset: 0, ct);
        var matches = response?.Tracks?.Items
            .Where(track => !string.IsNullOrWhiteSpace(track.ISRC)
                && string.Equals(track.ISRC, isrc, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches?.FirstOrDefault();
    }

    public async Task<QobuzAlbum?> FindAlbumByUPC(string upc, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(upc))
        {
            return null;
        }

        var query = $"upc:{upc.Trim()}";
        var response = await _apiClient.SearchAlbumsAsync(query, limit: 20, offset: 0, ct);
        var candidates = response?.Albums?.Items ?? new List<QobuzAlbum>();

        return candidates.FirstOrDefault(album =>
            string.Equals(album.UPC, upc, StringComparison.OrdinalIgnoreCase)
            || string.Equals(album.Barcode, upc, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<QobuzArtist?> FindArtistByName(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var response = await _apiClient.SearchArtistsAsync(name, limit: 20, offset: 0, ct);
        return response?.Artists?.Items.FirstOrDefault();
    }

    public async Task<List<QobuzTrack>> SearchTracks(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new List<QobuzTrack>();
        }

        var response = await _apiClient.SearchTracksAsync(query, limit: 50, offset: 0, ct);
        var tracks = response?.Tracks?.Items ?? new List<QobuzTrack>();
        var catalogTracks = await SearchCatalogTracks(query, ct);
        if (catalogTracks.Count == 0)
        {
            return tracks;
        }

        var merged = new Dictionary<int, QobuzTrack>();
        foreach (var track in tracks.Concat(catalogTracks).Where(track => track.Id > 0))
        {
            merged.TryAdd(track.Id, track);
        }

        return merged.Count > 0 ? merged.Values.ToList() : tracks;
    }

    public async Task<List<QobuzTrack>> SearchAlbumTracks(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new List<QobuzTrack>();
        }

        var response = await _apiClient.SearchAlbumsAsync(query, limit: 10, offset: 0, ct);
        var albums = response?.Albums?.Items ?? new List<QobuzAlbum>();
        if (albums.Count == 0)
        {
            return new List<QobuzTrack>();
        }

        var results = new Dictionary<int, QobuzTrack>();
        foreach (var album in albums.Where(static album => !string.IsNullOrWhiteSpace(album.Url)))
        {
            var singleTrackAlbum = await BuildSingleTrackAlbumCandidateAsync(album, ct);
            if (singleTrackAlbum?.Id > 0)
            {
                results.TryAdd(singleTrackAlbum.Id, singleTrackAlbum);
                continue;
            }

            var pageTracks = await _apiClient.GetAlbumPageTracksAsync(album.Url!, ct);
            foreach (var track in pageTracks.Where(static track => track.Id > 0))
            {
                MergeAlbumMetadata(track, album);
                results.TryAdd(track.Id, track);
            }
        }

        return results.Values.ToList();
    }

    public async Task<List<QobuzTrack>> SearchTracksAutosuggest(string query, string? store, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new List<QobuzTrack>();
        }

        var resolvedStore = string.IsNullOrWhiteSpace(store) ? _config.DefaultStore : store;
        var response = await _apiClient.SearchAutosuggestAsync(resolvedStore, query, ct);
        if (response == null || response.Tracks.ValueKind == System.Text.Json.JsonValueKind.Undefined)
        {
            return new List<QobuzTrack>();
        }

        return ParseAutosuggestTracks(response.Tracks);
    }

    private static List<QobuzTrack> ParseAutosuggestTracks(System.Text.Json.JsonElement tracksElement)
    {
        var results = new List<QobuzTrack>();
        if (tracksElement.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            AddAutosuggestTracks(results, tracksElement);
            return results;
        }

        if (tracksElement.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return results;
        }

        if (!tracksElement.TryGetProperty("items", out var items) || items.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return results;
        }

        AddAutosuggestTracks(results, items);
        return results;
    }

    private static void AddAutosuggestTracks(List<QobuzTrack> results, System.Text.Json.JsonElement items)
    {
        foreach (var item in items.EnumerateArray())
        {
            if (!TryParseAutosuggestTrack(item, out var track))
            {
                continue;
            }

            results.Add(track);
        }
    }

    private static bool TryParseAutosuggestTrack(System.Text.Json.JsonElement item, out QobuzTrack track)
    {
        track = new QobuzTrack();
        if (!TryReadInt32(item, "id", out var id))
        {
            return false;
        }

        track.Id = id;
        track.Title = ReadString(item, "title");
        track.Duration = ReadInt32OrDefault(item, "duration");
        track.ISRC = ReadString(item, "isrc");
        track.MaximumBitDepth = ReadInt32OrDefault(item, "maximum_bit_depth");
        track.MaximumSamplingRate = ReadDoubleOrDefault(item, "maximum_sampling_rate");
        track.HiRes = ReadTrue(item, "hires");
        track.Performer = ResolvePerformer(item);
        track.Album = ResolveAlbum(item);
        return true;
    }

    private static QobuzArtist? ResolvePerformer(System.Text.Json.JsonElement item)
    {
        var performerName = ReadNestedString(item, "performer", "name")
            ?? ReadNestedString(item, ArtistProperty, "name")
            ?? ReadString(item, ArtistProperty)
            ?? ReadNestedString(item, "album", ArtistProperty, "name");
        if (string.IsNullOrWhiteSpace(performerName))
        {
            return null;
        }

        return new QobuzArtist
        {
            Id = ReadNestedInt32OrDefault(item, "performer", "id"),
            Name = performerName
        };
    }

    private static QobuzAlbum? ResolveAlbum(System.Text.Json.JsonElement item)
    {
        if (item.TryGetProperty("album", out var albumTitleValue)
            && albumTitleValue.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var autosuggestAlbumTitle = albumTitleValue.GetString();
            return string.IsNullOrWhiteSpace(autosuggestAlbumTitle)
                ? null
                : new QobuzAlbum { Title = autosuggestAlbumTitle };
        }

        if (!item.TryGetProperty("album", out var albumElement)
            || albumElement.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return null;
        }

        var albumId = ReadString(albumElement, "id");
        var albumTitle = ReadString(albumElement, "title");
        if (string.IsNullOrWhiteSpace(albumId) && string.IsNullOrWhiteSpace(albumTitle))
        {
            return null;
        }

        var album = new QobuzAlbum
        {
            Id = albumId,
            Title = albumTitle,
            MaximumBitDepth = ReadInt32OrDefault(albumElement, "maximum_bit_depth"),
            MaximumSamplingRate = ReadDoubleOrDefault(albumElement, "maximum_sampling_rate"),
            HiRes = ReadTrue(albumElement, "hires"),
            Streamable = ReadTrue(albumElement, "streamable"),
            Downloadable = ReadTrue(albumElement, "downloadable"),
            Purchasable = ReadTrue(albumElement, "purchasable")
        };
        var artistName = ReadNestedString(albumElement, ArtistProperty, "name");
        if (!string.IsNullOrWhiteSpace(artistName))
        {
            album.Artists.Add(new QobuzArtist
            {
                Id = ReadNestedInt32OrDefault(albumElement, ArtistProperty, "id"),
                Name = artistName
            });
        }

        return album;
    }

    private async Task<List<QobuzTrack>> SearchCatalogTracks(string query, CancellationToken cancellationToken)
    {
        var response = await _apiClient.SearchCatalogAsync(query, limit: 20, offset: 0, cancellationToken);
        if (response == null)
        {
            return new List<QobuzTrack>();
        }

        var results = new Dictionary<int, QobuzTrack>();
        foreach (var track in response.Tracks?.Items.Where(track => track.Id > 0) ?? Enumerable.Empty<QobuzTrack>())
        {
            results.TryAdd(track.Id, track);
        }

        foreach (var album in response.Albums?.Items ?? Enumerable.Empty<QobuzAlbum>())
        {
            var track = await BuildSingleTrackAlbumCandidateAsync(album, cancellationToken);
            if (track?.Id > 0)
            {
                results.TryAdd(track.Id, track);
            }
        }

        return results.Values.ToList();
    }

    private async Task<QobuzTrack?> BuildSingleTrackAlbumCandidateAsync(
        QobuzAlbum album,
        CancellationToken cancellationToken)
    {
        if (album.TracksCount != 1 || string.IsNullOrWhiteSpace(album.Url))
        {
            return null;
        }

        var trackIds = await _apiClient.GetAlbumPageTrackIdsAsync(album.Url, cancellationToken);
        if (trackIds.Count != 1)
        {
            return null;
        }

        var artist = album.Artists.FirstOrDefault();
        return new QobuzTrack
        {
            Id = trackIds[0],
            Title = album.Title,
            Duration = album.Duration,
            MaximumBitDepth = album.MaximumBitDepth,
            MaximumSamplingRate = album.MaximumSamplingRate,
            HiRes = album.HiRes,
            ParentalWarning = album.ParentalWarning,
            TrackNumber = 1,
            MediaNumber = 1,
            Performer = artist,
            Album = album
        };
    }

    private static void MergeAlbumMetadata(QobuzTrack track, QobuzAlbum album)
    {
        if (track.Album == null)
        {
            track.Album = album;
            return;
        }

        track.Album.Id ??= album.Id;
        track.Album.QobuzId = track.Album.QobuzId > 0 ? track.Album.QobuzId : album.QobuzId;
        track.Album.Title ??= album.Title;
        track.Album.Version ??= album.Version;
        track.Album.Url ??= album.Url;
        track.Album.Duration = track.Album.Duration > 0 ? track.Album.Duration : album.Duration;
        track.Album.TracksCount = track.Album.TracksCount > 0 ? track.Album.TracksCount : album.TracksCount;
        track.Album.MaximumBitDepth = track.Album.MaximumBitDepth > 0 ? track.Album.MaximumBitDepth : album.MaximumBitDepth;
        track.Album.MaximumSamplingRate = track.Album.MaximumSamplingRate > 0 ? track.Album.MaximumSamplingRate : album.MaximumSamplingRate;
        track.Album.HiRes = track.Album.HiRes || album.HiRes;
        track.Album.Streamable = track.Album.Streamable || album.Streamable;
        track.Album.Downloadable = track.Album.Downloadable || album.Downloadable;
        track.Album.Purchasable = track.Album.Purchasable || album.Purchasable;

        if (track.Album.Artists.Count == 0 && album.Artists.Count > 0)
        {
            track.Album.Artists.AddRange(album.Artists);
        }
    }

    private static string? ReadString(System.Text.Json.JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string? ReadNestedString(System.Text.Json.JsonElement element, params string[] path)
    {
        var current = element;
        if (!path.All(segment =>
            current.ValueKind == System.Text.Json.JsonValueKind.Object
            && current.TryGetProperty(segment, out current)))
        {
            return null;
        }

        return current.ValueKind == System.Text.Json.JsonValueKind.String ? current.GetString() : null;
    }

    private static int ReadInt32OrDefault(System.Text.Json.JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return 0;
        }

        return ReadInt32OrDefault(value);
    }

    private static int ReadInt32OrDefault(System.Text.Json.JsonElement value)
    {
        if (value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetInt32(out var numericValue))
        {
            return numericValue;
        }

        if (value.ValueKind == System.Text.Json.JsonValueKind.String
            && int.TryParse(value.GetString(), out var stringValue))
        {
            return stringValue;
        }

        return 0;
    }

    private static int ReadNestedInt32OrDefault(System.Text.Json.JsonElement element, params string[] path)
    {
        var current = element;
        if (!path.All(segment => current.TryGetProperty(segment, out current)))
        {
            return 0;
        }

        return ReadInt32OrDefault(current);
    }

    private static double ReadDoubleOrDefault(System.Text.Json.JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Number
            ? value.GetDouble()
            : 0d;
    }

    private static bool ReadTrue(System.Text.Json.JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.True;
    }

    private static bool TryReadInt32(System.Text.Json.JsonElement element, string property, out int value)
    {
        value = default;
        return element.TryGetProperty(property, out var propertyValue)
               && propertyValue.ValueKind == System.Text.Json.JsonValueKind.Number
               && propertyValue.TryGetInt32(out value);
    }

    public async Task<List<QobuzAlbum>> SearchAlbums(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new List<QobuzAlbum>();
        }

        var response = await _apiClient.SearchAlbumsAsync(query, limit: 50, offset: 0, ct);
        return response?.Albums?.Items ?? new List<QobuzAlbum>();
    }

    public async Task<List<QobuzArtist>> SearchArtists(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new List<QobuzArtist>();
        }

        var response = await _apiClient.SearchArtistsAsync(query, limit: 50, offset: 0, ct);
        return response?.Artists?.Items ?? new List<QobuzArtist>();
    }

    public async Task<QobuzArtist?> GetArtistDiscography(int artistId, string store, CancellationToken ct)
    {
        return await _artistService.GetArtistWithDiscographyAsync(artistId, store, ct);
    }

    public async Task<List<QobuzAlbum>> GetArtistAlbums(int artistId, string store, CancellationToken ct)
    {
        var artist = await _artistService.GetArtistWithDiscographyAsync(artistId, store, ct);
        return artist?.Albums?.Items ?? new List<QobuzAlbum>();
    }

    public async Task<QobuzTrack?> GetTrack(int trackId, CancellationToken ct)
    {
        if (trackId <= 0)
        {
            return null;
        }

        return await _apiClient.GetTrackAsync(trackId, ct);
    }

    public async Task<QobuzQualityInfo?> GetTrackQuality(int trackId, CancellationToken ct)
    {
        var track = await _apiClient.GetTrackAsync(trackId, ct);
        if (track == null)
        {
            return null;
        }

        return new QobuzQualityInfo
        {
            BitDepth = track.MaximumBitDepth,
            SampleRate = track.MaximumSamplingRate,
            IsHiRes = track.HiRes,
            IsStreamable = track.Album?.Streamable ?? false,
            IsDownloadable = track.Album?.Downloadable ?? false,
            IsPurchasable = track.Album?.Purchasable ?? false
        };
    }
}
