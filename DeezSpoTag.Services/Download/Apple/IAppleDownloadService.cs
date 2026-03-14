namespace DeezSpoTag.Services.Download.Apple;

public interface IAppleDownloadService
{
    Task<AppleDownloadResult> DownloadAsync(AppleDownloadRequest request, CancellationToken cancellationToken);
}

public sealed record AppleDownloadResult(
    bool Success,
    string Message,
    string OutputPath,
    string StreamGroup = "",
    bool IsVideo = false,
    string VideoResolutionTier = "",
    bool VideoHdr = false,
    string VideoAudioProfile = "")
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Human-readable quality label (e.g., "24-bit/192kHz", "Dolby Atmos", "AAC LC 256Kbps").
    /// </summary>
    public string QualityLabel => IsVideo ? "Video" : FormatStreamGroup(StreamGroup);

    public static AppleDownloadResult Ok(string outputPath, string streamGroup = "") => new(true, string.Empty, outputPath, streamGroup);
    public static AppleDownloadResult OkVideo(
        string outputPath,
        string streamGroup,
        string resolutionTier,
        bool hdr,
        string audioProfile) =>
        new(true, string.Empty, outputPath, streamGroup, true, resolutionTier, hdr, audioProfile);

    public static AppleDownloadResult Fail(string message) => new(false, message, string.Empty);

    /// <summary>
    /// Formats the HLS audio group identifier into a human-readable quality label.
    /// Ported from apmyx-gui Go backend's emitStreamInfo function.
    /// </summary>
    private static string FormatStreamGroup(string streamGroup)
    {
        if (string.IsNullOrWhiteSpace(streamGroup))
        {
            return string.Empty;
        }

        var group = streamGroup.ToLowerInvariant();
        return TryFormatAlac(streamGroup, group)
            ?? TryFormatAtmos(group)
            ?? TryFormatDolbyDigital(group)
            ?? TryFormatBinaural(streamGroup, group)
            ?? TryFormatDownmix(streamGroup, group)
            ?? TryFormatAac(streamGroup, group)
            ?? streamGroup;
    }

    private static string? TryFormatAlac(string streamGroup, string normalizedGroup)
    {
        if (!normalizedGroup.Contains("alac", StringComparison.Ordinal))
        {
            return null;
        }

        var parts = streamGroup.Split('-');
        if (parts.Length >= 4 && int.TryParse(parts[^2], out var sampleRate) && sampleRate > 0)
        {
            var bitDepth = parts[^1];
            var sampleRateKhz = sampleRate / 1000;
            return $"{bitDepth}-bit/{sampleRateKhz}kHz";
        }

        return "ALAC Lossless";
    }

    private static string? TryFormatAtmos(string normalizedGroup)
        => normalizedGroup.Contains("atmos", StringComparison.Ordinal) ? "Dolby Atmos" : null;

    private static string? TryFormatDolbyDigital(string normalizedGroup)
        => normalizedGroup.Contains("ec-3", StringComparison.Ordinal)
           || normalizedGroup.Contains("ec3", StringComparison.Ordinal)
           || normalizedGroup.Contains("ac-3", StringComparison.Ordinal)
           || normalizedGroup.Contains("ac3", StringComparison.Ordinal)
            ? "Dolby Digital"
            : null;

    private static string? TryFormatBinaural(string streamGroup, string normalizedGroup)
        => TryFormatBitrateFamily(streamGroup, normalizedGroup, "binaural", "AAC Binaural");

    private static string? TryFormatDownmix(string streamGroup, string normalizedGroup)
        => TryFormatBitrateFamily(streamGroup, normalizedGroup, "downmix", "AAC Downmix");

    private static string? TryFormatAac(string streamGroup, string normalizedGroup)
    {
        if (!normalizedGroup.Contains("stereo", StringComparison.Ordinal)
            && !normalizedGroup.Contains("aac", StringComparison.Ordinal))
        {
            return null;
        }

        var bitrate = ExtractTrailingNumber(streamGroup);
        return bitrate > 0 ? $"AAC LC {bitrate}Kbps" : "AAC LC 256Kbps";
    }

    private static string? TryFormatBitrateFamily(
        string streamGroup,
        string normalizedGroup,
        string marker,
        string label)
    {
        if (!normalizedGroup.Contains(marker, StringComparison.Ordinal))
        {
            return null;
        }

        var bitrate = ExtractTrailingNumber(streamGroup);
        return bitrate > 0 ? $"{label} {bitrate}Kbps" : label;
    }

    private static int ExtractTrailingNumber(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        // Find the last number in the string
        var match = System.Text.RegularExpressions.Regex.Match(
            value,
            @"(\d+)(?!.*\d)",
            System.Text.RegularExpressions.RegexOptions.None,
            RegexTimeout);
        return match.Success && int.TryParse(match.Groups[1].Value, out var number) ? number : 0;
    }
}
