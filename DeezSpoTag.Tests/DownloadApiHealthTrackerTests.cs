using System;
using DeezSpoTag.Services.Download;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DownloadApiHealthTrackerTests
{
    private static readonly string[] HealthyFallbackSources =
    [
        "tidal|HI_RES_LOSSLESS",
        "deezer|9"
    ];

    [Fact]
    public void PrioritizeSources_MovesCoolingApiOutOfImmediateAutoPath()
    {
        var tracker = new DownloadApiHealthTracker();
        tracker.ReportFailure("qobuz", "provider timeout");
        var sources = new[] { "qobuz|27", "tidal|HI_RES_LOSSLESS", "deezer|9" };

        var prioritized = tracker.PrioritizeSources(sources);

        Assert.Equal(HealthyFallbackSources, prioritized);
    }

    [Fact]
    public void PrioritizeSources_KeepsProtectedEngineEvenWhenCoolingDown()
    {
        var tracker = new DownloadApiHealthTracker();
        tracker.ReportFailure("qobuz", "provider timeout");
        var sources = new[] { "qobuz|27", "tidal|HI_RES_LOSSLESS" };

        var prioritized = tracker.PrioritizeSources(sources, protectedEngine: "qobuz");

        Assert.Equal(sources, prioritized);
    }

    [Fact]
    public void PrioritizeSources_LiftsRecentlySuccessfulApi()
    {
        var tracker = new DownloadApiHealthTracker();
        tracker.ReportSuccess("deezer");
        var sources = new[] { "qobuz|27", "tidal|HI_RES_LOSSLESS", "deezer|9" };

        var prioritized = tracker.PrioritizeSources(sources);

        Assert.Equal("deezer|9", prioritized[0]);
    }

    [Fact]
    public void PrioritizeSources_RestoresApiAfterCooldownWindow()
    {
        var tracker = new DownloadApiHealthTracker();
        tracker.ReportFailure("qobuz", "provider timeout");
        var sources = new[] { "qobuz|27", "tidal|HI_RES_LOSSLESS" };

        var prioritized = tracker.PrioritizeSources(sources, now: DateTimeOffset.UtcNow.AddMinutes(3));

        Assert.Equal(sources, prioritized);
    }

    [Fact]
    public void PrioritizeSources_KeepsOriginalList_WhenEveryApiIsCoolingDown()
    {
        var tracker = new DownloadApiHealthTracker();
        tracker.ReportFailure("qobuz", "provider timeout");
        tracker.ReportFailure("tidal", "provider timeout");
        var sources = new[] { "qobuz|27", "tidal|HI_RES_LOSSLESS" };

        var prioritized = tracker.PrioritizeSources(sources);

        Assert.Equal(sources, prioritized);
    }
}
