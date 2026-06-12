namespace DeezSpoTag.Services.Download.Queue;

public abstract class DownloadIdentityLookupRequest
{
    public string? Isrc { get; init; }
    public string? DeezerTrackId { get; init; }
    public string? DeezerAlbumId { get; init; }
    public string? DeezerArtistId { get; init; }
    public string? SpotifyTrackId { get; init; }
    public string? SpotifyAlbumId { get; init; }
    public string? SpotifyArtistId { get; init; }
    public string? AppleTrackId { get; init; }
    public string? AppleAlbumId { get; init; }
    public string? AppleArtistId { get; init; }
    public string? QobuzTrackId { get; init; }
    public string? TidalTrackId { get; init; }
    public string? AmazonTrackId { get; init; }
    public string TrackTitle { get; init; } = string.Empty;
    public int? DurationMs { get; init; }
    public long? DestinationFolderId { get; init; }
    public string? ContentType { get; init; }
}
