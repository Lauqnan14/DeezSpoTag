namespace DeezSpoTag.Core.Models.Settings;

public static class LrcTimingModes
{
    public const string Line = "line";
    public const string WordEnhanced = "word-enhanced";
    public const string PreferEnhanced = "prefer-enhanced";

    public static string Normalize(string? value, bool? preferEnhancedLrc = null)
    {
        var token = (value ?? string.Empty).Trim().ToLowerInvariant();
        return token switch
        {
            Line or "line-timed" or "line-level" or "standard" => Line,
            WordEnhanced or "word" or "enhanced" or "word-only" or "enhanced-only" => WordEnhanced,
            PreferEnhanced or "prefer" or "prefer-enhanced-else-line" => PreferEnhanced,
            _ => preferEnhancedLrc == false ? Line : PreferEnhanced
        };
    }

    public static bool ImpliesEnhanced(string? value)
        => !string.Equals(Normalize(value), Line, StringComparison.Ordinal);

    public static bool RequiresWordTiming(string? value)
        => string.Equals(Normalize(value), WordEnhanced, StringComparison.Ordinal);
}
