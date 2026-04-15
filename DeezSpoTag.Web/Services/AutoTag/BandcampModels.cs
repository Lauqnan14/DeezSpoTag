using System.Globalization;
using System.Text.Json.Serialization;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class BandcampSearchResult
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("album_id")]
    public long? AlbumId { get; set; }

    [JsonPropertyName("band_id")]
    public long BandId { get; set; }

    [JsonPropertyName("band_name")]
    public string BandName { get; set; } = "";

    [JsonPropertyName("album_name")]
    public string? AlbumName { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("item_url_path")]
    public string ItemUrlPath { get; set; } = "";

    [JsonPropertyName("item_url_root")]
    public string ItemUrlRoot { get; set; } = "";

    [JsonPropertyName("img")]
    public string ImageUrl { get; set; } = "";

    [JsonPropertyName("is_label")]
    public bool IsLabel { get; set; }

    public BandcampTrackInfo ToTrackInfo()
    {
        return new BandcampTrackInfo
        {
            Title = Name,
            Artists = new List<string> { BandName },
            Album = AlbumName,
            ReleaseId = AlbumId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            TrackId = Id.ToString(CultureInfo.InvariantCulture),
            Url = ItemUrlPath
        };
    }
}

public sealed class BandcampTrack
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("duration")]
    public string? Duration { get; set; }

    [JsonPropertyName("datePublished")]
    public string DatePublished { get; set; } = "";

    [JsonPropertyName("inAlbum")]
    public BandcampAlbumSmall InAlbum { get; set; } = new();

    [JsonPropertyName("byArtist")]
    public BandcampArtistSmall ByArtist { get; set; } = new();

    [JsonPropertyName("publisher")]
    public BandcampPublisherSmall Publisher { get; set; } = new();

    [JsonPropertyName("keywords")]
    public List<string>? Keywords { get; set; }

    [JsonPropertyName("image")]
    public string Image { get; set; } = "";

    [JsonPropertyName("@id")]
    public string Id { get; set; } = "";

    public DateTime? GetReleaseDate()
    {
        if (string.IsNullOrWhiteSpace(DatePublished))
        {
            return null;
        }

        var slice = DatePublished.Length >= 11 ? DatePublished[..11] : DatePublished;
        return DateTime.TryParseExact(slice, "dd MMM yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : (DateTime?)null;
    }

    public BandcampTrackInfo ToTrackInfo()
    {
        var releaseDate = GetReleaseDate();
        var genre = Publisher.GetGenre();
        var styles = (Keywords ?? new List<string>())
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Where(k => genre == null || !string.Equals(k, genre, StringComparison.OrdinalIgnoreCase))
            .Where(k => BandcampGenres.IsKnownGenre(k))
            .Select(k => BandcampText.CapitalizeWords(k.Replace(" and ", " & ", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var albumArtist = InAlbum.ByArtist?.Name ?? ByArtist.Name;

        return new BandcampTrackInfo
        {
            Title = Name,
            Album = InAlbum.Name,
            Artists = new List<string> { albumArtist },
            AlbumArtists = new List<string> { albumArtist },
            Label = Publisher.Name,
            Genres = genre != null ? new List<string> { genre } : new List<string>(),
            Styles = styles,
            Art = Image,
            TrackId = Id,
            Url = Id,
            ReleaseId = InAlbum.Id ?? string.Empty,
            TrackTotal = InAlbum.NumTracks,
            ReleaseDate = releaseDate,
            ReleaseYear = releaseDate?.Year
        };
    }


}

public sealed class BandcampAlbumSmall
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("numTracks")]
    public int? NumTracks { get; set; }

    [JsonPropertyName("@id")]
    public string? Id { get; set; }

    [JsonPropertyName("byArtist")]
    public BandcampArtistSmall? ByArtist { get; set; }
}

public sealed class BandcampArtistSmall
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

public sealed class BandcampPublisherSmall
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("genre")]
    public string? Genre { get; set; }

    public string? GetGenre()
    {
        if (string.IsNullOrWhiteSpace(Genre))
        {
            return null;
        }

        var last = Genre.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(last))
        {
            return null;
        }

        return BandcampText.CapitalizeWords(last);
    }
}

public sealed class BandcampTrackInfo
{
    public string Title { get; set; } = "";
    public List<string> Artists { get; set; } = new();
    public List<string> AlbumArtists { get; set; } = new();
    public string? Album { get; set; }
    public string? Label { get; set; }
    public List<string> Genres { get; set; } = new();
    public List<string> Styles { get; set; } = new();
    public string? Art { get; set; }
    public string Url { get; set; } = "";
    public string TrackId { get; set; } = "";
    public string ReleaseId { get; set; } = "";
    public int? TrackTotal { get; set; }
    public DateTime? ReleaseDate { get; set; }
    public int? ReleaseYear { get; set; }
}

public static class BandcampText
{
    public static string CapitalizeWords(string input)
    {
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return string.Empty;
        }

        for (var i = 0; i < parts.Length; i++)
        {
            var word = parts[i];
            if (string.IsNullOrEmpty(word))
            {
                continue;
            }

            var first = char.ToUpperInvariant(word[0]);
            var rest = word.Length > 1 ? word[1..] : string.Empty;
            parts[i] = first + rest;
        }

        return string.Join(' ', parts);
    }
}
