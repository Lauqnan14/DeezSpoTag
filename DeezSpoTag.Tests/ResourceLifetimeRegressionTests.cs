using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using System.Diagnostics;
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
    public void ShazamRecognizer_TerminationWaitsUntilOwnedProcessExits()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/sh",
            UseShellExecute = false,
            ArgumentList = { "-c", "sleep 30" }
        }) ?? throw new InvalidOperationException("Unable to start test process.");
        var method = typeof(ShazamRecognitionService).GetMethod(
            "TryTerminateRecognizerProcess",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("TryTerminateRecognizerProcess not found.");

        var terminated = Assert.IsType<bool>(method.Invoke(null, [process]));

        Assert.True(terminated);
        Assert.True(process.HasExited);
    }

    [Fact]
    public void ShazamRecognizer_SelectsRepeatedFingerprintOverFirstConflictingHit()
    {
        var attempts = new List<ShazamRecognitionAttempt>
        {
            MatchedShazamAttempt("672163252", "Paijo", "Ufuk KAPLAN", null),
            MatchedShazamAttempt("270857053", "One Girl", "Bigpin", "TCAFP2115582"),
            MatchedShazamAttempt("270857053", "One Girl", "Bigpin", "TCAFP2115582")
        };
        var method = typeof(ShazamRecognitionService).GetMethod(
            "SelectBestAudioOnlyAttempt",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("SelectBestAudioOnlyAttempt not found.");

        var selected = Assert.IsType<ShazamRecognitionAttempt>(method.Invoke(null, [attempts]));

        Assert.Equal("270857053", selected.Recognition?.TrackId);
        Assert.Equal("One Girl", selected.Recognition?.Title);
        Assert.Equal("2", Assert.Single(selected.Recognition!.Tags["SHAZAM_FINGERPRINT_SELECTED_COUNT"]));
        Assert.Equal("3", Assert.Single(selected.Recognition.Tags["SHAZAM_FINGERPRINT_TOTAL_MATCHES"]));
        Assert.Equal("true", Assert.Single(selected.Recognition.Tags["SHAZAM_FINGERPRINT_HAD_CONFLICT"]));
    }

    [Fact]
    public void ShazamRecognizer_RejectsConflictingSingletonFingerprintsWithoutIndependentIsrc()
    {
        var attempts = new List<ShazamRecognitionAttempt>
        {
            MatchedShazamAttempt("111", "Wrong One", "Artist A", null),
            MatchedShazamAttempt("222", "Wrong Two", "Artist B", null)
        };
        var method = typeof(ShazamRecognitionService).GetMethod(
            "SelectBestAudioOnlyAttempt",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("SelectBestAudioOnlyAttempt not found.");

        var selected = method.Invoke(null, [attempts]);

        Assert.Null(selected);
    }

    [Fact]
    public void ShazamRecognizer_PrefersIndependentIsrcWhenFingerprintCountsTie()
    {
        var attempts = new List<ShazamRecognitionAttempt>
        {
            MatchedShazamAttempt("672163252", "Paijo", "Ufuk KAPLAN", null),
            MatchedShazamAttempt("270857053", "One Girl", "Bigpin", "TCAFP2115582")
        };
        var method = typeof(ShazamRecognitionService).GetMethod(
            "SelectBestAudioOnlyAttempt",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("SelectBestAudioOnlyAttempt not found.");

        var selected = Assert.IsType<ShazamRecognitionAttempt>(method.Invoke(null, [attempts]));

        Assert.Equal("270857053", selected.Recognition?.TrackId);
        Assert.Equal("TCAFP2115582", selected.Recognition?.Isrc);
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
        var spotifyMetadata = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "SpotifyMetadataService.cs"));
        var spotifyArtwork = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "SpotifyArtworkResolver.cs"));
        var spotifyTracklist = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "SpotifyTracklistService.cs"));
        var soundtrack = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "MediaServerSoundtrackService.cs"));

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
        Assert.Contains("AudioFeatureCacheLimit", spotifyMetadata, StringComparison.Ordinal);
        Assert.Contains("PlaylistTrackCacheLimit", spotifyMetadata, StringComparison.Ordinal);
        Assert.Contains("CacheLimit", spotifyArtwork, StringComparison.Ordinal);
        Assert.Contains("SnapshotCacheLimit", spotifyTracklist, StringComparison.Ordinal);
        Assert.Contains("SoundtrackCacheLimit", soundtrack, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoTagTerminalCleanup_RemovesAllPerJobRuntimeState()
    {
        var repoRoot = FindRepoRoot();
        var source = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "AutoTagService.cs"));

        Assert.Contains("_lastActivityLines.TryRemove(job.Id, out _);", source, StringComparison.Ordinal);
        Assert.Contains("_archiveLocks.TryRemove(job.Id, out _);", source, StringComparison.Ordinal);
        Assert.Contains("_lastRunIndexUpdateUtc.TryRemove(job.Id, out _);", source, StringComparison.Ordinal);
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

    private static ShazamRecognitionAttempt MatchedShazamAttempt(string trackId, string title, string artist, string? isrc)
        => new()
        {
            Outcome = ShazamRecognitionOutcome.Matched,
            Recognition = new ShazamRecognitionInfo
            {
                TrackId = trackId,
                Title = title,
                Artist = artist,
                Artists = new List<string> { artist },
                Isrc = isrc,
                Url = $"https://www.shazam.com/track/{trackId}/{title.Replace(' ', '-').ToLowerInvariant()}"
            }
        };
}
