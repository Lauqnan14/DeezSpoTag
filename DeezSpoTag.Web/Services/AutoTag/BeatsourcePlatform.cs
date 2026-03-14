namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class BeatsourcePlatform : AutoTagPlatformBase
{
    public BeatsourcePlatform(IWebHostEnvironment environment) : base(environment) { }

    public override AutoTagPlatformDescriptor Describe()
    {
        var info = new PlatformInfo
        {
            Id = "beatsource",
            Name = "Beatsource",
            Description = "Overall more specialized in open-format (Hip Hop/Latin/Dancehall)",
            Version = "1.0.0",
            MaxThreads = 0,
            RequiresAuth = false,
            SupportedTags = new List<SupportedTag>
            {
                SupportedTag.Title,
                SupportedTag.Version,
                SupportedTag.Artist,
                SupportedTag.Album,
                SupportedTag.Key,
                SupportedTag.BPM,
                SupportedTag.Genre,
                SupportedTag.AlbumArt,
                SupportedTag.URL,
                SupportedTag.Label,
                SupportedTag.CatalogNumber,
                SupportedTag.TrackId,
                SupportedTag.ReleaseId,
                SupportedTag.Duration,
                SupportedTag.Remixer,
                SupportedTag.ReleaseDate,
                SupportedTag.ISRC
            },
            CustomOptions = new PlatformCustomOptions
            {
                Options = new List<PlatformCustomOption>
                {
                    new()
                    {
                        Id = "art_resolution",
                        Label = "Album art resolution",
                        Tooltip = "Select album art resolution",
                        Value = new PlatformCustomOptionNumber { Min = 100, Max = 1600, Step = 100, Value = 500 }
                    }
                }
            }
        };

        return CreateDescriptor(info, "beatsource.png");
    }
}
