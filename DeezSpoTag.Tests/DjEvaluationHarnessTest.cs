using System;
using System.Collections.Generic;
using System.Linq;
using DeezSpoTag.Services.Library.Dj;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Phase 9: whether the strategies actually produce good playlists.
///
/// <para>The premise of this file is that every strategy here could be badly wrong while
/// passing all of its own tests. A DJ that collapses onto a single seed produces a full,
/// plausible, seed-bearing playlist — every behavioural assertion still holds. Only
/// measuring the result against the library it came from catches that.</para>
///
/// <para>So the most important tests are the negative ones: they build a deliberately
/// collapsed space and assert the evaluator notices. Without those, the metrics are
/// decoration and a green run means nothing.</para>
/// </summary>
public sealed class DjEvaluationHarnessTest
{
    private readonly IMelodayDjStrategy[] _strategies =
    {
        new AnchorDjStrategy(),
        new CompanionDjStrategy(),
        new JourneyDjStrategy(),
    };

    // -------------------------------------------------- the metrics must bite

    [Fact]
    public void ACollapsedPlaylistIsCaught()
    {
        // Every candidate is nearly identical to seed 1 and nothing to seed 2. A
        // strategy that took the "most similar" tracks would fill the playlist with
        // near-duplicates and every one of its own tests would still pass.
        var request = Request(
            seeds: new long[] { 1, 2 },
            trackIds: Range(1, 12),
            trackCount: 10,
            affinities: Affinities(
                For(1, seed: 1, 1.00), For(2, seed: 1, 0.99), For(2, seed: 2, 0.02),
                For(3, seed: 1, 0.98), For(4, seed: 1, 0.98), For(5, seed: 1, 0.97),
                For(6, seed: 1, 0.97), For(7, seed: 1, 0.96), For(8, seed: 1, 0.96),
                For(9, seed: 1, 0.95), For(10, seed: 1, 0.95), For(11, seed: 1, 0.94)));

        var result = _strategies[0].BuildPlaylist(request);

        // The chosen tracks genuinely sound alike: every pair is near-identical. This
        // is the case a strategy's own tests cannot see, because every one of its
        // assertions still holds on a full, seed-bearing, plausible playlist.
        var report = DjPlaylistEvaluator.Evaluate(
            _strategies[0],
            request,
            result,
            FlatPairs(result, distance: 0.02));

        Assert.True(report.Metrics.PairwiseMeasured);
        Assert.False(report.Metrics.IsSpreadHealthy);
        Assert.Contains(report.Findings, finding => finding.Contains("near-duplicates", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnUnmeasuredPlaylistIsNotReportedAsHealthy()
    {
        // Silence is not success. Without pair distances there is no evidence either
        // way, and calling it healthy would make an unevaluated run look validated.
        var request = Request(
            seeds: new long[] { 1, 2 },
            trackIds: Range(1, 30),
            trackCount: 10,
            affinities: SpreadAffinities(30));

        var result = _strategies[0].BuildPlaylist(request);
        var report = DjPlaylistEvaluator.Evaluate(_strategies[0], request, result);

        Assert.False(report.Metrics.PairwiseMeasured);
        Assert.False(report.Metrics.IsSpreadHealthy);
        Assert.Equal(0d, report.Metrics.MeanPairwiseDistance);
    }

    [Fact]
    public void AHealthyPlaylistIsNotFlaggedForSpread()
    {
        // The other direction: the metric has to stay quiet when there is nothing wrong,
        // or every finding becomes noise and stops being read.
        var request = Request(
            seeds: new long[] { 1, 2 },
            trackIds: Range(1, 30),
            trackCount: 10,
            affinities: SpreadAffinities(30));

        var result = _strategies[0].BuildPlaylist(request);
        var report = DjPlaylistEvaluator.Evaluate(
            _strategies[0],
            request,
            result,
            FlatPairs(result, distance: 0.45));

        Assert.True(report.Metrics.IsSpreadHealthy);
        Assert.DoesNotContain(report.Findings, finding => finding.Contains("near-duplicates", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OneDuplicatedPairInsideAVariedPlaylistIsCaughtByTheClosestPair()
    {
        // The mean hides it, which is why the closest pair is measured separately.
        var request = Request(
            seeds: new long[] { 1 },
            trackIds: Range(1, 12),
            trackCount: 10,
            affinities: SpreadAffinities(12, seeds: new[] { 1L }));

        var result = _strategies[0].BuildPlaylist(request);
        var pairs = FlatPairs(result, distance: 0.45).ToList();
        var first = result.Candidates[0].TrackId;
        var second = result.Candidates[1].TrackId;
        pairs.Add(DjTrackPairDistance.Between(first, second, 0.001));

        var report = DjPlaylistEvaluator.Evaluate(_strategies[0], request, result, pairs);

        Assert.True(report.Metrics.IsSpreadHealthy);
        Assert.Equal(0.001, report.Metrics.ClosestPairDistance);
    }

    [Fact]
    public void APlaylistMissingItsSeedsIsFlagged()
    {
        // A DJ that dropped its own seeds is not playing what it was configured to play,
        // whatever its similarities look like.
        var request = Request(
            seeds: new long[] { 1 },
            trackIds: Range(1, 10),
            trackCount: 2,
            affinities: SpreadAffinities(10, seeds: new long[] { 1 }));

        // One seed, a budget of one, and the seed itself absent from the candidate pool
        // would be refused upstream, so the scenario is expressed by asking for fewer
        // tracks than there are seeds.
        var twoSeeds = Request(
            seeds: new long[] { 1, 2 },
            trackIds: Range(1, 10),
            trackCount: 1,
            affinities: SpreadAffinities(10, seeds: new long[] { 1, 2 }));

        var report = DjPlaylistEvaluator.Evaluate(
            _strategies[0],
            twoSeeds,
            _strategies[0].BuildPlaylist(twoSeeds));

        Assert.False(report.Metrics.RepresentsItsSeeds);
        Assert.Contains(report.Findings, finding => finding.Contains("seeds", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(request);
    }

    [Fact]
    public void ADuplicateTrackInTheOutputIsFlagged()
    {
        var request = Request(
            seeds: new long[] { 1 },
            trackIds: Range(1, 5),
            trackCount: 5,
            affinities: SpreadAffinities(5, seeds: new long[] { 1 }));

        // A strategy returning the same track twice is a real defect, and it must be
        // visible in the measurement rather than inferred from the metrics.
        var doubled = new DjStrategyResult
        {
            Candidates = new[]
            {
                new DjCandidate(1, 1d, DjItemReasons.Seed),
                new DjCandidate(2, 0.8, DjItemReasons.Neighbour),
                new DjCandidate(2, 0.8, DjItemReasons.Neighbour),
            },
        };

        var report = DjPlaylistEvaluator.Evaluate(_strategies[0], request, doubled);

        Assert.Contains(report.Findings, finding => finding.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnEmptyPlaylistIsReportedRatherThanScored()
    {
        var request = Request(seeds: new long[] { 1 }, trackIds: Range(1, 5), trackCount: 5, affinities: null);
        var empty = new DjStrategyResult { Candidates = Array.Empty<DjCandidate>() };

        var report = DjPlaylistEvaluator.Evaluate(_strategies[0], request, empty);

        Assert.Equal(0, report.Metrics.TrackCount);
        Assert.Contains(report.Findings, finding => finding.Contains("no tracks", StringComparison.OrdinalIgnoreCase));
    }

    // -------------------------------------------- spread against the library

    [Fact]
    public void TheCollapseThresholdDoesNotDependOnHowDenseTheLibraryIs()
    {
        // Why the threshold is absolute. A library-relative measure cannot work: in a
        // library where everything sounds alike, every selection looks as varied as the
        // library, so a collapsed playlist reads as perfectly healthy.
        var dense = Request(
            seeds: new long[] { 1 },
            trackIds: Range(1, 10),
            trackCount: 4,
            affinities: SpreadAffinities(10, seeds: new[] { 1L }, baseAffinity: 0.95, spread: 0.04));
        var sparse = Request(
            seeds: new long[] { 1 },
            trackIds: Range(1, 10),
            trackCount: 4,
            affinities: SpreadAffinities(10, seeds: new[] { 1L }, baseAffinity: 0.20, spread: 0.60));

        var denseResult = _strategies[0].BuildPlaylist(dense);
        var sparseResult = _strategies[0].BuildPlaylist(sparse);

        // Identical pair distances, opposite libraries. The verdict must be the same,
        // because the playlists sound the same.
        var denseReport = DjPlaylistEvaluator.Evaluate(
            _strategies[0], dense, denseResult, FlatPairs(denseResult, distance: 0.02));
        var sparseReport = DjPlaylistEvaluator.Evaluate(
            _strategies[0], sparse, sparseResult, FlatPairs(sparseResult, distance: 0.02));

        Assert.Equal(denseReport.Metrics.IsSpreadHealthy, sparseReport.Metrics.IsSpreadHealthy);
        Assert.False(denseReport.Metrics.IsSpreadHealthy);
    }

    [Fact]
    public void PairsAreBuiltWithoutSelfPairsOrDuplicates()
    {
        // A self-pair scored as zero would drag every average down, and a repeated track
        // would otherwise be counted against itself more than once.
        var pairs = DjPlaylistEvaluator.PairsOf(new long[] { 5, 3, 5, 1 });

        Assert.Equal(3, pairs.Count);
        Assert.All(pairs, pair => Assert.NotEqual(pair.Left, pair.Right));
        Assert.All(pairs, pair => Assert.True(pair.Left < pair.Right));
    }

    // ------------------------------------------------------- cross-strategy

    [Fact]
    public void AllThreeStrategiesRunOverTheSameRequest()
    {
        var request = Request(
            seeds: new long[] { 1, 2, 3 },
            trackIds: Range(1, 60),
            trackCount: 12,
            affinities: SpreadAffinities(60, seeds: new long[] { 1, 2, 3 }));

        var harness = DjEvaluationHarness.Run(request, _strategies);

        Assert.Equal(3, harness.Evaluations.Count);
        Assert.Contains(harness.Evaluations, evaluation => evaluation.Report.Strategy == "anchor");
        Assert.Contains(harness.Evaluations, evaluation => evaluation.Report.Strategy == "companion");
        Assert.Contains(harness.Evaluations, evaluation => evaluation.Report.Strategy == "journey");
    }

    [Fact]
    public void TheThreeStrategiesProduceMeasurablyDifferentSets()
    {
        // The question Phase 9 exists to answer. Three names over identical behaviour is
        // worse than one strategy, because it offers a choice that does not exist.
        var request = Request(
            seeds: new long[] { 1, 2, 3 },
            trackIds: Range(1, 80),
            trackCount: 12,
            affinities: SpreadAffinities(80, seeds: new long[] { 1, 2, 3 }));

        var harness = DjEvaluationHarness.Run(request, _strategies);

        Assert.False(
            harness.HasIndistinguishableStrategies(),
            "Two strategies produced the same set:\n" + harness.Summarise());
    }

    [Fact]
    public void EveryPairOfStrategiesIsReportedForOverlap()
    {
        var request = Request(
            seeds: new long[] { 1, 2 },
            trackIds: Range(1, 40),
            trackCount: 8,
            affinities: SpreadAffinities(40, seeds: new long[] { 1, 2 }));

        var harness = DjEvaluationHarness.Run(request, _strategies);

        foreach (var evaluation in harness.Evaluations)
        {
            // Every other strategy, not just the next one.
            Assert.Equal(2, evaluation.OverlapWithOthers.Count);
            Assert.DoesNotContain(evaluation.Report.Strategy, evaluation.OverlapWithOthers.Keys);
        }
    }

    [Fact]
    public void TwoStrategiesGivenIdenticalBehaviourAreDetectedAsIndistinguishable()
    {
        // The harness has to be able to say "these are the same strategy", or it cannot
        // be trusted when it says they are not.
        var request = Request(
            seeds: new long[] { 1, 2 },
            trackIds: Range(1, 40),
            trackCount: 8,
            affinities: SpreadAffinities(40, seeds: new long[] { 1, 2 }));

        var twins = new IMelodayDjStrategy[]
        {
            new AnchorDjStrategy(),
            new AnchorDjStrategy(),
        };

        Assert.True(DjEvaluationHarness.Run(request, twins).HasIndistinguishableStrategies());
    }

    [Fact]
    public void DisjointSetsDoNotCountAsIndistinguishable()
    {
        Assert.Equal(0d, DjPlaylistEvaluator.Overlap(new long[] { 1, 2 }, new long[] { 8, 9 }));
        Assert.Equal(1d, DjPlaylistEvaluator.Overlap(new long[] { 1, 2, 3 }, new long[] { 3, 2, 1 }));
        Assert.Equal(0d, DjPlaylistEvaluator.Overlap(Array.Empty<long>(), new long[] { 1 }));
    }

    [Fact]
    public void OverlapIsJaccardSoASharedTrackCountsOnce()
    {
        // Union, not the smaller set: {1,2} and {2,3} share one track of a union of
        // three, which is 1/3. Anything defined against one side only would read a
        // 50% overlap here and understate how different two sets really are.
        Assert.Equal(0.333d, DjPlaylistEvaluator.Overlap(new long[] { 1, 2 }, new long[] { 2, 3 }));
    }

    // ------------------------------------------------------------- arc

    [Fact]
    public void JourneyIsMeasuredForArcAndTheOtherStrategiesAreNot()
    {
        // Anchor and Companion return a ranking and promise nothing about order.
        // Reporting "no progression" for them would be reporting a defect in a
        // strategy that never intended to progress.
        var request = Request(
            seeds: new long[] { 1, 2, 3 },
            trackIds: Range(1, 60),
            trackCount: 12,
            affinities: SpreadAffinities(60, seeds: new long[] { 1, 2, 3 }));

        var harness = DjEvaluationHarness.Run(request, _strategies);

        Assert.NotNull(harness.Evaluations.Single(e => e.Report.Strategy == "journey").Report.Arc);
        Assert.Null(harness.Evaluations.Single(e => e.Report.Strategy == "anchor").Report.Arc);
        Assert.Null(harness.Evaluations.Single(e => e.Report.Strategy == "companion").Report.Arc);
    }

    [Fact]
    public void APlaylistThatNeverMovesIsReportedAsNoProgression()
    {
        // Every track sits at the same distance from the opening landmark, so nothing
        // was travelled even though the strategy is the one that promises to travel.
        var affinities = new Dictionary<long, IReadOnlyDictionary<long, double>>();
        foreach (var trackId in Range(1, 20))
        {
            affinities[trackId] = new Dictionary<long, double>
            {
                [1] = 0.70,
                [2] = 0.70,
                [3] = 0.70,
            };
        }

        var trackIds = Range(1, 12);
        var arc = DjPlaylistEvaluator.MeasureArc(trackIds, new DjStrategyRequest
        {
            Definition = new DjDefinitionDto { DjDefinitionId = "j", Name = "J", Strategy = "journey" },
            Seeds = new[] { new DjSeed(1, 1d, HasEmbedding: true), new DjSeed(2, 1d, HasEmbedding: true) },
            CandidateTrackIds = trackIds,
            TrackCount = 12,
            SonicCoveragePercent = 100d,
            OccurrenceKey = "2026-09-30",
            SeedAffinities = affinities,
        });

        Assert.False(arc.Progressed);
        Assert.Equal(0d, arc.Progression);
    }

    [Fact]
    public void APlaylistThatMovesAwayFromItsOpeningIsReportedAsProgressing()
    {
        var trackIds = Range(1, 12);
        var affinities = new Dictionary<long, IReadOnlyDictionary<long, double>>();
        foreach (var trackId in trackIds)
        {
            // High at the start of the arc, low by the end.
            affinities[trackId] = new Dictionary<long, double>
            {
                [1] = trackId <= 6 ? 0.95 : 0.40,
            };
        }

        var arc = DjPlaylistEvaluator.MeasureArc(trackIds, new DjStrategyRequest
        {
            Definition = new DjDefinitionDto { DjDefinitionId = "j", Name = "J", Strategy = "journey" },
            Seeds = new[] { new DjSeed(1, 1d, HasEmbedding: true) },
            CandidateTrackIds = trackIds,
            TrackCount = 12,
            SonicCoveragePercent = 100d,
            OccurrenceKey = "2026-09-30",
            SeedAffinities = affinities,
        });

        Assert.True(arc.Progressed);
        Assert.True(arc.Progression > 0d);
    }

    [Fact]
    public void AnUnmeasuredArcIsReportedAsNotProgressedRatherThanPerfect()
    {
        // Nothing measurable is not evidence of success, and reporting it as a perfect
        // score would make an unmeasured arc look like a validated one.
        var arc = DjPlaylistEvaluator.MeasureArc(
            new long[] { 1, 2, 3 },
            new DjStrategyRequest
            {
                Definition = new DjDefinitionDto { DjDefinitionId = "j", Name = "J", Strategy = "journey" },
                Seeds = new[] { new DjSeed(1, 1d, HasEmbedding: true) },
                CandidateTrackIds = new long[] { 1, 2, 3 },
                TrackCount = 3,
                SonicCoveragePercent = 100d,
                OccurrenceKey = "2026-09-30",
                SeedAffinities = null,
            });

        Assert.False(arc.Progressed);
    }

    // --------------------------------------------------------- determinism

    [Fact]
    public void EvaluationIsReproducible()
    {
        // An evaluation that varied between runs would be unusable as a signal, and the
        // strategies themselves are deterministic so nothing excuses the variance.
        var request = Request(
            seeds: new long[] { 1, 2, 3 },
            trackIds: Range(1, 60),
            trackCount: 12,
            affinities: SpreadAffinities(60, seeds: new long[] { 1, 2, 3 }));

        var summaries = Enumerable.Range(0, 10)
            .Select(_ => DjEvaluationHarness.Run(request, _strategies).Summarise())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Single(summaries);
    }

    [Fact]
    public void TheHarnessSummaryNamesEveryStrategyItMeasured()
    {
        var request = Request(
            seeds: new long[] { 1, 2 },
            trackIds: Range(1, 30),
            trackCount: 8,
            affinities: SpreadAffinities(30, seeds: new long[] { 1, 2 }));

        var summary = DjEvaluationHarness.Run(request, _strategies).Summarise();

        Assert.Contains("anchor", summary, StringComparison.Ordinal);
        Assert.Contains("companion", summary, StringComparison.Ordinal);
        Assert.Contains("journey", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void FindingsAreAttributedToTheStrategyThatProducedThem()
    {
        // An unattributed finding cannot be acted on: there is no way to know which DJ
        // or which configuration produced it.
        var request = Request(
            seeds: new long[] { 1, 2, 3 },
            trackIds: Range(1, 20),
            trackCount: 4,
            affinities: SpreadAffinities(20, seeds: new long[] { 1, 2, 3 }));

        var harness = DjEvaluationHarness.Run(request, _strategies);

        Assert.All(harness.AllFindings, finding =>
            Assert.True(
                _strategies.Any(strategy => finding.StartsWith(strategy.Strategy + ":", StringComparison.Ordinal)),
                $"'{finding}' is not attributed to a strategy."));
    }

    // ---------------------------------------------------------------- helpers

    private static IReadOnlyList<long> Range(int from, int count)
        => Enumerable.Range(from, count).Select(value => (long)value).ToArray();

    /// <summary>
    /// Every pair of a result's chosen tracks, at one distance.
    ///
    /// <para>Uniform so a test states one fact — "these tracks sound alike" — instead of
    /// inventing a whole distance matrix and then having to reason about it.</para>
    /// </summary>
    private static IReadOnlyList<DjTrackPairDistance> FlatPairs(DjStrategyResult result, double distance)
        => DjPlaylistEvaluator.PairsOf(result.Candidates.Select(candidate => candidate.TrackId).ToList())
            .Select(pair => DjTrackPairDistance.Between(pair.Left, pair.Right, distance))
            .ToList();

    private static (long Candidate, long Seed, double Affinity) For(long candidate, long seed, double affinity)
        => (candidate, seed, affinity);

    private static DjStrategyRequest Request(
        IReadOnlyList<long> seeds,
        IReadOnlyList<long> trackIds,
        int trackCount,
        IReadOnlyDictionary<long, IReadOnlyDictionary<long, double>>? affinities)
        => new()
        {
            Definition = new DjDefinitionDto
            {
                DjDefinitionId = "eval",
                Name = "Evaluation",
                Strategy = AnchorDjStrategy.StrategyId,
                MinTracks = 1,
                MaxTracks = 60,
            },
            Seeds = seeds.Select(seedId => new DjSeed(seedId, 1d, HasEmbedding: true)).ToList(),
            CandidateTrackIds = trackIds,
            TrackCount = trackCount,
            SonicCoveragePercent = 100d,
            OccurrenceKey = "2026-09-30",
            SeedAffinities = affinities,
        };

    private static Dictionary<long, IReadOnlyDictionary<long, double>> Affinities(
        params (long Candidate, long Seed, double Affinity)[] entries)
    {
        var table = new Dictionary<long, Dictionary<long, double>>();
        foreach (var (candidate, seed, affinity) in entries)
        {
            if (!table.TryGetValue(candidate, out var perSeed))
            {
                perSeed = new Dictionary<long, double>();
                table[candidate] = perSeed;
            }

            perSeed[seed] = affinity;
        }

        return table.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyDictionary<long, double>)entry.Value);
    }

    /// <summary>
    /// A library that is not collapsed: every candidate has its own distance from each
    /// seed, spread evenly and reproducibly.
    ///
    /// <para>Deterministic by construction — derived from the track id — because an
    /// evaluation over a random space would itself be unreproducible, which would make
    /// every finding a one-off.</para>
    /// </summary>
    private static Dictionary<long, IReadOnlyDictionary<long, double>> SpreadAffinities(
        int trackCount,
        IReadOnlyList<long>? seeds = null,
        double baseAffinity = 0.60,
        double spread = 0.30)
    {
        var seedIds = seeds ?? new long[] { 1 };
        var table = new Dictionary<long, IReadOnlyDictionary<long, double>>();

        for (var trackId = 1; trackId <= trackCount; trackId++)
        {
            var perSeed = new Dictionary<long, double>();
            foreach (var seedId in seedIds)
            {
                // Varies by track and by seed, so no two candidates look alike and no
                // candidate is close to every seed equally.
                var offset = ((trackId * 37) + (seedId * 13)) % 100 / 100d;
                perSeed[seedId] = Math.Clamp(
                    baseAffinity + (((offset - 0.5d) * 2d) * spread),
                    0.05d,
                    0.99d);
            }

            table[trackId] = perSeed;
        }

        return table;
    }
}