using System;
using System.Collections.Generic;
using System.Linq;
using DeezSpoTag.Services.Library.Dj;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// The Companion strategy.
///
/// <para>Companion exists to beat one specific alternative: filling outward from the
/// seed set as a single bag, which is what Anchor does. Most of these tests are
/// therefore about the case where the two strategies must disagree — a track close to
/// one seed versus a track close to two — because if they agreed everywhere the
/// strategy would be redundant.</para>
///
/// <para>Determinism is held to the same standard as everywhere else: a re-run
/// replaces the previous playlist in place, so a reordering would replace a good
/// playlist for no reason.</para>
/// </summary>
public sealed class CompanionDjStrategyTest
{
    private readonly CompanionDjStrategy _strategy = new();

    // ------------------------------------------------------------- basic shape

    [Fact]
    public void TheStrategyIsSelectedByItsName()
    {
        Assert.Equal("companion", _strategy.Strategy);
        Assert.Equal(CompanionDjStrategy.StrategyId, _strategy.Strategy);
        Assert.NotEqual("anchor", _strategy.Strategy);
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
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("bridge", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ASeedOutsideTheCandidatePoolIsIgnored()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 1, 999 },
            candidates: new long[] { 1, 2, 3 },
            trackCount: 3,
            affinities: Affinities((2, 1, 0.8), (3, 1, 0.7))));

        Assert.Equal(3, result.Candidates.Count);
        Assert.DoesNotContain(999, result.Candidates.Select(candidate => candidate.TrackId));
    }

    [Fact]
    public void RequestingZeroTracksYieldsNothing()
    {
        var result = _strategy.BuildPlaylist(Request(seeds: new long[] { 1 }, candidates: new long[] { 1, 2 }, trackCount: 0));

        Assert.Empty(result.Candidates);
    }

    // ------------------------------------------- what makes it different from Anchor

    [Fact]
    public void ABridgingTrackOutranksATrackCloseToOnlyOneSeed()
    {
        // The case the strategy exists for. Track 30 sits between both seeds; track 40
        // is a better match to seed 10 but has nothing to do with seed 20.
        //
        // Anchor would rank 40 first, because it takes the single best affinity.
        // Companion ranks 30 first, because sitting between is the point.
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20 },
            candidates: new long[] { 10, 20, 30, 40 },
            trackCount: 4,
            affinities: Affinities(
                (30, 10, 0.80), (30, 20, 0.78),
                (40, 10, 0.95), (40, 20, 0.02))));

        Assert.Equal(new long[] { 10, 20, 30, 40 }, result.Candidates.Select(candidate => candidate.TrackId));
        Assert.True(result.Candidates[2].Similarity > result.Candidates[3].Similarity);
    }

    [Fact]
    public void ATwoSeedSetIsScoredDifferentlyFromAOneSeedSet()
    {
        // With one seed there is no gap to bridge, and the strategy must not pretend
        // otherwise by reporting a neighbour as a companion.
        var oneSeed = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10 },
            candidates: new long[] { 10, 30 },
            trackCount: 2,
            affinities: Affinities((30, 10, 0.80))));

        var companion = Assert.Single(oneSeed.Candidates, candidate => candidate.TrackId == 30);
        Assert.Equal(DjItemReasons.Companion, companion.Reason);
        Assert.Equal(0.4d, companion.Similarity);
    }

    [Fact]
    public void ASingleSeedIsReportedBecauseThereIsNothingToBridge()
    {
        // A caller who configured one seed should be told their intent could not be
        // honoured, rather than receiving a set that looks like bridging.
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10 },
            candidates: new long[] { 10, 30 },
            trackCount: 2,
            affinities: Affinities((30, 10, 0.80))));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("bridge", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TwoSeedsRaiseNoBridgingComplaint()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20 },
            candidates: new long[] { 10, 20, 30 },
            trackCount: 3,
            affinities: Affinities((30, 10, 0.7), (30, 20, 0.7))));

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Contains("bridge", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ATrackCloseToManySeedsBeatsOneCloseToFewer()
    {
        // The second half of the strategy: genuine overlap across the seed set is
        // what identifies a good companion.
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20, 30, 40 },
            candidates: new long[] { 10, 20, 30, 40, 50, 60 },
            trackCount: 6,
            affinities: Affinities(
                (50, 10, 0.60), (50, 20, 0.60),
                (60, 10, 0.62), (60, 20, 0.62), (60, 30, 0.60), (60, 40, 0.58))));

        Assert.Equal(60, result.Candidates[4].TrackId);
        Assert.True(result.Candidates[4].Similarity > result.Candidates[5].Similarity);
    }

    [Fact]
    public void ABridgingBonusIsCappedSoManySeedsCannotBeatEverything()
    {
        // Without a cap, a track close to every seed would score above one genuinely
        // bridging two of them, and "close to the sound" would become "popular".
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20, 30, 40, 50, 60, 70 },
            candidates: new long[] { 10, 20, 30, 40, 50, 60, 70, 80, 90 },
            trackCount: 9,
            affinities: Affinities(
                (80, 10, 0.40), (80, 20, 0.40), (80, 30, 0.40), (80, 40, 0.40), (80, 50, 0.40),
                (90, 10, 0.90), (90, 20, 0.05))));

        Assert.All(result.Candidates, candidate => Assert.InRange(candidate.Similarity, 0d, 1d));
    }

    // -------------------------------------------------------- seeds come first

    [Fact]
    public void SeedsArePlacedBeforeAnyCompanion()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20 },
            candidates: new long[] { 10, 20, 30, 40 },
            trackCount: 4,
            affinities: Affinities((30, 10, 0.8), (30, 20, 0.8), (40, 10, 0.9), (40, 20, 0.9))));

        Assert.Equal(DjItemReasons.Seed, result.Candidates[0].Reason);
        Assert.Equal(DjItemReasons.Seed, result.Candidates[1].Reason);
        Assert.All(result.Candidates.Skip(2), candidate => Assert.Equal(DjItemReasons.Companion, candidate.Reason));
    }

    [Fact]
    public void ASeedIsKeptEvenWhenACompanionScoresHigher()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10 },
            candidates: new long[] { 10, 30 },
            trackCount: 2,
            affinities: Affinities((30, 10, 1.0))));

        Assert.Equal(10, Assert.Single(result.Candidates, candidate => candidate.Reason == DjItemReasons.Seed).TrackId);
    }

    [Fact]
    public void HeavierSeedsLeadThePlaylist()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: Array.Empty<long>(),
            weightedSeeds: new[] { new DjSeed(20, 1d, HasEmbedding: true), new DjSeed(10, 5d, HasEmbedding: true) },
            candidates: new long[] { 10, 20, 30 },
            trackCount: 3,
            affinities: Affinities((30, 10, 0.8), (30, 20, 0.8))));

        Assert.Equal(10, result.Candidates[0].TrackId);
        Assert.Equal(20, result.Candidates[1].TrackId);
    }

    [Fact]
    public void EqualWeightSeedsAreOrderedDeterministically()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 30, 10, 20 },
            candidates: new long[] { 30, 10, 20, 40 },
            trackCount: 4,
            affinities: Affinities((40, 10, 0.8), (40, 20, 0.8), (40, 30, 0.8))));

        Assert.Equal(new long[] { 10, 20, 30 }, result.Candidates.Take(3).Select(candidate => candidate.TrackId));
    }

    [Fact]
    public void ADuplicateSeedIsNotPlacedTwice()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: Array.Empty<long>(),
            weightedSeeds: new[] { new DjSeed(10, 1d, HasEmbedding: true), new DjSeed(10, 1d, HasEmbedding: true) },
            candidates: new long[] { 10, 20 },
            trackCount: 2,
            affinities: Affinities((20, 10, 0.8))));

        Assert.Single(result.Candidates, candidate => candidate.Reason == DjItemReasons.Seed);
    }

    // -------------------------------------------------------------- selection

    [Fact]
    public void CompanionsAreOrderedByFallingBridgeScore()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20 },
            candidates: new long[] { 10, 20, 30, 40, 50 },
            trackCount: 5,
            affinities: Affinities(
                (30, 10, 0.70), (30, 20, 0.60),
                (40, 10, 0.80), (40, 20, 0.75),
                (50, 10, 0.55), (50, 20, 0.50))));

        Assert.Equal(new long[] { 10, 20, 40, 30, 50 }, result.Candidates.Select(candidate => candidate.TrackId));
    }

    [Fact]
    public void TheTrackCountIsHonoured()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20 },
            candidates: new long[] { 10, 20, 30, 40, 50, 60, 70, 80 },
            trackCount: 4,
            affinities: Affinities((30, 10, 0.8), (40, 10, 0.7), (50, 10, 0.6), (60, 10, 0.5), (70, 10, 0.4), (80, 10, 0.3))));

        Assert.Equal(4, result.Candidates.Count);
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
    public void ATrackScoredOnlyAgainstAnExcludedSeedIsNotChosen()
    {
        // Close to a seed that is not this DJ's says nothing about this DJ.
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10 },
            candidates: new long[] { 10, 20, 30 },
            trackCount: 3,
            affinities: Affinities((20, 999, 0.99), (30, 10, 0.50))));

        Assert.DoesNotContain(20, result.Candidates.Select(candidate => candidate.TrackId));
        Assert.Equal(new long[] { 10, 30 }, result.Candidates.Select(candidate => candidate.TrackId));
    }

    [Fact]
    public void AnUnusableAffinityNeverCorruptsTheOrder()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20 },
            candidates: new long[] { 10, 20, 30, 40 },
            trackCount: 4,
            affinities: Affinities((30, 10, double.NaN), (40, 10, 0.8), (40, 20, 0.7))));

        Assert.Equal(new long[] { 10, 20, 40 }, result.Candidates.Select(candidate => candidate.TrackId));
    }

    [Fact]
    public void ATieInBridgeScoreIsBrokenByTrackId()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20 },
            candidates: new long[] { 10, 20, 30, 40 },
            trackCount: 4,
            affinities: Affinities((30, 10, 0.7), (30, 20, 0.7), (40, 10, 0.7), (40, 20, 0.7))));

        Assert.Equal(new long[] { 10, 20, 30, 40 }, result.Candidates.Select(candidate => candidate.TrackId));
    }

    [Fact]
    public void ADuplicateCandidateIsNotChosenTwice()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10 },
            candidates: new long[] { 10, 20, 20, 30 },
            trackCount: 4,
            affinities: Affinities((20, 10, 0.8), (30, 10, 0.7))));

        Assert.Equal(3, result.Candidates.Count);
        Assert.Equal(
            result.Candidates.Count,
            result.Candidates.Select(candidate => candidate.TrackId).Distinct().Count());
    }

    [Fact]
    public void EveryChosenTrackComesFromTheCandidatePool()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10 },
            candidates: new long[] { 10, 20, 30 },
            trackCount: 5,
            affinities: Affinities((20, 10, 0.8), (999, 10, 0.99))));

        var pool = new HashSet<long> { 10, 20, 30 };
        Assert.All(result.Candidates, candidate => Assert.Contains(candidate.TrackId, pool));
    }

    [Fact]
    public void ANegativeTrackCountIsTreatedAsNone()
    {
        var result = _strategy.BuildPlaylist(Request(seeds: new long[] { 10 }, candidates: new long[] { 10, 20 }, trackCount: -5));

        Assert.Empty(result.Candidates);
    }

    // -------------------------------------------------------- no sonic at all

    [Fact]
    public void WithNoAffinitiesTheDjStillPlaysItsSeeds()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20 },
            candidates: new long[] { 10, 20, 30, 40 },
            trackCount: 4,
            affinities: null));

        Assert.Equal(new long[] { 10, 20 }, result.Candidates.Select(candidate => candidate.TrackId));
    }

    [Fact]
    public void LowCoverageIsReportedBecauseTheResultIsWorthLessThanItLooks()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20 },
            candidates: new long[] { 10, 20, 30 },
            trackCount: 3,
            coverage: 8.2,
            affinities: Affinities((30, 10, 0.8), (30, 20, 0.7))));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("8.2", StringComparison.Ordinal));
    }

    [Fact]
    public void HealthyCoverageIsNotReportedAsAProblem()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20 },
            candidates: new long[] { 10, 20, 30 },
            trackCount: 3,
            coverage: 91.3,
            affinities: Affinities((30, 10, 0.8), (30, 20, 0.7))));

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ASuccessfulRunReportsWhatItsSeedsWere()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20 },
            candidates: new long[] { 10, 20, 30 },
            trackCount: 3,
            affinities: Affinities((30, 10, 0.8), (30, 20, 0.7))));

        Assert.NotNull(result.SeedSummary);
        Assert.Contains("2", result.SeedSummary!, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ determinism

    [Fact]
    public void TheSameRequestProducesTheSamePlaylist()
    {
        var request = Request(
            seeds: new long[] { 10, 20 },
            candidates: new long[] { 10, 20, 30, 40, 50, 60 },
            trackCount: 5,
            affinities: Affinities(
                (30, 10, 0.81), (30, 20, 0.62),
                (40, 10, 0.44), (40, 20, 0.88),
                (50, 10, 0.79), (50, 20, 0.31),
                (60, 10, 0.55), (60, 20, 0.55)));

        var runs = Enumerable.Range(0, 25)
            .Select(_ => _strategy.BuildPlaylist(request))
            .ToList();

        var expected = runs[0].Candidates.Select(candidate => candidate.TrackId).ToArray();
        Assert.All(runs, run =>
            Assert.Equal(expected, run.Candidates.Select(candidate => candidate.TrackId)));
    }

    [Fact]
    public void ReorderingTheInputDoesNotReorderThePlaylist()
    {
        var affinities = Affinities((30, 10, 0.7), (30, 20, 0.7), (40, 10, 0.6), (40, 20, 0.6));

        var forwards = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20 },
            candidates: new long[] { 10, 20, 30, 40 },
            trackCount: 4,
            affinities: affinities));

        var backwards = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 20, 10 },
            candidates: new long[] { 40, 30, 20, 10 },
            trackCount: 4,
            affinities: affinities));

        Assert.Equal(
            forwards.Candidates.Select(candidate => candidate.TrackId),
            backwards.Candidates.Select(candidate => candidate.TrackId));
    }

    [Fact]
    public void CompanionDisagreesWithAnchorWhereItIsMeantTo()
    {
        // The strategy earns its place only if it ranks differently from Anchor on the
        // case it was written for. If the two ever agreed here, Companion would be a
        // rename rather than a strategy.
        var affinities = Affinities(
            (30, 10, 0.80), (30, 20, 0.78),
            (40, 10, 0.95), (40, 20, 0.02));

        var anchor = new AnchorDjStrategy().BuildPlaylist(new DjStrategyRequest
        {
            Definition = new DjDefinitionDto { DjDefinitionId = "x", Name = "X", Strategy = "anchor" },
            Seeds = new[] { new DjSeed(10, 1d, HasEmbedding: true), new DjSeed(20, 1d, HasEmbedding: true) },
            CandidateTrackIds = new long[] { 10, 20, 30, 40 },
            TrackCount = 4,
            SonicCoveragePercent = 100d,
            OccurrenceKey = "2026-09-30",
            SeedAffinities = affinities,
        });

        var companion = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20 },
            candidates: new long[] { 10, 20, 30, 40 },
            trackCount: 4,
            affinities: affinities));

        Assert.NotEqual(
            anchor.Candidates.Select(candidate => candidate.TrackId),
            companion.Candidates.Select(candidate => candidate.TrackId));
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
                DjDefinitionId = "companion-test",
                Name = "Companion Test",
                Strategy = CompanionDjStrategy.StrategyId,
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