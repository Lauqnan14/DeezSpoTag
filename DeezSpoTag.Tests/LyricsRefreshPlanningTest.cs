using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Web.Services;
using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class LyricsRefreshPlanningTest
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
        Assert.Equal(
            shouldFetch ? LyricsSidecarWorkKind.UpgradeLrcToWord : LyricsSidecarWorkKind.None,
            plan.Work);
    }

    [Fact]
    public void MissingRequestedLrc_Fetches()
    {
        using var temp = new TemporaryDirectory();
        var audioPath = CreateAudio(temp.Path);

        var plan = LyricsRefreshQueueService.PlanExistingLyrics(1, audioPath, LrcSettings(LrcTimingModes.Line), LyricsRefreshOptions.Default);

        Assert.True(plan.ShouldFetchLyrics);
        Assert.Equal(LyricsSidecarWorkKind.FetchMissing, plan.Work);
    }

    [Fact]
    public void RemoveOnlyLineSyncedTtml_IsLocalWorkNotAFetch()
    {
        using var temp = new TemporaryDirectory();
        var audioPath = CreateAudio(temp.Path);
        File.WriteAllText(Path.ChangeExtension(audioPath, ".ttml"), "<tt>line synced</tt>");

        var plan = LyricsRefreshQueueService.PlanExistingLyrics(
            1,
            audioPath,
            LrcSettings(LrcTimingModes.Line),
            new LyricsRefreshOptions(RefreshLyrics: false, RemoveLineSyncedTtml: true));

        Assert.False(plan.ShouldFetchLyrics);
        Assert.True(plan.NeedsLocalOnly);
        Assert.Equal(LyricsSidecarWorkKind.RemoveLineSyncedTtml, plan.Work);
    }

    [Fact]
    public void RewriteLineSyncedTtml_IsNetworkWorkWhenProfileWantsTtml()
    {
        using var temp = new TemporaryDirectory();
        var audioPath = CreateAudio(temp.Path);
        File.WriteAllText(Path.ChangeExtension(audioPath, ".ttml"), "<tt>line synced</tt>");
        var settings = LrcSettings(LrcTimingModes.Line);
        settings.LrcType = "ttml-lyrics";
        settings.LrcFormat = "ttml";

        var plan = LyricsRefreshQueueService.PlanExistingLyrics(
            1,
            audioPath,
            settings,
            new LyricsRefreshOptions(RefreshLyrics: false, RewriteLineSyncedTtml: true));

        Assert.True(plan.ShouldFetchLyrics);
        Assert.Equal(LyricsSidecarWorkKind.RewriteTtmlToWord, plan.Work);
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
