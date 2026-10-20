using System.Text.Json;
using System.Diagnostics;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Web.Services;

/// <summary>Versioned, immutable source evidence. Pending work never replaces the applied cache.</summary>
public sealed record PreparedPlaylistSnapshot(
    string Source, string SourceId, IReadOnlyList<PlaylistTrackCandidate> Candidates,
    string? SnapshotId, string? Name, string? Description, string? ImageUrl, int? TrackCount,
    bool IsComplete, bool IsAuthoritativeEmpty, bool CanClearImageUrl, string? OwnerName,
    string? FailureCode, string? FailureIncidentId, bool FailureIsIncidentOrigin,
    int? SourceItemsObserved, string IdentityRevision, string ProviderReadinessRevision,
    DateTimeOffset FetchedAtUtc, bool WasExpanded, int ContractVersion = 1, int SnapshotTrackLimit = 0,
    int? FailureHttpStatusCode = null, DateTimeOffset? RetryAfterUtc = null);

/// <summary>
/// Immutable, per-playlist result of the read-only platform snapshot phase.
/// </summary>
public sealed record PlaylistHeadSnapshot(
    string Source,
    string SourceId,
    string? SnapshotId = null,
    string? Name = null,
    string? Description = null,
    string? ImageUrl = null,
    int? TrackCount = null,
    bool IsAuthoritativeEmpty = false,
    bool CanClearImageUrl = false,
    string? OwnerName = null,
    string? FailureCode = null,
    string? FailureIncidentId = null,
    bool FailureIsIncidentOrigin = true,
    int? FailureHttpStatusCode = null, DateTimeOffset? RetryAfterUtc = null)
{
    /// <summary>
    /// True when the provider answered but the answer is unusable, so no trustworthy change
    /// decision can be made for this playlist.
    /// </summary>
    public bool IsFailed => !string.IsNullOrWhiteSpace(FailureCode);

    public bool HasUsableMetadata =>
        !IsFailed
        && (TrackCount is > 0
            || !string.IsNullOrWhiteSpace(SnapshotId)
            || !string.IsNullOrWhiteSpace(Name)
            || !string.IsNullOrWhiteSpace(ImageUrl));
}

/// <summary>Advisory classification used for progress reporting. Never drives correctness.</summary>
public enum PlaylistHeadChangeState
{
    Unchanged,
    Changed,
    New,
    RequiresExpansion,
    Failed,
    Deferred
}

/// <summary>
/// Outcome of the head phase for one playlist, including timing for the snapshot pipeline.
/// </summary>
public sealed record PlaylistHeadDiscovery(
    string Source,
    string SourceId,
    PlaylistHeadChangeState ChangeState,
    bool RequiresExpansion,
    PlaylistHeadSnapshot? Head,
    string? Error,
    long DurationMs,
    long? PlatformHeadElapsedMs = null)
{
    public static PlaylistHeadDiscovery Failed(string source, string sourceId, string error, long durationMs)
        => new(source, sourceId, PlaylistHeadChangeState.Failed, false, null, error, durationMs);
}

/// <summary>
/// Aggregated lifecycle for one platform's snapshot pass.
/// </summary>
public sealed record PlaylistPlatformSnapshotResult(
    string Source,
    int TotalPlaylists,
    int HeadsChecked,
    int Unchanged,
    int Changed,
    int New,
    int Failed,
    int RequiresExpansion,
    long HeadPhaseMs,
    long TotalMs)
{
    public int Pending => Unchanged + Changed + New + RequiresExpansion;
}

/// <summary>
/// Per-provider head-check concurrency. Kept as one named table so the limits are tunable in a
/// single place rather than scattered through the discovery loop, and deliberately conservative:
/// the goal is predictable bounded work, not maximum throughput.
/// </summary>
public static class PlaylistPlatformSnapshotPolicy
{
    private static readonly IReadOnlyDictionary<string, int> DefaultConcurrency =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["spotify"] = 5,
            ["deezer"] = 5,
            ["apple"] = 3,
            ["tidal"] = 3,
            ["qobuz"] = 3,
            ["boomplay"] = 2
        };

    public const int FallbackConcurrency = 2;
    public const int GlobalSourceConcurrency = 5;
    public const int PreparationsPerSource = 1;

    /// <summary>
    /// Providers without an explicit entry (generated sources such as smart tracklists) use a
    /// conservative fallback rather than an unbounded fan-out.
    /// </summary>
    public static int HeadConcurrencyFor(string source)
        => !string.IsNullOrWhiteSpace(source)
           && DefaultConcurrency.TryGetValue(source.Trim(), out var configured)
            ? configured
            : FallbackConcurrency;

    public static IReadOnlyCollection<string> KnownSources => DefaultConcurrency.Keys.ToList();
}

/// <summary>
/// Phase 1 of the platform-oriented watchlist workflow.
///
/// Streams read-only heads and preparations fairly across providers under shared bounded capacity.
/// The compatibility head-only API still returns immutable results grouped by provider.
///
/// Source work never applies playlist membership or admits downloads. It persists confirmed
/// provider cooldowns immediately and publishes immutable prepared evidence through the caller's
/// bounded durable handoff. Playlist and missing-ledger mutation stays in the ordered consumer.
/// </summary>
public sealed class PlaylistPlatformSnapshotCoordinator
{
    private readonly IServiceProvider _serviceProvider;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly ILogger<PlaylistPlatformSnapshotCoordinator> _logger;
    private readonly TimeProvider _timeProvider;

    public PlaylistPlatformSnapshotCoordinator(
        IServiceProvider serviceProvider,
        DeezSpoTagSettingsService settingsService,
        ILogger<PlaylistPlatformSnapshotCoordinator> logger, TimeProvider? timeProvider = null)
    {
        _serviceProvider = serviceProvider;
        _settingsService = settingsService;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Discovers heads for every supplied playlist, grouped by platform. One playlist's failure is
    /// recorded on that playlist only and never cancels or invalidates its siblings.
    /// </summary>
    public async Task<IReadOnlyList<PlaylistHeadDiscovery>> DiscoverHeadsAsync(
        IReadOnlyList<PlaylistWatchlistDto> playlists,
        Func<string, string, string, Task<bool>>? isSourceCircuitOpen,
        CancellationToken cancellationToken)
    {
        if (playlists.Count == 0)
        {
            return [];
        }

        var ordered = playlists
            .Where(static playlist => playlist is not null)
            .ToList();

        // Group by normalised source while preserving the caller's ordering, so a platform's
        // playlists are visited together without disturbing global priority order.
        var batches = new List<(string Source, List<PlaylistWatchlistDto> Playlists)>();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var playlist in ordered)
        {
            var source = (playlist.Source ?? string.Empty).Trim();
            if (index.TryGetValue(source, out var existing))
            {
                batches[existing].Playlists.Add(playlist);
                continue;
            }

            index[source] = batches.Count;
            batches.Add((source, [playlist]));
        }

        var results = new List<PlaylistHeadDiscovery>(ordered.Count);
        foreach (var (source, batch) in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A platform whose circuit breaker is open costs zero requests, exactly as before.
            if (isSourceCircuitOpen is not null
                && await isSourceCircuitOpen(source, "playlist", string.Empty))
            {
                foreach (var playlist in batch)
                {
                    results.Add(PlaylistHeadDiscovery.Failed(
                        source,
                        (playlist.SourceId ?? string.Empty).Trim(),
                        "Source circuit breaker open.",
                        0));
                }

                continue;
            }

            results.AddRange(await DiscoverPlatformHeadsAsync(source, batch, cancellationToken));
        }

        return results;
    }

    /// <summary>Publish each prepared item immediately; reconciliation never blocks subsequent source reads.</summary>
    public async Task PrepareSnapshotsAsync(
        IReadOnlyList<PlaylistWatchlistDto> playlists,
        Func<string, string, string, Task<bool>> isSourceCircuitOpen,
        Action<IReadOnlyList<PlaylistHeadDiscovery>> headsDiscovered,
        Func<PlaylistWatchlistDto, PreparedPlaylistSnapshot?, Exception?, CancellationToken, Task> publish,
        CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = lifetime.Token;
        var sources = playlists.GroupBy(playlist => playlist.Source.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new SourceWork(group.Key, group.ToList())).ToList();
        var operations = new List<(SourceWork Source, bool Preparation, int Index,
            Task<(PlaylistHeadDiscovery? Head, PreparedPlaylistSnapshot? Snapshot, Exception? Failure)> Task)>();
        var publications = new List<Task>();
        var cursor = 0;
        try
        {
            while (sources.Any(source => source.NextPreparation < source.Playlists.Count)
                   || operations.Count > 0 || publications.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                // Observe settled work before offering another read: in particular, never dispatch
                // past a known throttle just because the serial reconciliation consumer is busy.
                foreach (var publication in publications.Where(task => task.IsCompleted).ToArray())
                { publications.Remove(publication); await publication; }
                foreach (var operation in operations.Where(work => work.Task.IsCompleted).ToArray())
                {
                    if (operation.Preparation && publications.Count >= PlaylistPlatformSnapshotPolicy.GlobalSourceConcurrency) continue;
                    operations.Remove(operation);
                    var result = await operation.Task;
                    var source = operation.Source;
                    if (!operation.Preparation)
                    {
                        source.Heads[operation.Index] = result.Head!;
                        headsDiscovered([result.Head!]);
                        WatchlistPerformanceMeasurement.Current?.Increment(result.Head!.ChangeState switch
                        {
                            PlaylistHeadChangeState.Unchanged => "heads_unchanged",
                            PlaylistHeadChangeState.Changed => "heads_changed",
                            PlaylistHeadChangeState.New => "heads_new",
                            PlaylistHeadChangeState.RequiresExpansion => "heads_requires_expansion",
                            PlaylistHeadChangeState.Deferred => "heads_deferred",
                            _ => "heads_failed"
                        });
                        if (result.Head?.Head?.FailureHttpStatusCode == 429) source.Blocked = true;
                    }
                    else
                    {
                        source.Preparing = false;
                        source.NextPreparation++;
                        if (result.Snapshot?.FailureHttpStatusCode == 429
                            || result.Failure is HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests })
                        {
                            source.Blocked = true;
                            await PersistThrottleAsync(source.Source, result.Snapshot?.RetryAfterUtc, token);
                        }
                        if (result.Snapshot is { WasExpanded: true, Candidates.Count: > 0 })
                            source.NextExpansionUtc = _timeProvider.GetUtcNow().AddSeconds(
                                _settingsService.LoadSettings().WatchDelayBetweenPlaylistsSeconds);
                        publications.Add(publish(source.Playlists[operation.Index], result.Snapshot, result.Failure, token));
                    }
                }

                var launched = false;
                // A rotating cursor offers each represented provider one operation per pass.
                // Within a provider, expansions retain caller priority even if heads finish out of order.
                for (var offered = 0; offered < sources.Count
                     && operations.Count(work => !work.Task.IsCompleted) < PlaylistPlatformSnapshotPolicy.GlobalSourceConcurrency; offered++)
                {
                    var source = sources[cursor];
                    cursor = (cursor + 1) % sources.Count;
                    var active = operations.Count(work => work.Source == source && !work.Task.IsCompleted);
                    if (active >= PlaylistPlatformSnapshotPolicy.HeadConcurrencyFor(source.Source)) continue;
                    var index = source.NextPreparation;
                    var canPrepare = (source.Preparing ? 1 : 0) < PlaylistPlatformSnapshotPolicy.PreparationsPerSource && index < source.Playlists.Count && source.Heads[index] is not null
                        && publications.Count + operations.Count(work => work.Preparation) < PlaylistPlatformSnapshotPolicy.GlobalSourceConcurrency
                        && (source.Blocked || source.Heads[index]!.ChangeState == PlaylistHeadChangeState.Unchanged
                            || _timeProvider.GetUtcNow() >= source.NextExpansionUtc);
                    if (canPrepare)
                    {
                        source.Preparing = true;
                        operations.Add((source, true, index, PrepareOneAsync(source, index)));
                        launched = true;
                    }
                    else if (source.NextHead < source.Playlists.Count)
                    {
                        index = source.NextHead++;
                        operations.Add((source, false, index, HeadAsync(source, index)));
                        launched = true;
                    }
                    // A synchronously completed response may carry a throttle. Settle it now.
                    if (operations.Any(work => work.Task.IsCompleted)) break;
                }
                if (launched || operations.Any(work => work.Task.IsCompleted
                    && (!work.Preparation || publications.Count < PlaylistPlatformSnapshotPolicy.GlobalSourceConcurrency))) continue;
                var waits = operations.Select(work => (Task)work.Task).Where(task => !task.IsCompleted)
                    .Concat(publications).ToList();
                var nextExpansion = sources.Where(source => !source.Preparing && source.NextPreparation < source.Playlists.Count
                        && source.Heads[source.NextPreparation] is not null && source.NextExpansionUtc > _timeProvider.GetUtcNow())
                    .Select(source => (DateTimeOffset?)source.NextExpansionUtc).Min();
                using var waitLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
                if (nextExpansion is { } due)
                    waits.Add(Task.Delay(due - _timeProvider.GetUtcNow() > TimeSpan.Zero ? due - _timeProvider.GetUtcNow() : TimeSpan.Zero,
                        _timeProvider, waitLifetime.Token));
                if (waits.Count > 0) await Task.WhenAny(waits).WaitAsync(token);
                waitLifetime.Cancel();
            }
        }
        catch { lifetime.Cancel(); throw; }
        finally
        {
            // Cancellation owns the underlying calls until they finish unwinding. Never detach
            // source or publication work from this cycle, including on a callback failure.
            lifetime.Cancel();
            try { await Task.WhenAll(operations.Select(work => (Task)work.Task).Concat(publications)); }
            catch (Exception ex) when (ex is not OutOfMemoryException && lifetime.IsCancellationRequested)
            {
                // The dispatcher already observed the original terminal error. Draining cancelled
                // siblings must not replace it with their cancellation exception.
            }
            finally { foreach (var source in sources) source.HeadGate.Dispose(); }
        }

        async Task<(PlaylistHeadDiscovery?, PreparedPlaylistSnapshot?, Exception?)> HeadAsync(SourceWork source, int index)
        {
            var playlist = source.Playlists[index];
            if (source.Blocked || await isSourceCircuitOpen(source.Source, "playlist", playlist.SourceId))
            {
                WatchlistPerformanceMeasurement.Current?.Increment("source_deferrals");
                return (PlaylistHeadDiscovery.Failed(source.Source, playlist.SourceId, "Source circuit breaker open.", 0), null, null);
            }
            var head = await DiscoverOneAsync(source.Source, playlist, source.HeadGate, token,
                (stage, elapsed) => WatchlistPerformanceMeasurement.Current?.Increment("head_cache_" + stage + "_work_ms", elapsed),
                () => source.Blocked, () => source.Blocked = true);
            return (head, null, null);
        }
        async Task<(PlaylistHeadDiscovery?, PreparedPlaylistSnapshot?, Exception?)> PrepareOneAsync(SourceWork source, int index)
        {
            try
            {
                var playlist = source.Playlists[index];
                var head = source.Heads[index]!;
                if (source.Blocked || await isSourceCircuitOpen(source.Source, "playlist", playlist.SourceId))
                    throw new HttpRequestException("Source circuit breaker open.");
                if (head.Error is not null)
                    throw new HttpRequestException(head.Error, null, (System.Net.HttpStatusCode?)head.Head?.FailureHttpStatusCode);
                using var scope = _serviceProvider.CreateScope();
                WatchlistPerformanceMeasurement.Current?.Increment("source_preparations");
                var prepared = await scope.ServiceProvider.GetRequiredService<PlaylistWatchReconciler>()
                    .PreparePlaylistSnapshotAsync(playlist, head.Head, token);
                if (prepared.FailureHttpStatusCode == 429) source.Blocked = true;
                return (null, prepared, null);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return (null, null, ex); }
        }
    }

    private sealed class SourceWork(string source, List<PlaylistWatchlistDto> playlists)
    {
        public string Source { get; } = source;
        public List<PlaylistWatchlistDto> Playlists { get; } = playlists;
        public PlaylistHeadDiscovery?[] Heads { get; } = new PlaylistHeadDiscovery?[playlists.Count];
        public SemaphoreSlim HeadGate { get; } = new(PlaylistPlatformSnapshotPolicy.HeadConcurrencyFor(source));
        public int NextHead;
        public int NextPreparation;
        public bool Preparing;
        public volatile bool Blocked;
        public DateTimeOffset NextExpansionUtc;
    }

    private async Task PersistThrottleAsync(string source, DateTimeOffset? deadline, CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        WatchlistPerformanceMeasurement.Current?.Increment("source_throttles");
        await WatchlistRunCoordinator.PersistSourceThrottleAsync(scope.ServiceProvider.GetRequiredService<LibraryRepository>(),
            source, deadline, _timeProvider.GetUtcNow(), cancellationToken);
    }

    private async Task<IReadOnlyList<PlaylistHeadDiscovery>> DiscoverPlatformHeadsAsync(
        string source,
        IReadOnlyList<PlaylistWatchlistDto> batch,
        CancellationToken cancellationToken)
    {
        var platformTimer = Stopwatch.StartNew();
        var limit = PlaylistPlatformSnapshotPolicy.HeadConcurrencyFor(source);

        _logger.LogInformation(
            "Playlist platform snapshot discovery starting for {Source} with {PlaylistCount} playlist(s) at concurrency {Concurrency}.",
            source,
            batch.Count,
            limit);

        using var gate = new SemaphoreSlim(limit, limit);
        long cacheLookupWorkMs = 0, cacheDeserializeWorkMs = 0;
        void RecordCacheTiming(string stage, long elapsed)
        {
            if (stage == "lookup") Interlocked.Add(ref cacheLookupWorkMs, elapsed);
            else Interlocked.Add(ref cacheDeserializeWorkMs, elapsed);
        }
        var results = new PlaylistHeadDiscovery[batch.Count];
        var next = -1;
        var throttled = 0;
        var workers = Enumerable.Range(0, Math.Min(limit, batch.Count)).Select(_ => ReadHeadsAsync()).ToArray();
        await Task.WhenAll(workers);
        async Task ReadHeadsAsync()
        {
            int index;
            while ((index = Interlocked.Increment(ref next)) < batch.Count)
                results[index] = await DiscoverOneAsync(source, batch[index], gate, cancellationToken, RecordCacheTiming,
                    () => Volatile.Read(ref throttled) != 0, () => Interlocked.Exchange(ref throttled, 1));
        }
        var totalMs = platformTimer.ElapsedMilliseconds;
        results = results.Select(result => result with { PlatformHeadElapsedMs = totalMs }).ToArray();
        var headMs = results.Sum(static result => result.DurationMs);

        var unchangedCount = results.Count(static result => result.ChangeState == PlaylistHeadChangeState.Unchanged);
        var changedCount = results.Count(static result => result.ChangeState == PlaylistHeadChangeState.Changed);
        var newCount = results.Count(static result => result.ChangeState == PlaylistHeadChangeState.New);
        var expansionCount = results.Count(static result => result.ChangeState == PlaylistHeadChangeState.RequiresExpansion);
        var failedCount = results.Count(static result => result.ChangeState == PlaylistHeadChangeState.Failed);
        WatchlistPerformanceMeasurement.Current?.Increment("heads_unchanged", unchangedCount);
        WatchlistPerformanceMeasurement.Current?.Increment("heads_changed", changedCount);
        WatchlistPerformanceMeasurement.Current?.Increment("heads_new", newCount);
        WatchlistPerformanceMeasurement.Current?.Increment("heads_requires_expansion", expansionCount);
        WatchlistPerformanceMeasurement.Current?.Increment("heads_failed", failedCount);

        _logger.LogInformation(
            "Playlist platform snapshot discovery completed for {Source}. Playlists={PlaylistCount} Unchanged={Unchanged} Changed={Changed} New={New} RequiresExpansion={RequiresExpansion} Failed={Failed} HeadPhaseMs={HeadPhaseMs} TotalMs={TotalMs} CacheLookupWorkMs={CacheLookupWorkMs} CacheDeserializeWorkMs={CacheDeserializeWorkMs}",
            new object?[]
            {
                source,
                results.Length,
                unchangedCount,
                changedCount,
                newCount,
                expansionCount,
                failedCount,
                headMs,
                totalMs,
                cacheLookupWorkMs,
                cacheDeserializeWorkMs
            });

        return results;
    }

    private async Task<PlaylistHeadDiscovery> DiscoverOneAsync(
        string source,
        PlaylistWatchlistDto playlist,
        SemaphoreSlim gate,
        CancellationToken cancellationToken,
        Action<string, long> recordCacheTiming,
        Func<bool>? isThrottleKnown = null,
        Action? observeThrottle = null)
    {
        var sourceId = (playlist.SourceId ?? string.Empty).Trim();
        var requestTimer = Stopwatch.StartNew();

        // One playlist's cancellation or failure must never take down the batch. Host shutdown
        // still propagates so a stopping app is not reported as a per-playlist provider failure.
        var acquired = false;
        try
        {
            await gate.WaitAsync(cancellationToken);
            acquired = true;
            WatchlistPerformanceMeasurement.Current?.Increment("head_gate_wait_work_ms", requestTimer.ElapsedMilliseconds);
            requestTimer.Restart();

            using var scope = _serviceProvider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<LibraryRepository>();
            var circuit = await repository.GetWatchlistSourceCircuitStateAsync("playlist", source, cancellationToken);
            if (isThrottleKnown?.Invoke() == true
                || circuit is { IsOpen: true } && (circuit.OpenUntilUtc is null || circuit.OpenUntilUtc > _timeProvider.GetUtcNow()))
                return PlaylistHeadDiscovery.Failed(source, sourceId, "Source circuit breaker open.", 0);
            var engine = scope.ServiceProvider.GetRequiredService<PlaylistWatchReconciler>();
            WatchlistPerformanceMeasurement.Current?.Increment("source_operations");
            var head = await engine.FetchPlaylistHeadAsync(source, sourceId, cancellationToken);
            var durationMs = requestTimer.ElapsedMilliseconds;

            if (head.FailureHttpStatusCode == 429)
            {
                observeThrottle?.Invoke();
                await PersistThrottleAsync(source, head.RetryAfterUtc, cancellationToken);
            }
            if (head.IsFailed)
            {
                return PlaylistHeadDiscovery.Failed(source, sourceId, head.FailureCode ?? "head_fetch_failed", durationMs) with { Head = head };
            }

            var state = await ClassifyAsync(source, sourceId, head, cancellationToken, recordCacheTiming);
            return new PlaylistHeadDiscovery(
                source,
                sourceId,
                state,
                RequiresExpansion: state != PlaylistHeadChangeState.Unchanged,
                head,
                Error: null,
                durationMs);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Contained deliberately: the other playlists on this platform still get their heads.
            _logger.LogWarning(
                ex,
                "Playlist head discovery failed for {Source}:{SourceId}; sibling playlists are unaffected.",
                source,
                sourceId);
            if (ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests })
            {
                observeThrottle?.Invoke();
                await PersistThrottleAsync(source, null, cancellationToken);
                return PlaylistHeadDiscovery.Failed(source, sourceId, "Provider throttled requests (HTTP 429).", requestTimer.ElapsedMilliseconds)
                    with { Head = new PlaylistHeadSnapshot(source, sourceId, FailureCode: "http_429", FailureHttpStatusCode: 429) };
            }
            return PlaylistHeadDiscovery.Failed(
                source,
                sourceId,
                ex.Message,
                requestTimer.ElapsedMilliseconds);
        }
        finally
        {
            if (acquired)
            {
                gate.Release();
            }
        }
    }

    /// <summary>
    /// Advisory classification for progress reporting only. The authoritative change decision
    /// still happens inside reconciliation, which re-reads the candidate cache itself, so a stale
    /// classification here can never cause a wrong reconciliation outcome.
    /// </summary>
    private async Task<PlaylistHeadChangeState> ClassifyAsync(
        string source,
        string sourceId,
        PlaylistHeadSnapshot head,
        CancellationToken cancellationToken,
        Action<string, long> recordCacheTiming)
    {
        if (!head.HasUsableMetadata)
        {
            return PlaylistHeadChangeState.Failed;
        }

        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<LibraryRepository>();
        if (!repository.IsConfigured)
        {
            return PlaylistHeadChangeState.Deferred;
        }

        var cacheTimer = Stopwatch.StartNew();
        PlaylistTrackCandidateCacheDto? cache;
        try { cache = await repository.GetPlaylistTrackCandidateCacheAsync(source, sourceId, cancellationToken); }
        finally { recordCacheTiming("lookup", cacheTimer.ElapsedMilliseconds); }
        if (cache is null)
        {
            return PlaylistHeadChangeState.New;
        }

        // A cache that cannot safely be reused must be regenerated regardless of the snapshot id.
        // The candidate list is re-deserialised here purely to answer "is this cache reusable?";
        // reconciliation still re-reads the cache authoritatively for its own decision.
        if (!PlaylistCandidateContract.IsReusableCache(
                source,
                cache.SchemaVersion,
                ParseForClassification(cache.CandidatesJson),
                head.TrackCount,
                cache.IsComplete,
                cache.SourceItemsObserved))
        {
            return PlaylistHeadChangeState.RequiresExpansion;
        }

        var settings = _settingsService.LoadSettings();
        if (settings.WatchUseSnapshotIdChecking && SupportsStrictSnapshotReuse(source))
        {
            if (string.IsNullOrWhiteSpace(head.SnapshotId))
            {
                return PlaylistHeadChangeState.RequiresExpansion;
            }

            return string.Equals(cache.SnapshotId, head.SnapshotId, StringComparison.Ordinal)
                ? PlaylistHeadChangeState.Unchanged
                : PlaylistHeadChangeState.Changed;
        }

        IReadOnlyList<PlaylistTrackCandidate>? ParseForClassification(string json)
        {
            var parsingTimer = Stopwatch.StartNew();
            try { return SafeParseCandidates(json); }
            finally { recordCacheTiming("parse", parsingTimer.ElapsedMilliseconds); }
        }

        // Without a trustworthy change token, the playlist has to be re-examined.
        return PlaylistHeadChangeState.RequiresExpansion;
    }

    /// <summary>
    /// Mirrors the engine's strict-reuse source set. Only these providers expose a change token
    /// trustworthy enough to skip expansion on an unchanged snapshot.
    /// </summary>
    private static bool SupportsStrictSnapshotReuse(string source)
    {
        return string.Equals(source, "spotify", StringComparison.OrdinalIgnoreCase)
               || string.Equals(source, "deezer", StringComparison.OrdinalIgnoreCase)
               || string.Equals(source, "apple", StringComparison.OrdinalIgnoreCase)
               || string.Equals(source, "boomplay", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<PlaylistTrackCandidate>? SafeParseCandidates(string? candidatesJson)
    {
        if (string.IsNullOrWhiteSpace(candidatesJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<PlaylistTrackCandidate>>(candidatesJson);
        }
        catch (JsonException)
        {
            // Unreadable cache is treated as unusable, which routes the playlist to expansion.
            return null;
        }
    }
}
