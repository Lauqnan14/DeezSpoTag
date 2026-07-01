namespace DeezSpoTag.Services.Download;

public static class QualityCatalog
{
    public sealed record QualityOption(string Value, string Label);
    public sealed record EngineQualityValue(string Engine, string Quality);
    public sealed record QualityTier(
        string Value,
        string Label,
        int LocalRank,
        int CanonicalRank,
        IReadOnlyList<EngineQualityValue> EngineValues);

    public const string MaxHiRes192 = "max_hires_192";
    public const string HiRes96 = "hires_96";
    public const string Alac = "alac";
    public const string CdLossless = "cd_lossless";
    public const string Flac = "flac";
    public const string AacLc = "aac_lc";
    public const string Mp3_320 = "mp3_320";
    public const string Mp3_128 = "mp3_128";
    public const string Mp3_96 = "mp3_96";

    private const string Apple = "apple";
    private const string Amazon = "amazon";
    private const string Deezer = "deezer";
    private const string Qobuz = "qobuz";
    private const string Tidal = "tidal";

    private static readonly QualityTier[] LibraryFolderQualityTiers =
    [
        new(
            MaxHiRes192,
            "Max Hi-Res (24-bit/192kHz)",
            LocalRank: 4,
            CanonicalRank: 120,
            [new(Qobuz, "27"), new(Tidal, "HI_RES_LOSSLESS")]),
        new(
            HiRes96,
            "Hi-Res (24-bit/96kHz)",
            LocalRank: 4,
            CanonicalRank: 100,
            [new(Qobuz, "7"), new(Tidal, "HI_RES")]),
        new(
            Alac,
            "ALAC",
            LocalRank: 3,
            CanonicalRank: 110,
            [new(Apple, "ALAC")]),
        new(
            CdLossless,
            "CD Lossless (16-bit/44.1kHz)",
            LocalRank: 3,
            CanonicalRank: 90,
            [new(Qobuz, "6"), new(Tidal, "LOSSLESS")]),
        new(
            Flac,
            "FLAC",
            LocalRank: 3,
            CanonicalRank: 70,
            [new(Amazon, "FLAC"), new(Deezer, "9")]),
        new(
            AacLc,
            "AAC-LC",
            LocalRank: 2,
            CanonicalRank: 50,
            [new(Apple, "AAC")]),
        new(
            Mp3_320,
            "MP3 320 kbps",
            LocalRank: 2,
            CanonicalRank: 45,
            [new(Qobuz, "5"), new(Tidal, "HIGH"), new(Deezer, "3")]),
        new(
            Mp3_128,
            "MP3 128 kbps",
            LocalRank: 1,
            CanonicalRank: 30,
            [new(Deezer, "1")]),
        new(
            Mp3_96,
            "MP3 96 kbps",
            LocalRank: 1,
            CanonicalRank: 25,
            [new(Tidal, "LOW")])
    ];

    public static IReadOnlyDictionary<string, IReadOnlyList<QualityOption>> GetEngineQualityOptions()
    {
        return new Dictionary<string, IReadOnlyList<QualityOption>>(StringComparer.OrdinalIgnoreCase)
        {
            ["apple"] = new[]
            {
                new QualityOption("ALAC", "Apple Music ALAC (lossless)"),
                new QualityOption("AAC", "Apple Music AAC")
            },
            ["deezer"] = new[]
            {
                new QualityOption("9", "Deezer FLAC"),
                new QualityOption("3", "Deezer MP3 320kbps"),
                new QualityOption("1", "Deezer MP3 128kbps")
            },
            ["amazon"] = new[]
            {
                new QualityOption("ULTRA_HD_FLAC", "Ultra HD FLAC"),
                new QualityOption("HD_FLAC", "HD FLAC"),
                new QualityOption("OPUS", "Opus"),
                new QualityOption("DOLBY_ATMOS", "Dolby Atmos")
            },
            ["qobuz"] = new[]
            {
                new QualityOption("27", "Max Hi-Res (24-bit/192kHz)"),
                new QualityOption("7", "Hi-Res (24-bit/96kHz)"),
                new QualityOption("6", "CD Lossless (16-bit/44.1kHz)"),
                new QualityOption("5", "MP3 (320kbps)")
            },
            ["tidal"] = new[]
            {
                new QualityOption("HI_RES_LOSSLESS", "Max Hi-Res (24-bit/192kHz)"),
                new QualityOption("HI_RES", "Hi-Res (24-bit/96kHz)"),
                new QualityOption("LOSSLESS", "CD Lossless (16-bit/44.1kHz)"),
                new QualityOption("HIGH", "MP3 (320kbps)"),
                new QualityOption("LOW", "Low (96kbps)")
            }
        };
    }

    /// <summary>
    /// Centralized quality options used by Settings "Library Folders" desired quality selector.
    /// Values are engine-specific identifiers currently used throughout the download pipeline.
    /// </summary>
    public static IReadOnlyList<QualityOption> GetLibraryFolderQualityOptions()
    {
        return LibraryFolderQualityTiers
            .Select(tier => new QualityOption(tier.Value, tier.Label))
            .ToArray();
    }

    public static IReadOnlyList<QualityTier> GetLibraryFolderQualityTiers()
        => LibraryFolderQualityTiers;

    public static string NormalizeLibraryFolderQualityValue(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return MaxHiRes192;
        }

        if (IsSpecialFolderQuality(normalized))
        {
            return normalized.ToLowerInvariant();
        }

        var tier = FindLibraryFolderQualityTier(normalized);
        return tier?.Value ?? normalized;
    }

    public static int? GetLibraryFolderLocalRank(string? value)
        => FindLibraryFolderQualityTier(value)?.LocalRank;

    public static int? GetLibraryFolderCanonicalRank(string? value)
        => FindLibraryFolderQualityTier(value)?.CanonicalRank;

    public static string? ResolveEngineQualityForLibraryFolderTier(string? value, string? engine)
    {
        if (string.IsNullOrWhiteSpace(engine))
        {
            return null;
        }

        var tier = FindLibraryFolderQualityTier(value);
        return tier?.EngineValues
            .FirstOrDefault(link => string.Equals(link.Engine, engine, StringComparison.OrdinalIgnoreCase))
            ?.Quality;
    }

    public static QualityTier? FindLibraryFolderQualityTier(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return LibraryFolderQualityTiers[0];
        }

        return LibraryFolderQualityTiers.FirstOrDefault(tier =>
            string.Equals(tier.Value, normalized, StringComparison.OrdinalIgnoreCase)
            || string.Equals(tier.Label, normalized, StringComparison.OrdinalIgnoreCase)
            || tier.EngineValues.Any(link => string.Equals(link.Quality, normalized, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsSpecialFolderQuality(string value)
        => string.Equals(value, "atmos", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "video", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "podcast", StringComparison.OrdinalIgnoreCase);
}
