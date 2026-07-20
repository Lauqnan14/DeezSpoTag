namespace DeezSpoTag.Services.Download.Shared;

internal enum TidalStereoQualityTier
{
    Unknown = 0,
    Low = 1,
    High = 2,
    CdLossless = 3,
    HiRes = 4,
    MaxHiRes = 5,
    DolbyAtmos = 6
}

internal static class TidalStereoQuality
{
    public const string Low = "LOW";
    public const string High = "HIGH";
    public const string CdLossless = "LOSSLESS";
    public const string HiRes = "HI_RES";
    public const string MaxHiRes = "HI_RES_LOSSLESS";
    public const string DolbyAtmos = "DOLBY_ATMOS";

    public static TidalStereoQualityTier Normalize(string? quality)
    {
        var normalized = (quality ?? string.Empty).Trim().ToUpperInvariant();
        return normalized switch
        {
            "" => TidalStereoQualityTier.CdLossless,
            "LOW" or "LOW_96K" or "TIDAL_LOW" => TidalStereoQualityTier.Low,
            "HIGH" or "HIGH_320K" or "TIDAL_HIGH" => TidalStereoQualityTier.High,
            "LOSSLESS" or "HIGH_LOSSLESS" or "CD_LOSSLESS" or "TIDAL_CD_LOSSLESS" => TidalStereoQualityTier.CdLossless,
            "HI_RES" or "HI_RES_96" or "HI_RES_LOSSLESS_96" or "TIDAL_HI_RES" => TidalStereoQualityTier.HiRes,
            "HI_RES_LOSSLESS" or "MAX" or "MAX_HI_RES" or "TIDAL_MAX_HI_RES" => TidalStereoQualityTier.MaxHiRes,
            "ATMOS" or "DOLBY_ATMOS" => TidalStereoQualityTier.DolbyAtmos,
            _ => TidalStereoQualityTier.Unknown
        };
    }

    public static string ToFallbackQuality(TidalStereoQualityTier tier)
        => tier switch
        {
            TidalStereoQualityTier.Low => Low,
            TidalStereoQualityTier.High => High,
            TidalStereoQualityTier.CdLossless => CdLossless,
            TidalStereoQualityTier.HiRes => HiRes,
            TidalStereoQualityTier.MaxHiRes => MaxHiRes,
            TidalStereoQualityTier.DolbyAtmos => DolbyAtmos,
            _ => string.Empty
        };

    public static string ToTidalRequestQuality(string? quality)
        => ToTidalRequestQuality(Normalize(quality));

    public static string ToTidalRequestQuality(TidalStereoQualityTier tier)
        => tier switch
        {
            TidalStereoQualityTier.Low => Low,
            TidalStereoQualityTier.High => High,
            TidalStereoQualityTier.CdLossless => CdLossless,
            TidalStereoQualityTier.HiRes => HiRes,
            TidalStereoQualityTier.MaxHiRes => MaxHiRes,
            TidalStereoQualityTier.DolbyAtmos => DolbyAtmos,
            _ => CdLossless
        };

    public static bool Accepts(TidalStereoQualityTier requested, ActualAudioQuality actual)
        => requested switch
        {
            TidalStereoQualityTier.Low => !actual.IsLossless
                && actual.BitrateKbps > 0
                && actual.BitrateKbps <= 128,
            TidalStereoQualityTier.High => !actual.IsLossless
                && actual.BitrateKbps >= 256,
            TidalStereoQualityTier.CdLossless => actual.IsLossless
                && actual.BitsPerSample > 0
                && actual.BitsPerSample <= 16
                && actual.SampleRate > 0
                && actual.SampleRate <= 48000,
            TidalStereoQualityTier.HiRes => actual.IsLossless
                && actual.BitsPerSample >= 24
                && actual.SampleRate > 0
                && actual.SampleRate <= 96000,
            TidalStereoQualityTier.MaxHiRes => actual.IsLossless
                && actual.BitsPerSample >= 24
                && actual.SampleRate > 96000,
            TidalStereoQualityTier.DolbyAtmos => true,
            _ => true
        };

    public static string FormatRequested(string? quality)
        => Normalize(quality) switch
        {
            TidalStereoQualityTier.Low => "Tidal Low (96kbps)",
            TidalStereoQualityTier.High => "Tidal High (320kbps)",
            TidalStereoQualityTier.CdLossless => "Tidal CD Lossless (16-bit/44.1kHz)",
            TidalStereoQualityTier.HiRes => "Tidal Hi-Res (24-bit/96kHz)",
            TidalStereoQualityTier.MaxHiRes => "Tidal Max Hi-Res (24-bit/192kHz)",
            TidalStereoQualityTier.DolbyAtmos => "Tidal Dolby Atmos",
            _ => string.IsNullOrWhiteSpace(quality) ? "Tidal requested quality" : quality
        };
}
