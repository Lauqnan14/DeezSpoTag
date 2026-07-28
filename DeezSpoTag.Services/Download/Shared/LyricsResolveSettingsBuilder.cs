using DeezSpoTag.Core.Models.Settings;

namespace DeezSpoTag.Services.Download.Shared;

internal static class LyricsResolveSettingsBuilder
{
    private const string LyricsType = "lyrics";
    private const string UnsyncedLyricsType = "unsynced-lyrics";
    private const string SyllableLyricsType = "syllable-lyrics";

    public static DeezSpoTagSettings Build(DeezSpoTagSettings settings, TagSettings tagSettings)
    {
        var allowsSyncedBySettings = settings.SyncedLyrics;
        var allowsUnsyncedBySettings = settings.SaveLyrics;

        return new DeezSpoTagSettings
        {
            SyncedLyrics = allowsSyncedBySettings,
            SaveLyrics = allowsUnsyncedBySettings,
            LrcType = ResolveTypes(
                tagSettings,
                allowsSyncedBySettings,
                allowsUnsyncedBySettings,
                settings.LrcType),
            LrcFormat = settings.LrcFormat,
            SynthesizeLrcFromTtml = settings.SynthesizeLrcFromTtml,
            LyricsFallbackEnabled = settings.LyricsFallbackEnabled,
            LyricsFallbackOrder = settings.LyricsFallbackOrder,
            DeezerCountry = settings.DeezerCountry,
            AppleMusic = settings.AppleMusic,
            AuthorizationToken = settings.AuthorizationToken,
            Tags = new TagSettings
            {
                Lyrics = tagSettings.Lyrics && allowsUnsyncedBySettings,
                SyncedLyrics = tagSettings.SyncedLyrics && allowsSyncedBySettings
            }
        };
    }

    private static string ResolveTypes(
        TagSettings tagSettings,
        bool allowsSynced,
        bool allowsUnsynced,
        string? configuredTypes)
    {
        var types = new List<string>();
        var selected = (configuredTypes ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeType)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Count == 0)
        {
            selected.UnionWith([LyricsType, SyllableLyricsType, "ttml-lyrics", UnsyncedLyricsType]);
        }

        if (tagSettings.SyncedLyrics && allowsSynced)
        {
            foreach (var type in new[] { LyricsType, SyllableLyricsType, "ttml-lyrics" })
            {
                if (selected.Contains(type))
                {
                    types.Add(type);
                }
            }
        }

        if (tagSettings.Lyrics && allowsUnsynced && selected.Contains(UnsyncedLyricsType))
        {
            types.Add(UnsyncedLyricsType);
        }

        if (types.Count == 0)
        {
            types.Add(LyricsType);
        }

        return string.Join(',', types.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string NormalizeType(string value)
        => value.Trim().ToLowerInvariant() switch
        {
            "synced-lyrics" => LyricsType,
            "time-synced-lyrics" or "timesynced-lyrics" or "time_synced_lyrics" => SyllableLyricsType,
            "ttml" or "ttmllyrics" or "ttml_lyrics" => "ttml-lyrics",
            "unsyncedlyrics" or "unsynced" or "unsynchronized-lyrics" or "unsynchronised-lyrics" => UnsyncedLyricsType,
            var normalized => normalized
        };
}
