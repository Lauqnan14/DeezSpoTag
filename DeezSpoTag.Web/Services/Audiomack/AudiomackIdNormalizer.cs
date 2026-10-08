using System;
using System.Linq;

namespace DeezSpoTag.Web.Services.Audiomack;

/// <summary>
/// Normalizes user-supplied Audiomack artist identifiers. Accepts a bare slug
/// ("alikiba") or an audiomack.com profile URL ("https://audiomack.com/alikiba")
/// and returns the canonical lowercase slug, or null when the input cannot be a
/// valid Audiomack slug (including URLs from other providers).
/// </summary>
public static class AudiomackIdNormalizer
{
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var trimmed = input.Trim().TrimEnd('/');
        string value;
        var looksLikeUrl = trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (looksLikeUrl && Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            if (!IsValidSongUri(uri))
            {
                return null;
            }

            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            value = segments.Length > 0 ? Uri.UnescapeDataString(segments[^1]) : string.Empty;
        }
        else
        {
            var lastSlash = trimmed.LastIndexOf('/');
            value = lastSlash >= 0 ? trimmed[(lastSlash + 1)..] : trimmed;
        }

        value = value.ToLowerInvariant();
        if (value.Length == 0 || value.Length > 64)
        {
            return null;
        }

        if (!value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'))
        {
            return null;
        }

        if (value.StartsWith('-') || value.EndsWith('-') || value.Contains("--"))
        {
            return null;
        }

        return value;
    }

    /// <summary>
    /// Extracts the artist and song slugs from an Audiomack song URL
    /// ("https://audiomack.com/&lt;artist&gt;/song/&lt;song&gt;").
    /// </summary>
    public static bool TryExtractSongSlugs(string? url, out string artistSlug, out string songSlug)
    {
        artistSlug = string.Empty;
        songSlug = string.Empty;
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || !IsValidSongUri(uri))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // Expected shape: /<artist>/song/<song> — tolerate omitting the "song" segment.
        string rawArtist, rawSong;
        if (segments.Length == 3
            && segments[1].Equals("song", StringComparison.OrdinalIgnoreCase))
        {
            rawArtist = segments[0];
            rawSong = segments[2];
        }
        else if (segments.Length == 2)
        {
            rawArtist = segments[0];
            rawSong = segments[1];
        }
        else
        {
            return false;
        }

        var decodedArtist = Uri.UnescapeDataString(rawArtist);
        var decodedSong = Uri.UnescapeDataString(rawSong);
        if (decodedArtist.Contains('/') || decodedArtist.Contains('\\')
            || decodedSong.Contains('/') || decodedSong.Contains('\\'))
        {
            return false;
        }

        return Normalize(decodedArtist) is { Length: > 0 } normalizedArtist
            && Normalize(decodedSong) is { Length: > 0 } normalizedSong
            && (artistSlug = normalizedArtist) is not null
            && (songSlug = normalizedSong) is not null;
    }

    /// <summary>
    /// The canonical "artist/song" identity path shared by the two supported URL
    /// shapes ("/artist/song/slug" and "/artist/slug"), so both spellings compare
    /// equal. Query, fragment and trailing slashes never change identity.
    /// </summary>
    public static bool TryGetCanonicalSongPath(string? url, out string canonicalPath)
    {
        canonicalPath = string.Empty;
        if (!TryExtractSongSlugs(url, out var artistSlug, out var songSlug))
        {
            return false;
        }

        canonicalPath = $"{artistSlug}/{songSlug}";
        return true;
    }

    /// <summary>Audiomack track ids are numeric; normalize for equality (leading zeros, whitespace).</summary>
    internal static string? NormalizeTrackId(string? id)
    {
        var trimmed = id?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.All(char.IsDigit) && trimmed.Length <= 20
            && long.TryParse(trimmed, out var value))
        {
            return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        // Unknown shape: still comparable as-is, never empty.
        return trimmed;
    }

    /// <summary>ISRCs normalize to 12 uppercase alphanumeric characters; anything else is not valid ISRC evidence.</summary>
    internal static string? NormalizeIsrc(string? isrc)
    {
        if (string.IsNullOrWhiteSpace(isrc))
        {
            return null;
        }

        var compact = new string(isrc.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return compact.Length == 12 ? compact : null;
    }

    /// <summary>
    /// Collects the verifiable identity of a candidate. Returns false when the
    /// candidates own fields contradict each other (explicit URL pointing at a
    /// different path than the artist/song slugs). Missing evidence is not an
    /// error; it is reported as null outputs and enforced by IsSameSong.
    /// </summary>
    internal static bool TryGetSongIdentity(
        AudiomackSongCandidate candidate,
        out string? trackId,
        out string? canonicalPath,
        out string? rejectionReason)
    {
        trackId = NormalizeTrackId(candidate.Id);
        rejectionReason = null;

        var urlPath = TryGetCanonicalSongPath(candidate.Url, out var fromUrl) ? fromUrl : null;
        string? slugPath = null;
        var artistSlug = Normalize(candidate.ArtistSlug);
        var songSlug = Normalize(candidate.UrlSlug);
        if (artistSlug is { Length: > 0 } && songSlug is { Length: > 0 })
        {
            slugPath = $"{artistSlug}/{songSlug}";
        }

        if (urlPath is not null && slugPath is not null
            && !string.Equals(urlPath, slugPath, StringComparison.Ordinal))
        {
            canonicalPath = null;
            rejectionReason = "url-slug-mismatch";
            return false;
        }

        canonicalPath = urlPath ?? slugPath;
        return true;
    }

    /// <summary>
    /// True only when the received payload is verifiably the same recording as the
    /// expected one: internally coherent, no conflicting strong identity (ID, path,
    /// ISRC), no conflicting title/version/credit evidence, and at least one
    /// matching strong identifier (track id or canonical song path). ISRC agreement
    /// alone never authorizes mixing payloads.
    /// </summary>
    internal static bool IsSameSong(
        AudiomackSongCandidate expected,
        AudiomackSongCandidate received,
        out string? rejectionReason)
    {
        if (!TryGetSongIdentity(expected, out var expectedId, out var expectedPath, out rejectionReason)
            || !TryGetSongIdentity(received, out var receivedId, out var receivedPath, out rejectionReason))
        {
            return false;
        }

        if (expectedId is not null && receivedId is not null
            && !string.Equals(expectedId, receivedId, StringComparison.Ordinal))
        {
            rejectionReason = "id-mismatch";
            return false;
        }

        if (expectedPath is not null && receivedPath is not null
            && !string.Equals(expectedPath, receivedPath, StringComparison.Ordinal))
        {
            rejectionReason = "path-mismatch";
            return false;
        }

        var expectedIsrc = NormalizeIsrc(expected.Isrc);
        var receivedIsrc = NormalizeIsrc(received.Isrc);
        if (expectedIsrc is not null && receivedIsrc is not null
            && !string.Equals(expectedIsrc, receivedIsrc, StringComparison.Ordinal))
        {
            rejectionReason = "isrc-mismatch";
            return false;
        }

        var idMatches = expectedId is not null && receivedId is not null;
        var pathMatches = expectedPath is not null && receivedPath is not null;
        if (!idMatches && !pathMatches)
        {
            rejectionReason = "insufficient-identity-evidence";
            return false;
        }

        var expectedArtist = RecordingArtists(expected);
        var receivedArtist = RecordingArtists(received);
        if (expectedArtist.Count > 0 && receivedArtist.Count > 0
            && !expectedArtist.Overlaps(receivedArtist))
        {
            rejectionReason = "recording-artist-conflict";
            return false;
        }

        // Matching strong identifiers do not excuse conflicting recording evidence.
        if (!string.IsNullOrWhiteSpace(expected.Title) && !string.IsNullOrWhiteSpace(received.Title))
        {
            if (DeezSpoTag.Core.Utils.TrackTitleMatcher.HasVersionDrift(expected.Title, received.Title))
            {
                rejectionReason = "version-drift";
                return false;
            }

            if (!DeezSpoTag.Core.Utils.TrackTitleMatcher.HasCompatibleTitleIdentity(expected.Title, received.Title))
            {
                rejectionReason = "title-conflict";
                return false;
            }
        }

        var expectedCredits = ParseFeaturedGuests(expected.Featuring)
            .Concat(ParseTitleFeaturedGuests(expected.Title))
            .ToHashSet(StringComparer.Ordinal);
        var receivedCredits = ParseFeaturedGuests(received.Featuring)
            .Concat(ParseTitleFeaturedGuests(received.Title))
            .ToHashSet(StringComparer.Ordinal);
        if (expectedCredits.Count > 0 && receivedCredits.Count > 0
            && !expectedCredits.SetEquals(receivedCredits))
        {
            rejectionReason = "guest-credit-conflict";
            return false;
        }

        rejectionReason = null;
        return true;
    }

    private static HashSet<string> RecordingArtists(AudiomackSongCandidate candidate)
        => (string.IsNullOrWhiteSpace(candidate.Artist)
                ? candidate.Artists
                : new[] { candidate.Artist })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(DeezSpoTag.Web.Services.AutoTag.AutoTagSimilarity.NormalizeText)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Parses a featuring list ("HWASA, X") into normalized guest keys.</summary>
    internal static IReadOnlyList<string> ParseFeaturedGuests(string? featuring)
    {
        var guests = new List<string>();
        if (string.IsNullOrWhiteSpace(featuring))
        {
            return guests;
        }

        foreach (var key in featuring.Split(new[] { ',', ';', '&' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeGuestName)
            .Where(key => key.Length > 0 && !guests.Contains(key, StringComparer.Ordinal)))
        {
            guests.Add(key);
        }

        return guests;
    }

    /// <summary>Finds an explicit "feat./ft./featuring" credit inside a title.</summary>
    internal static IReadOnlyList<string> ParseTitleFeaturedGuests(string? title)
    {
        var guests = new List<string>();
        if (string.IsNullOrWhiteSpace(title))
        {
            return guests;
        }

        var lower = title.ToLowerInvariant();
        foreach (var marker in new[] { "featuring", "feat.", "feat ", "ft.", "ft " })
        {
            var index = lower.IndexOf(marker, StringComparison.Ordinal);
            if (index <= 0 || (!char.IsWhiteSpace(lower[index - 1]) && lower[index - 1] is not '(' and not '['))
            {
                continue;
            }

            var rest = title[(index + marker.Length)..];
            // The credit ends at the closing bracket, a dash tail, or the end of the title.
            var end = rest.Length;
            foreach (var found in new[] { ")", "]", " - ", " – " }
                .Select(terminator => rest.IndexOf(terminator, StringComparison.Ordinal))
                .Where(found => found > 0 && found < end))
            {
                end = found;
            }

            foreach (var key in ParseFeaturedGuests(rest[..end])
                .Where(key => !guests.Contains(key, StringComparer.Ordinal)))
            {
                guests.Add(key);
            }

            break;
        }

        return guests;
    }

    private static string NormalizeGuestName(string raw)
    {
        var builder = new System.Text.StringBuilder(raw.Length);
        foreach (var ch in raw.ToLowerInvariant())
        {
            builder.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool IsValidSongUri(Uri uri)
    {
        if (!uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            && !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        return uri.Host.Equals("audiomack.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".audiomack.com", StringComparison.OrdinalIgnoreCase);
    }
}
