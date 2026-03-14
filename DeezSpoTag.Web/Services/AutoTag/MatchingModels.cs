using DeezSpoTag.Core.Models;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class AutoTagAudioInfo
{
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public List<string> Artists { get; set; } = new();
    public string? Album { get; set; }
    public int? DurationSeconds { get; set; }
    public string? Isrc { get; set; }
    public int? TrackNumber { get; set; }
    public Dictionary<string, List<string>> Tags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool HasEmbeddedTitle { get; set; }
    public bool HasEmbeddedArtist { get; set; }
}

public sealed class AutoTagMatchingConfig
{
    public bool MatchDuration { get; set; }
    public int MaxDurationDifferenceSeconds { get; set; } = 30;
    public double Strictness { get; set; } = 0.7;
    public MultipleMatchesSort MultipleMatches { get; set; } = MultipleMatchesSort.Default;
}

public sealed class AutoTagMatchResult
{
    public double Accuracy { get; set; }
    public AutoTagTrack Track { get; set; } = new();
}

public sealed class AutoTagTrack : AudioFeaturesBase
{
    public string Title { get; set; } = "";
    public List<string> Artists { get; set; } = new();
    public List<string> AlbumArtists { get; set; } = new();
    public string? Album { get; set; }
    public string? Url { get; set; }
    public string? TrackId { get; set; }
    public string? ReleaseId { get; set; }
    public TimeSpan? Duration { get; set; }
    public int? TrackNumber { get; set; }
    public int? TrackTotal { get; set; }
    public int? DiscNumber { get; set; }
    public string? Isrc { get; set; }
    public string? Label { get; set; }
    public string? CatalogNumber { get; set; }
    public List<string> Genres { get; set; } = new();
    public List<string> Styles { get; set; } = new();
    public string? Art { get; set; }
    public long? Bpm { get; set; }
    public string? Key { get; set; }
    public string? Version { get; set; }
    public List<string> Remixers { get; set; } = new();
    public string? Mood { get; set; }
    public bool? Explicit { get; set; }
    public DateTime? ReleaseDate { get; set; }
    public DateTime? PublishDate { get; set; }
    public Dictionary<string, List<string>> Other { get; set; } = new();
}
