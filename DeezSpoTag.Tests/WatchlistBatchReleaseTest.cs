using System;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class WatchlistBatchReleaseTest
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Now.AddHours(2);

    [Fact]
    public void ActiveBatchBlocksTheNextSnapshotEvenAfterSixHours()
    {
        var action = WatchlistRunCoordinator.DecideWaitAction(
            Now,
            Now.AddMinutes(-1),
            batchBusy: true,
            missingTracksRemain: true);

        Assert.Equal(WatchlistWaitAction.Wait, action);
    }

    [Fact]
    public void FinishedImportedBatchReleasesTheNextFiftyBeforeTheSnapshotIsDue()
    {
        var action = WatchlistRunCoordinator.DecideWaitAction(
            Now,
            Later,
            batchBusy: false,
            missingTracksRemain: true);

        Assert.Equal(WatchlistWaitAction.ReleaseNextBatch, action);
    }

    [Fact]
    public void SnapshotStartsOnlyAfterTheCurrentBatchIsFinishedAndImported()
    {
        var waiting = WatchlistRunCoordinator.DecideWaitAction(
            Now,
            Now.AddMinutes(-1),
            batchBusy: true,
            missingTracksRemain: false);
        var ready = WatchlistRunCoordinator.DecideWaitAction(
            Now,
            Now.AddMinutes(-1),
            batchBusy: false,
            missingTracksRemain: true);

        Assert.Equal(WatchlistWaitAction.Wait, waiting);
        Assert.Equal(WatchlistWaitAction.StartSnapshotRun, ready);
    }

    [Fact]
    public void IdleWatchlistWaitsUntilTheSixHourSnapshot()
    {
        var action = WatchlistRunCoordinator.DecideWaitAction(
            Now,
            Later,
            batchBusy: false,
            missingTracksRemain: false);

        Assert.Equal(WatchlistWaitAction.Wait, action);
    }
}
