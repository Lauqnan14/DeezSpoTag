using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class SpotifyTracklistTerminalMetadataPolicyTests
{
    [Fact]
    public void MatchedResult_DoesNotEnterTerminalMetadataPass()
    {
        var result = new SpotifyTracklistResolveResult(
            "123",
            SpotifyTracklistResolveOutcome.Matched,
            "isrc_match");

        Assert.False(SpotifyTracklistMatchBackgroundService.ShouldRunTerminalMetadataPass(result));
    }

    [Fact]
    public void TransientFailure_DoesNotEnterTerminalMetadataPass()
    {
        var result = new SpotifyTracklistResolveResult(
            null,
            SpotifyTracklistResolveOutcome.TransientFailure,
            "transient_upstream_failure");

        Assert.False(SpotifyTracklistMatchBackgroundService.ShouldRunTerminalMetadataPass(result));
    }

    [Fact]
    public void HardMismatch_EntersTerminalMetadataPass()
    {
        var result = new SpotifyTracklistResolveResult(
            null,
            SpotifyTracklistResolveOutcome.HardMismatch,
            "unresolved");

        Assert.True(SpotifyTracklistMatchBackgroundService.ShouldRunTerminalMetadataPass(result));
    }
}
