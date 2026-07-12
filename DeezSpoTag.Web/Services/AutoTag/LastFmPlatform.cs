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
                SupportedTag.Genre,
                SupportedTag.Style,
                SupportedTag.Mood
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
                    },
                    new()
                    {
                        Id = "minTagCount",
                        Label = "Minimum tag weight",
                        Tooltip = "Reject weak community tags below this Last.fm weight.",
                        Value = new PlatformCustomOptionNumber { Min = 0, Max = 100, Step = 1, Value = 10, Slider = true }
                    }
                }
            }
        };

        return CreateDescriptor(info, "last-fm.png");
    }
}
