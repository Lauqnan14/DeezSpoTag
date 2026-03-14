namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class DiscogsPlatform : AutoTagPlatformBase
{
    public DiscogsPlatform(IWebHostEnvironment environment) : base(environment) { }

    public override AutoTagPlatformDescriptor Describe()
    {
        var info = new PlatformInfo
        {
            Id = "discogs",
            Name = "Discogs",
            Description = "Slow due rate limits (~25 tracks / min) & requires a free account",
            Version = "1.0.0",
            MaxThreads = 1,
            RequiresAuth = true,
            SupportedTags = new List<SupportedTag>
            {
                SupportedTag.Title,
                SupportedTag.Artist,
                SupportedTag.AlbumArtist,
                SupportedTag.Album,
                SupportedTag.Genre,
                SupportedTag.Style,
                SupportedTag.AlbumArt,
                SupportedTag.URL,
                SupportedTag.Label,
                SupportedTag.ReleaseDate,
                SupportedTag.CatalogNumber,
                SupportedTag.ReleaseId,
                SupportedTag.Duration,
                SupportedTag.TrackNumber,
                SupportedTag.DiscNumber,
                SupportedTag.TrackTotal,
                SupportedTag.OtherTags
            },
            CustomOptions = new PlatformCustomOptions
            {
                Options = new List<PlatformCustomOption>
                {
                    new()
                    {
                        Id = "max_albums",
                        Label = "Max albums to check",
                        Tooltip = "How many albums in search results to check. Due to rate limiting this increases tagging time by a lot",
                        Value = new PlatformCustomOptionNumber { Min = 1, Max = 16, Step = 1, Value = 4 }
                    },
                    new()
                    {
                        Id = "track_number_int",
                        Label = "Write track number as number, rather than Discogs's format",
                        Value = new PlatformCustomOptionBoolean { Value = false }
                    }
                }
            }
        };

        return CreateDescriptor(info, "discogs.png");
    }
}
