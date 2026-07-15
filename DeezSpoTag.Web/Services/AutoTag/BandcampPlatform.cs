namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class BandcampPlatform : AutoTagPlatformBase
{
    public BandcampPlatform(IWebHostEnvironment environment) : base(environment) { }

    public override AutoTagPlatformDescriptor Describe()
    {
        var info = new PlatformInfo
        {
            Id = "bandcamp",
            Name = "Bandcamp",
            Description = "Specialized in indie artists. Limited amount of tags",
            Version = "1.0.0",
            MaxThreads = 4,
            RequiresAuth = false,
            SupportedTags = new List<SupportedTag>
            {
                SupportedTag.Title,
                SupportedTag.Artist,
                SupportedTag.AlbumArtist,
                SupportedTag.Album,
                SupportedTag.ReleaseDate,
                SupportedTag.Label,
                SupportedTag.AlbumArt,
                SupportedTag.Style,
                SupportedTag.Genre,
                SupportedTag.TrackId,
                SupportedTag.URL,
                SupportedTag.ReleaseId,
                SupportedTag.TrackTotal,
                SupportedTag.ReleaseType
            }
        };

        return CreateDescriptor(info, "bandcamp.png");
    }
}
