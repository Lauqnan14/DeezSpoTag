using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Covers the platform-level progress and reporting introduced in Stage C.
///
/// The goal is fewer, more meaningful messages: one line per platform rather than one per playlist
/// head request. Critically, this must not be achieved by hiding information -- per-playlist detail
/// has to remain available on cards, in history, and in the logs, and actionable outcomes
/// (failures, truncation) must still surface.
/// </summary>
public sealed class WatchlistPlatformProgressTest
{
    private static string CoordinatorSource()
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "DeezSpoTag.Web", "Services", "WatchlistRunCoordinator.cs"));

    private static string EngineSource()
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "DeezSpoTag.Web", "Services", "WatchlistEngine.cs"));

    private static string RealtimeSource()
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "DeezSpoTag.Web", "Services", "ActivitiesRealtimeService.cs"));

    private static string ControllerSource()
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "DeezSpoTag.Web", "Controllers", "Api", "LibraryPlaylistWatchlistApiController.cs"));

    private static string ScriptSource()
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "DeezSpoTag.Web", "wwwroot", "js", "library-watchlists.js"));

    [Fact]
    public void ProgressIsStructuredRatherThanFreeText()
    {
        var progress = typeof(WatchlistPlatformProgress);
        foreach (var property in new[]
                 {
                     nameof(WatchlistPlatformProgress.Source),
                     nameof(WatchlistPlatformProgress.Label),
                     nameof(WatchlistPlatformProgress.TotalPlaylists),
                     nameof(WatchlistPlatformProgress.HeadsChecked),
                     nameof(WatchlistPlatformProgress.Unchanged),
                     nameof(WatchlistPlatformProgress.Changed),
                     nameof(WatchlistPlatformProgress.New),
                     nameof(WatchlistPlatformProgress.RequiresExpansion),
                     nameof(WatchlistPlatformProgress.Failed),
                     nameof(WatchlistPlatformProgress.Completed),
                     nameof(WatchlistPlatformProgress.Incomplete)
                 })
        {
            Assert.NotNull(progress.GetProperty(property));
        }

        // Timing and lifecycle stamps travel with the structured record.
        Assert.NotNull(progress.GetProperty(nameof(WatchlistPlatformProgress.StartedUtc)));
        Assert.NotNull(progress.GetProperty(nameof(WatchlistPlatformProgress.CompletedUtc)));
        Assert.NotNull(progress.GetProperty(nameof(WatchlistPlatformProgress.HeadPhaseMs)));
        Assert.NotNull(progress.GetProperty(nameof(WatchlistPlatformProgress.TotalMs)));
    }

    private static WatchlistPlatformProgress Progress(
        string source,
        string label,
        int total,
        int checkedCount,
        int unchanged = 0,
        int changed = 0,
        int newCount = 0,
        int requiresExpansion = 0,
        int failed = 0,
        int incomplete = 0)
        => new(
            source,
            label,
            total,
            checkedCount,
            unchanged,
            changed,
            newCount,
            requiresExpansion,
            failed,
            Completed: 0,
            incomplete);

    [Fact]
    public void SummaryReadsAsOneLinePerPlatform()
    {
        var progress = Progress("spotify", "Spotify", 20, 20, unchanged: 18, changed: 2);

        var summary = progress.Summary;
        Assert.Contains("20/20 checked", summary, StringComparison.Ordinal);
        Assert.Contains("2 changed", summary, StringComparison.Ordinal);
        // An all-clear run must not pad the line with zeroes.
        Assert.DoesNotContain("0 failed", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("0 incomplete", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ActionableOutcomesAreAlwaysSurfaced()
    {
        // Failures and truncation are never hidden, however small the counts are.
        var failed = Progress("deezer", "Deezer", 12, 12, failed: 1).Summary;
        Assert.Contains("1 failed", failed, StringComparison.Ordinal);

        var truncated = Progress("apple", "Apple Music", 8, 8, incomplete: 3).Summary;
        Assert.Contains("3 incomplete", truncated, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgressIsCarriedOnTheExistingRuntimeHealthContract()
    {
        // Extended the existing record rather than introducing a parallel status framework.
        Assert.NotNull(typeof(WatchlistRuntimeHealth).GetProperty("Platforms"));
        Assert.NotNull(typeof(WatchlistRuntimeHealth).GetProperty(nameof(WatchlistRuntimeHealth.PlatformProgress)));
    }

    [Fact]
    public void PlatformProgressIsNeverNull()
    {
        // Callers and serializers must not each need their own null handling.
        var health = new WatchlistRuntimeHealth(false, false, null, null, null, 0);
        Assert.NotNull(health.PlatformProgress);
        Assert.Empty(health.PlatformProgress);
    }

    [Fact]
    public void ProgressIsPublishedOncePerPlatformNotPerPlaylist()
    {
        var realtime = RealtimeSource();
        Assert.Contains("PublishWatchlistPlatformProgressChanged", realtime, StringComparison.Ordinal);

        // A single event carrying the whole platform list, not one event per playlist.
        Assert.Equal(
            1,
            realtime.Split("watchlistPlatformProgressChanged", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void ProgressReusesTheExistingRealtimeService()
    {
        var realtime = RealtimeSource();

        // No parallel notification framework was introduced.
        Assert.Contains("IHubContext<ActivitiesHub>", realtime, StringComparison.Ordinal);
        Assert.Contains("PublishAsync(", realtime, StringComparison.Ordinal);
        Assert.DoesNotContain("class ActivitiesRealtimeService2", realtime, StringComparison.Ordinal);
        Assert.DoesNotContain("IHubContext<", realtime.Replace("IHubContext<ActivitiesHub>", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void ProgressIsExposedByTheExistingRuntimeEndpoint()
    {
        var controller = ControllerSource();
        Assert.Contains("platforms = runtime?.PlatformProgress", controller, StringComparison.Ordinal);

        // The UI contract must include the summary plus the actionable counters.
        Assert.Contains("summary = platform.Summary", controller, StringComparison.Ordinal);
        Assert.Contains("failed = platform.Failed", controller, StringComparison.Ordinal);
        Assert.Contains("incomplete = platform.Incomplete", controller, StringComparison.Ordinal);
        Assert.Contains("changed = platform.Changed", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUiRendersOneLinePerPlatform()
    {
        var script = ScriptSource();
        Assert.Contains("runtime?.platforms", script, StringComparison.Ordinal);
        Assert.Contains("platform.label", script, StringComparison.Ordinal);
        Assert.Contains("platform.summary", script, StringComparison.Ordinal);

        // The UI must not iterate playlists to build progress messages.
        Assert.DoesNotContain("items.forEach(item => showToast", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgressReportingCanNeverFailAWatchRun()
    {
        var coordinator = CoordinatorSource();
        Assert.Contains("Publishing Watchlist platform progress failed.", coordinator, StringComparison.Ordinal);
        Assert.Contains("catch (Exception ex) when (ex is not OperationCanceledException)", coordinator, StringComparison.Ordinal);
    }

    [Fact]
    public void DiscoveryProgressCountsAreAggregatedPerPlatform()
    {
        var coordinator = CoordinatorSource();
        Assert.Contains("BuildPlatformProgress", coordinator, StringComparison.Ordinal);
        Assert.Contains("GroupBy(static discovery => discovery.Source", coordinator, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryPlatformHasAReadableLabel()
    {
        foreach (var (source, expected) in new[]
                 {
                     ("spotify", "Spotify"),
                     ("deezer", "Deezer"),
                     ("apple", "Apple Music"),
                     ("tidal", "Tidal"),
                     ("qobuz", "Qobuz"),
                     ("boomplay", "Boomplay")
                 })
        {
            Assert.Equal(expected, WatchlistRunCoordinator.PlatformLabel(source));
        }

        // Unknown and blank sources still produce something printable.
        Assert.Equal("Unknown", WatchlistRunCoordinator.PlatformLabel(string.Empty));
        Assert.Equal("Unknown", WatchlistRunCoordinator.PlatformLabel(null!));
        Assert.Equal("SomeFuturePlatform", WatchlistRunCoordinator.PlatformLabel("SomeFuturePlatform"));
    }

    [Fact]
    public void LabelsAreCaseInsensitive()
    {
        Assert.Equal("Apple Music", WatchlistRunCoordinator.PlatformLabel("APPLE"));
        Assert.Equal("Spotify", WatchlistRunCoordinator.PlatformLabel("  Spotify  "));
    }

    [Fact]
    public void SnapshotTimingIsInstrumented()
    {
        var engine = EngineSource();

        // Per-playlist snapshot timing, so real per-provider cost can be measured later rather than
        // estimated. Reuse, change, completeness and truncation are all reported.
        Assert.Contains("Playlist snapshot phase completed.", engine, StringComparison.Ordinal);
        Assert.Contains("TotalSnapshotMs={TotalSnapshotMs}", engine, StringComparison.Ordinal);
        Assert.Contains("SnapshotReused={SnapshotReused}", engine, StringComparison.Ordinal);
        Assert.Contains("SnapshotChanged={SnapshotChanged}", engine, StringComparison.Ordinal);
        Assert.Contains("SnapshotComplete={SnapshotComplete}", engine, StringComparison.Ordinal);
        Assert.Contains("Truncated={Truncated}", engine, StringComparison.Ordinal);
        Assert.Contains("FetchedTrackCount={FetchedTrackCount}", engine, StringComparison.Ordinal);
    }

    [Fact]
    public void PlatformHeadTimingIsInstrumented()
    {
        var platform = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "DeezSpoTag.Web", "Services", "PlaylistPlatformSnapshotCoordinator.cs"));

        Assert.Contains("HeadPhaseMs={HeadPhaseMs}", platform, StringComparison.Ordinal);
        Assert.Contains("TotalMs={TotalMs}", platform, StringComparison.Ordinal);
        Assert.Contains("Playlists={PlaylistCount}", platform, StringComparison.Ordinal);
    }

    [Fact]
    public void InstrumentationLogsNoCredentials()
    {
        foreach (var source in new[] { EngineSource(), CoordinatorSource(), RealtimeSource() })
        {
            Assert.DoesNotContain("access_token", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Authorization:", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Cookie:", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PerPlaylistHistoryIsStillWritten()
    {
        var engine = EngineSource();

        // Reducing UI noise must not mean dropping per-playlist audit history.
        Assert.Contains("RecordPlaylistSelectionHistoryAsync", engine, StringComparison.Ordinal);
        Assert.Contains("WatchlistHistoryStatus.SkippedAlreadyAvailable", engine, StringComparison.Ordinal);
        Assert.Contains("WatchlistHistoryStatus.SkippedBlocked", engine, StringComparison.Ordinal);
        Assert.Contains("AddPlaylistWatchHistoryAsync", engine, StringComparison.Ordinal);
    }

    [Fact]
    public void PerPlaylistFailureDetailIsStillPersisted()
    {
        var engine = EngineSource();
        var coordinator = CoordinatorSource();

        // Individual failures remain discoverable in playlist state even though the headline is
        // now platform-level. Source failures are raised by the engine; backoff is applied by the
        // coordinator, so both are checked where they actually live.
        Assert.Contains("WatchlistPlaylistState.SourceFailure", engine, StringComparison.Ordinal);
        Assert.Contains("WatchlistPlaylistState.Backoff", coordinator, StringComparison.Ordinal);
        Assert.Contains("FailureFingerprint", engine, StringComparison.Ordinal);
    }

    [Fact]
    public void HistoryRealtimeIsNotSuppressed()
    {
        var realtime = RealtimeSource();

        // The existing per-entry history event must remain: the history page still refreshes live.
        Assert.Contains("PublishWatchlistHistoryChanged", realtime, StringComparison.Ordinal);
        Assert.Contains("watchlistHistoryChanged", realtime, StringComparison.Ordinal);
    }
}
