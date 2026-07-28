using System;
using System.Collections.Generic;
using System.IO;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Utils;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class LyricsArtifactStateTests
{
    [Theory]
    [InlineData("lrc", "lrc")]
    [InlineData("ttml", "ttml")]
    [InlineData("both", "ttml,elrc,lrc")]
    [InlineData("richlyrics", "ttml,elrc,lrc")]
    public void DescribeResolutionPlan_UsesRequestedRichFormats(string format, string expected)
    {
        var plan = LyricsService.DescribeResolutionPlan(new DeezSpoTagSettings
        {
            SyncedLyrics = true,
            SaveLyrics = true,
            LrcType = "lyrics,syllable-lyrics,ttml-lyrics,unsynced-lyrics",
            LrcFormat = format,
            LyricsFallbackEnabled = true,
            LyricsFallbackOrder = "lrclib,apple"
        });

        Assert.Equal(expected, string.Join(',', plan.RequestedFormats));
        Assert.True(plan.PlainFallbackAllowed);
        Assert.Equal(["lrclib", "apple"], plan.Providers);
    }

    [Fact]
    public void DescribeResolutionPlan_FallbackDisabled_UsesOnlyPreferredProvider()
    {
        var plan = LyricsService.DescribeResolutionPlan(new DeezSpoTagSettings
        {
            SyncedLyrics = true,
            LrcType = "lyrics",
            LrcFormat = "lrc",
            LyricsFallbackEnabled = false,
            LyricsFallbackOrder = "spotify,lrclib,apple"
        });

        Assert.Equal(["spotify"], plan.Providers);
    }

    [Fact]
    public void ApplyResolution_SuppressesTxtWhenAnyRichFormatResolved()
    {
        var plan = new LyricsResolutionPlan(["ttml", "lrc"], ["apple", "lrclib"], true);
        var state = LyricsArtifactState.Fetching(plan);
        var result = new LyricsResolutionResult(
            null,
            plan,
            ["apple", "lrclib"],
            ["lrc", "txt"],
            new Dictionary<string, string>
            {
                ["lrc"] = "lrclib",
                ["txt"] = "apple"
            },
            null);

        state.ApplyResolution(result);

        Assert.Equal(["lrc"], state.ResolvedFormats);
        Assert.False(state.SourcesByFormat.ContainsKey("txt"));
        Assert.Equal("resolved", state.Status);
    }

    [Fact]
    public void ApplyResolution_AllowsTxtOnlyWhenNoRichFormatResolved()
    {
        var plan = new LyricsResolutionPlan(["ttml", "lrc"], ["apple"], true);
        var state = LyricsArtifactState.Fetching(plan);
        var result = new LyricsResolutionResult(
            null,
            plan,
            ["apple"],
            ["txt"],
            new Dictionary<string, string> { ["txt"] = "apple" },
            null);

        state.ApplyResolution(result);

        Assert.Equal(["txt"], state.ResolvedFormats);
        Assert.Equal("resolved", state.Status);
    }

    [Fact]
    public void ApplyDownloadedFiles_RecordsWrittenSidecarsAndSuppressesTxt()
    {
        var state = LyricsArtifactState.Fetching(new LyricsResolutionPlan(["lrc"], ["lrclib"], true));

        state.ApplyDownloadedFiles(new Dictionary<string, string>
        {
            ["lrc"] = "/music/track.lrc",
            ["txt"] = "/music/track.txt"
        });

        Assert.Equal(["lrc"], state.DownloadedFormats);
        Assert.Equal("completed", state.Status);
        Assert.False(state.FilesByFormat.ContainsKey("txt"));
    }

    [Fact]
    public void ApplyDownloadedFiles_RecordsEveryRichLyricsFormat()
    {
        var state = LyricsArtifactState.Fetching(new LyricsResolutionPlan(["ttml", "elrc", "lrc"], ["musixmatch"], false));

        state.ApplyDownloadedFiles(new Dictionary<string, string>
        {
            ["ttml"] = "/music/track.ttml",
            ["elrc"] = "/music/track.elrc",
            ["lrc"] = "/music/track.lrc"
        });

        Assert.Equal(["ttml", "elrc", "lrc"], state.DownloadedFormats);
        Assert.Equal("/music/track.elrc", state.FilesByFormat["elrc"]);
        Assert.Equal("completed", state.Status);
    }

    [Fact]
    public void QueueSettingsSnapshot_PreservesLyricsPreferencesForRetries()
    {
        var snapshot = QueueSourceSettingsSnapshot.Capture(new DeezSpoTagSettings
        {
            SyncedLyrics = true,
            SaveLyrics = true,
            LrcType = "lyrics,unsynced-lyrics",
            LrcFormat = "ttml",
            SynthesizeLrcFromTtml = true,
            LyricsFallbackEnabled = false,
            LyricsFallbackOrder = "lrclib,apple"
        });

        var effective = snapshot.ApplyTo(new DeezSpoTagSettings());

        Assert.True(effective.SyncedLyrics);
        Assert.True(effective.SaveLyrics);
        Assert.Equal("lyrics,unsynced-lyrics", effective.LrcType);
        Assert.Equal("ttml", effective.LrcFormat);
        Assert.True(effective.SynthesizeLrcFromTtml);
        Assert.False(effective.LyricsFallbackEnabled);
        Assert.Equal("lrclib,apple", effective.LyricsFallbackOrder);
    }

    [Fact]
    public void Fetching_PreservesCompletedArtifactsWithoutStartingAnotherLyricsPath()
    {
        var tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-lyrics-artifacts-" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);
        var ttmlPath = Path.Join(tempRoot, "track.ttml");
        var lrcPath = Path.Join(tempRoot, "track.lrc");
        File.WriteAllText(ttmlPath, "<tt><body><p>lyrics</p></body></tt>");
        File.WriteAllText(lrcPath, "[00:01.00]lyrics");
        var plan = new LyricsResolutionPlan(["ttml", "lrc"], ["apple", "lrclib"], true);
        var previous = new LyricsArtifactState
        {
            Revision = 50,
            Status = "completed",
            ResolvedFormats = ["ttml", "lrc"],
            DownloadedFormats = ["ttml", "lrc"],
            FilesByFormat = new Dictionary<string, string>
            {
                ["ttml"] = ttmlPath,
                ["lrc"] = lrcPath
            }
        };

        try
        {
            var next = LyricsArtifactState.Fetching(plan, previous);

            Assert.True(next.Satisfies(plan));
            Assert.Equal("completed", next.Status);
            Assert.True(next.Revision > previous.Revision);
            Assert.False(string.IsNullOrWhiteSpace(next.AttemptId));
            Assert.False(string.IsNullOrWhiteSpace(next.PlanFingerprint));
            Assert.Equal(64, next.FileHashesByFormat["ttml"].Length);
            Assert.Equal(64, next.FileHashesByFormat["lrc"].Length);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Fetching_InvalidatesMissingArtifactsSoRetryRefetchesLyrics()
    {
        var plan = new LyricsResolutionPlan(["elrc"], ["musixmatch"], false);
        var previous = new LyricsArtifactState
        {
            Status = "completed",
            ResolvedFormats = ["elrc"],
            DownloadedFormats = ["elrc"],
            FilesByFormat = new Dictionary<string, string>
            {
                ["elrc"] = Path.Join(Path.GetTempPath(), Path.GetRandomFileName() + ".elrc")
            }
        };

        var next = LyricsArtifactState.Fetching(plan, previous);

        Assert.False(next.Satisfies(plan));
        Assert.Equal("fetching", next.Status);
        Assert.Empty(next.DownloadedFormats);
        Assert.Empty(next.FilesByFormat);
    }

    [Fact]
    public void Fetching_InvalidatesArtifactsWhenResolutionPlanChanges()
    {
        var tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-lyrics-plan-" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);
        var path = Path.Join(tempRoot, "track.lrc");
        File.WriteAllText(path, "[00:01.00]lyrics");
        try
        {
            var original = LyricsArtifactState.Fetching(
                new LyricsResolutionPlan(["lrc"], ["lrclib"], false));
            original.ApplyDownloadedFiles(new Dictionary<string, string> { ["lrc"] = path });

            var changed = LyricsArtifactState.Fetching(
                new LyricsResolutionPlan(["elrc"], ["musixmatch"], false),
                original);

            Assert.Empty(changed.FilesByFormat);
            Assert.Equal("fetching", changed.Status);
            Assert.NotEqual(original.PlanFingerprint, changed.PlanFingerprint);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Fetching_InvalidatesArtifactWhoseContentsChanged()
    {
        var tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-lyrics-hash-" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);
        var path = Path.Join(tempRoot, "track.lrc");
        File.WriteAllText(path, "[00:01.00]first");
        var plan = new LyricsResolutionPlan(["lrc"], ["lrclib"], false);
        try
        {
            var original = LyricsArtifactState.Fetching(plan);
            original.ApplyDownloadedFiles(new Dictionary<string, string> { ["lrc"] = path });
            File.WriteAllText(path, "[00:01.00]changed");

            var retried = LyricsArtifactState.Fetching(plan, original);

            Assert.Empty(retried.FilesByFormat);
            Assert.False(retried.Satisfies(plan));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
