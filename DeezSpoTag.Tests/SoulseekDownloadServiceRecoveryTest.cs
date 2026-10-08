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
///     The engine must find the transfer slskd really started, whether or not the enqueue said so.
/// </summary>
/// <remarks>
///     <para>
///         slskd answers the enqueue request after a short internal wait, and it has two ways of saying nothing
///         useful: a server error, and a plain success with no transfer in it. The first was handled by looking
///         for the transfer afterwards; the second was not, because a null id with no exception went straight to
///         the file check. A download that was running - and, in the observed case, one that had already
///         finished - was reported as "slskd started no transfer".
///     </para>
///     <para>
///         These exercise the orchestration itself rather than the source text, because the difference between
///         the two paths is whether the transfer list is read at all.
///     </para>
/// </remarks>
public sealed class SoulseekDownloadServiceRecoveryTest : IDisposable
{
    private const string Peer = "listener";
    private const string RemotePath = @"@@listener\Music\Album\04 Isabella.mp3";
    private const long RemoteSize = 13_764_977;

    private readonly string _tempRoot;
    private readonly string _stagingDir;
    private readonly ScriptedTransferService _transfer = new();

    public SoulseekDownloadServiceRecoveryTest()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-soulseek-recovery-" + Path.GetRandomFileName());
        _stagingDir = Path.Join(_tempRoot, "staging");
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
    public async Task EnqueueWithoutAnId_AdoptsTheTransferSlskdActuallyStarted()
    {
        // slskd's own 0.26 answer: the transfer exists, and the reply carries no id to it. The transfer list is
        // the only place that id can come from, so it has to be read before the download gives up on the peer.
        var started = Guid.NewGuid();
        _transfer.EnqueueResult = null;
        _transfer.FindResult = Status(started);
        var completed = Path.Join(_stagingDir, "Isabella.mp3");
        File.WriteAllBytes(completed, new byte[RemoteSize]);
        _transfer.CompletedFile = new SoulseekCompletedFile(Peer, RemotePath, completed, RemoteSize);

        var file = await CreateService().DownloadAsync(
            Request(), "queue-recover", Path.Join(_stagingDir, "expected.flac"), null, CancellationToken.None);

        Assert.Equal(completed, file.LocalPath);
        Assert.Equal(1, _transfer.FindCalls);
        Assert.Equal(1, _transfer.EnqueueCalls);
        Assert.Equal([started], _transfer.WaitedForIds);

        // The adopted transfer is recorded against the item, not just watched.
        var recorded = await CreateRepository().GetTransferAsync(started, CancellationToken.None);
        Assert.NotNull(recorded);
        Assert.Equal("queue-recover", recorded!.QueueUuid);
        Assert.True(recorded.Verified);
    }

    [Fact]
    public async Task EnqueueThatThrows_AdoptsTheTransferAndKeepsWatchingIt()
    {
        var started = Guid.NewGuid();
        _transfer.EnqueueException = new SlskdApiException(500, null, "slskd returned HTTP 500.");
        _transfer.FindResult = Status(started);
        var completed = Path.Join(_stagingDir, "thrown.mp3");
        File.WriteAllBytes(completed, new byte[RemoteSize]);
        _transfer.CompletedFile = new SoulseekCompletedFile(Peer, RemotePath, completed, RemoteSize);

        var file = await CreateService().DownloadAsync(
            Request(), "queue-throw", Path.Join(_stagingDir, "expected.flac"), null, CancellationToken.None);

        Assert.Equal(completed, file.LocalPath);
        Assert.Equal([started], _transfer.WaitedForIds);
        Assert.Equal(1, _transfer.EnqueueCalls);
    }

    [Fact]
    public async Task EnqueueThatThrowsWithNothingToAdopt_Fails()
    {
        // A failed enqueue response cannot prove that nothing started. Preserve its diagnostic cause and the
        // reservation, and stop the peer retry loop while the remote outcome is uncertain.
        _transfer.EnqueueException = new SlskdApiException(500, null, "slskd returned HTTP 500.");
        _transfer.FindResult = null;

        var error = await Assert.ThrowsAsync<SoulseekTransferMonitoringException>(() => CreateService().DownloadAsync(
            Request(), "queue-no-adopt", Path.Join(_stagingDir, "expected.flac"), null, CancellationToken.None));

        Assert.IsType<SlskdApiException>(error.InnerException);
        Assert.Empty(_transfer.WaitedForIds);
        Assert.Equal(1, _transfer.EnqueueCalls);
        Assert.NotEmpty(await CreateRepository().GetSourceClaimsForQueueAsync("queue-no-adopt"));
    }

    /// <summary>
    ///     A null transfer id with nothing to adopt is a monitoring failure, not an invented transfer.
    /// </summary>
    /// <remarks>
    ///     This used to fall through to a disk check and then report "Soulseek started no transfer". There is no
    ///     longer a disk check to fall through to: without a transfer id there is nothing to attach the reserved
    ///     paths to, so a file at the destination cannot be attributed to this attempt and must not be taken. The
    ///     answer is a monitoring failure, which says the app lost track of a transfer rather than claiming the
    ///     network supplied nothing - a different problem with a different action for the reader.
    /// </remarks>
    [Fact]
    public async Task EnqueueWithoutAnIdAndNothingToAdopt_ReportsMonitoringFailure()
    {
        _transfer.EnqueueResult = null;
        _transfer.FindResult = null;

        var error = await Assert.ThrowsAsync<SoulseekTransferMonitoringException>(() => CreateService().DownloadAsync(
            Request(), "queue-nothing", Path.Join(_stagingDir, "expected.flac"), null, CancellationToken.None));

        Assert.Contains("reserved paths cannot be safely attached or released", error.Message, StringComparison.Ordinal);
        Assert.Empty(_transfer.WaitedForIds);
        Assert.Equal([], _transfer.GetStatusCalls);
    }

    /// <summary>
    ///     A file at the destination with no transfer id is refused, even though its size is exactly right.
    /// </summary>
    /// <remarks>
    ///     The reproduced adoption this repair exists to stop. slskd reported no transfer, so the app has no
    ///     evidence that this file is this download's, and taking it hands back a track that was never fetched for
    ///     this item. The reservation is also kept, because the transfer slskd may have started is still
    ///     unaccounted for and a second attempt must not fetch the same file alongside it.
    /// </remarks>
    [Fact]
    public async Task AFileThatArrivedWithoutATransferId_IsNotAdopted()
    {
        var arrived = Path.Join(_stagingDir, "arrived-anyway.flac");
        File.WriteAllBytes(arrived, new byte[RemoteSize]);
        _transfer.EnqueueResult = null;
        _transfer.FindResult = null;

        var error = await Assert.ThrowsAsync<SoulseekTransferMonitoringException>(() => CreateService().DownloadAsync(
            Request(), "queue-arrived", arrived, null, CancellationToken.None));

        Assert.Contains("already in use", error.Message, StringComparison.Ordinal);
        Assert.Empty(_transfer.WaitedForIds);
    }

    [Fact]
    public async Task CancellationDuringEnqueue_IsNotSwallowedIntoAdoption()
    {
        // A cancelled item has no transfer to adopt, and must not spend six seconds looking for one.
        _transfer.EnqueueException = new TaskCanceledException("cancelled");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateService().DownloadAsync(
            Request(), "queue-cancel", Path.Join(_stagingDir, "expected.flac"), null, CancellationToken.None));

        Assert.Equal(0, _transfer.FindCalls);
    }

    [Fact]
    public async Task TheConfiguredSlskdRootReachesTheTransferService()
    {
        // The request builder reads the setting; this is where it has to arrive, because the transfer service is
        // what looks in that directory.
        var started = Guid.NewGuid();
        _transfer.EnqueueResult = started;
        _transfer.CompletedFile = new SoulseekCompletedFile(Peer, RemotePath, "/tmp/isabella.mp3", RemoteSize);

        var request = Request();
        request.CompletedDownloadsRoot = "/home/user/slskd/downloads";
        await CreateService().DownloadAsync(
            request, "queue-root", Path.Join(_stagingDir, "expected.flac"), null, CancellationToken.None);

        Assert.Equal("/home/user/slskd/downloads", _transfer.RequestedRoot);
    }

    /// <summary>
    ///     The verified file has to end up where the rest of the pipeline expects it.
    /// </summary>
    /// <remarks>
    ///     slskd writes into its own download root with the peer's own directory and file name, so a verified
    ///     download arrives at a path the pipeline has never heard of. Promotion is what reconciles the two, and
    ///     until it exists the file is found by the engine and invisible to everything after it.
    /// </remarks>
    [Fact]
    public async Task TheVerifiedFileIsMovedIntoTheExpectedStagingPath()
    {
        var slskdRoot = Path.Join(_tempRoot, "slskd", "downloads");
        var source = WriteBytes(Path.Join(slskdRoot, "Live and Die in Afrika", "04 Isabella.mp3"), 1024);
        var payload = new SoulseekQueueItem
        {
            Id = "queue-promote",
            FilePath = Path.Join(_tempRoot, "staging", "Sauti Sol - Isabella.flac")
        };

        await SeedVerifiedSourceAsync(payload, source);

        var promoted = await CreateService().PromoteAcceptedAudioAsync(payload, source, CancellationToken.None);

        Assert.Equal(Path.GetFullPath(payload.FilePath), promoted);
        Assert.True(File.Exists(promoted));
        Assert.Equal(1024, new FileInfo(promoted).Length);
        Assert.False(File.Exists(source), "The source must not be left behind once the file is in place.");
        Assert.Equal(promoted, payload.AcquiredAudioPath);
        Assert.True(payload.AudioAcquired);
    }

    [Fact]
    public async Task PromotionCreatesTheDestinationDirectory()
    {
        var source = WriteBytes(Path.Join(_tempRoot, "slskd", "downloads", "loose.flac"), 512);
        var destination = Path.Join(_tempRoot, "staging", "Artist", "Album", "01 - Track.flac");
        var payload = new SoulseekQueueItem { Id = "queue-mkdir", FilePath = destination };

        await SeedVerifiedSourceAsync(payload, source);

        var promoted = await CreateService().PromoteAcceptedAudioAsync(payload, source, CancellationToken.None);

        Assert.Equal(Path.GetFullPath(destination), promoted);
        Assert.True(File.Exists(promoted));
    }

    [Fact]
    public async Task PromotionDoesNothingWhenTheFileIsAlreadyWhereItShouldBe()
    {
        // The shared-mount arrangement: slskd wrote straight into the engine's staging path, so there is nothing
        // to move and the file must survive untouched.
        var expected = WriteBytes(Path.Join(_tempRoot, "staging", "already-there.flac"), 256);
        var payload = new SoulseekQueueItem { Id = "queue-same", FilePath = expected };

        await SeedVerifiedSourceAsync(payload, expected);

        var promoted = await CreateService().PromoteAcceptedAudioAsync(payload, expected, CancellationToken.None);

        Assert.Equal(Path.GetFullPath(expected), promoted);
        Assert.Equal(256, new FileInfo(expected).Length);
    }

    [Fact]
    public async Task PromotionRefusesToOverwriteAnExistingFile()
    {
        // Overwriting would destroy a file that is already there, and would do it silently.
        var source = WriteBytes(Path.Join(_tempRoot, "slskd", "downloads", "mine.flac"), 111);
        var destination = WriteBytes(Path.Join(_tempRoot, "staging", "theirs.flac"), 222);
        var payload = new SoulseekQueueItem { Id = "queue-clash", FilePath = destination };

        await SeedVerifiedSourceAsync(payload, source);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => CreateService()
            .PromoteAcceptedAudioAsync(payload, source, CancellationToken.None));

        Assert.Contains(destination, error.Message, StringComparison.Ordinal);
        Assert.Equal(222, new FileInfo(destination).Length);
        Assert.True(File.Exists(source), "The only verified copy must survive a refused promotion.");
    }

    [Fact]
    public async Task AMissingSourceIsNotSilentlyReportedAsPromoted()
    {
        var destination = Path.Join(_tempRoot, "staging", "gone.flac");
        var payload = new SoulseekQueueItem { Id = "queue-gone", FilePath = destination };

        await Assert.ThrowsAnyAsync<Exception>(() => CreateService()
            .PromoteAcceptedAudioAsync(payload, Path.Join(_tempRoot, "slskd", "downloads", "never.flac"), CancellationToken.None));

        Assert.False(File.Exists(destination));
    }

    /// <summary>
    ///     Copying across filesystems is the fallback, and it is the only place a verified file can be lost.
    /// </summary>
    /// <remarks>
    ///     The move is attempted first because it is atomic and cheap; a copy is only used when a move cannot
    ///     cross a filesystem boundary. These cover the copy itself, which cannot be reached through
    ///     <c>PromoteAcceptedAudioAsync</c> without unmounting a disk.
    /// </remarks>
    [Fact]
    public void TheCopyFallbackPublishesOnlyAfterTheLengthIsVerified()
    {
        var root = Path.Join(_tempRoot, "copy-fallback");
        var source = WriteBytes(Path.Join(root, "source", "track.flac"), 4096);
        var destination = Path.Join(root, "destination", "track.flac");

        var promoted = InvokeCopyFallback(source, destination);

        Assert.Equal(Path.GetFullPath(destination), promoted);
        Assert.Equal(4096, new FileInfo(destination).Length);
        Assert.False(File.Exists(source), "The source is deleted only after the copy is published.");
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination)!, "*.deezspotag-promote-*"));
    }

    [Fact]
    public void TheCopyFallbackLeavesTheSourceAloneWhenTheCopyIsShort()
    {
        // A truncated copy is worse than no copy: it would be published as the download and only fail much later
        // in tagging. The source has to stay where it is so the transfer can be retried.
        var root = Path.Join(_tempRoot, "copy-short");
        var source = WriteBytes(Path.Join(root, "source", "track.flac"), 4096);
        var destination = Path.Join(root, "destination", "track.flac");

        Assert.ThrowsAny<Exception>(() => InvokeCopyFallback(source, destination, truncateTo: 2048));

        Assert.True(File.Exists(source), "A failed copy must never delete the only verified copy.");
        Assert.False(File.Exists(destination), "A short copy must never be published.");
    }

    [Fact]
    public void TheCopyFallbackRefusesAnExistingDestination()
    {
        var root = Path.Join(_tempRoot, "copy-clash");
        var source = WriteBytes(Path.Join(root, "source", "track.flac"), 4096);
        var destination = WriteBytes(Path.Join(root, "destination", "track.flac"), 99);

        Assert.ThrowsAny<Exception>(() => InvokeCopyFallback(source, destination));

        Assert.Equal(99, new FileInfo(destination).Length);
        Assert.True(File.Exists(source));
    }

    /// <summary>
    ///     The association has to exist before anything is watched, or a crash mid-transfer leaves slskd
    ///     downloading a file nobody in this app knows about.
    /// </summary>
    /// <remarks>
    ///     Transfers are written only at terminal outcomes today, so a transfer that was still running when the
    ///     app stopped left no row at all: nothing to resume, nothing to clean up, and the next attempt started a
    ///     second transfer for a file slskd was already fetching.
    /// </remarks>
    [Fact]
    public async Task AssociationIsPersistedBeforeMonitoring()
    {
        var started = Guid.NewGuid();
        _transfer.EnqueueResult = started;
        var attached = new List<Guid>();
        _transfer.OnWait = async queueUuid =>
        {
            // Inside the monitor, the row must already be there and the ownership callback must have run.
            var recorded = await CreateRepository().GetTransferAsync(started, CancellationToken.None);
            Assert.NotNull(recorded);
            Assert.Equal(queueUuid, recorded!.QueueUuid);
            Assert.Equal(Peer, recorded.Username);
            Assert.Equal(RemotePath, recorded.Filename);
            Assert.Equal(RemoteSize, recorded.Size);
            Assert.Equal(Path.Join(_stagingDir, "expected.flac"), recorded.ExpectedPath);

            // Not terminal yet: this is ownership, not an outcome.
            Assert.False(recorded.IsTerminal);
            Assert.False(recorded.IsSuccessful);
            Assert.False(recorded.Verified);

            Assert.Equal([started], attached);
        };

        var completed = Path.Join(_stagingDir, "associated.mp3");
        File.WriteAllBytes(completed, new byte[RemoteSize]);
        _transfer.CompletedFile = new SoulseekCompletedFile(Peer, RemotePath, completed, RemoteSize);

        var request = Request();
        request.TransferAttachedAsync = (status, _) =>
        {
            attached.Add(status.TransferId!.Value);
            return Task.CompletedTask;
        };

        await CreateService().DownloadAsync(
            request, "queue-associated", Path.Join(_stagingDir, "expected.flac"), null, CancellationToken.None);

        Assert.Equal([started], attached);
    }

    [Fact]
    public async Task TheOwnershipCallbackRunsBeforeTheWaitAndSeesTheAttachedId()
    {
        var started = Guid.NewGuid();
        _transfer.EnqueueResult = started;
        _transfer.CompletedFile = new SoulseekCompletedFile(
            Peer, RemotePath, WriteBytes(Path.Join(_stagingDir, "cb.mp3"), (int)RemoteSize), RemoteSize);

        var seen = new List<Guid>();
        var request = Request();
        request.TransferAttachedAsync = (status, _) =>
        {
            seen.Add(status.TransferId!.Value);
            return Task.CompletedTask;
        };

        await CreateService().DownloadAsync(
            request, "queue-callback", Path.Join(_stagingDir, "expected.flac"), null, CancellationToken.None);

        Assert.Equal([started], seen);
    }

    /// <summary>
    ///     A restart must reattach to the transfer that is already running rather than starting a second one.
    /// </summary>
    [Fact]
    public async Task NewServiceInstanceResumesRecordedTransfer()
    {
        var started = Guid.NewGuid();
        _transfer.EnqueueResult = started;
        _transfer.CompletedFile = new SoulseekCompletedFile(
            Peer, RemotePath, WriteBytes(Path.Join(_stagingDir, "resumed.mp3"), (int)RemoteSize), RemoteSize);

        await CreateService().DownloadAsync(
            Request(), "queue-resume", Path.Join(_stagingDir, "expected.flac"), null, CancellationToken.None);
        Assert.Equal(1, _transfer.EnqueueCalls);

        // A second service over the same repository, as an app restart is. It must find the row the first one
        // left behind and watch that transfer. Enqueueing again would be a second slskd download of one file.
        var search = new StubSearchService();
        var second = new SoulseekDownloadService(
            search,
            _transfer,
            new StubCredentials(new SlskdCredentials("http://localhost:5030", "key")),
            new AllowingPeerPolicy(),
            new SpeedFirstScoring(),
            CreateRepository(),
            NullLogger<SoulseekDownloadService>.Instance);

        _transfer.Reset();
        _transfer.CompletedFile = new SoulseekCompletedFile(
            Peer, RemotePath, WriteBytes(Path.Join(_stagingDir, "resumed2.mp3"), (int)RemoteSize), RemoteSize);

        var file = await second.DownloadAsync(
            Request(), "queue-resume", Path.Join(_stagingDir, "expected.flac"), null, CancellationToken.None);

        Assert.Equal(0, _transfer.EnqueueCalls);
        Assert.Equal(0, search.SearchCalls);
        Assert.Equal([started], _transfer.WaitedForIds);
        Assert.NotNull(file);
    }

    /// <summary>
    ///     Another queue's record, or another peer's, must never lend this item its filename or its size.
    /// </summary>
    [Fact]
    public async Task WrongQueueOrPeerRecordCannotSupplyDiskEvidence()
    {
        var foreign = Guid.NewGuid();
        await CreateRepository().UpsertTransferAsync(
            new SoulseekTransferStatus(
                foreign,
                "someone-else",
                "Music\\Other\\09 - Other Track.flac",
                "in_progress",
                IsTerminal: false,
                IsSuccessful: false,
                1024,
                4096,
                256,
                PlaceInQueue: null,
                Progress: 25),
            "a-different-queue",
            Path.Join(_stagingDir, "somebody-elses.flac"),
            verified: false,
            CancellationToken.None);

        var started = Guid.NewGuid();
        _transfer.EnqueueResult = started;

        // The file that belongs to the other queue is sitting exactly where this one expects its own file, and is
        // the size the other queue's record advertises. Two independent reasons refuse it: the path is already
        // occupied, and the other queue's record is not this item's to borrow a filename or a size from.
        WriteBytes(Path.Join(_stagingDir, "expected.flac"), 4096);

        var result = await Assert.ThrowsAnyAsync<Exception>(() => CreateService().DownloadAsync(
            Request(), "queue-mine", Path.Join(_stagingDir, "expected.flac"), null, CancellationToken.None));

        Assert.DoesNotContain(foreign, _transfer.WaitedForIds);
        Assert.DoesNotContain(started, _transfer.WaitedForIds);
    }

    [Fact]
    public async Task AnAutomaticRecoveredTransferStaysAutomatic()
    {
        // Recovery must not promote an automatic request into a reader's exact choice. The pre-download peer
        // fields are what decide that, so they stay empty and only the transfer record carries the identity.
        var started = Guid.NewGuid();
        _transfer.EnqueueResult = started;
        _transfer.CompletedFile = new SoulseekCompletedFile(
            Peer, RemotePath, WriteBytes(Path.Join(_stagingDir, "auto.mp3"), (int)RemoteSize), RemoteSize);

        var request = Request();
        request.Username = string.Empty;
        request.RemotePath = string.Empty;
        request.Automation = true;

        await CreateService().DownloadAsync(
            request, "queue-auto", Path.Join(_stagingDir, "expected.flac"), null, CancellationToken.None);

        var recorded = await CreateRepository().GetTransferAsync(started, CancellationToken.None);
        Assert.NotNull(recorded);
        Assert.Equal("queue-auto", recorded!.QueueUuid);

        // The request still carries no peer of its own, so nothing about the recovery turned it into the
        // reader's exact choice - which is what would make later attempts refuse to substitute a peer.
        Assert.Equal(string.Empty, request.Username);
        Assert.Equal(string.Empty, request.RemotePath);
    }

    /// <summary>
    ///     Losing ownership is not the peer's fault and must not be answered with a different peer.
    /// </summary>
    [Fact]
    public async Task AFailedOwnershipWriteCannotEnqueueASubstituteTransfer()
    {
        var started = Guid.NewGuid();
        _transfer.EnqueueResult = started;
        _transfer.CompletedFile = new SoulseekCompletedFile(
            Peer, RemotePath, WriteBytes(Path.Join(_stagingDir, "own.mp3"), (int)RemoteSize), RemoteSize);

        var request = Request();
        request.TransferAttachedAsync = (_, _) => throw new SoulseekTransferMonitoringException(
            "could not persist the transfer association");

        var error = await Assert.ThrowsAsync<SoulseekTransferMonitoringException>(() => CreateService().DownloadAsync(
            request, "queue-own-fail", Path.Join(_stagingDir, "expected.flac"), null, CancellationToken.None));

        // The useful message survives instead of being flattened into "could not deliver the file".
        Assert.Contains("persist the transfer association", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, _transfer.EnqueueCalls);
        Assert.Empty(_transfer.WaitedForIds);
    }

    [Fact]
    public async Task UnreadableOwnershipNeverSearchesOrEnqueuesAnotherTransfer()
    {
        File.WriteAllText(Path.Join(_tempRoot, "queue.db"), "unreadable database");
        var search = new StubSearchService();
        _transfer.EnqueueResult = Guid.NewGuid();

        var error = await Assert.ThrowsAsync<SoulseekTransferMonitoringException>(() =>
            CreateService(search).DownloadAsync(Request(), "queue-db-error",
                Path.Join(_stagingDir, "expected.flac"), null, CancellationToken.None));

        Assert.IsType<Microsoft.Data.Sqlite.SqliteException>(error.InnerException);
        Assert.Equal(0, search.SearchCalls);
        Assert.Equal(0, _transfer.EnqueueCalls);
        Assert.Empty(_transfer.WaitedForIds);
    }

    /// <summary>
    ///     Losing the login stops new work at the enqueue, and nowhere else.
    /// </summary>
    /// <remarks>
    ///     The order matters more than the refusal. Recovery and the owned-transfer read happen first, so a
    ///     transfer slskd is already running keeps being watched and a verified file is still collected; only
    ///     once there is provably nothing to recover does the gate refuse to start something new.
    /// </remarks>
    [Fact]
    public async Task AnInactiveLoginStopsNewWorkButNotRecovery()
    {
        var started = Guid.NewGuid();
        _transfer.EnqueueResult = started;
        _transfer.CompletedFile = new SoulseekCompletedFile(
            Peer, RemotePath, WriteBytes(Path.Join(_stagingDir, "after-logout.mp3"), (int)RemoteSize), RemoteSize);

        var connection = new StubConnectionService(SoulseekConnectionState.Connected);

        // Logged in: the transfer is created and recorded.
        await CreateService(connection: connection).DownloadAsync(
            Request(), "queue-gated", Path.Join(_stagingDir, "expected.flac"), null, CancellationToken.None);
        Assert.Equal(1, _transfer.EnqueueCalls);
        Assert.NotNull(await CreateRepository().GetTransferAsync(started, CancellationToken.None));

        // The reader logs out and retries. Recovery must still find and finish the transfer it already owns.
        connection.State = SoulseekConnectionState.Disconnected;
        _transfer.Reset();
        _transfer.CompletedFile = new SoulseekCompletedFile(
            Peer, RemotePath, WriteBytes(Path.Join(_stagingDir, "after-logout2.mp3"), (int)RemoteSize), RemoteSize);

        var recovered = await CreateService(connection: connection).DownloadAsync(
            Request(), "queue-gated", Path.Join(_stagingDir, "expected.flac"), null, CancellationToken.None);

        Assert.NotNull(recovered);
        Assert.Equal(0, _transfer.EnqueueCalls);
        Assert.Equal([started], _transfer.WaitedForIds);
    }

    /// <summary>
    ///     With nothing to recover, no login means no new transfer - and no search either.
    /// </summary>
    [Fact]
    public async Task AnInactiveLoginRefusesBeforeAnythingIsAskedOfTheNetwork()
    {
        var connection = new StubConnectionService(SoulseekConnectionState.Disconnected);

        var failure = await Assert.ThrowsAsync<SoulseekLoginRequiredException>(() =>
            CreateService(connection: connection).DownloadAsync(
                Request(), "queue-refused", Path.Join(_stagingDir, "expected.flac"), null, CancellationToken.None));

        // The reason is the connection's own, because "log in" and "no peer has this" need different actions.
        Assert.Equal("slskd is not connected to Soulseek.", failure.Message);

        // No transfer, and no peer search: the reader is told what to fix rather than being billed for a
        // search that could not have been acted on.
        Assert.Equal(0, _transfer.EnqueueCalls);
        Assert.Equal(0, _transfer.FindCalls);
        Assert.Empty(_transfer.Enqueued);
    }

    [Fact]
    public async Task AnInactiveLoginIsNotAnsweredByTryingAnotherPeer()
    {
        var connection = new StubConnectionService(SoulseekConnectionState.Disconnected);

        await Assert.ThrowsAsync<SoulseekLoginRequiredException>(() =>
            CreateService(connection: connection).DownloadAsync(
                Request(), "queue-no-substitute", Path.Join(_stagingDir, "expected.flac"), null, CancellationToken.None));

        // One refusal, not three attempts. Asking peers again cannot produce a file while the gate is shut.
        Assert.Equal(1, connection.EligibilityCalls);
        Assert.Equal(0, _transfer.EnqueueCalls);
        Assert.Empty(_transfer.Enqueued);
    }

    /// <summary>
    ///     The refusal is raised outside every peer-related catch, so no amount of peer bookkeeping sees it.
    /// </summary>
    /// <remarks>
    ///     Enforced by inspection of the source rather than by a stub, because the property is about where the
    ///     throw sits and no runtime observation can distinguish "excluded by a filter" from "never enclosed".
    ///     The behavioural half is covered above: no enqueue, no search, one attempt.
    /// </remarks>
    [Fact]
    public void TheLoginRefusalIsRaisedWhereNoPeerHandlerCanSeeIt()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Services/Download/Soulseek/SoulseekDownloadService.cs"));

        var gate = source.IndexOf("throw new SoulseekLoginRequiredException", StringComparison.Ordinal);
        Assert.True(gate > 0, "The login refusal is gone; new work would start without a verified login.");

        // Everything that charges a peer, or recovers by adopting a transfer slskd already started, sits inside a
        // try that begins after the gate. A refusal raised before them cannot be counted as a peer failure and
        // cannot spend time polling for a transfer that was never enqueued.
        // Measured against the FIRST try in the method rather than the next one after the gate: a gate that had been
        // moved inside the enqueue try would still sit before some later try, and the check would pass.
        var methodStart = source.LastIndexOf(
            "private async Task<SoulseekCompletedFile> AttemptAsync",
            gate,
            StringComparison.Ordinal);
        Assert.True(methodStart > 0, "AttemptAsync was not found.");

        // Scoped to AttemptAsync: a try in an earlier method would otherwise be found first and the
        // comparison below would be against the wrong block entirely.
        var firstTry = source.IndexOf("        try", methodStart, StringComparison.Ordinal);
        Assert.True(firstTry > methodStart, "AttemptAsync no longer contains a try block.");
        Assert.True(gate < firstTry, "The login gate must be raised before AttemptAsync's first try block.");

        // Nothing between them may claim the exception type, which is what would let a peer-related handler
        // swallow a refusal and spend time adopting a transfer that was never enqueued.
        Assert.DoesNotContain(
            "not SoulseekLoginRequiredException",
            source[gate..firstTry],
            StringComparison.Ordinal);

        // And it carries the connection's own reason rather than a generic failure.
        Assert.Contains("new SoulseekLoginRequiredException(eligibility.Message)", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInactiveLoginIsNeverChargedToThePeer()
    {
        var connection = new StubConnectionService(SoulseekConnectionState.Disconnected);
        var peerPolicy = new RecordingPeerPolicy();

        var service = new SoulseekDownloadService(
            new StubSearchService(),
            _transfer,
            new StubCredentials(new SlskdCredentials("http://localhost:5030", "key")),
            peerPolicy,
            new SpeedFirstScoring(),
            CreateRepository(),
            NullLogger<SoulseekDownloadService>.Instance,
            connection);

        await Assert.ThrowsAsync<SoulseekLoginRequiredException>(() => service.DownloadAsync(
            Request(), "queue-peer-clean", Path.Join(_stagingDir, "expected.flac"), null, CancellationToken.None));

        // The peer was never asked for anything, so it must not be cooled down as though it had refused.
        Assert.Empty(peerPolicy.Failures);
    }

    private sealed class RecordingPeerPolicy : ISoulseekPeerPolicyService
    {
        public List<string> Failures { get; } = [];

        public List<string> Successes { get; } = [];

        public Task<SoulseekPeerDecision> EvaluateAsync(SoulseekRawCandidate candidate, CancellationToken cancellationToken = default)
            => Task.FromResult(new SoulseekPeerDecision(true));

        public Task<SoulseekPeerDecision> EvaluateAsync(
            SoulseekRawCandidate candidate,
            IReadOnlyList<(string Username, DateTimeOffset CooldownExpiresUtc)> cooldownsInEffect,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SoulseekPeerDecision(true));

        public bool IsBlockedFilename(string? filename) => false;

        public Task RecordSuccessAsync(string username, CancellationToken cancellationToken = default)
        {
            Successes.Add(username);
            return Task.CompletedTask;
        }

        public Task RecordFailureAsync(string username, string reason, CancellationToken cancellationToken = default)
        {
            Failures.Add($"{username}:{reason}");
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<(string Username, DateTimeOffset CooldownExpiresUtc)>> GetCooldownsAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<(string, DateTimeOffset)>>([]);

        public Task<int> CleanupExpiredCooldownsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(0);
    }

    private string InvokeCopyFallback(string source, string destination, int? truncateTo = null)
    {
        var method = typeof(SoulseekDownloadService).GetMethod(
            "CopyThenPublish",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException("CopyThenPublish is gone; the cross-filesystem promotion path has moved.");

        try
        {
            return (string)method.Invoke(CreateService(), [source, destination, truncateTo])!;
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // The test wants the failure the promotion itself would raise, not the reflection wrapper.
            throw ex.InnerException;
        }
    }

    private string WriteBytes(string path, int length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[length];
        Array.Fill(bytes, (byte)7);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>
    ///     A chosen file must be the transfer, not the opening move of another search.
    /// </summary>
    /// <remarks>
    ///     The observed failure is exactly this: a file the reader picked in the results tab went into the queue,
    ///     and the engine then searched for the track and downloaded a different copy of it from a different
    ///     peer. The reader had asked for one file and got another, and nothing in the item said so. A complete
    ///     pin is therefore a promise the engine has to keep before it touches the network.
    /// </remarks>
    [Fact]
    public async Task PinnedCandidateStartsTransferWithoutSearching()
    {
        const string peer = "Nintendude94";
        const string remotePath = "Music\\Nick Drake\\(1969) Five Leaves Left\\01 - Nick Drake - Time Has Told Me.flac";
        const long size = 24_679_285;

        var started = Guid.NewGuid();
        _transfer.EnqueueResult = started;
        var completed = Path.Join(_stagingDir, "promoted-isabella.flac");
        File.WriteAllBytes(completed, new byte[size]);
        _transfer.CompletedFile = new SoulseekCompletedFile(peer, remotePath, completed, size);

        var request = Request();
        request.Username = peer;
        request.RemotePath = remotePath;
        request.RemoteSizeBytes = size;
        request.SoulseekQuality = "FLAC";

        var search = new StubSearchService();
        var file = await CreateService(search).DownloadAsync(
            request, "queue-pinned", Path.Join(_stagingDir, "expected.flac"), null, CancellationToken.None);

        // No search was run, and the transfer is the file the reader chose - byte for byte.
        Assert.Equal(0, search.SearchCalls);
        var enqueued = Assert.Single(_transfer.Enqueued);
        Assert.Equal((peer, remotePath, size), enqueued);

        // And the file continues through the existing verification rather than stopping at the enqueue.
        Assert.Equal(completed, file.LocalPath);
        Assert.Equal([started], _transfer.WaitedForIds);
    }

    [Fact]
    public async Task FailedPinnedCandidateIsNotSilentlyReplacedBySearch()
    {
        const string peer = "chosen-peer";
        const string remotePath = @"Chosen Album\01 Chosen.flac";
        _transfer.EnqueueException = new SoulseekTransferException("chosen peer failed", peer);
        var request = Request();
        request.Username = peer;
        request.RemotePath = remotePath;
        request.RemoteSizeBytes = RemoteSize;
        var search = new StubSearchService();

        var failure = await Assert.ThrowsAsync<SoulseekTransferException>(() => CreateService(search).DownloadAsync(
            request, "queue-pinned-failure", Path.Join(_stagingDir, "expected.flac"), null, CancellationToken.None));

        Assert.Equal("chosen peer failed", failure.Message);
        Assert.Equal(0, search.SearchCalls);
        var attempt = Assert.Single(_transfer.Enqueued);
        Assert.Equal((peer, remotePath, RemoteSize), attempt);
    }

    [Fact]
    public async Task UnpinnedCandidateUsesSearchService()
    {
        // The inverse control: with no file chosen there is nothing to enqueue, so the engine must search - and
        // must do so for the quality of the rung it is on.
        var started = Guid.NewGuid();
        _transfer.EnqueueResult = started;
        var completed = Path.Join(_stagingDir, "searched.flac");
        File.WriteAllBytes(completed, new byte[RemoteSize]);
        _transfer.CompletedFile = new SoulseekCompletedFile(Peer, RemotePath, completed, RemoteSize);

        var request = Request();
        request.SoulseekQuality = "FLAC";

        var search = new StubSearchService();
        await CreateService(search).DownloadAsync(
            request, "queue-unpinned", Path.Join(_stagingDir, "expected.flac"), null, CancellationToken.None);

        Assert.Equal(1, search.SearchCalls);
        Assert.Equal("FLAC", search.LastRequiredQualityCode);
        Assert.Equal(1, _transfer.EnqueueCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReservationWithoutHistoryResumesBeforeNewWork(bool alreadyAttached)
    {
        var expected = Path.Join(_stagingDir, "reserved.flac");
        var owner = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var repository = CreateRepository();
        Assert.True(await repository.TryClaimSourcePathsAsync(owner, "queue-reservation", Peer, RemotePath, [expected]));
        if (alreadyAttached) await repository.AttachSourceClaimsAsync(owner, transferId);
        _transfer.FindResult = Status(transferId);
        _transfer.CompletedFile = new SoulseekCompletedFile(Peer, RemotePath, expected, RemoteSize);
        var request = Request();
        request.Username = Peer;
        request.RemotePath = RemotePath;
        request.RemoteSizeBytes = RemoteSize;
        request.Automation = false;
        var search = new StubSearchService();
        request.TransferAttachedAsync = async (status, token) =>
        {
            var record = await repository.GetTransferAsync(status.TransferId!.Value, token);
            Assert.NotNull(record);
            Assert.Equal(expected, record!.ExpectedPath);
        };

        await CreateService(search).DownloadAsync(request, "queue-reservation", expected, null, CancellationToken.None);

        Assert.Equal(0, search.SearchCalls);
        Assert.Equal(0, _transfer.EnqueueCalls);
        Assert.Equal([transferId], _transfer.WaitedForIds);
        Assert.Equal(transferId, Assert.Single(await repository.GetSourceClaimsForQueueAsync("queue-reservation")).TransferId);
    }

    [Fact]
    public async Task UnknownReservationKeepsOwnershipWithoutSearchingOrEnqueueing()
    {
        var expected = Path.Join(_stagingDir, "unknown.flac");
        var repository = CreateRepository();
        var owner = Guid.NewGuid();
        Assert.True(await repository.TryClaimSourcePathsAsync(owner, "queue-unknown", Peer, RemotePath, [expected]));
        var request = Request();
        request.Username = Peer;
        request.RemotePath = RemotePath;
        request.Automation = false;
        var search = new StubSearchService();
        await Assert.ThrowsAsync<SoulseekTransferMonitoringException>(() => CreateService(search)
            .DownloadAsync(request, "queue-unknown", expected, null, CancellationToken.None));
        Assert.Equal(0, search.SearchCalls);
        Assert.Equal(0, _transfer.EnqueueCalls);
        Assert.Null(Assert.Single(await repository.GetSourceClaimsForQueueAsync("queue-unknown")).TransferId);
    }

    [Fact]
    public async Task PromotionReleasesOwnedClaimsAndPersistsTheNewVerifiedPath()
    {
        var source = Path.Join(_tempRoot, "slskd", "owned.flac");
        var destination = Path.Join(_stagingDir, "owned.flac");
        var payload = new SoulseekQueueItem { Id = "queue-owned", FilePath = destination };
        var owner = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var repository = CreateRepository();
        Assert.True(await repository.TryClaimSourcePathsAsync(owner, payload.Id, Peer, RemotePath, [source, destination]));
        await repository.AttachSourceClaimsAsync(owner, transferId);
        WriteBytes(source, 512);
        await SeedVerifiedSourceAsync(payload, source, transferId);

        await CreateService().PromoteAcceptedAudioAsync(payload, source, CancellationToken.None);

        Assert.Empty(await repository.GetSourceClaimsForQueueAsync(payload.Id));
        Assert.False(File.Exists(source));
        var record = await repository.GetTransferAsync(transferId);
        Assert.True(record!.Verified);
        Assert.Equal(destination, record.ExpectedPath);
    }

    [Fact]
    public async Task PromotionAndRejectionCannotTouchAnotherQueuesFile()
    {
        var source = Path.Join(_stagingDir, "foreign.flac");
        var repository = CreateRepository();
        var owner = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        Assert.True(await repository.TryClaimSourcePathsAsync(owner, "queue-foreign", Peer, RemotePath, [source]));
        await repository.AttachSourceClaimsAsync(owner, transferId);
        WriteBytes(source, 512);
        var payload = new SoulseekQueueItem { Id = "queue-other", FilePath = Path.Join(_stagingDir, "other.flac"),
            SoulseekTransferId = transferId.ToString(), SoulseekUsername = Peer, SoulseekRemotePath = RemotePath };

        await Assert.ThrowsAsync<SoulseekTransferMonitoringException>(() => CreateService().PromoteAcceptedAudioAsync(payload, source, CancellationToken.None));
        await Assert.ThrowsAsync<SoulseekTransferMonitoringException>(() => CreateService().DeleteRejectedStagingAudio(payload, source, CancellationToken.None));
        Assert.True(File.Exists(source));
        Assert.Single(await repository.GetSourceClaimsForQueueAsync("queue-foreign"));
    }

    [Fact]
    public async Task RejectingOwnedAudioReleasesClaimsAfterDeletingTheFile()
    {
        var source = Path.Join(_stagingDir, "rejected.flac");
        var repository = CreateRepository();
        var owner = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        Assert.True(await repository.TryClaimSourcePathsAsync(owner, "queue-rejected", Peer, RemotePath, [source]));
        await repository.AttachSourceClaimsAsync(owner, transferId);
        WriteBytes(source, 512);
        var payload = new SoulseekQueueItem { Id = "queue-rejected", FilePath = source };
        await SeedVerifiedSourceAsync(payload, source, transferId);

        await CreateService().DeleteRejectedStagingAudio(payload, source, CancellationToken.None);

        Assert.False(File.Exists(source));
        Assert.Empty(await repository.GetSourceClaimsForQueueAsync(payload.Id));
        Assert.False((await repository.GetTransferAsync(transferId))!.Verified);
    }

    private async Task SeedVerifiedSourceAsync(SoulseekQueueItem payload, string source, Guid? id = null)
    {
        var transferId = id ?? Guid.NewGuid();
        payload.SoulseekTransferId = transferId.ToString();
        payload.SoulseekUsername = Peer;
        payload.SoulseekRemotePath = RemotePath;
        var size = new FileInfo(source).Length;
        await CreateRepository().UpsertTransferAsync(new SoulseekTransferStatus(transferId, Peer, RemotePath,
            "completed", true, true, size, size, 0, null, 100), payload.Id, source, true);
    }

    private SoulseekDownloadRequest Request() => new()
    {
        Target = new SoulseekSearchTarget("Sauti Sol", "Isabella", "Live and Die in Afrika"),
        QueueUuid = "queue-recover"
    };

    private SoulseekDownloadService CreateService(
        ISoulseekSearchService? search = null,
        ISoulseekConnectionService? connection = null) => new(
        search ?? new StubSearchService(),
        _transfer,
        new StubCredentials(new SlskdCredentials("http://localhost:5030", "key")),
        new AllowingPeerPolicy(),
        new SpeedFirstScoring(),
        CreateRepository(),
        NullLogger<SoulseekDownloadService>.Instance,
        connection);

    private sealed class StubConnectionService(SoulseekConnectionState state) : ISoulseekConnectionService
    {
        public SoulseekConnectionState State { get; set; } = state;

        public int EligibilityCalls { get; private set; }

        public int InvalidateCalls { get; private set; }

        public Task<SoulseekConnectionStatus> GetStatusAsync(bool force = false, CancellationToken cancellationToken = default)
            => Task.FromResult(Status());

        public Task<SoulseekConnectionStatus> GetEligibilityAsync(CancellationToken cancellationToken = default)
        {
            EligibilityCalls++;
            return Task.FromResult(Status());
        }

        public Task<SoulseekConnectionStatus> EnsureAvailableAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Status());

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(State == SoulseekConnectionState.Connected);

        public void Invalidate() => InvalidateCalls++;

        private SoulseekConnectionStatus Status()
            => new(
                State,
                State == SoulseekConnectionState.Connected
                    ? "slskd is connected to Soulseek."
                    : "slskd is not connected to Soulseek.");
    }

    private SoulseekRepository CreateRepository() => new(
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Queue"] = $"Data Source={Path.Join(_tempRoot, "queue.db")}"
            })
            .Build(),
        NullLogger<SoulseekRepository>.Instance);

    private static SoulseekTransferStatus Status(Guid id) => new(
        id,
        Peer,
        RemotePath,
        "in_progress",
        IsTerminal: false,
        IsSuccessful: false,
        RemoteSize / 2,
        RemoteSize,
        512,
        PlaceInQueue: 0,
        Progress: 50);

    private sealed class StubSearchService : ISoulseekSearchService
    {
        /// <summary>The quality the last search was asked to satisfy, so a test can see the step's rung arrive.</summary>
        public string? LastRequiredQualityCode { get; private set; }

        /// <summary>How many remote searches this service was asked to run.</summary>
        public int SearchCalls { get; private set; }

        public Task<SoulseekSearchOutcome> SearchAsync(
            SoulseekSearchTarget target,
            SoulseekSearchMode mode = SoulseekSearchMode.Manual,
            string? queueUuid = null,
            CancellationToken cancellationToken = default,
            string? requiredQualityCode = null)
        {
            LastRequiredQualityCode = requiredQualityCode;
            SearchCalls++;
            return Task.FromResult(Outcome());
        }

        public Task<SoulseekSearchOutcome> ObserveAsync(
            SoulseekSearchTarget target,
            Guid searchId,
            string searchText,
            SoulseekSearchMode mode,
            string? queueUuid,
            CancellationToken cancellationToken = default,
            string? requiredQualityCode = null)
            => Task.FromResult(Outcome());

        public Task<IReadOnlyList<SlskdDirectory>> BrowseAsync(
            string username,
            string? directory = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SlskdDirectory>>([]);

        public Task<int> CleanupStaleSearchesAsync(TimeSpan olderThan, CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        private static SoulseekSearchOutcome Outcome()
        {
            var raw = new SoulseekRawCandidate(
                Peer,
                RemotePath,
                RemoteSize,
                ".mp3",
                320,
                null,
                null,
                200,
                IsVariableBitrate: false,
                IsLocked: false,
                PeerQueueLength: 0,
                PeerHasFreeUploadSlot: true,
                PeerUploadSpeed: 1_048_576);
            var candidate = new SoulseekCandidate(raw, "MP3_320", "MP3 320 kbps", "mp3_320", 1, 0.8, 0.9, Accepted: true);

            return new SoulseekSearchOutcome(Guid.NewGuid(), "Isabella", [candidate], candidate, true, false, 1);
        }
    }

    private sealed class ScriptedTransferService : ISoulseekTransferService
    {
        public Guid? EnqueueResult { get; set; }

        public Exception? EnqueueException { get; set; }

        public SoulseekTransferStatus? FindResult { get; set; }

        public SoulseekCompletedFile? CompletedFile { get; set; }

        public int EnqueueCalls { get; private set; }

        public int FindCalls { get; private set; }

        public List<Guid> WaitedForIds { get; } = [];

        public List<Guid> GetStatusCalls { get; } = [];

        public string? RequestedRoot { get; private set; }

        /// <summary>Runs at the moment monitoring starts, so a test can inspect state from inside the wait.</summary>
        public Func<string, Task>? OnWait { get; set; }

        /// <summary>The transfers whose ownership callback the engine invoked, in order.</summary>
        public List<Guid> AttachedFor { get; } = [];

        /// <summary>The host token the engine threaded through, so shutdown can be told from cancellation.</summary>
        public CancellationToken LastHostStoppingToken { get; private set; }

        /// <summary>Puts the counters back to zero so a second service can be observed on its own.</summary>
        public void Reset()
        {
            EnqueueCalls = 0;
            FindCalls = 0;
            WaitedForIds.Clear();
            GetStatusCalls.Clear();
            Enqueued.Clear();
            AttachedFor.Clear();
            OnWait = null;
        }

        /// <summary>Exactly what the engine asked slskd to fetch, in order.</summary>
        public List<(string Username, string Filename, long Size)> Enqueued { get; } = [];

        public Task<Guid?> EnqueueAsync(string username, string filename, long size, CancellationToken cancellationToken = default)
        {
            EnqueueCalls++;
            Enqueued.Add((username, filename, size));
            if (EnqueueException is not null)
            {
                throw EnqueueException;
            }

            return Task.FromResult(EnqueueResult);
        }

        public Task<SoulseekTransferStatus?> FindTransferAsync(
            string username,
            string filename,
            CancellationToken cancellationToken = default)
        {
            FindCalls++;
            return Task.FromResult(FindResult);
        }

        public Task<SoulseekTransferStatus?> GetStatusAsync(
            string username,
            Guid transferId,
            CancellationToken cancellationToken = default)
        {
            GetStatusCalls.Add(transferId);
            return Task.FromResult<SoulseekTransferStatus?>(null);
        }

        public Task<SoulseekCompletedFile?> WaitForCompletionAsync(
            string username,
            Guid transferId,
            string expectedPath,
            string queueUuid,
            string? completedDownloadsRoot = null,
            Func<double, double, Task>? progress = null,
            CancellationToken cancellationToken = default,
            CancellationToken hostStoppingToken = default)
        {
            WaitedForIds.Add(transferId);
            RequestedRoot = completedDownloadsRoot;
            LastHostStoppingToken = hostStoppingToken;
            return OnWait is null
                ? Task.FromResult(CompletedFile)
                : OnWait(queueUuid).ContinueWith(_ => CompletedFile, TaskScheduler.Default);
        }

        public Task CancelAsync(string username, Guid transferId, bool remove = false, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<int> CleanupStaleTransfersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        public Task<IReadOnlyDictionary<string, string>> FetchSidecarsAsync(
            string username,
            IReadOnlyList<string> remotePaths,
            string outputDirectory,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, string>>(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }

    private sealed class AllowingPeerPolicy : ISoulseekPeerPolicyService
    {
        public Task<SoulseekPeerDecision> EvaluateAsync(SoulseekRawCandidate candidate, CancellationToken cancellationToken = default)
            => Task.FromResult(new SoulseekPeerDecision(true));

        public Task<SoulseekPeerDecision> EvaluateAsync(
            SoulseekRawCandidate candidate,
            IReadOnlyList<(string Username, DateTimeOffset CooldownExpiresUtc)> cooldownsInEffect,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SoulseekPeerDecision(true));

        public bool IsBlockedFilename(string? filename) => false;

        public Task RecordFailureAsync(string username, string reason, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RecordSuccessAsync(string username, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<(string Username, DateTimeOffset CooldownExpiresUtc)>> GetCooldownsAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<(string, DateTimeOffset)>>([]);

        public Task<int> CleanupExpiredCooldownsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(0);
    }

    private sealed class SpeedFirstScoring : ISoulseekResultScoringService
    {
        public Task<IReadOnlyList<SoulseekCandidate>> ScoreAsync(
            SoulseekSearchTarget target,
            IReadOnlyList<SoulseekRawCandidate> raw,
            SoulseekSearchMode mode = SoulseekSearchMode.Manual,
            IReadOnlyList<string>? allowedQualities = null,
            bool? allowUnknownQuality = null,
            CancellationToken cancellationToken = default,
            string? requiredQualityCode = null)
            => throw new NotSupportedException();

        public SoulseekCandidate? SelectBest(IReadOnlyList<SoulseekCandidate> candidates)
            => candidates
                .Where(candidate => candidate.Accepted)
                .OrderByDescending(candidate => candidate.Raw.PeerUploadSpeed)
                .ThenByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Raw.PeerQueueLength)
                .FirstOrDefault();
    }

    private sealed class StubCredentials(SlskdCredentials? credentials) : ISoulseekCredentialProvider
    {
        public Task<SlskdCredentials?> GetCredentialsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(credentials);
    }
}
