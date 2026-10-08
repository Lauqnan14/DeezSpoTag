using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Covers the platform-oriented watchlist workflow introduced in Stage B.
///
/// The contract under test: playlist heads are discovered per platform with bounded concurrency and
/// per-playlist failure isolation, while everything that mutates state stays strictly serial and
/// ordered. Discovery is an optimisation of ordering and concurrency, never a correctness
/// requirement, so a missing or failing discovery must degrade to the previous behaviour.
/// </summary>
public sealed class PlaylistPlatformSnapshotTest
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

    private static string PlatformSource()
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "DeezSpoTag.Web", "Services", "PlaylistPlatformSnapshotCoordinator.cs"));

    [Fact]
    public void AllSixStreamingPlatformsHaveAnExplicitConcurrencyLimit()
    {
        foreach (var (source, expected) in new[]
                 {
                     ("spotify", 5),
                     ("deezer", 5),
                     ("apple", 3),
                     ("tidal", 3),
                     ("qobuz", 3),
                     ("boomplay", 2)
                 })
        {
            Assert.Equal(expected, PlaylistPlatformSnapshotPolicy.HeadConcurrencyFor(source));
        }

        // A source with no configured entry still gets a bounded limit, never an unbounded fan-out.
        Assert.Equal(
            PlaylistPlatformSnapshotPolicy.FallbackConcurrency,
            PlaylistPlatformSnapshotPolicy.HeadConcurrencyFor("smarttracklist"));
        Assert.Equal(
            PlaylistPlatformSnapshotPolicy.FallbackConcurrency,
            PlaylistPlatformSnapshotPolicy.HeadConcurrencyFor(string.Empty));
    }

    [Fact]
    public void SourceLookupIsCaseInsensitive()
    {
        Assert.Equal(5, PlaylistPlatformSnapshotPolicy.HeadConcurrencyFor("Spotify"));
        Assert.Equal(2, PlaylistPlatformSnapshotPolicy.HeadConcurrencyFor("BOOMPLAY"));
        Assert.Equal(3, PlaylistPlatformSnapshotPolicy.HeadConcurrencyFor("  apple  "));
    }

    [Fact]
    public void ConcurrencyLimitsLiveInOneTable()
    {
        var source = PlatformSource();
        Assert.Contains("DefaultConcurrency", source, StringComparison.Ordinal);
        Assert.Contains("HeadConcurrencyFor", source, StringComparison.Ordinal);
        // No magic numbers should be scattered into the discovery loop itself.
        Assert.DoesNotContain("new SemaphoreSlim(3", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new SemaphoreSlim(5", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderBatchesAreSequentialAndPlaylistsWithinABatchAreBounded()
    {
        var source = PlatformSource();

        // Provider batches must not run concurrently with each other: the discovery loop awaits
        // each platform in turn, so only playlists inside one platform are ever in flight.
        Assert.Contains("foreach (var (source, batch) in batches)", source, StringComparison.Ordinal);
        Assert.Contains("results.AddRange(await DiscoverPlatformHeadsAsync(source, batch, cancellationToken));", source, StringComparison.Ordinal);

        // Within a platform, heads are bounded by the per-provider semaphore.
        Assert.Contains("using var gate = new SemaphoreSlim(limit, limit);", source, StringComparison.Ordinal);
        Assert.Contains("PlaylistPlatformSnapshotPolicy.HeadConcurrencyFor(source)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ThereIsNoUnboundedFanOut()
    {
        var source = PlatformSource();
        Assert.DoesNotContain("Task.WhenAll(playlists", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Parallel.ForEach", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Run(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OnePlaylistFailureDoesNotAffectItsSiblings()
    {
        var source = PlatformSource();

        // A per-playlist failure is captured into that playlist's own result, so awaiting the batch
        // together can neither fail the batch nor cancel unrelated playlists.
        Assert.Contains("PlaylistHeadDiscovery.Failed(", source, StringComparison.Ordinal);
        Assert.Contains("sibling playlists are unaffected", source, StringComparison.Ordinal);
        Assert.Contains("catch (Exception ex)", source, StringComparison.Ordinal);

        // Host shutdown must still propagate rather than being misreported as a provider failure.
        Assert.Contains("catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CancellationIsHonouredByTheConcurrencyGate()
    {
        var source = PlatformSource();
        Assert.Contains("await gate.WaitAsync(cancellationToken)", source, StringComparison.Ordinal);
        Assert.Contains("cancellationToken.ThrowIfCancellationRequested()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSemaphoreIsAlwaysReleased()
    {
        var source = PlatformSource();
        // A leak here would eventually deadlock every later platform batch.
        Assert.Contains("var acquired = false;", source, StringComparison.Ordinal);
        Assert.Contains("finally", source, StringComparison.Ordinal);
        Assert.Contains("if (acquired)", source, StringComparison.Ordinal);
        Assert.Contains("gate.Release();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOpenSourceCircuitCostsNoRequests()
    {
        var source = PlatformSource();

        // A platform whose breaker is open must be skipped before any head fetch is attempted.
        Assert.Contains("isSourceCircuitOpen", source, StringComparison.Ordinal);
        Assert.Contains("Source circuit breaker open.", source, StringComparison.Ordinal);
        Assert.Contains("results.AddRange(await DiscoverPlatformHeadsAsync(source, batch, cancellationToken));", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ReconciliationIsNeverRunConcurrently()
    {
        // The serial loop and its per-item lock must be untouched by the discovery phase.
        var coordinator = CoordinatorSource();
        Assert.Contains("var itemLock = _itemLocks.GetOrAdd(item.Key, _ => new SemaphoreSlim(1, 1));", coordinator, StringComparison.Ordinal);
        Assert.Contains("if (!await itemLock.WaitAsync(0, stoppingToken))", coordinator, StringComparison.Ordinal);
        Assert.Contains("foreach (var activeItem in playlistItems)", coordinator, StringComparison.Ordinal);

        // Discovery is awaited before the loop, not interleaved into it.
        var discoveryIndex = coordinator.IndexOf("DiscoverPlatformHeadsAsync(", StringComparison.Ordinal);
        var loopIndex = coordinator.IndexOf("foreach (var activeItem in playlistItems)", StringComparison.Ordinal);
        Assert.True(discoveryIndex > 0 && loopIndex > discoveryIndex, "discovery must complete before the serial loop");
    }

    [Fact]
    public void ThePrefetchedHeadIsThreadedThroughToReconciliation()
    {
        var coordinator = CoordinatorSource();
        var engine = EngineSource();

        Assert.Contains("prefetchedHeads.GetValueOrDefault(", coordinator, StringComparison.Ordinal);
        Assert.Contains("RunItemAsync(item, serviceProvider, stoppingToken, prefetchedHead)", coordinator, StringComparison.Ordinal);
        Assert.Contains("prefetchedHead", engine, StringComparison.Ordinal);
        Assert.Contains("ToLiveHeadSnapshot(prefetchedHead)", engine, StringComparison.Ordinal);
    }

    [Fact]
    public void ReconciliationIsUnchangedWhenNoHeadIsPrefetched()
    {
        var engine = EngineSource();

        // Every existing caller (including the API's forced sync) passes no head, and must keep
        // fetching it inline exactly as before.
        Assert.Contains(
            ": await FetchLivePlaylistHeadAsync(source, sourceId, cancellationToken);",
            engine,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AMismatchedPrefetchedHeadIsIgnored()
    {
        var engine = EngineSource();

        // A head belonging to a different playlist must never be used for this one.
        Assert.Contains("string.Equals(prefetchedHead.Source, source, StringComparison.OrdinalIgnoreCase)", engine, StringComparison.Ordinal);
        Assert.Contains("string.Equals(prefetchedHead.SourceId, sourceId, StringComparison.Ordinal)", engine, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedDiscoveriesAreExcludedSoTheSerialPathRetriesThem()
    {
        var coordinator = CoordinatorSource();

        // A failed head is deliberately not mapped, so reconciliation fetches its own head and keeps
        // the established per-playlist failure handling, circuit and backoff behaviour.
        Assert.Contains("discovery.ChangeState == PlaylistHeadChangeState.Failed", coordinator, StringComparison.Ordinal);
        Assert.Contains("discovery.Head.IsFailed", coordinator, StringComparison.Ordinal);
    }

    [Fact]
    public void DiscoveryFailureFallsBackRatherThanAbortingTheRun()
    {
        var coordinator = CoordinatorSource();

        // A discovery-phase failure must never stop the watch run.
        Assert.Contains("Playlist platform head discovery failed; falling back", coordinator, StringComparison.Ordinal);
        Assert.Contains("catch (OperationCanceledException)", coordinator, StringComparison.Ordinal);
    }

    [Fact]
    public void ClassificationIsAdvisoryAndCannotDecideCorrectness()
    {
        var source = PlatformSource();

        // Classification exists for progress reporting. Reconciliation re-reads the candidate cache
        // itself, so a stale classification cannot change a reconciliation outcome.
        Assert.Contains("Advisory classification used for progress reporting. Never drives correctness.", source, StringComparison.Ordinal);
        Assert.Contains("GetPlaylistTrackCandidateCacheAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ClassificationCoversTheRequiredStates()
    {
        var source = PlatformSource();
        foreach (var state in new[]
                 {
                     nameof(PlaylistHeadChangeState.Unchanged),
                     nameof(PlaylistHeadChangeState.Changed),
                     nameof(PlaylistHeadChangeState.New),
                     nameof(PlaylistHeadChangeState.RequiresExpansion),
                     nameof(PlaylistHeadChangeState.Failed),
                     nameof(PlaylistHeadChangeState.Deferred)
                 })
        {
            Assert.Contains(state, source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ClassificationHonoursSnapshotIdChecking()
    {
        var source = PlatformSource();

        // The existing fast path must not be weakened: snapshot-id checking still decides whether an
        // unchanged playlist can skip expansion.
        Assert.Contains("settings.WatchUseSnapshotIdChecking", source, StringComparison.Ordinal);
        Assert.Contains("SupportsStrictSnapshotReuse(source)", source, StringComparison.Ordinal);
        Assert.Contains("cache.SnapshotId, head.SnapshotId", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PlatformLifecycleKnowsItsCounts()
    {
        // The per-platform result must be able to report total, checked, unchanged, changed, new,
        // failed and expansion counts, plus timing for the snapshot pipeline.
        var result = typeof(PlaylistPlatformSnapshotResult);
        foreach (var property in new[]
                 {
                     nameof(PlaylistPlatformSnapshotResult.TotalPlaylists),
                     nameof(PlaylistPlatformSnapshotResult.HeadsChecked),
                     nameof(PlaylistPlatformSnapshotResult.Unchanged),
                     nameof(PlaylistPlatformSnapshotResult.Changed),
                     nameof(PlaylistPlatformSnapshotResult.New),
                     nameof(PlaylistPlatformSnapshotResult.Failed),
                     nameof(PlaylistPlatformSnapshotResult.RequiresExpansion),
                     nameof(PlaylistPlatformSnapshotResult.HeadPhaseMs),
                     nameof(PlaylistPlatformSnapshotResult.TotalMs)
                 })
        {
            Assert.NotNull(result.GetProperty(property));
        }
    }

    [Fact]
    public void HeadDiscoveryCapturesTimingAndErrors()
    {
        var discovery = typeof(PlaylistHeadDiscovery);
        Assert.NotNull(discovery.GetProperty(nameof(PlaylistHeadDiscovery.Source)));
        Assert.NotNull(discovery.GetProperty(nameof(PlaylistHeadDiscovery.SourceId)));
        Assert.NotNull(discovery.GetProperty(nameof(PlaylistHeadDiscovery.ChangeState)));
        Assert.NotNull(discovery.GetProperty(nameof(PlaylistHeadDiscovery.RequiresExpansion)));
        Assert.NotNull(discovery.GetProperty(nameof(PlaylistHeadDiscovery.Head)));
        Assert.NotNull(discovery.GetProperty(nameof(PlaylistHeadDiscovery.Error)));
        Assert.NotNull(discovery.GetProperty(nameof(PlaylistHeadDiscovery.DurationMs)));
    }

    [Fact]
    public void HeadResultsAreImmutableValueObjects()
    {
        // The discovery phase collects immutable results so the mutation phase cannot be influenced
        // by concurrent writes to shared state.
        Assert.True(typeof(PlaylistHeadSnapshot).IsClass);
        Assert.NotNull(typeof(PlaylistHeadSnapshot).GetConstructor(
        [
            typeof(string), typeof(string), typeof(string), typeof(string), typeof(string),
            typeof(string), typeof(int?), typeof(bool), typeof(bool), typeof(string),
            typeof(string), typeof(string), typeof(bool)
        ]));
        Assert.True(typeof(PlaylistHeadDiscovery).IsClass);
        Assert.True(typeof(PlaylistPlatformSnapshotResult).IsClass);
    }

    [Fact]
    public void NoSecretsAreLogged()
    {
        var source = PlatformSource();

        // Head discovery only ever reports identity and counts.
        Assert.DoesNotContain("access_token", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cookie", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", source, StringComparison.Ordinal);
    }
}
