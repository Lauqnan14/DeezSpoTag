using DeezSpoTag.Core.Models;
using DeezSpoTag.Services.Apple;

namespace DeezSpoTag.Web.Services;

internal static class LyricsSidecarTimingBadges
{
    public static IReadOnlyList<string> FromAudioPath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return Array.Empty<string>();
        }

        TryRead(Path.ChangeExtension(filePath, ".ttml"), out var ttml);
        TryRead(Path.ChangeExtension(filePath, ".lrc"), out var lrc);
        var hasTxt = File.Exists(Path.ChangeExtension(filePath, ".txt"));
        return FromSidecars(ttml, lrc, hasTxt);
    }

    /// <summary>
    /// Matches Lyrics Settings: line-synced .lrc → synced, word-synced .lrc → enhanced,
    /// word-synced .ttml → ttml, .txt only when no synchronized sidecar is present.
    /// Line-synced TTML is never a TTML lyrics badge.
    /// </summary>
    internal static IReadOnlyList<string> FromSidecars(string? ttml, string? lrc, bool hasUnsyncedTxt)
    {
        var badges = new List<string>();
        if (AppleLyricsService.IsWordSyncedTtml(ttml))
        {
            badges.Add("ttml");
        }

        var timing = LrcContent.ClassifyTiming(lrc);
        if (timing == LrcTimingKind.Word)
        {
            badges.Add("enhanced");
        }
        else if (timing == LrcTimingKind.Line)
        {
            badges.Add("synced");
        }

        if (badges.Count == 0 && hasUnsyncedTxt)
        {
            badges.Add("unsynced");
        }

        return badges;
    }

    private static bool TryRead(string path, out string content)
    {
        content = string.Empty;
        try
        {
            content = File.ReadAllText(path);
            return true;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return false;
        }
    }
}
