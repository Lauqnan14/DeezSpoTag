using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DeezSpoTag.Services.Library.Dj;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Structural guardrails for the Companion strategy.
///
/// <para>The behavioural tests prove Companion ranks the way it should. These prove
/// the things that would still be wrong if every one of those tests passed: a
/// dependency smuggled in beside a correct algorithm, or Companion quietly
/// duplicating Anchor's scoring and becoming a rename of it.</para>
///
/// <para>The last two matter most. Anchor and Companion exist to disagree, and nothing
/// in the algorithm itself would stop a future edit from making them identical while
/// leaving both sets of tests green.</para>
/// </summary>
public sealed class CompanionDjStrategyGuardrailTest
{
    [Fact]
    public void CompanionIsRegisteredByTheNameStoredOnTheDefinition()
    {
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "CompanionDjStrategy.cs");

        Assert.Contains("public const string StrategyId = \"companion\"", strategy, StringComparison.Ordinal);
        Assert.Contains("public string Strategy => StrategyId;", strategy, StringComparison.Ordinal);
        Assert.Contains("public sealed class CompanionDjStrategy : IMelodayDjStrategy", strategy, StringComparison.Ordinal);
    }

    [Fact]
    public void CompanionDoesNotCollideWithAnchorsStrategyName()
    {
        // Two strategies sharing a name would make the definition's strategy field
        // ambiguous and leave whichever registered last silently in charge.
        var companion = Read("DeezSpoTag.Services", "Library", "Dj", "CompanionDjStrategy.cs");
        var anchor = Read("DeezSpoTag.Services", "Library", "Dj", "AnchorDjStrategy.cs");

        Assert.Contains("public const string StrategyId = \"anchor\"", anchor, StringComparison.Ordinal);
        Assert.NotEqual(
            new AnchorDjStrategy().Strategy,
            new CompanionDjStrategy().Strategy);
    }

    [Fact]
    public void CompanionTakesOnlyItsRequestAndHasNoConstructorDependencies()
    {
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "CompanionDjStrategy.cs");

        Assert.Contains(
            "public DjStrategyResult BuildPlaylist(DjStrategyRequest request)",
            strategy,
            StringComparison.Ordinal);
        Assert.DoesNotContain("public CompanionDjStrategy(", strategy, StringComparison.Ordinal);
    }

    [Fact]
    public void CompanionReadsNoClockAndNoRandomSource()
    {
        // Both would break the property a person relies on: the same seeds producing
        // the same playlist. A clock makes it age; randomness makes it luck.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "CompanionDjStrategy.cs");

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
    public void CompanionTouchesNoDatabaseAndRunsNoInference()
    {
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "CompanionDjStrategy.cs");

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

        // Similarity arrives resolved on the request.
        Assert.Contains("SeedAffinities", strategy, StringComparison.Ordinal);
    }

    [Fact]
    public void CompanionKeepsAffinitiesOnTheRequestRatherThanAmbientState()
    {
        // Ambient state would make behaviour depend on something the caller did not
        // pass, and would break the moment two builds ran concurrently.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "CompanionDjStrategy.cs");

        foreach (var forbidden in new[] { "AsyncLocal", "ThreadStatic", "[ThreadStatic]", "Current.Value" })
        {
            Assert.DoesNotContain(forbidden, strategy, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CompanionStaysInsideTheDomainNamespace()
    {
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "CompanionDjStrategy.cs");

        Assert.Contains("namespace DeezSpoTag.Services.Library.Dj;", strategy, StringComparison.Ordinal);
        Assert.DoesNotContain("DeezSpoTag.Web", strategy, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.AspNetCore", strategy, StringComparison.Ordinal);
    }

    [Fact]
    public void CompanionDoesNotChangeGenreStyleOrMood()
    {
        // Sonic is a separate axis from the semantic analysis. A DJ must never edit
        // library metadata through what is meant to be a playlist.
        var code = StripComments(Read("DeezSpoTag.Services", "Library", "Dj", "CompanionDjStrategy.cs"));

        foreach (var forbidden in new[] { "VibeSemantic", "Genre", "Mood", "Style", "Tag", "Evidence", "Analysis" })
        {
            Assert.False(
                System.Text.RegularExpressions.Regex.IsMatch(code, $@"\b{forbidden}\b"),
                $"Companion references '{forbidden}', which would mean it reaches the semantic analysis.");
        }
    }

    [Fact]
    public void EverySortIsTotalSoOrderingIsReproducible()
    {
        // LINQ's OrderBy is stable, so equal keys keep input order. Where that input
        // came from a HashSet the order shifts between runs, so every ordering needs
        // a final tie-break on a unique value.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "CompanionDjStrategy.cs");
        var orderings = strategy
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith(".OrderBy", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, orderings.Count);
        foreach (var ordering in orderings)
        {
            var block = strategy.Substring(strategy.IndexOf(ordering, StringComparison.Ordinal), 260);
            Assert.Contains(".ThenBy", block);
        }
    }

    [Fact]
    public void CompanionScoresOnTwoSeedsRatherThanTheBestOneAlone()
    {
        // The difference from Anchor, stated in code. Anchor takes the single best
        // affinity; Companion takes the mean of the two strongest. Without this the
        // strategy would collapse back into Anchor under a new name.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "CompanionDjStrategy.cs");
        var scoring = ExtractRegion(strategy, "private static double BridgeScore", "private static string DescribeSeeds");

        Assert.Contains("scored[0]", scoring, StringComparison.Ordinal);
        Assert.Contains("scored[1]", scoring, StringComparison.Ordinal);
        Assert.Contains("mean", scoring, StringComparison.Ordinal);
    }

    [Fact]
    public void CompanionUsesADifferentCorroborationWeightFromAnchor()
    {
        // The two strategies must weight "close to several seeds" differently, since
        // that is the only mechanism distinguishing them beyond the mean.
        var companion = Read("DeezSpoTag.Services", "Library", "Dj", "CompanionDjStrategy.cs");
        var anchor = Read("DeezSpoTag.Services", "Library", "Dj", "AnchorDjStrategy.cs");

        Assert.Contains("public const double BridgeBonus", companion, StringComparison.Ordinal);
        Assert.Contains("public const double CorroborationBonus", anchor, StringComparison.Ordinal);
    }

    [Fact]
    public void CompanionRefusesToInventABridgeForAnUnscoredTrack()
    {
        // A candidate absent from the table was never measured. Choosing it would put
        // a track in the playlist on a guess.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "CompanionDjStrategy.cs");
        var scoring = ExtractRegion(strategy, "private static double BridgeScore", "private static string DescribeSeeds");

        Assert.Contains("if (!affinities.TryGetValue(trackId, out var perSeed)", scoring, StringComparison.Ordinal);
        Assert.Contains("return 0d;", scoring, StringComparison.Ordinal);
        Assert.Contains("double.IsFinite(affinity)", scoring, StringComparison.Ordinal);
    }

    [Fact]
    public void CompanionBoundsEveryScoreItReportsToAUnitRange()
    {
        // Scores are stored as a REAL and read back as a similarity, so an unbounded
        // value would make stored provenance meaningless.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "CompanionDjStrategy.cs");
        var scoring = ExtractRegion(strategy, "private static double BridgeScore", "private static string DescribeSeeds");

        Assert.Contains("Math.Clamp(affinity, 0d, 1d)", scoring, StringComparison.Ordinal);
        Assert.Contains("Math.Clamp(mean + bonus, 0d, 1d)", scoring, StringComparison.Ordinal);
    }

    [Fact]
    public void CompanionCapsItsBridgeBonusSoManySeedsCannotBeatEverything()
    {
        // Without a cap, a track close to every seed would outrank one genuinely
        // bridging two of them, and the strategy would degrade into "popular".
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "CompanionDjStrategy.cs");

        Assert.Contains("Math.Min(BridgeBonus * (scored.Count - 1), BridgeCap)", strategy, StringComparison.Ordinal);
    }

    [Fact]
    public void CompanionEmitsOnlyTheReasonsItCanJustify()
    {
        // Two reasons: a seed is a seed, everything else is a companion of the set.
        // Claiming "neighbour" would be reporting Anchor's intent, and "journey" an
        // arc this strategy does not model.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "CompanionDjStrategy.cs");

        Assert.Contains("DjItemReasons.Seed", strategy, StringComparison.Ordinal);
        Assert.Contains("DjItemReasons.Companion", strategy, StringComparison.Ordinal);
        Assert.DoesNotContain("DjItemReasons.Neighbour", strategy, StringComparison.Ordinal);
        Assert.DoesNotContain("DjItemReasons.Journey", strategy, StringComparison.Ordinal);
    }

    [Fact]
    public void CompanionReportsThinInputRatherThanHidingIt()
    {
        // A DJ that quietly returned three tracks for a forty-track request, or
        // bridged from a single seed, would look like a considered decision.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "CompanionDjStrategy.cs");

        foreach (var token in new[]
        {
            "SonicCoveragePercent < LowCoveragePercent",
            "ordered.Count < trackCount",
            "seeds.Count == 0",
            "candidatePool.Count == 0",
            "seeds.Count == 1",
        })
        {
            Assert.Contains(token, strategy, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CompanionSaysWhenItCannotActuallyBridge()
    {
        // With one seed there is no gap. The strategy degrades gracefully but must
        // not imply it bridged when it did not.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "CompanionDjStrategy.cs");

        Assert.Contains("nothing to bridge between", strategy, StringComparison.Ordinal);
    }

    [Fact]
    public void CompanionKeepsNoMutableStaticState()
    {
        var code = StripComments(Read("DeezSpoTag.Services", "Library", "Dj", "CompanionDjStrategy.cs"));

        foreach (var line in code.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.Contains(" static ", StringComparison.Ordinal) || trimmed.Contains(" const ", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.True(
                trimmed.Contains('('),
                $"Companion appears to declare mutable static state: {trimmed}");
        }
    }

    [Fact]
    public void BothStrategiesLiveBesideTheContract()
    {
        // Adding a third must not mean a second location for strategy code.
        var files = Directory
            .GetFiles(RepoPath("DeezSpoTag.Services", "Library", "Dj"), "*.cs")
            .Select(Path.GetFileName)
            .ToArray();

        Assert.Contains("AnchorDjStrategy.cs", files);
        Assert.Contains("CompanionDjStrategy.cs", files);
        Assert.Contains("IMelodayDjStrategy.cs", files);
    }

    [Fact]
    public void TheStrategyNamesAreDistinctAcrossTheDomain()
    {
        // One name per implementation, or a definition could silently resolve to the
        // wrong strategy.
        //
        // Asserted only on distinctness, never on a count. The Companion test was
        // written when two strategies existed and pinned the number, so adding the
        // third failed it — the exact coupling this rule was meant to prevent.
        var declared = StrategyIdsInDomain().ToArray();

        Assert.NotEmpty(declared);
        Assert.Equal(declared.Length, declared.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// Every <c>StrategyId</c> declared anywhere in the DJ domain.
    ///
    /// <para>Shared with the Journey guardrail so both check the same thing from the
    /// same place; a second copy of this scan would drift.</para>
    /// </summary>
    internal static IEnumerable<string?> StrategyIdsInDomain()
    {
        const string marker = "public const string StrategyId = \"";
        foreach (var path in Directory.GetFiles(RepoPath("DeezSpoTag.Services", "Library", "Dj"), "*.cs"))
        {
            var text = File.ReadAllText(path);
            var start = text.IndexOf(marker, StringComparison.Ordinal);
            while (start >= 0)
            {
                start += marker.Length;
                yield return text[start..text.IndexOf('"', start)];

                // Continue past this hit so a file declaring the constant more than
                // once is reported rather than silently counted once.
                start = text.IndexOf(marker, start, StringComparison.Ordinal);
            }
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