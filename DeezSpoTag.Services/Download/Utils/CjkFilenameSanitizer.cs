using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DeezSpoTag.Services.Download.Utils;

/// <summary>
/// Centralized filename/path-segment sanitation with explicit CJK detection.
/// Keeps Unicode (including CJK) intact while removing filesystem-invalid chars.
/// </summary>
public static class CjkFilenameSanitizer
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex CollapseWhitespaceRegex = new(@"\s+", RegexOptions.Compiled, RegexTimeout);

    private static readonly HashSet<char> InvalidFileNameChars = new(Path.GetInvalidFileNameChars())
    {
        '<', '>', ':', '"', '/', '\\', '|', '?', '*'
    };

    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static readonly (int Start, int End)[] CjkCodeRanges =
    {
        (0x1100, 0x11FF), (0x2E80, 0x2EFF), (0x2F00, 0x2FDF), (0x2FF0, 0x2FFF),
        (0x3000, 0x303F), (0x3040, 0x309F), (0x30A0, 0x30FF), (0x3130, 0x318F),
        (0x31C0, 0x31EF), (0x31F0, 0x31FF), (0x3200, 0x32FF), (0x3300, 0x33FF),
        (0x3400, 0x4DBF), (0x4E00, 0x9FFF), (0xA960, 0xA97F), (0xAC00, 0xD7AF),
        (0xD7B0, 0xD7FF), (0xF900, 0xFAFF), (0xFE30, 0xFE4F), (0xFF65, 0xFF9F),
        (0xFFA0, 0xFFDC), (0x1AFF0, 0x1AFFF), (0x1B000, 0x1B0FF), (0x1B100, 0x1B12F),
        (0x1B130, 0x1B16F), (0x1F200, 0x1F2FF), (0x20000, 0x2A6DF), (0x2A700, 0x2B73F),
        (0x2B740, 0x2B81F), (0x2B820, 0x2CEAF), (0x2CEB0, 0x2EBEF), (0x2EBF0, 0x2EE5F),
        (0x2F800, 0x2FA1F), (0x30000, 0x3134F), (0x31350, 0x323AF)
    };

    public static bool ContainsCjk(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.EnumerateRunes()
            .Select(rune => rune.Value)
            .Any(IsCjkCodePoint);
    }

    public static string SanitizeSegment(
        string? value,
        string fallback = "Unknown",
        string replacement = "_",
        bool collapseWhitespace = false,
        bool trimTrailingDotsAndSpaces = false,
        int maxLength = 0)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var replacementValue = NormalizeReplacement(replacement);
        var normalized = value.Normalize(NormalizationForm.FormC);
        var builder = new StringBuilder(normalized.Length);

        foreach (var rune in normalized.EnumerateRunes())
        {
            if (IsControlRune(rune))
            {
                builder.Append(replacementValue);
                continue;
            }

            if (rune.Value <= char.MaxValue && InvalidFileNameChars.Contains((char)rune.Value))
            {
                builder.Append(replacementValue);
                continue;
            }

            builder.Append(rune);
        }

        var cleaned = builder.ToString();
        cleaned = collapseWhitespace
            ? CollapseWhitespaceRegex.Replace(cleaned, " ").Trim()
            : cleaned.Trim();

        if (trimTrailingDotsAndSpaces)
        {
            cleaned = cleaned.TrimEnd('.', ' ');
        }

        if (maxLength > 0)
        {
            cleaned = TruncateByRuneCount(cleaned, maxLength);
        }

        cleaned = ProtectWindowsReservedName(cleaned, replacementValue);
        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
    }

    public static string TruncateByRuneCount(string value, int maxRunes)
    {
        if (string.IsNullOrEmpty(value) || maxRunes <= 0)
        {
            return string.Empty;
        }

        var count = 0;
        var builder = new StringBuilder(value.Length);

        foreach (var rune in value.EnumerateRunes())
        {
            if (count >= maxRunes)
            {
                break;
            }

            builder.Append(rune);
            count++;
        }

        return builder.ToString();
    }

    private static string NormalizeReplacement(string replacement)
    {
        if (string.IsNullOrEmpty(replacement))
        {
            return string.Empty;
        }

        return replacement.Any(ch => char.IsControl(ch) || InvalidFileNameChars.Contains(ch))
            ? "_"
            : replacement;
    }

    private static bool IsControlRune(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        return category == UnicodeCategory.Control
            || category == UnicodeCategory.Surrogate
            || category == UnicodeCategory.OtherNotAssigned;
    }

    private static string ProtectWindowsReservedName(string value, string replacement)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var candidate = value.TrimEnd('.', ' ');
        if (WindowsReservedNames.Contains(candidate))
        {
            return $"{(string.IsNullOrEmpty(replacement) ? "_" : replacement)}{value}";
        }

        return value;
    }

    // Ported from the Apple reference's full CJK detector.
    private static bool IsCjkCodePoint(int r)
    {
        return CjkCodeRanges.Any(range => r >= range.Start && r <= range.End);
    }
}
