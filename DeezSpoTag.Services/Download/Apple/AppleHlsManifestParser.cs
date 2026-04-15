using System.Linq;
using System.Text.RegularExpressions;

namespace DeezSpoTag.Services.Download.Apple;

public static class AppleHlsManifestParser
{
    private static readonly string[] LineSeparators = ["\r\n", "\n"];
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex AttributeRegex = new(
        "(?<key>[A-Z0-9-]+)=(\"(?<quoted>[^\"]*)\"|(?<raw>[^,]*))",
        RegexOptions.Compiled,
        RegexTimeout);
    private static readonly Regex ByteRangeRegex = new(
        "^(?<len>\\d+)(?:@(?<off>\\d+))?$",
        RegexOptions.Compiled,
        RegexTimeout);

    public static AppleHlsMasterManifest ParseMaster(string content, Uri baseUri)
    {
        var master = new AppleHlsMasterManifest();
        var lines = SplitLines(content);

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (IsMediaTag(line))
            {
                master.Media.Add(ParseMediaEntry(line, baseUri));
                continue;
            }

            if (IsStreamInfTag(line)
                && TryParseVariantEntry(lines, i, baseUri, out var variant))
            {
                master.Variants.Add(variant);
            }
        }

        return master;
    }

    private static bool IsMediaTag(string line)
        => line.StartsWith("#EXT-X-MEDIA", StringComparison.OrdinalIgnoreCase);

    private static bool IsStreamInfTag(string line)
        => line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase);

    private static AppleHlsMediaEntry ParseMediaEntry(string line, Uri baseUri)
    {
        var attributes = ParseAttributes(line);
        return new AppleHlsMediaEntry
        {
            Type = GetAttribute(attributes, "TYPE"),
            GroupId = GetAttribute(attributes, "GROUP-ID"),
            Name = GetAttribute(attributes, "NAME"),
            Uri = ResolveUri(baseUri, GetAttribute(attributes, "URI")),
            Default = string.Equals(GetAttribute(attributes, "DEFAULT"), "YES", StringComparison.OrdinalIgnoreCase),
            AutoSelect = string.Equals(GetAttribute(attributes, "AUTOSELECT"), "YES", StringComparison.OrdinalIgnoreCase),
            Channels = GetAttribute(attributes, "CHANNELS"),
            Characteristics = GetAttribute(attributes, "CHARACTERISTICS")
        };
    }

    private static bool TryParseVariantEntry(
        List<string> lines,
        int currentIndex,
        Uri baseUri,
        out AppleHlsVariantEntry variant)
    {
        variant = new AppleHlsVariantEntry();
        var uriLine = currentIndex + 1 < lines.Count ? lines[currentIndex + 1] : string.Empty;
        if (string.IsNullOrWhiteSpace(uriLine) || uriLine.StartsWith('#'))
        {
            return false;
        }

        var attributes = ParseAttributes(lines[currentIndex]);
        variant = new AppleHlsVariantEntry
        {
            Uri = ResolveUri(baseUri, uriLine),
            Codecs = GetAttribute(attributes, "CODECS"),
            Resolution = GetAttribute(attributes, "RESOLUTION"),
            AudioGroup = GetAttribute(attributes, "AUDIO"),
            VideoRange = GetAttribute(attributes, "VIDEO-RANGE"),
            Bandwidth = TryParseInt(GetAttribute(attributes, "BANDWIDTH")),
            AverageBandwidth = TryParseInt(GetAttribute(attributes, "AVERAGE-BANDWIDTH"))
        };
        return true;
    }

    public static AppleHlsMediaPlaylist ParseMedia(string content, Uri baseUri)
    {
        var playlist = new AppleHlsMediaPlaylist();
        var lines = SplitLines(content);
        AppleHlsByteRange? pendingRange = null;
        var lastRangeEndByUri = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            if (TryProcessKeyLine(line, playlist, baseUri))
            {
                continue;
            }

            if (TryProcessMapLine(line, playlist, baseUri, lastRangeEndByUri))
            {
                continue;
            }

            if (TryParsePendingByteRange(line, out var parsedRange))
            {
                pendingRange = parsedRange;
                continue;
            }

            if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var resolvedUri = ResolveUri(baseUri, line);
            var segmentRange = ApplyPendingRangeOffset(pendingRange, resolvedUri, lastRangeEndByUri);
            playlist.Segments.Add(new AppleHlsSegment { Uri = resolvedUri, Range = segmentRange });
            RecordRangeEnd(lastRangeEndByUri, resolvedUri, segmentRange);
            pendingRange = null;
        }

        return playlist;
    }

    private static bool TryProcessKeyLine(string line, AppleHlsMediaPlaylist playlist, Uri baseUri)
    {
        if (!line.StartsWith("#EXT-X-KEY", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var attributes = ParseAttributes(line);
        playlist.KeyUri = ResolveUri(baseUri, GetAttribute(attributes, "URI"));
        return true;
    }

    private static bool TryProcessMapLine(
        string line,
        AppleHlsMediaPlaylist playlist,
        Uri baseUri,
        Dictionary<string, long> lastRangeEndByUri)
    {
        if (!line.StartsWith("#EXT-X-MAP", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var attributes = ParseAttributes(line);
        var resolvedInitUri = ResolveUri(baseUri, GetAttribute(attributes, "URI"));
        playlist.InitSegment = resolvedInitUri;
        var initFallbackOffset = GetLastRangeEnd(lastRangeEndByUri, resolvedInitUri) ?? 0;
        var initRange = ParseByteRange(GetAttribute(attributes, "BYTERANGE"), initFallbackOffset);
        playlist.InitRange = initRange;
        RecordRangeEnd(lastRangeEndByUri, resolvedInitUri, initRange);
        return true;
    }

    private static bool TryParsePendingByteRange(string line, out AppleHlsByteRange? pendingRange)
    {
        if (line.StartsWith("#EXT-X-BYTERANGE", StringComparison.OrdinalIgnoreCase))
        {
            var rangeValue = line.Split(':', 2).ElementAtOrDefault(1);
            pendingRange = ParseByteRange(rangeValue ?? string.Empty, null);
            return true;
        }

        pendingRange = null;
        return false;
    }

    private static AppleHlsByteRange? ApplyPendingRangeOffset(
        AppleHlsByteRange? pendingRange,
        string resolvedUri,
        Dictionary<string, long> lastRangeEndByUri)
    {
        if (pendingRange == null || pendingRange.Offset != null)
        {
            return pendingRange;
        }

        var fallbackOffset = GetLastRangeEnd(lastRangeEndByUri, resolvedUri) ?? 0;
        return new AppleHlsByteRange(pendingRange.Length, fallbackOffset);
    }

    private static void RecordRangeEnd(
        Dictionary<string, long> rangeEnds,
        string uri,
        AppleHlsByteRange? range)
    {
        if (range == null)
        {
            return;
        }

        var key = ToRangeKey(uri);
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var offset = range.Offset ?? 0;
        rangeEnds[key] = offset + range.Length;
    }

    private static long? GetLastRangeEnd(Dictionary<string, long> rangeEnds, string uri)
    {
        var key = ToRangeKey(uri);
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        return rangeEnds.TryGetValue(key, out var end) ? end : null;
    }

    private static string ToRangeKey(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(uri, UriKind.Absolute, out var absolute))
        {
            var plain = uri;
            var queryIndex = plain.IndexOf('?', StringComparison.Ordinal);
            if (queryIndex >= 0)
            {
                plain = plain[..queryIndex];
            }

            var fragmentIndex = plain.IndexOf('#', StringComparison.Ordinal);
            if (fragmentIndex >= 0)
            {
                plain = plain[..fragmentIndex];
            }

            return plain;
        }

        var builder = new UriBuilder(absolute)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri.AbsoluteUri;
    }

    private static Dictionary<string, string> ParseAttributes(string line)
    {
        var idx = line.IndexOf(':');
        if (idx >= 0)
        {
            line = line[(idx + 1)..];
        }

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var groups in AttributeRegex.Matches(line).Select(static match => match.Groups))
        {
            var key = groups["key"].Value;
            var value = groups["quoted"].Success ? groups["quoted"].Value : groups["raw"].Value;
            dict[key] = value;
        }
        return dict;
    }

    private static string GetAttribute(Dictionary<string, string> attributes, string key)
    {
        return attributes.TryGetValue(key, out var value) ? value : string.Empty;
    }

    private static string ResolveUri(Uri baseUri, string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return string.Empty;
        }

        if (Uri.TryCreate(uri, UriKind.Absolute, out var absolute))
        {
            return absolute.ToString();
        }

        return new Uri(baseUri, uri).ToString();
    }

    private static int TryParseInt(string value)
    {
        return int.TryParse(value, out var result) ? result : 0;
    }

    private static List<string> SplitLines(string content)
    {
        return content.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static AppleHlsByteRange? ParseByteRange(string raw, long? lastRangeEnd)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var match = ByteRangeRegex.Match(raw.Trim());
        if (!match.Success)
        {
            return null;
        }

        var length = long.Parse(match.Groups["len"].Value);
        long? offset = null;
        if (match.Groups["off"].Success)
        {
            offset = long.Parse(match.Groups["off"].Value);
        }
        else if (lastRangeEnd != null)
        {
            offset = lastRangeEnd;
        }

        return new AppleHlsByteRange(length, offset);
    }
}

public sealed class AppleHlsMasterManifest
{
    public List<AppleHlsVariantEntry> Variants { get; } = new();
    public List<AppleHlsMediaEntry> Media { get; } = new();
}

public sealed class AppleHlsVariantEntry
{
    public string Uri { get; set; } = "";
    public string Codecs { get; set; } = "";
    public string Resolution { get; set; } = "";
    public string AudioGroup { get; set; } = "";
    public string VideoRange { get; set; } = "";
    public int Bandwidth { get; set; }
    public int AverageBandwidth { get; set; }
}

public sealed class AppleHlsMediaEntry
{
    public string Type { get; set; } = "";
    public string GroupId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Uri { get; set; } = "";
    public bool Default { get; set; }
    public bool AutoSelect { get; set; }
    public string Channels { get; set; } = "";
    public string Characteristics { get; set; } = "";
}

public sealed class AppleHlsMediaPlaylist
{
    public string InitSegment { get; set; } = "";
    public AppleHlsByteRange? InitRange { get; set; }
    public string KeyUri { get; set; } = "";
    public List<AppleHlsSegment> Segments { get; } = new();
}

public sealed class AppleHlsSegment
{
    public string Uri { get; set; } = "";
    public AppleHlsByteRange? Range { get; set; }
}

public sealed record AppleHlsByteRange(long Length, long? Offset);
