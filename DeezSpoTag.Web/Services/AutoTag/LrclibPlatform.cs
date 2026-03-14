namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class LrclibPlatform : AutoTagPlatformBase
{
    public LrclibPlatform(IWebHostEnvironment environment) : base(environment) { }

    public override AutoTagPlatformDescriptor Describe()
    {
        var info = new PlatformInfo
        {
            Id = "lrclib",
            Name = "LRCLIB",
            Description = "Official LRCLIB API for synced and unsynced lyrics.",
            Version = "1.0.0",
            MaxThreads = 2,
            RequiresAuth = false,
            SupportedTags = new List<SupportedTag>
            {
                SupportedTag.SyncedLyrics,
                SupportedTag.UnsyncedLyrics
            },
            CustomOptions = new PlatformCustomOptions
            {
                Options = new List<PlatformCustomOption>
                {
                    new()
                    {
                        Id = "duration_tolerance_seconds",
                        Label = "Duration tolerance (seconds)",
                        Tooltip = "Maximum duration difference used when ranking LRCLIB search results.",
                        Value = new PlatformCustomOptionNumber
                        {
                            Min = 0,
                            Max = 60,
                            Step = 1,
                            Value = 10,
                            Slider = true
                        }
                    },
                    new()
                    {
                        Id = "use_duration_hint",
                        Label = "Use duration hint",
                        Tooltip = "Sends track duration to LRCLIB metadata lookup when available.",
                        Value = new PlatformCustomOptionBoolean { Value = true }
                    },
                    new()
                    {
                        Id = "search_fallback",
                        Label = "Enable search fallback",
                        Tooltip = "Falls back to /api/search when exact metadata lookup misses.",
                        Value = new PlatformCustomOptionBoolean { Value = true }
                    },
                    new()
                    {
                        Id = "prefer_synced",
                        Label = "Prefer synced lyrics",
                        Tooltip = "Ranks synced lyrics above plain lyrics when multiple matches are found.",
                        Value = new PlatformCustomOptionBoolean { Value = true }
                    }
                }
            }
        };

        return CreateDescriptor(info, "lrclib.png");
    }
}
