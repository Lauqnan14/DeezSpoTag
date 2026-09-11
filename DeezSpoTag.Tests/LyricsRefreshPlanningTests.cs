using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Web.Services;
using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class LyricsRefreshPlanningTests
{
    [Fact]
    public void WordSyncedLrcAlreadyPresent_DoesNotFetch()
    {
        using var temp = new TemporaryDirectory();
        var audioPath = CreateAudio(temp.Path);
        File.WriteAllText(Path.ChangeExtension(audioPath, ".lrc"), "[00:01.00]<00:01.000>Hello");

        var plan = LyricsRefreshQueueService.PlanExistingLyrics(1, audioPath, LrcSettings(LrcTimingModes.WordEnhanced), LyricsRefreshOptions.Default);

        Assert.False(plan.ShouldFetchLyrics);
        Assert.Contains("enhanced", plan.CurrentBadges);
    }

    [Theory]
    [InlineData(LrcTimingModes.WordEnhanced, true)]
    [InlineData(LrcTimingModes.PreferEnhanced, true)]
    [InlineData(LrcTimingModes.Line, false)]
    public void LineSyncedLrc_ObeysSelectedTimingUpgrade(string timing, bool shouldFetch)
    {
        using var temp = new TemporaryDirectory();
        var audioPath = CreateAudio(temp.Path);
        File.WriteAllText(Path.ChangeExtension(audioPath, ".lrc"), "[00:01.00]Hello");

        var plan = LyricsRefreshQueueService.PlanExistingLyrics(1, audioPath, LrcSettings(timing), LyricsRefreshOptions.Default);

        Assert.Equal(shouldFetch, plan.ShouldFetchLyrics);
    }

    [Fact]
    public void MissingRequestedLrc_Fetches()
    {
        using var temp = new TemporaryDirectory();
        var audioPath = CreateAudio(temp.Path);

        var plan = LyricsRefreshQueueService.PlanExistingLyrics(1, audioPath, LrcSettings(LrcTimingModes.Line), LyricsRefreshOptions.Default);

        Assert.True(plan.ShouldFetchLyrics);
    }

    [Fact]
    public void DisabledLyricsProfile_DoesNotFetch()
    {
        using var temp = new TemporaryDirectory();
        var audioPath = CreateAudio(temp.Path);
        var settings = LrcSettings(LrcTimingModes.Line);
        settings.SyncedLyrics = false;
        settings.SaveLyrics = false;

        var plan = LyricsRefreshQueueService.PlanExistingLyrics(1, audioPath, settings, LyricsRefreshOptions.Default);

        Assert.False(plan.ShouldFetchLyrics);
    }

    private static DeezSpoTagSettings LrcSettings(string timing) => new()
    {
        SyncedLyrics = true,
        SaveLyrics = false,
        LrcType = "lyrics,syllable-lyrics",
        LrcFormat = "lrc",
        LrcTimingPreference = timing
    };

    private static string CreateAudio(string directory)
    {
        var path = Path.Join(directory, "track.flac");
        File.WriteAllText(path, "audio");
        return path;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Join(System.IO.Path.GetTempPath(), $"deezspotag-lyrics-plan-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
