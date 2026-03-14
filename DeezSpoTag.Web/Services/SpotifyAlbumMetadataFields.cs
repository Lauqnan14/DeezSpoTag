namespace DeezSpoTag.Web.Services;

public abstract record SpotifyAlbumMetadataFields
{
    public IReadOnlyList<string>? Genres { get; init; }
    public string? Label { get; init; }
    public int? Popularity { get; init; }
    public string? ReleaseDatePrecision { get; init; }
    public IReadOnlyList<string>? AvailableMarkets { get; init; }
    public IReadOnlyList<SpotifyCopyrightInfo>? Copyrights { get; init; }
    public string? CopyrightText { get; init; }
    public string? Review { get; init; }
    public IReadOnlyList<string>? RelatedAlbumIds { get; init; }
    public string? OriginalTitle { get; init; }
    public string? VersionTitle { get; init; }
    public IReadOnlyList<SpotifySalePeriod>? SalePeriods { get; init; }
    public IReadOnlyList<SpotifyAvailabilityInfo>? Availability { get; init; }
}
