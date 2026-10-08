using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Covers the per-playlist sync schedule.
/// <para>
/// Two things carry the risk here. The first is a schedule that fires when it should not - a
/// destination being written on a cadence the user never chose, or being written twice for one
/// boundary - because every write is a change to someone's real playlist. The second is a schedule
/// that never fires, which is quieter but just as broken. Both are asserted directly.
/// </para>
/// </summary>
public sealed class PlaylistSyncScheduleTest : IDisposable
{
    private readonly string _dataRoot = Path.Combine(
        Path.GetTempPath(), "playlist-sync-schedule-" + Guid.NewGuid().ToString("N"));

    private sealed class TestEnvironment : IWebHostEnvironment, IAppDataRootOverride
    {
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Development";
        public string? AppDataRoot => ContentRootPath;
    }

    private PlaylistSyncScheduleStore NewStore()
    {
        Directory.CreateDirectory(_dataRoot);
        return new PlaylistSyncScheduleStore(
            new TestEnvironment { ContentRootPath = _dataRoot },
            NullLogger<PlaylistSyncScheduleStore>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }

    // ------------------------------------------------------------------ the default

    [Fact]
    public async Task APlaylistWithNoScheduleIsManual()
    {
        // Nothing syncs on a timer until a user asks for it. An unconfigured app must not start
        // writing to someone's Spotify.
        var store = NewStore();
        var entry = await store.GetAsync("plex", "pl-1");

        Assert.Null(entry);
        Assert.Empty(await store.LoadAsync());
    }

    [Fact]
    public async Task AnAbsentScheduleFileReadsAsNoSchedulesRatherThanFailing()
    {
        // A schedule is a convenience. An unreadable or missing file must never stop the app.
        var store = NewStore();
        Assert.Empty(await store.LoadAsync());
    }

    // ------------------------------------------------------------------ setting one

    [Fact]
    public async Task SavingASchedulePersistsItAcrossStoreInstances()
    {
        var store = NewStore();
        await store.SaveAsync("plex", "pl-1", PlaylistSyncCadence.Hourly, new[] { "spotify" });

        // A second store reads the same file, which is what proves it reached disk rather than only
        // the in-memory cache.
        var reloaded = await NewStore().GetAsync("plex", "pl-1");

        Assert.NotNull(reloaded);
        Assert.Equal(PlaylistSyncCadence.Hourly, reloaded!.Cadence);
        Assert.Equal(new[] { "spotify" }, reloaded.Targets);
    }

    [Fact]
    public async Task SavingTheSamePlaylistTwiceUpdatesItRatherThanDuplicating()
    {
        var store = NewStore();
        await store.SaveAsync("plex", "pl-1", PlaylistSyncCadence.Hourly, new[] { "spotify" });
        await store.SaveAsync("plex", "pl-1", PlaylistSyncCadence.Daily, new[] { "deezer" });

        var entries = await store.LoadAsync();

        Assert.Single(entries);
        Assert.Equal(PlaylistSyncCadence.Daily, entries[0].Cadence);
        Assert.Equal(new[] { "deezer" }, entries[0].Targets);
    }

    [Fact]
    public async Task SwitchingAPlaylistBackToManualClearsItsNextRun()
    {
        var store = NewStore();
        await store.SaveAsync("plex", "pl-1", PlaylistSyncCadence.Hourly, new[] { "spotify" });
        var cancelled = await store.SaveAsync("plex", "pl-1", PlaylistSyncCadence.Manual, Array.Empty<string>());

        // A cancelled schedule that kept its due time would fire once more after the user turned it
        // off, which is exactly the surprise this must not produce.
        Assert.Null(cancelled.NextRunAtUtc);
        Assert.Empty(cancelled.Targets);
    }

    [Fact]
    public async Task ADuplicateOrCasedDestinationIsStoredOnce()
    {
        var store = NewStore();
        var entry = await store.SaveAsync(
            "plex", "pl-1", PlaylistSyncCadence.Hourly, new[] { "Spotify", "spotify", " deezer " });

        Assert.Equal(new[] { "deezer", "spotify" }, entry.Targets);
    }

    [Fact]
    public async Task AScheduleNeedsAPlaylistToIdentifyIt()
    {
        var store = NewStore();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.SaveAsync("", "pl-1", PlaylistSyncCadence.Hourly, new[] { "spotify" }));
    }

    [Fact]
    public async Task AnUnknownCadenceIsRefusedRatherThanStored()
    {
        // A cadence this app does not implement would otherwise be persisted and then either never
        // fire or fire constantly.
        var store = NewStore();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.SaveAsync("plex", "pl-1", (PlaylistSyncCadence)7, new[] { "spotify" }));
    }

    [Fact]
    public async Task ACorruptScheduleFileIsTreatedAsNoSchedulesRatherThanThrowing()
    {
        var store = NewStore();
        await store.SaveAsync("plex", "pl-1", PlaylistSyncCadence.Hourly, new[] { "spotify" });

        // Corrupt the file the store just wrote.
        var path = Directory.EnumerateFiles(_dataRoot, "schedule.json", SearchOption.AllDirectories).First();
        await File.WriteAllTextAsync(path, "{ this is not json");

        Assert.Empty(await NewStore().LoadAsync());
    }

    // ------------------------------------------------------------------ the cadence itself

    [Theory]
    [InlineData(PlaylistSyncCadence.Every15Minutes, 15)]
    [InlineData(PlaylistSyncCadence.Hourly, 60)]
    [InlineData(PlaylistSyncCadence.Every6Hours, 360)]
    [InlineData(PlaylistSyncCadence.Daily, 1440)]
    public void TheNextRunLandsOnAWholeBoundaryOfTheInterval(
        PlaylistSyncCadence cadence, int intervalMinutes)
    {
        // Aligning to absolute boundaries is what makes the cadence predictable and restart-proof.
        // "Interval since the last run" would drift every time the app was restarted.
        var now = new DateTimeOffset(2026, 3, 14, 10, 7, 23, TimeSpan.Zero);
        var next = PlaylistSyncScheduleStore.NextBoundary(now, cadence);

        Assert.True(next > now, "The next run must be in the future.");
        Assert.Equal(0, next.Minute % intervalMinutes);
        Assert.Equal(0, next.Second);
    }

    [Fact]
    public void LandingExactlyOnABoundarySchedulesAFullIntervalLater()
    {
        // Otherwise a pass that finished exactly on a boundary would be immediately due again and
        // fire twice for the same boundary.
        var onBoundary = new DateTimeOffset(2026, 3, 14, 10, 0, 0, TimeSpan.Zero);
        var next = PlaylistSyncScheduleStore.NextBoundary(onBoundary, PlaylistSyncCadence.Hourly);

        Assert.Equal(onBoundary.AddHours(1), next);
    }

    [Fact]
    public void AManualScheduleHasNoNextRunAtAll()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(DateTimeOffset.MaxValue, PlaylistSyncScheduleStore.NextBoundary(now, PlaylistSyncCadence.Manual));
    }

    [Fact]
    public async Task ARunAdvancesTheScheduleSoItCannotFireTwiceForOneBoundary()
    {
        var store = NewStore();
        var entry = await store.SaveAsync("plex", "pl-1", PlaylistSyncCadence.Hourly, new[] { "spotify" });
        var firstDue = entry.NextRunAtUtc;

        await store.RecordRunAsync(entry, succeeded: true, "done");

        var after = await store.GetAsync("plex", "pl-1");
        Assert.True(after!.NextRunAtUtc > firstDue, "The next run must move past the one just served.");
        Assert.True(after.LastRunSucceeded);
        Assert.Equal("done", after.LastRunMessage);
    }

    [Fact]
    public async Task AFailedRunIsStillRescheduledRatherThanRetriedImmediately()
    {
        // A destination that is refusing requests usually keeps refusing. Retrying on every tick
        // would hammer it and bury the user in failures.
        var store = NewStore();
        var entry = await store.SaveAsync("plex", "pl-1", PlaylistSyncCadence.Every15Minutes, new[] { "spotify" });

        await store.RecordRunAsync(entry, succeeded: false, "Deezer is not responding.");

        var after = await store.GetAsync("plex", "pl-1");
        Assert.NotNull(after!.NextRunAtUtc);
        Assert.False(after.LastRunSucceeded);
        Assert.Equal("Deezer is not responding.", after.LastRunMessage);
    }

    [Fact]
    public async Task RecordingARunForAManualPlaylistChangesNothing()
    {
        var store = NewStore();
        var entry = await store.SaveAsync("plex", "pl-1", PlaylistSyncCadence.Manual, Array.Empty<string>());

        await store.RecordRunAsync(entry, succeeded: true, "should be ignored");

        var after = await store.GetAsync("plex", "pl-1");
        Assert.Null(after!.LastRunAtUtc);
        Assert.Null(after.NextRunAtUtc);
    }

    [Fact]
    public async Task RemovingAPlaylistsScheduleLeavesTheOthersAlone()
    {
        var store = NewStore();
        await store.SaveAsync("plex", "pl-1", PlaylistSyncCadence.Hourly, new[] { "spotify" });
        await store.SaveAsync("plex", "pl-2", PlaylistSyncCadence.Daily, new[] { "deezer" });

        await store.RemoveAsync("plex", "pl-1");

        var entries = await store.LoadAsync();
        Assert.Single(entries);
        Assert.Equal("pl-2", entries[0].SourcePlaylistId);
    }

    // ------------------------------------------------------------------ excluded tracks

    [Fact]
    public async Task ExcludedTracksAreStoredOnTheScheduleNotInAnySharedBlocklist()
    {
        // Excluding a track here is about this playlist's sync only. If it landed in the shared
        // watchlist blocklist, excluding a track from one playlist would quietly stop that track
        // being added anywhere else in the app.
        var store = NewStore();
        var entry = await store.SaveAsync(
            "plex", "pl-1", PlaylistSyncCadence.Hourly, new[] { "spotify" }, new[] { "t1", "t2" });

        Assert.Equal(new[] { "t1", "t2" }, entry.ExcludedTrackIds);

        var reloaded = await NewStore().GetAsync("plex", "pl-1");
        Assert.Equal(new[] { "t1", "t2" }, reloaded!.ExcludedTrackIds);
    }

    [Fact]
    public async Task ExcludedTracksAreDeduplicatedAndOrdered()
    {
        var store = NewStore();
        var entry = await store.SaveAsync(
            "plex", "pl-1", PlaylistSyncCadence.Hourly, new[] { "spotify" }, new[] { "t3", "t1", "t3", " t2 " });

        Assert.Equal(new[] { "t1", "t2", "t3" }, entry.ExcludedTrackIds);
    }

    [Fact]
    public async Task ADuplicateExclusionAcrossPlaylistsDoesNotCrossOver()
    {
        // The property that makes this safe: excluding a track on one playlist must not exclude it
        // on another, because the two syncs are independent decisions.
        var store = NewStore();
        await store.SaveAsync("plex", "pl-1", PlaylistSyncCadence.Hourly, new[] { "spotify" }, new[] { "t1" });

        var other = await store.GetAsync("plex", "pl-2");
        Assert.True(other is null || other.ExcludedTrackIds.Count == 0);
    }

    // ------------------------------------------------------------------ artwork

    [Fact]
    public void AnimatedArtworkIsAccepted()
    {
        // A moving cover is a real thing people want, and both formats are still images as far as a
        // destination's upload endpoint is concerned.
        Assert.True(PlaylistSyncService.IsAllowedMergeArtworkContentType("image/gif"));
        Assert.True(PlaylistSyncService.IsAllowedMergeArtworkContentType("image/webp"));
        Assert.True(PlaylistSyncService.IsAllowedMergeArtworkContentType("image/jpeg"));
        Assert.True(PlaylistSyncService.IsAllowedMergeArtworkContentType("image/png"));
    }

    [Fact]
    public void ANonImageIsRejected()
    {
        Assert.False(PlaylistSyncService.IsAllowedMergeArtworkContentType("image/svg+xml"));
        Assert.False(PlaylistSyncService.IsAllowedMergeArtworkContentType("application/pdf"));
        Assert.False(PlaylistSyncService.IsAllowedMergeArtworkContentType(null));
    }

    [Fact]
    public void TheArtworkCeilingIsFifteenMegabytes()
    {
        Assert.Equal(15 * 1024 * 1024, PlaylistSyncService.MaxArtworkBytes);
    }

    [Fact]
    public void ASquareImageUnderTheLimitIsAccepted()
    {
        var bytes = new byte[2048];
        var dataUrl = "data:image/png;base64," + Convert.ToBase64String(bytes);

        var accepted = PlaylistSyncScheduleStore.ValidateArtwork(dataUrl);

        Assert.NotNull(accepted);
        Assert.StartsWith("data:image/png;base64,", accepted, StringComparison.Ordinal);
    }

    [Fact]
    public void ArtworkOverFifteenMegabytesIsRefused()
    {
        // A real byte count one byte over the ceiling, so the check is proven against the decoded
        // length rather than the encoded string length. A few base64 characters would decode to a
        // handful of bytes and would be accepted, which is correct - the limit is on bytes.
        var oneByteOver = new byte[PlaylistSyncService.MaxArtworkBytes + 1];
        var dataUrl = "data:image/gif;base64," + Convert.ToBase64String(oneByteOver);

        var ex = Assert.Throws<ArgumentException>(
            () => PlaylistSyncScheduleStore.ValidateArtwork(dataUrl));
        Assert.Contains("15 MB", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ArtworkExactlyAtTheCeilingIsAccepted()
    {
        // The boundary itself must pass, or the documented limit would be off by one against the
        // message the user is shown.
        var atCeiling = new byte[PlaylistSyncService.MaxArtworkBytes];
        var dataUrl = "data:image/gif;base64," + Convert.ToBase64String(atCeiling);

        Assert.NotNull(PlaylistSyncScheduleStore.ValidateArtwork(dataUrl));
    }

    [Fact]
    public void AnUnsupportedArtworkTypeIsRefused()
    {
        var svg = "data:image/svg+xml;base64," + Convert.ToBase64String(new byte[] { 1, 2, 3 });

        var ex = Assert.Throws<ArgumentException>(() => PlaylistSyncScheduleStore.ValidateArtwork(svg));
        Assert.Contains("JPEG", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANonDataUrlIsRefused()
    {
        // A remote URL would be a request the app makes on a user's behalf at sync time, and a path
        // could point anywhere on disk. Neither belongs in the schedule file.
        Assert.Throws<ArgumentException>(() => PlaylistSyncScheduleStore.ValidateArtwork("https://example.com/a.png"));
        Assert.Throws<ArgumentException>(() => PlaylistSyncScheduleStore.ValidateArtwork("/etc/passwd"));
    }

    [Fact]
    public void NoArtworkIsNotAnError()
    {
        // A playlist with no cover is normal, so absence must not be read as a bad upload.
        Assert.Null(PlaylistSyncScheduleStore.ValidateArtwork(null));
        Assert.Null(PlaylistSyncScheduleStore.ValidateArtwork("   "));
    }

    [Fact]
    public async Task ArtworkAndDescriptionSurviveASave()
    {
        var store = NewStore();
        var png = "data:image/png;base64," + Convert.ToBase64String(new byte[] { 9, 8, 7 });
        await store.SaveAsync(
            "plex", "pl-1", PlaylistSyncCadence.Hourly, new[] { "spotify" },
            new[] { "t1" }, png, "  A road trip playlist.  ");

        var reloaded = await NewStore().GetAsync("plex", "pl-1");
        Assert.Equal(png, reloaded!.Artwork);
        Assert.Equal("A road trip playlist.", reloaded.Description);
    }

    [Fact]
    public async Task BadArtworkIsRefusedByTheStoreRatherThanPersisted()
    {
        // Rejecting here means a scheduled pass can never wake up holding artwork it cannot use.
        var store = NewStore();
        var svg = "data:image/svg+xml;base64," + Convert.ToBase64String(new byte[] { 1 });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.SaveAsync("plex", "pl-1", PlaylistSyncCadence.Hourly, new[] { "spotify" }, null, svg));

        Assert.Empty(await store.LoadAsync());
    }
}
