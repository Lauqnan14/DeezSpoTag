namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class BetterLyricsPlatform : AutoTagPlatformBase
{
    public BetterLyricsPlatform(IWebHostEnvironment environment) : base(environment) { }

    public override AutoTagPlatformDescriptor Describe()
    {
        return CreateDescriptor(
            new PlatformInfo
            {
                Id = "betterlyrics",
                Name = "BetterLyrics",
                Description = "Fetch native TTML and synchronized lyrics.",
                Version = "1.0.0",
                MaxThreads = 1,
                RequiresAuth = false,
                SupportedTags =
                [
                    SupportedTag.SyncedLyrics,
                    SupportedTag.UnsyncedLyrics,
                    SupportedTag.TtmlLyrics
                ],
                CustomOptions = new PlatformCustomOptions
                {
                    Options = new List<PlatformCustomOption>
                    {
                        new()
                        {
                            Id = "duration_tolerance_seconds",
                            Label = "Extra length allowance (seconds)",
                            Tooltip = "BetterLyrics matches by song and artist, so a radio edit, a live take or a "
                                + "same-titled song can come back instead. Lyrics are rejected when the timed "
                                + "document ends more than this much later than the track, or more than 5 seconds "
                                + "earlier (missing verses). Trailing silence and outros routinely run long, so keep "
                                + "this generous.",
                            Value = new PlatformCustomOptionNumber
                            {
                                Min = 0,
                                Max = 120,
                                Step = 1,
                                Value = 15,
                                Slider = true
                            }
                        }
                    }
                }
            },
            "better-lyrics.png");
    }
}
