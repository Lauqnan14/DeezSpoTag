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
            if (!uri.Host.EndsWith("audiomack.com", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            value = segments.LastOrDefault() ?? string.Empty;
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

        if (!value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-'))
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
            || !uri.Host.EndsWith("audiomack.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // Expected shape: /<artist>/song/<song> — tolerate omitting the "song" segment.
        if (segments.Length >= 3
            && segments[1].Equals("song", StringComparison.OrdinalIgnoreCase))
        {
            artistSlug = segments[0];
            songSlug = segments[2];
        }
        else if (segments.Length == 2)
        {
            artistSlug = segments[0];
            songSlug = segments[1];
        }
        else
        {
            return false;
        }

        return Normalize(artistSlug) is { Length: > 0 } normalizedArtist
            && Normalize(songSlug) is { Length: > 0 } normalizedSong
            && (artistSlug = normalizedArtist) is not null
            && (songSlug = normalizedSong) is not null;
    }
}
