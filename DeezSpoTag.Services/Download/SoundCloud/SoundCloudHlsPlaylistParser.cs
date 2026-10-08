using System.Globalization;

namespace DeezSpoTag.Services.Download.SoundCloud;

/// <summary>
///     One <c>#EXT-X-KEY</c> directive: an encryption method, a resolved key URI, and an optional IV.
/// </summary>
/// <param name="Method">The declared method. Only <c>NONE</c> and <c>AES-128</c> reach this type.</param>
/// <param name="Uri">The absolute key URI.</param>
/// <param name="Iv">The explicit IV as written in the playlist, which may be empty.</param>
public sealed record SoundCloudHlsKey(string Method, string Uri, string Iv);

/// <summary>
///     One playable segment of a media playlist.
/// </summary>
/// <param name="Uri">The absolute segment URI.</param>
/// <param name="DurationSeconds">The declared duration, carried for diagnostics.</param>
/// <param name="Key">The key in force for this segment, or <see langword="null"/> when it is not encrypted.</param>
public sealed record SoundCloudHlsSegment(string Uri, double DurationSeconds, SoundCloudHlsKey? Key);

/// <summary>
///     A parsed SoundCloud media playlist.
/// </summary>
/// <param name="MediaSequence">
///     The <c>#EXT-X-MEDIA-SEQUENCE</c> value. It seeds the IV for any segment that does not declare one, which
///     is how HLS derives an implicit IV, and is therefore load-bearing rather than informational.
/// </param>
/// <param name="Segments">The segments, in playlist order.</param>
public sealed record SoundCloudHlsPlaylist(long MediaSequence, IReadOnlyList<SoundCloudHlsSegment> Segments);

/// <summary>
///     Parses the subset of HLS SoundCloud publishes.
/// </summary>
/// <remarks>
///     <para>
///         Written by hand rather than taken from a package: SoundCloud emits a narrow, stable subset, and the
///         engine only needs to refuse anything it cannot actually assemble.
///     </para>
///     <para>
///         A master playlist is rejected rather than followed. A variant list means the caller picked the wrong
///         URL, and silently choosing one variant would download audio nobody asked for.
///     </para>
/// </remarks>
public static class SoundCloudHlsPlaylistParser
{
    private const string ExtInfTag = "#EXTINF:";
    private const string MediaSequenceTag = "#EXT-X-MEDIA-SEQUENCE:";
    private const string KeyTag = "#EXT-X-KEY:";
    private const string StreamInfTag = "#EXT-X-STREAM-INF:";
    private const string TagPrefix = "#";

    private static readonly char[] TrimChars = ['\r', '\n', ' ', '\t'];

    /// <summary>
    ///     Parses a media playlist whose segment and key URIs are relative to <paramref name="playlistUrl"/>.
    /// </summary>
    /// <param name="playlistUrl">The playlist's own URL, used as the base for relative URIs.</param>
    /// <param name="content">The playlist body.</param>
    /// <returns>The parsed playlist.</returns>
    /// <exception cref="SoundCloudNoStreamException">The playlist is unusable.</exception>
    public static SoundCloudHlsPlaylist Parse(string playlistUrl, string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (!Uri.TryCreate(playlistUrl, UriKind.Absolute, out var baseUri))
        {
            throw new SoundCloudNoStreamException("The SoundCloud media playlist URL is not absolute.");
        }

        if (content.Contains(StreamInfTag, StringComparison.OrdinalIgnoreCase))
        {
            throw new SoundCloudNoStreamException(
                "SoundCloud returned a master playlist. Only a media playlist can be assembled.");
        }

        var mediaSequence = 0L;
        var segments = new List<SoundCloudHlsSegment>();
        var currentKey = (SoundCloudHlsKey?)null;
        var pendingDuration = 0d;
        var sawExtInf = false;

        foreach (var line in content.Split('\n')
            .Select(rawLine => rawLine.Trim(TrimChars))
            .Where(line => line.Length > 0))
        {
            if (line.StartsWith(MediaSequenceTag, StringComparison.Ordinal))
            {
                mediaSequence = ParseMediaSequence(line[MediaSequenceTag.Length..]);
                continue;
            }

            if (line.StartsWith(KeyTag, StringComparison.Ordinal))
            {
                currentKey = ParseKey(line[KeyTag.Length..], baseUri);
                continue;
            }

            if (line.StartsWith(ExtInfTag, StringComparison.Ordinal))
            {
                pendingDuration = ParseDuration(line[ExtInfTag.Length..]);
                sawExtInf = true;
                continue;
            }

            if (line.StartsWith(TagPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (!sawExtInf)
            {
                throw new SoundCloudNoStreamException(
                    "A SoundCloud media playlist segment had no #EXTINF header.");
            }

            segments.Add(new SoundCloudHlsSegment(
                ResolveUri(line, baseUri),
                pendingDuration,
                currentKey));
            pendingDuration = 0d;
            sawExtInf = false;
        }

        if (segments.Count == 0)
        {
            throw new SoundCloudNoStreamException("The SoundCloud media playlist contains no segments.");
        }

        return new SoundCloudHlsPlaylist(mediaSequence, segments);
    }

    private static long ParseMediaSequence(string value)
    {
        var separator = value.IndexOf(',');
        if (separator >= 0)
        {
            value = value[..separator];
        }

        return long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
               && parsed >= 0
            ? parsed
            : 0L;
    }

    private static double ParseDuration(string value)
    {
        var separator = value.IndexOf(',');
        if (separator >= 0)
        {
            value = value[..separator];
        }

        return double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0d;
    }

    private static SoundCloudHlsKey? ParseKey(string attributes, Uri baseUri)
    {
        var method = ReadAttribute(attributes, "METHOD");
        if (method.Length == 0)
        {
            throw new SoundCloudNoStreamException("A SoundCloud #EXT-X-KEY had no METHOD.");
        }

        if (method.Equals("NONE", StringComparison.OrdinalIgnoreCase))
        {
            // Explicitly clears the key for the segments that follow, which is how a playlist rotates back to
            // plaintext. Returning null is exactly that.
            return null;
        }

        if (!method.Equals("AES-128", StringComparison.OrdinalIgnoreCase))
        {
            throw new SoundCloudNoStreamException(
                $"SoundCloud used HLS encryption method '{method}', which DeezSpoTag does not decrypt.");
        }

        var keyUri = ReadAttribute(attributes, "URI");
        if (keyUri.Length == 0)
        {
            throw new SoundCloudNoStreamException("A SoundCloud #EXT-X-KEY:AES-128 had no URI.");
        }

        return new SoundCloudHlsKey(method, ResolveUri(keyUri, baseUri), ReadAttribute(attributes, "IV"));
    }

    private static string ReadAttribute(string attributes, string name)
    {
        foreach (var pair in attributes.Split(','))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            if (!pair[..separator].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = pair[(separator + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1];
            }

            return value;
        }

        return string.Empty;
    }

    private static string ResolveUri(string value, Uri baseUri)
    {
        var trimmed = value.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute))
        {
            return absolute.ToString();
        }

        if (Uri.TryCreate(baseUri, trimmed, out var resolved))
        {
            return resolved.ToString();
        }

        throw new SoundCloudNoStreamException(
            $"A SoundCloud media playlist contained an unresolvable URI '{LogSanitizerUrl(trimmed)}'.");
    }

    private static string LogSanitizerUrl(string value)
        => SoundCloudUrlRedactor.Redact(value);
}