using System.Text.RegularExpressions;

namespace DeezSpoTag.Services.Download.Shared.Utils;

public static class EngineLinkParser
{
    private static readonly Regex AmazonTrackIdRegex = new(
        @"^B[0-9A-Z]{9}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(250));

    public static string? NormalizeAmazonTrackId(string? value)
    {
        var normalized = value?.Trim().ToUpperInvariant();
        return !string.IsNullOrWhiteSpace(normalized) && AmazonTrackIdRegex.IsMatch(normalized)
            ? normalized
            : null;
    }

    public static string? NormalizeNumericTrackId(string? value)
    {
        var normalized = value?.Trim();
        return !string.IsNullOrWhiteSpace(normalized) && normalized.All(char.IsDigit)
            ? normalized
            : null;
    }

    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

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
            || !IsHostOrSubdomain(parsed.Host, "qobuz.com"))
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
            || !IsHostOrSubdomain(parsed.Host, "tidal.com"))
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
        if (string.IsNullOrWhiteSpace(sourceUrl)
            || !Uri.TryCreate(sourceUrl, UriKind.Absolute, out var parsed)
            || !IsAmazonHost(parsed.Host))
        {
            return null;
        }

        return sourceUrl;
    }

    public static string? TryExtractAmazonTrackId(string? sourceUrl, TimeSpan regexTimeout)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return null;
        }

        if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var parsed)
            && IsAmazonHost(parsed.Host))
        {
            return NormalizeAmazonTrackId(
                TryExtractAmazonTrackAsinFromQuery(parsed.Query)
                ?? TryExtractAmazonTrackIdFromPath(parsed.AbsolutePath));
        }

        var match = Regex.Match(
            sourceUrl,
            @"(?:trackAsin=|asin=|\/tracks?\/)(?<id>B[0-9A-Z]{9})(?:$|[/?&#])",
            RegexOptions.IgnoreCase,
            regexTimeout);
        return match.Success ? NormalizeAmazonTrackId(match.Groups["id"].Value) : null;
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

    public static string? TryExtractDeezerTrackId(string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl)
            || !Uri.TryCreate(sourceUrl, UriKind.Absolute, out var parsed)
            || !IsHostOrSubdomain(parsed.Host, "deezer.com"))
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
            return candidate.All(char.IsDigit) && candidate != "0" ? candidate : null;
        }

        return null;
    }

    private static bool IsHostOrSubdomain(string host, string domain)
        => host.Equals(domain, StringComparison.OrdinalIgnoreCase)
           || host.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase);

    private static bool IsAmazonHost(string host)
        => host.Equals("amazon.com", StringComparison.OrdinalIgnoreCase)
           || host.StartsWith("amazon.", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".amazon.com", StringComparison.OrdinalIgnoreCase)
           || host.StartsWith("music.amazon.", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".amazon.co.uk", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".amazon.co.jp", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".amazon.de", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".amazon.fr", StringComparison.OrdinalIgnoreCase);
}
