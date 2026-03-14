namespace DeezSpoTag.Core.Models.Deezer;

/// <summary>
/// API options for Deezer requests (ported from deezer-sdk APIOptions interface)
/// </summary>
public class ApiOptions
{
    public int? Index { get; set; }
    public int? Limit { get; set; }
    public int? Start { get; set; }
    public bool? Strict { get; set; }
    public string? Order { get; set; }
}

/// <summary>
/// Search order constants (ported from deezer-sdk SearchOrder)
/// </summary>
public static class SearchOrder
{
    public const string Ranking = "RANKING";
    public const string TrackAsc = "TRACK_ASC";
    public const string TrackDesc = "TRACK_DESC";
    public const string ArtistAsc = "ARTIST_ASC";
    public const string ArtistDesc = "ARTIST_DESC";
    public const string AlbumAsc = "ALBUM_ASC";
    public const string AlbumDesc = "ALBUM_DESC";
    public const string RatingAsc = "RATING_ASC";
    public const string RatingDesc = "RATING_DESC";
    public const string DurationAsc = "DURATION_ASC";
    public const string DurationDesc = "DURATION_DESC";
}