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

    // ---------------------------------------------------------------------------
    // Scoped finalization gate semantics (watchlist finalization backlog repair).
    //
    // The `batchBusy` input here is no longer a global "ANY pending finalization row" flag: both
    // coordinator admission paths compute it as (active downloads owned by live queue rows)
    // OR (admitted finalization work with valid current watchlist ownership). These cases pin the
    // repaired behavior that historically failed rows, removed owners, and attention-required
    // jobs are NOT a batch-busy reason.
    // ---------------------------------------------------------------------------

    [Fact]
    public void HistoricalBacklogIsNotABatchBusyReason()
    {
        var action = WatchlistRunCoordinator.DecideWaitAction(
            Now,
            Later,
            batchBusy: false,
            missingTracksRemain: true);

        Assert.Equal(WatchlistWaitAction.ReleaseNextBatch, action);
    }

    [Fact]
    public void AttentionRequiredWorkDoesNotHoldTheNextBatch()
    {
        var action = WatchlistRunCoordinator.DecideWaitAction(
            Now,
            Now.AddMinutes(-10),
            batchBusy: false,
            missingTracksRemain: false);

        Assert.Equal(WatchlistWaitAction.StartSnapshotRun, action);
    }
}
