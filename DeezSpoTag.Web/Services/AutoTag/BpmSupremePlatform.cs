namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class BpmSupremePlatform : AutoTagPlatformBase
{
    public BpmSupremePlatform(IWebHostEnvironment environment) : base(environment) { }

    public override AutoTagPlatformDescriptor Describe()
    {
        var info = new PlatformInfo
        {
            Id = "bpmsupreme",
            Name = "BPM Supreme",
            Description = "Specialized in chart & open-format. Requires a free account",
            Version = "1.0.0",
            MaxThreads = 1,
            RequiresAuth = true,
            SupportedTags = new List<SupportedTag>
            {
                SupportedTag.Artist,
                SupportedTag.Title,
                SupportedTag.BPM,
                SupportedTag.AlbumArt,
                SupportedTag.Genre,
                SupportedTag.Key,
                SupportedTag.Label,
                SupportedTag.ReleaseDate,
                SupportedTag.TrackId,
                SupportedTag.Mood,
                SupportedTag.URL
            }
        };

        return CreateDescriptor(info, "bpmsupreme.png");
    }
}
