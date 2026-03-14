using DeezSpoTag.Core.Models;

namespace DeezSpoTag.Services.Download.Shared.Models;

public sealed class DownloadIntent : MusicKeyAudioFeaturesBase
{
    public string SourceService { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string SpotifyId { get; set; } = "";
    public string DeezerId { get; set; } = "";
    public string DeezerAlbumId { get; set; } = "";
    public string DeezerArtistId { get; set; } = "";
    public string Isrc { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string AlbumArtist { get; set; } = "";
    public string Cover { get; set; } = "";
    public int DurationMs { get; set; }
    public int Position { get; set; }
    public List<string> Genres { get; set; } = new();
    public string Label { get; set; } = "";
    public string Copyright { get; set; } = "";
    public bool? Explicit { get; set; }
    public string Composer { get; set; } = "";
    public string ReleaseDate { get; set; } = "";
    public int TrackNumber { get; set; }
    public int DiscNumber { get; set; }
    public int TrackTotal { get; set; }
    public int DiscTotal { get; set; }
    public string Url { get; set; } = "";
    public string Barcode { get; set; } = "";
    public string PreferredEngine { get; set; } = "";
    public string Quality { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long? DestinationFolderId { get; set; }
    public long? SecondaryDestinationFolderId { get; set; }
    public string AppleId { get; set; } = "";
    public string WatchlistSource { get; set; } = "";
    public string WatchlistPlaylistId { get; set; } = "";
    public string WatchlistTrackId { get; set; } = "";
    public bool HasAtmos { get; set; }
    public bool HasAppleDigitalMaster { get; set; }
    public bool AllowQualityUpgrade { get; set; }
}

public sealed class DownloadIntentResult
{
    public bool Success { get; set; }
    public string Engine { get; set; } = "";
    public string Message { get; set; } = "";
    public List<string> Queued { get; set; } = new();
    public int Skipped { get; set; }
    public List<string> SkipReasonCodes { get; set; } = new();
    public List<string> SkipReasons { get; set; } = new();
}
