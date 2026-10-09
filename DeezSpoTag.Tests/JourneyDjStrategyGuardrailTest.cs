using System;
using System.IO;
using System.Linq;
using DeezSpoTag.Services.Library.Dj;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Structural guardrails for the Journey strategy.
///
/// <para>Behavioural tests prove Journey orders the way it should. These prove the
/// things that would still be wrong if every one of those passed — a dependency
/// smuggled in beside a correct algorithm, or Journey quietly collapsing into a
/// ranked set with an arc-shaped variable name.</para>
///
/// <para>The arc tests at the end matter most. Nothing in Journey's algorithm stops a
/// future edit from emitting one globally-ranked list, which would look correct in
/// every existing test and be indistinguishable from Anchor.</para>
/// </summary>
public sealed class JourneyDjStrategyGuardrailTest
{
    [Fact]
    public void JourneyIsRegisteredByTheNameStoredOnTheDefinition()
    {
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "JourneyDjStrategy.cs");

        Assert.Contains("public const string StrategyId = \"journey\"", strategy, StringComparison.Ordinal);
        Assert.Contains("public string Strategy => StrategyId;", strategy, StringComparison.Ordinal);
        Assert.Contains("public sealed class JourneyDjStrategy : IMelodayDjStrategy", strategy, StringComparison.Ordinal);
    }

    [Fact]
    public void JourneyTakesOnlyItsRequestAndHasNoConstructorDependencies()
    {
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "JourneyDjStrategy.cs");

        Assert.Contains(
            "public DjStrategyResult BuildPlaylist(DjStrategyRequest request)",
            strategy,
            StringComparison.Ordinal);
        Assert.DoesNotContain("public JourneyDjStrategy(", strategy, StringComparison.Ordinal);
    }

    [Fact]
    public void JourneyReadsNoClockAndNoRandomSource()
    {
        // Both would break the property a person relies on: the same seeds producing
        // the same arc. A clock makes it age; randomness makes it luck.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "JourneyDjStrategy.cs");

        foreach (var forbidden in new[]
        {
            "DateTime",
            "DateTimeOffset",
            "DateOnly",
            "Random",
            "Stopwatch",
            "Environment.TickCount",
            "DateTime.UtcNow",
        })
        {
            Assert.DoesNotContain(forbidden, strategy, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void JourneyTouchesNoDatabaseAndRunsNoInference()
    {
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "JourneyDjStrategy.cs");

        foreach (var forbidden in new[]
        {
            "LibraryRepository",
            "Sqlite",
            "DbConnection",
            "dj_generation",
            "dj_seed_spec",
            "track_sonic_embedding",
            "SonicSimilarity",
            "Cosine",
        })
        {
            Assert.DoesNotContain(forbidden, strategy, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("SeedAffinities", strategy, StringComparison.Ordinal);
    }

    [Fact]
    public void JourneyKeepsAffinitiesOnTheRequestRatherThanAmbientState()
    {
        // Ambient state would make behaviour depend on something the caller did not
        // pass, and would break the moment two builds ran concurrently.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "JourneyDjStrategy.cs");

        foreach (var forbidden in new[] { "AsyncLocal", "ThreadStatic", "[ThreadStatic]", "Current.Value" })
        {
            Assert.DoesNotContain(forbidden, strategy, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void JourneyStaysInsideTheDomainNamespace()
    {
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "JourneyDjStrategy.cs");

        Assert.Contains("namespace DeezSpoTag.Services.Library.Dj;", strategy, StringComparison.Ordinal);
        Assert.DoesNotContain("DeezSpoTag.Web", strategy, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.AspNetCore", strategy, StringComparison.Ordinal);
    }

    [Fact]
    public void JourneyDoesNotChangeGenreStyleOrMood()
    {
        // Sonic is a separate axis from the semantic analysis. A DJ must never edit
        // library metadata through what is meant to be a playlist.
        var code = StripComments(Read("DeezSpoTag.Services", "Library", "Dj", "JourneyDjStrategy.cs"));

        foreach (var forbidden in new[] { "VibeSemantic", "Genre", "Mood", "Style", "Tag", "Evidence", "Analysis" })
        {
            Assert.False(
                System.Text.RegularExpressions.Regex.IsMatch(code, $@"\b{forbidden}\b"),
                $"Journey references '{forbidden}', which would mean it reaches the semantic analysis.");
        }
    }

    [Fact]
    public void EverySortIsTotalSoOrderingIsReproducible()
    {
        // LINQ's OrderBy is stable, so equal keys keep input order. Where that input
        // came from a HashSet the order shifts between runs, so every ordering needs
        // a final tie-break on a unique value.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "JourneyDjStrategy.cs");
        var orderings = strategy
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith(".OrderBy", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(orderings);
        foreach (var ordering in orderings)
        {
            var block = strategy.Substring(strategy.IndexOf(ordering, StringComparison.Ordinal), 300);
            Assert.Contains(".ThenBy", block);
        }
    }

    [Fact]
    public void JourneyEmitsOnlyTheReasonsItCanJustify()
    {
        // A landmark is a seed and everything else is a point on the arc. Claiming
        // "neighbour" would report Anchor's intent and "companion" Companion's.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "JourneyDjStrategy.cs");

        Assert.Contains("DjItemReasons.Seed", strategy, StringComparison.Ordinal);
        Assert.Contains("DjItemReasons.Journey", strategy, StringComparison.Ordinal);
        Assert.DoesNotContain("DjItemReasons.Neighbour", strategy, StringComparison.Ordinal);
        Assert.DoesNotContain("DjItemReasons.Companion", strategy, StringComparison.Ordinal);
    }

    [Fact]
    public void JourneyActuallyBuildsLegsBetweenConsecutiveLandmarks()
    {
        // The mechanism the whole strategy rests on. If legs were built any other way
        // — all pairs, or seeds against every seed — the arc would no longer be a
        // path through the landmarks in order.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "JourneyDjStrategy.cs");
        var buildLegs = ExtractRegion(strategy, "private static IReadOnlyList<Leg> BuildLegs", "private static IReadOnlyList<List<LegCandidate>> AssignLegs");

        Assert.Contains("for (var index = 0; index + 1 < landmarks.Count; index++)", buildLegs, StringComparison.Ordinal);
        Assert.Contains("new Leg(landmarks[index], landmarks[index + 1])", buildLegs, StringComparison.Ordinal);
    }

    [Fact]
    public void ALegRequiresBothOfItsEndsToBeReached()
    {
        // A leg has two ends. Scoring on one alone is neighbourhood, which is
        // Anchor's job, and would fill the arc with tracks belonging nowhere in it.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "JourneyDjStrategy.cs");
        var legScore = ExtractRegion(strategy, "private static double LegScore", "private static bool TryAffinity");

        Assert.Contains("leg.FromLandmark", legScore, StringComparison.Ordinal);
        Assert.Contains("leg.ToLandmark", legScore, StringComparison.Ordinal);
    }

    [Fact]
    public void JourneyFloorsHowCloseATrackMustBeToBothEndsOfALeg()
    {
        // Without a floor, a track at 0.95 to one landmark and 0.05 to the other still
        // averages to a respectable 0.5 and gets placed mid-arc, where it audibly
        // belongs to neither end.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "JourneyDjStrategy.cs");
        var legScore = ExtractRegion(strategy, "private static double LegScore", "private static bool TryAffinity");

        Assert.Contains("MinimumLegAffinity", legScore, StringComparison.Ordinal);
        Assert.Contains("public const double MinimumLegAffinity", strategy, StringComparison.Ordinal);
    }

    [Fact]
    public void JourneyPlacesTracksInLegOrderRatherThanOneGlobalRanking()
    {
        // The property that makes Journey different from Anchor. Legs are emitted in
        // index order, so a track assigned to a later leg cannot precede one assigned
        // to an earlier leg.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "JourneyDjStrategy.cs");

        Assert.Contains("for (var legIndex = 0; legIndex < legs.Count; legIndex++)", strategy, StringComparison.Ordinal);

        // The emission loop walks legs by index and sorts only *within* one leg. A
        // per-leg sort is correct — the best track for a position leads it — so the
        // guard is that the sort is scoped to assignments[legIndex] and never spans
        // legs. A sort over all assignments at once would flatten the arc into a
        // single ranking and make Journey identical to Anchor.
        var emitLoop = ExtractRegion(
            strategy,
            "var placedAny = false;",
            "if (legs.Count == 1)");

        Assert.Contains("for (var legIndex", emitLoop, StringComparison.Ordinal);
        Assert.Contains("var chosen = assignments[legIndex]", emitLoop, StringComparison.Ordinal);
        Assert.Contains(".OrderByDescending(entry => entry.Similarity)", emitLoop, StringComparison.Ordinal);

        // Exactly one sort, and it is inside the per-leg loop.
        Assert.Equal(
            1,
            emitLoop.Split(".OrderByDescending").Length - 1);
    }

    [Fact]
    public void TheLegsAreEmittedInArcOrderRatherThanByScore()
    {
        // The specific failure: sorting all assignments together before emitting. Every
        // existing behavioural test would still pass, because the tracks chosen would
        // be identical — only their positions would differ.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "JourneyDjStrategy.cs");
        var emitLoop = ExtractRegion(
            strategy,
            "var placedAny = false;",
            "if (legs.Count == 1)");

        // Nothing may be sorted or chosen from outside the current leg.
        Assert.DoesNotContain("assignments", emitLoop.Replace("assignments[legIndex]", string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public void JourneyGivesEachLegItsOwnBudgetRatherThanFirstCome()
    {
        // A fixed budget per leg would let a rich opening starve the ending, and the
        // arc would describe only its first section.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "JourneyDjStrategy.cs");

        Assert.Contains("EvenBudget", strategy, StringComparison.Ordinal);
        Assert.Contains("var quotas = EvenBudget(budget, assignments)", strategy, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLegBudgetIsSplitProportionallyAndDeterministically()
    {
        // Proportional by what each leg can supply, remainder largest-share-first, ties
        // by leg index so the split never varies between runs.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "JourneyDjStrategy.cs");
        var budget = ExtractRegion(strategy, "private static int[] EvenBudget", "/// <summary>\n    /// Single-landmark case");

        Assert.Contains("budget * (double)assignments[index].Count / total", budget, StringComparison.Ordinal);
        Assert.Contains("OrderByDescending(entry => entry.Fraction)", budget, StringComparison.Ordinal);
        Assert.Contains("ThenBy(entry => entry.Leg)", budget, StringComparison.Ordinal);
    }

    [Fact]
    public void ALegCanBeProportionalButStillGetTheWholeRemainder()
    {
        // An even split with a leftover slot would either drop a track the arc could
        // have used or give a leg more than it can fill. The remainder is distributed
        // rather than discarded.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "JourneyDjStrategy.cs");
        var budget = ExtractRegion(strategy, "private static int[] EvenBudget", "/// <summary>\n    /// Single-landmark case");

        Assert.Contains("var spare = budget - assigned;", budget, StringComparison.Ordinal);
        Assert.DoesNotContain("return quotas;\n    }\n\n    /// <summary>\n    /// Single", budget.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void JourneyRefusesToInventABridgeForAnUnscoredTrack()
    {
        // A candidate absent from the table was never measured. Choosing it would put a
        // track in the arc on a guess, wearing the same confidence as a measured one.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "JourneyDjStrategy.cs");
        var assign = ExtractRegion(strategy, "private static IReadOnlyList<List<LegCandidate>> AssignLegs", "private static double LegScore");

        Assert.Contains("if (!affinities.TryGetValue(trackId, out var perSeed)", assign, StringComparison.Ordinal);
        Assert.Contains("continue;", assign, StringComparison.Ordinal);
    }

    [Fact]
    public void JourneyBoundsEveryScoreItReportsToAUnitRange()
    {
        // Scores are stored as a REAL and read back as a similarity, so an unbounded
        // value would make stored provenance meaningless.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "JourneyDjStrategy.cs");

        Assert.Contains("Math.Clamp(value, 0d, 1d)", strategy, StringComparison.Ordinal);
        Assert.Contains("Math.Clamp(mean + (balance * 0.1d), 0d, 1d)", strategy, StringComparison.Ordinal);
    }

    [Fact]
    public void JourneyDoesNotPlaceTheSameTrackOnTwoLegs()
    {
        // A track that could serve two legs must fill only the better one, or the
        // playlist would contain it twice.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "JourneyDjStrategy.cs");
        var assign = ExtractRegion(strategy, "private static IReadOnlyList<List<LegCandidate>> AssignLegs", "private static double LegScore");

        // Each candidate is appended to exactly one leg, chosen by its best score.
        Assert.Contains("assignments[bestLeg].Add(new LegCandidate(trackId, bestScore))", assign, StringComparison.Ordinal);
    }

    [Fact]
    public void JourneyReportsThinInputRatherThanHidingIt()
    {
        // A DJ that quietly returned a short arc, or one that never bridged, would look
        // like a considered decision.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "JourneyDjStrategy.cs");

        foreach (var token in new[]
        {
            "SonicCoveragePercent < LowCoveragePercent",
            "ordered.Count < trackCount",
            "landmarks.Count == 0",
            "candidatePool.Count == 0",
            "legs.Count == 0",
            "legs.Count == 1",
            "assignments.Where(leg => leg.Count == 0)",
        })
        {
            Assert.Contains(token, strategy, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void JourneySaysWhenItCannotActuallyTravel()
    {
        // With one landmark there is no arc, and with two there is only a single
        // section. Reporting either as a journey would misrepresent what it built.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "JourneyDjStrategy.cs");

        Assert.Contains("there is no arc to follow", strategy, StringComparison.Ordinal);
        Assert.Contains("rather than a journey", strategy, StringComparison.Ordinal);
    }

    [Fact]
    public void JourneyPlacesLandmarksBeforeAnythingElse()
    {
        // The arc's structure is fixed before any track is considered for a position.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "JourneyDjStrategy.cs");

        var landmarkPlacement = strategy.IndexOf("DjItemReasons.Seed)", StringComparison.Ordinal);
        var legEmission = strategy.IndexOf("var placedAny = false;", StringComparison.Ordinal);

        Assert.True(landmarkPlacement > 0, "Landmark placement was not found.");
        Assert.True(legEmission > 0, "Leg emission was not found.");
        Assert.True(
            landmarkPlacement < legEmission,
            "Journey fills legs before placing landmarks, so the arc's structure can be displaced.");
    }

    [Fact]
    public void JourneyKeepsNoMutableStaticState()
    {
        var code = StripComments(Read("DeezSpoTag.Services", "Library", "Dj", "JourneyDjStrategy.cs"));

        foreach (var line in code.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.Contains(" static ", StringComparison.Ordinal)
                || trimmed.Contains(" const ", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.True(
                trimmed.Contains('('),
                $"Journey appears to declare mutable static state: {trimmed}");
        }
    }

    [Fact]
    public void AllThreeStrategiesLiveBesideTheContract()
    {
        // A fourth must not mean a second location for strategy code. Asserts the
        // three named implementations exist; adding a fourth does not break this.
        var files = Directory
            .GetFiles(RepoPath("DeezSpoTag.Services", "Library", "Dj"), "*.cs")
            .Select(Path.GetFileName)
            .ToArray();

        Assert.Contains("AnchorDjStrategy.cs", files);
        Assert.Contains("CompanionDjStrategy.cs", files);
        Assert.Contains("JourneyDjStrategy.cs", files);
        Assert.Contains("IMelodayDjStrategy.cs", files);
    }

    [Fact]
    public void EveryStrategyNameInTheDomainIsDistinct()
    {
        // One name per implementation, or a definition could silently resolve to the
        // wrong strategy. No count is asserted: the rule is distinctness, and pinning
        // a count makes every new strategy fail a test about names.
        var declared = CompanionDjStrategyGuardrailTest.StrategyIdsInDomain()
            .Where(name => name is not null)
            .ToArray();

        Assert.NotEmpty(declared);
        Assert.Equal(declared.Length, declared.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryItemReasonIsUsedByAtLeastOneStrategy()
    {
        // Each reason exists because a strategy emits it. An unused one means a
        // strategy was planned and never written, or a reason was invented for a
        // strategy that reports something else.
        var djDirectory = RepoPath("DeezSpoTag.Services", "Library", "Dj");
        var sources = string.Join(
            "\n",
            Directory.GetFiles(djDirectory, "*.cs").Select(File.ReadAllText));

        foreach (var reason in new[] { "Seed", "Neighbour", "Companion", "Journey" })
        {
            Assert.Contains(
                $"DjItemReasons.{reason}",
                sources,
                StringComparison.Ordinal);
        }
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>
    /// Source with comment-only lines removed, so a doc comment that mentions a term
    /// in order to disclaim it is not read as a violation.
    /// </summary>
    private static string StripComments(string source)
        => string.Join(
            "\n",
            source
                .Split('\n')
                .Where(line => !line.TrimStart().StartsWith("///", StringComparison.Ordinal)
                    && !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static string ExtractRegion(string text, string startMarker, string endMarker)
    {
        var start = text.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{startMarker}' was not found.");
        var end = text.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"'{endMarker}' was not found after '{startMarker}'.");
        return text[start..end];
    }

    private static string Read(params string[] parts) => File.ReadAllText(RepoPath(parts));

    private static string RepoPath(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Join(directory.FullName, "DeezSpoTag.Web")))
        {
            directory = directory.Parent;
        }

        var path = directory?.FullName
            ?? throw new DirectoryNotFoundException("Repository root not found.");
        foreach (var part in parts)
        {
            path = Path.Join(path, part);
        }

        return path;
    }
}