using System;
using DeezSpoTag.Services.Download.Soulseek;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     Whether the peer, path, size and quality a reader picked in the Soulseek drawer survive onto the queue
///     item the engine actually receives.
/// </summary>
/// <remarks>
///     <para>
///         This is the difference between "download this file" and "download something like this file". The
///         reader names a peer and a path in the drawer; if those are lost the engine runs its own search and
///         fetches whatever it finds instead, from a peer nobody chose, at a size nobody declared.
///     </para>
///     <para>
///         It failed in exactly that way: the payload arrived with no pin, the engine searched, wrote back the
///         peer it happened to find, and slskd refused the transfer because the expected size was zero.
///     </para>
/// </remarks>
public sealed class SoulseekPinnedIntentSurvivesTest
{
    private const string Peer = "OvervalueSkipping7";
    private const string RemotePath = @"music\Massive Attack\Mezzanine (1998)\Massive Attack - Mezzanine - 01 - Angel.flac";
    private const long RemoteSize = 114_581_750;
    private const string Quality = "FLAC_HI_RES";

    private static DeezSpoTag.Services.Download.Shared.Models.DownloadIntent Pinned()
        => SoulseekBatchQueuePlanner.BuildPinnedIntent(
            Peer,
            RemotePath,
            RemoteSize,
            "Angel",
            "Massive Attack",
            "Mezzanine",
            336_000,
            trackNumber: 1,
            quality: Quality,
            destinationFolderId: 3,
            displayCoverUrl: string.Empty);

    [Fact]
    public void TheIntentTheDrawerBuildsIsAManualSelection()
    {
        // If this is false the pin is never written, and nothing downstream can recover it: the payload has
        // no peer and no path, so the engine has to search, and a search cannot know what the reader picked.
        var intent = Pinned();

        Assert.Equal(Peer, intent.SoulseekUsername);
        Assert.Equal(RemotePath, intent.SoulseekRemotePath);
        Assert.Equal(RemoteSize, intent.SoulseekRemoteSizeBytes);
        Assert.Equal(Quality, intent.Quality);

        Assert.True(
            SoulseekPinnedCandidatePolicy.IsManualSelection(intent),
            "a peer and a path chosen in the drawer is a manual selection");
    }

    [Fact]
    public void ThePinIsDistinguishedFromALibraryTrackRequest()
    {
        // The other half of the rule: a track request from the library carries no peer and no path, and must
        // stay unpinned so the engine is still allowed to search for itself.
        var unpinned = new DeezSpoTag.Services.Download.Shared.Models.DownloadIntent
        {
            SourceService = SoulseekQueueItem.EngineId,
            Artist = "Massive Attack",
            Title = "Angel"
        };

        Assert.False(SoulseekPinnedCandidatePolicy.IsManualSelection(unpinned));
    }

    [Fact]
    public void AnItemIsOnlyRecognisedAsPinnedOnceItCarriesAQuality()
    {
        // SoulseekQualityCode is what tells a restart that the peer and path were the reader's choice rather
        // than a peer an automated search happened to try. Without it the same peer and path are ambiguous,
        // and treating them as a pin would freeze an automated request onto whichever peer it last saw.
        var withoutQuality = new SoulseekQueueItem
        {
            SourceService = SoulseekQueueItem.EngineId,
            SoulseekUsername = Peer,
            SoulseekRemotePath = RemotePath
        };

        var withQuality = new SoulseekQueueItem
        {
            SourceService = SoulseekQueueItem.EngineId,
            SoulseekUsername = Peer,
            SoulseekRemotePath = RemotePath,
            SoulseekQualityCode = Quality
        };

        Assert.False(SoulseekPinnedCandidatePolicy.IsManualSelection(withoutQuality));
        Assert.True(SoulseekPinnedCandidatePolicy.IsManualSelection(withQuality));
    }

    [Fact]
    public void AnotherEngineMayNotInheritAPeerAndAPath()
    {
        // An intent is reused across engines. A Deezer item carrying a Soulseek pin would make its engine go
        // and fetch a Soulseek file under a Deezer request, which is the reason the guard checks the source.
        var foreign = new DeezSpoTag.Services.Download.Shared.Models.DownloadIntent
        {
            SourceService = "deezer",
            SoulseekUsername = Peer,
            SoulseekRemotePath = RemotePath
        };

        Assert.False(SoulseekPinnedCandidatePolicy.IsManualSelection(foreign));
    }

    [Fact]
    public void ABlankPeerOrPathIsNotAPin()
    {
        var noPeer = new DeezSpoTag.Services.Download.Shared.Models.DownloadIntent
        {
            SourceService = SoulseekQueueItem.EngineId,
            SoulseekRemotePath = RemotePath
        };

        var noPath = new DeezSpoTag.Services.Download.Shared.Models.DownloadIntent
        {
            SourceService = SoulseekQueueItem.EngineId,
            SoulseekUsername = Peer
        };

        Assert.False(SoulseekPinnedCandidatePolicy.IsManualSelection(noPeer));
        Assert.False(SoulseekPinnedCandidatePolicy.IsManualSelection(noPath));
    }

    [Fact]
    public void AnUnpinnedFailureMayTryAnotherPeerButACancelledOrUnavailableOneMayNot()
    {
        // Retrying is what finds a peer that will serve the file, but a reader who cancelled, or an slskd with
        // no Soulseek connection, would only fail the same way twice.
        Assert.True(SoulseekPinnedCandidatePolicy.ShouldSearchAgain(new InvalidOperationException("peer refused")));
        Assert.False(SoulseekPinnedCandidatePolicy.ShouldSearchAgain(new OperationCanceledException()));
        Assert.False(SoulseekPinnedCandidatePolicy.ShouldSearchAgain(new SoulseekUnavailableException("not logged in")));
    }
}
