using System.Text.RegularExpressions;

namespace DeezSpoTag.Core.Models;

public enum LrcTimingKind
{
    None,
    Line,
    Word
}

public static partial class LrcContent
{
    [GeneratedRegex(@"\[\d{1,3}:\d{2}(?:[.:]\d{1,3})?\]", RegexOptions.CultureInvariant)]
    private static partial Regex LineTimestampPattern();

    [GeneratedRegex(@"\[\d{1,3}:\d{2}(?:[.:]\d{1,3})?\][^\r\n]*<\d{1,3}:\d{2}(?:[.:]\d{1,3})?>", RegexOptions.CultureInvariant)]
    private static partial Regex AngleBracketWordPattern();

    // Karaoke / enhanced LRC that timestamps each word with [mm:ss] instead of <mm:ss>.
    // Dual line-start stamps ("[00:12.00][00:45.00]Chorus") have no lyric text between
    // the brackets and stay line-synced.
    [GeneratedRegex(@"\[\d{1,3}:\d{2}(?:[.:]\d{1,3})?\][^\r\n\[\]]*\S[^\r\n\[\]]*\[\d{1,3}:\d{2}(?:[.:]\d{1,3})?\]", RegexOptions.CultureInvariant)]
    private static partial Regex InterleavedBracketWordPattern();

    public static LrcTimingKind ClassifyTiming(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return LrcTimingKind.None;
        }

        if (AngleBracketWordPattern().IsMatch(content)
            || InterleavedBracketWordPattern().IsMatch(content))
        {
            return LrcTimingKind.Word;
        }

        return LineTimestampPattern().IsMatch(content)
            ? LrcTimingKind.Line
            : LrcTimingKind.None;
    }

    public static LrcTimingKind ClassifyTiming(IEnumerable<string>? lines)
        => lines == null ? LrcTimingKind.None : ClassifyTiming(string.Join('\n', lines));

    public static bool IsWordSynchronized(string? content)
        => ClassifyTiming(content) == LrcTimingKind.Word;

    public static bool IsWordSynchronized(IEnumerable<string>? lines)
        => ClassifyTiming(lines) == LrcTimingKind.Word;
}
