namespace DeezSpoTag.Core.Utils;

public static class MediaQualityInference
{
    private readonly record struct QualityRanks(
        int AtmosRank,
        int VideoRank,
        int HiResRank,
        int LosslessRank,
        int AacRank,
        int CompressedRank,
        int LowRank);

    public static int? InferCanonicalQualityRankFromText(string? quality, string atmosToken)
    {
        var normalized = NormalizeText(quality);
        return InferQualityRank(
            normalized,
            atmosToken,
            treatPodcastAsVideo: false,
            new QualityRanks(
                AtmosRank: 130,
                VideoRank: 125,
                HiResRank: 120,
                LosslessRank: 70,
                AacRank: 50,
                CompressedRank: 40,
                LowRank: 30));
    }

    public static int MapRequestedNumericQualityToLocalRank(int requestedQuality)
        => requestedQuality switch
        {
            125 => 0,
            >= 130 => 5,
            120 => 4,
            115 => 4,
            110 => 3,
            100 => 4,
            90 => 3,
            80 => 3,
            70 => 3,
            60 => 3,
            50 => 2,
            40 => 2,
            30 => 1,
            >= 5 => 5,
            4 => 4,
            < 0 => 0,
            _ => requestedQuality
        };

    public static int? MapCanonicalRankToLocalRank(int canonicalRank)
        => canonicalRank switch
        {
            125 => 0,
            >= 130 => 5,
            120 => 4,
            115 => 4,
            110 => 3,
            100 => 4,
            >= 60 => 3,
            >= 40 => 2,
            >= 30 => 1,
            <= 0 => 0,
            _ => null
        };

    public static int? InferLocalQualityRankFromText(string? quality, string atmosToken, bool treatPodcastAsVideo)
    {
        var normalized = NormalizeText(quality);
        return InferQualityRank(
            normalized,
            atmosToken,
            treatPodcastAsVideo,
            new QualityRanks(
                AtmosRank: 5,
                VideoRank: 0,
                HiResRank: 4,
                LosslessRank: 3,
                AacRank: 2,
                CompressedRank: 2,
                LowRank: 1));
    }

    private static int? InferQualityRank(
        string normalized,
        string atmosToken,
        bool treatPodcastAsVideo,
        QualityRanks ranks)
    {
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        var normalizedAtmosToken = NormalizeText(atmosToken);
        if (!string.IsNullOrWhiteSpace(normalizedAtmosToken)
            && normalized.Contains(normalizedAtmosToken, StringComparison.Ordinal))
        {
            return ranks.AtmosRank;
        }

        if (ContainsAny(normalized, "video")
            || (treatPodcastAsVideo && ContainsAny(normalized, "podcast")))
        {
            return ranks.VideoRank;
        }

        if (ContainsAny(normalized, "hi_res", "hi-res", "24bit", "24-bit", "24 bit"))
        {
            return ranks.HiResRank;
        }

        if (ContainsAny(normalized, "lossless", "flac", "alac", "cd", "16bit", "16-bit", "16 bit"))
        {
            return ranks.LosslessRank;
        }

        if (ContainsAny(normalized, "aac", "256"))
        {
            return ranks.AacRank;
        }

        if (ContainsAny(normalized, "320", "mp3", "vorbis", "opus"))
        {
            return ranks.CompressedRank;
        }

        return ContainsAny(normalized, "128") ? ranks.LowRank : null;
    }

    private static bool ContainsAny(string normalized, params string[] tokens)
    {
        return tokens.Any(token => normalized.Contains(token, StringComparison.Ordinal));
    }

    private static string NormalizeText(string? value)
        => value?.Trim().ToLowerInvariant() ?? string.Empty;
}
