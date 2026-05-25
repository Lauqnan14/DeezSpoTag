using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DeezSpoTag.Services.Utils;
using Xunit;

namespace DeezSpoTag.Tests;

[Collection("DataRoot Environment")]
public sealed class CodeQlCleanupRegressionGuardrailTests
{
    private const int MaxProductionPathCombineCalls = 96;
    private const int MaxProductionUnfilteredBroadExceptionCatches = 0;
    private const int MaxUnobservedBroadExceptionCatches = 9;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    [Fact]
    public void DataRootNormalization_CollapsesRepeatedDeezSpoTagSuffixes()
    {
        var basePath = Path.Join(Path.GetTempPath(), "deezspotag-codeql-guardrail-" + Path.GetRandomFileName());
        var configuredPath = Path.Join(basePath, "deezspotag", "deezspotag");

        var normalized = AppDataPathResolver.NormalizeConfiguredDataRoot(configuredPath);

        Assert.Equal(Path.GetFullPath(basePath), normalized);
    }

    [Fact]
    public void ResolveDataRootOrDefault_PrefersConfiguredUnifiedRoot()
    {
        var configuredRoot = Path.Join(Path.GetTempPath(), "deezspotag-configured-root-" + Path.GetRandomFileName());
        using var scope = new TestConfigRootScope(configuredRoot);

        var resolved = AppDataPathResolver.ResolveDataRootOrDefault(
            Path.Join(Path.GetTempPath(), "deezspotag-untrusted-default-" + Path.GetRandomFileName()));

        Assert.Equal(scope.RootPath, resolved);
    }

    [Fact]
    public void ResolveDbPathStrict_MovesLegacyDatabaseIntoScopedDirectory()
    {
        var root = CreateTempDirectory("deezspotag-db-move-");
        try
        {
            var legacyPath = Path.Join(root, "library.db");
            File.WriteAllText(legacyPath, "legacy");

            var resolved = AppDataPathResolver.ResolveDbPathStrict(root, "library", "library.db");

            Assert.Equal(Path.GetFullPath(Path.Join(root, "db", "library", "library.db")), resolved);
            Assert.False(File.Exists(legacyPath));
            Assert.Equal("legacy", File.ReadAllText(resolved));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveDbPathStrict_RejectsLegacyAndScopedDatabaseConflict()
    {
        var root = CreateTempDirectory("deezspotag-db-conflict-");
        try
        {
            var legacyPath = Path.Join(root, "library.db");
            var scopedPath = Path.Join(root, "db", "library", "library.db");
            Directory.CreateDirectory(Path.GetDirectoryName(scopedPath)!);
            File.WriteAllText(legacyPath, "legacy");
            File.WriteAllText(scopedPath, "scoped");

            var exception = Assert.Throws<InvalidOperationException>(
                () => AppDataPathResolver.ResolveDbPathStrict(root, "library", "library.db"));

            Assert.Contains("Database layout conflict", exception.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(legacyPath));
            Assert.True(File.Exists(scopedPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProductionSource_PathCombineUsage_MustNotIncreaseDuringCodeQlCleanup()
    {
        var calls = EnumerateProductionSources()
            .Sum(path => CountOccurrences(File.ReadAllText(path), "Path.Combine("));

        Assert.True(
            calls <= MaxProductionPathCombineCalls,
            $"Path.Combine usage increased from the cleanup benchmark of {MaxProductionPathCombineCalls} to {calls}.");
    }

    [Fact]
    public void ProductionSource_UnfilteredBroadExceptionCatches_AreNotAllowed()
    {
        var broadCatchPattern = new Regex(
            @"catch\s*\(\s*Exception(?:\s+\w+)?\s*\)(?!\s*when\s*\()",
            RegexOptions.CultureInvariant,
            RegexTimeout);
        var catches = EnumerateProductionSources()
            .Sum(path => broadCatchPattern.Count(File.ReadAllText(path)));

        Assert.True(
            catches <= MaxProductionUnfilteredBroadExceptionCatches,
            $"Unfiltered broad Exception catches must stay eliminated. Found {catches}.");
    }

    [Fact]
    public void ProductionSource_BroadExceptionCatches_MustRemainObservedOrExplicitlyBenign()
    {
        var unobservedCatches = EnumerateProductionSources()
            .SelectMany(path => FindUnobservedBroadExceptionCatches(path))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unobservedCatches.Count <= MaxUnobservedBroadExceptionCatches,
            "Unobserved broad Exception catches increased from the cleanup benchmark of " +
            $"{MaxUnobservedBroadExceptionCatches}: {string.Join(", ", unobservedCatches)}");
    }

    [Fact]
    public void ProductionSource_EmptyCatchBlocks_AreNotAllowed()
    {
        var emptyCatchPattern = new Regex(
            @"catch\s*(?:\([^)]*\))?\s*\{\s*\}",
            RegexOptions.CultureInvariant,
            RegexTimeout);
        var offenders = EnumerateProductionSources()
            .Where(path => emptyCatchPattern.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(ResolveSrcRoot(), path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Empty catch blocks found in: " + string.Join(", ", offenders));
    }

    private static string CreateTempDirectory(string prefix)
    {
        var path = Path.Join(Path.GetTempPath(), prefix + Path.GetRandomFileName());
        Directory.CreateDirectory(path);
        return path;
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string[] FindUnobservedBroadExceptionCatches(string path)
    {
        var source = File.ReadAllText(path);
        var catchPattern = new Regex(
            @"catch\s*\(\s*Exception(?:\s+\w+)?\s*\)\s*\{",
            RegexOptions.CultureInvariant,
            RegexTimeout);

        return catchPattern.Matches(source)
            .Cast<Match>()
            .Where(match => !IsCatchBlockObserved(source, match.Index + match.Length))
            .Select(match => $"{Path.GetRelativePath(ResolveSrcRoot(), path)}:{GetLineNumber(source, match.Index)}")
            .ToArray();
    }

    private static bool IsCatchBlockObserved(string source, int blockStart)
    {
        var depth = 1;
        var index = blockStart;
        while (index < source.Length && depth > 0)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
            }

            index++;
        }

        var block = source[blockStart..Math.Min(index, source.Length)];
        return block.Contains("Log", StringComparison.Ordinal)
            || block.Contains("_logger", StringComparison.Ordinal)
            || block.Contains("logger", StringComparison.Ordinal)
            || block.Contains("Record", StringComparison.Ordinal)
            || block.Contains("History", StringComparison.Ordinal)
            || block.Contains("Console.", StringComparison.Ordinal)
            || block.Contains("return", StringComparison.Ordinal)
            || block.Contains("throw", StringComparison.Ordinal);
    }

    private static int GetLineNumber(string source, int index)
        => source.AsSpan(0, index).Count('\n') + 1;

    private static string[] EnumerateProductionSources()
    {
        var srcRoot = ResolveSrcRoot();
        return new[]
            {
                Path.Join(srcRoot, "DeezSpoTag.Web"),
                Path.Join(srcRoot, "DeezSpoTag.Services")
            }
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();
    }

    private static string ResolveSrcRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Join(current.FullName, "Directory.Build.props"))
                && Directory.Exists(Path.Join(current.FullName, "DeezSpoTag.Web"))
                && Directory.Exists(Path.Join(current.FullName, "DeezSpoTag.Tests")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not resolve src root.");
    }
}
