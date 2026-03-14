namespace DeezSpoTag.Web.Services.CoverPort;

internal static class CoverSourcePolicies
{
    public static TimeSpan GetMinInterval(CoverSourceName source)
    {
        return source switch
        {
            CoverSourceName.CoverArtArchive => TimeSpan.FromMilliseconds(350),
            CoverSourceName.Deezer => TimeSpan.FromMilliseconds(200),
            CoverSourceName.Discogs => TimeSpan.FromMilliseconds(1000),
            CoverSourceName.Itunes => TimeSpan.FromMilliseconds(200),
            CoverSourceName.LastFm => TimeSpan.FromMilliseconds(250),
            _ => TimeSpan.FromMilliseconds(250)
        };
    }

    public static TimeSpan GetJsonCacheTtl(CoverSourceName source)
    {
        return source switch
        {
            CoverSourceName.CoverArtArchive => TimeSpan.FromDays(21),
            CoverSourceName.Deezer => TimeSpan.FromDays(14),
            CoverSourceName.Discogs => TimeSpan.FromDays(21),
            CoverSourceName.Itunes => TimeSpan.FromDays(14),
            CoverSourceName.LastFm => TimeSpan.FromDays(21),
            _ => TimeSpan.FromDays(14)
        };
    }

    public static TimeSpan GetImageCacheTtl(CoverSourceName source)
    {
        return TimeSpan.FromDays(30);
    }
}
