using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Web.Services;

/// <summary>The playlist a scheduled pass is copying, as the sync service wants it.</summary>
public sealed record PlaylistSyncPassSource(PlaylistWatchlistDto Playlist);

/// <summary>
/// Runs each library playlist's scheduled sync when it comes due.
/// <para>
/// Follows the shape of <c>QualityScannerAutomationHostedService</c>: one long-lived loop, woken
/// either by the next due time or by a schedule change made in the UI. Each pass is serialised
/// through a single gate, because a playlist sync writes to remote services and two overlapping
/// passes over the same playlist would race each other's reads and removals.
/// </para>
/// <para>
/// Nothing here decides WHAT to sync. Every pass calls the same
/// <c>PlaylistSyncService.SyncSinglePlaylistAsync</c> the UI's own Sync button calls, so a scheduled
/// pass and a manual one cannot diverge in behaviour - there is no second code path to keep
/// correct.
/// </para>
/// </summary>
public sealed class PlaylistSyncSchedulerService : BackgroundService
{
    /// <summary>How long to wait before re-checking when nothing is scheduled.</summary>
    private static readonly TimeSpan IdleCheck = TimeSpan.FromMinutes(5);

    /// <summary>The floor on how long to sleep, so a clock change cannot spin the loop.</summary>
    private static readonly TimeSpan MinimumSleep = TimeSpan.FromSeconds(5);

    private readonly PlaylistSyncScheduleStore _store;
    private readonly Func<string, string, CancellationToken, Task<PlaylistSyncPassSource?>> _sourceResolver;
    private readonly Func<string, string, CancellationToken, Task<IReadOnlyList<PlaylistTrackCandidate>>> _candidateResolver;
    private readonly PlaylistSyncService _playlistSyncService;
    private readonly SemaphoreSlim _passGate = new(1, 1);
    private readonly ILogger<PlaylistSyncSchedulerService> _logger;

    public PlaylistSyncSchedulerService(
        PlaylistSyncScheduleStore store,
        Func<string, string, CancellationToken, Task<PlaylistSyncPassSource?>> sourceResolver,
        Func<string, string, CancellationToken, Task<IReadOnlyList<PlaylistTrackCandidate>>> candidateResolver,
        PlaylistSyncService playlistSyncService,
        ILogger<PlaylistSyncSchedulerService> logger)
    {
        _store = store;
        _sourceResolver = sourceResolver;
        _candidateResolver = candidateResolver;
        _playlistSyncService = playlistSyncService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Playlist sync scheduler started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var delay = await RunDuePassesAsync(stoppingToken);
                await SleepAsync(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException
                                       or InvalidOperationException
                                       or TimeoutException
                                       or HttpRequestException
                                       or Microsoft.Data.Sqlite.SqliteException
                                       or UnauthorizedAccessException)
            {
                // The loop outlives any single pass. A provider that is down, a database that is
                // locked, a malformed schedule entry - none of these may stop the scheduler, because
                // then no playlist would ever sync again without a restart. The filter keeps an
                // unexpected fault (a null reference, say) loud instead of turning a real bug into
                // a scheduler that quietly retries forever.
                _logger.LogWarning(ex, "Playlist sync scheduler loop failed; retrying shortly.");
                await SleepAsync(IdleCheck, stoppingToken);
            }
        }

        _logger.LogInformation("Playlist sync scheduler stopped.");
    }

    /// <summary>Runs every pass that is due, and returns how long to wait before the next one.</summary>
    internal async Task<TimeSpan> RunDuePassesAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var entries = await _store.LoadAsync(cancellationToken);

        var due = entries
            .Where(entry => entry.Cadence != PlaylistSyncCadence.Manual
                && entry.Targets.Count > 0
                && entry.NextRunAtUtc is not null
                && entry.NextRunAtUtc <= now)
            .ToList();

        foreach (var entry in due)
        {
            await RunPassAsync(entry, cancellationToken);
        }

        // Wake for the soonest upcoming boundary. A due entry that was just run has had its next
        // time advanced, so it is no longer counted here.
        var upcoming = entries
            .Where(entry => entry.Cadence != PlaylistSyncCadence.Manual && entry.NextRunAtUtc is not null)
            .Select(entry => entry.NextRunAtUtc!.Value)
            .DefaultIfEmpty(now.Add(IdleCheck))
            .Min();

        var wait = upcoming - DateTimeOffset.UtcNow;
        return wait < MinimumSleep ? MinimumSleep : wait;
    }

    private async Task RunPassAsync(PlaylistSyncScheduleEntry entry, CancellationToken cancellationToken)
    {
        // One pass at a time. Two concurrent passes over the same playlist would each read the
        // destination, decide what to remove, and then write - and the second write would be based on
        // a membership the first had already changed.
        if (!await _passGate.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            _logger.LogDebug("A playlist sync pass is already running; {Key} waits for the next tick.", entry.Key);
            return;
        }

        try
        {
            // The playlist is re-read live, exactly as a manual sync does. A schedule stores only the
            // identity, never a snapshot of the contents, so an unattended pass copies what the
            // playlist holds now rather than whatever it held when the schedule was set.
            var source = await _sourceResolver(entry.SourceService, entry.SourcePlaylistId, cancellationToken);
            if (source is null)
            {
                _logger.LogWarning(
                    "Scheduled sync for {Key} skipped: the source playlist could not be read.",
                    entry.Key);
                await _store.RecordRunAsync(
                    entry, false, "The source playlist could not be read.", cancellationToken);
                return;
            }

            var candidates = await _candidateResolver(
                entry.SourceService, entry.SourcePlaylistId, cancellationToken);
            if (candidates.Count == 0)
            {
                // Nothing to copy. Recorded as a pass so the schedule advances instead of retrying a
                // playlist that is empty on every tick.
                await _store.RecordRunAsync(
                    entry, false, "The playlist had no tracks to sync.", cancellationToken);
                return;
            }

            var request = new PlaylistSyncService.PlaylistSingleSyncRequest(
                source.Playlist,
                SourcePreference: null,
                candidates,
                entry.Targets);

            var result = await _playlistSyncService.SyncSinglePlaylistAsync(request, cancellationToken);
            _logger.LogInformation(
                "Scheduled sync for {Key} to [{Targets}]: {Message}",
                entry.Key,
                string.Join(", ", entry.Targets),
                result.Message);

            await _store.RecordRunAsync(entry, result.Success, result.Message, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
                                   or InvalidOperationException
                                   or TimeoutException
                                   or HttpRequestException
                                   or Microsoft.Data.Sqlite.SqliteException
                                   or UnauthorizedAccessException)
        {
            // A pass that throws still has to advance its schedule, or the loop would retry it on
            // every single tick and the destination would see a burst of failed requests.
            _logger.LogWarning(ex, "Scheduled sync for {Key} failed.", entry.Key);
            await _store.RecordRunAsync(
                entry, false, "The scheduled sync failed. See the log for details.", cancellationToken);
        }
        finally
        {
            _passGate.Release();
        }
    }

    /// <summary>
    /// Sleeps for the requested time, or wakes early when the schedule changes. A change made in the
    /// UI should take effect without waiting out a sleep that could be hours long.
    /// </summary>
    private async Task SleepAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var storeChange = _store.WaitForChangeAsync(linked.Token);
        var elapsed = Task.Delay(delay, linked.Token);

        var completed = await Task.WhenAny(elapsed, storeChange);
        if (completed == storeChange)
        {
            // Consume the signal so the next wait is not woken immediately by this one.
            try
            {
                await storeChange;
            }
            catch (OperationCanceledException)
            {
                // Cancelled while waiting: the loop is shutting down.
            }
        }

        linked.Cancel();
    }
}
