using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AutoTagPlatformMatchTimeoutGuardrailTests
{
    [Fact]
    public void LocalAutoTagRunner_OpensPerJobCircuitAfterFirstMatchTimeout()
    {
        var runner = ReadSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");

        Assert.Contains("HashSet<string> UnavailablePlatforms", runner, StringComparison.Ordinal);
        Assert.Contains("IsPlatformUnavailable(context.JobMatchCache, context.Platform)", runner, StringComparison.Ordinal);
        Assert.Contains("MarkPlatformUnavailable(context.JobMatchCache, context.Platform)", runner, StringComparison.Ordinal);
        Assert.Contains("provider_unavailable", runner, StringComparison.Ordinal);
        Assert.Contains("skipped; platform unavailable after earlier match timeout", runner, StringComparison.Ordinal);
        Assert.Contains("skipping remaining {context.Platform} matches in this run", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalAutoTagRunner_DoesNotTreatLaterTimeoutSkipsAsProviderErrors()
    {
        var runner = ReadSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");
        var resolveStart = runner.IndexOf("private async Task<AutoTagMatchResult?> ResolvePlatformMatchAsync", StringComparison.Ordinal);
        Assert.True(resolveStart >= 0, "Missing ResolvePlatformMatchAsync.");
        var resolveEnd = runner.IndexOf("private async Task<AutoTagMatchResult?> RunPlatformMatchWithTimeoutAsync", resolveStart, StringComparison.Ordinal);
        Assert.True(resolveEnd > resolveStart, "Missing RunPlatformMatchWithTimeoutAsync after ResolvePlatformMatchAsync.");
        var resolve = runner[resolveStart..resolveEnd];

        Assert.Contains("IsPlatformUnavailable", resolve, StringComparison.Ordinal);
        Assert.Contains("context.MatchFailureOutcome = \"provider_unavailable\"", resolve, StringComparison.Ordinal);
        var fileStart = runner.IndexOf("var match = await ResolvePlatformMatchAsync(context, matchInfo);", StringComparison.Ordinal);
        Assert.True(fileStart >= 0);
        var fileSlice = runner[fileStart..(fileStart + 900)];
        Assert.Contains("string.Equals(context.MatchFailureOutcome, \"provider_error\"", fileSlice, StringComparison.Ordinal);
        Assert.DoesNotContain("provider_unavailable\", StringComparison.Ordinal))", fileSlice, StringComparison.Ordinal);
    }

    [Fact]
    public void PathfinderQueries_FailFasterThanTheAutoTagMatchBudget()
    {
        var pathfinder = ReadSource("DeezSpoTag.Web", "Services", "SpotifyPathfinderMetadataClient.cs");

        Assert.Contains("private static readonly TimeSpan PathfinderRequestTimeout = TimeSpan.FromSeconds(8.0)", pathfinder, StringComparison.Ordinal);
        Assert.Contains("private static readonly TimeSpan PathfinderQueryTimeout = TimeSpan.FromSeconds(12.0)", pathfinder, StringComparison.Ordinal);
        Assert.Contains("queryTimeout.CancelAfter(PathfinderQueryTimeout)", pathfinder, StringComparison.Ordinal);
        Assert.Contains("requestTimeout.CancelAfter(PathfinderRequestTimeout)", pathfinder, StringComparison.Ordinal);
        Assert.Contains("Spotify Pathfinder request timed out after", pathfinder, StringComparison.Ordinal);
        Assert.Contains("Spotify Pathfinder query exceeded", pathfinder, StringComparison.Ordinal);
    }

    [Fact]
    public void LibrespotWorker_UsesShortTimeoutAndStartBackoff()
    {
        var blob = ReadSource("DeezSpoTag.Web", "Services", "SpotifyBlobService.cs");

        Assert.Contains("private static readonly TimeSpan LibrespotMetadataRequestTimeout = TimeSpan.FromSeconds(12)", blob, StringComparison.Ordinal);
        Assert.Contains("private static readonly TimeSpan LibrespotStartFailureBackoff = TimeSpan.FromMinutes(2)", blob, StringComparison.Ordinal);
        Assert.Contains("DateTimeOffset.UtcNow < _librespotStartRetryAfter", blob, StringComparison.Ordinal);
        Assert.Contains("MarkLibrespotStartFailure()", blob, StringComparison.Ordinal);
        Assert.Contains("worker start timed out after", blob, StringComparison.Ordinal);
        Assert.DoesNotContain("private static readonly TimeSpan LibrespotMetadataRequestTimeout = TimeSpan.FromSeconds(45)", blob, StringComparison.Ordinal);
    }

    [Fact]
    public void SpotifyIdMatch_DoesNotRequireEnrichmentToSucceed()
    {
        var matcher = ReadSource("DeezSpoTag.Web", "Services", "AutoTag", "SpotifyMatcher.cs");
        var client = ReadSource("DeezSpoTag.Web", "Services", "AutoTag", "SpotifyClient.cs");

        Assert.Contains("private static readonly TimeSpan IdEnrichmentTimeout = TimeSpan.FromSeconds(8)", matcher, StringComparison.Ordinal);
        Assert.Contains("EnrichOrKeepAsync", matcher, StringComparison.Ordinal);
        Assert.Contains("enrichmentTimeout.CancelAfter(IdEnrichmentTimeout)", matcher, StringComparison.Ordinal);
        Assert.Contains("A known Spotify ID is already a match", matcher, StringComparison.Ordinal);
        Assert.Contains("hydrateTracks: false", client, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "var enriched = await _client.EnrichTrackWithPathfinderAsync(seeded, cancellationToken);",
            matcher,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LibrespotInnerTimeout_IsShorterThanAutoTagMatchTimeout()
    {
        var runner = ReadSource("DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs");
        var blob = ReadSource("DeezSpoTag.Web", "Services", "SpotifyBlobService.cs");
        var pathfinder = ReadSource("DeezSpoTag.Web", "Services", "SpotifyPathfinderMetadataClient.cs");

        Assert.Contains("PlatformMatchTimeout = TimeSpan.FromSeconds(45)", runner, StringComparison.Ordinal);
        Assert.Contains("PathfinderQueryTimeout = TimeSpan.FromSeconds(12.0)", pathfinder, StringComparison.Ordinal);
        Assert.Contains("LibrespotMetadataRequestTimeout = TimeSpan.FromSeconds(12)", blob, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] relativeParts)
    {
        var repoRoot = ResolveRepoRoot();
        var path = Path.Join(repoRoot, Path.Join(relativeParts));
        Assert.True(File.Exists(path), $"Missing source: {path}");
        return File.ReadAllText(path);
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Join(current.FullName, "Directory.Build.props")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Unable to locate repository root from test output path.");
    }
}
