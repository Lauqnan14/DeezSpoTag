using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

internal sealed record MelodayVibeMatch(long TrackId, double Similarity);

internal static class MelodayVibeSelector
{
    internal static IReadOnlyList<MelodayVibeMatch> Select(
        IReadOnlyList<long> seedTrackIds,
        IReadOnlyCollection<TrackAnalysisResultDto> analyses,
        IReadOnlySet<long> allowedTrackIds,
        IReadOnlySet<long> excludedTrackIds,
        int limit,
        double maximumDistance)
    {
        if (seedTrackIds.Count == 0 || analyses.Count == 0 || limit <= 0)
        {
            return Array.Empty<MelodayVibeMatch>();
        }

        var completedByTrackId = analyses
            .Where(IsUsableAnalysis)
            .Where(analysis => allowedTrackIds.Contains(analysis.TrackId))
            .GroupBy(static analysis => analysis.TrackId)
            .ToDictionary(static group => group.Key, static group => group.First());
        var seedIds = seedTrackIds
            .Where(completedByTrackId.ContainsKey)
            .Distinct()
            .ToHashSet();
        if (seedIds.Count == 0)
        {
            return Array.Empty<MelodayVibeMatch>();
        }

        var seedAnalyses = seedIds
            .Select(seedId => completedByTrackId[seedId])
            .ToList();
        var normalizedMaximumDistance = Math.Clamp(maximumDistance, 0d, 1d);

        return completedByTrackId.Values
            .Where(candidate => !seedIds.Contains(candidate.TrackId))
            .Where(candidate => !excludedTrackIds.Contains(candidate.TrackId))
            .Select(candidate => new MelodayVibeMatch(
                candidate.TrackId,
                seedAnalyses.Max(seed => VibeSimilarityScorer.CalculateSimilarity(seed, candidate))))
            .Where(match => 1d - match.Similarity <= normalizedMaximumDistance)
            .OrderByDescending(static match => match.Similarity)
            .ThenBy(static match => match.TrackId)
            .Take(limit)
            .ToList();
    }

    internal static bool IsUsableAnalysis(TrackAnalysisResultDto analysis)
        => (string.Equals(analysis.Status, "complete", StringComparison.OrdinalIgnoreCase)
            || string.Equals(analysis.Status, "completed", StringComparison.OrdinalIgnoreCase))
           && VibeSimilarityScorer.HasMeaningfulFeatureCoverage(analysis);
}
