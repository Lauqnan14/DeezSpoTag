using DeezSpoTag.Core.Models.Settings;
using System.Linq;

namespace DeezSpoTag.Services.Download.Shared;

public static class LyricsSettingsPolicy
{
    private const string LyricsType = "lyrics";
    private const string UnsyncedLyricsType = "unsynced-lyrics";
    private const string SyllableLyricsType = "syllable-lyrics";
    private const string TtmlLyricsType = "ttml-lyrics";

    /// <summary>
    /// Legacy rich-type priority, used as the default type order and to append
    /// rich types the caller did not supply. Plain lyrics are never part of it;
    /// they are pinned last by <see cref="NormalizeLyricsTypeOrder"/> and by the
    /// resolver regardless of their position in a supplied order.
    /// </summary>
    public const string DefaultLyricsTypeOrder = "ttml-lyrics,syllable-lyrics,lyrics";

    /// <summary>
    /// Normalizes a supplied lyrics type order into canonical CSV: known aliases
    /// map to canonical tokens, unknown tokens and duplicates are dropped, the
    /// supplied rich order is preserved, missing rich types are appended in
    /// legacy default order, and plain lyrics are pinned last when supplied.
    /// This is a pure preference string: it never enables a type or source, and
    /// enabled-type membership continues to come from LrcType and the enable
    /// settings alone.
    /// </summary>
    public static string NormalizeLyricsTypeOrder(string? rawValue)
    {
        var supplied = (rawValue ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeTypeToken)
            .Where(static token => KnownLyricsTypes.Contains(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var richOrder = new List<string>();
        var plainSupplied = false;
        foreach (var token in supplied)
        {
            if (token == UnsyncedLyricsType)
            {
                plainSupplied = true;
                continue;
            }

            richOrder.Add(token);
        }

        // Legacy default order for rich types the caller omitted.
        foreach (var richType in (string[])[TtmlLyricsType, SyllableLyricsType, LyricsType])
        {
            if (!richOrder.Contains(richType, StringComparer.OrdinalIgnoreCase))
            {
                richOrder.Add(richType);
            }
        }

        if (plainSupplied)
        {
            richOrder.Add(UnsyncedLyricsType);
        }

        return string.Join(",", richOrder);
    }

    /// <summary>
    /// The rich-type portion of a lyrics type order, normalized. Plain lyrics are
    /// removed because they are pinned last by the resolver in every plan and their
    /// availability is carried separately; the rich order is the only ordering
    /// information the value adds.
    /// </summary>
    public static string GetRichLyricsTypeOrder(string? rawValue)
        => string.Join(
            ",",
            NormalizeLyricsTypeOrder(rawValue)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static token => !string.Equals(token, UnsyncedLyricsType, StringComparison.OrdinalIgnoreCase)));

    private static readonly HashSet<string> KnownLyricsTypes =
        [LyricsType, SyllableLyricsType, TtmlLyricsType, UnsyncedLyricsType];

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
        var timingPreference = LrcTimingModes.Normalize(settings.LrcTimingPreference, settings.PreferEnhancedLrc);
        var hasPlainTimedLyrics = types.Contains(LyricsType)
            && !LrcTimingModes.RequiresWordTiming(timingPreference);
        var hasSyllableLyrics = types.Contains(SyllableLyricsType)
            && LrcTimingModes.ImpliesEnhanced(timingPreference);
        var synthesizesTtml = settings.SynthesizeLrcFromTtml && types.Contains(TtmlLyricsType);
        return formats.Contains("lrc")
            && (hasPlainTimedLyrics || hasSyllableLyrics || synthesizesTtml);
    }

    public static bool WantsEnhancedLrc(DeezSpoTagSettings settings)
        => WantsLrcOutput(settings)
           && ParseSelectedTypes(settings.LrcType).Contains(SyllableLyricsType)
           && LrcTimingModes.ImpliesEnhanced(
               LrcTimingModes.Normalize(settings.LrcTimingPreference, settings.PreferEnhancedLrc));

    public static bool WantsLineSyncedLrc(DeezSpoTagSettings settings)
        => WantsLrcOutput(settings)
           && ParseSelectedTypes(settings.LrcType).Contains(LyricsType)
           && !LrcTimingModes.RequiresWordTiming(
               LrcTimingModes.Normalize(settings.LrcTimingPreference, settings.PreferEnhancedLrc));

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
