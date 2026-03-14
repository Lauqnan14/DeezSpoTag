using System.Globalization;
using System.Text.Json.Serialization;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class BeatsourceSearchResponse
{
    public int Count { get; set; }
    public List<BeatsourceTrack> Tracks { get; set; } = new();
}

public sealed class BeatsourceTrack
{
    public List<BeatsourceSmall> Artists { get; set; } = new();
    public long? Bpm { get; set; }
    [JsonPropertyName("catalog_number")]
    public string? CatalogNumber { get; set; }
    public BeatsourceSmall? Genre { get; set; }
    public long Id { get; set; }
    public string? Isrc { get; set; }
    public BeatsourceKey? Key { get; set; }
    [JsonPropertyName("length_ms")]
    public long? LengthMs { get; set; }
    [JsonPropertyName("mix_name")]
    public string? MixName { get; set; }
    public string? Name { get; set; }
    [JsonPropertyName("publish_date")]
    public string? PublishDate { get; set; }
    public BeatsourceRelease? Release { get; set; }
    public List<BeatsourceSmall> Remixers { get; set; } = new();
    public string? Slug { get; set; }

    public BeatsourceTrackInfo ToTrackInfo(BeatsourceMatchConfig config)
    {
        var releaseId = Release?.Id.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        var releaseName = Release?.Name;
        var releaseLabel = Release?.Label?.Name;
        var art = Release?.Image?.DynamicUri;
        if (!string.IsNullOrWhiteSpace(art))
        {
            art = art.Replace("{w}", config.ArtResolution.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
                     .Replace("{h}", config.ArtResolution.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        }

        var slug = Slug ?? string.Empty;
        var publishDate = ParseDate(PublishDate);

        return new BeatsourceTrackInfo
        {
            Title = Name ?? string.Empty,
            Version = MixName,
            Artists = Artists.Select(a => a.Name).ToList(),
            Album = releaseName,
            Key = Key?.Name.Replace("Major", "", StringComparison.OrdinalIgnoreCase)
                .Replace("Minor", "m", StringComparison.OrdinalIgnoreCase)
                .Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim(),
            Bpm = Bpm,
            Genres = Genre != null ? new List<string> { Genre.Name } : new List<string>(),
            Art = art,
            Url = $"https://beatsource.com/track/{slug}/{Id}",
            Label = releaseLabel,
            CatalogNumber = CatalogNumber,
            TrackId = Id.ToString(CultureInfo.InvariantCulture),
            ReleaseId = releaseId,
            Duration = LengthMs.HasValue ? TimeSpan.FromMilliseconds(LengthMs.Value) : TimeSpan.Zero,
            Remixers = Remixers.Select(r => r.Name).ToList(),
            ReleaseDate = publishDate,
            Isrc = Isrc
        };
    }

    private static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : (DateTime?)null;
    }
}

public sealed class BeatsourceSmall
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
}

public sealed class BeatsourceKey
{
    public string Name { get; set; } = "";
    public long Id { get; set; }
}

public sealed class BeatsourceRelease
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public BeatsourceImage? Image { get; set; }
    public BeatsourceSmall Label { get; set; } = new();
}

public sealed class BeatsourceImage
{
    public long Id { get; set; }
    [JsonPropertyName("dynamic_uri")]
    public string DynamicUri { get; set; } = "";
    public string Uri { get; set; } = "";
}

public sealed class BeatsourceMatchConfig
{
    public int ArtResolution { get; set; } = 500;
}

public sealed class BeatsourceTrackInfo
{
    public string Title { get; set; } = "";
    public string? Version { get; set; }
    public List<string> Artists { get; set; } = new();
    public string? Album { get; set; }
    public string? Key { get; set; }
    public long? Bpm { get; set; }
    public List<string> Genres { get; set; } = new();
    public string? Art { get; set; }
    public string Url { get; set; } = "";
    public string? Label { get; set; }
    public string? CatalogNumber { get; set; }
    public string TrackId { get; set; } = "";
    public string ReleaseId { get; set; } = "";
    public TimeSpan Duration { get; set; }
    public List<string> Remixers { get; set; } = new();
    public DateTime? ReleaseDate { get; set; }
    public string? Isrc { get; set; }
}
