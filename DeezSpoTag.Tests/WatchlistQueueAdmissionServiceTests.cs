using DeezSpoTag.Web.Services;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class WatchlistQueueAdmissionServiceTests
{
    [Fact]
    public void GetRemaining_WhenNoRunActive_DeniesWatchlistQueueing()
    {
        var service = new WatchlistQueueAdmissionService();

        Assert.Equal(0, service.GetRemaining());
        Assert.False(service.TryReserve(1));
    }

    [Fact]
    public void BeginRun_TracksRemainingBudget()
    {
        var service = new WatchlistQueueAdmissionService();
        var token = service.BeginRun(5);

        Assert.Equal(5, service.GetRemaining());
        Assert.True(service.TryReserve(2));
        Assert.Equal(3, service.GetRemaining());

        service.EndRun(token);
        Assert.Equal(0, service.GetRemaining());
    }

    [Fact]
    public void Release_ReturnsUnusedReservationWithoutExceedingRunLimit()
    {
        var service = new WatchlistQueueAdmissionService();
        _ = service.BeginRun(3);

        Assert.True(service.TryReserve(2));
        service.Release(1);
        service.Release(5);

        Assert.Equal(3, service.GetRemaining());
    }

    [Fact]
    public void TryReserve_DoesNotPartiallyReserveBeyondRemainingBudget()
    {
        var service = new WatchlistQueueAdmissionService();
        _ = service.BeginRun(2);

        Assert.False(service.TryReserve(3));
        Assert.Equal(2, service.GetRemaining());
    }

    [Fact]
    public void TryReserve_ConcurrentCallersCannotExceedRunLimit()
    {
        var service = new WatchlistQueueAdmissionService();
        _ = service.BeginRun(10);
        var reserved = 0;

        Parallel.For(
            0,
            50,
            _ =>
            {
                if (service.TryReserve(1))
                {
                    Interlocked.Increment(ref reserved);
                }
            });

        Assert.Equal(10, reserved);
        Assert.Equal(0, service.GetRemaining());
    }

    [Fact]
    public void EndRun_IgnoresStaleToken()
    {
        var service = new WatchlistQueueAdmissionService();
        _ = service.BeginRun(4);
        var activeToken = service.BeginRun(6);

        service.EndRun(activeToken - 1);

        Assert.Equal(6, service.GetRemaining());
    }

    [Fact]
    public void EmptyBudget_ReturnsRunBudgetDecision()
    {
        var service = new WatchlistQueueAdmissionService();
        var token = service.BeginRun(0);

        var decision = service.TryAdmitTrack();
        Assert.False(decision.Allowed);
        Assert.Equal(WatchQueueStopReason.RunBudget, decision.Reason);

        service.EndRun(token);
    }

    [Fact]
    public void BeginRunIfInactive_OpensBudgetForDirectReconciliationOnlyWhenNoRunOwnsContext()
    {
        var service = new WatchlistQueueAdmissionService();

        var directToken = service.BeginRunIfInactive(2);

        Assert.NotEqual(0, directToken);
        Assert.True(service.TryReserve(1));
        Assert.Equal(1, service.GetRemaining());
        Assert.Equal(0, service.BeginRunIfInactive(5));

        service.EndRun(directToken);
        Assert.Equal(0, service.GetRemaining());
    }

    [Theory]
    [InlineData(true, "online", false, false, true)]
    [InlineData(true, "online", true, true, true)]
    [InlineData(true, "online", true, false, false)]
    [InlineData(true, "offline", false, true, false)]
    [InlineData(true, "degraded", false, true, false)]
    [InlineData(true, "rate_limited", false, true, false)]
    [InlineData(false, "online", false, true, false)]
    public void PublicApiReadiness_UsesOnlyOnlineHealthAndRequiredVerification(
        bool enabled,
        string status,
        bool requiresVerification,
        bool verificationValid,
        bool expected)
    {
        Assert.Equal(
            expected,
            WatchlistPublicApiReadinessService.IsProviderUsable(
                enabled,
                status,
                requiresVerification,
                verificationValid));
    }

}
