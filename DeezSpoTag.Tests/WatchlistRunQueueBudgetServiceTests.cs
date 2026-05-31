using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class WatchlistRunQueueBudgetServiceTests
{
    [Fact]
    public void GetRemaining_WhenNoRunActive_IsUnlimited()
    {
        var service = new WatchlistRunQueueBudgetService();

        Assert.Equal(int.MaxValue, service.GetRemaining());
    }

    [Fact]
    public void BeginRun_TracksRemainingBudget()
    {
        var service = new WatchlistRunQueueBudgetService();
        var token = service.BeginRun(5);

        Assert.Equal(5, service.GetRemaining());
        Assert.Equal(2, service.Consume(2));
        Assert.Equal(3, service.GetRemaining());

        service.EndRun(token);
        Assert.Equal(int.MaxValue, service.GetRemaining());
    }

    [Fact]
    public void EndRun_IgnoresStaleToken()
    {
        var service = new WatchlistRunQueueBudgetService();
        _ = service.BeginRun(4);
        var activeToken = service.BeginRun(6);

        service.EndRun(activeToken - 1);

        Assert.Equal(6, service.GetRemaining());
    }
}
