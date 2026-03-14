namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class TraxsourcePlatform : AutoTagPlatformBase
{
    public TraxsourcePlatform(IWebHostEnvironment environment) : base(environment) { }

    public override AutoTagPlatformDescriptor Describe()
    {
        var info = new PlatformInfo
        {
            Id = "traxsource",
            Name = "Traxsource",
            Description = "Overall more specialized in House",
            Version = "1.0.0",
            MaxThreads = 0,
            RequiresAuth = false,
            SupportedTags = new List<SupportedTag>
            {
                SupportedTag.Version,
                SupportedTag.Artist,
                SupportedTag.BPM,
                SupportedTag.Key,
                SupportedTag.Title,
                SupportedTag.URL,
                SupportedTag.Label,
                SupportedTag.ReleaseDate,
                SupportedTag.Genre,
                SupportedTag.TrackId,
                SupportedTag.Duration,
                SupportedTag.Album,
                SupportedTag.ReleaseId,
                SupportedTag.CatalogNumber,
                SupportedTag.AlbumArtist,
                SupportedTag.TrackNumber,
                SupportedTag.TrackTotal,
                SupportedTag.AlbumArt
            }
        };

        return CreateDescriptor(info, "traxsource.png");
    }
}
