using System;
using System.Collections.Generic;
using System.Linq;
using DeezSpoTag.Services.Library.Dj;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// The strategy seam.
///
/// <para>There is deliberately no strategy implementation in this phase — Anchor,
/// Companion and Journey are the following ones. What this file pins down is the
/// contract those three will be written against, because a seam that is agreed once
/// and then quietly widened is how three strategies end up each carrying their own
/// half of the orchestration.</para>
///
/// <para>The rules worth protecting are the ones a strategy is most tempted to break:
/// reaching for the database, running inference, and depending on a clock or a
/// random number. Each of those would make the strategy untestable and would
/// duplicate machinery that already exists.</para>
/// </summary>
public sealed class DjStrategyContractTest
{
    [Fact]
    public void AStrategySelectsItselfByAStringStoredOnTheDefinition()
    {
        // The property that makes a new kind of DJ a data change rather than a
        // branch in the orchestration: adding a strategy must not require editing
        // anything that dispatches on it.
        var definition = new DjDefinitionDto { DjDefinitionId = "warm-up", Strategy = "anchor" };

        Assert.Equal("anchor", definition.Strategy);
    }

    [Fact]
    public void TheStrategyNameIsADedicatedStringRatherThanAnEnum()
    {
        // An enum would need a code change to add a strategy, defeating the point.
        // The string is compared against IMelodayDjStrategy.Strategy, and an
        // unregistered value is resolved to no strategy rather than throwing.
        var definition = new DjDefinitionDto { Strategy = "a-strategy-from-a-plugin" };

        Assert.Equal("a-strategy-from-a-plugin", definition.Strategy);
    }

    [Fact]
    public void AStrategyRequestCarriesEverythingAndNeedsNothingElse()
    {
        // A strategy is a pure function over this record. If it needed anything the
        // request does not carry, the seam would have to widen and the strategy would
        // stop being testable in isolation.
        var request = new DjStrategyRequest
        {
            Definition = new DjDefinitionDto { DjDefinitionId = "warm-up", Name = "Warm Up" },
            Seeds = new[] { new DjSeed(101, 1d, HasEmbedding: true) },
            CandidateTrackIds = new long[] { 101, 102, 103 },
            TrackCount = 2,
            SonicCoveragePercent = 96.4,
            OccurrenceKey = "2026-09-30",
        };

        Assert.Equal("warm-up", request.Definition.DjDefinitionId);
        Assert.Equal(new long[] { 101, 102, 103 }, request.CandidateTrackIds);
        Assert.Equal(2, request.TrackCount);
        Assert.Equal(96.4, request.SonicCoveragePercent);
        Assert.Equal("2026-09-30", request.OccurrenceKey);
    }

    [Fact]
    public void ASeedSaysWhetherItHasAnEmbedding()
    {
        // The distinction a strategy needs: a seed with no vector can still anchor
        // by tag or mood, but cannot anchor acoustically. Collapsing the two would
        // force every strategy to re-derive it.
        Assert.True(new DjSeed(101, 1d, HasEmbedding: true).HasEmbedding);
        Assert.False(new DjSeed(102, 1d, HasEmbedding: false).HasEmbedding);
    }

    [Fact]
    public void ACandidateCarriesItsSimilarityAndReason()
    {
        // Provenance is the whole point of a DJ, so the reason travels with the
        // choice rather than being reconstructed later from the score.
        var candidate = new DjCandidate(101, 0.93, DjItemReasons.Neighbour);

        Assert.Equal(101, candidate.TrackId);
        Assert.Equal(0.93, candidate.Similarity);
        Assert.Equal(DjItemReasons.Neighbour, candidate.Reason);
    }

    [Fact]
    public void AnEmptyResultCanExplainItself()
    {
        // "Produced nothing" and "did its best and produced nothing" are different
        // messages, and only the second one is useful to a person reading the UI.
        var result = DjStrategyResultFactory.Empty("No seeds could be resolved.");

        Assert.Empty(result.Candidates);
        Assert.Equal("No seeds could be resolved.", Assert.Single(result.Diagnostics));
    }

    [Fact]
    public void ASuccessfulResultHasNoDiagnostics()
    {
        // A diagnostic on a successful result is a warning, and mixing the two means
        // a caller cannot tell which it is looking at.
        var result = new DjStrategyResult
        {
            Candidates = new[] { new DjCandidate(101, 1d, DjItemReasons.Seed) },
        };

        Assert.Empty(result.Diagnostics);
        Assert.Null(result.SeedSummary);
    }

    [Fact]
    public void TheInterfaceForbidsTheThingsThatWouldMakeAStrategyUntestable()
    {
        // A strategy that took a repository, a clock or a similarity service could
        // only be tested by standing up all three, which in practice means it goes
        // untested. Pinned so widening the seam has to be deliberate.
        var contract = typeof(IMelodayDjStrategy);

        var parameters = contract
            .GetMethod(nameof(IMelodayDjStrategy.BuildPlaylist))!
            .GetParameters();

        Assert.Single(parameters);
        Assert.Equal(typeof(DjStrategyRequest), parameters[0].ParameterType);

        // The input is a single request record, not a set of collaborators.
        Assert.DoesNotContain(
            parameters,
            parameter => parameter.ParameterType != typeof(DjStrategyRequest));
    }

    [Fact]
    public void TheStrategyInterfaceDoesNotExposeTheDatabaseOrInference()
    {
        // Sonic similarity is resolved by the caller before the strategy runs, so the
        // one exact-similarity implementation stays the only one. A strategy that
        // computed its own similarity would also be impossible to test.
        var contract = typeof(IMelodayDjStrategy);
        var surface = string.Join("\n", contract.GetMembers().Select(member => member.ToString()));

        Assert.DoesNotContain("LibraryRepository", surface, StringComparison.Ordinal);
        Assert.DoesNotContain("ISonicSimilarityService", surface, StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationToken", surface, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOccurrenceKeyIsAvailableSoAStrategyCanBreakTiesDeterministically()
    {
        // Randomness in a strategy would make a re-run replace a good playlist with a
        // different one for no reason, so the tie-breaker is supplied rather than
        // reached for.
        var request = new DjStrategyRequest
        {
            Definition = new DjDefinitionDto { DjDefinitionId = "warm-up" },
            Seeds = Array.Empty<DjSeed>(),
            CandidateTrackIds = Array.Empty<long>(),
            TrackCount = 10,
            SonicCoveragePercent = 0d,
            OccurrenceKey = "2026-09-30",
        };

        Assert.False(string.IsNullOrWhiteSpace(request.OccurrenceKey));
    }

    [Fact]
    public void SonicCoverageIsReportedEvenWhenThereAreNoEmbeddings()
    {
        // Zero coverage is a fact the caller needs to state, not an absence. A
        // strategy that only learns coverage is 0 by receiving no seeds cannot tell
        // a library with no embeddings from a library with no analysis.
        var request = new DjStrategyRequest
        {
            Definition = new DjDefinitionDto { DjDefinitionId = "warm-up" },
            Seeds = Array.Empty<DjSeed>(),
            CandidateTrackIds = new long[] { 101 },
            TrackCount = 10,
            SonicCoveragePercent = 0d,
            OccurrenceKey = "2026-09-30",
        };

        Assert.Equal(0d, request.SonicCoveragePercent);
        Assert.Empty(request.Seeds);
        Assert.NotEmpty(request.CandidateTrackIds);
    }

    [Fact]
    public void TheDjTypesAreInTheServicesAssemblyNotTheWebProject()
    {
        // The domain has to be usable by the test project and by any future worker
        // without dragging in the web host. This also keeps the strategy seam from
        // depending on hosting types.
        var domainAssembly = typeof(IMelodayDjStrategy).Assembly.GetName().Name;
        var webAssembly = typeof(DeezSpoTag.Web.Services.MelodayRunStateEntry).Assembly.GetName().Name;

        Assert.Equal("DeezSpoTag.Services", domainAssembly);
        Assert.NotEqual(domainAssembly, webAssembly);
    }
}
