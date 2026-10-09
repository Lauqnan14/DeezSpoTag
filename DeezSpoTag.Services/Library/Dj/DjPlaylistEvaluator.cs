using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DeezSpoTag.Services.Library.Dj;

/// <summary>
/// Measures a strategy's decision.
///
/// <para>Pure computation. It is handed the tracks a strategy chose, the seed
/// affinities that were available, and — when the caller could afford it — the
/// distances between the chosen tracks themselves. Nothing here reads a database, a
/// clock or an index, which is what makes an evaluation reproducible, and
/// reproducibility is the whole point of running one.</para>
///
/// <para>The measures exist because each strategy could be badly wrong while passing
/// all of its own tests. All three could collapse onto one seed and still produce a
/// full, plausible, seed-bearing playlist.</para>
/// </summary>
public static class DjPlaylistEvaluator
{
    public static DjEvaluationReport Evaluate(
        IMelodayDjStrategy strategy,
        DjStrategyRequest request,
        DjStrategyResult result,
        IReadOnlyList<DjTrackPairDistance>? pairwiseDistances = null)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);

        var chosen = result.Candidates.ToList();
        var trackIds = chosen.Select(candidate => candidate.TrackId).ToArray();

        var seedIds = (request.Seeds ?? Array.Empty<DjSeed>()).Select(seed => seed.TrackId).ToHashSet();

        var metrics = new DjPlaylistMetrics
        {
            TrackCount = chosen.Count,
            SeedCount = seedIds.Count,
            SeedsPlaced = chosen.Count(candidate => seedIds.Contains(candidate.TrackId)),
            DistinctTracks = trackIds.Distinct().Count(),
            PairwiseMeasured = pairwiseDistances is not null,
            MeanPairwiseDistance = MeanPairwiseDistance(pairwiseDistances),
            ClosestPairDistance = ClosestPairDistance(pairwiseDistances),
            MeanDistanceToNearestSeed = MeanDistanceToNearestSeed(chosen),
            ReasonCounts = chosen
                .GroupBy(candidate => candidate.Reason ?? string.Empty, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
        };

        // Only the strategy that claims to travel is measured for travelling. Reporting
        // "no progression" for Anchor would be reporting a defect in a strategy that
        // never intended to progress.
        var arc = string.Equals(strategy.Strategy, JourneyDjStrategy.StrategyId, StringComparison.OrdinalIgnoreCase)
            ? MeasureArc(trackIds, request)
            : null;

        return new DjEvaluationReport
        {
            Strategy = strategy.Strategy,
            Metrics = metrics,
            Arc = arc,
            TrackIds = trackIds,
            Findings = Findings(metrics, arc, request.TrackCount),
        };
    }

    private static IReadOnlyList<string> Findings(DjPlaylistMetrics metrics, DjArcMetrics? arc, int requestedTrackCount)
    {
        var findings = new List<string>();

        if (metrics.TrackCount == 0)
        {
            findings.Add("The DJ produced no tracks at all.");
            return findings;
        }

        if (!metrics.RepresentsItsSeeds)
        {
            findings.Add(string.Format(
                CultureInfo.InvariantCulture,
                "Only {0} of {1} seeds were placed, so the DJ is not playing what it was configured to play.",
                metrics.SeedsPlaced,
                metrics.SeedCount));
        }

        if (metrics.PairwiseMeasured && metrics.MeanPairwiseDistance < DjPlaylistMetrics.CollapsedPairwiseDistance)
        {
            findings.Add(string.Format(
                CultureInfo.InvariantCulture,
                "The chosen tracks are only {0:F3} apart on average, which usually means the DJ returned near-duplicates of one sound.",
                metrics.MeanPairwiseDistance));
        }

        if (metrics.DistinctTracks != metrics.TrackCount)
        {
            findings.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0} of {1} entries were duplicate tracks.",
                metrics.TrackCount - metrics.DistinctTracks,
                metrics.TrackCount));
        }

        if (requestedTrackCount > 0 && metrics.TrackCount < requestedTrackCount)
        {
            findings.Add(string.Format(
                CultureInfo.InvariantCulture,
                "Only {0} of {1} requested tracks could be chosen.",
                metrics.TrackCount,
                requestedTrackCount));
        }

        if (arc is { Progressed: false })
        {
            findings.Add(
                "The arc did not measurably move: the second half is no further from the opening landmark than the first.");
        }

        return findings;
    }

    /// <summary>
    /// Mean dissimilarity across every measured pair of chosen tracks.
    ///
    /// <para>Zero when unmeasured, and reported as unmeasured alongside. A zero that
    /// meant "identical" and a zero that meant "we did not look" are indistinguishable
    /// in a number, so the distinction is carried by
    /// <see cref="DjPlaylistMetrics.PairwiseMeasured"/> rather than inferred from the
    /// value.</para>
    /// </summary>
    private static double MeanPairwiseDistance(IReadOnlyList<DjTrackPairDistance>? pairs)
        => pairs is null || pairs.Count == 0
            ? 0d
            : Round(pairs.Average(pair => ClampDistance(pair.Distance)));

    private static double ClosestPairDistance(IReadOnlyList<DjTrackPairDistance>? pairs)
        => pairs is null || pairs.Count == 0
            ? 0d
            : Round(pairs.Min(pair => ClampDistance(pair.Distance)));

    /// <summary>
    /// How far the chosen tracks strayed from their nearest seed, on average.
    /// </summary>
    /// <remarks>
    /// A seed's distance to itself is zero, which is what "this track is a landmark"
    /// should mean. Averaged in with the rest, so a DJ that plays mostly seeds reports
    /// a low figure — which is correct, and is why this is not the collapse signal.
    /// </remarks>
    private static double MeanDistanceToNearestSeed(IReadOnlyList<DjCandidate> chosen)
        => chosen.Count == 0
            ? 0d
            : Round(chosen.Average(candidate => 1d - ClampSimilarity(candidate.Similarity)));

    /// <summary>
    /// Whether the second half of the playlist sits further from the opening landmark
    /// than the first.
    /// </summary>
    /// <remarks>
    /// Measured against the opening landmark specifically rather than each track's
    /// nearest seed: a playlist that merely alternated between two seeds would score
    /// well on the nearest-seed measure while never having gone anywhere.
    /// </remarks>
    public static DjArcMetrics MeasureArc(IReadOnlyList<long> trackIds, DjStrategyRequest request)
    {
        var seedIds = (request.Seeds ?? Array.Empty<DjSeed>()).Select(seed => seed.TrackId).ToList();
        if (trackIds.Count == 0 || seedIds.Count == 0)
        {
            return new DjArcMetrics { OpeningAffinity = 0d, ClosingAffinity = 0d };
        }

        var opening = seedIds[0];
        var affinities = request.SeedAffinities
            ?? new Dictionary<long, IReadOnlyDictionary<long, double>>();

        var measured = new List<double>(trackIds.Count);
        foreach (var trackId in trackIds)
        {
            if (affinities.TryGetValue(trackId, out var perSeed)
                && perSeed.TryGetValue(opening, out var value)
                && double.IsFinite(value))
            {
                measured.Add(ClampSimilarity(value));
            }
        }

        if (measured.Count < 2)
        {
            // Nothing measurable. Reported as no progression rather than as a perfect
            // one, because an unmeasured arc has not been shown to work.
            return new DjArcMetrics { OpeningAffinity = 0d, ClosingAffinity = 0d };
        }

        var midpoint = measured.Count / 2;
        return new DjArcMetrics
        {
            OpeningAffinity = Round(measured.Take(midpoint).Average()),
            ClosingAffinity = Round(measured.Skip(midpoint).Average()),
        };
    }

    /// <summary>
    /// Every pair of the given tracks, excluding self-pairs.
    ///
    /// <para>The caller uses this to decide what to ask the similarity index for. Built
    /// here so the pairing is defined once: a self-pair scored as zero would drag every
    /// average down, and an off-by-one on the bounds would silently omit a track.</para>
    /// </summary>
    public static IReadOnlyList<(long Left, long Right)> PairsOf(IReadOnlyList<long> trackIds)
    {
        var distinct = trackIds.Distinct().OrderBy(trackId => trackId).ToList();
        var pairs = new List<(long Left, long Right)>();
        for (var left = 0; left < distinct.Count; left++)
        {
            for (var right = left + 1; right < distinct.Count; right++)
            {
                // Emitted in ascending value order, not merely index order, so a pair
                // has one canonical form. Without that a caller matching a measured
                // distance back to its pair has to normalise both sides, and a missed
                // normalisation silently drops the measurement.
                pairs.Add((distinct[left], distinct[right]));
            }
        }

        return pairs;
    }

    /// <summary>
    /// How different two strategies' output is, as a Jaccard overlap.
    ///
    /// <para>Zero means disjoint, one means identical. Two strategies that score one
    /// here are the same strategy under two names, which no single-strategy test can
    /// detect.</para>
    /// </summary>
    public static double Overlap(IReadOnlyList<long> left, IReadOnlyList<long> right)
    {
        var leftSet = left.ToHashSet();
        var rightSet = right.ToHashSet();
        if (leftSet.Count == 0 || rightSet.Count == 0)
        {
            return 0d;
        }

        var intersection = leftSet.Intersect(rightSet).Count();
        var union = leftSet.Union(rightSet).Count();
        return Math.Round(intersection / (double)union, 3);
    }

    private static double ClampSimilarity(double value)
        => double.IsFinite(value) ? Math.Clamp(value, 0d, 1d) : 0d;

    private static double ClampDistance(double value)
        => double.IsFinite(value) ? Math.Clamp(value, 0d, 1d) : 1d;

    private static double Round(double value)
        => Math.Round(value, 4);
}