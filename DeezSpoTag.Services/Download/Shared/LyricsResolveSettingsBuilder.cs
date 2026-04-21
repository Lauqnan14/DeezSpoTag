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
            LrcType = ResolveTypes(tagSettings, allowsSyncedBySettings, allowsUnsyncedBySettings),
            LrcFormat = settings.LrcFormat,
            LyricsFallbackEnabled = settings.LyricsFallbackEnabled,
            LyricsFallbackOrder = settings.LyricsFallbackOrder,
            DeezerCountry = settings.DeezerCountry,
            Arl = settings.Arl,
            AppleMusic = settings.AppleMusic,
            AuthorizationToken = settings.AuthorizationToken,
            Tags = new TagSettings
            {
                Lyrics = tagSettings.Lyrics && allowsUnsyncedBySettings,
                SyncedLyrics = tagSettings.SyncedLyrics && allowsSyncedBySettings
            }
        };
    }

    private static string ResolveTypes(TagSettings tagSettings, bool allowsSynced, bool allowsUnsynced)
    {
        var types = new List<string>();
        if (tagSettings.SyncedLyrics && allowsSynced)
        {
            types.Add(LyricsType);
            types.Add(SyllableLyricsType);
        }

        if (tagSettings.Lyrics && allowsUnsynced)
        {
            types.Add(UnsyncedLyricsType);
        }

        if (types.Count == 0)
        {
            types.Add(LyricsType);
        }

        return string.Join(',', types.Distinct(StringComparer.OrdinalIgnoreCase));
    }
}
