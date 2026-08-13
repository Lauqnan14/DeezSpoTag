using System;
using System.IO;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class LyricsSidecarTimingBadgesTests
{
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
