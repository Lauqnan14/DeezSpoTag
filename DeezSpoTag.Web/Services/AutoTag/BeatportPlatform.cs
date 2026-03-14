namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class BeatportPlatform : AutoTagPlatformBase
{
    public BeatportPlatform(IWebHostEnvironment environment) : base(environment) { }

    public override AutoTagPlatformDescriptor Describe()
    {
        var info = new PlatformInfo
        {
            Id = "beatport",
            Name = "Beatport",
            Description = "Overall more specialized in Techno",
            Version = "1.0.0",
            MaxThreads = 0,
            RequiresAuth = false,
            SupportedTags = new List<SupportedTag>
            {
                SupportedTag.Title,
                SupportedTag.Version,
                SupportedTag.Artist,
                SupportedTag.AlbumArtist,
                SupportedTag.Album,
                SupportedTag.BPM,
                SupportedTag.Genre,
                SupportedTag.Style,
                SupportedTag.Label,
                SupportedTag.URL,
                SupportedTag.ReleaseDate,
                SupportedTag.PublishDate,
                SupportedTag.Key,
                SupportedTag.AlbumArt,
                SupportedTag.OtherTags,
                SupportedTag.TrackId,
                SupportedTag.ReleaseId,
                SupportedTag.Duration,
                SupportedTag.Remixer,
                SupportedTag.CatalogNumber,
                SupportedTag.TrackTotal,
                SupportedTag.ISRC,
                SupportedTag.TrackNumber
            },
            CustomOptions = new PlatformCustomOptions
            {
                Options = new List<PlatformCustomOption>
                {
                    new()
                    {
                        Id = "art_resolution",
                        Label = "Album art resolution",
                        Value = new PlatformCustomOptionNumber { Min = 200, Max = 1600, Step = 100, Value = 500 }
                    },
                    new()
                    {
                        Id = "max_pages",
                        Label = "Max pages",
                        Tooltip = "How many pages of search results to scan for tracks",
                        Value = new PlatformCustomOptionNumber { Min = 1, Max = 10, Step = 1, Value = 1 }
                    },
                    new()
                    {
                        Id = "ignore_version",
                        Label = "Ignore version when matching",
                        Tooltip = "Ignores (Extended Mix), (Original Mix) and such",
                        Value = new PlatformCustomOptionBoolean { Value = false }
                    }
                }
            }
        };

        return CreateDescriptor(info, "beatport.png");
    }
}
