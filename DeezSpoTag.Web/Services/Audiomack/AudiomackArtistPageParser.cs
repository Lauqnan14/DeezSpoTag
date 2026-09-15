using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeezSpoTag.Web.Services.Audiomack;

/// <summary>
/// Extracts the raw artist location (hometown/location) and biography (<c>bio</c>)
/// from a public Audiomack artist page. The page embeds its data in Next.js flight
/// chunks
/// (self.__next_f.push([1,"&lt;escaped json&gt;"])). Each chunk string is decoded
/// exactly one level with a real escape decoder and the decoded stream is
/// reassembled, so objects fragmented across chunks become whole. Balanced,
/// string-aware object spans are then collected over the decoded stream and the
/// artist object whose url_slug matches the requested slug is parsed with a real
/// JSON parser — name, hometown and location therefore always come from the same
/// object, never from neighbouring objects on the page. The object's name is
/// cross-checked against the expected artist name, because slugs can be
/// recycled or reassigned to a different artist; a mismatch (or a non-match)
/// returns null instead of guessing, so a wrong artist can never contribute a
/// location.
/// </summary>
/// Result of a successful artist-object match: the canonical url_slug of the
/// matched profile (always equal to the requested slug today, but carried
/// explicitly so callers can persist the identifier) plus the raw location and
/// biography. Either value may be null; the info is returned when at least one
/// of them is present, so a biography-only profile is still usable.
public sealed record AudiomackArtistPageInfo(string CanonicalUrlSlug, string? RawLocation, string? RawBiography);

public static class AudiomackArtistPageParser
{
    private static readonly Regex PushChunkRegex =
        new("self\\.__next_f\\.push\\(\\[1,\\s*\"(?<payload>(?:[^\"\\\\]|\\\\.)*)\"\\]\\)", RegexOptions.Compiled);

    private static readonly Regex UrlSlugRegex =
        new("\"url_slug\"\\s*:\\s*\"(?<slug>[^\"]*)\"", RegexOptions.Compiled);

    public static string? TryExtractRawLocation(string? html, string? urlSlug, string? expectedArtistName)
    {
        return TryExtractArtistPageInfo(html, urlSlug, expectedArtistName)?.RawLocation;
    }

    /// <summary>
    /// Raw biography (<c>bio</c>) of the matched artist object, or null when the
    /// artist object carries none. Never synthesised: only the profile's own
    /// value is returned.
    /// </summary>
    public static string? TryExtractRawBiography(string? html, string? urlSlug, string? expectedArtistName)
    {
        return TryExtractArtistPageInfo(html, urlSlug, expectedArtistName)?.RawBiography;
    }

    public static AudiomackArtistPageInfo? TryExtractArtistPageInfo(string? html, string? urlSlug, string? expectedArtistName)
    {
        if (string.IsNullOrWhiteSpace(html) || string.IsNullOrWhiteSpace(urlSlug) || string.IsNullOrWhiteSpace(expectedArtistName))
        {
            return null;
        }

        var decoded = new StringBuilder();
        foreach (Match chunk in PushChunkRegex.Matches(html))
        {
            var chunkText = TryDecodeFlightChunk(chunk.Groups["payload"].Value);
            if (chunkText != null)
            {
                decoded.Append(chunkText);
            }
        }

        if (decoded.Length == 0)
        {
            return null;
        }

        var stream = decoded.ToString();
        var objectSpans = CollectBalancedObjectSpans(stream);

        foreach (Match slugMatch in UrlSlugRegex.Matches(stream))
        {
            if (!string.Equals(slugMatch.Groups["slug"].Value, urlSlug, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var span = FindInnermostSpanContaining(objectSpans, slugMatch.Index);
            if (span == null)
            {
                continue;
            }

            string? name;
            string? hometown;
            string? location;
            string? biography;
            string? canonicalSlug = null;
            try
            {
                using var document = JsonDocument.Parse(stream[span.Value.Open..(span.Value.Close + 1)]);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                name = GetTrimmedStringOrNull(root, "name");
                hometown = GetTrimmedStringOrNull(root, "hometown");
                location = GetTrimmedStringOrNull(root, "location")
                           ?? GetLocationDisplayOrNull(root);
                biography = GetTrimmedStringOrNull(root, "bio");
                canonicalSlug = GetTrimmedStringOrNull(root, "url_slug");
            }
            catch (JsonException)
            {
                // Unparsable fragment: skip rather than guess.
                continue;
            }

            if (!IsExpectedArtist(name, expectedArtistName))
            {
                continue;
            }

            var raw = !string.IsNullOrWhiteSpace(hometown) ? hometown : location;
            var rawLocation = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
            var rawBiography = string.IsNullOrWhiteSpace(biography) ? null : biography.Trim();
            if (rawLocation != null || rawBiography != null)
            {
                return new AudiomackArtistPageInfo(canonicalSlug ?? urlSlug, rawLocation, rawBiography);
            }
        }

        return null;
    }

    /// <summary>Decodes the escaped chunk string pushed into the flight stream.</summary>
    private static string? TryDecodeFlightChunk(string payload)
    {
        try
        {
            return Regex.Unescape(payload);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// One forward pass over the decoded stream, tracking string literals and
    /// escapes, recording the (open, close) index pair of every balanced JSON
    /// object.
    /// </summary>
    private static List<ObjectSpan> CollectBalancedObjectSpans(string text)
    {
        var spans = new List<ObjectSpan>();
        var openBraces = new Stack<int>();
        var inString = false;
        var escaped = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
            }
            else if (c == '{')
            {
                openBraces.Push(i);
            }
            else if (c == '}' && openBraces.Count > 0)
            {
                spans.Add(new ObjectSpan(openBraces.Pop(), i));
            }
        }

        return spans;
    }

    private static ObjectSpan? FindInnermostSpanContaining(List<ObjectSpan> spans, int index)
    {
        ObjectSpan? innermost = null;
        foreach (var span in spans)
        {
            if (span.Open <= index && index <= span.Close
                && (innermost == null || span.Open > innermost.Value.Open))
            {
                innermost = span;
            }
        }

        return innermost;
    }

    private static string? GetTrimmedStringOrNull(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var raw = value.GetString();
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    /// <summary>
    /// Some profiles carry location as a structured object instead of a string,
    /// e.g. {"tag":"ghanagreateraccraaccra","display":"Accra, Ghana"}; its
    /// display value is the artist's normalized "City, Country" text.
    /// </summary>
    private static string? GetLocationDisplayOrNull(JsonElement root)
    {
        if (!root.TryGetProperty("location", out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!value.TryGetProperty("display", out var display) || display.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var raw = display.GetString();
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
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

        const int minimumNameLengthForPrefixMatch = 4;
        return expected.Length >= minimumNameLengthForPrefixMatch
            && actual.Length >= minimumNameLengthForPrefixMatch
            && (expected.StartsWith(actual, StringComparison.Ordinal) || actual.StartsWith(expected, StringComparison.Ordinal));
    }

    private static string NormalizeName(string value) =>
        new(value.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private readonly record struct ObjectSpan(int Open, int Close);
}
