using System.Net;
using System.Text.RegularExpressions;

namespace DeezSpoTag.Web.Services;

/// <summary>
/// Single shared biography cleaner used by the cache refresh, the metadata updater,
/// the Navidrome metadata agent, and any biography payload served to the artist page.
/// Strips platform markup (Tidal [wimpLink…]), unwraps anchors, removes HTML tags,
/// decodes entities, and collapses whitespace.
/// </summary>
public static partial class ArtistBiographySanitizer
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    [GeneratedRegex(@"\[/?\s*wimpLink[^\]]*\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WimpLinkMarkupPattern();

    [GeneratedRegex(@"<a\b[^>]*>(.*?)</a\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex AnchorTagPattern();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTagPattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRunPattern();

    /// <summary>
    /// Removes platform markup, HTML tags, and decodes entities without collapsing
    /// whitespace — for callers that preserve their own paragraph/newline handling.
    /// </summary>
    public static string StripPlatformMarkup(string value)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        text = WimpLinkMarkupPattern().Replace(text, string.Empty);
        text = AnchorTagPattern().Replace(text, "$1");
        text = HtmlTagPattern().Replace(text, " ");
        return WebUtility.HtmlDecode(text).Trim();
    }

    public static string? Clean(string? value)
    {
        var text = StripPlatformMarkup(value ?? string.Empty);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = WhitespaceRunPattern().Replace(text, " ").Trim();
        return text.Length == 0 ? null : text;
    }
}
