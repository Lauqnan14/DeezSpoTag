using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace DeezSpoTag.Web.Services.Audiomack;

/// <summary>
/// Extracts the raw artist location (hometown/location) from a public Audiomack
/// artist page. The page embeds its data as flight-payload strings where JSON
/// quotes are escaped (\"), so values are read after unescaping and anchored on
/// the artist object whose url_slug matches the requested slug exactly. The
/// object's own name is cross-checked against the expected artist name, because
/// slugs can be recycled or reassigned to a different artist; a mismatch (or a
/// non-match) returns null instead of guessing, so a wrong artist can never
/// contribute a location.
/// </summary>
public static class AudiomackArtistPageParser
{
    private const int LookupWindowChars = 5000;
    private const int MinimumNameLengthForPrefixMatch = 4;

    private static readonly Regex UrlSlugRegex =
        new("\"url_slug\"\\s*:\\s*\"(?<slug>[^\"]*)\"", RegexOptions.Compiled);

    // Matches the "name" key of a JSON object (after '{' or ',' or a flight-chunk
    // counter) while excluding keys that merely end in "name" (e.g. "twitter_name").
    private static readonly Regex ArtistNameRegex =
        new("(?<![\\w])\"name\"\\s*:\\s*\"(?<value>[^\"]*)\"", RegexOptions.Compiled);

    private static readonly Regex HometownRegex =
        new("\"hometown\"\\s*:\\s*(?:null|\"(?<value>.*?)\")", RegexOptions.Compiled);

    private static readonly Regex LocationFieldRegex =
        new("\"location\"\\s*:\\s*(?:null|\"(?<value>.*?)\")", RegexOptions.Compiled);

    public static string? TryExtractRawLocation(string? html, string? urlSlug, string? expectedArtistName)
    {
        if (string.IsNullOrWhiteSpace(html) || string.IsNullOrWhiteSpace(urlSlug) || string.IsNullOrWhiteSpace(expectedArtistName))
        {
            return null;
        }

        // Flight payloads embed JSON inside strings: \"hometown\":\"Lagos, Nigeria\".
        var unescaped = html.Replace("\\\"", "\"");
        foreach (Match slugMatch in UrlSlugRegex.Matches(unescaped))
        {
            if (!string.Equals(slugMatch.Groups["slug"].Value, urlSlug, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var windowStart = Math.Max(0, slugMatch.Index - LookupWindowChars);
            var windowLength = Math.Min(unescaped.Length, slugMatch.Index + LookupWindowChars) - windowStart;
            var window = unescaped.Substring(windowStart, windowLength);
            var slugIndexInWindow = slugMatch.Index - windowStart;

            var objectName = ExtractClosestValue(window, ArtistNameRegex, slugIndexInWindow);
            if (!IsExpectedArtist(objectName, expectedArtistName))
            {
                continue;
            }

            var raw = ExtractClosestValue(window, HometownRegex, slugIndexInWindow)
                      ?? ExtractClosestValue(window, LocationFieldRegex, slugIndexInWindow);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                return raw.Trim();
            }
        }

        return null;
    }

    private static bool IsExpectedArtist(string? objectName, string expectedArtistName)
    {
        if (string.IsNullOrWhiteSpace(objectName))
        {
            return false;
        }

        var expected = NormalizeName(expectedArtistName);
        var actual = NormalizeName(objectName);
        if (expected.Length == 0 || actual.Length == 0)
        {
            return false;
        }

        if (string.Equals(expected, actual, StringComparison.Ordinal))
        {
            return true;
        }

        return expected.Length >= MinimumNameLengthForPrefixMatch
            && actual.Length >= MinimumNameLengthForPrefixMatch
            && (expected.StartsWith(actual, StringComparison.Ordinal) || actual.StartsWith(expected, StringComparison.Ordinal));
    }

    private static string NormalizeName(string value) =>
        new(value.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static string? ExtractClosestValue(string window, Regex regex, int anchorIndex)
    {
        string? closest = null;
        var closestDistance = int.MaxValue;
        foreach (Match match in regex.Matches(window))
        {
            if (!match.Groups["value"].Success)
            {
                continue;
            }

            var value = match.Groups["value"].Value;
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var distance = Math.Abs(match.Index - anchorIndex);
            if (distance < closestDistance)
            {
                closestDistance = distance;
                closest = value;
            }
        }

        return closest;
    }
}
