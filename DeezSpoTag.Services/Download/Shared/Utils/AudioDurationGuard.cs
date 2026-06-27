using IOFile = System.IO.File;
using TagLib;

namespace DeezSpoTag.Services.Download.Shared.Utils;

public static class AudioDurationGuard
{
    public static AudioDurationGuardResult ValidateAgainstPreview(string filePath, int expectedDurationSeconds)
    {
        if (expectedDurationSeconds <= 0)
        {
            return AudioDurationGuardResult.Ok();
        }

        if (string.IsNullOrWhiteSpace(filePath) || !IOFile.Exists(filePath))
        {
            return AudioDurationGuardResult.Fail("Audio validation failed: output file is missing.");
        }

        if (!TryReadDurationSeconds(filePath, out var actualDurationSeconds))
        {
            return AudioDurationGuardResult.Fail("Audio validation failed: unable to read output duration.");
        }

        if (IsExpectedDurationAcceptable(actualDurationSeconds, expectedDurationSeconds))
        {
            return AudioDurationGuardResult.Ok();
        }

        return AudioDurationGuardResult.Fail(
            $"Audio validation failed: output duration is {actualDurationSeconds:F1}s but expected about {expectedDurationSeconds}s. Refusing likely preview download.");
    }

    private static bool TryReadDurationSeconds(string filePath, out double durationSeconds)
    {
        durationSeconds = 0;
        try
        {
            using var file = TagLib.File.Create(filePath);
            durationSeconds = file.Properties.Duration.TotalSeconds;
            return durationSeconds > 0;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return false;
        }
    }

    public static bool IsExpectedDurationAcceptable(double actualSeconds, int expectedSeconds)
    {
        if (expectedSeconds <= 0)
        {
            return true;
        }

        if (actualSeconds <= 0)
        {
            return false;
        }

        var allowedDelta = Math.Max(5d, expectedSeconds * 0.12d);
        return Math.Abs(actualSeconds - expectedSeconds) <= allowedDelta;
    }
}

public sealed record AudioDurationGuardResult(bool Success, string Message)
{
    public static AudioDurationGuardResult Ok() => new(true, string.Empty);

    public static AudioDurationGuardResult Fail(string message) => new(false, message);
}
