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
    [InlineData("both", "ttml,lrc")]
    public void DescribeResolutionPlan_UsesRequestedRichFormats(string format, string expected)
    {
        var plan = LyricsService.DescribeResolutionPlan(new DeezSpoTagSettings
        {
            SyncedLyrics = true,
            SaveLyrics = true,
            LrcType = "lyrics,syllable-lyrics,unsynced-lyrics",
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
    public void ApplyDownloadedFiles_RecordsExistingSidecarsAndSuppressesTxt()
    {
        var directory = Path.Join(Path.GetTempPath(), "deezspotag-lyrics-artifacts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Join(directory, "track.lrc"), "[00:00.00]line");
            File.WriteAllText(Path.Join(directory, "track.txt"), "line");
            var state = LyricsArtifactState.Fetching(new LyricsResolutionPlan(["lrc"], ["lrclib"], true));

            state.ApplyDownloadedFiles(directory, "track.flac");

            Assert.Equal(["lrc"], state.DownloadedFormats);
            Assert.Equal("completed", state.Status);
            Assert.False(state.FilesByFormat.ContainsKey("txt"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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
            SynthesizeTtmlLyrics = true,
            LyricsFallbackEnabled = false,
            LyricsFallbackOrder = "lrclib,apple"
        });

        var effective = snapshot.ApplyTo(new DeezSpoTagSettings());

        Assert.True(effective.SyncedLyrics);
        Assert.True(effective.SaveLyrics);
        Assert.Equal("lyrics,unsynced-lyrics", effective.LrcType);
        Assert.Equal("ttml", effective.LrcFormat);
        Assert.True(effective.SynthesizeTtmlLyrics);
        Assert.False(effective.LyricsFallbackEnabled);
        Assert.Equal("lrclib,apple", effective.LyricsFallbackOrder);
    }

    [Fact]
    public void Fetching_PreservesCompletedArtifactsWithoutStartingAnotherLyricsPath()
    {
        var plan = new LyricsResolutionPlan(["ttml", "lrc"], ["apple", "lrclib"], true);
        var previous = new LyricsArtifactState
        {
            Revision = 50,
            Status = "completed",
            ResolvedFormats = ["ttml", "lrc"],
            DownloadedFormats = ["ttml", "lrc"]
        };

        var next = LyricsArtifactState.Fetching(plan, previous);

        Assert.True(next.Satisfies(plan));
        Assert.Equal("completed", next.Status);
        Assert.True(next.Revision > previous.Revision);
    }
}
