namespace DeezSpoTag.Services.Download.Qobuz;

public static class QobuzQualityCodeNormalizer
{
    public static string Normalize(string? quality, string defaultCode)
    {
        if (string.IsNullOrWhiteSpace(quality))
        {
            return defaultCode;
        }

        var normalized = quality.Trim().ToUpperInvariant();
        if (normalized is "27" or "7" or "6" or "5")
        {
            return normalized;
        }

        if (normalized.Contains("MP3", StringComparison.Ordinal)
            || normalized.Contains("320", StringComparison.Ordinal))
        {
            return "5";
        }

        if (normalized.Contains("MAX", StringComparison.Ordinal)
            || normalized.Contains("192", StringComparison.Ordinal))
        {
            return "27";
        }

        if (normalized.Contains("HI_RES", StringComparison.Ordinal)
            || normalized.Contains("HI-RES", StringComparison.Ordinal)
            || normalized.Contains("HIRES", StringComparison.Ordinal)
            || normalized.Contains("24", StringComparison.Ordinal))
        {
            return "7";
        }

        if (normalized.Contains("LOSSLESS", StringComparison.Ordinal)
            || normalized.Contains("CD", StringComparison.Ordinal)
            || normalized.Contains("16", StringComparison.Ordinal))
        {
            return "6";
        }

        if (normalized.Contains("FLAC", StringComparison.Ordinal))
        {
            return "7";
        }

        return defaultCode;
    }
}
