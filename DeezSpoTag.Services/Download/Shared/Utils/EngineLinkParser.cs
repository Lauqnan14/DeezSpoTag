using System.Text.RegularExpressions;

namespace DeezSpoTag.Services.Download.Shared.Utils;

public static class EngineLinkParser
{
    public static string? TryExtractSpotifyTrackId(string? sourceUrl, TimeSpan regexTimeout)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return null;
        }

        var match = Regex.Match(sourceUrl, @"spotify\.com\/track\/(?<id>[a-zA-Z0-9]+)", RegexOptions.IgnoreCase, regexTimeout);
        return match.Success ? match.Groups["id"].Value : null;
    }

    public static string? TryNormalizeAmazonUrl(string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return null;
        }

        if (sourceUrl.Contains("music.amazon.", StringComparison.OrdinalIgnoreCase)
            || sourceUrl.Contains("amazon.com/music", StringComparison.OrdinalIgnoreCase)
            || sourceUrl.Contains("amazon.co", StringComparison.OrdinalIgnoreCase))
        {
            return sourceUrl;
        }

        return null;
    }

    public static string? TryExtractAmazonTrackId(string? sourceUrl, TimeSpan regexTimeout)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return null;
        }

        var match = Regex.Match(sourceUrl, @"(?:trackAsin=|\/tracks\/)(?<id>[A-Z0-9]+)", RegexOptions.IgnoreCase, regexTimeout);
        return match.Success ? match.Groups["id"].Value : null;
    }
}
