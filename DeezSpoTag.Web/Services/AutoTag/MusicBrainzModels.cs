using System.Text.Json.Serialization;

namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class RecordingSearchResults
{
    public int Count { get; set; }
    public int Offset { get; set; }
    public List<Recording> Recordings { get; set; } = new();
}

public sealed class Recording
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public long? Length { get; set; }
    [JsonPropertyName("artist-credit")]
    public List<ArtistCredit>? ArtistCredit { get; set; }
    [JsonPropertyName("first-release-date")]
    public string? FirstReleaseDate { get; set; }
    public List<ReleaseSmall>? Releases { get; set; }
    public List<string>? Isrcs { get; set; }
}

public sealed class ReleaseSmall
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    [JsonPropertyName("artist-credit")]
    public List<ArtistCredit>? ArtistCredit { get; set; }
    [JsonPropertyName("release-group")]
    public ReleaseGroup ReleaseGroup { get; set; } = new();
    public string? Date { get; set; }
    public string? Status { get; set; }
    public string? Country { get; set; }
}

public sealed class Release
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    [JsonPropertyName("artist-credit")]
    public List<ArtistCredit>? ArtistCredit { get; set; }
    public string? Date { get; set; }
    public string? Barcode { get; set; }
    public string? Status { get; set; }
    public string? Quality { get; set; }
    public List<Genre> Genres { get; set; } = new();
    [JsonPropertyName("label-info")]
    public List<LabelInfo>? LabelInfo { get; set; }
    public List<ReleaseMedia> Media { get; set; } = new();
    [JsonPropertyName("cover-art-archive")]
    public CoverArtArchive CoverArtArchive { get; set; } = new();
    [JsonPropertyName("release-group")]
    public ReleaseGroup? ReleaseGroup { get; set; }
    public string? Country { get; set; }
}

public sealed class CoverArtArchive
{
    public bool Back { get; set; }
    public bool Front { get; set; }
    public bool Artwork { get; set; }
    public int Count { get; set; }
}

public sealed class ReleaseMedia
{
    public int? Position { get; set; }
    public string? Format { get; set; }
    [JsonPropertyName("track-count")]
    public int? TrackCount { get; set; }
    public List<MusicBrainzTrack> Tracks { get; set; } = new();
}

public sealed class MusicBrainzTrack
{
    public string Id { get; set; } = "";
    public int Position { get; set; }
    public string? Number { get; set; }
    public long? Length { get; set; }
    public Recording Recording { get; set; } = new();

    public string Platform { get; set; } = "musicbrainz";
    public string Title { get; set; } = "";
    public List<string> Artists { get; set; } = new();
    public List<string> AlbumArtists { get; set; } = new();
    public string? Album { get; set; }
    public string Url { get; set; } = "";
    public string? TrackId { get; set; }
    public string ReleaseId { get; set; } = "";
    public TimeSpan Duration { get; set; }
    public int? ReleaseYear { get; set; }
    public DateTime? ReleaseDate { get; set; }
    public string? Isrc { get; set; }
    public string? Label { get; set; }
    public string? CatalogNumber { get; set; }
    public int? TrackNumber { get; set; }
    public int? TrackTotal { get; set; }
    public int? DiscNumber { get; set; }
    public List<string> Genres { get; set; } = new();
    public string? Art { get; set; }
    public List<(string Key, List<string> Values)> Other { get; set; } = new();
}

public sealed class Genre
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class ArtistCredit
{
    public string Name { get; set; } = "";
    public Artist Artist { get; set; } = new();
}

public sealed class Artist
{
    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
}

public sealed class ReleaseGroup
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    [JsonPropertyName("primary-type")]
    public string? PrimaryType { get; set; }
    [JsonPropertyName("secondary-types")]
    public List<string>? SecondaryTypes { get; set; }
}


public sealed class LabelInfo
{
    [JsonPropertyName("catalog-number")]
    public string? CatalogNumber { get; set; }
    public Label? Label { get; set; }
}

public sealed class Label
{
    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
}

public sealed class BrowseReleases
{
    [JsonPropertyName("release-offset")]
    public int ReleaseOffset { get; set; }
    [JsonPropertyName("release-count")]
    public int ReleaseCount { get; set; }
    public List<Release> Releases { get; set; } = new();
}

public sealed class MusicBrainzMatchConfig
{
    [JsonPropertyName("search_limit")]
    public int SearchLimit { get; set; } = 25;

    [JsonPropertyName("use_isrc_first")]
    public bool UseIsrcFirst { get; set; } = true;

    [JsonPropertyName("match_by_id")]
    public bool MatchById { get; set; } = true;

    [JsonPropertyName("prefer_official")]
    public bool PreferOfficial { get; set; } = true;

    [JsonPropertyName("exclude_compilations")]
    public bool ExcludeCompilations { get; set; } = true;

    [JsonPropertyName("preferred_primary_type")]
    public string PreferredPrimaryType { get; set; } = "Any";

    [JsonPropertyName("preferred_release_countries")]
    public string PreferredReleaseCountries { get; set; } = "";

    [JsonPropertyName("preferred_media_formats")]
    public string PreferredMediaFormats { get; set; } = "Digital Media,CD";

    [JsonPropertyName("prefer_release_year")]
    public bool PreferReleaseYear { get; set; } = true;

    [JsonPropertyName("official_weight")]
    public int OfficialWeight { get; set; } = 10;

    [JsonPropertyName("compilation_penalty_weight")]
    public int CompilationPenaltyWeight { get; set; } = 20;

    [JsonPropertyName("primary_type_weight")]
    public int PrimaryTypeWeight { get; set; } = 7;

    [JsonPropertyName("country_weight")]
    public int CountryWeight { get; set; } = 3;

    [JsonPropertyName("format_weight")]
    public int FormatWeight { get; set; } = 2;

    [JsonPropertyName("year_weight")]
    public int YearWeight { get; set; } = 1;
}
