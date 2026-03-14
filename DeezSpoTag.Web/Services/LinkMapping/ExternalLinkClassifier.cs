using System.Text.RegularExpressions;

namespace DeezSpoTag.Web.Services.LinkMapping;

public sealed class ExternalLinkClassifier
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly Regex SpotifyRegex = CreateRegex(
        @"^https?:\/\/(?:open\.spotify\.com\/(?:intl-[a-z]{2}\/)?(?:track|album|playlist|artist|episode|show)\/[\w]{11,24}|spotify\.link\/[\w]+)(?:[\?#].*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex YouTubeRegex = CreateRegex(
        @"(?:https?:\/\/)?(?:www\.)?(?:youtube\.com|youtu\.be|music\.youtube\.com)\/(?:watch\?v=|embed\/|v\/|shorts\/|playlist\?list=|channel\/)?([^&\s]{11,})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AppleMusicRegex = CreateRegex(
        @"^https?:\/\/(?:music\.apple\.com|itunes\.apple\.com)\/(?:[a-z]{2}(?:-[a-z]{2})?\/)?(?:album|playlist|station|artist|music-video|video-playlist|show|song)\/([^\/?]+)(?:\/([^\/?]+))?(?:\?.*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DeezerRegex = CreateRegex(
        @"^https?:\/\/(?:(?:www\.)?deezer\.com\/(?:[a-z]{2}(?:-[a-z]{2})?\/)?(?:track|album|playlist|artist|episode|show)\/[\w-]+|deezer\.page\.link\/[\w-]+)(?:[\?#].*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SoundCloudRegex = CreateRegex(
        @"^(?:https?:\/\/soundcloud\.com\/([\w-]+)\/([\w-]+)(?:\/sets\/([\w-]+))?(?:[\?#].*)?|https?:\/\/on\.soundcloud\.com\/([\w-]{8,}))$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TidalRegex = CreateRegex(
        @"^https?:\/\/tidal\.com\/(?:browse\/)?(track|artist|album|mix|video)\/([\w-]+)(?:\/[\w-]+)?(?:[\?#].*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex QobuzRegex = CreateRegex(
        @"^https?:\/\/(?:open|play|www)\.qobuz\.com\/(?:\w{2}-\w{2}\/)?(?:artist|album|interpreter|track)\/(?:[\w-]+\/)?(\w+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BandcampRegex = CreateRegex(
        @"^https?:\/\/([^\.]+)\.bandcamp\.com\/(album|track)?\/?([^\/?]+)?\/?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PandoraRegex = CreateRegex(
        @"^https?:\/\/(?:www\.)?pandora\.com\/(playlist|podcast|artist)\/(?:[^\/]+\/)?([^\/]+\/)?(?:[^\/]+\/)?((?:AL|AR|TR|PC|PE).+)\/?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BoomplayRegex = CreateRegex(
        @"^https?:\/\/(?:www\.|m\.)?boomplay\.com\/.+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public ExternalLinkSource Classify(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return ExternalLinkSource.Unknown;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !IsHttp(uri))
        {
            return ExternalLinkSource.Unknown;
        }

        var normalized = uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);

        if (SpotifyRegex.IsMatch(normalized)) return ExternalLinkSource.Spotify;
        if (YouTubeRegex.IsMatch(normalized)) return ExternalLinkSource.YouTube;
        if (AppleMusicRegex.IsMatch(normalized)) return ExternalLinkSource.AppleMusic;
        if (DeezerRegex.IsMatch(normalized)) return ExternalLinkSource.Deezer;
        if (SoundCloudRegex.IsMatch(normalized)) return ExternalLinkSource.SoundCloud;
        if (TidalRegex.IsMatch(normalized)) return ExternalLinkSource.Tidal;
        if (QobuzRegex.IsMatch(normalized)) return ExternalLinkSource.Qobuz;
        if (BandcampRegex.IsMatch(normalized)) return ExternalLinkSource.Bandcamp;
        if (PandoraRegex.IsMatch(normalized)) return ExternalLinkSource.Pandora;
        if (BoomplayRegex.IsMatch(normalized)) return ExternalLinkSource.Boomplay;

        return ExternalLinkSource.Unknown;
    }

    private static bool IsHttp(Uri uri)
    {
        return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    private static Regex CreateRegex(string pattern, RegexOptions options)
        => new(pattern, options, RegexTimeout);
}
