using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Covers the cycle-end target-sync drain cap (WatchlistRunCoordinator.MaxDrainJobsPerCycle) and
/// the per-attempt deadline that keeps a single stalled target from wedging the coordinator.
///
/// The behavioural tests drive the real durable job queue rather than a mock, because the property
/// that matters is a persistence property: a capped pass must leave the remainder durably queued,
/// claimable by a later cycle, with nothing lost and nothing processed twice.
/// </summary>
public sealed class WatchlistTargetSyncDrainCapTest : IAsyncLifetime
{
    private const int DrainCap = WatchlistRunCoordinator.MaxDrainJobsPerCycle;

    private string _tempRoot = string.Empty;
    private string _dbPath = string.Empty;
    private IConfiguration _configuration = default!;
    private LibraryRepository _repository = default!;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-watch-draincap-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
        _dbPath = Path.Join(_tempRoot, "library.db");
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = $"Data Source={_dbPath}",
                ["ConnectionStrings:Queue"] = $"Data Source={Path.Join(_tempRoot, "queue.db")}"
            })
            .Build();
        await new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();
        _repository = NewRepository();
    }

    public Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort test cleanup.
        }

        return Task.CompletedTask;
    }

    [Fact]
    public void DrainCap_IsANamedPositiveConstant()
    {
        // The cap must stay a single named value on the coordinator, not a literal repeated
        // through the drain path, so it can be tuned in one place.
        Assert.Equal(25, DrainCap);
        Assert.True(DrainCap > 0);
    }

    [Fact]
    public void DrainBounded_ClampsToAtLeastOneJob_AndDrainAllStaysUnbounded()
    {
        var bounded = TargetSyncBudget.DrainBounded(WatchlistSyncJobKind.All, 25);
        Assert.Equal(25, bounded.MaxJobs);

        var clamped = TargetSyncBudget.DrainBounded(WatchlistSyncJobKind.All, 0);
        Assert.Equal(1, clamped.MaxJobs);

        // DrainAll is used by the mid-cycle pump, which must keep draining until the cycle ends.
        Assert.Null(TargetSyncBudget.DrainAll(WatchlistSyncJobKind.All).MaxJobs);
    }

    [Fact]
    public async Task PendingJobsUpToTheCap_AreAllClaimableInOnePass()
    {
        var total = DrainCap;
        var ids = await SeedJobsAsync(total);

        var claimed = await ClaimAsync(total);

        Assert.Equal(total, claimed.Count);
        Assert.Equal(ids.Count, claimed.Count);
        Assert.Empty(claimed.Except(ids));
    }

    [Fact]
    public async Task DrainBeyondTheCap_LeavesTheRemainderDurablyQueued()
    {
        var total = DrainCap + 7;
        var ids = await SeedJobsAsync(total);

        // One bounded pass claims at most the cap; the cap is checked before claiming, so a job
        // is never left in 'processing' with a live lease waiting for lease expiry.
        var claimed = await ClaimAsync(DrainCap);

        Assert.Equal(DrainCap, claimed.Count);

        var counts = await _repository.GetWatchlistSyncJobStatusCountsAsync();
        Assert.Equal(DrainCap, counts.Processing);
        Assert.Equal(total, counts.Due + counts.Processing);

        // Nothing was discarded, failed, or blocked by reaching the cap.
        Assert.Equal(0, counts.Blocked);
        Assert.Equal(0, counts.RetryWaiting);
    }

    [Fact]
    public async Task NextCycle_ResumesTheRemainingJobs()
    {
        var total = DrainCap + 7;
        await SeedJobsAsync(total);

        var firstPass = await ClaimAsync(DrainCap);
        var secondPass = await ClaimAsync(DrainCap);

        Assert.Equal(DrainCap, firstPass.Count);
        Assert.Equal(7, secondPass.Count);

        // The second pass picks up exactly the work the first left behind.
        Assert.Empty(firstPass.Intersect(secondPass));
    }

    [Fact]
    public async Task RepeatedCappedPasses_DrainTheWholeBacklogWithoutLossOrDuplication()
    {
        var total = (DrainCap * 2) + 3;
        var seeded = await SeedJobsAsync(total);

        var processed = new List<long>();
        for (var pass = 0; pass < 4 && processed.Count < total; pass++)
        {
            processed.AddRange(await ClaimAsync(DrainCap));
        }

        Assert.Equal(total, processed.Count);
        // No job handled twice across cycle boundaries.
        Assert.Equal(processed.Count, processed.Distinct().Count());
        Assert.Empty(seeded.Except(processed));
    }

    [Fact]
    public async Task ACappedPassIsNotAFailure_AndLeavesNoStrandedProcessingRows()
    {
        await SeedJobsAsync(DrainCap + 4);
        _ = await ClaimAsync(DrainCap);

        // Reached-cap must not be reported as an error state: nothing is blocked, and no row is
        // left mid-flight by the cap itself.
        var counts = await _repository.GetWatchlistSyncJobStatusCountsAsync();
        Assert.Equal(0, counts.Blocked);
        Assert.Equal(0, counts.ExpiredProcessing);
        Assert.Equal(DrainCap, counts.Processing);
    }

    [Fact]
    public async Task AFailedJob_DoesNotStopTheRestOfThePass()
    {
        await SeedJobsAsync(6);

        var claimed = await ClaimAsync(6);
        Assert.Equal(6, claimed.Count);

        // Simulate the first job failing: it is returned to a claimable state, and the other five
        // were still processed in the same pass. A single bad job cannot wedge or truncate a pass.
        var failing = claimed[0];
        Assert.True(await _repository.RetryWatchlistSyncJobAsync(
            failing,
            "test-owner",
            1,
            DateTimeOffset.UtcNow,
            "synthetic failure"));

        foreach (var succeeded in claimed.Skip(1))
        {
            Assert.True(await _repository.CompleteWatchlistSyncJobAsync(succeeded, "test-owner"));
        }

        var requeued = Assert.Single(await _repository.ClaimDueWatchlistSyncJobsAsync(
            10,
            TimeSpan.FromMinutes(15),
            "second-pass"));
        Assert.Equal(failing, requeued.Id);
    }

    [Fact]
    public void DrainChecksItsCapBeforeClaiming_SoNoJobIsStrandedMidFlight()
    {
        var source = ReadServiceSource();

        var capIndex = source.IndexOf("budget.MaxJobs is { } maxJobs && processed >= maxJobs", StringComparison.Ordinal);
        var claimIndex = source.IndexOf("ClaimDueWatchlistSyncJobsAsync(", StringComparison.Ordinal);

        Assert.True(capIndex > 0, "drain must check its job cap");
        Assert.True(claimIndex > 0, "drain must claim jobs");
        // Ordering is the correctness point: claiming first and breaking after would leave the
        // job that crossed the cap in 'processing' with a live lease, invisible until expiry.
        Assert.True(capIndex < claimIndex, "the cap must be checked before a job is claimed");
    }

    [Fact]
    public void EveryTargetSyncAttempt_IsDeadlineBounded()
    {
        var source = ReadServiceSource();

        // A target that accepts the connection and then stalls must not be able to hold cycle
        // teardown open, so each attempt runs under its own deadline.
        Assert.Contains("TargetSyncAttemptDeadline", source, StringComparison.Ordinal);
        Assert.Contains("CancelAfter(TargetSyncAttemptDeadline)", source, StringComparison.Ordinal);
        // A deadline firing is a retryable transport failure, so the job is retried and the
        // target circuit breaker counts it instead of the job silently vanishing.
        Assert.Contains("SyncFailureClass.Transport", source, StringComparison.Ordinal);
        // Host shutdown must still propagate rather than being reported as a deadline.
        Assert.Contains("when (!cancellationToken.IsCancellationRequested)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DrainIsBoundedByJobCount_NotByWallClock()
    {
        var serviceSource = ReadServiceSource();
        var coordinatorSource = ReadCoordinatorSource();

        // The user-visible contract is determinism: a fixed amount of cycle-end work regardless of
        // machine speed or provider latency. A wall-clock budget would reintroduce variance.
        Assert.Contains("MaxDrainJobsPerCycle = 25", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("DrainBounded", coordinatorSource, StringComparison.Ordinal);
        Assert.DoesNotContain("TimeBudget", serviceSource, StringComparison.Ordinal);
        Assert.DoesNotContain("timeBudget", serviceSource, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadServiceSource()
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "DeezSpoTag.Web", "Services", "WatchlistPostDownloadSyncService.cs"));

    private static string ReadCoordinatorSource()
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "DeezSpoTag.Web", "Services", "WatchlistRunCoordinator.cs"));

    /// <summary>
    /// Claims jobs one at a time, mirroring the real drain loop's claim-per-iteration shape, and
    /// returns the claimed ids.
    /// </summary>
    private async Task<IReadOnlyList<long>> ClaimAsync(int maxToClaim)
    {
        var claimed = new List<long>();
        for (var i = 0; i < maxToClaim; i++)
        {
            var batch = await _repository.ClaimDueWatchlistSyncJobsAsync(
                1,
                TimeSpan.FromMinutes(15),
                "test-owner");
            if (batch.Count == 0)
            {
                break;
            }

            claimed.AddRange(batch.Select(static job => job.Id));
        }

        return claimed;
    }

    /// <summary>
    /// Seeds exactly <paramref name="jobCount"/> sync jobs (one per playlist/target pair) and
    /// returns their ids, read straight from the table so the test never has to claim (and
    /// therefore lease) a job just to learn its identity.
    /// </summary>
    private async Task<IReadOnlyList<long>> SeedJobsAsync(int jobCount)
    {
        var allTargets = new[] { "plex", "jellyfin", "navidrome" };
        var remaining = jobCount;
        var index = 0;
        while (remaining > 0)
        {
            var targets = allTargets.Take(Math.Min(allTargets.Length, remaining)).ToArray();
            var sourceId = $"drain-list-{index}";
            await _repository.AddPlaylistWatchlistAsync(
                "spotify",
                sourceId,
                new PlaylistWatchlistMetadataInput($"Drain {index}", null, null, 1));
            await _repository.UpsertPlaylistWatchPreferenceAsync(
                new LibraryRepository.PlaylistWatchPreferenceUpsertInput(
                    Source: "spotify",
                    SourceId: sourceId,
                    DestinationFolderId: 42,
                    Service: targets[0],
                    SyncTargets: targets,
                    PreferredEngine: null,
                    DownloadEngineOrder: null,
                    DownloadVariantMode: null,
                    SyncMode: "mirror",
                    UpdateArtwork: true,
                    ReuseSavedArtwork: false));
            await _repository.EnqueueWatchlistPlaylistSyncJobsAsync("spotify", sourceId, "snapshot-1");

            remaining -= targets.Length;
            index++;
        }

        var ids = await ReadJobIdsAsync();
        Assert.Equal(jobCount, ids.Count);
        return ids;
    }

    private async Task<IReadOnlyList<long>> ReadJobIdsAsync()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM watchlist_sync_job ORDER BY id;";
        var ids = new List<long>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    private LibraryRepository NewRepository()
        => new(_configuration, NullLogger<LibraryRepository>.Instance);
}
