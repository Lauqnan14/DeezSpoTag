namespace DeezSpoTag.Services.Download.Utils;

public sealed class PlatformLinkResult
{
    public string? TidalUrl { get; set; }
    public string? AmazonUrl { get; set; }
    public string? QobuzUrl { get; set; }
    public string? DeezerUrl { get; set; }
    public string? DeezerId { get; set; }
    public string? AppleMusicUrl { get; set; }
    public string? SpotifyUrl { get; set; }
    public string? SpotifyId { get; set; }
    public string? YouTubeUrl { get; set; }
    public string? YouTubeId { get; set; }
    public string? Isrc { get; set; }
    public string? SourceType { get; set; }
    public string? SourceTitle { get; set; }
    public string? SourceArtist { get; set; }

    public bool HasAnyResolvedLink()
    {
        return !string.IsNullOrWhiteSpace(DeezerId)
               || !string.IsNullOrWhiteSpace(DeezerUrl)
               || !string.IsNullOrWhiteSpace(SpotifyId)
               || !string.IsNullOrWhiteSpace(SpotifyUrl)
               || !string.IsNullOrWhiteSpace(TidalUrl)
               || !string.IsNullOrWhiteSpace(QobuzUrl)
               || !string.IsNullOrWhiteSpace(AppleMusicUrl)
               || !string.IsNullOrWhiteSpace(AmazonUrl)
               || !string.IsNullOrWhiteSpace(YouTubeUrl);
    }
}
