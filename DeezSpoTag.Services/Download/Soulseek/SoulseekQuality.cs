using DeezSpoTag.Services.Download;

namespace DeezSpoTag.Services.Download.Soulseek;

/// <summary>
///     The outcome of normalizing one Soulseek file's quality.
/// </summary>
/// <param name="Code">The Soulseek engine quality code, for example <c>FLAC</c> or <c>MP3_320</c>.</param>
/// <param name="Label">A user-facing label carrying the true bitrate, depth or sample rate.</param>
/// <param name="TierValue">
///     The DeezSpoTag quality tier this maps onto, or <see langword="null"/> when the quality is unknown.
/// </param>
/// <param name="CanonicalRank">
///     The canonical rank of the mapped tier, or <see langword="null"/> when the quality is unknown.
/// </param>
/// <param name="IsLossless">Whether the file is a lossless format.</param>
/// <param name="IsUnknown">Whether the quality could not be determined.</param>
public sealed record SoulseekQualityInfo(
    string Code,
    string Label,
    string? TierValue,
    int? CanonicalRank,
    bool IsLossless,
    bool IsUnknown)
{
    /// <summary>Gets the Soulseek code used when nothing could be determined.</summary>
    public const string UnknownCode = "UNKNOWN";
}

/// <summary>
///     Normalizes the quality of a Soulseek file into DeezSpoTag's existing quality tiers.
/// </summary>
/// <remarks>
///     <para>
///         Soulseek advertises raw per-file facts — extension, bitrate, bit depth, sample rate — rather than
///         tier-specific codes. This type turns those facts into a Soulseek code and then into the tier
///         values that <see cref="QualityCatalog"/> already defines, so dedupe, the quality guard and the
///         folder quality selector all keep working through the single existing quality system. No second
///         quality model is introduced.
///     </para>
///     <para>
///         An undeterminable quality maps to <see cref="SoulseekQualityInfo.UnknownCode"/> with a
///         <see langword="null"/> tier and rank. That is deliberate: unknown quality must never rank as a
///         high match, so it carries no rank at all and the scoring service rejects it unless the user has
///         explicitly allowed unknown-quality candidates.
///     </para>
/// </remarks>
public static class SoulseekQuality
{
    /// <summary>Lossless extension -> whether it is FLAC or another lossless container.</summary>
    private static readonly HashSet<string> LosslessExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".alac", ".wav", ".wave", ".aif", ".aiff", ".aifc", ".ape", ".wv", ".m4a", ".tak", ".tta", ".alac"
    };

    private static readonly HashSet<string> FlacExtensions = new(StringComparer.OrdinalIgnoreCase) { ".flac" };
    private static readonly HashSet<string> Mp3Extensions = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".mp2" };

    private const int Mp3HighBitrateThresholdKbps = 256;
    private const int Mp3MediumBitrateThresholdKbps = 192;
    private const int Mp3LowBitrateThresholdKbps = 160;
    private const int Mp3AcceptableBitrateThresholdKbps = 128;
    private const int HiResBitDepth = 24;

    /// <summary>
    ///     The Soulseek code for lossless, and the label shown when nothing could be determined.
    /// </summary>
    /// <remarks>
    ///     Both were written out as raw literals at every use. The code in particular is compared
    ///     against codes read from the Soulseek protocol, so a copy that drifted - even in case -
    ///     would classify a lossless file as something else and downgrade its quality tier.
    /// </remarks>
    private const string LosslessCode = "LOSSLESS";

    private const string UnknownQualityLabel = "Unknown quality";

    /// <summary>
    ///     The Soulseek quality codes the engine understands, in descending order of preference.
    /// </summary>
    /// <remarks>
    ///     FLAC is split by resolution into the same three bands Qobuz and Tidal use, so a preference can ask
    ///     for a specific depth and sample rate instead of "some FLAC". ALAC has no code of its own: it is
    ///     lossless, so it answers to <c>LOSSLESS</c> like WAV, APE and WavPack do.
    /// </remarks>
    public static IReadOnlyList<string> KnownCodes { get; } =
    [
        SoulseekQualityInfo.UnknownCode,
        "FLAC_HI_RES_LOSSLESS",
        "FLAC_HI_RES",
        "FLAC",
        LosslessCode,
        "MP3_320",
        "MP3_256",
        "MP3_192",
        "MP3_128"
    ];

    /// <summary>
    ///     Normalizes a Soulseek quality code to one of <see cref="KnownCodes"/>.
    /// </summary>
    public static string NormalizeCode(string? code)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        return KnownCodes.FirstOrDefault(known => string.Equals(known, normalized, StringComparison.Ordinal))
            ?? SoulseekQualityInfo.UnknownCode;
    }

    /// <summary>
    ///     Normalizes a candidate's quality from the facts slskd reported.
    /// </summary>
    /// <param name="filename">The remote filename, used to derive the extension when one was not reported.</param>
    /// <param name="extension">The extension slskd reported, if any.</param>
    /// <param name="bitrateKbps">Bitrate in kbps, when known.</param>
    /// <param name="bitDepth">Bit depth, when known.</param>
    /// <param name="sampleRateHz">Sample rate in Hz, when known.</param>
    public static SoulseekQualityInfo Normalize(
        string? filename,
        string? extension,
        int? bitrateKbps,
        int? bitDepth = null,
        int? sampleRateHz = null)
    {
        var normalizedExtension = NormalizeExtension(extension) ?? DeriveExtension(filename);
        var isLossless = normalizedExtension is not null && LosslessExtensions.Contains(normalizedExtension);

        if (normalizedExtension is null)
        {
            return Unknown();
        }

        if (isLossless)
        {
            return Lossless(normalizedExtension, bitDepth, sampleRateHz);
        }

        if (Mp3Extensions.Contains(normalizedExtension))
        {
            return Mp3(bitrateKbps);
        }

        // A recognised audio extension we have no tier for (AAC, Opus, WMA, ...). It is lossless-agnostic
        // and must not be treated as a high quality match.
        return Unknown();
    }

    /// <summary>
    ///     Maps a Soulseek quality code plus per-file facts onto an existing quality tier.
    /// </summary>
    /// <remarks>
    ///     <c>MP3_256</c> and <c>MP3_192</c> have no dedicated cross-engine tier. They rank within the
    ///     existing 320 kbps tier rather than adding new tiers to the shared folder-quality selector, and
    ///     the caller keeps the precise label for display.
    /// </remarks>
    public static SoulseekQualityInfo NormalizeCodeWithFacts(string? code, int? bitDepth = null, int? sampleRateHz = null)
    {
        var normalized = NormalizeCode(code);

        return normalized switch
        {
            "FLAC_HI_RES_LOSSLESS" => Tiered("FLAC_HI_RES_LOSSLESS", "Max Hi-Res (24-bit/192kHz)", QualityCatalog.MaxHiRes192),
            "FLAC_HI_RES" => Tiered("FLAC_HI_RES", "Hi-Res (24-bit/96kHz)", QualityCatalog.HiRes96),
            "FLAC" => Tiered("FLAC", "FLAC", QualityCatalog.Flac),
            LosslessCode => Lossless(".flac", bitDepth, sampleRateHz),
            "MP3_320" => Mp3Tiered("MP3_320", "MP3 320 kbps", QualityCatalog.Mp3_320),
            "MP3_256" => Mp3Tiered("MP3_256", "MP3 256 kbps", QualityCatalog.Mp3_320),
            "MP3_192" => Mp3Tiered("MP3_192", "MP3 192 kbps", QualityCatalog.Mp3_320),
            "MP3_128" => Mp3Tiered("MP3_128", "MP3 128 kbps", QualityCatalog.Mp3_128),
            _ => Unknown()
        };
    }

    /// <summary>
    ///     Resolves the Soulseek code to request for a given quality tier.
    /// </summary>
    /// <remarks>
    ///     Used when a destination folder pins a desired quality tier. Lossless tiers resolve to
    ///     <c>LOSSLESS</c>, which the scorer then narrows using the individual file's depth and sample rate.
    /// </remarks>
    public static string? ResolveCodeForTier(string? tierValue)
    {
        // A blank tier means "no preference". FindLibraryFolderQualityTier deliberately answers a blank
        // input with the highest tier, which is the right default for the folder selector but the wrong
        // one here: it would silently pin every download to max hi-res.
        if (string.IsNullOrWhiteSpace(tierValue))
        {
            return null;
        }

        var tier = QualityCatalog.FindLibraryFolderQualityTier(tierValue);
        if (tier is null)
        {
            return null;
        }

        return tier.Value switch
        {
            QualityCatalog.MaxHiRes192 => "FLAC_HI_RES_LOSSLESS",
            QualityCatalog.HiRes96 => "FLAC_HI_RES",
            QualityCatalog.CdLossless or QualityCatalog.Flac => "FLAC",

            // ALAC is lossless here, so it asks for the same generic code WAV and APE ask for.
            QualityCatalog.Alac => LosslessCode,
            QualityCatalog.Mp3_320 or QualityCatalog.Mp3_96 => "MP3_320",
            QualityCatalog.Mp3_128 => "MP3_128",
            _ => null
        };
    }

    /// <summary>
    ///     Returns a value indicating whether the code is a lossless request.
    /// </summary>
    public static bool IsLosslessCode(string? code)
        => NormalizeCode(code) is "FLAC_HI_RES_LOSSLESS" or "FLAC_HI_RES" or "FLAC" or LosslessCode;

    /// <summary>
    ///     Returns a value indicating whether the code names an explicitly unknown quality.
    /// </summary>
    public static bool IsUnknownCode(string? code)
        => NormalizeCode(code) == SoulseekQualityInfo.UnknownCode;

    private static SoulseekQualityInfo Lossless(string extension, int? bitDepth, int? sampleRateHz)
    {
        var isFlac = FlacExtensions.Contains(extension);

        // Hi-res evidence beats the container. A 24-bit/192kHz FLAC is a Max Hi-Res file, not a plain FLAC.
        var hiResTier = ResolveHiResTier(bitDepth, sampleRateHz);

        // A FLAC names its own resolution band, the same way Qobuz and Tidal name theirs. Every other lossless
        // container, ALAC included, reports the single generic code.
        var code = !isFlac
            ? LosslessCode
            : hiResTier switch
            {
                QualityCatalog.MaxHiRes192 => "FLAC_HI_RES_LOSSLESS",
                QualityCatalog.HiRes96 => "FLAC_HI_RES",
                _ => "FLAC"
            };

        string tier;
        if (hiResTier is not null)
        {
            tier = hiResTier;
        }
        else if (isFlac)
        {
            // Consistent with how the sibling engines label FLAC: Deezer's "9" and Amazon's "FLAC" both map
            // onto the flac tier.
            tier = QualityCatalog.Flac;
        }
        else
        {
            // ALAC, WAV, APE, WavPack and friends carry no container-specific tier, so they fall back to CD
            // lossless.
            tier = QualityCatalog.CdLossless;
        }

        return Tiered(code, BuildLosslessLabel(code, bitDepth, sampleRateHz), tier);
    }

    /// <summary>
    ///     Returns the hi-res tier implied by the reported depth and sample rate, or <see langword="null"/>
    ///     when the file is not hi-res or the facts are missing.
    /// </summary>
    private static string? ResolveHiResTier(int? bitDepth, int? sampleRateHz)
    {
        if (bitDepth is not { } depth || depth < HiResBitDepth)
        {
            return null;
        }

        if (sampleRateHz is not { } rate)
        {
            // 24-bit with an unreported rate: assume hi-res rather than CD, but never max hi-res.
            return QualityCatalog.HiRes96;
        }

        if (rate >= 192000)
        {
            return QualityCatalog.MaxHiRes192;
        }

        return rate > 44100 ? QualityCatalog.HiRes96 : null;
    }

    private static string BuildLosslessLabel(string code, int? bitDepth, int? sampleRateHz)
    {
        var depth = bitDepth.HasValue ? $"{bitDepth.Value}-bit" : null;
        var rate = sampleRateHz.HasValue ? $"{sampleRateHz.Value / 1000.0:0.###}kHz".Replace('.', ',') : null;
        var facts = new[] { depth, rate }.Where(part => !string.IsNullOrEmpty(part)).ToArray();

        if (facts.Length == 0)
        {
            return code switch
            {
                "FLAC" => "FLAC",
                "FLAC_HI_RES" => "Hi-Res FLAC",
                "FLAC_HI_RES_LOSSLESS" => "Max Hi-Res FLAC",
                _ => "Lossless"
            };
        }

        return $"{code} ({string.Join("/", facts)})";
    }

    private static SoulseekQualityInfo Mp3(int? bitrateKbps)
    {
        if (!bitrateKbps.HasValue || bitrateKbps.Value <= 0)
        {
            return Unknown();
        }

        var kbps = bitrateKbps.Value;

        // The design names only 320, 256, 192 and 128. Anything below 128 kbps is a real file but a weak
        // one, so it is reported as unknown quality and carries no rank rather than being rounded up to
        // the 128 tier, which would let it pass a 128 kbps request.
        if (kbps < Mp3AcceptableBitrateThresholdKbps)
        {
            return Unknown();
        }

        var (code, tier) = kbps >= Mp3HighBitrateThresholdKbps
            ? ("MP3_320", QualityCatalog.Mp3_320)
            : kbps >= Mp3MediumBitrateThresholdKbps
                ? ("MP3_256", QualityCatalog.Mp3_320)
                : kbps >= Mp3LowBitrateThresholdKbps
                    ? ("MP3_192", QualityCatalog.Mp3_320)
                    : ("MP3_128", QualityCatalog.Mp3_128);

        return Mp3Tiered(code, $"MP3 {kbps} kbps", tier);
    }

    private static SoulseekQualityInfo Mp3Tiered(string code, string label, string tier)
        => Tiered(code, label, tier, isLossless: false);

    private static SoulseekQualityInfo Tiered(string code, string label, string tier, bool isLossless = true)
        => new(
            code,
            label,
            tier,
            QualityCatalog.GetLibraryFolderCanonicalRank(tier),
            isLossless,
            IsUnknown: false);

    private static SoulseekQualityInfo Unknown()
        => new(SoulseekQualityInfo.UnknownCode, UnknownQualityLabel, null, null, IsLossless: false, IsUnknown: true);

    private static string? NormalizeExtension(string? extension)
    {
        var normalized = (extension ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length == 0)
        {
            return null;
        }

        return normalized.StartsWith('.') ? normalized : $".{normalized}";
    }

    private static string? DeriveExtension(string? filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
        {
            return null;
        }

        // Soulseek filenames are remote paths and may carry their own extension, for example
        // "@@user\\share\\Artist\\01 Track.flac". Take the last segment's extension.
        var leaf = filename.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(leaf))
        {
            return null;
        }

        var dot = leaf.LastIndexOf('.');
        if (dot < 0 || dot == leaf.Length - 1)
        {
            return null;
        }

        var extension = leaf[dot..].ToLowerInvariant();
        return extension.Length is > 1 and <= 6 ? extension : null;
    }
}
