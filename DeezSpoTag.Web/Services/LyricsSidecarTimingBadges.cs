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

        var badges = new List<string>();
        var ttmlPath = Path.ChangeExtension(filePath, ".ttml");
        if (File.Exists(ttmlPath) && TryRead(ttmlPath, out var ttml) && AppleLyricsService.IsWordSyncedTtml(ttml))
        {
            badges.Add("ttml");
        }

        var lrcPath = Path.ChangeExtension(filePath, ".lrc");
        if (File.Exists(lrcPath) && TryRead(lrcPath, out var lrc))
        {
            var timing = LrcContent.ClassifyTiming(lrc);
            if (timing == LrcTimingKind.Word)
            {
                badges.Add("enhanced");
            }
            else if (timing == LrcTimingKind.Line)
            {
                badges.Add("synced");
            }
        }

        if (badges.Count == 0 && File.Exists(Path.ChangeExtension(filePath, ".txt")))
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
