using System;
using System.Collections.Generic;
using System.Linq;
using DeezSpoTag.Services.Library.Dj;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// The Anchor strategy.
///
/// <para>Anchor is the strategy a person can predict: the same seeds always produce
/// the same playlist. That determinism is the property most worth protecting, and
/// also the easiest to lose by accident — one <c>Random</c>, one dictionary iteration
/// order, or one unstable sort tie-break and a re-run quietly replaces a good playlist
/// with a different one.</para>
///
/// <para>The second property is that seeds are never traded away. The seeds are the
/// DJ's identity, so a seed that fits within the track count appears, even if a
/// non-seed happens to score marginally closer.</para>
/// </summary>
public sealed class AnchorDjStrategyTest
{
    private readonly AnchorDjStrategy _strategy = new();

    // ------------------------------------------------------------- basic shape

    [Fact]
    public void TheStrategyIsSelectedByItsName()
    {
        Assert.Equal("anchor", _strategy.Strategy);
        Assert.Equal(AnchorDjStrategy.StrategyId, _strategy.Strategy);
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
        // The important failure this prevents: a DJ asked for seeds that are absent
        // from the library must not be handed the best-scoring tracks anyway, which
        // would look like a considered set with no relationship to the DJ.
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 999 },
            candidates: new long[] { 1, 2, 3 }));

        Assert.Empty(result.Candidates);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("anchor", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ASeedOutsideTheCandidatePoolIsIgnored()
    {
        // The library scope is the caller's decision; a strategy must not widen it.
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
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 1 },
            candidates: new long[] { 1, 2, 3 },
            trackCount: 0));

        Assert.Empty(result.Candidates);
        Assert.NotEmpty(result.Diagnostics);
    }

    // ---------------------------------------------------------- seeds come first

    [Fact]
    public void SeedsArePlacedBeforeAnyNeighbour()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10 },
            candidates: new long[] { 10, 20, 30, 40 },
            trackCount: 3,
            affinities: Affinities((20, 10, 0.99), (30, 10, 0.95), (40, 10, 0.90))));

        Assert.Equal(DjItemReasons.Seed, result.Candidates[0].Reason);
        Assert.Equal(10, result.Candidates[0].TrackId);
        Assert.All(result.Candidates.Skip(1), candidate =>
            Assert.Equal(DjItemReasons.Neighbour, candidate.Reason));
    }

    [Fact]
    public void ASeedIsKeptEvenWhenANonSeedScoresHigher()
    {
        // The property that makes Anchor predictable. Track 2 is the closest thing in
        // the library to the seed, but dropping the seed would leave the playlist
        // without the DJ's defining track.
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10 },
            candidates: new long[] { 10, 2 },
            trackCount: 2,
            affinities: Affinities((2, 10, 1.0))));

        var seed = Assert.Single(result.Candidates, candidate => candidate.Reason == DjItemReasons.Seed);
        Assert.Equal(10, seed.TrackId);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public void ASimilarityOfOneIsRecordedForASeedBecauseItIsItself()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10 },
            candidates: new long[] { 10, 20 },
            trackCount: 2,
            affinities: Affinities((20, 10, 0.5))));

        Assert.Equal(1d, result.Candidates[0].Similarity);
    }

    [Fact]
    public void HeavierSeedsLeadThePlaylist()
    {
        // A weighted seed is one a person deliberately emphasised, so it should open
        // the set. Normalising means adding a light seed does not demote the heavy one
        // by inflating the total.
        var result = _strategy.BuildPlaylist(Request(
            seeds: Array.Empty<long>(),
            weightedSeeds: new[]
            {
                new DjSeed(10, 5d, HasEmbedding: true),
                new DjSeed(20, 1d, HasEmbedding: true),
            },
            candidates: new long[] { 10, 20, 30 },
            trackCount: 3));

        Assert.Equal(10, result.Candidates[0].TrackId);
        Assert.Equal(20, result.Candidates[1].TrackId);
    }

    [Fact]
    public void AddingALightSeedDoesNotDemoteAHeavyOne()
    {
        // Normalisation, not summation: with summed weights every seed's score would
        // grow with the seed count and the ordering would shift whenever somebody
        // added a seed to the definition.
        var heavyAlone = _strategy.BuildPlaylist(Request(
            seeds: Array.Empty<long>(),
            weightedSeeds: new[] { new DjSeed(10, 5d, HasEmbedding: true), new DjSeed(20, 1d, HasEmbedding: true) },
            candidates: new long[] { 10, 20, 30 },
            trackCount: 3));

        var heavyPlusOne = _strategy.BuildPlaylist(Request(
            seeds: Array.Empty<long>(),
            weightedSeeds: new[]
            {
                new DjSeed(10, 5d, HasEmbedding: true),
                new DjSeed(20, 1d, HasEmbedding: true),
                new DjSeed(30, 0.1d, HasEmbedding: true),
            },
            candidates: new long[] { 10, 20, 30, 40 },
            trackCount: 4));

        Assert.Equal(
            heavyAlone.Candidates.Take(2).Select(candidate => candidate.TrackId),
            heavyPlusOne.Candidates.Take(2).Select(candidate => candidate.TrackId));
    }

    [Fact]
    public void EqualWeightSeedsAreOrderedDeterministically()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 30, 10, 20 },
            candidates: new long[] { 30, 10, 20, 40 },
            trackCount: 4));

        // Tie broken by track id, not by whatever order the caller listed the seeds in.
        Assert.Equal(new long[] { 10, 20, 30 }, result.Candidates.Take(3).Select(candidate => candidate.TrackId));
    }

    // ------------------------------------------------------------- neighbours

    [Fact]
    public void NeighboursAreOrderedByFallingSimilarity()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10 },
            candidates: new long[] { 10, 20, 30, 40 },
            trackCount: 4,
            affinities: Affinities((20, 10, 0.60), (30, 10, 0.90), (40, 10, 0.75))));

        Assert.Equal(new long[] { 10, 30, 40, 20 }, result.Candidates.Select(candidate => candidate.TrackId));
    }

    [Fact]
    public void TheTrackCountIsHonoured()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10 },
            candidates: new long[] { 10, 20, 30, 40, 50, 60 },
            trackCount: 3,
            affinities: Affinities(
                (20, 10, 0.9), (30, 10, 0.8), (40, 10, 0.7), (50, 10, 0.6), (60, 10, 0.5))));

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
        // Absent from the affinity table means never measured. Choosing it would put a
        // track in the playlist on a guess, presented with the same confidence as a
        // measured one.
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10 },
            candidates: new long[] { 10, 20, 999 },
            trackCount: 3,
            affinities: Affinities((20, 10, 0.8))));

        Assert.DoesNotContain(999, result.Candidates.Select(candidate => candidate.TrackId));
    }

    [Fact]
    public void ATrackScoredAgainstNothingUsableIsNotChosen()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10 },
            candidates: new long[] { 10, 20, 30 },
            trackCount: 3,
            affinities: Affinities((20, 10, double.NaN), (30, 10, -1d))));

        Assert.Equal(10, Assert.Single(result.Candidates).TrackId);
    }

    [Fact]
    public void AnUnusableAffinityNeverCorruptsTheOrder()
    {
        // NaN compares false against everything, so one poisoned value would
        // silently reorder the playlist rather than fail.
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10 },
            candidates: new long[] { 10, 20, 30 },
            trackCount: 3,
            affinities: Affinities((20, 10, double.NaN), (30, 10, 0.5))));

        Assert.Equal(new long[] { 10, 30 }, result.Candidates.Select(candidate => candidate.TrackId));
    }

    [Fact]
    public void CorroborationBreaksAJarAllSeedsToward()
    {
        // The failure this prevents: with only "closest single seed" as the score, a
        // library where every track resembles seed 10 collapses to near-identical
        // tracks and "close to the sound" quietly becomes "similar to track one".
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20 },
            candidates: new long[] { 10, 20, 30, 40 },
            trackCount: 4,
            affinities: Affinities(
                (30, 10, 0.80), (30, 20, 0.75),
                (40, 10, 0.82), (40, 20, 0.10))));

        Assert.Equal(30, result.Candidates[2].TrackId);
        Assert.Equal(40, result.Candidates[3].TrackId);
        Assert.True(result.Candidates[2].Similarity > result.Candidates[3].Similarity);
    }

    [Fact]
    public void CorroborationCannotPushAScoreAboveOne()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20 },
            candidates: new long[] { 10, 20, 30 },
            trackCount: 3,
            affinities: Affinities((30, 10, 1.0), (30, 20, 1.0))));

        Assert.All(result.Candidates, candidate =>
            Assert.InRange(candidate.Similarity, 0d, 1d));
    }

    [Fact]
    public void ASimilarityOnlyAgainstAnExcludedSeedIsIgnored()
    {
        // A track may be close to a seed that is not in this DJ's set. That says
        // nothing about this DJ, so it must not earn a place.
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10 },
            candidates: new long[] { 10, 20, 30 },
            trackCount: 3,
            affinities: Affinities((20, 999, 0.99), (30, 10, 0.50))));

        Assert.DoesNotContain(20, result.Candidates.Select(candidate => candidate.TrackId));
        Assert.Equal(new long[] { 10, 30 }, result.Candidates.Select(candidate => candidate.TrackId));
    }

    [Fact]
    public void ATieInSimilarityIsBrokenByTrackId()
    {
        var first = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10 },
            candidates: new long[] { 10, 30, 20 },
            trackCount: 3,
            affinities: Affinities((20, 10, 0.7), (30, 10, 0.7))));

        Assert.Equal(new long[] { 10, 20, 30 }, first.Candidates.Select(candidate => candidate.TrackId));
    }

    // -------------------------------------------------------- no sonic at all

    [Fact]
    public void WithNoAffinitiesTheDjStillPlaysItsSeeds()
    {
        // Sonic off is a supported state, not an error: the DJ is still defined, it
        // just cannot fill beyond its seeds.
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10, 20 },
            candidates: new long[] { 10, 20, 30, 40 },
            trackCount: 4,
            affinities: null));

        Assert.Equal(new long[] { 10, 20 }, result.Candidates.Select(candidate => candidate.TrackId));
        Assert.All(result.Candidates, candidate => Assert.Equal(DjItemReasons.Seed, candidate.Reason));
    }

    [Fact]
    public void LowCoverageIsReportedBecauseTheResultIsWorthLessThanItLooks()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10 },
            candidates: new long[] { 10, 20, 30 },
            trackCount: 3,
            coverage: 12.5,
            affinities: Affinities((20, 10, 0.8), (30, 10, 0.7))));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("12.5", StringComparison.Ordinal));
    }

    [Fact]
    public void HealthyCoverageIsNotReportedAsAProblem()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10 },
            candidates: new long[] { 10, 20, 30 },
            trackCount: 3,
            coverage: 96.4,
            affinities: Affinities((20, 10, 0.8), (30, 10, 0.7))));

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
            seeds: Array.Empty<long>(),
            weightedSeeds: new[] { new DjSeed(10, 2d, HasEmbedding: true), new DjSeed(20, 1d, HasEmbedding: true) },
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

        // A re-run replaces the previous playlist. If the strategy were not
        // deterministic, that would replace a good one with a different one for no
        // reason, which a person experiences as the DJ being unreliable.
        var expected = runs[0].Candidates.Select(candidate => candidate.TrackId).ToArray();
        Assert.All(runs, run =>
            Assert.Equal(expected, run.Candidates.Select(candidate => candidate.TrackId)));
    }

    [Fact]
    public void ReorderingTheInputDoesNotReorderThePlaylist()
    {
        // The candidate list and the seed list arrive in whatever order the caller
        // built them. If that leaked into the result, the same DJ would play
        // differently depending on an unrelated query's ordering.
        var affinities = Affinities((30, 10, 0.7), (40, 10, 0.7), (50, 10, 0.7));

        var forwards = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10 },
            candidates: new long[] { 10, 30, 40, 50 },
            trackCount: 4,
            affinities: affinities));

        var backwards = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10 },
            candidates: new long[] { 50, 40, 30, 10 },
            trackCount: 4,
            affinities: affinities));

        Assert.Equal(
            forwards.Candidates.Select(candidate => candidate.TrackId),
            backwards.Candidates.Select(candidate => candidate.TrackId));
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
    public void ADuplicateSeedIsNotPlacedTwice()
    {
        // A definition reloaded twice could list a seed twice. Placed twice it would
        // occupy two slots of a short playlist with the same track.
        var result = _strategy.BuildPlaylist(Request(
            seeds: Array.Empty<long>(),
            weightedSeeds: new[]
            {
                new DjSeed(10, 1d, HasEmbedding: true),
                new DjSeed(10, 1d, HasEmbedding: true),
            },
            candidates: new long[] { 10, 20 },
            trackCount: 2,
            affinities: Affinities((20, 10, 0.8))));

        Assert.Single(result.Candidates, candidate => candidate.Reason == DjItemReasons.Seed);
    }

    [Fact]
    public void ANegativeTrackCountIsTreatedAsNone()
    {
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10 },
            candidates: new long[] { 10, 20 },
            trackCount: -5));

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void EveryChosenTrackComesFromTheCandidatePool()
    {
        // The library scope must hold: a DJ may never reach outside it.
        var result = _strategy.BuildPlaylist(Request(
            seeds: new long[] { 10 },
            candidates: new long[] { 10, 20, 30 },
            trackCount: 5,
            affinities: Affinities((20, 10, 0.8), (30, 10, 0.9), (999, 10, 0.99))));

        var pool = new HashSet<long> { 10, 20, 30 };
        Assert.All(result.Candidates, candidate => Assert.Contains(candidate.TrackId, pool));
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
                DjDefinitionId = "anchor-test",
                Name = "Anchor Test",
                Strategy = AnchorDjStrategy.StrategyId,
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