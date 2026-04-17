namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class BoomplayPlatform : AutoTagPlatformBase
{
    public BoomplayPlatform(IWebHostEnvironment environment) : base(environment) { }

    public override AutoTagPlatformDescriptor Describe()
    {
        var info = new PlatformInfo
        {
            Id = "boomplay",
            Name = "Boomplay",
            Description = "Metadata from Boomplay tracks",
            Version = "1.0.0",
            MaxThreads = 1,
            RequiresAuth = false,
            DownloadTags = new List<string>
            {
                "title",
                "artist",
                "albumArtist",
                "album",
                "trackNumber",
                "discNumber",
                "genre",
                "date",
                "isrc",
                "length",
                "cover",
                "label",
                "bpm",
                "key",
                "composer",
                "language",
                "source",
                "url",
                "trackId"
            },
            SupportedTags = new List<SupportedTag>
            {
                SupportedTag.Title,
                SupportedTag.Artist,
                SupportedTag.AlbumArtist,
                SupportedTag.Album,
                SupportedTag.AlbumArt,
                SupportedTag.URL,
                SupportedTag.TrackId,
                SupportedTag.Duration,
                SupportedTag.TrackNumber,
                SupportedTag.DiscNumber,
                SupportedTag.ISRC,
                SupportedTag.ReleaseDate,
                SupportedTag.Genre,
                SupportedTag.Label,
                SupportedTag.BPM,
                SupportedTag.Key
            },
            CustomOptions = new PlatformCustomOptions
            {
                Options = new List<PlatformCustomOption>
                {
                    new()
                    {
                        Id = "match_by_id",
                        Label = "Match by existing Boomplay ID/URL tag first",
                        Value = new PlatformCustomOptionBoolean { Value = true }
                    },
                    new()
                    {
                        Id = "search_limit",
                        Label = "Search candidates to evaluate",
                        Value = new PlatformCustomOptionNumber { Min = 5, Max = 30, Step = 1, Value = 12, Slider = true }
                    }
                }
            }
        };

        return CreateDescriptor(info, "boomplay.png");
    }
}
