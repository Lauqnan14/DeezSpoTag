namespace DeezSpoTag.Core.Models;

public enum ArtistMetadataRole { Unknown, Main, Featured, Additional }
public enum LanguageMetadataScope { Unknown, Artist, Track, Lyrics }

public sealed record LanguageMetadataEvidence(
    IReadOnlyList<string> Values, LanguageMetadataScope Scope, string Source,
    string? SourceReference, DateTimeOffset KnownAt);

public sealed record ArtistEnrichmentLocation(
    string? Country, string? City, string? Region, string? CountryCode,
    string Source, string? SourceReference, DateTimeOffset? RetrievedAt,
    DateTimeOffset? KnownAt, string? ResolutionMethod, string? LocationMeaning)
{
    public string? Hometown { get; init; }
}

public sealed record ArtistEnrichmentMetadata(
    long? ArtistId, string ArtistName, ArtistMetadataRole ArtistRole,
    string BindingSource, string BindingReference, string? ProviderArtistId,
    ArtistEnrichmentLocation? Location, IReadOnlyList<LanguageMetadataEvidence> Languages);

/// <summary>Semantic field names shared by collection, writing and scanning.</summary>
public static class ArtistEnrichmentFields
{
    public const string CountryRawName = "ARTISTCOUNTRY";
    public const string CityRawName = "ARTISTCITY";
    public const string RegionRawName = "ARTISTREGION";
    public const string ArtistLanguageRawName = "ARTISTLANGUAGE";
    public const string TrackLanguageRawName = "LANGUAGE";

    public static readonly IReadOnlyDictionary<string, string> RawNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["artistCountry"] = CountryRawName, ["artistCity"] = CityRawName,
            ["artistRegion"] = RegionRawName, ["artistLanguage"] = ArtistLanguageRawName,
            ["language"] = TrackLanguageRawName
        };
}
