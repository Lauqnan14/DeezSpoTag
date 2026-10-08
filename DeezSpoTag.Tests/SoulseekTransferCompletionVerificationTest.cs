using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models.Soulseek;
using DeezSpoTag.Integrations.Soulseek;
using DeezSpoTag.Services.Download.Soulseek;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     Proves the rule that a completed slskd transfer is not a completed DeezSpoTag download.
/// </summary>
/// <remarks>
///     <para>
///         A transfer that slskd reports as completed can still have produced nothing useful: the file may be
///         missing, empty, or truncated. Each of those must be treated as a failure, because a phantom success
///         would tell the user they have a track they do not have.
///     </para>
/// </remarks>
public sealed class SoulseekTransferCompletionVerificationTest : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _stagingDir;
    private readonly string _dbPath;
    private readonly FakeSlskdClient _client = new();

    public SoulseekTransferCompletionVerificationTest()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-soulseek-verify-" + Path.GetRandomFileName());
        _stagingDir = Path.Join(_tempRoot, "staging");
        _dbPath = Path.Join(_tempRoot, "queue.db");
        Directory.CreateDirectory(_stagingDir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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

    [Fact]
    public async Task LegacyVerifiedStagingAudioSurvivesWithoutClaimsOrCredentials()
    {
        var transferId = Guid.NewGuid();
        var path = CreateFile("verified-legacy.flac", 512);
        await CreateRepository().UpsertTransferAsync(new SoulseekTransferStatus(transferId, "listener", "legacy.flac",
            "completed", true, true, 512, 512, 0, null, 100), "queue-legacy", path, true);
        var service = new SoulseekTransferService(_client, new StubCredentials(null), CreateRepository(),
            NullLogger<SoulseekTransferService>.Instance);
        var result = await service.WaitForCompletionAsync("listener", transferId, path, "queue-legacy");
        Assert.NotNull(result);
        Assert.Equal(path, result!.LocalPath);
    }

    [Fact]
    public async Task LegacyUnverifiedOwnershipIsReportedWithoutStartingAnotherTransfer()
    {
        var transferId = Guid.NewGuid();
        var path = CreateFile("unverified-legacy.flac", 512);
        await CreateRepository().UpsertTransferAsync(RunningStatus(transferId, 512, "legacy.flac"),
            "queue-legacy", path, false);
        await Assert.ThrowsAsync<SoulseekTransferMonitoringException>(() => CreateService()
            .WaitForCompletionAsync("listener", transferId, path, "queue-legacy"));
    }

    private SoulseekTransferService CreateService(ISoulseekRealtimePublisher? realtime = null) => new(
        _client,
        new StubCredentials(new SlskdCredentials("http://localhost:5030", "key")),
        CreateRepository(),
        NullLogger<SoulseekTransferService>.Instance, realtime);

    private SoulseekRepository CreateRepository()
        => new(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Queue"] = $"Data Source={_dbPath}" })
                .Build(),
            NullLogger<SoulseekRepository>.Instance);

    private static string StagingPath(string name) => Path.Join(Path.GetTempPath(), "unused");

    [Fact]
    public async Task CompletedTransferWithARealFile_IsVerified()
    {
        _client.Transfer = CompletedTransfer(size: 30_000);

        var transferId = _client.Transfer.Id!.Value;
        var path = await ClaimedPathAsync(transferId, "queue-verify", "track.flac", 30_000, remoteFilename: "track.flac");

        var result = await CreateService().WaitForCompletionAsync("listener", transferId, path, "queue-verify");

        Assert.NotNull(result);
        Assert.Equal(path, result!.LocalPath);
        Assert.Equal(30_000, result.Size);
        Assert.Equal("listener", result.Username);
    }

    /// <summary>
    ///     The reproduced adoption: a file already at the destination cannot become this transfer's download.
    /// </summary>
    /// <remarks>
    ///     Same size, same name, same peer, and slskd reporting the transfer completed - so every check the old code
    ///     had would have passed. What is missing is any evidence that this transfer is the one that fetched it, which
    ///     is what the path claim records. Without a claim there is nothing to adopt, and accepting the file anyway is
    ///     how a track this item never downloaded is handed back as its own.
    /// </remarks>
    [Fact]
    public async Task AnExistingSameSizedFileIsNotAdoptedAsThisTransfersDownload()
    {
        _client.Transfer = CompletedTransfer(size: 30_000);
        var transferId = _client.Transfer.Id!.Value;

        // Claimed: false, so the file is written with nothing claiming it - an unrelated download that was already
        // at this path when the transfer reported completion.
        var path = await ClaimedPathAsync(transferId, "queue-unclaimed", "track.flac", 30_000, claim: false, remoteFilename: "track.flac");

        var result = await CreateService().WaitForCompletionAsync("listener", transferId, path, "queue-unclaimed");

        Assert.Null(result);
    }

    /// <summary>
    ///     A claim naming a different peer is not evidence about this peer's file.
    /// </summary>
    /// <remarks>
    ///     The claim exists and is attached to this transfer, so ownership is not simply absent - it is present and
    ///     wrong. Reading it would let this transfer take a file some other peer's claim is holding.
    /// </remarks>
    [Fact]
    public async Task AClaimHeldByAnotherPeerCannotCompleteThisTransfer()
    {
        _client.Transfer = CompletedTransfer(size: 30_000);
        var transferId = _client.Transfer.Id!.Value;

        var path = await ClaimedPathAsync(
            transferId,
            "queue-other-peer",
            "track.flac",
            30_000,
            username: "a-different-peer",
            remoteFilename: "track.flac");

        var result = await CreateService().WaitForCompletionAsync("listener", transferId, path, "queue-other-peer");

        Assert.Null(result);
    }

    [Fact]
    public async Task CompletedTransferWithNoFile_IsNotASuccess()
    {
        var path = Path.Join(_stagingDir, "never-arrived.flac");
        _client.Transfer = CompletedTransfer(size: 30_000);

        var transferId = _client.Transfer.Id!.Value;
        var result = await CreateService().WaitForCompletionAsync("listener", transferId, path, "queue-verify");

        Assert.Null(result);
    }

    /// <summary>
    ///     The reproduced failure: slskd delivered the file and then stopped answering.
    /// </summary>
    /// <remarks>
    ///     The transfer was still running as far as slskd's last word went, so the monitor timed out, and the
    ///     timeout recorded a failed transfer while a complete file sat on disk. That is a download reported as a
    ///     failure with its own bytes in hand, and it is what this whole repair exists to stop.
    /// </remarks>
    [Fact]
    public async Task VerifiedFileOnDiskMustSurviveMonitoringTimeout()
    {
        var transferId = Guid.NewGuid();

        // Reserved before the file exists, which is the order the download service uses: the claim has to predate the
        // bytes, because a claim is refused on a path that already holds a file.
        var path = await ClaimedPathAsync(transferId, "queue-timeout", "survives-timeout.flac", length: 0);
        await CreateRepository().UpsertTransferAsync(
            RunningStatus(transferId, size: 30_000),
            "queue-timeout",
            path,
            verified: false,
            CancellationToken.None);

        // The completed file's bytes arrive during the second lookup, which times out. Written to the path already
        // reserved for this transfer, so what is under test is the transport failure rather than the ownership rule.
        _client.OnPoll = poll =>
        {
            if (poll == 2) WriteBytes(path, 30_000);
        };
        _client.Transfer = RunningTransfer(transferId, size: 30_000);
        _client.ThrowAfterFirstPoll = true;

        var result = await CreateService().WaitForCompletionAsync(
            "listener", transferId, path, "queue-timeout", cancellationToken: TestTimeout());

        Assert.NotNull(result);
        Assert.Equal(path, result!.LocalPath);
        Assert.Equal(30_000, result.Size);
        Assert.Equal(2, _client.Polls);

        // And the row says so, rather than sitting at the timeout with nothing verified.
        var recorded = await CreateRepository().GetTransferAsync(transferId, CancellationToken.None);
        Assert.NotNull(recorded);
        Assert.True(recorded!.IsTerminal);
        Assert.True(recorded.IsSuccessful);
        Assert.True(recorded.Verified);
        Assert.True(string.IsNullOrWhiteSpace(recorded.Error));
    }

    [Fact]
    public async Task PersistedIdentityVerifiesBeforeFirstStatusFailure()
    {
        // The very first lookup fails. Without the recorded identity there would be no filename and no size to
        // verify against, and a finished download would be thrown away on the first transport hiccup.
        var transferId = Guid.NewGuid();
        var path = await ClaimedPathAsync(transferId, "queue-persisted", "persisted-identity.flac", 30_000);
        var repository = CreateRepository();
        await repository.UpsertTransferAsync(
            RunningStatus(transferId, size: 30_000, filename: "album\\01 - Angel.flac"),
            "queue-persisted",
            path,
            verified: false,
            CancellationToken.None);

        _client.ThrowImmediately = true;

        var result = await CreateService().WaitForCompletionAsync(
            "listener", transferId, path, "queue-persisted", cancellationToken: TestTimeout());

        Assert.NotNull(result);
        Assert.Equal(path, result!.LocalPath);
    }

    /// <summary>
    ///     Another item's record must not lend this transfer a filename and a size.
    /// </summary>
    /// <remarks>
    ///     The persisted record is what lets a first lookup failure still verify a file. Read without asking
    ///     whose record it is, it would verify whatever file that other queue happened to be fetching - so a
    ///     completed download could be reported from a file this item never asked for.
    /// </remarks>
    [Fact]
    public async Task PersistedIdentityFromAnotherQueueOrPeerCannotVerifyThisFile()
    {
        // Sized so that only the other queue's record could justify calling it complete. This item's own
        // transfer reports no size, so with the foreign record refused there is nothing to measure against.
        var path = CreateFile("not-ours.flac", 5_000);
        var transferId = Guid.NewGuid();
        await CreateRepository().UpsertTransferAsync(
            RunningStatus(transferId, size: 5_000, filename: "somebody-elses.flac"),
            "a-different-queue",
            Path.Join(_stagingDir, "somebody-elses.flac"),
            verified: false,
            CancellationToken.None);

        // This transfer advertises no size of its own, and never finishes.
        _client.Transfer = RunningTransfer(transferId, size: 0);

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));

        await Assert.ThrowsAsync<SoulseekTransferMonitoringException>(() => CreateService().WaitForCompletionAsync(
            "listener", transferId, path, "queue-mine", cancellationToken: stop.Token));
        var row = await CreateRepository().GetTransferAsync(transferId);
        Assert.Equal("a-different-queue", row!.QueueUuid);
        Assert.Equal("somebody-elses.flac", row.Filename);
        Assert.False(row.IsTerminal);
        Assert.Equal(0, _client.Polls);
    }

    [Fact]
    public async Task TheFileIsCheckedBeforeAnyRemoteAnswerArrives()
    {
        // The whole point of seeding from the persisted record: a transfer that finished while slskd stopped
        // answering is a completed download, and it must not depend on ever getting a status back.
        var transferId = Guid.NewGuid();
        var path = await ClaimedPathAsync(transferId, "queue-no-answer", "before-any-answer.flac", 30_000);
        await CreateRepository().UpsertTransferAsync(
            RunningStatus(transferId, size: 30_000, filename: "album\\01 - Angel.flac"),
            "queue-no-answer",
            path,
            verified: false,
            CancellationToken.None);

        _client.ThrowImmediately = true;

        var result = await CreateService().WaitForCompletionAsync(
            "listener", transferId, path, "queue-no-answer", cancellationToken: TestTimeout());

        Assert.NotNull(result);
        Assert.Equal(0, _client.Polls);
    }

    /// <summary>
    ///     Logging out mid-transfer must not throw away a file slskd has already written.
    /// </summary>
    /// <remarks>
    ///     Logout deletes the slskd credentials, so a monitor that requires them up front cannot open at all.
    ///     The transfer keeps running in slskd regardless, and the file it produced is the outcome - refusing
    ///     before the disk check turned "the reader logged out" into "the download failed" and discarded a
    ///     completed file. The credentials are only needed to ask slskd about a transfer that has not arrived.
    /// </remarks>
    [Fact]
    public async Task CredentialsAreGoneButTheFileIsOnDisk_AndItStillCompletes()
    {
        var transferId = Guid.NewGuid();
        var path = await ClaimedPathAsync(transferId, "queue-logout", "survives-logout.flac", 30_000);
        await CreateRepository().UpsertTransferAsync(
            RunningStatus(transferId, size: 30_000),
            "queue-logout",
            path,
            verified: false,
            CancellationToken.None);

        // No credentials at all: this is what logout leaves behind.
        var service = new SoulseekTransferService(
            _client,
            new StubCredentials(null),
            CreateRepository(),
            NullLogger<SoulseekTransferService>.Instance);

        var result = await service.WaitForCompletionAsync(
            "listener", transferId, path, "queue-logout", cancellationToken: TestTimeout());

        Assert.NotNull(result);
        Assert.Equal(path, result!.LocalPath);

        var recorded = await CreateRepository().GetTransferAsync(transferId, CancellationToken.None);
        Assert.NotNull(recorded);
        Assert.True(recorded!.Verified);
        Assert.True(recorded.IsSuccessful);
    }

    /// <summary>
    ///     With no credentials and no file, the monitor has to say so rather than poll forever.
    /// </summary>
    [Fact]
    public async Task CredentialsAreGoneAndTheFileIsNotThere_ReportsUnavailable()
    {
        var path = Path.Join(_stagingDir, "never-arrived-after-logout.flac");
        var transferId = Guid.NewGuid();
        var owner = Guid.NewGuid();
        Assert.True(await CreateRepository().TryClaimSourcePathsAsync(owner, "queue-logout-missing", "listener", "album\\01 - Angel.flac", [path]));
        await CreateRepository().AttachSourceClaimsAsync(owner, transferId);
        await CreateRepository().UpsertTransferAsync(
            RunningStatus(transferId, size: 30_000),
            "queue-logout-missing",
            path,
            verified: false,
            CancellationToken.None);

        var service = new SoulseekTransferService(
            _client,
            new StubCredentials(null),
            CreateRepository(),
            NullLogger<SoulseekTransferService>.Instance);

        await Assert.ThrowsAsync<SoulseekUnavailableException>(() => service.WaitForCompletionAsync(
            "listener", transferId, path, "queue-logout-missing", cancellationToken: TestTimeout()));

        // slskd was never asked: there were no credentials to ask it with.
        Assert.Equal(0, _client.Polls);
    }

    /// <summary>
    ///     The terminal tolerance must not apply to a transfer that is still running.
    /// </summary>
    /// <remarks>
    ///     Two percent of 30,000 bytes is 600, so the terminal check accepts 29,999 as complete. Applied to a
    ///     transfer that has not finished, that would hand back a truncated file as the finished album.
    /// </remarks>
    [Fact]
    public async Task ActiveTransferDoesNotAcceptTerminalSizeTolerance()
    {
        var transferId = Guid.NewGuid();
        var path = await ClaimedPathAsync(transferId, "queue-short", "one-byte-short.flac", 29999);
        await CreateRepository().UpsertTransferAsync(
            RunningStatus(transferId, size: 30_000),
            "queue-short",
            path,
            verified: false,
            CancellationToken.None);

        // Still running, forever. The claim under test is that a file one byte short is never handed back as
        // the finished download, so the monitor is stopped rather than allowed to reach any terminal answer.
        _client.Transfer = RunningTransfer(transferId, size: 30_000);

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateService().WaitForCompletionAsync(
            "listener", transferId, path, "queue-short", cancellationToken: stop.Token));
    }

    [Fact]
    public async Task FileArrivingWhileStatusRemainsNonterminalIsACompletion()
    {
        var transferId = Guid.NewGuid();
        var path = await ClaimedPathAsync(transferId, "queue-early", "arrived-early.flac", 30_000);
        await CreateRepository().UpsertTransferAsync(
            RunningStatus(transferId, size: 30_000),
            "queue-early",
            path,
            verified: false,
            CancellationToken.None);

        _client.Transfer = RunningTransfer(transferId, size: 30_000);
        _client.ForgetAfterFirstPoll = true;

        var result = await CreateService().WaitForCompletionAsync(
            "listener", transferId, path, "queue-early", cancellationToken: TestTimeout());

        Assert.NotNull(result);
        Assert.Equal(path, result!.LocalPath);
    }

    [Fact]
    public async Task AFileOfUnknownAdvertisedSizeCannotBecomeACompletion()
    {
        // Nothing to measure it against. A file that cannot be checked against something is not evidence.
        var transferId = Guid.NewGuid();
        var path = await ClaimedPathAsync(transferId, "queue-unknown", "unknown-size.flac", 30000);
        await CreateRepository().UpsertTransferAsync(
            RunningStatus(transferId, size: 0),
            "queue-unknown",
            path,
            verified: false,
            CancellationToken.None);

        _client.Transfer = RunningTransfer(transferId, size: 0);

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateService().WaitForCompletionAsync(
            "listener", transferId, path, "queue-unknown", cancellationToken: stop.Token));
    }

    /// <summary>
    ///     The host going away is not the reader giving up, so the transfer must survive it.
    /// </summary>
    [Fact]
    public async Task ShutdownPreservesTransferForRecovery()
    {
        var path = Path.Join(_stagingDir, "interrupted.flac");
        var transferId = Guid.NewGuid();
        var owner = Guid.NewGuid();
        Assert.True(await CreateRepository().TryClaimSourcePathsAsync(owner, "queue-shutdown", "listener", "album\\01 - Angel.flac", [path]));
        await CreateRepository().AttachSourceClaimsAsync(owner, transferId);
        await CreateRepository().UpsertTransferAsync(
            RunningStatus(transferId, size: 30_000),
            "queue-shutdown",
            path,
            verified: false,
            CancellationToken.None);

        using var host = new CancellationTokenSource();
        using var item = CancellationTokenSource.CreateLinkedTokenSource(host.Token);
        host.Cancel();

        _client.Transfer = RunningTransfer(transferId, size: 30_000);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateService().WaitForCompletionAsync(
            "listener", transferId, path, "queue-shutdown", cancellationToken: item.Token, hostStoppingToken: host.Token));

        // slskd keeps fetching, so nothing was cancelled at the far end...
        Assert.Empty(_client.Cancelled);

        // ...and the row stays nonterminal so the next run finds the transfer to reattach to.
        var recorded = await CreateRepository().GetTransferAsync(transferId, CancellationToken.None);
        Assert.NotNull(recorded);
        Assert.False(recorded!.IsTerminal);
        Assert.NotEmpty(await CreateRepository().GetSourceClaimsForTransferAsync(transferId));
    }

    [Fact]
    public async Task ExplicitCancellationStillCancelsSlskd()
    {
        var path = Path.Join(_stagingDir, "reader-cancelled.flac");
        var transferId = Guid.NewGuid();
        var owner = Guid.NewGuid();
        Assert.True(await CreateRepository().TryClaimSourcePathsAsync(owner, "queue-cancelled", "listener", "album\\01 - Angel.flac", [path]));
        await CreateRepository().AttachSourceClaimsAsync(owner, transferId);
        await CreateRepository().UpsertTransferAsync(
            RunningStatus(transferId, size: 30_000),
            "queue-cancelled",
            path,
            verified: false,
            CancellationToken.None);

        using var item = new CancellationTokenSource();
        item.Cancel();

        _client.Transfer = RunningTransfer(transferId, size: 30_000);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateService().WaitForCompletionAsync(
            "listener", transferId, path, "queue-cancelled", cancellationToken: item.Token));

        // The reader gave up, so the transfer is stopped at the far end rather than left running unwatched.
        Assert.Contains(transferId, _client.Cancelled);
        Assert.Empty(await CreateRepository().GetSourceClaimsForTransferAsync(transferId));
    }

    [Fact]
    public async Task MonitoringFailurePreservesOwnershipAndDoesNotAuthorizePeerRetry()
    {
        var id = Guid.NewGuid();
        var path = Path.Join(_stagingDir, "unreachable.flac");
        var owner = Guid.NewGuid();
        Assert.True(await CreateRepository().TryClaimSourcePathsAsync(owner, "queue-monitor-error", "listener", "album\\01 - Angel.flac", [path]));
        await CreateRepository().AttachSourceClaimsAsync(owner, id);
        await CreateRepository().UpsertTransferAsync(RunningStatus(id, 30_000),
            "queue-monitor-error", path, false, CancellationToken.None);
        _client.StatusError = new SlskdApiException(401, null, "slskd rejected authentication.");

        var error = await Assert.ThrowsAsync<SoulseekTransferMonitoringException>(() =>
            CreateService().WaitForCompletionAsync("listener", id, path, "queue-monitor-error"));

        Assert.IsType<SlskdApiException>(error.InnerException);
        Assert.False(SoulseekPinnedCandidatePolicy.ShouldSearchAgain(error));
        var row = await CreateRepository().GetTransferAsync(id, CancellationToken.None);
        Assert.NotNull(row);
        Assert.False(row!.IsTerminal);
        Assert.False(row.Verified);
        Assert.Equal(30_000, row.Size);
        Assert.Equal("album\\01 - Angel.flac", row.Filename);
        Assert.Empty(_client.Cancelled);
        Assert.NotEmpty(await CreateRepository().GetSourceClaimsForTransferAsync(id));
    }

    [Fact]
    public async Task RemoteSuccessWithoutAFileNeverPublishesCompleted()
    {
        var events = new RecordingPublisher();
        _client.Transfer = CompletedTransfer(30_000);
        var result = await CreateService(events).WaitForCompletionAsync(
            "listener", _client.Transfer.Id!.Value,
            Path.Join(_stagingDir, "missing.flac"), "queue-remote-success");

        Assert.Null(result);
        Assert.DoesNotContain(events.Downloads, p => p.Stage == SoulseekDownloadStage.Completed);
        Assert.Contains(events.Downloads, p => p.Stage == SoulseekDownloadStage.Verifying);
        Assert.Equal(SoulseekDownloadStage.Failed, events.Downloads.Last().Stage);
        var row = await CreateRepository().GetTransferAsync(_client.Transfer.Id!.Value, CancellationToken.None);
        Assert.True(row!.IsTerminal);
        Assert.False(row.IsSuccessful);
        Assert.False(row.Verified);
        Assert.Contains("verified file", row.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifiedActiveFilePublishesCompletedAfterVerification()
    {
        var events = new RecordingPublisher();
        _client.Transfer = RunningTransfer(Guid.NewGuid(), 30_000);
        var path = await ClaimedPathAsync(_client.Transfer.Id!.Value, "queue-active-success", "active-success.flac", 30_000);
        var result = await CreateService(events).WaitForCompletionAsync(
            "listener", _client.Transfer.Id!.Value, path, "queue-active-success");

        Assert.NotNull(result);
        Assert.Equal(SoulseekDownloadStage.Completed, events.Downloads.Last().Stage);
        var row = await CreateRepository().GetTransferAsync(_client.Transfer.Id.Value, CancellationToken.None);
        Assert.True(row!.IsTerminal);
        Assert.True(row.Verified);
    }

    private static CancellationToken TestTimeout() => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

    /// <summary>A transfer slskd reports as still running, in the shape its API answers with.</summary>
    private static SlskdTransfer RunningTransfer(
        Guid id,
        long size,
        string filename = "album\\01 - Angel.flac") => new()
    {
        Id = id,
        Username = "listener",
        Filename = filename,
        Size = size,
        BytesTransferred = size / 2,
        State = 8, // InProgress
        StateInfo = SlskdTransferStateInfo.Decode(8)
    };

    /// <summary>The same transfer as this app records it, for the row a restart would read back.</summary>
    private static SoulseekTransferStatus RunningStatus(
        Guid id,
        long size,
        string filename = "album\\01 - Angel.flac") => new(
        id,
        "listener",
        filename,
        "enqueued",
        IsTerminal: false,
        IsSuccessful: false,
        size / 2,
        size,
        1024,
        PlaceInQueue: null,
        Progress: 50);

    [Fact]
    public async Task CompletedTransferWithAnEmptyFile_IsNotASuccess()
    {
        var path = CreateFile("empty.flac", 0);
        _client.Transfer = CompletedTransfer(size: 30_000);

        var transferId = _client.Transfer.Id!.Value;
        var result = await CreateService().WaitForCompletionAsync("listener", transferId, path, "queue-verify");

        Assert.Null(result);
    }

    [Fact]
    public async Task CompletedTransferWithATruncatedFile_IsNotASuccess()
    {
        // The peer advertised 30 MB but only 100 KB arrived, which is a failed transfer that slskd reported as
        // complete. Accepting it would put a broken file into the library.
        var path = CreateFile("truncated.flac", 100_000);
        _client.Transfer = CompletedTransfer(size: 30_000_000);

        var transferId = _client.Transfer.Id!.Value;
        var result = await CreateService().WaitForCompletionAsync("listener", transferId, path, "queue-verify");

        Assert.Null(result);
    }

    [Fact]
    public async Task CompletedTransferWithASlightlyShortFile_IsStillAccepted()
    {
        // A small shortfall is normal: peers mis-advertise length, and containers pad.
        _client.Transfer = CompletedTransfer(size: 30_000_000);

        var transferId = _client.Transfer.Id!.Value;
        var path = await ClaimedPathAsync(transferId, "queue-verify", "slightly-short.flac", 29_800_000, remoteFilename: "track.flac");
        var result = await CreateService().WaitForCompletionAsync("listener", transferId, path, "queue-verify");

        Assert.NotNull(result);
    }

    [Fact]
    public async Task FailedTransferWithAFileOnDisk_IsStillVerifiedAgainstTheFile()
    {
        // If a usable file is present the user has what they needed, so verification is allowed to succeed
        // even when slskd recorded a failure. The file, not slskd's opinion, is the source of truth.
        _client.Transfer = new SlskdTransfer
        {
            Id = Guid.NewGuid(),
            Username = "listener",
            Filename = "recovered.flac",
            Size = 30_000,
            State = 80, // Completed | Cancelled
            StateInfo = SlskdTransferStateInfo.Decode(80),
            BytesTransferred = 30_000
        };

        var transferId = _client.Transfer.Id!.Value;
        var path = await ClaimedPathAsync(transferId, "queue-verify", "recovered.flac", 30_000, remoteFilename: "recovered.flac");
        var result = await CreateService().WaitForCompletionAsync("listener", transferId, path, "queue-verify");

        Assert.NotNull(result);
    }

    [Fact]
    public async Task TerminalFailureIsRecordedAsUnverified()
    {
        // The transfer is recorded either way. Recording only the failure used to leave the table empty for
        // every download that worked, so the history endpoints had nothing to report.
        var path = Path.Join(_stagingDir, "gone.flac");
        var transfer = CompletedTransfer(size: 30_000);
        _client.Transfer = transfer;

        var result = await CreateService()
            .WaitForCompletionAsync("listener", transfer.Id!.Value, path, "queue-verify");
        Assert.Null(result);

        var recorded = await CreateRepository().GetTransferAsync(transfer.Id!.Value, CancellationToken.None);
        Assert.NotNull(recorded);
        Assert.False(recorded!.Verified);
        Assert.Equal("queue-verify", recorded.QueueUuid);
    }

    [Fact]
    public async Task TerminalSuccessIsRecordedAsVerifiedAndTiedToTheQueueItem()
    {
        var transfer = CompletedTransfer(size: 30_000);
        _client.Transfer = transfer;

        var path = await ClaimedPathAsync(transfer.Id!.Value, "queue-success", "recorded.flac", 30_000, remoteFilename: "track.flac");
        await CreateService().WaitForCompletionAsync("listener", transfer.Id!.Value, path, "queue-success");

        var recorded = await CreateRepository().GetTransferAsync(transfer.Id!.Value, CancellationToken.None);
        Assert.NotNull(recorded);
        Assert.True(recorded!.Verified);
        Assert.True(recorded.IsSuccessful);
        Assert.Equal("queue-success", recorded.QueueUuid);
        Assert.Equal(path, recorded.ExpectedPath);
    }

    [Fact]
    public async Task GetStatus_DecodesTheTransferState()
    {
        _client.Transfer = new SlskdTransfer
        {
            Id = Guid.NewGuid(),
            Username = "listener",
            Filename = "track.flac",
            Size = 1000,
            State = 8, // InProgress
            StateInfo = SlskdTransferStateInfo.Decode(8),
            BytesTransferred = 250,
            AverageSpeed = 512
        };

        var status = await CreateService().GetStatusAsync("listener", _client.Transfer.Id!.Value, CancellationToken.None);

        Assert.NotNull(status);
        Assert.Equal("in_progress", status!.State);
        Assert.False(status.IsTerminal);
        Assert.Equal(25, status.Progress);
    }

    [Fact]
    public async Task Enqueue_WithoutAPeerOrFilename_ReturnsNothingRatherThanCallingSlskd()
    {
        // A blank peer or filename can never produce a transfer, so it is rejected before the round trip.
        var service = CreateService();

        Assert.Null(await service.EnqueueAsync("", "track.flac", 100, CancellationToken.None));
        Assert.Null(await service.EnqueueAsync("listener", "   ", 100, CancellationToken.None));
    }

    [Fact]
    public async Task Transfer_WhenSoulseekIsNotConfigured_FailsWithAClearReason()
    {
        var service = new SoulseekTransferService(
            _client,
            new StubCredentials(null),
            CreateRepository(),
            NullLogger<SoulseekTransferService>.Instance);

        var error = await Assert.ThrowsAsync<SoulseekUnavailableException>(
            () => service.EnqueueAsync("listener", "track.flac", 100, CancellationToken.None));

        Assert.Contains("not configured", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ATransferSlskdForgetsIsStillRecordedAgainstTheItem()
    {
        // slskd drops a transfer it has given up on - a refused download is the common case - and the app then
        // reads nothing back. Without a record of that, a download that started, was refused and left no file
        // leaves no trace at all: the item fails with no transfer history, and neither the history endpoint nor
        // the peer policy has anything to point at. The file may still be on disk, so verification decides the
        // outcome; either way the attempt is recorded.
        _client.ForgetTransfer = true;
        _client.Transfer = new SlskdTransfer
        {
            Id = Guid.NewGuid(),
            Username = "listener",
            Filename = "vanished.flac",
            Size = 30_000,
            State = 8,
            StateInfo = SlskdTransferStateInfo.Decode(8),
            BytesTransferred = 4_000
        };

        var transferId = _client.Transfer.Id!.Value;
        var result = await CreateService().WaitForCompletionAsync(
            "listener", transferId, Path.Join(_stagingDir, "never-arrived.flac"), "queue-vanished");

        Assert.Null(result);

        var recorded = await CreateRepository().GetTransferAsync(transferId, CancellationToken.None);
        Assert.NotNull(recorded);
        Assert.Equal("queue-vanished", recorded!.QueueUuid);
        Assert.False(recorded.Verified);
        Assert.False(recorded.IsSuccessful);
    }

    [Fact]
    public async Task AFileFoundAfterSlskdForgetsTheTransferIsRecordedAsVerified()
    {
        // The same forgotten transfer, but the bytes did arrive: the download succeeded, and the record has to
        // say so rather than leaving a hole where a completed download should be.
        _client.Transfer = new SlskdTransfer
        {
            Id = Guid.NewGuid(),
            Username = "listener",
            Filename = "arrived-after-vanish.flac",
            Size = 30_000,
            State = 8,
            StateInfo = SlskdTransferStateInfo.Decode(8),
            BytesTransferred = 30_000
        };
        var transferId = _client.Transfer.Id!.Value;
        var arrived = await ClaimedPathAsync(
            transferId, "queue-vanished-success", "arrived-after-vanish.flac", 30_000,
            remoteFilename: "arrived-after-vanish.flac");

        _client.ForgetAfterFirstPoll = true;

        var result = await CreateService().WaitForCompletionAsync(
            "listener", transferId, arrived, "queue-vanished-success");

        Assert.NotNull(result);
        var recorded = await CreateRepository().GetTransferAsync(transferId, CancellationToken.None);
        Assert.NotNull(recorded);
        Assert.True(recorded!.Verified);
        Assert.True(recorded.IsSuccessful);
    }

    [Fact]
    public async Task ATransferThatStopsRespondingIsStillRecordedAgainstTheItem()
    {
        // slskd can stop answering mid-transfer - a timeout, a dropped connection - and then the poll throws
        // instead of returning a status. The attempt had a transfer, so the history has to show it: without this
        // the item fails with no record of the transfer that was actually running, and the peer that caused it
        // is never named anywhere.
        _client.Transfer = new SlskdTransfer
        {
            Id = Guid.NewGuid(),
            Username = "listener",
            Filename = "unresponsive.flac",
            Size = 30_000,
            State = 8,
            StateInfo = SlskdTransferStateInfo.Decode(8),
            BytesTransferred = 2_000
        };
        var transferId = _client.Transfer.Id!.Value;
        _client.ThrowAfterFirstPoll = true;

        await Assert.ThrowsAnyAsync<Exception>(() => CreateService().WaitForCompletionAsync(
            "listener", transferId, Path.Join(_stagingDir, "never.flac"), "queue-unresponsive"));

        var recorded = await CreateRepository().GetTransferAsync(transferId, CancellationToken.None);
        Assert.NotNull(recorded);
        Assert.Equal("queue-unresponsive", recorded!.QueueUuid);
        Assert.False(recorded.Verified);
        Assert.Equal("listener", recorded.Username);
    }

    /// <summary>
    ///     The regression for a Soulseek item frozen at 95% with no transfer history.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The download phase clamps its reported progress to 95 and only writes 100 after verification, so
    ///         a row sitting at 95 is a monitor that stopped before it decided anything. Monitoring is abandoned
    ///         when the reader cancels or when the queue reclaims the item as stalled, and this used to rethrow
    ///         straight past the recording step. The attempt then left no row at all, so the item had no
    ///         history, nothing could ever tell whether the bytes arrived, and the row sat at its last reported
    ///         percentage indefinitely.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task ATransferThatIsAbandonedMidFlightIsStillRecordedAgainstTheItem()
    {
        var transfer = new SlskdTransfer
        {
            Id = Guid.NewGuid(),
            Username = "listener",
            Filename = "stalled.flac",
            Size = 30_000,
            State = 8,
            StateInfo = SlskdTransferStateInfo.Decode(8),
            BytesTransferred = 28_500
        };
        _client.Transfer = transfer;

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateService().WaitForCompletionAsync(
            "listener",
            transfer.Id!.Value,
            Path.Join(_stagingDir, "never-arrived.flac"),
            "queue-stalled",
            null,
            null,
            cancellation.Token));

        var recorded = await CreateRepository().GetTransferAsync(transfer.Id!.Value, CancellationToken.None);
        Assert.NotNull(recorded);
        Assert.Equal("queue-stalled", recorded!.QueueUuid);
        Assert.Equal("listener", recorded.Username);
        Assert.False(recorded.Verified);
        Assert.False(recorded.IsSuccessful);
    }

    /// <summary>
    ///     A cancelled monitor frequently races a file that had in fact just finished, so the file decides the
    ///     recorded outcome rather than the cancellation.
    /// </summary>
    [Fact]
    public async Task AFileThatArrivedBeforeTheMonitorWasAbandonedIsRecordedAsVerified()
    {
        var transfer = new SlskdTransfer
        {
            Id = Guid.NewGuid(),
            Username = "listener",
            Filename = "finished-just-in-time.flac",
            Size = 30_000,
            State = 8,
            StateInfo = SlskdTransferStateInfo.Decode(8),
            BytesTransferred = 30_000
        };
        _client.Transfer = transfer;
        var arrived = await ClaimedPathAsync(
            transfer.Id!.Value, "queue-stalled-success", "finished-just-in-time.flac", 30_000,
            remoteFilename: "finished-just-in-time.flac");

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateService().WaitForCompletionAsync(
            "listener",
            transfer.Id!.Value,
            arrived,
            "queue-stalled-success",
            null,
            null,
            cancellation.Token));

        var recorded = await CreateRepository().GetTransferAsync(transfer.Id!.Value, CancellationToken.None);
        Assert.NotNull(recorded);
        Assert.True(recorded!.Verified);
        Assert.True(recorded.IsSuccessful);
    }

    /// <summary>
    ///     A cover image the peer shared is fetched and reported, which is the whole point of the opt-in.
    /// </summary>
    [Fact]
    public async Task FetchSidecars_TakesAPeerCoverAndReportsWhereItLanded()
    {
        _client.Transfer = CompletedTransfer(size: 120_000);

        // The sidecar reserves its destination before the enqueue and refuses a path that already holds a file, so
        // the file has to be written by the fake slskd during the transfer rather than being present beforehand.
        var cover = Path.Join(_stagingDir, "cover.jpg");
        _client.OnEnqueue = () => WriteBytes(cover, 120_000);

        var fetched = await CreateService().FetchSidecarsAsync(
            "listener",
            ["@@peer/Music/Album/cover.jpg"],
            _stagingDir);

        Assert.Equal(cover, fetched["@@peer/Music/Album/cover.jpg"]);
        Assert.Equal("@@peer/Music/Album/cover.jpg", Assert.Single(_client.Enqueued).Filename);
    }

    /// <summary>
    ///     A sidecar whose destination already holds a file is skipped rather than taken.
    /// </summary>
    /// <remarks>
    ///     The sidecar reservation is refused on an occupied path for the same reason the audio reservation is: a
    ///     file that was already there is not this peer's sidecar arriving, and taking it would report somebody
    ///     else's file as artwork for this release.
    /// </remarks>
    [Fact]
    public async Task FetchSidecars_SkipsAPathThatAlreadyHoldsAFile()
    {
        CreateFile("cover.jpg", 120_000);
        _client.Transfer = CompletedTransfer(size: 120_000);

        var fetched = await CreateService().FetchSidecarsAsync(
            "listener",
            ["@@peer/Music/Album/cover.jpg"],
            _stagingDir);

        Assert.Empty(fetched);
        Assert.Empty(_client.Enqueued);
    }

    /// <summary>
    ///     A sidecar is decoration. Nothing it can do may turn a completed audio download into a failed one,
    ///     so a refused enqueue yields nothing and no exception.
    /// </summary>
    [Fact]
    public async Task FetchSidecars_ARefusedCoverIsSkippedRatherThanThrown()
    {
        _client.ThrowOnEnqueue = true;

        var fetched = await CreateService().FetchSidecarsAsync(
            "listener",
            ["@@peer/Music/Album/cover.jpg"],
            _stagingDir);

        Assert.Empty(fetched);
    }

    /// <summary>
    ///     One sidecar that will not arrive must not cost the ones that do.
    /// </summary>
    [Fact]
    public async Task FetchSidecars_OneFailureDoesNotStopTheOthers()
    {
        _client.Transfer = CompletedTransfer(size: 120_000);

        var cover = Path.Join(_stagingDir, "cover.jpg");

        // Only the cover is actually delivered; the lyrics file's transfer reports complete without its bytes ever
        // landing, which is the failure this asserts is survivable.
        _client.OnEnqueue = () =>
        {
            if (_client.Enqueued.Count == 2)
            {
                WriteBytes(cover, 120_000);
            }
        };

        var fetched = await CreateService().FetchSidecarsAsync(
            "listener",
            ["@@peer/Music/Album/missing.lrc", "@@peer/Music/Album/cover.jpg"],
            _stagingDir);

        // The lyrics file is reported as gone by the peer, and the cover still arrives.
        Assert.Equal(cover, fetched["@@peer/Music/Album/cover.jpg"]);
    }

    [Fact]
    public async Task FetchSidecars_DoNothingWithoutARequest()
    {
        Assert.Empty(await CreateService().FetchSidecarsAsync("listener", [], _stagingDir));
        Assert.Empty(await CreateService().FetchSidecarsAsync("", ["cover.jpg"], _stagingDir));
        Assert.Empty(await CreateService().FetchSidecarsAsync("listener", ["cover.jpg"], ""));
        Assert.Empty(_client.Enqueued);
    }

    [Fact]
    public async Task FindTransfer_IgnoresTheSameFileHeldByADifferentPeer()
    {
        // Adoption must not take a transfer that belongs to someone else: it would download the wrong peer's
        // copy under this item's name and, worse, credit the failure to a peer that never failed.
        _client.Downloads =
        [
            new SlskdTransfer
            {
                Id = Guid.NewGuid(),
                Username = "somebody-else",
                Filename = "a.flac",
                Size = 30_000,
                State = 8
            }
        ];

        var found = await CreateService().FindTransferAsync("listener", "a.flac", CancellationToken.None);

        Assert.Null(found);
    }

    [Fact]
    public async Task FindTransfer_IgnoresADifferentFileFromTheSamePeer()
    {
        _client.Downloads =
        [
            new SlskdTransfer
            {
                Id = Guid.NewGuid(),
                Username = "listener",
                Filename = "another-track.flac",
                Size = 30_000,
                State = 8
            }
        ];

        var found = await CreateService().FindTransferAsync("listener", "a.flac", CancellationToken.None);

        Assert.Null(found);
    }

    [Fact]
    public async Task FindTransaction_MatchesOnTheExactPeerAndPath()
    {
        // The pair is the identity. Either half matching on its own is not a transfer for this item.
        var wanted = Guid.NewGuid();
        _client.Downloads =
        [
            new SlskdTransfer { Id = Guid.NewGuid(), Username = "other", Filename = "a.flac", Size = 1, State = 8 },
            new SlskdTransfer { Id = Guid.NewGuid(), Username = "listener", Filename = "b.flac", Size = 1, State = 8 },
            new SlskdTransfer { Id = wanted, Username = "LISTENER", Filename = "A.FLAC", Size = 1, State = 8 }
        ];

        var found = await CreateService().FindTransferAsync("listener", "a.flac", CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(wanted, found!.TransferId);
    }

    /// <summary>
    ///     slskd writes the finished file into its own download root, not into DeezSpoTag's staging path.
    /// </summary>
    /// <remarks>
    ///     The app tells slskd a peer and a path and nothing else, so slskd decides both the directory and the
    ///     name - and with its default destination template those are the peer's own source directory and file
    ///     name. The transfer therefore finished, the file was on disk, and the app reported "no file was found"
    ///     because it only ever looked at its own path.
    /// </remarks>
    [Theory]
    [InlineData(@"@@peer\Music\Live and Die in Afrika\04 Isabella.mp3", "Live and Die in Afrika", "04 Isabella.mp3")]
    [InlineData("@@peer/Music/Live and Die in Afrika/04 Isabella.mp3", "Live and Die in Afrika", "04 Isabella.mp3")]
    public async Task TheCompletedFileIsFoundUnderTheConfiguredSlskdRoot(string remotePath, string directory, string file)
    {
        var root = Path.Join(_tempRoot, "slskd", "downloads");
        var resolved = Path.Join(root, directory, file);

        _client.Transfer = TransferFor(remotePath, 30_000);

        // Claimed at the path slskd will actually write, which is not the staging path the app passed in. Reserving
        // the staging path instead would leave the completed file unclaimed, and an unclaimed file is refused. The
        // claim is attached to the very transfer being monitored, since that is what makes it this download's file.
        await ClaimExternalPathAsync(resolved, "peer", remotePath, _client.Transfer.Id!.Value);
        var actual = CreateUnder(root, Path.Join(directory, file), 30_000);
        var expected = Path.Join(_stagingDir, "Isabella.flac");

        var result = await CreateService().WaitForCompletionAsync(
            "peer", _client.Transfer.Id!.Value, expected, "queue-root", root);

        Assert.NotNull(result);
        Assert.Equal(actual, result!.LocalPath);
        Assert.Equal(30_000, result.Size);
    }

    [Fact]
    public async Task ARemoteFileWithNoSourceDirectoryLandsAtTheRoot()
    {
        var root = Path.Join(_tempRoot, "slskd", "downloads");
        _client.Transfer = TransferFor("loose.flac", 30_000);
        await ClaimExternalPathAsync(Path.Join(root, "loose.flac"), "peer", "loose.flac", _client.Transfer.Id!.Value);
        var actual = CreateUnder(root, "loose.flac", 30_000);

        var result = await CreateService().WaitForCompletionAsync(
            "peer", _client.Transfer.Id!.Value, Path.Join(_stagingDir, "Isabella.flac"), "queue-root", root);

        Assert.NotNull(result);
        Assert.Equal(actual, result!.LocalPath);
    }

    [Fact]
    public async Task ACompletedLookingFileInsideTopLevelIncompleteIsNeverVerified()
    {
        var root = Path.Join(_tempRoot, "downloads");
        var incomplete = CreateUnder(root, Path.Join("incomplete", "track.flac"), 30_000);
        _client.Transfer = TransferFor(@"@@peer\Music\incomplete\track.flac", 30_000);

        var result = await CreateService().WaitForCompletionAsync(
            "peer", _client.Transfer.Id!.Value, Path.Join(_stagingDir, "track.flac"), "queue-incomplete", root);

        Assert.Null(result);
        Assert.True(File.Exists(incomplete));
    }

    [Fact]
    public async Task TheFileAtTheExpectedPathIsStillUsedWhenItExists()
    {
        // The shared-mount arrangement, where slskd writes straight into the engine's staging path, must keep
        // working with no root configured at all.
        _client.Transfer = TransferFor(@"@@peer\Music\Album\shared-mount.flac", 30_000);
        var expected = await ClaimedPathAsync(
            _client.Transfer.Id!.Value, "queue-shared", "shared-mount.flac", 30_000,
            username: "peer", remoteFilename: @"@@peer\Music\Album\shared-mount.flac");

        var result = await CreateService().WaitForCompletionAsync(
            "peer", _client.Transfer.Id!.Value, expected, "queue-shared");

        Assert.NotNull(result);
        Assert.Equal(expected, result!.LocalPath);
    }

    [Theory]
    [InlineData("../outside.flac")]
    [InlineData("../../outside.flac")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\notepad.exe")]
    public async Task APathThatWouldLeaveTheConfiguredRootIsRefused(string remotePath)
    {
        // The remote name comes from the network, so it is untrusted input. The decoy is written exactly where a
        // permissive implementation would look - the naive join of the root and the remote name - so this fails
        // if the guard is removed, rather than passing because the file happens not to be there.
        var root = Path.Join(_tempRoot, "slskd", "downloads");
        var decoy = WriteAbsolute(Path.GetFullPath(Path.Join(root, remotePath)), 30_000);
        _client.Transfer = TransferFor(remotePath, 30_000);

        var result = await CreateService().WaitForCompletionAsync(
            "peer", _client.Transfer.Id!.Value, Path.Join(_stagingDir, "Isabella.flac"), "queue-escape", root);

        Assert.Null(result);
        Assert.True(File.Exists(decoy), $"The decoy at {decoy} must be left untouched.");
    }

    private string WriteAbsolute(string path, long length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[length];
        Array.Fill(bytes, (byte)1);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task ADirectoryWhereTheFileShouldBeIsNotADownload()
    {
        var root = Path.Join(_tempRoot, "slskd", "downloads");
        Directory.CreateDirectory(Path.Join(root, "Album"));
        Directory.CreateDirectory(Path.Join(root, "Album", "01 track.flac"));
        _client.Transfer = TransferFor(@"@@peer\Album\01 track.flac", 30_000);

        var result = await CreateService().WaitForCompletionAsync(
            "peer", _client.Transfer.Id!.Value, Path.Join(_stagingDir, "Isabella.flac"), "queue-dir", root);

        Assert.Null(result);
    }

    [Fact]
    public async Task NoRootConfiguredAndNoExpectedFile_FailsWithoutLookingAnywhereElse()
    {
        // A missing setting is a configuration answer, not a reason to scan the disk. The transfer is not
        // verified and the item says so.
        _client.Transfer = TransferFor(@"@@peer\Album\01 track.flac", 30_000);

        var result = await CreateService().WaitForCompletionAsync(
            "peer", _client.Transfer.Id!.Value, Path.Join(_stagingDir, "Isabella.flac"), "queue-unconfigured");

        Assert.Null(result);
    }

    [Fact]
    public async Task TheResolvedFileMustStillPassTheSizeChecks()
    {
        // Verification is not relaxed for a resolved path: a file the peer only half sent is still a failure,
        // and an empty one is still a failure.
        var root = Path.Join(_tempRoot, "slskd", "downloads");
        CreateUnder(root, Path.Join("Album", "short.flac"), 100_000);
        _client.Transfer = TransferFor(@"@@peer\Album\short.flac", 30_000_000);

        var result = await CreateService().WaitForCompletionAsync(
            "peer", _client.Transfer.Id!.Value, Path.Join(_stagingDir, "Isabella.flac"), "queue-short", root);

        Assert.Null(result);
    }

    [Fact]
    public async Task TheResolvedFileMustNotBeEmpty()
    {
        var root = Path.Join(_tempRoot, "slskd", "downloads");
        CreateUnder(root, Path.Join("Album", "empty.flac"), 0);
        _client.Transfer = TransferFor(@"@@peer\Album\empty.flac", 30_000);

        var result = await CreateService().WaitForCompletionAsync(
            "peer", _client.Transfer.Id!.Value, Path.Join(_stagingDir, "Isabella.flac"), "queue-empty", root);

        Assert.Null(result);
    }

    private SlskdTransfer TransferFor(string remotePath, long size) => new()
    {
        Id = Guid.NewGuid(),
        Username = "peer",
        Filename = remotePath,
        Size = size,
        State = 48, // Completed | Succeeded
        StateInfo = SlskdTransferStateInfo.Decode(48),
        BytesTransferred = size
    };

    private string CreateUnder(string root, string relative, long length)
    {
        var path = Path.Join(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (length <= 0)
        {
            File.WriteAllBytes(path, []);
            return path;
        }

        var bytes = new byte[length];
        Array.Fill(bytes, (byte)1);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string CreateFile(string name, long length)
    {
        var path = Path.Join(_stagingDir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (length <= 0)
        {
            File.WriteAllBytes(path, []);
            return path;
        }

        var bytes = new byte[length];
        Array.Fill(bytes, (byte)1);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>
    ///     The path a completed file will land at, reserved and attached to a transfer before it is written.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Every completion test now needs explicit ownership evidence, because a transfer may only accept a file
    ///         at a path its own claims cover. A claim is refused on a path that already holds a file - correctly, since
    ///         that is exactly the adoption this prevents - so the reservation has to happen first and the file written
    ///         afterwards. That is the order the download service uses, and keeping it here means the tests exercise the
    ///         real sequence rather than a shortcut that bypasses it.
    ///     </para>
    ///     <para>
    ///         Tests that expect a file to be <em>refused</em> pass <paramref name="claim"/> false and so never reserve
    ///         anything, which is what proves an unclaimed path is not adopted.
    ///     </para>
    /// </remarks>
    private async Task<string> ClaimedPathAsync(
        Guid transferId,
        string queueUuid,
        string name,
        long length,
        bool claim = true,
        string username = "listener",
        string remoteFilename = "album\\01 - Angel.flac")
    {
        var path = Path.Join(_stagingDir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (claim)
        {
            var repository = CreateRepository();
            var ownershipId = Guid.NewGuid();

            Assert.True(
                await repository.TryClaimSourcePathsAsync(ownershipId, queueUuid, username, remoteFilename, [path], CancellationToken.None),
                $"The test could not reserve {path} before writing it.");

            await repository.AttachSourceClaimsAsync(ownershipId, transferId, CancellationToken.None);
        }

        if (length <= 0)
        {
            File.WriteAllBytes(path, []);
            return path;
        }

        WriteBytes(path, length);
        return path;
    }

    /// <summary>
    ///     Reserves a path outside the staging directory, for a file slskd writes into its own download root.
    /// </summary>
    /// <remarks>
    ///     A claim is refused on a path that already holds a file, and these tests need the file present so the
    ///     root-relative resolution has something to find. Reserved first and written afterwards, which is the
    ///     production order: the download service reserves the resolved source path before it enqueues, and slskd
    ///     writes there once the transfer runs.
    /// </remarks>
    private async Task ClaimExternalPathAsync(string path, string username, string remoteFilename, Guid transferId)
    {
        var repository = CreateRepository();
        var ownershipId = Guid.NewGuid();

        Assert.True(
            await repository.TryClaimSourcePathsAsync(ownershipId, "queue-root", username, remoteFilename, [path], CancellationToken.None),
            $"The test could not reserve {path} before writing it.");

        await repository.AttachSourceClaimsAsync(ownershipId, transferId, CancellationToken.None);
    }

    /// <summary>
    ///     Fills an already-reserved path with <paramref name="length"/> bytes of filler.
    /// </summary>
    /// <remarks>
    ///     Split out from file creation so a test can write the bytes after reserving the path but before monitoring
    ///     begins, which is the sequence a real transfer follows. Without the split a reserved path could not be
    ///     written at all, since the claim is refused on a path that already holds a file.
    /// </remarks>
    private static void WriteBytes(string path, long length)
    {
        if (length <= 0)
        {
            File.WriteAllBytes(path, []);
            return;
        }

        var bytes = new byte[length];
        Array.Fill(bytes, (byte)1);
        File.WriteAllBytes(path, bytes);
    }

    private static SlskdTransfer CompletedTransfer(long size) => new()
    {
        Id = Guid.NewGuid(),
        Username = "listener",
        Filename = "track.flac",
        Size = size,
        State = 48, // Completed | Succeeded
        StateInfo = SlskdTransferStateInfo.Decode(48),
        BytesTransferred = size
    };

    private sealed class StubCredentials(SlskdCredentials? credentials) : ISoulseekCredentialProvider
    {
        public Task<SlskdCredentials?> GetCredentialsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(credentials);
    }

    private sealed class RecordingPublisher : ISoulseekRealtimePublisher
    {
        public List<SoulseekDownloadProgress> Downloads { get; } = [];
        public void PublishDownloadUpdate(SoulseekDownloadProgress progress) => Downloads.Add(progress);
        public void PublishConnectionState(SoulseekConnectionStatus status) { }
        public void PublishEngineHealth(string state, string? message) { }
        public void PublishSearchUpdate(SoulseekSearchProgress progress) { }
        public void PublishSearchResult(SoulseekSearchOutcome outcome) { }
        public void PublishShareSyncUpdate(SoulseekShareReconciliation reconciliation) { }
        public void PublishShareScanUpdate(SoulseekShareScanStatus status) { }
        public void PublishImportUpdate(SoulseekImportUpdate update) { }
    }

    private sealed class FakeSlskdClient : ISlskdClient
    {
        public SlskdTransfer Transfer { get; set; } = new();

        public IReadOnlyList<SlskdTransfer> Downloads { get; set; } = [];

        /// <summary>slskd has no record of the transfer at all, the way it behaves after a refusal.</summary>
        public bool ForgetTransfer { get; set; }

        /// <summary>The transfer is readable once, then gone.</summary>
        public bool ForgetAfterFirstPoll { get; set; }

        /// <summary>The transfer answers once, then slskd stops responding at all.</summary>
        public bool ThrowAfterFirstPoll { get; set; }

        /// <summary>slskd refuses the enqueue outright.</summary>
        public bool ThrowOnEnqueue { get; set; }

        /// <summary>Every file this client was asked to take, in order.</summary>
        public List<SlskdQueueDownload> Enqueued { get; } = new();

        private int _polls;

        /// <summary>Fails the very first status read, so only persisted identity can answer for the file.</summary>
        public bool ThrowImmediately { get; set; }

        /// <summary>The transfers this client was asked to cancel at the far end.</summary>
        public List<Guid> Cancelled { get; } = [];

        /// <summary>How many times a status was asked for, so a test can prove the file needed no answer.</summary>
        public int Polls => _polls;

        public Action<int>? OnPoll { get; set; }

        /// <summary>
        ///     Runs just after a file is enqueued, so a test can deliver that file's bytes the way slskd would.
        /// </summary>
        /// <remarks>
        ///     Needed because a reservation is refused on a path that already holds a file. A test cannot create the
        ///     completed file first and then claim the path; the bytes have to arrive after the claim, which is the
        ///     real order and the one the production code is built around.
        /// </remarks>
        public Action? OnEnqueue { get; set; }

        public Exception? StatusError { get; set; }

        public Task<SlskdTransfer?> GetDownloadAsync(SlskdCredentials credentials, string username, Guid transferId, CancellationToken cancellationToken = default)
        {
            _polls++;
            OnPoll?.Invoke(_polls);
            if (StatusError is not null) throw StatusError;
            if (ThrowImmediately)
            {
                throw new TimeoutException("slskd did not respond in time.");
            }

            if (ThrowAfterFirstPoll && _polls > 1)
            {
                throw new TimeoutException("slskd did not respond in time.");
            }

            if (ForgetTransfer || (ForgetAfterFirstPoll && _polls > 1))
            {
                return Task.FromResult<SlskdTransfer?>(null);
            }

            return Task.FromResult<SlskdTransfer?>(Transfer);
        }

        public Task<SlskdServerState> GetServerStateAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SlskdServerState> ConnectAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DisconnectAsync(SlskdCredentials credentials, string? message = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SlskdSearch> StartSearchAsync(SlskdCredentials credentials, SlskdSearchRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SlskdSearch?> GetSearchAsync(SlskdCredentials credentials, Guid searchId, bool includeResponses = false, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SlskdSearch>> ListSearchesAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SlskdSearchResponse>> GetSearchResponsesAsync(SlskdCredentials credentials, Guid searchId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task CancelSearchAsync(SlskdCredentials credentials, Guid searchId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> DeleteSearchAsync(SlskdCredentials credentials, Guid searchId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SlskdUserStatus?> GetUserStatusAsync(SlskdCredentials credentials, string username, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SlskdDirectory>> BrowseUserAsync(SlskdCredentials credentials, string username, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SlskdDirectory>> BrowseUserDirectoryAsync(SlskdCredentials credentials, string username, string? directory, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SlskdTransfer>> EnqueueDownloadsAsync(SlskdCredentials credentials, string username, IReadOnlyList<SlskdQueueDownload> downloads, CancellationToken cancellationToken = default)
        {
            Enqueued.AddRange(downloads);
            Transfer = Transfer with { Id = Guid.NewGuid(), Filename = downloads[0].Filename, Username = username };

            // The bytes arrive after the enqueue, which is the only order a reservation can be made in: the claim is
            // refused on a path that already holds a file, so a test that pre-created the file could never own it.
            OnEnqueue?.Invoke();

            if (ThrowOnEnqueue)
            {
                return Task.FromException<IReadOnlyList<SlskdTransfer>>(
                    new SlskdApiException(500, "boom", "slskd returned HTTP 500."));
            }

            if (ForgetTransfer)
            {
                return Task.FromResult<IReadOnlyList<SlskdTransfer>>([]);
            }

            return Task.FromResult<IReadOnlyList<SlskdTransfer>>([Transfer]);
        }

        public Task<IReadOnlyList<SlskdTransfer>> ListDownloadsAsync(SlskdCredentials credentials, bool includeRemoved = false, CancellationToken cancellationToken = default)
            => Task.FromResult(Downloads);

        public Task<int?> GetDownloadPositionAsync(SlskdCredentials credentials, string username, Guid transferId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task CancelDownloadAsync(SlskdCredentials credentials, string username, Guid transferId, bool remove = false, CancellationToken cancellationToken = default)
        {
            Cancelled.Add(transferId);
            return Task.CompletedTask;
        }

        public Task ClearCompletedDownloadsAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<SlskdShare>> ListSharesAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SlskdDirectory>> BrowseSharesAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task RescanSharesAsync(SlskdCredentials credentials, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
