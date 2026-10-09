using System;
using System.IO;
using System.Linq;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Structural guardrails for the Anchor strategy.
///
/// <para>The behavioural tests prove Anchor does the right thing. These prove the
/// things that would still be wrong even when every one of those tests passes — a
/// dependency smuggled in beside a correct algorithm, or a contract quietly widened
/// now that a real implementation exists to satisfy it.</para>
///
/// <para>The Phase 4 contract tests described the seam as a requirement. This file
/// checks the requirement was met rather than assumed.</para>
/// </summary>
public sealed class AnchorDjStrategyGuardrailTest
{
    [Fact]
    public void AnchorIsRegisteredByTheNameStoredOnTheDefinition()
    {
        // The stored value and the implementation's own id have to be the same
        // string, or a definition created with strategy "anchor" resolves to nothing.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "AnchorDjStrategy.cs");

        Assert.Contains("public const string StrategyId = \"anchor\"", strategy, StringComparison.Ordinal);
        Assert.Contains("public string Strategy => StrategyId;", strategy, StringComparison.Ordinal);
        Assert.Contains("public sealed class AnchorDjStrategy : IMelodayDjStrategy", strategy, StringComparison.Ordinal);
    }

    [Fact]
    public void AnchorTakesOnlyItsRequestAndReturnsAResult()
    {
        // Phase 4 pinned this on the interface. Anchor is the first implementation,
        // so it is the first chance for the seam to be widened.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "AnchorDjStrategy.cs");

        Assert.Contains(
            "public DjStrategyResult BuildPlaylist(DjStrategyRequest request)",
            strategy,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AnchorHasNoConstructorDependencies()
    {
        // A dependency would have to be supplied from outside, which is exactly what
        // "a strategy is a pure function of its request" rules out. A strategy with
        // state could not be reasoned about or tested in isolation.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "AnchorDjStrategy.cs");

        Assert.DoesNotContain("public AnchorDjStrategy(", strategy, StringComparison.Ordinal);
    }

    [Fact]
    public void AnchorReadsNoClockAndNoRandomSource()
    {
        // Both would break the property a person actually relies on: the same seeds
        // producing the same playlist. A clock makes it age; randomness makes it luck.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "AnchorDjStrategy.cs");

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
    public void AnchorTouchesNoDatabase()
    {
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "AnchorDjStrategy.cs");

        foreach (var forbidden in new[] { "LibraryRepository", "Sqlite", "DbConnection", "dj_generation", "dj_seed_spec" })
        {
            Assert.DoesNotContain(forbidden, strategy, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AnchorRunsNoInferenceAndComputesNoSimilarityOfItsOwn()
    {
        // Similarity arrives resolved in the request. A strategy that scored its own
        // tracks would duplicate the exact-search implementation and could not be
        // tested without the vector index.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "AnchorDjStrategy.cs");

        Assert.DoesNotContain("SonicSimilarity", strategy, StringComparison.Ordinal);
        Assert.DoesNotContain("Cosine", strategy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Vector", strategy, StringComparison.Ordinal);
        Assert.Contains("SeedAffinities", strategy, StringComparison.Ordinal);
    }

    [Fact]
    public void AnchorStaysInsideTheDomainNamespace()
    {
        // The domain must not depend on the web host, or it stops being usable from a
        // worker and stops being testable without one.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "AnchorDjStrategy.cs");

        Assert.Contains("namespace DeezSpoTag.Services.Library.Dj;", strategy, StringComparison.Ordinal);
        Assert.DoesNotContain("DeezSpoTag.Web", strategy, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.AspNetCore", strategy, StringComparison.Ordinal);
    }

    [Fact]
    public void AnchorDoesNotChangeGenreStyleOrMood()
    {
        // Sonic is a separate axis from the semantic analysis. If a DJ could adjust
        // tags it would be editing the library through what is meant to be a
        // playlist, and a database-only feature would start writing metadata.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "AnchorDjStrategy.cs");

        // Checks the code, not the comments: a doc comment may legitimately discuss
        // genre and mood in order to say this strategy does not touch them.
        var code = StripCommentsAndDocComments(strategy);

        foreach (var forbidden in new[]
        {
            "VibeSemantic",
            "Genre",
            "Mood",
            "Style",
            "Tag",
            "Evidence",
            "Analysis",
        })
        {
            // "Tag" is a substring of the namespace declaration itself
            // ("DeezSpoTag.Services.Library.Dj"), so it is matched on a word boundary
            // rather than as a bare substring.
            Assert.False(
                System.Text.RegularExpressions.Regex.IsMatch(
                    code,
                    $@"\b{forbidden}\b"),
                $"Anchor references '{forbidden}', which would mean it reaches the semantic analysis.");
        }
    }

    [Fact]
    public void AnchorKeepsNoMutableStaticState()
    {
        // The dedupe and the scoring are the only stateful-looking parts, and both are
        // per-call locals. A cached field would let one DJ's build leak into another's
        // and make results depend on execution order.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "AnchorDjStrategy.cs");

        // "static " appears only as a method modifier or a lambda marker, never as a
        // field declaration.
        foreach (var line in strategy.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("static ", StringComparison.Ordinal)
                && !trimmed.StartsWith("private static ", StringComparison.Ordinal)
                && !trimmed.StartsWith("public static ", StringComparison.Ordinal)
                && !trimmed.StartsWith("public const ", StringComparison.Ordinal))
            {
                continue;
            }

            // A field would need a type and a name; methods carry '(' after the name.
            // const is immutable by definition, so only non-const statics matter.
            if (trimmed.StartsWith("public const ", StringComparison.Ordinal)
                || trimmed.StartsWith("private const ", StringComparison.Ordinal))
            {
                continue;
            }

            // A method carries '(' after its name; a field does not.
            var parenIndex = trimmed.IndexOf('(');
            var hasMethodSignature = parenIndex >= 0;

            Assert.True(
                hasMethodSignature,
                $"Anchor appears to declare mutable static state: {trimmed}");
        }
    }

    [Fact]
    public void AnchorProducesNoVectorAndNeverTouchesTheEmbeddingTables()
    {
        // Vectors stay database-side. A strategy is handed similarities, never audio
        // or embeddings, so it cannot put one anywhere.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "AnchorDjStrategy.cs");

        Assert.DoesNotContain("track_sonic_embedding", strategy, StringComparison.Ordinal);
        Assert.DoesNotContain("Embedding", strategy, StringComparison.Ordinal);
    }

    [Fact]
    public void EverySortIsTotalSoOrderingIsReproducible()
    {
        // LINQ's OrderBy is a stable sort, so equal keys keep input order. Where the
        // input order is itself unstable (a set or a dictionary), an equal-key run
        // would reorder between runs. Every ordering therefore carries a final
        // tie-break on a value that is unique.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "AnchorDjStrategy.cs");

        var orderings = strategy
            .Split("\n")
            .Select(line => line.Trim())
            .Where(line => line.StartsWith(".OrderBy", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, orderings.Count);
        foreach (var ordering in orderings)
        {
            var block = strategy.Substring(
                strategy.IndexOf(ordering, StringComparison.Ordinal),
                260);
            Assert.Contains(".ThenBy", block);
        }
    }

    [Fact]
    public void AnchorRanksSeedsBeforeScoringNeighbours()
    {
        // Ordering of operations, not just of results. A seed is the DJ's identity,
        // so it must be placed before the count can be spent on a neighbour.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "AnchorDjStrategy.cs");

        var seedPlacement = strategy.IndexOf("DjItemReasons.Seed)", StringComparison.Ordinal);
        var neighbourOrdering = strategy.IndexOf("AffinityToSeeds(", StringComparison.Ordinal);

        Assert.True(seedPlacement > 0, "Seed placement was not found.");
        Assert.True(neighbourOrdering > 0, "Neighbour scoring was not found.");
        Assert.True(
            seedPlacement < neighbourOrdering,
            "Anchor scores neighbours before placing seeds, so a seed can be crowded out.");
    }

    [Fact]
    public void AnchorRefusesToInventAnAffinityForAnUnscoredTrack()
    {
        // A candidate absent from the table was never measured. Choosing it would put
        // a track in the playlist on a guess, wearing the same confidence as a
        // measured one.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "AnchorDjStrategy.cs");
        var scoring = ExtractRegion(strategy, "private static double AffinityToSeeds", "private static string DescribeSeeds");

        Assert.Contains("if (!affinities.TryGetValue(trackId, out var perSeed)", scoring, StringComparison.Ordinal);
        Assert.Contains("return 0d;", scoring, StringComparison.Ordinal);
        Assert.Contains("double.IsFinite(affinity)", scoring, StringComparison.Ordinal);
    }

    [Fact]
    public void AnchorBoundsEveryScoreItReportsToAUnitRange()
    {
        // The value is stored as a REAL and read back as a similarity. An unbounded
        // score would make the stored provenance meaningless and would break any
        // later threshold applied to it.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "AnchorDjStrategy.cs");
        var scoring = ExtractRegion(strategy, "private static double AffinityToSeeds", "private static string DescribeSeeds");

        Assert.Contains("Math.Clamp(best + corroboration, 0d, 1d)", scoring, StringComparison.Ordinal);
    }

    [Fact]
    public void AnchorReportsRatherThanHidesThinInput()
    {
        // A DJ that quietly returned three tracks for a forty-track request, over a
        // library with no embeddings, would look like a considered decision. Each of
        // those cases says so instead.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "AnchorDjStrategy.cs");

        Assert.Contains("SonicCoveragePercent < LowCoveragePercent", strategy, StringComparison.Ordinal);
        Assert.Contains("ordered.Count < trackCount", strategy, StringComparison.Ordinal);
        Assert.Contains("seeds.Count == 0", strategy, StringComparison.Ordinal);
        Assert.Contains("candidatePool.Count == 0", strategy, StringComparison.Ordinal);
    }

    [Fact]
    public void AnchorEmitsOnlyTheReasonsItCanJustify()
    {
        // Two reasons exist: a seed is a seed, and everything else is a neighbour of
        // the seed set. Claiming "companion" or "journey" would be reporting an
        // intent this strategy never had.
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "AnchorDjStrategy.cs");

        Assert.Contains("DjItemReasons.Seed", strategy, StringComparison.Ordinal);
        Assert.Contains("DjItemReasons.Neighbour", strategy, StringComparison.Ordinal);
        Assert.DoesNotContain("DjItemReasons.Companion", strategy, StringComparison.Ordinal);
        Assert.DoesNotContain("DjItemReasons.Journey", strategy, StringComparison.Ordinal);
    }

    [Fact]
    public void AnchorIsDiscoverableBesideTheOtherDomainFiles()
    {
        // Kept with the rest of the domain rather than growing a second location for
        // strategy code.
        var files = Directory
            .GetFiles(RepoPath("DeezSpoTag.Services", "Library", "Dj"), "*.cs")
            .Select(Path.GetFileName)
            .ToArray();

        Assert.Contains("AnchorDjStrategy.cs", files);
        Assert.Contains("IMelodayDjStrategy.cs", files);
    }

    [Fact]
    public void TheSeedAffinityMapIsOnTheRequestRatherThanAmbient()
    {
        // An ambient or thread-static table would make the strategy's behaviour
        // depend on something the caller did not pass, which is untestable and breaks
        // the moment two builds run concurrently.
        var contract = Read("DeezSpoTag.Services", "Library", "Dj", "IMelodayDjStrategy.cs");
        var strategy = Read("DeezSpoTag.Services", "Library", "Dj", "AnchorDjStrategy.cs");

        Assert.Contains("SeedAffinities", contract, StringComparison.Ordinal);

        foreach (var forbidden in new[]
        {
            "AsyncLocal",
            "ThreadStatic",
            "[ThreadStatic]",
            "SeedAffinityTable",
            "Current.Value",
        })
        {
            Assert.DoesNotContain(forbidden, strategy, StringComparison.Ordinal);
        }
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>
    /// Source with comment-only lines removed.
    ///
    /// <para>Needed because this project documents heavily: a doc comment may
    /// legitimately mention genre or mood in order to state that the strategy does not
    /// touch them, and asserting against the raw file would flag the explanation as
    /// the violation. Trailing comments on a code line are left alone, which is safe
    /// here because the terms asserted on appear in identifiers, not in trailing
    /// prose.</para>
    /// </summary>
    private static string StripCommentsAndDocComments(string source)
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