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

        if (!LooksLikePreviewDuration(actualDurationSeconds, expectedDurationSeconds))
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
        catch
        {
            return false;
        }
    }

    private static bool LooksLikePreviewDuration(double actualSeconds, int expectedSeconds)
    {
        if (actualSeconds <= 0 || expectedSeconds < 60)
        {
            return false;
        }

        var missingSeconds = expectedSeconds - actualSeconds;
        if (missingSeconds < 25)
        {
            return false;
        }

        var previewLengthForLongTrack = expectedSeconds > 120 && actualSeconds <= 120;
        var clearlyShorterThanExpected = actualSeconds <= expectedSeconds * 0.85d;
        var heavilyTruncated = actualSeconds < expectedSeconds * 0.5d;
        return (previewLengthForLongTrack && clearlyShorterThanExpected) || heavilyTruncated;
    }
}

public sealed record AudioDurationGuardResult(bool Success, string Message)
{
    public static AudioDurationGuardResult Ok() => new(true, string.Empty);

    public static AudioDurationGuardResult Fail(string message) => new(false, message);
}
