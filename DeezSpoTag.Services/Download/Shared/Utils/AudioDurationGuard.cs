using IOFile = System.IO.File;
using TagLib;

namespace DeezSpoTag.Services.Download.Shared.Utils;

public static class AudioDurationGuard
{
    public static AudioDurationGuardResult ValidateAgainstPreview(string filePath, int expectedDurationSeconds)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !IOFile.Exists(filePath))
        {
            return AudioDurationGuardResult.Fail("Audio validation failed: output file is missing.");
        }

        if (new FileInfo(filePath).Length == 0)
        {
            return AudioDurationGuardResult.Fail("Audio validation failed: output file is empty.");
        }

        if (!TryReadDurationSeconds(filePath, out var actualDurationSeconds))
        {
            return AudioDurationGuardResult.Inconclusive("Audio duration could not be read; duration validation was skipped.");
        }

        if (expectedDurationSeconds <= 0)
        {
            return IsObviousUnknownDurationPreview(actualDurationSeconds)
                ? AudioDurationGuardResult.Fail(
                    $"Audio validation failed: output duration is {actualDurationSeconds:F1}s with no expected duration. Refusing likely preview download.")
                : AudioDurationGuardResult.Ok();
        }

        if (IsExpectedDurationAcceptable(actualDurationSeconds, expectedDurationSeconds))
        {
            return AudioDurationGuardResult.Ok();
        }

        return AudioDurationGuardResult.Fail(
            $"Audio validation failed: output duration is {actualDurationSeconds:F1}s but expected about {expectedDurationSeconds}s. Refusing likely preview download.");
    }

    private static bool IsObviousUnknownDurationPreview(double actualSeconds)
        => actualSeconds is >= 25d and <= 35d;

    private static bool TryReadDurationSeconds(string filePath, out double durationSeconds)
    {
        durationSeconds = 0;
        try
        {
            using var file = TagLib.File.Create(filePath);
            durationSeconds = file.Properties.Duration.TotalSeconds;
            return durationSeconds > 0;
        }
        catch (Exception ex) when (ex is CorruptFileException
                                   or UnsupportedFormatException
                                   || DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
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

        if (actualSeconds >= expectedSeconds)
        {
            return true;
        }

        var missingSeconds = expectedSeconds - actualSeconds;
        var ratio = actualSeconds / expectedSeconds;
        var canonicalPreviewLength = new[] { 30d, 60d, 90d, 120d }
            .Any(length => Math.Abs(actualSeconds - length) <= 2d);
        var severelyTruncated = ratio <= 0.5d && missingSeconds >= 25d;
        var previewLengthTruncated = canonicalPreviewLength && ratio <= 0.75d && missingSeconds >= 25d;
        return !severelyTruncated && !previewLengthTruncated;
    }
}

public sealed record AudioDurationGuardResult(bool Success, bool Conclusive, string Message)
{
    public static AudioDurationGuardResult Ok() => new(true, true, string.Empty);

    public static AudioDurationGuardResult Inconclusive(string message) => new(true, false, message);

    public static AudioDurationGuardResult Fail(string message) => new(false, true, message);
}
