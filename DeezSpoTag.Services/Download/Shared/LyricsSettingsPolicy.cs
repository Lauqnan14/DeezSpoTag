using DeezSpoTag.Core.Models.Settings;
using System.Linq;

namespace DeezSpoTag.Services.Download.Shared;

public static class LyricsSettingsPolicy
{
    private const string LyricsType = "lyrics";
    private const string UnsyncedLyricsType = "unsynced-lyrics";
    private const string SyllableLyricsType = "syllable-lyrics";
    private const string TtmlLyricsType = "ttml-lyrics";

    public static bool IsLyricsGateEnabled(DeezSpoTagSettings settings)
    {
        return settings.SyncedLyrics
            || settings.SaveLyrics;
    }

    public static bool CanFetchLyrics(DeezSpoTagSettings settings)
    {
        if (!IsLyricsGateEnabled(settings))
        {
            return false;
        }

        var selected = ParseSelectedTypes(settings.LrcType);
        return selected.Contains(LyricsType)
            || selected.Contains(SyllableLyricsType)
            || selected.Contains(TtmlLyricsType)
            || selected.Contains(UnsyncedLyricsType);
    }

    public static bool WantsTtmlOutput(DeezSpoTagSettings settings)
    {
        if (!settings.SyncedLyrics || !IsLyricsGateEnabled(settings))
        {
            return false;
        }

        var formats = ParseOutputFormats(settings.LrcFormat);

        return ParseSelectedTypes(settings.LrcType).Contains(TtmlLyricsType)
            && formats.Contains("ttml");
    }

    public static bool WantsLrcOutput(DeezSpoTagSettings settings)
    {
        if (!settings.SyncedLyrics || !IsLyricsGateEnabled(settings))
        {
            return false;
        }

        var formats = ParseOutputFormats(settings.LrcFormat);
        var types = ParseSelectedTypes(settings.LrcType);
        return formats.Contains("lrc")
            && (types.Contains(LyricsType)
                || types.Contains(SyllableLyricsType)
                || (settings.SynthesizeLrcFromTtml && types.Contains(TtmlLyricsType)));
    }

    public static bool WantsEnhancedLrc(DeezSpoTagSettings settings)
        => WantsLrcOutput(settings)
           && ParseSelectedTypes(settings.LrcType).Contains(SyllableLyricsType)
           && LrcTimingModes.ImpliesEnhanced(settings.LrcTimingPreference);

    public static bool WantsLineSyncedLrc(DeezSpoTagSettings settings)
        => WantsLrcOutput(settings)
           && ParseSelectedTypes(settings.LrcType).Contains(LyricsType)
           && !LrcTimingModes.RequiresWordTiming(settings.LrcTimingPreference);

    public static bool WantsUnsyncedTextOutput(DeezSpoTagSettings settings)
        => settings.SaveLyrics
            && IsLyricsGateEnabled(settings)
            && ParseSelectedTypes(settings.LrcType).Contains(UnsyncedLyricsType);

    private static HashSet<string> ParseOutputFormats(string? rawValue)
    {
        var formats = (rawValue ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static token => token.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (formats.Overlaps(["both", "richlyrics", "rich-lyrics", "all", "lrc+ttml"]))
        {
            formats.Add("lrc");
            formats.Add("ttml");
        }

        return formats;
    }

    private static HashSet<string> ParseSelectedTypes(string? rawValue)
    {
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var normalized in (rawValue ?? string.Empty)
                     .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Select(NormalizeTypeToken)
                     .Where(static token => !string.IsNullOrWhiteSpace(token)))
        {
            selected.Add(normalized);
        }

        if (selected.Count == 0)
        {
            selected.Add(LyricsType);
            selected.Add(SyllableLyricsType);
            selected.Add(TtmlLyricsType);
            selected.Add(UnsyncedLyricsType);
        }

        return selected;
    }

    private static string NormalizeTypeToken(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            LyricsType => LyricsType,
            "synced-lyrics" => LyricsType,
            SyllableLyricsType => SyllableLyricsType,
            "time-synced-lyrics" => SyllableLyricsType,
            "timesynced-lyrics" => SyllableLyricsType,
            "time_synced_lyrics" => SyllableLyricsType,
            TtmlLyricsType => TtmlLyricsType,
            "ttml" => TtmlLyricsType,
            "ttmllyrics" => TtmlLyricsType,
            "ttml_lyrics" => TtmlLyricsType,
            UnsyncedLyricsType => UnsyncedLyricsType,
            "unsyncedlyrics" => UnsyncedLyricsType,
            "unsynced" => UnsyncedLyricsType,
            "unsynchronized-lyrics" => UnsyncedLyricsType,
            "unsynchronised-lyrics" => UnsyncedLyricsType,
            _ => normalized
        };
    }
}
