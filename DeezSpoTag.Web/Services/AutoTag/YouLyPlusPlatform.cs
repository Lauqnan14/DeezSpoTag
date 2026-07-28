namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class YouLyPlusPlatform : AutoTagPlatformBase
{
    public YouLyPlusPlatform(IWebHostEnvironment environment) : base(environment) { }

    public override AutoTagPlatformDescriptor Describe()
    {
        return CreateDescriptor(
            new PlatformInfo
            {
                Id = "youlyplus",
                Name = "YouLy+",
                Description = "Fetch plain, synchronized, and word-synchronized lyrics.",
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
            "youly+.png");
    }
}
