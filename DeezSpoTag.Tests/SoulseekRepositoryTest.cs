using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download.Soulseek;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     Exercises the Soulseek state repository and the folder share columns against a real SQLite database.
/// </summary>
/// <remarks>
///     These run against a temporary database file rather than a mock, because the behaviour under test is
///     largely SQL: the upsert conflict clauses, the terminal-only transfer cleanup and the opt-in filter.
///     A mock could not catch a malformed statement or a wrong column ordinal.
/// </remarks>
public sealed class SoulseekRepositoryTest : IDisposable
{
    private readonly string _dbPath;
    private readonly string _tempRoot;

    public SoulseekRepositoryTest()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-soulseek-repo-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
        _dbPath = Path.Join(_tempRoot, "queue.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // A locked temp file must not fail the run.
        }
    }

    /// <summary>
    ///     The database is left in WAL mode, and no read re-asserts it.
    /// </summary>
    /// <remarks>
    ///     <c>journal_mode</c> lives in the database file header, so it survives closing every connection.
    ///     Re-issuing it per connection was not merely redundant but a write that takes a lock, and the search
    ///     scorer opened a connection per candidate. It is now set once, when the schema is created.
    /// </remarks>
    [Fact]
    public async Task WriteAheadLoggingIsAppliedOnceAtSchemaCreation()
    {
        var repository = CreateRepository();
        await repository.RecordPeerFailureAsync("a-peer", "stalled", TimeSpan.FromMinutes(5), TestContext());
        await repository.GetPeerStatAsync("a-peer", TestContext());

        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync(TestContext());

        await using var command = new SqliteCommand("PRAGMA journal_mode;", connection);
        var mode = (string?)await command.ExecuteScalarAsync(TestContext());

        Assert.Equal("wal", mode, ignoreCase: true);

        // The peer stat is still readable, so moving the pragma did not break the read path it was on.
        Assert.NotNull(await repository.GetPeerStatAsync("a-peer", TestContext()));
    }

    /// <summary>
    ///     Asserting the mode alone proves nothing about how often it is set, so the placement is asserted too.
    /// </summary>
    /// <remarks>
    ///     A database left in WAL mode passes the check above even when every read re-issues the pragma, which
    ///     is the cost this change exists to remove. The pragma is therefore pinned to schema creation and
    ///     kept out of the per-connection path, which is what actually runs once per candidate.
    /// </remarks>
    [Fact]
    public void OnlyThePerConnectionPragmasAreAppliedOnEveryOpen()
    {
        var source = ReadRepoFile("DeezSpoTag.Services", "Download", "Soulseek", "SoulseekRepository.cs");

        var perConnection = ExtractConstant(source, "ConnectionPragmas");
        var onceOnly = ExtractConstant(source, "JournalModePragma");

        Assert.Contains("journal_mode=WAL", onceOnly, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("journal_mode", perConnection, StringComparison.OrdinalIgnoreCase);

        // synchronous and busy_timeout are per-connection settings in SQLite, so they must still be there.
        Assert.Contains("synchronous=NORMAL", perConnection, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("busy_timeout", perConnection, StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractConstant(string source, string name)
    {
        var start = source.IndexOf($"private const string {name} =", StringComparison.Ordinal);
        Assert.True(start > 0, $"{name} was not found in SoulseekRepository.cs.");

        // The value is a verbatim string, so the declaration ends at the first quote-semicolon rather than at
        // the first semicolon, which would be one of the pragmas inside it.
        var end = source.IndexOf("\";", start, StringComparison.Ordinal);
        Assert.True(end > start, $"{name} is not a verbatim string constant, so it could not be located.");
        return source.Substring(start, end - start);
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !Directory.Exists(Path.Join(current.FullName, "DeezSpoTag.Services")))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        return File.ReadAllText(Path.Join(new[] { current!.FullName }.Concat(parts).ToArray()));
    }

    [Fact]
    public async Task Schema_CreatesAllSoulseekTables()
    {
        var repository = CreateRepository();
        await repository.GetShareScanStateAsync(TestContext());

        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync(TestContext());

        var names = new List<string>();
        await using (var command = new SqliteCommand("SELECT name FROM sqlite_master WHERE type = 'table';", connection))
        await using (var reader = await command.ExecuteReaderAsync(TestContext()))
        {
            while (await reader.ReadAsync(TestContext()))
            {
                names.Add(reader.GetString(0));
            }
        }

        Assert.Contains("soulseek_search", names);
        Assert.Contains("soulseek_candidate", names);
        Assert.Contains("soulseek_transfer", names);
        Assert.Contains("soulseek_peer_stat", names);
        Assert.Contains("soulseek_share_scan_state", names);
    }

    [Fact]
    public async Task Schema_IsIdempotentAcrossRepositories()
    {
        var first = CreateRepository();
        await first.GetShareScanStateAsync(TestContext());

        // A second repository over the same file must not fail on the CREATE TABLE IF NOT EXISTS statements.
        var second = CreateRepository();
        await second.GetShareScanStateAsync(TestContext());
    }

    [Fact]
    public async Task Search_RoundTripsItsTerminalStateAndBestCandidate()
    {
        var repository = CreateRepository();
        var searchId = Guid.NewGuid();

        await repository.RecordSearchStartedAsync(
            searchId,
            "artist - title",
            SoulseekSearchMode.Automated,
            "queue-1",
            TestContext());

        var started = await repository.GetSearchAsync(searchId, TestContext());
        Assert.NotNull(started);
        Assert.Equal(searchId, started!.SearchId);
        Assert.Equal("artist - title", started.SearchText);
        Assert.Equal("queue-1", started.QueueUuid);
        Assert.Equal(SoulseekSearchMode.Automated, started.Mode);
        Assert.False(started.Completed);
        Assert.Null(started.EndedAtUtc);

        await repository.RecordSearchCompletedAsync(
            searchId,
            completed: true,
            timedOut: false,
            fileCount: 12,
            responseCount: 4,
            candidateCount: 3,
            bestCandidateId: null,
            TestContext());

        var completed = await repository.GetSearchAsync(searchId, TestContext());
        Assert.NotNull(completed);
        Assert.True(completed!.Completed);
        Assert.False(completed.TimedOut);
        Assert.Equal(12, completed.FileCount);
        Assert.Equal(4, completed.ResponseCount);
        Assert.Equal(3, completed.CandidateCount);
        Assert.NotNull(completed.EndedAtUtc);
    }

    [Fact]
    public async Task Search_IsListedForItsQueueItem()
    {
        var repository = CreateRepository();
        await repository.RecordSearchStartedAsync(Guid.NewGuid(), "a", SoulseekSearchMode.Manual, "queue-7", TestContext());
        await repository.RecordSearchStartedAsync(Guid.NewGuid(), "b", SoulseekSearchMode.Manual, "queue-7", TestContext());
        await repository.RecordSearchStartedAsync(Guid.NewGuid(), "c", SoulseekSearchMode.Manual, "queue-8", TestContext());

        var forQueue = await repository.GetSearchesForQueueAsync("queue-7", TestContext());

        Assert.Equal(2, forQueue.Count);
        Assert.All(forQueue, search => Assert.Equal("queue-7", search.QueueUuid));
    }

    [Fact]
    public async Task StaleSearches_AreDeleted()
    {
        var repository = CreateRepository();
        var searchId = Guid.NewGuid();
        await repository.RecordSearchStartedAsync(searchId, "a", SoulseekSearchMode.Manual, null, TestContext());

        // A cutoff in the future means the recorded search is older than it, so it goes.
        Assert.Equal(1, await repository.DeleteSearchesOlderThanAsync(DateTimeOffset.UtcNow.AddHours(1), TestContext()));
        Assert.Null(await repository.GetSearchAsync(searchId, TestContext()));

        // A cutoff in the past means nothing is stale yet.
        await repository.RecordSearchStartedAsync(searchId, "a", SoulseekSearchMode.Manual, null, TestContext());
        Assert.Equal(0, await repository.DeleteSearchesOlderThanAsync(DateTimeOffset.UtcNow.AddHours(-1), TestContext()));
        Assert.NotNull(await repository.GetSearchAsync(searchId, TestContext()));
    }

    [Fact]
    public async Task Timestamps_SortChronologicallyAsStoredText()
    {
        // The staleness and cleanup queries compare ISO-8601 strings in SQL. That is only correct because
        // every timestamp is written as UTC in round-trip format with a constant +00:00 offset, which makes
        // lexicographic order match chronological order. This test pins that property.
        var repository = CreateRepository();
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();

        await repository.RecordSearchStartedAsync(older, "older", SoulseekSearchMode.Manual, "queue-cutoff", TestContext());
        await Task.Delay(1100);
        await repository.RecordSearchStartedAsync(newer, "newer", SoulseekSearchMode.Manual, "queue-cutoff", TestContext());

        // Cut off everything recorded before "newer" started.
        var newerRecord = await repository.GetSearchAsync(newer, TestContext());
        Assert.NotNull(newerRecord);

        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync(TestContext());
        await using (var command = new SqliteCommand(
            "SELECT started_at_utc FROM soulseek_search WHERE search_id = @id;",
            connection))
        {
            command.Parameters.AddWithValue("id", newer.ToString("D"));
            var stored = (string?)await command.ExecuteScalarAsync(TestContext());
            Assert.NotNull(stored);
            Assert.EndsWith("+00:00", stored);
        }

        var removed = await repository.DeleteSearchesOlderThanAsync(newerRecord.StartedAtUtc, TestContext());
        Assert.Equal(1, removed);
        Assert.Null(await repository.GetSearchAsync(older, TestContext()));
        Assert.NotNull(await repository.GetSearchAsync(newer, TestContext()));
    }

    [Fact]
    public async Task Candidates_AreReplacedNotDuplicatedOnRescore()
    {
        var repository = CreateRepository();
        var searchId = Guid.NewGuid();
        await repository.RecordSearchStartedAsync(searchId, "a", SoulseekSearchMode.Manual, "queue-1", TestContext());

        var first = await repository.ReplaceCandidatesAsync(searchId, [Candidate("peer", "a.flac", 90, true)], TestContext());
        var second = await repository.ReplaceCandidatesAsync(
            searchId,
            [Candidate("peer", "b.flac", 80, true), Candidate("peer2", "c.mp3", 40, false, "quality_not_allowed")],
            TestContext());

        var stored = await repository.GetCandidatesAsync(searchId, TestContext());

        Assert.Single(first);
        Assert.Equal(2, second.Count);
        Assert.Equal(2, stored.Count);
        Assert.Equal(new[] { "b.flac", "c.mp3" }, stored.Select(c => c.Filename).ToArray());

        // Best score first.
        Assert.Equal(80, stored[0].Score);
        Assert.True(stored[0].Accepted);
        Assert.False(stored[1].Accepted);
        Assert.Equal("quality_not_allowed", stored[1].RejectedBecause);
    }

    [Fact]
    public async Task Candidates_StoreTheTierAndRankThatDedupeWillUse()
    {
        var repository = CreateRepository();
        var searchId = Guid.NewGuid();
        await repository.RecordSearchStartedAsync(searchId, "a", SoulseekSearchMode.Automated, null, TestContext());

        await repository.ReplaceCandidatesAsync(
            searchId,
            [
                new SoulseekCandidate(
                    new SoulseekRawCandidate("peer", "lossless.flac", 100, ".flac", 900, 24, 96000),
                    "FLAC",
                    "FLAC (24-bit/96kHz)",
                    "hires_96",
                    100,
                    95,
                    0.9,
                    Accepted: true),
                new SoulseekCandidate(
                    new SoulseekRawCandidate("peer", "mystery.opus", 100, ".opus", 128),
                    "UNKNOWN",
                    "Unknown quality",
                    null,
                    null,
                    10,
                    0.3,
                    Accepted: false,
                    RejectedBecause: "unknown_quality")
            ],
            TestContext());

        var stored = await repository.GetCandidatesAsync(searchId, TestContext());

        Assert.Equal(2, stored.Count);
        Assert.Equal("hires_96", stored[0].TierValue);
        Assert.Equal(100, stored[0].CanonicalRank);
        Assert.Null(stored[1].TierValue);
        Assert.Null(stored[1].CanonicalRank);
    }

    [Fact]
    public async Task Transfer_UpsertsAndKeepsTheExpectedPath()
    {
        var repository = CreateRepository();
        var transferId = Guid.NewGuid();

        await repository.UpsertTransferAsync(
            Status(transferId, "queued", isTerminal: false),
            "queue-1",
            expectedPath: "/staging/track.flac",
            verified: false,
            TestContext());

        var first = await repository.GetTransferAsync(transferId, TestContext());
        Assert.NotNull(first);
        Assert.Equal("queued", first!.State);
        Assert.False(first.IsTerminal);
        Assert.False(first.Verified);
        Assert.Equal("/staging/track.flac", first.ExpectedPath);

        // A later update that does not know the path must not wipe the one we already recorded.
        await repository.UpsertTransferAsync(
            Status(transferId, "completed", isTerminal: true, successful: true),
            "queue-1",
            expectedPath: null,
            verified: true,
            TestContext());

        var second = await repository.GetTransferAsync(transferId, TestContext());
        Assert.NotNull(second);
        Assert.Equal("completed", second!.State);
        Assert.True(second.IsTerminal);
        Assert.True(second.IsSuccessful);
        Assert.True(second.Verified);
        Assert.Equal("/staging/track.flac", second.ExpectedPath);

        // One row, not two.
        var forQueue = await repository.GetTransfersForQueueAsync("queue-1", TestContext());
        Assert.Single(forQueue);
    }

    [Fact]
    public async Task TransferCleanup_KeepsInFlightRowsAndRemovesTerminalOnes()
    {
        var repository = CreateRepository();
        var inFlight = Guid.NewGuid();
        var finished = Guid.NewGuid();

        await repository.UpsertTransferAsync(Status(inFlight, "in_progress", isTerminal: false), "queue-1", null, false, TestContext());
        await repository.UpsertTransferAsync(Status(finished, "completed", isTerminal: true, successful: true), "queue-1", null, true, TestContext());

        var removed = await repository.DeleteTerminalTransfersOlderThanAsync(DateTimeOffset.UtcNow.AddHours(1), TestContext());

        Assert.Equal(1, removed);
        Assert.NotNull(await repository.GetTransferAsync(inFlight, TestContext()));
        Assert.Null(await repository.GetTransferAsync(finished, TestContext()));
    }

    [Fact]
    public async Task PeerCooldown_IsAppliedClearedAndExpires()
    {
        var repository = CreateRepository();

        Assert.Null(await repository.GetPeerStatAsync("peer", TestContext()));

        await repository.RecordPeerFailureAsync("peer", "stalled", TimeSpan.FromMinutes(30), TestContext());
        var failed = await repository.GetPeerStatAsync("peer", TestContext());
        Assert.NotNull(failed);
        Assert.Equal(1, failed!.FailureCount);
        Assert.Equal("stalled", failed.LastFailureReason);
        Assert.NotNull(failed.CooldownExpiresUtc);

        var inCooldown = await repository.GetPeersInCooldownAsync(TestContext());
        Assert.Single(inCooldown);

        // A success clears the penalty.
        await repository.RecordPeerSuccessAsync("peer", TestContext());
        var recovered = await repository.GetPeerStatAsync("peer", TestContext());
        Assert.NotNull(recovered);
        Assert.Equal(1, recovered!.SuccessCount);
        Assert.Null(recovered.CooldownExpiresUtc);
        Assert.Empty(await repository.GetPeersInCooldownAsync(TestContext()));
    }

    [Fact]
    public async Task RepeatedPeerFailures_ExtendTheCooldown()
    {
        var repository = CreateRepository();

        await repository.RecordPeerFailureAsync("peer", "first", TimeSpan.FromMinutes(10), TestContext());
        var afterFirst = await repository.GetPeerStatAsync("peer", TestContext());

        await repository.RecordPeerFailureAsync("peer", "second", TimeSpan.FromHours(2), TestContext());
        var afterSecond = await repository.GetPeerStatAsync("peer", TestContext());

        Assert.NotNull(afterFirst);
        Assert.NotNull(afterSecond);
        Assert.Equal(2, afterSecond!.FailureCount);
        Assert.Equal("second", afterSecond.LastFailureReason);
        Assert.True(afterSecond.CooldownExpiresUtc > afterFirst!.CooldownExpiresUtc);
    }

    [Fact]
    public async Task ExpiredCooldowns_AreRemoved()
    {
        var repository = CreateRepository();

        await repository.RecordPeerFailureAsync("expired", "old", TimeSpan.FromMinutes(-30), TestContext());
        await repository.RecordPeerFailureAsync("live", "recent", TimeSpan.FromMinutes(30), TestContext());

        Assert.Equal(1, await repository.DeleteExpiredCooldownsAsync(TestContext()));

        var remaining = await repository.GetPeersInCooldownAsync(TestContext());
        Assert.Single(remaining);
        Assert.Equal("live", remaining[0].Username);
    }

    [Fact]
    public async Task ShareScanState_TracksRunningThenCompleted()
    {
        var repository = CreateRepository();

        Assert.Null(await repository.GetShareScanStateAsync(TestContext()));

        await repository.RecordShareScanStartedAsync(TestContext());
        var running = await repository.GetShareScanStateAsync(TestContext());
        Assert.NotNull(running);
        Assert.True(running!.IsRunning);
        Assert.Null(running.CompletedAtUtc);

        await repository.RecordShareScanCompletedAsync(3, 1234, error: null, TestContext());
        var done = await repository.GetShareScanStateAsync(TestContext());
        Assert.NotNull(done);
        Assert.False(done!.IsRunning);
        Assert.Equal(3, done.ShareCount);
        Assert.Equal(1234, done.FileCount);
        Assert.NotNull(done.CompletedAtUtc);
    }

    [Fact]
    public async Task ShareScanState_RecordsFailures()
    {
        var repository = CreateRepository();

        await repository.RecordShareScanCompletedAsync(0, 0, "slskd is unavailable.", TestContext());

        var state = await repository.GetShareScanStateAsync(TestContext());
        Assert.NotNull(state);
        Assert.Equal("slskd is unavailable.", state!.Error);
    }

    [Fact]
    public async Task AttachedClaimsCannotBeReassignedToAnotherTransfer()
    {
        var repository = CreateRepository();
        var owner = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var path = NewTempPath("attached.flac");
        Assert.True(await repository.TryClaimSourcePathsAsync(owner, "queue", "peer", "track.flac", [path]));
        await repository.AttachSourceClaimsAsync(owner, transferId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.AttachSourceClaimsAsync(owner, Guid.NewGuid()));
        Assert.Equal(transferId, Assert.Single(await repository.GetSourceClaimsForQueueAsync("queue")).TransferId);
    }

    [Fact]
    public async Task ConcurrentRepositoriesClaimingTheSamePathHaveOneWinner()
    {
        var first = CreateRepository();
        var second = CreateRepository();
        await first.GetSourceClaimsForQueueAsync("warm");
        await second.GetSourceClaimsForQueueAsync("warm");
        var path = NewTempPath("concurrent.flac");
        var results = await Task.WhenAll(
            Task.Run(() => first.TryClaimSourcePathsAsync(Guid.NewGuid(), "a", "peer", "file", [path])),
            Task.Run(() => second.TryClaimSourcePathsAsync(Guid.NewGuid(), "b", "peer", "file", [path])));
        Assert.Single(results.Where(won => won));
        Assert.Single(results.Where(won => !won));
    }

    #region Source path claims

    /// <summary>
    ///     Two repository instances claiming one path: exactly one wins.
    /// </summary>
    /// <remarks>
    ///     Separate instances are the realistic case, because two workers resolve the same shared download root and
    ///     each holds its own repository over the same file. A uniqueness check that only held inside one instance
    ///     would be no check at all. The loser's transaction rolls back, so it takes nothing at all.
    /// </remarks>
    [Fact]
    public async Task TwoRepositoriesClaimingOnePathProduceExactlyOneWinner()
    {
        var path = NewTempPath("shared.flac");
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        Assert.True(await CreateRepository().TryClaimSourcePathsAsync(first, "queue-a", "peer-a", "shared.flac", [path], TestContext()));
        Assert.False(await CreateRepository().TryClaimSourcePathsAsync(second, "queue-b", "peer-b", "shared.flac", [path], TestContext()));

        // And the winner is the one that holds it, not merely the one that claimed first by luck of ordering.
        var held = await CreateRepository().GetSourceClaimsForQueueAsync("queue-b", TestContext());
        Assert.Empty(held);
        var winners = await CreateRepository().GetSourceClaimsForQueueAsync("queue-a", TestContext());
        Assert.Equal(first, Assert.Single(winners).OwnershipId);
    }

    /// <summary>
    ///     Claiming several paths is all or nothing.
    /// </summary>
    /// <remarks>
    ///     A partial claim would leave the attempt holding the path that happened to be free and refusing the one it
    ///     needs, and the missing path is the one slskd is about to write into. The rollback is what makes the
    ///     reservation mean what it says.
    /// </remarks>
    [Fact]
    public async Task ClaimingSeveralPathsRollsBackEntirelyWhenOneConflicts()
    {
        var taken = NewTempPath("taken.flac");
        var free = NewTempPath("free.flac");
        var holder = Guid.NewGuid();

        Assert.True(await CreateRepository().TryClaimSourcePathsAsync(holder, "queue-holder", "peer", "taken.flac", [taken], TestContext()));

        var second = Guid.NewGuid();
        Assert.False(await CreateRepository().TryClaimSourcePathsAsync(second, "queue-second", "peer", "x.flac", [free, taken], TestContext()));

        // The free path was rolled back too, so this attempt holds nothing.
        Assert.Empty(await CreateRepository().GetSourceClaimsForQueueAsync("queue-second", TestContext()));
        Assert.NotEmpty(await CreateRepository().GetSourceClaimsForQueueAsync("queue-holder", TestContext()));
    }

    /// <summary>
    ///     A path already holding a file cannot be claimed by anyone.
    /// </summary>
    /// <remarks>
    ///     There is no evidence the file belongs to a new attempt, and adopting one on size alone is the failure the
    ///     claim exists to prevent. Refused for the first caller and every one after it.
    /// </remarks>
    [Fact]
    public async Task APathThatAlreadyHoldsAFileIsNeverClaimed()
    {
        var occupied = NewTempPath("already-here.flac");
        File.WriteAllBytes(occupied, [1, 2, 3]);

        Assert.False(await CreateRepository().TryClaimSourcePathsAsync(Guid.NewGuid(), "queue-x", "peer", "already-here.flac", [occupied], TestContext()));
        Assert.False(await CreateRepository().TryClaimSourcePathsAsync(Guid.NewGuid(), "queue-y", "peer", "already-here.flac", [occupied], TestContext()));
    }

    /// <summary>
    ///     A claim survives closing the repository and attaches to the transfer it was made for.
    /// </summary>
    /// <remarks>
    ///     This is the restart window. The reservation is written before the transfer id is known, so it has to
    ///     outlive the process that made it and still be found by the next run - otherwise a restart would enqueue a
    ///     second transfer for a path the first one may already be filling.
    /// </remarks>
    [Fact]
    public async Task ClaimsSurviveReopeningAndAttachToTheirTransfer()
    {
        var path = NewTempPath("restart.flac");
        var ownershipId = Guid.NewGuid();
        var transferId = Guid.NewGuid();

        // A brand new repository over the same file, as a restarted process would use.
        var repository = CreateRepository();
        Assert.True(await repository.TryClaimSourcePathsAsync(ownershipId, "queue-restart", "peer", "restart.flac", [path], TestContext()));

        await CreateRepository().AttachSourceClaimsAsync(ownershipId, transferId, TestContext());

        var reopened = CreateRepository();
        var byTransfer = await reopened.GetSourceClaimsForTransferAsync(transferId, TestContext());
        var claim = Assert.Single(byTransfer);
        Assert.Equal(ownershipId, claim.OwnershipId);
        Assert.Equal("queue-restart", claim.QueueUuid);
        Assert.Equal("peer", claim.Username);
        Assert.Equal("restart.flac", claim.Filename);
        Assert.Equal(path, claim.Path);

        // An unattached reservation is reported as unattached, which is how a restart recognises the gap.
        var fresh = Guid.NewGuid();
        Assert.True(await reopened.TryClaimSourcePathsAsync(fresh, "queue-fresh", "peer", "other.flac", [NewTempPath("other.flac")], TestContext()));
        Assert.Null(Assert.Single(await reopened.GetSourceClaimsForQueueAsync("queue-fresh", TestContext())).TransferId);
    }

    [Fact]
    public async Task ReleasingClaimsFreesThePathsForTheNextOperation()
    {
        var path = NewTempPath("released.flac");
        var owner = Guid.NewGuid();
        var repository = CreateRepository();

        Assert.True(await repository.TryClaimSourcePathsAsync(owner, "queue-release", "peer", "released.flac", [path], TestContext()));
        await repository.ReleaseSourceClaimsAsync(owner, TestContext());
        Assert.Empty(await repository.GetSourceClaimsForQueueAsync("queue-release", TestContext()));

        // Released, so the path is claimable again - and nothing is left behind for the next operation to trip over.
        var next = Guid.NewGuid();
        Assert.True(await repository.TryClaimSourcePathsAsync(next, "queue-next", "peer", "released.flac", [path], TestContext()));

        await repository.ReleaseSourceClaimsAsync(next, TestContext());
        Assert.Empty(await repository.GetSourceClaimsForQueueAsync("queue-next", TestContext()));
    }

    /// <summary>
    ///     One operation's successive peer attempts do not collide with each other.
    /// </summary>
    /// <remarks>
    ///     A download may try up to three peers, and each attempt reserves the same destination. The destination
    ///     belongs to the queue item rather than to one peer, so re-reserving it as the same owner succeeds; with a
    ///     per-attempt owner this would refuse the download for a reason unrelated to the peer.
    /// </remarks>
    [Fact]
    public async Task TheSameOwnerCanReclaimItsOwnPathsForAnotherPeer()
    {
        var path = NewTempPath("same-owner.flac");
        var owner = Guid.NewGuid();
        var repository = CreateRepository();

        Assert.True(await repository.TryClaimSourcePathsAsync(owner, "queue-same", "peer-one", "first.flac", [path], TestContext()));
        Assert.True(await repository.TryClaimSourcePathsAsync(owner, "queue-same", "peer-two", "second.flac", [path], TestContext()));

        // One row, not two, and it names the peer the next attempt will ask.
        var claims = await repository.GetSourceClaimsForQueueAsync("queue-same", TestContext());
        var claim = Assert.Single(claims);
        Assert.Equal("peer-two", claim.Username);
        Assert.Equal("second.flac", claim.Filename);
    }

    /// <summary>
    ///     Two peers offering the same file collide, because they resolve to the same local path.
    /// </summary>
    /// <remarks>
    ///     The remote paths differ only in the peer's own folder above the shared directory, so both land at
    ///     <c>root/Album/04 Isabella.mp3</c>. This is the collision the review focus names: two peer files mapping to
    ///     one completed path must not both hold it.
    /// </remarks>
    [Fact]
    public async Task TwoPeerFilesResolvingToOnePathCollide()
    {
        var root = Path.Join(_tempRoot, "slskd", "downloads");
        var first = SoulseekTransferService.ResolveSourcePath(@"@@peerA\Music\Album\04 Isabella.mp3", root);
        var second = SoulseekTransferService.ResolveSourcePath(@"@@peerB\Share\Album\04 Isabella.mp3", root);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first, second);

        Assert.True(await CreateRepository().TryClaimSourcePathsAsync(Guid.NewGuid(), "queue-p1", "peerA", "a.mp3", [first!], TestContext()));
        Assert.False(await CreateRepository().TryClaimSourcePathsAsync(Guid.NewGuid(), "queue-p2", "peerB", "b.mp3", [second!], TestContext()));
    }

    #endregion

    #region Terminal history deletion

    [Fact]
    public async Task DeletingATerminalTransferRemovesOnlyThatRow()
    {
        var repository = CreateRepository();
        var removed = Guid.NewGuid();
        var kept = Guid.NewGuid();
        await repository.UpsertTransferAsync(Status(removed, "completed", isTerminal: true, successful: true), "queue-del", null, true, TestContext());
        await repository.UpsertTransferAsync(Status(kept, "completed", isTerminal: true, successful: true), "queue-del", null, true, TestContext());

        var deleted = await repository.DeleteTerminalTransferAsync(removed, TestContext());
        Assert.True(deleted);
        Assert.Null(await repository.GetTransferAsync(removed, TestContext()));
        Assert.NotNull(await repository.GetTransferAsync(kept, TestContext()));
        Assert.Equal([kept], (await repository.GetTransfersForQueueAsync("queue-del", TestContext())).Select(t => t.TransferId));
    }

    [Fact]
    public async Task ANonTerminalTransferCannotBeDeleted()
    {
        // A running transfer holds a file slskd is fetching and a claim another attempt may rely on, so forgetting
        // it here would strand both.
        var repository = CreateRepository();
        var running = Guid.NewGuid();
        await repository.UpsertTransferAsync(Status(running, "in_progress", isTerminal: false), "queue-run", null, false, TestContext());

        Assert.False(await repository.DeleteTerminalTransferAsync(running, TestContext()));
        Assert.NotNull(await repository.GetTransferAsync(running, TestContext()));
    }

    [Fact]
    public async Task DeletingATransferTwiceReportsTheSecondAttemptAsRemovingNothing()
    {
        // The delete endpoint turns a false into a conflict, and the local 404 it answers for a repeat must not
        // depend on the row still being there.
        var repository = CreateRepository();
        var terminal = Guid.NewGuid();
        await repository.UpsertTransferAsync(Status(terminal, "failed", isTerminal: true), "queue-twice", null, false, TestContext());

        Assert.True(await repository.DeleteTerminalTransferAsync(terminal, TestContext()));
        Assert.False(await repository.DeleteTerminalTransferAsync(terminal, TestContext()));
    }

    #endregion

    #region Search failure persistence

    [Fact]
    public async Task AFailedSearchKeepsItsObservedCountsAndItsError()
    {
        // Peers did answer: that is what the counts record. Losing them would turn a retrieval failure into
        // "no peer responded" on the next read, which is the misclassification this persistence prevents.
        var repository = CreateRepository();
        var searchId = Guid.NewGuid();
        await repository.RecordSearchStartedAsync(searchId, "artist - title", SoulseekSearchMode.Manual, "queue-fail", TestContext());

        await repository.RecordSearchFailedAsync(
            searchId, fileCount: 41, responseCount: 3, "response_retrieval_failed", "Soulseek search results could not be retrieved.", TestContext());

        var record = await repository.GetSearchAsync(searchId, TestContext());
        Assert.NotNull(record);
        Assert.Equal("response_retrieval_failed", record!.ErrorCode);
        Assert.Equal("Soulseek search results could not be retrieved.", record.Error);
        Assert.Equal(41, record.FileCount);
        Assert.Equal(3, record.ResponseCount);

        // Neither completed nor timed out: the search finished and its files were never read, and neither word
        // describes that.
        Assert.False(record.Completed);
        Assert.False(record.TimedOut);

        // And it survives a reload, which is the whole point of recording it.
        Assert.Equal("response_retrieval_failed", (await CreateRepository().GetSearchAsync(searchId, TestContext()))!.ErrorCode);
    }

    [Fact]
    public async Task ASuccessfulSearchCarriesNoError()
    {
        // The columns must not leak a stale failure onto a later, successful search.
        var repository = CreateRepository();
        var searchId = Guid.NewGuid();
        await repository.RecordSearchStartedAsync(searchId, "a - b", SoulseekSearchMode.Manual, null, TestContext());
        await repository.RecordSearchCompletedAsync(searchId, true, false, 10, 2, 3, 0, TestContext());

        var record = await repository.GetSearchAsync(searchId, TestContext());
        Assert.NotNull(record);
        Assert.Null(record!.ErrorCode);
        Assert.Null(record.Error);
        Assert.True(record.Completed);
    }

    /// <summary>
    ///     A database written before these columns existed gains them without losing its rows.
    /// </summary>
    /// <remarks>
    ///     <c>CREATE TABLE IF NOT EXISTS</c> leaves an existing table exactly as it was, so without the additive
    ///     column step an upgraded install would fail every search write for a column the schema never created.
    /// </remarks>
    [Fact]
    public async Task ALegacySearchTableGainsTheErrorColumnsWithoutLosingHistory()
    {
        Directory.CreateDirectory(_tempRoot);

        await using (var legacy = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await legacy.OpenAsync(TestContext());
            await using var create = new SqliteCommand(
                @"
CREATE TABLE soulseek_search (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    search_id TEXT NOT NULL UNIQUE,
    queue_uuid TEXT,
    search_text TEXT NOT NULL,
    mode TEXT NOT NULL,
    file_count INTEGER NOT NULL DEFAULT 0,
    response_count INTEGER NOT NULL DEFAULT 0,
    candidate_count INTEGER NOT NULL DEFAULT 0,
    is_completed INTEGER NOT NULL DEFAULT 0,
    is_timed_out INTEGER NOT NULL DEFAULT 0,
    best_candidate_id INTEGER,
    started_at_utc TEXT NOT NULL,
    ended_at_utc TEXT
);
INSERT INTO soulseek_search (search_id, search_text, mode, file_count, response_count, started_at_utc)
VALUES (@id, 'a - b', 'Manual', 7, 1, @started);",
                legacy);
            create.Parameters.AddWithValue("id", Guid.NewGuid().ToString("D"));
            create.Parameters.AddWithValue("started", "2026-01-01T00:00:00.0000000+00:00");
            await create.ExecuteNonQueryAsync(TestContext());
        }

        var existing = Guid.Parse(ReadSearchId());
        var repository = CreateRepository();
        await repository.RecordSearchFailedAsync(existing, 5, 2, "response_retrieval_failed", "could not retrieve", TestContext());

        var record = await repository.GetSearchAsync(existing, TestContext());
        Assert.NotNull(record);
        Assert.Equal("response_retrieval_failed", record!.ErrorCode);
        Assert.Equal(5, record.FileCount);

        // The row that was already there is still readable, not replaced or dropped.
        Assert.Equal("a - b", record.SearchText);

        string ReadSearchId()
        {
            using var command = new SqliteCommand("SELECT search_id FROM soulseek_search;", new SqliteConnection($"Data Source={_dbPath}"));
            command.Connection!.Open();
            return (string)command.ExecuteScalar()!;
        }
    }

    #endregion

    /// <summary>A path inside this test's own temp tree that nothing has written to yet.</summary>
    private string NewTempPath(string name)
    {
        var path = Path.Join(_tempRoot, "claims", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    private SoulseekRepository CreateRepository()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Queue"] = $"Data Source={_dbPath}" })
            .Build();

        return new SoulseekRepository(configuration, NullLogger<SoulseekRepository>.Instance);
    }

    private static SoulseekCandidate Candidate(
        string username,
        string filename,
        double score,
        bool accepted,
        string? rejectedBecause = null)
        => new(
            new SoulseekRawCandidate(username, filename, 1000, ".flac"),
            "FLAC",
            "FLAC",
            "flac",
            70,
            score,
            0.8,
            accepted,
            rejectedBecause);

    private static SoulseekTransferStatus Status(
        Guid transferId,
        string state,
        bool isTerminal,
        bool successful = false)
        => new(
            transferId,
            "peer",
            "file.flac",
            state,
            isTerminal,
            successful,
            BytesTransferred: 500,
            Size: 1000,
            AverageSpeed: 128,
            PlaceInQueue: null,
            Progress: 50);

    private static CancellationToken TestContext() => CancellationToken.None;
}
