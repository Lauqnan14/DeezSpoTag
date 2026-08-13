using DeezSpoTag.Services.Apple;

namespace DeezSpoTag.Services.Download.Utils;

public static class TtmlSidecarCleanup
{
    public static AppleTtmlTimingKind ClassifyExisting(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return AppleTtmlTimingKind.Invalid;
        }

        try
        {
            return AppleLyricsService.ClassifyTtml(File.ReadAllText(path));
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return AppleTtmlTimingKind.Invalid;
        }
    }

    public static bool IsNonWordTimed(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            return !AppleLyricsService.IsWordSyncedTtml(File.ReadAllText(path));
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return false;
        }
    }

    public static bool TryDeleteNonWordTimed(string? path)
    {
        if (!IsNonWordTimed(path))
        {
            return false;
        }

        try
        {
            File.Delete(path!);
            return !File.Exists(path);
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return false;
        }
    }
}
