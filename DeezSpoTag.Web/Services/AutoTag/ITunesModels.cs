using System.Globalization;
using System.Text.Json.Serialization;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class ItunesSearchResults
{
    [JsonPropertyName("resultCount")]
    public int ResultCount { get; set; }

    [JsonPropertyName("results")]
    public List<ItunesSearchResult> Results { get; set; } = new();
}

public sealed class ItunesSearchResult
{
    [JsonPropertyName("wrapperType")]
    public string? WrapperType { get; set; }

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("artistId")]
    public long? ArtistId { get; set; }

    [JsonPropertyName("collectionId")]
    public long? CollectionId { get; set; }

    [JsonPropertyName("trackId")]
    public long? TrackId { get; set; }

    [JsonPropertyName("artistName")]
    public string? ArtistName { get; set; }

    [JsonPropertyName("collectionName")]
    public string? CollectionName { get; set; }

    [JsonPropertyName("trackName")]
    public string? TrackName { get; set; }

    [JsonPropertyName("trackCensoredName")]
    public string? TrackCensoredName { get; set; }

    [JsonPropertyName("collectionArtistName")]
    public string? CollectionArtistName { get; set; }

    [JsonPropertyName("collectionArtistId")]
    public long? CollectionArtistId { get; set; }

    [JsonPropertyName("discCount")]
    public short? DiscCount { get; set; }

    [JsonPropertyName("discNumber")]
    public short? DiscNumber { get; set; }

    [JsonPropertyName("trackCount")]
    public ushort? TrackCount { get; set; }

    [JsonPropertyName("trackNumber")]
    public int? TrackNumber { get; set; }

    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("trackViewUrl")]
    public string? TrackViewUrl { get; set; }

    [JsonPropertyName("previewUrl")]
    public string? PreviewUrl { get; set; }

    [JsonPropertyName("collectionViewUrl")]
    public string? CollectionViewUrl { get; set; }

    [JsonPropertyName("artistViewUrl")]
    public string? ArtistViewUrl { get; set; }

    [JsonPropertyName("collectionCensoredName")]
    public string? CollectionCensoredName { get; set; }

    [JsonPropertyName("trackTimeMillis")]
    public long? TrackTimeMillis { get; set; }

    [JsonPropertyName("primaryGenreName")]
    public string? PrimaryGenreName { get; set; }

    [JsonPropertyName("isrc")]
    public string? Isrc { get; set; }

    [JsonPropertyName("trackExplicitness")]
    public string? TrackExplicitness { get; set; }

    [JsonPropertyName("collectionExplicitness")]
    public string? CollectionExplicitness { get; set; }

    [JsonPropertyName("copyright")]
    public string? Copyright { get; set; }

    [JsonPropertyName("recordLabel")]
    public string? RecordLabel { get; set; }

    [JsonPropertyName("releaseDate")]
    public string? ReleaseDate { get; set; }

    [JsonPropertyName("artworkUrl100")]
    public string? ArtworkUrl100 { get; set; }

    public bool IsTrack => string.Equals(WrapperType, "track", StringComparison.OrdinalIgnoreCase);

    public ItunesTrackInfo? ToTrackInfo(ItunesMatchConfig config)
    {
        var resolvedTitle = ResolveTitle();
        if (!IsTrack || string.IsNullOrWhiteSpace(resolvedTitle))
        {
            return null;
        }

        var releaseDate = ParseDate(ReleaseDate);
        var art = ArtworkUrl100;
        if (!string.IsNullOrWhiteSpace(art))
        {
            art = art.Replace("100x100bb.jpg", $"{config.ArtResolution}x{config.ArtResolution}bb.jpg", StringComparison.OrdinalIgnoreCase);
        }

        List<string> albumArtists;
        if (!string.IsNullOrWhiteSpace(CollectionArtistName))
        {
            albumArtists = new List<string> { CollectionArtistName };
        }
        else if (!string.IsNullOrWhiteSpace(ArtistName))
        {
            albumArtists = new List<string> { ArtistName };
        }
        else
        {
            albumArtists = new List<string>();
        }

        return new ItunesTrackInfo
        {
            Title = resolvedTitle,
            Artists = !string.IsNullOrWhiteSpace(ArtistName) ? new List<string> { ArtistName } : new List<string>(),
            AlbumArtists = albumArtists,
            Album = !string.IsNullOrWhiteSpace(CollectionName) ? CollectionName : CollectionCensoredName,
            Url = TrackViewUrl ?? CollectionViewUrl ?? string.Empty,
            TrackId = TrackId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            ReleaseId = CollectionId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            ArtistId = ArtistId?.ToString(CultureInfo.InvariantCulture)
                ?? CollectionArtistId?.ToString(CultureInfo.InvariantCulture)
                ?? string.Empty,
            Duration = TrackTimeMillis.HasValue ? TimeSpan.FromMilliseconds(TrackTimeMillis.Value) : TimeSpan.Zero,
            Genres = !string.IsNullOrWhiteSpace(PrimaryGenreName) ? new List<string> { PrimaryGenreName } : new List<string>(),
            ReleaseDate = releaseDate,
            TrackNumber = TrackNumber,
            TrackTotal = TrackCount,
            DiscNumber = DiscNumber,
            Isrc = Isrc,
            Label = string.IsNullOrWhiteSpace(RecordLabel) ? null : RecordLabel,
            Explicit = ParseExplicitness(TrackExplicitness, CollectionExplicitness),
            Art = art
        };
    }

    private string? ResolveTitle()
    {
        if (!string.IsNullOrWhiteSpace(TrackName))
        {
            return TrackName;
        }

        return string.IsNullOrWhiteSpace(TrackCensoredName) ? null : TrackCensoredName;
    }

    private static bool? ParseExplicitness(string? trackExplicitness, string? collectionExplicitness)
    {
        if (string.Equals(trackExplicitness, "explicit", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(collectionExplicitness, "explicit", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(trackExplicitness, "notExplicit", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(collectionExplicitness, "notExplicit", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return null;
    }

    private static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 10)
        {
            return null;
        }

        var slice = value[..10];
        return DateTime.TryParseExact(slice, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : (DateTime?)null;
    }
}

public sealed class ItunesMatchConfig
{
    [JsonPropertyName("art_resolution")]
    public int ArtResolution { get; set; } = 1000;

    [JsonPropertyName("animated_artwork")]
    public bool? AnimatedArtwork { get; set; }

    [JsonPropertyName("country")]
    public string Country { get; set; } = "us";

    [JsonPropertyName("search_limit")]
    public int SearchLimit { get; set; } = 25;

    [JsonPropertyName("match_by_id")]
    public bool MatchById { get; set; } = true;
}

public sealed class ItunesTrackInfo
{
    public string Title { get; set; } = "";
    public List<string> Artists { get; set; } = new();
    public List<string> AlbumArtists { get; set; } = new();
    public string? Album { get; set; }
    public string Url { get; set; } = "";
    public string TrackId { get; set; } = "";
    public string ReleaseId { get; set; } = "";
    public string ArtistId { get; set; } = "";
    public TimeSpan Duration { get; set; }
    public List<string> Genres { get; set; } = new();
    public DateTime? ReleaseDate { get; set; }
    public int? TrackNumber { get; set; }
    public ushort? TrackTotal { get; set; }
    public short? DiscNumber { get; set; }
    public string? Isrc { get; set; }
    public string? Label { get; set; }
    public bool? Explicit { get; set; }
    public string? Art { get; set; }
}
