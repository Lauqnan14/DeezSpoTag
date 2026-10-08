using DeezSpoTag.Core.Models;

namespace DeezSpoTag.Web.Services.ArtistLocation;

/// <summary>
/// An artist's location, in the shape the library page already consumes.
/// </summary>
/// <remarks>
/// Keeps the existing fields and adds separate region and hometown information. The
/// existing JSON fields remain compatible: <c>city</c>, <c>country</c>,
/// <c>country_code</c> and <c>source</c>.
///
/// <para>
/// <see cref="CountryCode"/> is ISO 3166-1 alpha-2. It is not always derived: the
/// MusicBrainz source supplies an already-validated code, while the Audiomack
/// source has to infer one from a free-text hometown. Callers must therefore
/// treat a null code as normal rather than as missing data.
/// </para>
/// </remarks>
public sealed record ArtistLocationResult(
    string? RawLocation,
    string? City,
    string? Country,
    string? CountryCode,
    string Source)
{
    public long? ArtistId { get; init; }
    public string? ArtistName { get; init; }
    public ArtistMetadataRole ArtistRole { get; init; } = ArtistMetadataRole.Unknown;
    public string? SourceReference { get; init; }
    public DateTimeOffset? RetrievedAt { get; init; }
    public DateTimeOffset? KnownAt { get; init; }
    public string? ResolutionMethod { get; init; }
    public string? LocationMeaning { get; init; }
    public string? Region { get; init; }
    public string? Hometown { get; init; }
}
