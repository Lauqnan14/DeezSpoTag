using System;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class WatchlistRunSignalTests
{
    [Fact]
    public async Task PendingWakeReasonsAreCoalescedWithoutLosingWorkType()
    {
        var signal = new WatchlistRunSignal();

        Assert.True(signal.Request(WatchlistWakeReason.TargetSync));
        Assert.False(signal.Request(WatchlistWakeReason.Finalization));

        var reason = await signal.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.True(reason.HasFlag(WatchlistWakeReason.TargetSync));
        Assert.True(reason.HasFlag(WatchlistWakeReason.Finalization));
        Assert.False(reason.HasFlag(WatchlistWakeReason.ScheduledRefresh));
        Assert.False(signal.IsPending);
    }

    [Fact]
    public async Task TimerWakeIsScheduledRefreshOnly()
    {
        var signal = new WatchlistRunSignal();

        var reason = await signal.WaitAsync(TimeSpan.FromMilliseconds(10), CancellationToken.None);

        Assert.Equal(WatchlistWakeReason.ScheduledRefresh, reason);
    }
}
