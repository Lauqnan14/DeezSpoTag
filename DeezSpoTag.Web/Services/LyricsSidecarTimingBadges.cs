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
            badges.Add("time-synced");
        }

        var lrcPath = Path.ChangeExtension(filePath, ".lrc");
        var elrcPath = Path.ChangeExtension(filePath, ".elrc");
        if (File.Exists(lrcPath) && TryRead(lrcPath, out var lrc))
        {
            badges.Add(LrcContent.IsWordSynchronized(lrc) ? "enhanced-synchronized" : "synced");
        }
        else if (File.Exists(elrcPath))
        {
            badges.Add("enhanced-synchronized");
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
