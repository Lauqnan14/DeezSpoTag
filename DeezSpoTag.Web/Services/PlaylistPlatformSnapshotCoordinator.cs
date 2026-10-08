using System.Text.Json;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Web.Services;

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
    bool FailureIsIncidentOrigin = true)
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
    long DurationMs)
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
/// Groups watched playlists by normalised source, fetches every playlist head for that platform
/// under a bounded per-provider concurrency limit, and returns immutable per-playlist results.
/// Provider batches run sequentially so the app never issues an unbounded cross-provider burst.
///
/// This phase is read-only with respect to the database: it performs no writes, no state
/// transitions, and no queue work, which is what makes it safe to run concurrently. All mutation
/// stays in the caller's ordered, serial reconciliation loop.
/// </summary>
public sealed class PlaylistPlatformSnapshotCoordinator
{
    private readonly IServiceProvider _serviceProvider;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly ILogger<PlaylistPlatformSnapshotCoordinator> _logger;

    public PlaylistPlatformSnapshotCoordinator(
        IServiceProvider serviceProvider,
        DeezSpoTagSettingsService settingsService,
        ILogger<PlaylistPlatformSnapshotCoordinator> logger)
    {
        _serviceProvider = serviceProvider;
        _settingsService = settingsService;
        _logger = logger;
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

    private async Task<IReadOnlyList<PlaylistHeadDiscovery>> DiscoverPlatformHeadsAsync(
        string source,
        IReadOnlyList<PlaylistWatchlistDto> batch,
        CancellationToken cancellationToken)
    {
        var platformStartedUtc = DateTimeOffset.UtcNow;
        var limit = PlaylistPlatformSnapshotPolicy.HeadConcurrencyFor(source);

        _logger.LogInformation(
            "Playlist platform snapshot discovery starting for {Source} with {PlaylistCount} playlist(s) at concurrency {Concurrency}.",
            source,
            batch.Count,
            limit);

        using var gate = new SemaphoreSlim(limit, limit);
        var tasks = batch.Select(playlist => DiscoverOneAsync(source, playlist, gate, cancellationToken)).ToList();

        // Faults are contained per playlist inside DiscoverOneAsync, so awaiting them together
        // cannot fail the batch and cannot cancel unrelated playlists.
        var results = await Task.WhenAll(tasks);
        var totalMs = (long)(DateTimeOffset.UtcNow - platformStartedUtc).TotalMilliseconds;
        var headMs = results.Sum(static result => result.DurationMs);

        var unchangedCount = results.Count(static result => result.ChangeState == PlaylistHeadChangeState.Unchanged);
        var changedCount = results.Count(static result => result.ChangeState == PlaylistHeadChangeState.Changed);
        var newCount = results.Count(static result => result.ChangeState == PlaylistHeadChangeState.New);
        var expansionCount = results.Count(static result => result.ChangeState == PlaylistHeadChangeState.RequiresExpansion);
        var failedCount = results.Count(static result => result.ChangeState == PlaylistHeadChangeState.Failed);

        _logger.LogInformation(
            "Playlist platform snapshot discovery completed for {Source}. Playlists={PlaylistCount} Unchanged={Unchanged} Changed={Changed} New={New} RequiresExpansion={RequiresExpansion} Failed={Failed} HeadPhaseMs={HeadPhaseMs} TotalMs={TotalMs}",
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
                totalMs
            });

        return results;
    }

    private async Task<PlaylistHeadDiscovery> DiscoverOneAsync(
        string source,
        PlaylistWatchlistDto playlist,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        var sourceId = (playlist.SourceId ?? string.Empty).Trim();
        var startedUtc = DateTimeOffset.UtcNow;

        // One playlist's cancellation or failure must never take down the batch. Host shutdown
        // still propagates so a stopping app is not reported as a per-playlist provider failure.
        var acquired = false;
        try
        {
            await gate.WaitAsync(cancellationToken);
            acquired = true;

            using var scope = _serviceProvider.CreateScope();
            var engine = scope.ServiceProvider.GetRequiredService<PlaylistWatchReconciler>();
            var head = await engine.FetchPlaylistHeadAsync(source, sourceId, cancellationToken);
            var durationMs = (long)(DateTimeOffset.UtcNow - startedUtc).TotalMilliseconds;

            if (head.IsFailed)
            {
                return PlaylistHeadDiscovery.Failed(source, sourceId, head.FailureCode ?? "head_fetch_failed", durationMs);
            }

            var state = await ClassifyAsync(source, sourceId, head, cancellationToken);
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
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            // Contained deliberately: the other playlists on this platform still get their heads.
            _logger.LogWarning(
                ex,
                "Playlist head discovery failed for {Source}:{SourceId}; sibling playlists are unaffected.",
                source,
                sourceId);
            return PlaylistHeadDiscovery.Failed(
                source,
                sourceId,
                ex.Message,
                (long)(DateTimeOffset.UtcNow - startedUtc).TotalMilliseconds);
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
        CancellationToken cancellationToken)
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

        var cache = await repository.GetPlaylistTrackCandidateCacheAsync(source, sourceId, cancellationToken);
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
                SafeParseCandidates(cache.CandidatesJson),
                head.TrackCount,
                cache.IsComplete))
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
