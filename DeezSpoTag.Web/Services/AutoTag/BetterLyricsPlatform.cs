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
                ]
            },
            "better-lyrics.png");
    }
}
