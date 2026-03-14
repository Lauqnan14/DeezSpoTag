using System.Text.Json.Serialization;

namespace DeezSpoTag.Web.Controllers.Api;

public abstract class EngineDownloadBatchRequestBase
{
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long? DestinationFolderId { get; set; }
}

public abstract class EngineDownloadTrackDtoBase
{
    public string? AppleId { get; set; }
    public string? SourceUrl { get; set; }
    public string? SpotifyId { get; set; }
    public string? Isrc { get; set; }
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? AlbumArtist { get; set; }
    public string? Cover { get; set; }
    public string? ReleaseDate { get; set; }
    public string? CollectionName { get; set; }
    public string? CollectionType { get; set; }
    public string? QueueOrigin { get; set; }
    public int DurationSeconds { get; set; }
    public int DurationMs { get; set; }
    public int Position { get; set; }
    public int SpotifyTrackNumber { get; set; }
    public int SpotifyDiscNumber { get; set; }
    public int SpotifyTotalTracks { get; set; }
    public bool UseAlbumTrackNumber { get; set; }
}
