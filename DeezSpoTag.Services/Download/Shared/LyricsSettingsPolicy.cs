using DeezSpoTag.Core.Models.Settings;

namespace DeezSpoTag.Services.Download.Shared;

public static class LyricsSettingsPolicy
{
    private const string LyricsType = "lyrics";
    private const string UnsyncedLyricsType = "unsynced-lyrics";
    private const string SyllableLyricsType = "syllable-lyrics";

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
            || selected.Contains(UnsyncedLyricsType);
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
            UnsyncedLyricsType => UnsyncedLyricsType,
            "unsyncedlyrics" => UnsyncedLyricsType,
            "unsynced" => UnsyncedLyricsType,
            "unsynchronized-lyrics" => UnsyncedLyricsType,
            "unsynchronised-lyrics" => UnsyncedLyricsType,
            _ => normalized
        };
    }
}
