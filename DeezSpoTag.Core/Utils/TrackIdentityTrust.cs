using System.Text.RegularExpressions;

namespace DeezSpoTag.Core.Utils;

public static class TrackIdentityTrust
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly Regex RepeatedNumericFilenamePrefixRegex = new(
        @"^\s*(?:\d+\s*[-._)\]]\s*){2,}",
        RegexOptions.Compiled,
        RegexTimeout);
    private static readonly Regex NoisyCoreTagRegex = new(
        @"\b(?:official|audio|video|lyrics?|visualizer|final|finished|master|unknown)\b|(?:\.mp3|\.wav|\.m4a|\.aac)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        RegexTimeout);

    public static bool HasRepeatedNumericFilenamePrefix(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var fileName = Path.GetFileNameWithoutExtension(path);
            return !string.IsNullOrWhiteSpace(fileName)
                && RepeatedNumericFilenamePrefixRegex.IsMatch(fileName);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public static bool IsWeakMetadataValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var normalized = value
            .Trim()
            .Trim('[', ']')
            .ToLowerInvariant();
        if (normalized is "unknown"
            or "unknown artist"
            or "unknown album artist"
            or "unknown album"
            or "untitled"
            or "track"
            or "audio")
        {
            return true;
        }

        if (normalized.Length < 2)
        {
            return true;
        }

        return NoisyCoreTagRegex.IsMatch(normalized);
    }

    public static bool IsUntrustedIdentity(string? title, string? artist, string? filePath)
    {
        return IsWeakMetadataValue(title)
            || IsWeakMetadataValue(artist)
            || HasRepeatedNumericFilenamePrefix(filePath);
    }
}
