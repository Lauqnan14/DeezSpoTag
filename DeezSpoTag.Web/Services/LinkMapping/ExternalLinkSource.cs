namespace DeezSpoTag.Web.Services.LinkMapping;

public enum ExternalLinkSource
{
    Unknown = 0,
    Deezer,
    Spotify,
    YouTube,
    AppleMusic,
    SoundCloud,
    Tidal,
    Qobuz,
    Bandcamp,
    Pandora,
    Boomplay
}

public static class ExternalLinkSourceExtensions
{
    public static string ToClientValue(this ExternalLinkSource source)
    {
        return source switch
        {
            ExternalLinkSource.Deezer => "deezer",
            ExternalLinkSource.Spotify => "spotify",
            ExternalLinkSource.YouTube => "youTube",
            ExternalLinkSource.AppleMusic => "appleMusic",
            ExternalLinkSource.SoundCloud => "soundCloud",
            ExternalLinkSource.Tidal => "tidal",
            ExternalLinkSource.Qobuz => "qobuz",
            ExternalLinkSource.Bandcamp => "bandcamp",
            ExternalLinkSource.Pandora => "pandora",
            ExternalLinkSource.Boomplay => "boomplay",
            _ => "unknown"
        };
    }
}
