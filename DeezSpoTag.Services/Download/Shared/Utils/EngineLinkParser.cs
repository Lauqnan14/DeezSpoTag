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

    public static string? TryExtractQobuzTrackId(string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl)
            || !Uri.TryCreate(sourceUrl, UriKind.Absolute, out var parsed)
            || !parsed.Host.Contains("qobuz.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var segments = parsed.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (!segments[i].Equals("track", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var candidate = Uri.UnescapeDataString(segments[i + 1]).Trim();
            return string.IsNullOrWhiteSpace(candidate) ? null : candidate;
        }

        return null;
    }

    public static string? TryExtractTidalTrackId(string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl)
            || !Uri.TryCreate(sourceUrl, UriKind.Absolute, out var parsed)
            || !parsed.Host.Contains("tidal.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var segments = parsed.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (!segments[i].Equals("track", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var candidate = Uri.UnescapeDataString(segments[i + 1]).Trim();
            return candidate.All(char.IsDigit) ? candidate : null;
        }

        return null;
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

        if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var parsed)
            && parsed.Host.Contains("amazon.", StringComparison.OrdinalIgnoreCase))
        {
            return TryExtractAmazonTrackAsinFromQuery(parsed.Query)
                ?? TryExtractAmazonTrackIdFromPath(parsed.AbsolutePath);
        }

        var match = Regex.Match(sourceUrl, @"(?:trackAsin=|asin=|\/tracks?\/)(?<id>[A-Z0-9]+)", RegexOptions.IgnoreCase, regexTimeout);
        return match.Success ? match.Groups["id"].Value : null;
    }

    private static string? TryExtractAmazonTrackAsinFromQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pieces = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pieces.Length != 2)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(pieces[0]);
            if (!key.Equals("trackAsin", StringComparison.OrdinalIgnoreCase)
                && !key.Equals("asin", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = Uri.UnescapeDataString(pieces[1]).Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    private static string? TryExtractAmazonTrackIdFromPath(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("track", StringComparison.OrdinalIgnoreCase)
                || segments[i].Equals("tracks", StringComparison.OrdinalIgnoreCase))
            {
                var value = Uri.UnescapeDataString(segments[i + 1]).Trim();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        return null;
    }
}
