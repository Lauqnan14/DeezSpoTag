using System.Text.Json.Serialization;

namespace DeezSpoTag.Web.Services.AutoTag;

public enum SupportedTag
{
    Title,
    Artist,
    Album,
    Key,
    Genre,
    Style,
    ReleaseDate,
    PublishDate,
    AlbumArt,
    OtherTags,
    CatalogNumber,
    TrackId,
    ReleaseId,
    Version,
    Duration,
    AlbumArtist,
    Remixer,
    TrackNumber,
    TrackTotal,
    DiscNumber,
    Mood,
    SyncedLyrics,
    TtmlLyrics,
    UnsyncedLyrics,
    Label,
    Explicit,
    MetaTags,
    [JsonPropertyName("bpm")]
    BPM,
    Danceability,
    Energy,
    Valence,
    Acousticness,
    Instrumentalness,
    Speechiness,
    Loudness,
    Tempo,
    TimeSignature,
    Liveness,
    [JsonPropertyName("url")]
    URL,
    [JsonPropertyName("isrc")]
    ISRC
}

public sealed class PlatformInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "";
    public ushort MaxThreads { get; set; }
    public PlatformCustomOptions CustomOptions { get; set; } = new();
    public List<SupportedTag> SupportedTags { get; set; } = new();
    public List<string> DownloadTags { get; set; } = new();
    public bool RequiresAuth { get; set; }
}

public sealed class PlatformCustomOptions
{
    public List<PlatformCustomOption> Options { get; set; } = new();
}

public sealed class PlatformCustomOption
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public PlatformCustomOptionValue? Value { get; set; }
    public string? Tooltip { get; set; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PlatformCustomOptionBoolean), "boolean")]
[JsonDerivedType(typeof(PlatformCustomOptionNumber), "number")]
[JsonDerivedType(typeof(PlatformCustomOptionString), "string")]
[JsonDerivedType(typeof(PlatformCustomOptionTag), "tag")]
[JsonDerivedType(typeof(PlatformCustomOptionSelect), "option")]
public abstract class PlatformCustomOptionValue
{
}

public sealed class PlatformCustomOptionBoolean : PlatformCustomOptionValue
{
    public bool Value { get; set; }
}

public sealed class PlatformCustomOptionNumber : PlatformCustomOptionValue
{
    public int Min { get; set; }
    public int Max { get; set; }
    public int Step { get; set; }
    public int Value { get; set; }
    public bool Slider { get; set; }
}

public sealed class PlatformCustomOptionString : PlatformCustomOptionValue
{
    public string Value { get; set; } = "";
    public bool? Hidden { get; set; }
}

public sealed class PlatformCustomOptionTag : PlatformCustomOptionValue
{
    public string Value { get; set; } = "";
}

public sealed class PlatformCustomOptionSelect : PlatformCustomOptionValue
{
    public List<string> Values { get; set; } = new();
    public string Value { get; set; } = "";
}

public sealed class AutoTagPlatformDescriptor
{
    public string Id { get; set; } = "";
    public bool BuiltIn { get; set; }
    public PlatformInfo Platform { get; set; } = new();
    public string Icon { get; set; } = "";
    public bool RequiresAuth { get; set; }
    public List<SupportedTag> SupportedTags { get; set; } = new();
    public List<string> DownloadTags { get; set; } = new();
}
