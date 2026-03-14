using DeezSpoTag.Core.Models;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DeezSpoTag.Services.Download.Shared.Utils;

public static class LyricsPrefetchTypeHelper
{
    public static string ResolveFromLyrics(LyricsBase? lyrics)
    {
        if (lyrics == null)
        {
            return string.Empty;
        }

        var hasTtml = !string.IsNullOrWhiteSpace(lyrics.TtmlLyrics);
        var hasLrc = lyrics.SyncedLyrics?.Any(line => line.IsValid()) == true;
        var hasTxt = !string.IsNullOrWhiteSpace(lyrics.UnsyncedLyrics);
        return ComposeLyricsType(hasTtml, hasLrc, hasTxt);
    }

    public static string ResolveSavedLyricsType(string? directoryPath, string? baseFileName)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || string.IsNullOrWhiteSpace(baseFileName))
        {
            return string.Empty;
        }

        if (!Directory.Exists(directoryPath))
        {
            return string.Empty;
        }

        var normalizedBaseName = Path.GetFileNameWithoutExtension(baseFileName);
        if (string.IsNullOrWhiteSpace(normalizedBaseName))
        {
            normalizedBaseName = baseFileName.Trim();
        }

        var hasTtml = File.Exists(Path.Join(directoryPath, $"{normalizedBaseName}.ttml"));
        var hasLrc = File.Exists(Path.Join(directoryPath, $"{normalizedBaseName}.lrc"));
        var hasTxt = File.Exists(Path.Join(directoryPath, $"{normalizedBaseName}.txt"));
        return ComposeLyricsType(hasTtml, hasLrc, hasTxt);
    }

    private static string ComposeLyricsType(bool hasTtml, bool hasLrc, bool hasTxt)
    {
        var status = new List<string>(3);
        if (hasTtml)
        {
            status.Add("time-synced");
        }
        if (hasLrc)
        {
            status.Add("synced");
        }
        if (hasTxt)
        {
            status.Add("unsynced");
        }

        return string.Join(",", status);
    }
}
