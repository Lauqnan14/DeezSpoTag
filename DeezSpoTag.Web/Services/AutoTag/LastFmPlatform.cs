namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class LastFmPlatform : AutoTagPlatformBase
{
    public LastFmPlatform(IWebHostEnvironment environment) : base(environment) { }

    public override AutoTagPlatformDescriptor Describe()
    {
        var info = new PlatformInfo
        {
            Id = "lastfm",
            Name = "Last.fm",
            Description = "Good for filling mood/style gaps. Not good as the primary matcher source (weak identity, rate-limited, inconsistent coverage).",
            Version = "1.0.0",
            MaxThreads = 2,
            RequiresAuth = true,
            SupportedTags = new List<SupportedTag>
            {
                SupportedTag.Genre
            },
            CustomOptions = new PlatformCustomOptions
            {
                Options = new List<PlatformCustomOption>
                {
                    new()
                    {
                        Id = "maxTags",
                        Label = "Max tags",
                        Tooltip = "How many top tags to keep from Last.fm (higher = noisier).",
                        Value = new PlatformCustomOptionNumber { Min = 1, Max = 50, Step = 1, Value = 12, Slider = true }
                    }
                }
            }
        };

        return CreateDescriptor(info, "last-fm.png");
    }
}
