namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class MusixmatchPlatform : AutoTagPlatformBase
{
    public MusixmatchPlatform(IWebHostEnvironment environment) : base(environment) { }

    public override AutoTagPlatformDescriptor Describe()
    {
        var info = new PlatformInfo
        {
            Id = "musixmatch",
            Name = "Musixmatch",
            Description = "Fetch lyrics from the largest lyrics platform in the world",
            Version = "1.0.0",
            MaxThreads = 1,
            RequiresAuth = false,
            SupportedTags = new List<SupportedTag>
            {
                SupportedTag.SyncedLyrics,
                SupportedTag.UnsyncedLyrics,
                SupportedTag.TtmlLyrics
            }
        };

        return CreateDescriptor(info, "musixmatch.png");
    }
}
