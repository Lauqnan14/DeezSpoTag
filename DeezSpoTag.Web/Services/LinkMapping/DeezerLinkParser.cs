using System.Text.RegularExpressions;

namespace DeezSpoTag.Web.Services.LinkMapping;

public static class DeezerLinkParser
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex NumericIdRegex = new(
        @"^\d+$",
        RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex DeezerDirectRegex = new(
        @"^https?:\/\/(?:www\.)?deezer\.com\/(?:[a-z]{2}(?:-[a-z]{2})?\/)?(?<type>track|album|playlist|artist|episode|show)\/(?<id>\d+)(?:[\/#?].*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly HashSet<string> SupportedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "track",
        "album",
        "playlist",
        "artist",
        "episode",
        "show"
    };

    public static bool TryParse(string? deezerUrl, out DeezerLinkDescriptor descriptor)
    {
        descriptor = default!;

        if (string.IsNullOrWhiteSpace(deezerUrl)
            || !Uri.TryCreate(deezerUrl, UriKind.Absolute, out var uri)
            || !IsHttp(uri)
            || !IsDeezerHost(uri.Host))
        {
            return false;
        }

        var normalized = uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
        var directMatch = DeezerDirectRegex.Match(normalized);
        if (directMatch.Success)
        {
            var type = directMatch.Groups["type"].Value.Trim().ToLowerInvariant();
            var id = directMatch.Groups["id"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(type) && IsValidDeezerId(id))
            {
                descriptor = new DeezerLinkDescriptor(type, id, BuildCanonicalUrl(type, id));
                return true;
            }
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var index = 0; index < segments.Length - 1; index++)
        {
            var typeCandidate = segments[index];
            if (!SupportedTypes.Contains(typeCandidate))
            {
                continue;
            }

            var idCandidate = segments[index + 1].Trim();
            if (!IsValidDeezerId(idCandidate))
            {
                continue;
            }

            var normalizedType = typeCandidate.ToLowerInvariant();
            descriptor = new DeezerLinkDescriptor(
                normalizedType,
                idCandidate,
                BuildCanonicalUrl(normalizedType, idCandidate));
            return true;
        }

        return false;
    }

    private static bool IsValidDeezerId(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && NumericIdRegex.IsMatch(value);
    }

    private static bool IsHttp(Uri uri)
    {
        return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDeezerHost(string host)
    {
        return host.Equals("deezer.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("www.deezer.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("deezer.page.link", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildCanonicalUrl(string type, string id)
    {
        return $"https://www.deezer.com/{type}/{id}";
    }
}
