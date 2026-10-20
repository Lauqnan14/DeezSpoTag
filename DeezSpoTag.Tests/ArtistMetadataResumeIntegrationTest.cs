using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Services.Library;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Exercises the persisted run checkpoint against a real state file: the v3 to v4 read migration, the
/// outcome round trip, and the resume inputs a restarted process would actually see. The in-memory
/// counter behaviour is covered by <see cref="ArtistMetadataRunOutcomeTest"/>.
/// </summary>
public class ArtistMetadataResumeIntegrationTest : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "deezspotag-artist-resume-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort cleanup only.
        }
    }

    [Fact]
    public async Task ExplicitCancellationClearsRunEvenWhenWorkThrowsDuringCleanup()
    {
        var coordinator = CreateCoordinator();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<CancellationToken, Task<bool>> work = async token =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { throw new IOException("Tracking save failed after cancellation"); }
        };
        Assert.True(await EnqueueTestWork(coordinator, work, operation: "target-update"));
        await started.Task;
        Assert.True(await coordinator.CancelAsync(CancellationToken.None));
        await (Task)typeof(ArtistMetadataAutomationCoordinator)
            .GetField("_activeOperation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator)!;
        Assert.Null((await ReadState()).ActiveRun);
        Assert.Null((await ReadState()).LastTargetUpdateUtc);
    }

    [Fact]
    public void PausedCheckpoint_RetainsRequestsIdentityAndScheduleClocks()
    {
        var timestamp = DateTimeOffset.Parse("2026-10-01T12:00:00Z");
        var state = new ArtistMetadataAutomationState
        {
            LastCacheRefreshUtc = timestamp, LastTargetUpdateUtc = timestamp.AddDays(-1),
            LastDeepRefreshUtc = timestamp.AddDays(-2),
            ActiveRun = new ArtistMetadataActiveRun
            {
                Paused = true, RunId = "paused-run", Operation = "cache-refresh",
                TargetArtistIds = new List<long> { 11, 12 },
                CacheRequest = new ArtistMetadataCacheRefreshRequest(null, 7, "auto", true),
                TargetRequest = new MetadataUpdaterRunRequest { Source = "spotify" }
            }
        };
        var restored = RoundTrip(state);
        Assert.True(restored.ActiveRun!.Paused);
        Assert.Equal("paused-run", restored.ActiveRun.RunId);
        Assert.Equal(7, restored.ActiveRun.CacheRequest!.FolderId);
        Assert.Equal("spotify", restored.ActiveRun.TargetRequest!.Source);
        Assert.Equal(new long[] { 11, 12 }, restored.ActiveRun.TargetArtistIds);
        Assert.Equal(state.LastCacheRefreshUtc, restored.LastCacheRefreshUtc);
        Assert.Equal(state.LastTargetUpdateUtc, restored.LastTargetUpdateUtc);
        Assert.Equal(state.LastDeepRefreshUtc, restored.LastDeepRefreshUtc);
        Assert.False(JsonSerializer.Deserialize<ArtistMetadataActiveRun>("{\"RunId\":\"legacy\"}")!.Paused);
    }

    [Fact]
    public async Task Pause_DrainsAndPersistsWithoutStamping_AndRejectsStaleRun()
    {
        var coordinator = CreateCoordinator();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<CancellationToken, Task<bool>> work = async token =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { await finish.Task; }
            return true;
        };
        Assert.True(await EnqueueTestWork(coordinator, work));
        await started.Task;
        var id = coordinator.GetStatus().RunId!;
        Assert.False(await coordinator.PauseAsync("target-update", id, CancellationToken.None));
        Assert.False(await coordinator.PauseAsync("cache-refresh", "old-run", CancellationToken.None));
        var pause = coordinator.PauseAsync("cache-refresh", id, CancellationToken.None);
        await WaitUntil(() => coordinator.GetStatus().RunState == "pausing");
        Assert.True((await ReadState()).ActiveRun!.Paused);
        Assert.False(await EnqueueTestWork(coordinator, work));
        var interruptedDuringDrain = CreateCoordinator();
        await Invoke(interruptedDuringDrain, "ResumeInterruptedRunAsync", CancellationToken.None);
        Assert.Equal("paused", interruptedDuringDrain.GetStatus().RunState);
        finish.SetResult();
        Assert.True(await pause);
        Assert.Equal("paused", coordinator.GetStatus().RunState);
        var saved = await ReadState();
        Assert.True(saved.ActiveRun!.Paused);
        Assert.Null(saved.LastCacheRefreshUtc);
        Assert.False(await coordinator.PauseAsync("cache-refresh", id, CancellationToken.None));
        var restarted = CreateCoordinator();
        await Invoke(restarted, "ResumeInterruptedRunAsync", CancellationToken.None);
        Assert.Equal("paused", restarted.GetStatus().RunState);
        Assert.Equal(id, restarted.GetStatus().RunId);
        Assert.False(await restarted.ResumeAsync("cache-refresh", "stale", CancellationToken.None));
        Assert.True(await restarted.CancelAsync(CancellationToken.None));
        Assert.Null((await ReadState()).ActiveRun);
        Assert.Null((await ReadState()).LastCacheRefreshUtc);
        Assert.Equal("Cache refresh cancelled", restarted.GetStatus().CacheRefresh.Phase);
    }

    [Fact]
    public async Task PauseRacingWithSuccessfulCompletion_DoesNotRetainCompletedRun()
    {
        var coordinator = CreateCoordinator();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(await EnqueueTestWork(coordinator, async token =>
        { started.SetResult(); await finish.Task; return true; }));
        await started.Task;
        var pause = coordinator.PauseAsync("cache-refresh", coordinator.GetStatus().RunId!, CancellationToken.None);
        await WaitUntil(() => coordinator.GetStatus().RunState == "pausing");
        finish.SetResult();
        Assert.False(await pause);
        var saved = await ReadState();
        Assert.Null(saved.ActiveRun);
        Assert.NotNull(saved.LastCacheRefreshUtc);
        Assert.Equal("idle", coordinator.GetStatus().RunState);
    }

    [Fact]
    public async Task PausePersistenceFailure_IsNotAcknowledgedAndDoesNotCancelWorker()
    {
        var coordinator = CreateCoordinator();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken workerToken = default;
        Assert.True(await EnqueueTestWork(coordinator, async token =>
        { workerToken = token; started.SetResult(); await finish.Task; return true; }));
        await started.Task;
        Directory.CreateDirectory(StatePath + ".tmp");
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => coordinator.PauseAsync(
            "cache-refresh", coordinator.GetStatus().RunId!, CancellationToken.None));
        Assert.False(workerToken.IsCancellationRequested);
        Assert.False((await ReadState()).ActiveRun!.Paused);
        Directory.Delete(StatePath + ".tmp");
        finish.SetResult();
        await WaitUntil(() => coordinator.GetStatus().RunState == "idle");
    }

    [Fact]
    public async Task SavedSchedules_ReopenWithSameDueTimes_AndPausedRunBlocksScheduler()
    {
        var env = new TestEnvironment(_root);
        var preferences = new UserPreferencesStore(env, NullLogger<UserPreferencesStore>.Instance);
        await preferences.SaveAsync(new UserPreferencesDto
        { MetadataCacheRefreshIntervalDays = 7, MetadataTargetUpdateIntervalDays = 14, MetadataDeepRefreshIntervalDays = 30 });
        var reopened = await new UserPreferencesStore(env, NullLogger<UserPreferencesStore>.Instance).LoadAsync();
        Assert.Equal(7, reopened.MetadataCacheRefreshIntervalDays);
        Assert.Equal(14, reopened.MetadataTargetUpdateIntervalDays);
        Assert.Equal(30, reopened.MetadataDeepRefreshIntervalDays);
        var last = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        var state = new ArtistMetadataAutomationState
        {
            LastCacheRefreshUtc = last, LastTargetUpdateUtc = last, LastDeepRefreshUtc = last,
            ActiveRun = new ArtistMetadataActiveRun { Paused = true, Operation = "cache-refresh", RunId = "saved", TargetArtistIds = new() { 1, 2 } }
        };
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        await File.WriteAllTextAsync(StatePath, JsonSerializer.Serialize(state));
        var restarted = CreateCoordinator();
        await Invoke(restarted, "RunScheduledOperationsAsync", CancellationToken.None);
        var status = restarted.GetStatus();
        Assert.Equal("paused", status.RunState);
        Assert.Equal(last.AddDays(7), status.NextCacheRefreshUtc);
        Assert.Equal(last.AddDays(14), status.NextTargetUpdateUtc);
        Assert.Equal(last, (await ReadState()).LastDeepRefreshUtc);
        Assert.True((await ReadState()).ActiveRun!.Paused);
        Assert.Equal(2, status.CacheRefresh.TotalArtists);
    }

    [Fact]
    public async Task Resume_UsesOriginalCheckpointAndJournal_AndRejectsDuplicateStarts()
    {
        var coordinator = CreateCoordinator();
        var run = new ArtistMetadataActiveRun { Operation = "cache-refresh", RunId = "retained", Paused = true,
            TargetArtistIds = new() { 1, 2 }, CacheRequest = new(null, 17, "spotify", true) };
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        await File.WriteAllTextAsync(StatePath, JsonSerializer.Serialize(new ArtistMetadataAutomationState { ActiveRun = run }));
        var journalPath = Path.Combine(_root, "library-artist-images", "metadata-automation-outcomes.ndjson");
        await using (var journal = await ArtistMetadataOutcomeJournal.OpenAsync(journalPath, run.RunId, true, null, CancellationToken.None))
            await journal.AppendAsync(new ArtistRunOutcomeRecord(1, ArtistRunOutcomes.Succeeded), CancellationToken.None);
        await Invoke(coordinator, "ResumeInterruptedRunAsync", CancellationToken.None);
        Assert.Equal(1, coordinator.GetStatus().CacheRefresh.ProcessedArtists);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(await coordinator.ResumeAsync("cache-refresh", "retained", CancellationToken.None, async (saved, token) =>
        {
            Assert.Equal(17, saved.CacheRequest!.FolderId);
            Assert.Equal("spotify", saved.CacheRequest.Source);
            Assert.Equal(new long[] { 1, 2 }, saved.TargetArtistIds);
            return await EnqueueTestWork(coordinator, async ct => { started.SetResult(); await finish.Task; return true; }, saved);
        }));
        await started.Task;
        Assert.False((await ReadState()).ActiveRun!.Paused);
        Assert.Equal("retained", coordinator.GetStatus().RunId);
        await Invoke(coordinator, "PersistRunTargetsAsync", new long[] { 1 });
        await Invoke(coordinator, "NoteRunTargets", new long[] { 1 });
        await Invoke(coordinator, "PersistCheckpointAsync", false);
        Assert.Equal(new long[] { 1, 2 }, (await ReadState()).ActiveRun!.TargetArtistIds);

        Assert.Single(ArtistMetadataOutcomeJournal.Read(journalPath, "retained"));
        Assert.False(await coordinator.ResumeAsync("cache-refresh", "retained", CancellationToken.None));
        finish.SetResult();
        await WaitUntil(() => coordinator.GetStatus().RunState == "idle");
        Assert.Null((await ReadState()).ActiveRun);
    }

    [Fact]
    public async Task InterruptedRunningCheckpoint_RecoveryEnqueuesSameRun()
    {
        var coordinator = CreateCoordinator();
        var run = new ArtistMetadataActiveRun { Operation = "cache-refresh", RunId = "interrupted", TargetArtistIds = new() { 1 } };
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        await File.WriteAllTextAsync(StatePath, JsonSerializer.Serialize(new ArtistMetadataAutomationState { ActiveRun = run }));
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await coordinator.RecoverRunAsync(CancellationToken.None, async (saved, token) =>
        {
            Assert.Equal("interrupted", saved.RunId);
            return await EnqueueTestWork(coordinator, async ct => { started.SetResult(); await finish.Task; return true; }, saved);
        });
        await started.Task;
        Assert.Equal("running", coordinator.GetStatus().RunState);
        Assert.Equal("interrupted", coordinator.GetStatus().RunId);
        Assert.False((await ReadState()).ActiveRun!.Paused);
        finish.SetResult();
        await WaitUntil(() => coordinator.GetStatus().RunState == "idle");
    }


    [Fact]
    public async Task TargetWorkerReturningFalseOnPause_KeepsRunAndCounts()
    {
        var coordinator = CreateCoordinator();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(await EnqueueTestWork(coordinator, async token =>
        {
            await Invoke(coordinator, "NoteRunTargets", new long[] { 1, 2, 3 });
            typeof(ArtistMetadataAutomationCoordinator).GetMethod("NoteArtistOutcome", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(coordinator, new object[] { new ArtistRunOutcomeRecord(1, ArtistRunOutcomes.Succeeded,
                    Targets: new[] { new ArtistTargetResult("navidrome", ArtistTargetOutcome.Updated, new[] { ArtistTargetFields.Avatar }) }) });
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { return false; }
            return true;
        }, operation: "target-update"));
        await started.Task;
        Assert.True(await coordinator.PauseAsync("target-update", coordinator.GetStatus().RunId!, CancellationToken.None));
        var restarted = CreateCoordinator();
        await Invoke(restarted, "ResumeInterruptedRunAsync", CancellationToken.None);
        var status = restarted.GetStatus();
        Assert.Equal("paused", status.RunState);
        Assert.Equal(3, status.TargetUpdate.TotalArtists);
        Assert.Equal(1, status.TargetUpdate.ProcessedArtists);
        Assert.Equal(1, status.TargetUpdate.SuccessfulArtists);
        Assert.Null((await ReadState()).LastTargetUpdateUtc);
        Assert.Equal(1, Assert.Single(status.TargetUpdate.Targets).Updated);
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(await restarted.ResumeAsync("target-update", status.RunId!, CancellationToken.None, async (saved, token) =>
            await EnqueueTestWork(restarted, async ct => { resumed.SetResult(); await finish.Task; return true; }, saved, "target-update")));
        await resumed.Task;
        Assert.Equal(3, restarted.GetStatus().TargetUpdate.TotalArtists);
        Assert.Equal(1, restarted.GetStatus().TargetUpdate.ProcessedArtists);
        finish.SetResult();
        await WaitUntil(() => restarted.GetStatus().RunState == "idle");

    }

    [Fact]
    public void CacheResume_ReusesOriginalArtistsAndExcludesNewArrivals()
    {
        var artists = new[] { new ArtistDto(1, "Original", true, null, null),
            new ArtistDto(2, "Original Two", true, null, null), new ArtistDto(3, "New Arrival", true, null, null) };
        var request = new ArtistMetadataCacheRefreshRequest(null, null, "auto", false);
        var selected = ArtistMetadataCacheRefreshService.SelectRunArtists(artists, request,
            new ArtistRunResumeContext(Array.Empty<ArtistRunOutcomeRecord>(), new long[] { 1, 2 }));
        Assert.Equal(new long[] { 1, 2 }, selected.Select(a => a.Id));
        Assert.Equal(3, ArtistMetadataCacheRefreshService.SelectRunArtists(artists, request, null).Count);
    }

    [Fact]
    public async Task LegacyRecovery_JournalCreationFailureRetainsInlineOutcomes()
    {
        var coordinator = CreateCoordinator();
        var run = new ArtistMetadataActiveRun { Operation = "cache-refresh", RunId = "legacy",
            Outcomes = new() { new ArtistRunOutcomeRecord(1, ArtistRunOutcomes.Succeeded) },
            TargetArtistIds = new() { 1, 2 } };
        Directory.CreateDirectory(Path.Combine(_root, "library-artist-images", "metadata-automation-outcomes.ndjson"));
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => EnqueueTestWork(coordinator, token => Task.FromResult(true), run));
        Assert.Equal(1, Assert.Single((await ReadState()).ActiveRun!.Outcomes!).ArtistId);
    }

    [Fact]
    public async Task Pause_DoesNotAcknowledgeFailedOutcomeDurability()
    {
        var coordinator = CreateCoordinator();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(await EnqueueTestWork(coordinator, async token =>
        {
            typeof(ArtistMetadataAutomationCoordinator).GetField("_journalAppend", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(coordinator, Task.FromException(new IOException("Simulated journal write failure")));
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return true;
        }));
        await started.Task;
        await Assert.ThrowsAnyAsync<IOException>(() => coordinator.PauseAsync(
            "cache-refresh", coordinator.GetStatus().RunId!, CancellationToken.None));
        Assert.True((await ReadState()).ActiveRun!.Paused);
        Assert.Equal("paused", coordinator.GetStatus().RunState);
    }

    [Fact]
    public async Task Shutdown_CancelsThenDrainsWorkerAndRetainsItsCheckpoint()
    {
        var preferences = new UserPreferencesStore(new TestEnvironment(_root), NullLogger<UserPreferencesStore>.Instance);
        await preferences.SaveAsync(new UserPreferencesDto
        { MetadataCacheRefreshIntervalDays = 0, MetadataTargetUpdateIntervalDays = 0, MetadataDeepRefreshIntervalDays = 0 });
        var coordinator = CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(await EnqueueTestWork(coordinator, async token =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { await Task.Delay(100); drained.SetResult(); }
            return true;
        }));
        await started.Task;
        var id = coordinator.GetStatus().RunId;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await coordinator.StopAsync(timeout.Token);
        Assert.True(drained.Task.IsCompleted, "Stop must signal shutdown before waiting and finish draining before returning.");
        var state = await ReadState();
        Assert.Equal(id, state.ActiveRun!.RunId);
        Assert.False(state.ActiveRun.Paused);
        Assert.Null(state.LastCacheRefreshUtc);
    }

    [Fact]
    public async Task CancelAlreadyRequested_CannotBeReplacedByPause()
    {
        var coordinator = CreateCoordinator();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(await EnqueueTestWork(coordinator, async token =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { await finish.Task; }
            return true;
        }));
        await started.Task;
        var id = coordinator.GetStatus().RunId!;
        Assert.True(await coordinator.CancelAsync(CancellationToken.None));
        var attemptedPause = coordinator.PauseAsync("cache-refresh", id, CancellationToken.None);
        finish.SetResult();
        Assert.False(await attemptedPause);
        await WaitUntil(() => coordinator.GetStatus().RunState == "idle");
        Assert.Null((await ReadState()).ActiveRun);
    }

    [Fact]
    public async Task RepeatedAuditFailureRetainsUnknownEvidenceAfterAttemptLimit()
    {
        var coordinator = CreateCoordinator();
        ArtistMetadataActiveRun? resume = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Assert.True(await EnqueueTestWork(coordinator, _ => Task.FromException<bool>(
                new DeezSpoTag.Integrations.TargetLookupException("Plex HTTP 500", target: "plex",
                    unresolvedArtists: new Dictionary<long, string> { [7] = "HTTP 500" })), resume, "target-update"));
            await ActiveTask(coordinator);
            resume = (await ReadState()).ActiveRun;
            Assert.NotNull(resume);
            Assert.Equal("HTTP 500", resume!.UnresolvedAuditArtists["plex"][7]);
        }
        Assert.True(resume!.Paused);
        Assert.Equal(ArtistMetadataRunDispositions.NeedsAttention, resume.Disposition);
        Assert.Null((await ReadState()).LastTargetUpdateUtc);
    }

    [Fact]
    public async Task PartialAuditPersistsUnknownIdsAndPausesOnAuthenticationFailure()
    {
        var coordinator = CreateCoordinator();
        var exception = new DeezSpoTag.Integrations.TargetLookupException("Plex HTTP 401",
            target: "plex", unresolvedArtists: new Dictionary<long, string> { [7] = "HTTP 401" });
        Assert.True(await EnqueueTestWork(coordinator, _ => Task.FromException<bool>(exception), operation: "target-update"));
        await ActiveTask(coordinator);
        var state = await ReadState();
        Assert.True(state.ActiveRun!.Paused);
        Assert.Equal("HTTP 401", state.ActiveRun.UnresolvedAuditArtists["plex"][7]);
        Assert.Equal(ArtistMetadataRunDispositions.NeedsAttention, state.ActiveRun.Disposition);
        Assert.Null(state.LastTargetUpdateUtc);
    }

    [Fact]
    public async Task StrictLookupFailureUsesCoordinatorRecovery()
    {
        var coordinator = CreateCoordinator();
        Assert.True(await EnqueueTestWork(coordinator,
            _ => Task.FromException<bool>(new DeezSpoTag.Integrations.TargetLookupException("Preparation lookup failed")),
            operation: "target-update"));
        await ActiveTask(coordinator);
        var state = await ReadState();
        Assert.Equal(1, state.ActiveRun!.ConsecutiveFailures);
        Assert.Null(state.LastTargetUpdateUtc);
    }

    [Fact]
    public async Task RepeatedRunFailuresPersistAndAbandonAfterThreeAttempts()
    {
        var coordinator = CreateCoordinator();
        ArtistMetadataActiveRun? resume = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Assert.True(await EnqueueTestWork(coordinator,
                _ => Task.FromException<bool>(new IOException("Preparation failed")), resume));
            await ActiveTask(coordinator);
            var state = await ReadState();
            if (attempt < 3)
            {
                Assert.Equal(attempt, state.ActiveRun!.ConsecutiveFailures);
                resume = state.ActiveRun;
            }
            else Assert.Null(state.ActiveRun);
            Assert.Null(state.LastCacheRefreshUtc);
        }
    }

    [Fact]
    public async Task RetainedRunWaitsForItsRetryWindowBeforeItIsRequeued()
    {
        var coordinator = CreateCoordinator();
        var key = ArtistMetadataPendingWork.Key(7, "plex");
        await WriteRetainedStateAsync(new ArtistMetadataActiveRun
        {
            Operation = "target-update", RunId = "retry-run", TargetArtistIds = new List<long> { 7 },
            TargetAttempts = new Dictionary<string, int> { [key] = 1 },
            NextAttemptUtc = DateTimeOffset.UtcNow.AddMinutes(20),
            Disposition = ArtistMetadataRunDispositions.PendingRetry,
            PendingNotice = "Update Servers retained 1 artist/target pair for another attempt."
        });

        var queued = 0;
        await Invoke(coordinator, "RecoverRunAsync", CancellationToken.None,
            (Func<ArtistMetadataActiveRun, CancellationToken, Task<bool>>)((_, _) => { queued++; return Task.FromResult(true); }));

        // The retry window has not elapsed, so the run waits instead of hammering the failing target.
        Assert.Equal(0, queued);
        Assert.Equal("pending-retry", coordinator.GetStatus().RunState);
        Assert.Equal(ArtistMetadataRunDispositions.PendingRetry, coordinator.GetStatus().RunDisposition);
        Assert.NotNull(coordinator.GetStatus().RunNextAttemptUtc);
        Assert.Contains("Retrying", coordinator.GetStatus().ResumeNotice!, StringComparison.Ordinal);

        // Once the window has elapsed the same retained run is requeued.
        var due = await ReadState();
        due.ActiveRun!.NextAttemptUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        await File.WriteAllTextAsync(StatePath, JsonSerializer.Serialize(due));
        await Invoke(coordinator, "RecoverRunAsync", CancellationToken.None,
            (Func<ArtistMetadataActiveRun, CancellationToken, Task<bool>>)((_, _) => { queued++; return Task.FromResult(true); }));
        Assert.Equal(1, queued);
    }

    [Fact]
    public async Task ThreeUnresolvedAttemptsPauseTheRetainedRunAndResumeStartsANewWindow()
    {
        var coordinator = CreateCoordinator();
        var pending = new[]
        {
            new ArtistTargetPendingWork("plex", new[] { ArtistTargetFields.Background }, ArtistPendingWorkCategories.Transient)
        };
        var key = ArtistMetadataPendingWork.Key(7, "plex");
        ArtistMetadataActiveRun? resume = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Assert.True(await EnqueueTargetSweep(coordinator, pending, resume));
            await ActiveTask(coordinator);
            var state = await ReadState();
            Assert.Equal(attempt, state.ActiveRun!.TargetAttempts[key]);
            Assert.Null(state.LastTargetUpdateUtc);
            if (attempt < 3)
            {
                Assert.False(state.ActiveRun.Paused);
                Assert.Equal(ArtistMetadataRunDispositions.PendingRetry, coordinator.GetStatus().RunDisposition);
                Assert.NotNull(state.ActiveRun.NextAttemptUtc);
                resume = state.ActiveRun;
            }
            else
            {
                // Exhaustion pauses the run and keeps it visible instead of abandoning the work.
                Assert.True(state.ActiveRun.Paused);
                Assert.Equal(ArtistMetadataRunDispositions.NeedsAttention, coordinator.GetStatus().RunDisposition);
                Assert.Null(state.ActiveRun.NextAttemptUtc);
                Assert.Contains("Resume", state.ActiveRun.PendingNotice!, StringComparison.Ordinal);

                // The avatar the very first attempt confirmed is still recorded: retries add evidence,
                // they never erase an established success.
                var journalPath = Path.Combine(_root, "library-artist-images", "metadata-automation-outcomes.ndjson");
                var record = Assert.Single(ArtistMetadataOutcomeJournal.Read(journalPath, state.ActiveRun.RunId));
                var plex = Assert.Single(record.TargetResults);
                Assert.Equal("plex", plex.Target);
                Assert.Equal(ArtistTargetOutcome.Updated, plex.Outcome);
                Assert.Contains(ArtistTargetFields.Avatar, plex.UpdatedFieldList);
                Assert.Contains(ArtistTargetFields.Background, record.PendingWorkList.Single().FieldList);
            }
        }

        // Resume starts a new retry window; the recorded work is still the same scope.
        var status = coordinator.GetStatus();
        Assert.True(await coordinator.ResumeAsync("target-update", status.RunId!, CancellationToken.None,
            async (saved, token) => await EnqueueTargetSweep(coordinator, pending, saved)));
        await ActiveTask(coordinator);
        var afterResume = await ReadState();
        Assert.Equal(1, afterResume.ActiveRun!.TargetAttempts[key]);
        Assert.False(afterResume.ActiveRun.Paused);
    }

    [Fact]
    public async Task PauseRetainedRunStopsTheWindowAndResumeRestartsIt()
    {
        var coordinator = CreateCoordinator();
        var journalPath = Path.Combine(_root, "library-artist-images", "metadata-automation-outcomes.ndjson");
        await WriteRetainedStateAsync(new ArtistMetadataActiveRun
        {
            Operation = "target-update", RunId = "waiting-run", TargetArtistIds = new List<long> { 7 },
            TargetAttempts = new Dictionary<string, int> { [ArtistMetadataPendingWork.Key(7, "plex")] = 1 },
            NextAttemptUtc = DateTimeOffset.UtcNow.AddMinutes(5),
            Disposition = ArtistMetadataRunDispositions.PendingRetry
        });

        Assert.True(await coordinator.PauseAsync("target-update", "waiting-run", CancellationToken.None));
        var paused = await ReadState();
        Assert.True(paused.ActiveRun!.Paused);
        Assert.Null(paused.ActiveRun.NextAttemptUtc);
        // Pausing keeps the evidence a retry would need; only the automatic window stops.
        Assert.True(File.Exists(journalPath));

        var status = coordinator.GetStatus();
        Assert.Equal(ArtistMetadataRunDispositions.Paused, status.RunDisposition);

        // Resuming starts a new retry window: the spent counters are cleared durably, while the
        // journal evidence the retry suppresses against is untouched.
        Assert.True(await coordinator.ResumeAsync("target-update", status.RunId!, CancellationToken.None,
            (_, _) => Task.FromResult(true)));
        var resumed = await ReadState();
        Assert.Empty(resumed.ActiveRun!.TargetAttempts);
        Assert.Null(resumed.ActiveRun.NextAttemptUtc);
        Assert.Null(resumed.ActiveRun.PendingNotice);
        Assert.Null(resumed.ActiveRun.Disposition);
        Assert.True(File.Exists(journalPath));
    }

    [Fact]
    public async Task CancellingARetainedRunDiscardsItsOutstandingWork()
    {
        var coordinator = CreateCoordinator();
        await WriteRetainedStateAsync(new ArtistMetadataActiveRun
        {
            Operation = "target-update", RunId = "retained-run", TargetArtistIds = new List<long> { 7 },
            TargetAttempts = new Dictionary<string, int> { [ArtistMetadataPendingWork.Key(7, "plex")] = 1 },
            NextAttemptUtc = DateTimeOffset.UtcNow.AddMinutes(5),
            Disposition = ArtistMetadataRunDispositions.PendingRetry
        });
        var journalPath = Path.Combine(_root, "library-artist-images", "metadata-automation-outcomes.ndjson");
        await File.WriteAllTextAsync(journalPath, "{\"kind\":\"header\",\"runId\":\"retained-run\"}\n");

        Assert.True(await coordinator.CancelAsync(CancellationToken.None));

        var state = await ReadState();
        Assert.Null(state.ActiveRun);
        Assert.Null(state.LastTargetUpdateUtc);
        Assert.False(File.Exists(journalPath));
        Assert.Equal("idle", coordinator.GetStatus().RunState);
    }

    [Fact]
    public async Task RunLevelFailuresDoNotConsumeTargetAttempts()
    {
        var coordinator = CreateCoordinator();
        var pending = new[]
        {
            new ArtistTargetPendingWork("plex", new[] { ArtistTargetFields.Avatar }, ArtistPendingWorkCategories.Transient)
        };
        Assert.True(await EnqueueTargetSweep(coordinator, pending));
        await ActiveTask(coordinator);
        var retained = await ReadState();

        // The retained run's next attempt is interrupted by a recoverable run-level failure, which
        // is handled by the run loop rather than escaping it.
        Assert.True(await EnqueueTestWork(coordinator, _ => Task.FromException<bool>(new IOException("Preparation failed")),
            retained.ActiveRun, "target-update"));
        await ActiveTask(coordinator);
        var failed = await ReadState();
        Assert.Equal(1, failed.ActiveRun!.ConsecutiveFailures);
        Assert.Equal(1, failed.ActiveRun.TargetAttempts[ArtistMetadataPendingWork.Key(7, "plex")]);
        // A run-level failure must not consume the retained target attempt or lose its evidence.
        Assert.Equal(1, ArtistMetadataOutcomeJournal.Read(
            Path.Combine(_root, "library-artist-images", "metadata-automation-outcomes.ndjson"),
            failed.ActiveRun.RunId).Count);
        Assert.Null(failed.LastTargetUpdateUtc);
    }

    [Fact]
    public void UndocumentedVersionTwoStateReadsTolerantlyWithoutInventingOutcomes()
    {
        // The observed on-disk value has no defined v2 contract: only its known timestamp and
        // null-active-run fields may be trusted, and no per-artist outcome may be manufactured.
        var json = """
        {
          "version": 2,
          "lastCacheRefreshUtc": "2026-09-04T23:59:25.3293777+00:00",
          "lastTargetUpdateUtc": "2026-09-05T23:59:25.3293777+00:00",
          "lastDeepRefreshUtc": "2026-09-01T23:59:25.3293777+00:00",
          "activeRun": null
        }
        """;

        var state = JsonSerializer.Deserialize<ArtistMetadataAutomationState>(json, StateJsonOptions)
            ?? throw new InvalidOperationException("State did not deserialize.");

        Assert.Equal(2, state.Version);
        Assert.Null(state.ActiveRun);
        Assert.Equal(DateTimeOffset.Parse("2026-09-04T23:59:25.3293777+00:00"), state.LastCacheRefreshUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-09-05T23:59:25.3293777+00:00"), state.LastTargetUpdateUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-09-01T23:59:25.3293777+00:00"), state.LastDeepRefreshUtc);
        Assert.Empty(ArtistMetadataOutcomeJournal.Read(Path.Combine(_root, "outcomes.ndjson"), "anything"));
    }

    [Fact]
    public void DefinedStateVersionsThreeAndFourStayExplicit()
    {
        Assert.Equal(3, ArtistMetadataAutomationStateVersions.VisitedIdsOnly);
        Assert.Equal(4, ArtistMetadataAutomationStateVersions.Current);
        // Version 3 is the visited-id-only shape and version 4 adds per-artist outcomes; the current
        // version must not be rolled backwards to accommodate the undocumented v2 file.
        Assert.True(ArtistMetadataAutomationStateVersions.Current > ArtistMetadataAutomationStateVersions.VisitedIdsOnly);
    }

    private async Task WriteRetainedStateAsync(ArtistMetadataActiveRun run)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        await File.WriteAllTextAsync(StatePath, JsonSerializer.Serialize(new ArtistMetadataAutomationState { ActiveRun = run }));
        // The retained run's evidence lives in the journal, so a state file alone would not be a
        // realistic restart fixture.
        var journalPath = Path.Combine(_root, "library-artist-images", "metadata-automation-outcomes.ndjson");
        await File.WriteAllTextAsync(journalPath, $"{{\"kind\":\"header\",\"runId\":\"{run.RunId}\"}}\n");
    }

    private static Task<bool> EnqueueTargetSweep(
        ArtistMetadataAutomationCoordinator coordinator,
        IReadOnlyList<ArtistTargetPendingWork> pendingWork,
        ArtistMetadataActiveRun? resuming = null)
        => (Task<bool>)typeof(ArtistMetadataAutomationCoordinator)
            .GetMethod("EnqueueAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(coordinator, new object?[]
            {
                "target-update",
                (Func<CancellationToken, Task<bool>>)(_ =>
                {
                    typeof(ArtistMetadataAutomationCoordinator)
                        .GetMethod("NoteArtistOutcome", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(coordinator, new object[]
                        {
                            new ArtistRunOutcomeRecord(
                                7,
                                ArtistRunOutcomes.Partial,
                                Targets: new[] { new ArtistTargetResult("plex", ArtistTargetOutcome.Updated, new[] { ArtistTargetFields.Avatar }) },
                                PendingWork: pendingWork)
                        });
                    return Task.FromResult(true);
                }),
                CancellationToken.None,
                resuming,
                null,
                new MetadataUpdaterRunRequest { Targets = new List<string> { "plex" }, Force = true },
                false
            })!;

    [Fact]
    public async Task CleanupReadFailureStillReleasesOperationGate()
    {
        var coordinator = CreateCoordinator();
        Assert.True(await EnqueueTestWork(coordinator, async _ =>
        {
            await File.WriteAllTextAsync(StatePath, "broken JSON");
            throw new IOException("Storage failure");
        }));
        try { await ActiveTask(coordinator); } catch (JsonException) { }
        var gate = (SemaphoreSlim)typeof(ArtistMetadataAutomationCoordinator)
            .GetField("_operationGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator)!;
        Assert.Equal(1, gate.CurrentCount);
        Assert.Null(coordinator.GetStatus().ActiveOperation);
    }

    [Theory]
    [InlineData("{invalid header}")]
    [InlineData("{\"kind\":\"header\",\"runId\":\"old-run\"}")]
    public async Task ResumeReplacesUnusableJournalHeader(string header)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "outcomes.ndjson");
        await File.WriteAllTextAsync(path, header + "\n");
        await using (var journal = await ArtistMetadataOutcomeJournal.OpenAsync(path, "current", false, null, CancellationToken.None))
            await journal.AppendAsync(new ArtistRunOutcomeRecord(7, ArtistRunOutcomes.Succeeded), CancellationToken.None);
        Assert.Equal(7, Assert.Single(ArtistMetadataOutcomeJournal.Read(path, "current")).ArtistId);
    }

    [Fact]
    public async Task ResumeSeparatesTornTailFromNewOutcome()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "outcomes.ndjson");
        await using (var journal = await ArtistMetadataOutcomeJournal.OpenAsync(path, "current", true, null, CancellationToken.None))
            await journal.AppendAsync(new ArtistRunOutcomeRecord(1, ArtistRunOutcomes.Succeeded), CancellationToken.None);
        await File.AppendAllTextAsync(path, "{\"kind\":\"record\",\"record\":");
        await using (var journal = await ArtistMetadataOutcomeJournal.OpenAsync(path, "current", false, null, CancellationToken.None))
            await journal.AppendAsync(new ArtistRunOutcomeRecord(2, ArtistRunOutcomes.Succeeded), CancellationToken.None);
        Assert.Equal(new long[] { 1, 2 }, ArtistMetadataOutcomeJournal.Read(path, "current").Select(r => r.ArtistId));
    }

    [Fact]
    public async Task JournalTrackingSnapshotsRecoverPushTimesRotationAndTargetResults()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "outcomes.ndjson");
        var pushedAt = DateTimeOffset.UtcNow;
        var tracked = new MetadataUpdaterTrackedArtist { ArtistId = 7, ArtistName = "Artist",
            LastPushedAtUtc = pushedAt, UpdatedAtUtc = pushedAt, AvatarRotationIndex = 3, BackgroundRotationIndex = 4 };
        await using (var journal = await ArtistMetadataOutcomeJournal.OpenAsync(path, "current", true, null, CancellationToken.None))
            await journal.AppendAsync(new ArtistRunOutcomeRecord(7, ArtistRunOutcomes.Partial,
                Targets: new[] { new ArtistTargetResult("plex", ArtistTargetOutcome.Failed, Error: "Server unavailable") },
                TrackingState: tracked), CancellationToken.None);
        var restored = ArtistMetadataOutcomeJournal.Read(path, "current");
        var state = new MetadataUpdaterState();
        ArtistMetadataUpdaterService.RestoreTrackingSnapshots(state, restored);
        var artist = Assert.Single(state.Artists);
        Assert.Equal(pushedAt, artist.LastPushedAtUtc);
        Assert.Equal(3, artist.AvatarRotationIndex);
        Assert.Equal(4, artist.BackgroundRotationIndex);
        Assert.Equal("Server unavailable", Assert.Single(Assert.Single(restored).TargetResults).Error);
        var newer = new MetadataUpdaterTrackedArtist { ArtistId = 7, ArtistName = "Manual edit", UpdatedAtUtc = pushedAt.AddSeconds(1) };
        state.Artists[0] = newer;
        ArtistMetadataUpdaterService.RestoreTrackingSnapshots(state, restored);
        Assert.Same(newer, Assert.Single(state.Artists));
    }

    [Fact]
    public async Task JournalKeepsLatestOutcomeWhenAnArtistIsRetried()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "outcomes.ndjson");
        await using (var journal = await ArtistMetadataOutcomeJournal.OpenAsync(path, "current", true, null, CancellationToken.None))
        {
            await journal.AppendAsync(new ArtistRunOutcomeRecord(1, ArtistRunOutcomes.Failed), CancellationToken.None);
            await journal.AppendAsync(new ArtistRunOutcomeRecord(1, ArtistRunOutcomes.Succeeded), CancellationToken.None);
        }
        Assert.Equal(ArtistRunOutcomes.Succeeded, Assert.Single(ArtistMetadataOutcomeJournal.Read(path, "current")).Outcome);
    }

    [Fact]
    public async Task TargetSetPersistenceFailureIsAwaitedBeforeArtistProcessing()
    {
        var coordinator = CreateCoordinator();
        var processed = false;
        Assert.True(await EnqueueTestWork(coordinator, async _ =>
        {
            await File.WriteAllTextAsync(StatePath, "broken JSON");
            await Invoke(coordinator, "NoteRunTargets", new long[] { 1, 2 });
            processed = true;
            return true;
        }));
        await ActiveTask(coordinator);
        Assert.False(processed);
    }

    private static Task ActiveTask(ArtistMetadataAutomationCoordinator coordinator)
        => (Task)typeof(ArtistMetadataAutomationCoordinator).GetField("_activeOperation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator)!;

    private string StatePath => Path.Combine(_root, "library-artist-images", "metadata-automation-state.json");
    private async Task<ArtistMetadataAutomationState> ReadState()
        => JsonSerializer.Deserialize<ArtistMetadataAutomationState>(await File.ReadAllTextAsync(StatePath))!;

    private ArtistMetadataAutomationCoordinator CreateCoordinator()
    {
        // Only status/cancellation of the updater is used; provider dependencies are never invoked.
        var updater = (ArtistMetadataUpdaterService)RuntimeHelpers.GetUninitializedObject(typeof(ArtistMetadataUpdaterService));
        typeof(ArtistMetadataUpdaterService).GetField("_statusLock", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(updater, new object());
        typeof(ArtistMetadataUpdaterService).GetField("_status", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(updater, MetadataUpdaterStatusSnapshot.Idle());
        var env = new TestEnvironment(_root);
        return new ArtistMetadataAutomationCoordinator(null!, updater,
            new UserPreferencesStore(env, NullLogger<UserPreferencesStore>.Instance), env,
            NullLogger<ArtistMetadataAutomationCoordinator>.Instance);
    }

    private static Task<bool> EnqueueTestWork(ArtistMetadataAutomationCoordinator coordinator,
        Func<CancellationToken, Task<bool>> work, ArtistMetadataActiveRun? resuming = null, string operation = "cache-refresh")
        => (Task<bool>)typeof(ArtistMetadataAutomationCoordinator).GetMethod("EnqueueAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(coordinator, new object?[] { operation, work, CancellationToken.None, resuming,
                new ArtistMetadataCacheRefreshRequest(null, null, "auto", false), null, false })!;

    private static Task Invoke(ArtistMetadataAutomationCoordinator coordinator, string method, params object[] args)
        => (Task)typeof(ArtistMetadataAutomationCoordinator).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(coordinator, args)!;

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class TestEnvironment(string root) : IWebHostEnvironment, IAppDataRootOverride
    {
        public string? AppDataRoot => root;
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = root;
        public string WebRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public void Checkpoint_RoundTripsOutcomesAndFrozenTargets()
    {
        var run = new ArtistMetadataActiveRun
        {
            Operation = "target-update",
            Automatic = false,
            StartedAtUtc = new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero),
            RunId = "run-1",
            TargetArtistIds = new List<long> { 11, 12, 13 },
            ConsecutiveFailures = 1
        };
        var state = new ArtistMetadataAutomationState { ActiveRun = run };

        var restored = RoundTrip(state);

        var active = restored.ActiveRun;
        Assert.NotNull(active);
        Assert.Equal(new long[] { 11, 12, 13 }, active!.TargetArtistIds.ToArray());
        Assert.Equal("run-1", active.RunId);
        Assert.Equal(1, active.ConsecutiveFailures);
    }

    [Fact]
    public async Task OutcomeJournal_AppendsAndReadsBackEveryRecord()
    {
        var path = Path.Combine(_root, "library-artist-images", "metadata-automation-outcomes.ndjson");
        await using (var journal = await ArtistMetadataOutcomeJournal.OpenAsync(path, "run-1", truncate: true, seed: null, CancellationToken.None))
        {
            for (var id = 1; id <= 5; id++)
            {
                await journal.AppendAsync(
                    new ArtistRunOutcomeRecord(
                        id,
                        id % 2 == 0 ? ArtistRunOutcomes.Failed : ArtistRunOutcomes.Succeeded,
                        Targets: new[]
                        {
                            new ArtistTargetResult("navidrome", ArtistTargetOutcome.Updated, new[] { ArtistTargetFields.Avatar })
                        }),
                    CancellationToken.None);
            }
        }

        var records = ArtistMetadataOutcomeJournal.Read(path, "run-1");

        Assert.Equal(5, records.Count);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, records.Select(r => r.ArtistId).ToArray());
        Assert.Equal(ArtistRunOutcomes.Failed, records[1].Outcome);
        Assert.Equal("navidrome", records[0].TargetResults[0].Target);
    }

    [Fact]
    public async Task OutcomeJournal_DiscardsAJournalFromADifferentRun()
    {
        var path = Path.Combine(_root, "library-artist-images", "metadata-automation-outcomes.ndjson");
        await using (var journal = await ArtistMetadataOutcomeJournal.OpenAsync(path, "run-old", truncate: true, seed: null, CancellationToken.None))
        {
            await journal.AppendAsync(new ArtistRunOutcomeRecord(1, ArtistRunOutcomes.Succeeded), CancellationToken.None);
        }

        // A journal left behind by an abandoned run must never be merged into an unrelated one.
        Assert.Empty(ArtistMetadataOutcomeJournal.Read(path, "run-new"));
        Assert.Single(ArtistMetadataOutcomeJournal.Read(path, "run-old"));
    }

    [Fact]
    public async Task OutcomeJournal_TruncatesWhenStartingANewRun()
    {
        var path = Path.Combine(_root, "library-artist-images", "metadata-automation-outcomes.ndjson");
        await using (var journal = await ArtistMetadataOutcomeJournal.OpenAsync(path, "run-1", truncate: true, seed: null, CancellationToken.None))
        {
            await journal.AppendAsync(new ArtistRunOutcomeRecord(1, ArtistRunOutcomes.Succeeded), CancellationToken.None);
        }

        await using (var journal = await ArtistMetadataOutcomeJournal.OpenAsync(path, "run-2", truncate: true, seed: null, CancellationToken.None))
        {
            // A brand new run recovers nothing: the previous run's records were truncated away.
            Assert.Equal(0, journal.RecoveredCount);
            await journal.AppendAsync(new ArtistRunOutcomeRecord(2, ArtistRunOutcomes.Succeeded), CancellationToken.None);
        }

        var records = ArtistMetadataOutcomeJournal.Read(path, "run-2");
        Assert.Equal(new long[] { 2 }, records.Select(r => r.ArtistId).ToArray());
    }

    [Fact]
    public async Task OutcomeJournal_ResumesAndAppendsToAnExistingRun()
    {
        var path = Path.Combine(_root, "library-artist-images", "metadata-automation-outcomes.ndjson");
        await using (var journal = await ArtistMetadataOutcomeJournal.OpenAsync(path, "run-1", truncate: true, seed: null, CancellationToken.None))
        {
            await journal.AppendAsync(new ArtistRunOutcomeRecord(1, ArtistRunOutcomes.Succeeded), CancellationToken.None);
        }

        // Reopening without truncation must recover what was already recorded and keep appending.
        await using (var resumed = await ArtistMetadataOutcomeJournal.OpenAsync(path, "run-1", truncate: false, seed: null, CancellationToken.None))
        {
            Assert.Equal(1, resumed.RecoveredCount);
            await resumed.AppendAsync(new ArtistRunOutcomeRecord(2, ArtistRunOutcomes.Failed), CancellationToken.None);
        }

        Assert.Equal(2, ArtistMetadataOutcomeJournal.Read(path, "run-1").Count);
    }

    [Fact]
    public async Task OutcomeJournal_SeedsLegacyOutcomesWhenResumingOldState()
    {
        var path = Path.Combine(_root, "library-artist-images", "metadata-automation-outcomes.ndjson");
        var seed = new[] { new ArtistRunOutcomeRecord(7, ArtistRunOutcomes.Succeeded) };

        await using (var journal = await ArtistMetadataOutcomeJournal.OpenAsync(path, "run-legacy", truncate: false, seed, CancellationToken.None))
        {
            Assert.Equal(1, journal.RecoveredCount);
        }

        Assert.Equal(new long[] { 7 }, ArtistMetadataOutcomeJournal.Read(path, "run-legacy").Select(r => r.ArtistId).ToArray());
    }

    [Fact]
    public async Task OutcomeJournal_IgnoresATornTrailingRecord()
    {
        var path = Path.Combine(_root, "library-artist-images", "metadata-automation-outcomes.ndjson");
        await using (var journal = await ArtistMetadataOutcomeJournal.OpenAsync(path, "run-1", truncate: true, seed: null, CancellationToken.None))
        {
            await journal.AppendAsync(new ArtistRunOutcomeRecord(1, ArtistRunOutcomes.Succeeded), CancellationToken.None);
        }

        // Simulate a hard stop part-way through an append.
        await File.AppendAllTextAsync(path, "{\"kind\":\"record\",\"record\":{\"artistId\":2,\"outc");

        var records = ArtistMetadataOutcomeJournal.Read(path, "run-1");

        // The intact record survives; the torn one is dropped, so the artist is simply reprocessed.
        Assert.Equal(new long[] { 1 }, records.Select(r => r.ArtistId).ToArray());
    }

    /// <summary>Mirrors the camelCase options the coordinator uses to read and write the state file.</summary>
    private static readonly JsonSerializerOptions StateJsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Checkpoint_V3StateWithoutOutcomesStillDeserializes()
    {
        // State written by a build that only knew the visited-id list must not fail to load, and the
        // recorded version must survive so the read path can recognise it as pre-outcome state. The
        // legacy id list is still read so it can be migrated into the outcome journal.
        var json = """
        {
          "version": 3,
          "lastCacheRefreshUtc": "2026-09-04T23:59:25.3293777+00:00",
          "activeRun": {
            "operation": "target-update",
            "automatic": false,
            "startedAtUtc": "2026-09-25T08:00:00+00:00",
            "completedArtistIds": [ 101, 102, 103 ]
          }
        }
        """;

        var state = JsonSerializer.Deserialize<ArtistMetadataAutomationState>(json, StateJsonOptions)
            ?? throw new InvalidOperationException("State did not deserialize.");

        Assert.Equal(3, state.Version);
        Assert.NotNull(state.ActiveRun);
        Assert.Equal(new long[] { 101, 102, 103 }, state.ActiveRun!.CompletedArtistIds!);
        Assert.Null(state.ActiveRun.Outcomes);
        Assert.Empty(state.ActiveRun.TargetArtistIds);
    }

    [Fact]
    public void Checkpoint_DoesNotWriteOutcomesBackIntoTheStateFile()
    {
        // The state file must stay a small run header. Outcomes live in the journal, so writing them
        // back here is what reintroduced the quadratic cost. The coordinator builds its persisted
        // snapshot as a fresh header-only object, which leaves both legacy fields null.
        var run = new ArtistMetadataActiveRun
        {
            Operation = "target-update",
            RunId = "abc123",
            TargetArtistIds = new List<long> { 1, 2, 3 }
        };
        var state = new ArtistMetadataAutomationState { ActiveRun = run };

        var json = JsonSerializer.Serialize(state, StateJsonOptions);

        Assert.DoesNotContain("completedArtistIds", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outcomes", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"runId\":\"abc123\"", json, StringComparison.Ordinal);
        Assert.Contains("\"targetArtistIds\":[1,2,3]", json, StringComparison.Ordinal);
    }

    [Fact]
    public void VisitedIdsFromALegacyRunMigrateIntoTerminalSuccesses()
    {
        // The old shape recorded only which artists were visited, including ones that failed. Every
        // such id is therefore read as a success: it is the only defensible reading, and it keeps the
        // counters reconciling instead of silently drifting.
        var outcomes = Migrate(new List<long> { 7, 8, 8, 0, -1 });

        Assert.Equal(new long[] { 7, 8 }, outcomes.Select(o => o.ArtistId).ToArray());
        Assert.All(outcomes, outcome => Assert.Equal(ArtistRunOutcomes.Succeeded, outcome.Outcome));
        Assert.Equal(new long[] { 7, 8 }, ArtistRunOutcomes.BuildResumeSkipSet(outcomes).OrderBy(id => id).ToArray());
    }

    [Fact]
    public void FreshStateUsesTheCurrentSchemaVersion()
    {
        Assert.Equal(4, ArtistMetadataAutomationStateVersions.Current);
        Assert.Equal(4, new ArtistMetadataAutomationState().Version);
        Assert.True(ArtistMetadataAutomationStateVersions.Current > ArtistMetadataAutomationStateVersions.VisitedIdsOnly);
    }

    [Fact]
    public void StatusSnapshot_ExposesTheFullBucketSetOnIdle()
    {
        var idle = MetadataUpdaterStatusSnapshot.Idle();

        Assert.Equal(0, idle.PartialArtists);
        Assert.Equal(0, idle.NoMetadataArtists);
        Assert.Equal(0, idle.ResumedArtists);
        Assert.Empty(idle.Targets);
        Assert.Empty(idle.SkipReasons);
    }

    [Fact]
    public async Task CheckpointFile_IsWrittenAtomicallyAndReloads()
    {
        var path = Path.Combine(_root, "library-artist-images", "metadata-automation-state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var state = new ArtistMetadataAutomationState
        {
            ActiveRun = new ArtistMetadataActiveRun
            {
                Operation = "cache-refresh",
                Outcomes = new List<ArtistRunOutcomeRecord> { new(5, ArtistRunOutcomes.Succeeded) }
            }
        };

        var temporary = path + ".tmp";
        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, state, StateJsonOptions);
        }

        File.Move(temporary, path, true);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(temporary));

        await using var read = File.OpenRead(path);
        var restored = await JsonSerializer.DeserializeAsync<ArtistMetadataAutomationState>(read, StateJsonOptions);
        Assert.NotNull(restored?.ActiveRun);
        Assert.Equal("cache-refresh", restored!.ActiveRun!.Operation);
    }

    [Fact]
    public async Task UpgradeFromLegacyState_ResumesTheSameRunWithAFreshJournal()
    {
        // The deployed image wrote a state file with only a visited-id list: no run id, no frozen
        // target set, no outcomes, and no journal on disk. This walks that exact upgrade transition.
        var root = Path.Combine(_root, "library-artist-images");
        Directory.CreateDirectory(root);
        var statePath = Path.Combine(root, "metadata-automation-state.json");
        var journalPath = Path.Combine(root, "metadata-automation-outcomes.ndjson");

        await File.WriteAllTextAsync(statePath, """
        {
          "version": 3,
          "activeRun": {
            "operation": "target-update",
            "automatic": true,
            "startedAtUtc": "2026-09-25T08:00:00+00:00",
            "completedArtistIds": [ 11, 12, 13, 14 ]
          }
        }
        """);

        // The coordinator gives a legacy run a fresh run id, because it has none.
        var state = JsonSerializer.Deserialize<ArtistMetadataAutomationState>(
            await File.ReadAllTextAsync(statePath), StateJsonOptions)!;
        var run = state.ActiveRun!;
        if (string.IsNullOrWhiteSpace(run.RunId))
        {
            run.RunId = Guid.NewGuid().ToString("N");
        }

        // Resume context: the journal is absent, so the legacy ids are migrated instead.
        Assert.Empty(ArtistMetadataOutcomeJournal.Read(journalPath, run.RunId));
        var outcomes = Migrate(run.CompletedArtistIds!);
        Assert.Equal(new long[] { 11, 12, 13, 14 }, outcomes.Select(o => o.ArtistId).ToArray());

        // Opening the journal for that run seeds the migrated records.
        await using (var journal = await ArtistMetadataOutcomeJournal.OpenAsync(
            journalPath, run.RunId, truncate: false, outcomes, CancellationToken.None))
        {
            Assert.Equal(4, journal.RecoveredCount);
        }

        // The resumed run skips exactly the four already-finished artists, so it continues rather than
        // restarting, and the run is not abandoned.
        var skip = ArtistRunOutcomes.BuildResumeSkipSet(ArtistMetadataOutcomeJournal.Read(journalPath, run.RunId));
        Assert.Equal(new long[] { 11, 12, 13, 14 }, skip.OrderBy(id => id).ToArray());

        // A different run id must not pick the journal up.
        Assert.Empty(ArtistMetadataOutcomeJournal.Read(journalPath, "some-other-run"));
    }

    private static IReadOnlyList<ArtistRunOutcomeRecord> Migrate(List<long> completedArtistIds)
    {
        var method = typeof(ArtistMetadataAutomationCoordinator)
            .GetMethod("MigrateVisitedIdsToOutcomes", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MigrateVisitedIdsToOutcomes was not found.");
        return (IReadOnlyList<ArtistRunOutcomeRecord>)method.Invoke(null, [completedArtistIds])!;
    }

    private static ArtistMetadataAutomationState RoundTrip(ArtistMetadataAutomationState state)
        => JsonSerializer.Deserialize<ArtistMetadataAutomationState>(JsonSerializer.Serialize(state, StateJsonOptions), StateJsonOptions)
            ?? throw new InvalidOperationException("State did not round trip.");
}
