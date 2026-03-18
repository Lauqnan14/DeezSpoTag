namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class SpotifyPlatform : AutoTagPlatformBase
{
    public SpotifyPlatform(IWebHostEnvironment environment) : base(environment) { }

    public override AutoTagPlatformDescriptor Describe()
    {
        var supportedTags = SharedDownloadParityTags();

        var info = new PlatformInfo
        {
            Id = "spotify",
            Name = "Spotify",
            Description = "Requires a free account",
            Version = "1.0.0",
            MaxThreads = 1,
            RequiresAuth = true,
            DownloadTags = new List<string>
            {
                "title",
                "artist",
                "artists",
                "album",
                "albumArtist",
                "trackNumber",
                "trackTotal",
                "discNumber",
                "genre",
                "year",
                "date",
                "explicit",
                "isrc",
                "length",
                "label",
                "cover",
                "source",
                "url",
                "trackId",
                "releaseId"
            },
            SupportedTags = supportedTags
        };

        return CreateDescriptor(info, "spotify.png");
    }

    internal static List<SupportedTag> SharedDownloadParityTags()
    {
        return new List<SupportedTag>
        {
            SupportedTag.Title,
            SupportedTag.Artist,
            SupportedTag.AlbumArtist,
            SupportedTag.Album,
            SupportedTag.AlbumArt,
            SupportedTag.URL,
            SupportedTag.TrackId,
            SupportedTag.ReleaseId,
            SupportedTag.Duration,
            SupportedTag.BPM,
            SupportedTag.TrackNumber,
            SupportedTag.TrackTotal,
            SupportedTag.DiscNumber,
            SupportedTag.ISRC,
            SupportedTag.ReleaseDate,
            SupportedTag.Genre,
            SupportedTag.Label,
            SupportedTag.Explicit,
            SupportedTag.UnsyncedLyrics,
            SupportedTag.SyncedLyrics
        };
    }
}
