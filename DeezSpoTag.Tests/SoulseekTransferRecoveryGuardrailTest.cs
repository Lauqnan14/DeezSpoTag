using System;
using System.IO;
using DeezSpoTag.Services.Download.Soulseek;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     Why a Soulseek download must attach to the transfer slskd really started.
/// </summary>
/// <remarks>
///     <para>
///         slskd answers the enqueue request after a short internal wait. A slow peer makes it miss that wait:
///         slskd reports a server error and no transfer id while the transfer it created keeps running. The
///         app used to believe the reply, so it threw "did not create a transfer", never obtained an id, and
///         therefore never called the only code that reports progress - which is why a transfer that reached
///         59% was shown as 0% and then failed.
///     </para>
/// </remarks>
public sealed class SoulseekTransferRecoveryGuardrailTest
{
    private static string DownloadService()
        => File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Services/Download/Soulseek/SoulseekDownloadService.cs")));

    [Fact]
    public void AFailedEnqueueLooksForTheTransferSlskdAlreadyStarted()
    {
        var source = DownloadService();

        Assert.Contains("AdoptStartedTransferAsync", source, StringComparison.Ordinal);
        Assert.Contains("_transfer.FindTransferAsync(", source, StringComparison.Ordinal);
        Assert.Contains("catch (Exception ex) when (ex is not OperationCanceledException and not SoulseekUnavailableException)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileIsNeverAdoptedWithoutAnAttachedPathClaim()
    {
        // This used to be "a file that arrived anyway is a download, not a failure", decided by existence and size
        // alone. That is what consumed an unrelated file: with no transfer id to attribute the bytes to, any file
        // at the expected path that happened to be the right size was accepted as this download. A path claim is now
        // the evidence, and the unattributed case has to be refused rather than recovered.
        var source = DownloadService();

        Assert.DoesNotContain("var finished = Verify(expectedPath, username, remotePath, remoteSize);", source, StringComparison.Ordinal);
        Assert.Contains("TryClaimSourcePathsAsync", source, StringComparison.Ordinal);
        Assert.Contains("AttachSourceClaimsAsync", source, StringComparison.Ordinal);
        Assert.Contains("so the reserved paths cannot be safely attached or released.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnresolvedReservationIsReportedRatherThanStartedAgain()
    {
        // A crash between reserving the path and learning the transfer id leaves a reservation with no transfer
        // attached. Searching again would fetch the same file twice, and releasing the path would let a second
        // operation consume whatever the first transfer left behind. Both are prevented by refusing, and by keeping
        // the reservation.
        var source = DownloadService();

        Assert.Contains("FindStartedTransferAsync", source, StringComparison.Ordinal);
        Assert.Contains("its transfer identity could not be established. No new transfer was started.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedTransferNamesThePeerSoTheNextAttemptCanAvoidIt()
    {
        // Without the peer on the failure, "try another peer" is impossible: the retry would hand the same dead
        // peer back and the item would die the same way twice.
        var source = DownloadService();

        Assert.Contains("new SoulseekTransferException(", source, StringComparison.Ordinal);
        Assert.Contains("failedPeers.Add(peer);", source, StringComparison.Ordinal);
        Assert.Contains("excludedPeers.Contains(candidate.Username)", source, StringComparison.Ordinal);
        Assert.Contains("private const int MaxPeerAttempts = 3;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFailureMessageSaysWhatTheTransferActuallyDid()
    {
        // "Reported as complete but no file" was the message for a transfer that had stopped at a third of the
        // file, which is the common case and points at the wrong thing entirely. How far it got is the part
        // the reader can act on, so that part stays.
        var source = DownloadService();

        Assert.Contains("DescribeUnverifiedTransfer", source, StringComparison.Ordinal);
        Assert.Contains("Soulseek peer stopped at {last.Progress:0.#}%.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFailureMessageIsReadableAndCarriesNoInternalDetail()
    {
        // This string is the item's error: one line under its title in the queue list. "slskd did not create a
        // transfer for ...\Hi Scores..." names an internal service and a remote path, says nothing about what
        // to do, and reads as though nothing was ever tried. The transport's words belong in the log and in the
        // item's history, so the message is built from the transfer alone - the signature is the guard.
        var source = DownloadService();

        Assert.Contains("private static string DescribeUnverifiedTransfer(SoulseekTransferStatus? last)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("slskd reported", source, StringComparison.Ordinal);
        Assert.DoesNotContain("no file was found at", source, StringComparison.Ordinal);
        Assert.DoesNotContain("so nothing was saved", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExhaustedItemIsOneShortLineAndTheDetailGoesToTheLog()
    {
        // "All enabled sources were tried" is a summary the status beside it already gives, and appending the
        // raw step detail answered it with a log line. The item gets one short line; the detail is the
        // diagnosis, so it has to be written somewhere a reader can find it.
        var coordinator = File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../DeezSpoTag.Services/Download/Fallback/EngineFallbackCoordinator.cs")));

        Assert.Contains("DescribeLastAttempt", coordinator, StringComparison.Ordinal);
        Assert.Contains("Fallback exhausted detail for {request.QueueUuid}", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("Last attempt ({last.ErrorClass ?? last.Status})", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("Download failed after all enabled sources were tried.", coordinator, StringComparison.Ordinal);
    }
}
