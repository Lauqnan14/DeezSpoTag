using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ResourceLifetimeRegressionTests
{
    [Fact]
    public void AutoTagPersistenceSnapshot_DoesNotDuplicateTagDiffGraph()
    {
        var source = new AutoTagJob { Id = "job", Status = "completed" };
        source.Logs.Add("complete");
        source.TagDiffs["/music/track.flac"] = new AutoTagTagDiff { Path = "/music/track.flac" };

        var method = typeof(AutoTagService).GetMethod(
            "CreateJobPersistenceSnapshot",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("CreateJobPersistenceSnapshot not found.");
        var snapshot = Assert.IsType<AutoTagJob>(method.Invoke(null, [source]));

        Assert.Empty(snapshot.TagDiffs);
        Assert.Equal(source.Logs, snapshot.Logs);
    }

    [Fact]
    public void AutoTagDiffSnapshot_ExcludesEmbeddedBinaryArtworkTags()
    {
        var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["TITLE"] = ["Track"],
            ["METADATA_BLOCK_PICTURE"] = [new string('A', 100_000)],
            ["APIC"] = [new string('B', 100_000)]
        };
        var method = typeof(AutoTagService).GetMethod(
            "CloneTags",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("CloneTags not found.");

        var clone = Assert.IsType<Dictionary<string, List<string>>>(method.Invoke(null, [tags]));

        Assert.Equal(["Track"], clone["TITLE"]);
        Assert.DoesNotContain("METADATA_BLOCK_PICTURE", clone.Keys);
        Assert.DoesNotContain("APIC", clone.Keys);
    }

    [Fact]
    public void ShazamRecognizer_DrainsOutputBeforeWaitingAndKillsTimedOutTree()
    {
        var repoRoot = FindRepoRoot();
        var service = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "ShazamRecognitionService.cs"));
        var script = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Tools", "shazam_port", "recognize.py"));

        var readIndex = service.IndexOf("process.StandardOutput.ReadToEndAsync(CancellationToken.None)", StringComparison.Ordinal);
        var waitIndex = service.IndexOf("process.WaitForExitAsync(timeout.Token)", StringComparison.Ordinal);
        Assert.True(readIndex >= 0 && waitIndex > readIndex);
        Assert.Contains("process.Kill(entireProcessTree: true)", service, StringComparison.Ordinal);
        Assert.DoesNotContain("process.WaitForExit();", service, StringComparison.Ordinal);
        Assert.DoesNotContain("\"response\": response", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ActiveStaticCaches_HaveExplicitResourceCaps()
    {
        var repoRoot = FindRepoRoot();
        var shazamDiscovery = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "ShazamDiscoveryService.cs"));
        var lastFmTags = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "LastFmTagService.cs"));
        var spotifyPathfinder = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "SpotifyPathfinderMetadataClient.cs"));
        var trackAvailability = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "TrackAvailabilityService.cs"));
        var localAutoTagRunner = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "AutoTag", "LocalAutoTagRunner.cs"));

        Assert.Contains("MaxSessionCardCacheEntries", shazamDiscovery, StringComparison.Ordinal);
        Assert.Contains("TrimSessionCardCacheIfNeeded", shazamDiscovery, StringComparison.Ordinal);
        Assert.Contains("MaxTagCacheEntries", lastFmTags, StringComparison.Ordinal);
        Assert.Contains("MaxSimilarArtistCacheEntries", lastFmTags, StringComparison.Ordinal);
        Assert.Contains("MaxSimilarTrackCacheEntries", lastFmTags, StringComparison.Ordinal);
        Assert.Contains("MaxIsrcCacheEntries", spotifyPathfinder, StringComparison.Ordinal);
        Assert.Contains("MaxArtistSearchEnrichmentCacheEntries", spotifyPathfinder, StringComparison.Ordinal);
        Assert.Contains("MaxShowCacheEntries", spotifyPathfinder, StringComparison.Ordinal);
        Assert.Contains("MaxShowEpisodeCacheEntries", spotifyPathfinder, StringComparison.Ordinal);
        Assert.Contains("MaxAppleSearchCacheEntries", trackAvailability, StringComparison.Ordinal);
        Assert.Contains("_jobMatchCaches.TryRemove(jobId, out _);", localAutoTagRunner, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Join(directory.FullName, "DeezSpoTag.Web")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
