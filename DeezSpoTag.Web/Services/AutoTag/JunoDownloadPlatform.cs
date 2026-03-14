namespace DeezSpoTag.Web.Services.AutoTag;

public sealed class JunoDownloadPlatform : AutoTagPlatformBase
{
    public JunoDownloadPlatform(IWebHostEnvironment environment) : base(environment) { }

    public override AutoTagPlatformDescriptor Describe()
    {
        var info = new PlatformInfo
        {
            Id = "junodownload",
            Name = "Juno Download",
            Description = "Overall a mixed bag with a lot of niche genres",
            Version = "1.0.0",
            MaxThreads = 4,
            RequiresAuth = false,
            SupportedTags = new List<SupportedTag>
            {
                SupportedTag.Title,
                SupportedTag.Artist,
                SupportedTag.AlbumArtist,
                SupportedTag.Album,
                SupportedTag.BPM,
                SupportedTag.Genre,
                SupportedTag.Label,
                SupportedTag.ReleaseDate,
                SupportedTag.AlbumArt,
                SupportedTag.URL,
                SupportedTag.CatalogNumber,
                SupportedTag.ReleaseId,
                SupportedTag.TrackNumber,
                SupportedTag.TrackTotal,
                SupportedTag.Duration
            }
        };

        return CreateDescriptor(info, "junodownload.png");
    }
}
