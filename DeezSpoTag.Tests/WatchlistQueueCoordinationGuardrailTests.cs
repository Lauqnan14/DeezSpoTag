using System;
using System.IO;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class WatchlistQueueCoordinationGuardrailTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Fact]
    public void PlaylistWatchQueue_RespectsWatchlistTrackLimitAsQueueCapacity()
    {
        var source = ReadSource("DeezSpoTag.Web/Services/PlaylistWatchService.cs");

        Assert.Contains("settings.WatchMaxTracksPerPlaylistCheck", source, StringComparison.Ordinal);
        Assert.Contains("GetActiveDownloadCountAsync", source, StringComparison.Ordinal);
        Assert.Contains("queuedCount >= capacity.Value.Remaining", source, StringComparison.Ordinal);
        Assert.Contains("active downloads already meet the watchlist cap", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistWatchQueue_DefersWhenDownloadGateIsPaused()
    {
        var watchSource = ReadSource("DeezSpoTag.Web/Services/PlaylistWatchService.cs");
        var intentSource = ReadSource("DeezSpoTag.Web/Services/DownloadIntentService.cs");

        Assert.Contains("EvaluateDownloadGateAsync", watchSource, StringComparison.Ordinal);
        Assert.Contains("ShouldDeferWatchTrack", watchSource, StringComparison.Ordinal);
        Assert.Contains("download_gate_paused", watchSource, StringComparison.Ordinal);
        Assert.Contains("download_gate_paused", intentSource, StringComparison.Ordinal);
        Assert.DoesNotContain("\"failed\",\r\n                    cancellationToken);\r\n                failedCount++;\r\n                break;", watchSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistWatchQueue_DoesNotDeadlockOnStaleUnfinishedRowsWithoutActiveDownloads()
    {
        var watchSource = ReadSource("DeezSpoTag.Web/Services/PlaylistWatchService.cs");

        Assert.Contains("unfinishedWatchlistCount > 0 && activeWatchlistCount > 0", watchSource, StringComparison.Ordinal);
        Assert.Contains("Continuing queue flow to avoid stale watch deadlock", watchSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistWatchQueue_SetsDeferredWhenTrackIsDeferredByDownloadGate()
    {
        var watchSource = ReadSource("DeezSpoTag.Web/Services/PlaylistWatchService.cs");

        Assert.Contains("var deferred = false;", watchSource, StringComparison.Ordinal);
        Assert.Contains("deferred = true;", watchSource, StringComparison.Ordinal);
        Assert.Contains("new QueueWatchResult(queuedCount, completedCount, failedCount, Deferred: deferred)", watchSource, StringComparison.Ordinal);
    }

    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot, relativePath));
}
