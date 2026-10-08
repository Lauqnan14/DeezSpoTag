using System;
using System.Text.Json;
using System.Threading.Tasks;
using System.Text.Json.Nodes;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Soulseek;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     Which unpinned peer-search failures may try another peer.
/// </summary>
/// <remarks>
///     <para>
///         These rules apply only to an unpinned request from another source. The download service does not
///         call this retry policy for a manual Soulseek peer/file selection.
///     </para>
///     <para>
///         Two failures are not the file's fault and must not be retried as one: a cancelled item (the reader
///         stopped it) and an unattached or unconfigured Soulseek (there is no network to search). Retrying
///         either would search a network that is not there and then report the same failure twice.
///     </para>
/// </remarks>
public sealed class SoulseekPinnedCandidatePolicyTest
{
    [Fact]
    public void OnlyASoulseekOriginWithBothPeerAndPathIsAManualSelection()
    {
        Assert.True(SoulseekPinnedCandidatePolicy.IsManualSelection(new DownloadIntent
        {
            SourceService = "soulseek", SoulseekUsername = "peer", SoulseekRemotePath = "Album/Track.flac"
        }));
        Assert.False(SoulseekPinnedCandidatePolicy.IsManualSelection(new DownloadIntent
        {
            SourceService = "deezer", SoulseekUsername = "peer", SoulseekRemotePath = "Album/Track.flac"
        }));
        Assert.False(SoulseekPinnedCandidatePolicy.IsManualSelection(new DownloadIntent
        {
            SourceService = "soulseek", SoulseekUsername = "peer"
        }));
        Assert.True(SoulseekPinnedCandidatePolicy.IsManualSelection(JsonNode.Parse(
            """{"sourceService":"soulseek","soulseekUsername":"peer","soulseekRemotePath":"Album/Track.flac","soulseekQualityCode":"FLAC"}""")!.AsObject()));
    }

    [Fact]
    public void APeerFoundByCrossEngineFallbackIsNotAManualSelection()
    {
        var payload = new SoulseekQueueItem
        {
            SourceService = "soulseek",
            Quality = "FLAC_HI_RES",
            SoulseekUsername = "automatically-found-peer",
            SoulseekRemotePath = "Album/Track.flac",
            SoulseekQualityCode = string.Empty
        };

        Assert.False(SoulseekPinnedCandidatePolicy.IsManualSelection(payload));
        Assert.False(SoulseekPinnedCandidatePolicy.IsManualSelection(
            JsonNode.Parse(JsonSerializer.Serialize(payload))!.AsObject()));
    }

    [Theory]
    [InlineData("peer went away mid-transfer")]
    [InlineData("slskd did not create a transfer for the file.")]
    [InlineData("the transfer completed but no file was found at the expected path")]
    public void AnUnpinnedPeerFailureMaySearchAgain(string message)
    {
        Assert.True(
            SoulseekPinnedCandidatePolicy.ShouldSearchAgain(new InvalidOperationException(message)),
            "Only an unpinned track request may try another peer.");
    }

    [Fact]
    public void AFailedTransferSaysWhichPeerItCameFrom()
    {
        // The peer is carried on the failure, which is what lets the next attempt avoid it.
        var failure = new SoulseekTransferException(
            "The transfer of \"a.flac\" from vrdel did not finish - it was reported as Errored and stopped at 35.8% (49293344 of 137663591 bytes).",
            "vrdel");

        Assert.Equal("vrdel", failure.Username);
        Assert.Contains("35.8%", failure.Message, StringComparison.Ordinal);
        Assert.True(
            SoulseekPinnedCandidatePolicy.ShouldSearchAgain(failure),
            "An unpinned track request may try another peer after a stalled transfer.");
    }

    [Fact]
    public void ACancelledItemIsNotSearchedForAgain()
    {
        Assert.False(SoulseekPinnedCandidatePolicy.ShouldSearchAgain(new OperationCanceledException()));
        Assert.False(SoulseekPinnedCandidatePolicy.ShouldSearchAgain(new TaskCanceledException()));
    }

    [Fact]
    public void AnUnattachedSoulseekIsNotSearchedForAgain()
    {
        Assert.False(
            SoulseekPinnedCandidatePolicy.ShouldSearchAgain(new SoulseekUnavailableException("Soulseek is not configured.")),
            "There is no peer network to search again, so a second attempt can only fail the same way.");
    }
}
