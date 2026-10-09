using System;
using System.Collections.Generic;
using System.Linq;
using DeezSpoTag.Services.Library.Dj;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// The Journey strategy.
///
/// <para>Journey is the first strategy where the <em>order</em> is the decision rather
/// than the ranking. Anchor and Companion both return a set whose order is mostly
/// incidental; Journey builds an arc and places each track at a point along it. Most of
/// these tests are therefore about position: a track can be chosen for where it fits
/// rather than how good it is, and a leg can be short without the arc becoming wrong.</para>
///
/// <para>Determinism is held to the same standard as everywhere else. A re-run replaces
/// the previous playlist in place, so a reordering would replace a good playlist for
/// no reason.</para>
/// </summary>
public sealed class JourneyDjStrategyTest
{
    private readonly JourneyDjStrategy _strategy = new();

    // ------------------------------------------------------------- basic shape

    [Fact]
    public void TheStrategyIsSelectedByItsName()
    {
        Assert.Equal("journey", _strategy.Strategy);
        Assert.Equal(JourneyDjStrategy.StrategyId, _strategy.Strategy);
        Assert.NotEqual("anchor", _strategy.Strategy);
        Assert.NotEqual("companion", _strategy.Strategy);
    }

    [Fact]
    public void AnEmptyCandidatePoolProducesNothingAndSaysSo()
    {
        var result = _strategy.BuildPlaylist(Request(seeds: new long[] { 1 }, candidates: Array.Empty<long>()));

        Assert.Empty(result.Candidates);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void NoUsableSeedProducesNothingRatherThanAnUnrelatedSet()
    {
        var result = _strategy.BuildPlaylist(Request(seeds: new long[] { 999 }, candidates: new long[] { 1, 2, 3 }));

        Assert.Empty(result.Candidates);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("arc", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RequestingZeroTracksYieldsNothing()
    {
        var result = _strategy.BuildPlaylist(Request(seeds: new long[] { 1, 2 }, candidates: new long[] { 1, 2, 3 }, trackCount: 0));

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void ANegativeTrackCountIsTreatedAsNone()
    {
        var result = _strategy.BuildPlaylist(Request(seeds: new long[] { 1, 2 }, candidates: new long[] { 1, 2, 3 }, trackCount: -5));

        Assert.Empty(result.Candidates);
    }

    // -------------------------------------------------------- landmarks first

    [Fact]
    public void LandmarksLeadTheArcInWeightOrder()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: Array.Empty<long>(),
            weightedSeeds: new[]
            {
                new DjSeed(30, 1d, HasEmbedding: true),
                new DjSeed(10, 5d, HasEmbedding: true),
                new DjSeed(20, 3d, HasEmbedding: true),
            },
            candidates: new long[] { 10, 20, 30, 40 },
            trackCount: 4,
            affinities: Affinities((40, 10, 0.7), (40, 20, 0.7), (40, 30, 0.7))));

        // The arc runs heaviest seed to lightest, and nothing displaces a landmark.
        Assert.Equal(new long[] { 10, 20, 30 }, result.Candidates.Take(3).Select(candidate => candidate.TrackId));
        Assert.All(result.Candidates.Take(3), candidate => Assert.Equal(DjItemReasons.Seed, candidate.Reason));
    }

    [Fact]
    public void EqualWeightLandmarksAreOrderedDeterministically()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 30, 10, 20 },
            candidates: new long[] { 30, 10, 20, 40 },
            trackCount: 4,
            affinities: Affinities((40, 10, 0.8), (40, 20, 0.8), (40, 30, 0.8))));

        Assert.Equal(new long[] { 10, 20, 30 }, result.Candidates.Take(3).Select(candidate => candidate.TrackId));
    }

    [Fact]
    public void ALandmarkIsKeptEvenWhenAJourneyTrackWouldScoreHigher()
    {
        // The structure of the arc is fixed before anything else is considered.
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20 },
            candidates: new long[] { 10, 20, 40 },
            trackCount: 3,
            affinities: Affinities((40, 10, 1.0), (40, 20, 1.0))));

        Assert.Equal(2, result.Candidates.Count(candidate => candidate.Reason == DjItemReasons.Seed));
        Assert.Equal(10, result.Candidates[0].TrackId);
        Assert.Equal(20, result.Candidates[1].TrackId);
    }

    [Fact]
    public void FewerTracksThanLandmarksTruncatesRatherThanReordering()
    {
        // Asking for less room than the arc needs must not reorder the arc.
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20, 30 },
            candidates: new long[] { 10, 20, 30, 40 },
            trackCount: 2,
            affinities: Affinities((40, 10, 0.8), (40, 20, 0.8), (40, 30, 0.8))));

        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal(new long[] { 10, 20 }, result.Candidates.Select(candidate => candidate.TrackId));
    }

    [Fact]
    public void ADuplicateLandmarkIsNotPlacedTwice()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: Array.Empty<long>(),
            weightedSeeds: new[] { new DjSeed(10, 1d, HasEmbedding: true), new DjSeed(10, 1d, HasEmbedding: true) },
            candidates: new long[] { 10, 20 },
            trackCount: 2,
            affinities: Affinities((20, 10, 0.8))));

        Assert.Single(result.Candidates, candidate => candidate.Reason == DjItemReasons.Seed);
    }

    // ------------------------------------------------- what makes it different

    [Fact]
    public void ATrackIsPlacedForWhereItFitsNotForHowGoodItIs()
    {
        // The property that makes Journey more than a ranked set. Two landmarks, so
        // one leg. Track 60 fits the gap; track 70 is close to landmark 10 but
        // nothing to do with 20, so it cannot bridge at all.
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20 },
            candidates: new long[] { 10, 20, 60, 70 },
            trackCount: 4,
            affinities: Affinities(
                (60, 10, 0.70), (60, 20, 0.68),
                (70, 10, 0.95), (70, 20, 0.05))));

        // Only one track can honestly hold the middle of this leg, so the arc is
        // three tracks and says it is short rather than padding with track 70.
        Assert.Equal(3, result.Candidates.Count);
        Assert.Contains(60, result.Candidates.Select(candidate => candidate.TrackId));
        Assert.DoesNotContain(70, result.Candidates.Select(candidate => candidate.TrackId));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("3 of the 4", StringComparison.Ordinal));
    }

    [Fact]
    public void ATrackTooFarFromOneEndOfALegCannotHoldItsMiddle()
    {
        // The floor, isolated. Track 80 is a good match for landmark 10 and almost
        // nothing for landmark 20. Averaged it would look respectable, but placing it
        // mid-arc claims a position its measured affinities do not support.
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20 },
            candidates: new long[] { 10, 20, 80 },
            trackCount: 3,
            affinities: Affinities((80, 10, 0.90), (80, 20, 0.10))));

        Assert.Equal(new long[] { 10, 20 }, result.Candidates.Select(candidate => candidate.TrackId));
        Assert.All(result.Candidates, candidate => Assert.Equal(DjItemReasons.Seed, candidate.Reason));
    }

    [Fact]
    public void ATrackJustAboveTheFloorCanStillBridgeALeg()
    {
        // The floor must exclude the lopsided, not the merely modest. Track 81 clears
        // it against both landmarks and belongs mid-arc.
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20 },
            candidates: new long[] { 10, 20, 81 },
            trackCount: 3,
            affinities: Affinities((81, 10, 0.30), (81, 20, 0.28))));

        Assert.Contains(81, result.Candidates.Select(candidate => candidate.TrackId));
    }

    [Fact]
    public void ATrackCloseToOneLandmarkOnlyCannotBridgeALeg()
    {
        // A leg has two ends. Scoring on one end is neighbourhood, which is Anchor's
        // job, and including it would put tracks in the arc that fit nowhere in it.
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20 },
            candidates: new long[] { 10, 20, 70 },
            trackCount: 3,
            affinities: Affinities((70, 10, 0.95))));

        Assert.Equal(new long[] { 10, 20 }, result.Candidates.Select(candidate => candidate.TrackId));
    }

    [Fact]
    public void JourneyTracksAppearBetweenTheirLandmarksInArcOrder()
    {
        // Three landmarks means two legs. A track assigned to the second leg must come
        // after the second landmark, never before the first.
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20, 30 },
            candidates: new long[] { 10, 20, 30, 50, 60 },
            trackCount: 5,
            affinities: Affinities(
                (50, 10, 0.80), (50, 20, 0.78),
                (60, 20, 0.80), (60, 30, 0.78))));

        var order = result.Candidates.Select(candidate => candidate.TrackId).ToArray();
        Assert.Equal(50, order[3]);
        Assert.Equal(60, order[4]);
    }

    [Fact]
    public void ATravelTrackIsNotUsedToFillAnEarlierLeg()
    {
        // A track that fits the second leg must not leak into the first, or the arc
        // would double back on itself.
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20, 30 },
            candidates: new long[] { 10, 20, 30, 60 },
            trackCount: 4,
            affinities: Affinities((60, 20, 0.80), (60, 30, 0.78))));

        // Only one journey track available, and it belongs to the second leg. The
        // first leg gets nothing rather than borrowing.
        Assert.Equal(4, result.Candidates.Count);
        Assert.Equal(60, result.Candidates[3].TrackId);
    }

    [Fact]
    public void EveryTrackAppearsOnlyOnceAcrossTheWholeArc()
    {
        // A track that could serve two legs must not fill both, or the playlist would
        // contain the same track twice.
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20, 30 },
            candidates: new long[] { 10, 20, 30, 40, 50, 60 },
            trackCount: 6,
            affinities: Affinities(
                (40, 10, 0.75), (40, 20, 0.74),
                (40, 20, 0.74), (40, 30, 0.75),
                (50, 10, 0.70), (50, 20, 0.72),
                (60, 20, 0.70), (60, 30, 0.72))));

        var ids = result.Candidates.Select(candidate => candidate.TrackId).ToArray();
        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public void ABalancedTrackIsPreferredOnALegOverALopsidedOne()
    {
        // Both bridge the gap, but one sits between the landmarks while the other
        // leans on the first. The balanced one is the better fit for the position.
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20 },
            candidates: new long[] { 10, 20, 50, 60 },
            trackCount: 4,
            affinities: Affinities(
                (50, 10, 0.90), (50, 20, 0.30),
                (60, 10, 0.62), (60, 20, 0.60))));

        Assert.Equal(60, result.Candidates[2].TrackId);
    }

    [Fact]
    public void ABudgetIsSpreadAcrossLegsRatherThanSpentOnTheFirst()
    {
        // The failure a fixed per-leg budget would cause: a rich opening and a starved
        // ending, which is not a journey.
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20, 30 },
            candidates: new long[] { 10, 20, 30, 50, 51, 52, 60 },
            trackCount: 7,
            affinities: Affinities(
                (50, 10, 0.80), (50, 20, 0.78),
                (51, 10, 0.80), (51, 20, 0.78),
                (52, 10, 0.80), (52, 20, 0.78),
                (60, 20, 0.80), (60, 30, 0.78))));

        // Four tracks bridge the first leg and one the second; all four plus the one
        // are used rather than the first leg claiming every slot.
        Assert.Equal(7, result.Candidates.Count);
        Assert.Equal(60, result.Candidates[6].TrackId);
    }

    [Fact]
    public void ALegWithNoCandidatesIsReportedRatherThanQuietlySkipped()
    {
        // A hole in the arc is audible as a jump. A person hearing it should be able
        // to find out that no track could be found, rather than assume it was a choice.
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20, 30 },
            candidates: new long[] { 10, 20, 30 },
            trackCount: 5,
            affinities: null));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("connect", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ASingleLandmarkIsReportedAsHavingNoArc()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10 },
            candidates: new long[] { 10, 30 },
            trackCount: 2,
            affinities: Affinities((30, 10, 0.80))));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("arc", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new long[] { 10, 30 }, result.Candidates.Select(candidate => candidate.TrackId));
    }

    [Fact]
    public void TwoLandmarksAreReportedAsHavingASingleSection()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20 },
            candidates: new long[] { 10, 20, 30 },
            trackCount: 3,
            affinities: Affinities((30, 10, 0.80), (30, 20, 0.78))));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("section", StringComparison.OrdinalIgnoreCase));
    }

    // -------------------------------------------------------------- selection

    [Fact]
    public void TheTrackCountIsHonoured()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20 },
            candidates: new long[] { 10, 20, 30, 40, 50, 60 },
            trackCount: 3,
            affinities: Affinities((30, 10, 0.8), (30, 20, 0.8), (40, 10, 0.8), (40, 20, 0.8), (50, 10, 0.8), (50, 20, 0.8), (60, 10, 0.8), (60, 20, 0.8))));

        Assert.Equal(3, result.Candidates.Count);
    }

    [Fact]
    public void FewerTracksThanRequestedYieldsEverythingAvailableAndSaysSo()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20 },
            candidates: new long[] { 10, 20, 30 },
            trackCount: 10,
            affinities: Affinities((30, 10, 0.8), (30, 20, 0.7))));

        Assert.Equal(3, result.Candidates.Count);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("10", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnscoredTrackIsNotChosen()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20 },
            candidates: new long[] { 10, 20, 30, 999 },
            trackCount: 4,
            affinities: Affinities((30, 10, 0.8), (30, 20, 0.7))));

        Assert.DoesNotContain(999, result.Candidates.Select(candidate => candidate.TrackId));
    }

    [Fact]
    public void ATieInLegScoreIsBrokenByTrackId()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20 },
            candidates: new long[] { 10, 20, 30, 40 },
            trackCount: 4,
            affinities: Affinities((30, 10, 0.7), (30, 20, 0.7), (40, 10, 0.7), (40, 20, 0.7))));

        Assert.Equal(new long[] { 10, 20, 30, 40 }, result.Candidates.Select(candidate => candidate.TrackId));
    }

    [Fact]
    public void AnUnusableAffinityNeverCorruptsTheArc()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20 },
            candidates: new long[] { 10, 20, 30, 40 },
            trackCount: 4,
            affinities: Affinities((30, 10, double.NaN), (30, 20, 0.7), (40, 10, 0.8), (40, 20, 0.7))));

        Assert.Equal(new long[] { 10, 20, 40 }, result.Candidates.Select(candidate => candidate.TrackId));
    }

    [Fact]
    public void EveryChosenTrackComesFromTheCandidatePool()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10 },
            candidates: new long[] { 10, 30 },
            trackCount: 5,
            affinities: Affinities((30, 10, 0.8), (999, 10, 0.99))));

        var pool = new HashSet<long> { 10, 30 };
        Assert.All(result.Candidates, candidate => Assert.Contains(candidate.TrackId, pool));
    }

    [Fact]
    public void EveryScoreStaysInTheUnitRange()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20, 30 },
            candidates: new long[] { 10, 20, 30, 50, 60 },
            trackCount: 5,
            affinities: Affinities(
                (50, 10, 1.0), (50, 20, 1.0), (50, 30, 1.0),
                (60, 10, 1.0), (60, 20, 1.0), (60, 30, 1.0))));

        Assert.All(result.Candidates, candidate => Assert.InRange(candidate.Similarity, 0d, 1d));
    }

    [Fact]
    public void WithNoAffinitiesTheArcIsLandmarksOnly()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20, 30 },
            candidates: new long[] { 10, 20, 30, 40 },
            trackCount: 4,
            affinities: null));

        Assert.Equal(new long[] { 10, 20, 30 }, result.Candidates.Select(candidate => candidate.TrackId));
        Assert.All(result.Candidates, candidate => Assert.Equal(DjItemReasons.Seed, candidate.Reason));
    }

    [Fact]
    public void LowCoverageIsReportedBecauseTheArcIsWorthLessThanItLooks()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20 },
            candidates: new long[] { 10, 20, 30 },
            trackCount: 3,
            coverage: 4.5,
            affinities: Affinities((30, 10, 0.8), (30, 20, 0.7))));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("4.5", StringComparison.Ordinal));
    }

    [Fact]
    public void HealthyCoverageIsNotReportedAsAProblem()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20 },
            candidates: new long[] { 10, 20, 30 },
            trackCount: 3,
            coverage: 88.1,
            affinities: Affinities((30, 10, 0.8), (30, 20, 0.7))));

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Contains("embedding", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ASuccessfulRunReportsHowManyLandmarksItFollowed()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20, 30 },
            candidates: new long[] { 10, 20, 30, 50 },
            trackCount: 4,
            affinities: Affinities((50, 10, 0.8), (50, 20, 0.7))));

        Assert.NotNull(result.SeedSummary);
        Assert.Contains("3", result.SeedSummary!, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ determinism

    [Fact]
    public void TheSameRequestProducesTheSameArc()
    {
        var request = Request(
            seeds: new long[] { 10, 20, 30 },
            candidates: new long[] { 10, 20, 30, 50, 51, 60, 61 },
            trackCount: 6,
            affinities: Affinities(
                (50, 10, 0.81), (50, 20, 0.62),
                (51, 10, 0.79), (51, 20, 0.64),
                (60, 20, 0.82), (60, 30, 0.60),
                (61, 20, 0.78), (61, 30, 0.63)));

        var runs = Enumerable.Range(0, 25)
            .Select(_ => _strategy.BuildPlaylist(request))
            .ToList();

        var expected = runs[0].Candidates.Select(candidate => candidate.TrackId).ToArray();
        Assert.All(runs, run =>
            Assert.Equal(expected, run.Candidates.Select(candidate => candidate.TrackId)));
    }

    [Fact]
    public void ReorderingTheInputDoesNotReorderTheArc()
    {
        var affinities = Affinities(
            (50, 10, 0.7), (50, 20, 0.7),
            (60, 20, 0.7), (60, 30, 0.7));

        var forwards = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20, 30 },
            candidates: new long[] { 10, 20, 30, 50, 60 },
            trackCount: 5,
            affinities: affinities));

        var backwards = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 30, 20, 10 },
            candidates: new long[] { 60, 50, 30, 20, 10 },
            trackCount: 5,
            affinities: affinities));

        Assert.Equal(
            forwards.Candidates.Select(candidate => candidate.TrackId),
            backwards.Candidates.Select(candidate => candidate.TrackId));
    }

    [Fact]
    public void JourneyDiffersFromAnchorAndCompanionOnPosition()
    {
        // Journey is only distinct if its ordering differs. Same input, three strategies:
        // the arc must place tracks in leg order rather than by one global ranking.
        var affinities = Affinities(
            (50, 10, 0.70), (50, 20, 0.68),
            (60, 20, 0.90), (60, 30, 0.88));

        var request = Request(
            seeds: new long[] { 10, 20, 30 },
            candidates: new long[] { 10, 20, 30, 50, 60 },
            trackCount: 5,
            affinities: affinities);

        var journey = _strategy.BuildPlaylist(request).Candidates.Select(candidate => candidate.TrackId).ToArray();
        var anchor = new AnchorDjStrategy().BuildPlaylist(request).Candidates.Select(candidate => candidate.TrackId).ToArray();

        Assert.Equal(new long[] { 10, 20, 30, 50, 60 }, journey);
        Assert.NotEqual(anchor, journey);
    }

    // ----------------------------------------------------------------- helpers

    private static DjStrategyRequest Request(
        IReadOnlyList<long> seeds,
        IReadOnlyList<long> candidates,
        int trackCount = 10,
        double coverage = 100d,
        IReadOnlyList<DjSeed>? weightedSeeds = null,
        IReadOnlyDictionary<long, IReadOnlyDictionary<long, double>>? affinities = null)
        => new()
        {
            Definition = new DjDefinitionDto
            {
                DjDefinitionId = "journey-test",
                Name = "Journey Test",
                Strategy = JourneyDjStrategy.StrategyId,
                MinTracks = 1,
                MaxTracks = 60,
            },
            Seeds = weightedSeeds
                ?? seeds.Select(seedId => new DjSeed(seedId, 1d, HasEmbedding: true)).ToList(),
            CandidateTrackIds = candidates,
            TrackCount = trackCount,
            SonicCoveragePercent = coverage,
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
}