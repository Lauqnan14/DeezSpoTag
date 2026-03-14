using DeezSpoTag.Core.Models.Qobuz;
using DeezSpoTag.Integrations.Qobuz;
using Microsoft.Extensions.Options;
namespace DeezSpoTag.Services.Metadata.Qobuz;

public sealed class QobuzMetadataService : IQobuzMetadataService
{
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
        return response?.Tracks?.Items ?? new List<QobuzTrack>();
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
        if (tracksElement.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return results;
        }

        if (!tracksElement.TryGetProperty("items", out var items) || items.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return results;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (!TryParseAutosuggestTrack(item, out var track))
            {
                continue;
            }

            results.Add(track);
        }

        return results;
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
        return true;
    }

    private static QobuzArtist? ResolvePerformer(System.Text.Json.JsonElement item)
    {
        var performerName = ReadNestedString(item, "performer", "name")
            ?? ReadNestedString(item, "artist", "name")
            ?? ReadNestedString(item, "album", "artist", "name");
        return string.IsNullOrWhiteSpace(performerName) ? null : new QobuzArtist { Name = performerName };
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
        foreach (var segment in path)
        {
            if (!current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind == System.Text.Json.JsonValueKind.String ? current.GetString() : null;
    }

    private static int ReadInt32OrDefault(System.Text.Json.JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Number
            ? value.GetInt32()
            : 0;
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
