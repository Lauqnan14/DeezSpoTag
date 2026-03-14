namespace DeezSpoTag.Web.Services.CoverPort;

public static class CoverRankingService
{
    public static IReadOnlyList<RankedCoverCandidate> Rank(
        IEnumerable<CoverCandidate> candidates,
        CoverSearchOptions options)
    {
        if (options.ScoringMode == CoverScoringMode.SacadCompatibility)
        {
            return RankWithSacadCompatibility(candidates, options);
        }

        return candidates
            .Select(candidate => new RankedCoverCandidate(candidate, Score(candidate, options)))
            .OrderByDescending(item => item.Score)
            .ToList();
    }

    public static IReadOnlyList<CoverCandidate> SortReferenceCandidates(IEnumerable<CoverCandidate> candidates)
    {
        var list = candidates.ToList();
        list.Sort((a, b) => CompareSacadPreferred(left: b, right: a, searchMode: false, targetSize: null));
        return list;
    }

    private static List<RankedCoverCandidate> RankWithSacadCompatibility(
        IEnumerable<CoverCandidate> candidates,
        CoverSearchOptions options)
    {
        var sorted = candidates.ToList();
        sorted.Sort((a, b) => CompareSacadPreferred(left: b, right: a, searchMode: true, options.TargetSize));

        // Preserve score shape for callers but ranking is comparator-driven in compatibility mode.
        var total = sorted.Count;
        return sorted
            .Select((candidate, index) => new RankedCoverCandidate(candidate, total - index))
            .ToList();
    }

    private static double Score(CoverCandidate candidate, CoverSearchOptions options)
    {
        var score = 0d;

        // Source confidence contributes a stable base score.
        score += Clamp01(candidate.SourceReliability) * 30d;

        // Metadata query match confidence.
        score += Clamp01(candidate.MatchConfidence) * 35d;

        // Similarity to reference cover (when available).
        if (candidate.SimilarityScore.HasValue)
        {
            score += Clamp01(candidate.SimilarityScore.Value) * 30d;
        }

        // Size quality and closeness to target size.
        var minEdge = Math.Min(candidate.Width, candidate.Height);
        if (minEdge > 0 && options.TargetSize > 0)
        {
            var tolerance = Math.Max(0, options.SizeTolerancePercent) / 100d;
            var minAccepted = options.TargetSize * (1d - tolerance);
            var delta = Math.Abs(minEdge - options.TargetSize);
            var sizeCloseness = Math.Max(0d, 1d - (delta / Math.Max(1d, options.TargetSize)));

            score += sizeCloseness * 25d;
            if (minEdge < minAccepted)
            {
                score -= 12d;
            }
            else if (minEdge >= options.TargetSize)
            {
                score += 5d;
            }
        }

        // Mild format preference.
        var format = NormalizeFormat(candidate.Format);
        if (options.PreferPng)
        {
            score += string.Equals(format, "png", StringComparison.OrdinalIgnoreCase) ? 4d : 0d;
        }
        else
        {
            score += string.Equals(format, "jpg", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(format, "jpeg", StringComparison.OrdinalIgnoreCase)
                ? 4d
                : 0d;
        }

        return score;
    }

    private static string NormalizeFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return "jpg";
        }

        var normalized = format.Trim().TrimStart('.').ToLowerInvariant();
        return normalized switch
        {
            "jpeg" => "jpg",
            _ => normalized
        };
    }

    private static double Clamp01(double value)
    {
        if (value < 0d)
        {
            return 0d;
        }

        if (value > 1d)
        {
            return 1d;
        }

        return value;
    }

    private static int CompareSacadPreferred(
        CoverCandidate left,
        CoverCandidate right,
        bool searchMode,
        int? targetSize)
    {
        var ratioA = AspectRatioDistanceFromSquare(left);
        var ratioB = AspectRatioDistanceFromSquare(right);
        var ratioComparison = CompareAspectRatioThreshold(ratioA, ratioB);
        if (ratioComparison != 0) return ratioComparison;

        var avgSizeA = AverageSize(left);
        var avgSizeB = AverageSize(right);

        var searchModeComparison = CompareSearchModeSimilarityAndTarget(left, right, searchMode, targetSize, avgSizeA, avgSizeB);
        if (searchModeComparison != 0) return searchModeComparison;

        var relevanceComparison = CompareRelevancePriority(left, right);
        if (relevanceComparison != 0) return relevanceComparison;

        var rankComparison = right.Rank.CompareTo(left.Rank);
        if (rankComparison != 0) return rankComparison;

        var knownFlagsComparison = CompareKnownFlags(left, right);
        if (knownFlagsComparison != 0) return knownFlagsComparison;

        var targetDistanceComparison = CompareTargetDistance(searchMode, targetSize, avgSizeA, avgSizeB);
        if (targetDistanceComparison != 0) return targetDistanceComparison;

        var formatComparison = CompareFormatPreference(left, right);
        if (formatComparison != 0) return formatComparison;

        return ratioB.CompareTo(ratioA);
    }

    private static int CompareAspectRatioThreshold(double ratioA, double ratioB)
    {
        return Math.Abs(ratioA - ratioB) > 0.15d
            ? ratioB.CompareTo(ratioA)
            : 0;
    }

    private static int CompareSearchModeSimilarityAndTarget(
        CoverCandidate left,
        CoverCandidate right,
        bool searchMode,
        int? targetSize,
        int avgSizeA,
        int avgSizeB)
    {
        if (!searchMode || !targetSize.HasValue)
        {
            return 0;
        }

        var similarComparison = CompareSimilarity(left, right);
        if (similarComparison != 0)
        {
            return similarComparison;
        }

        var targetComparison = CompareTargetBand(avgSizeA, avgSizeB, targetSize.Value);
        if (targetComparison != 0)
        {
            return targetComparison;
        }

        if (avgSizeA != avgSizeB && avgSizeA < targetSize.Value && avgSizeB < targetSize.Value)
        {
            return avgSizeA.CompareTo(avgSizeB);
        }

        return 0;
    }

    private static int CompareSimilarity(CoverCandidate left, CoverCandidate right)
    {
        var asim = left.IsSimilarToReference == true;
        var bsim = right.IsSimilarToReference == true;
        return asim != bsim ? asim.CompareTo(bsim) : 0;
    }

    private static int CompareTargetBand(int avgSizeA, int avgSizeB, int targetSize)
    {
        var aTargetCmp = avgSizeA.CompareTo(targetSize);
        var bTargetCmp = avgSizeB.CompareTo(targetSize);
        if (aTargetCmp < 0 && bTargetCmp >= 0)
        {
            return -1;
        }

        if (aTargetCmp >= 0 && bTargetCmp < 0)
        {
            return 1;
        }

        return 0;
    }

    private static int CompareRelevancePriority(CoverCandidate left, CoverCandidate right)
    {
        var relevanceA = left.Relevance ?? DefaultRelevance(left.Source);
        var relevanceB = right.Relevance ?? DefaultRelevance(right.Source);
        return CompareRelevance(relevanceA, relevanceB);
    }

    private static int CompareKnownFlags(CoverCandidate left, CoverCandidate right)
    {
        if (left.IsSizeKnown != right.IsSizeKnown)
        {
            return left.IsSizeKnown ? 1 : -1;
        }

        if (left.IsFormatKnown != right.IsFormatKnown)
        {
            return left.IsFormatKnown ? 1 : -1;
        }

        return 0;
    }

    private static int CompareTargetDistance(bool searchMode, int? targetSize, int avgSizeA, int avgSizeB)
    {
        if (!searchMode || !targetSize.HasValue || avgSizeA == avgSizeB)
        {
            return 0;
        }

        var diffA = Math.Abs(avgSizeA - targetSize.Value);
        var diffB = Math.Abs(avgSizeB - targetSize.Value);
        return diffB.CompareTo(diffA);
    }

    private static int CompareFormatPreference(CoverCandidate left, CoverCandidate right)
    {
        var formatA = NormalizeFormat(left.Format);
        var formatB = NormalizeFormat(right.Format);
        if (string.Equals(formatA, formatB, StringComparison.Ordinal))
        {
            return 0;
        }

        if (formatA == "png" && formatB == "jpg")
        {
            return 1;
        }

        if (formatA == "jpg" && formatB == "png")
        {
            return -1;
        }

        return 0;
    }

    private static int CompareRelevance(CoverRelevance a, CoverRelevance b)
    {
        if (a.UnrelatedRisk != b.UnrelatedRisk)
        {
            return a.UnrelatedRisk ? -1 : 1;
        }

        if (a.OnlyFrontCovers != b.OnlyFrontCovers)
        {
            return a.OnlyFrontCovers.CompareTo(b.OnlyFrontCovers);
        }

        if (a.Fuzzy != b.Fuzzy)
        {
            return a.Fuzzy ? -1 : 1;
        }

        return 0;
    }

    private static int AverageSize(CoverCandidate candidate)
    {
        var width = Math.Max(1, candidate.Width);
        var height = Math.Max(1, candidate.Height);
        return (width + height) / 2;
    }

    private static double AspectRatioDistanceFromSquare(CoverCandidate candidate)
    {
        var width = Math.Max(1, candidate.Width);
        var height = Math.Max(1, candidate.Height);
        return Math.Abs(((double)width / height) - 1d);
    }

    private static CoverRelevance DefaultRelevance(CoverSourceName source)
    {
        return source switch
        {
            CoverSourceName.Discogs => new CoverRelevance(Fuzzy: false, OnlyFrontCovers: false, UnrelatedRisk: false),
            _ => new CoverRelevance(Fuzzy: false, OnlyFrontCovers: true, UnrelatedRisk: false)
        };
    }
}
