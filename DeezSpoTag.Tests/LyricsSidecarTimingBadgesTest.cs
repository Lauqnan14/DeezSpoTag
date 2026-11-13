using System;
using System.IO;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class LyricsSidecarTimingBadgesTest
{
    [Fact]
    public async System.Threading.Tasks.Task QueueFinalizationDisplaysTheActualStageAndElapsedTime()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Join(root.FullName, "DeezSpoTag.Web/Views/Activities/Index.cshtml"))) root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Join(root!.FullName, "DeezSpoTag.Web/Views/Activities/Index.cshtml"));
        var start = source.IndexOf("function getDownloadFinalizationActivity(", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf("\nfunction ", start + 1, StringComparison.Ordinal);
        var function = source[start..end];
        var script = "const assert=require('node:assert/strict');" + function + """
            const task={status:'running',audioAcquired:true,finalizationStage:'lyrics-prefetch',finalizationElapsedMs:65000};
            assert.equal(getDownloadFinalizationActivity(task),'Waiting for lyrics (65s)');
            assert.equal(getDownloadFinalizationActivity({...task,finalizationStage:'artwork-prefetch'}),'Waiting for artwork (65s)');
            assert.equal(getDownloadFinalizationActivity({...task,finalizationStage:'artwork_prefetch'}),'Waiting for artwork (65s)');
            assert.equal(getDownloadFinalizationActivity({...task,finalizationStage:'tag_writing'}),'Writing tags (65s)');
            assert.equal(getDownloadFinalizationActivity({...task,finalizationStage:'tag-writing'}),'Writing tags (65s)');
            assert.equal(getDownloadFinalizationActivity({...task,status:'complete'}),'');
            assert.equal(getDownloadFinalizationActivity({...task,audioAcquired:false}),'');
            assert.equal(getDownloadFinalizationActivity({...task,finalizationElapsedMs:-1}),'Waiting for lyrics (0s)');
            """;
        var startInfo = new System.Diagnostics.ProcessStartInfo("node") { RedirectStandardError = true };
        startInfo.ArgumentList.Add("-e"); startInfo.ArgumentList.Add(script);
        using var process = System.Diagnostics.Process.Start(startInfo)!;
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
    }

    [Theory]
    [InlineData("[ti:Title]", LrcTimingKind.None)]
    [InlineData("[00:01.20]Hello", LrcTimingKind.Line)]
    [InlineData("[00:01.20][00:45.00]Chorus", LrcTimingKind.Line)]
    [InlineData("[00:01.20]<00:01.200>Hello", LrcTimingKind.Word)]
    [InlineData("[00:01.20]Hel[00:01.50]lo", LrcTimingKind.Word)]
    public void ClassifyTiming_ReturnsCanonicalLrcQuality(string content, LrcTimingKind expected)
    {
        Assert.Equal(expected, LrcContent.ClassifyTiming(content));
    }

    [Fact]
    public void FromAudioPath_ReportsWordSyncedLrcAsEnhancedLyrics()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"deezspotag-lyrics-badges-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var audioPath = Path.Combine(directory, "track.flac");
            File.WriteAllText(audioPath, "audio");
            File.WriteAllText(Path.Combine(directory, "track.lrc"), "[00:01.00]<00:01.000>hello");

            var badges = LyricsSidecarTimingBadges.FromAudioPath(audioPath);

            Assert.Contains("enhanced", badges);
            Assert.DoesNotContain("synced", badges);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FromAudioPath_IgnoresLegacyElrcFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"deezspotag-lyrics-badges-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var audioPath = Path.Combine(directory, "track.flac");
            File.WriteAllText(audioPath, "audio");
            File.WriteAllText(Path.Combine(directory, "track.elrc"), "[00:01.00]<00:01.000>hello");

            Assert.Empty(LyricsSidecarTimingBadges.FromAudioPath(audioPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FromAudioPath_ReportsExistingSyncedSidecarEvenWhenRefreshIsNotRequested()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"deezspotag-lyrics-badges-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var audioPath = Path.Combine(directory, "track.flac");
            File.WriteAllText(audioPath, "audio");
            File.WriteAllText(Path.Combine(directory, "track.lrc"), "[00:01.00]hello");

            var badges = LyricsSidecarTimingBadges.FromAudioPath(audioPath);

            Assert.Contains("synced", badges);
            Assert.DoesNotContain("unsynced", badges);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FromAudioPath_ReturnsEmptyWhenNoSidecarsExist()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"deezspotag-lyrics-badges-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var audioPath = Path.Combine(directory, "track.flac");
            File.WriteAllText(audioPath, "audio");

            Assert.Empty(LyricsSidecarTimingBadges.FromAudioPath(audioPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FromSidecars_MapsFilesToLyricsSettingsLabels()
    {
        Assert.Equal(["synced"], LyricsSidecarTimingBadges.FromSidecars(null, "[00:01.00]hello", hasUnsyncedTxt: true));
        Assert.Equal(["enhanced"], LyricsSidecarTimingBadges.FromSidecars(null, "[00:01.00]<00:01.000>hello", hasUnsyncedTxt: false));
        const string wordTtml =
            "<tt xmlns=\"http://www.w3.org/ns/ttml\" timing=\"Word\"><body><div>"
            + "<p begin=\"1.0\" end=\"3.0\"><span begin=\"1.0\" end=\"1.3\">Oh</span>"
            + "<span begin=\"1.4\" end=\"2.5\">yeah</span></p></div></body></tt>";
        const string lineTtml =
            "<tt xmlns:itunes=\"http://music.apple.com/lyric-ttml-internal\"><body><div>"
            + "<p begin=\"1.0\" end=\"3.0\">Oh yeah</p></div></body></tt>";
        Assert.Equal(["ttml", "synced"], LyricsSidecarTimingBadges.FromSidecars(wordTtml, "[00:01.00]hello", hasUnsyncedTxt: true));
        Assert.Equal(["synced"], LyricsSidecarTimingBadges.FromSidecars(lineTtml, "[00:01.00]hello", hasUnsyncedTxt: true));
        Assert.Equal(["unsynced"], LyricsSidecarTimingBadges.FromSidecars(null, null, hasUnsyncedTxt: true));
    }
}
