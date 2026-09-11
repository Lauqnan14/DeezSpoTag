using System;
using System.IO;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class LyricsSidecarTimingBadgesTest
{
    [Theory]
    [InlineData("[ti:Title]", LrcTimingKind.None)]
    [InlineData("[00:01.20]Hello", LrcTimingKind.Line)]
    [InlineData("[00:01.20]<00:01.200>Hello", LrcTimingKind.Word)]
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
}
