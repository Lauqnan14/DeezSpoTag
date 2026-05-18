using System;
using DeezSpoTag.Services.Runtime;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class BackgroundWorkCoordinatorTests
{
    [Fact]
    public void NewCoordinator_StartsInGracePeriod()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.Parse("2026-05-18T00:00:00Z"));
        var coordinator = new BackgroundWorkCoordinator(timeProvider);

        Assert.True(coordinator.IsStartupGraceActive);
        Assert.Equal(
            DateTimeOffset.Parse("2026-05-18T00:01:30Z"),
            coordinator.StartupGraceEndsAtUtc);
    }

    [Fact]
    public void StartupGrace_ExpiresAfterConfiguredPeriod()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.Parse("2026-05-18T00:00:00Z"));
        var coordinator = new BackgroundWorkCoordinator(timeProvider);

        timeProvider.Advance(TimeSpan.FromSeconds(91));

        Assert.False(coordinator.IsStartupGraceActive);
    }

    [Fact]
    public void LibraryWatcherDegradedMode_DisablesWatchersTemporarily()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.Parse("2026-05-18T00:00:00Z"));
        var coordinator = new BackgroundWorkCoordinator(timeProvider);

        coordinator.MarkLibraryWatchersDegraded("inotify limit reached");

        var snapshot = coordinator.GetSnapshot();
        Assert.False(coordinator.CanRunLibraryWatchers());
        Assert.True(snapshot.LibraryWatchersDegraded);
        Assert.Equal("inotify limit reached", snapshot.LibraryWatchersDegradedReason);

        timeProvider.Advance(TimeSpan.FromMinutes(31));

        Assert.True(coordinator.CanRunLibraryWatchers());
        Assert.False(coordinator.GetSnapshot().LibraryWatchersDegraded);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public ManualTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan value)
        {
            _utcNow = _utcNow.Add(value);
        }
    }
}
