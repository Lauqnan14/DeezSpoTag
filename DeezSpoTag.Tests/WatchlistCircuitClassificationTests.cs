using System;
using System.IO;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class WatchlistCircuitClassificationTests
{
    private static string Root => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Fact]
    public void CircuitsIncrementOnlyOnTransportAndAuth()
    {
        var worker = File.ReadAllText(Path.Combine(
            Root,
            "DeezSpoTag.Web",
            "Services",
            "WatchlistPostDownloadSyncService.cs"));

        Assert.Contains("ShouldIncrementTargetCircuit(outcome.FailureClass)", worker, StringComparison.Ordinal);
        Assert.Contains("SyncFailureClass.Transport or SyncFailureClass.Auth", worker, StringComparison.Ordinal);
        Assert.Contains(
            "sync is temporarily paused after repeated {FormatCircuitFailureClass(targetCircuit.Reason)} failures.",
            worker,
            StringComparison.Ordinal);
        Assert.DoesNotContain("repeated failures: {targetCircuit.Reason}", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("HasNoTargetCoverage", worker, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("No eligible playlist tracks are visible in the DeezSpoTag library yet.")]
    [InlineData("No Plex matches found for this playlist.")]
    [InlineData("No Jellyfin matches found for this playlist.")]
    [InlineData("Jellyfin playlist verification is incomplete; unresolved target identities will be refreshed and retried. Source tracks: 267.")]
    public void IdentityAndLibraryEmptyMessagesDoNotIncrementCircuit(string message)
    {
        var result = PlaylistSyncResult.IsLibraryEmptyMessage(message)
            ? PlaylistSyncResult.NoLocalTracks(message)
            : PlaylistSyncResult.IsNoTargetMatchesMessage(message)
                ? PlaylistSyncResult.IdentityGap(message)
                : PlaylistSyncResult.Failed(message, PlaylistSyncResultKind.WriteLag);

        Assert.True(result.Success || result.Kind == PlaylistSyncResultKind.WriteLag);
        Assert.False(
            WatchlistPostDownloadSyncService.ClassifySyncFailureClass(result) is SyncFailureClass.Transport
                or SyncFailureClass.Auth);
    }

    [Theory]
    [InlineData("Jellyfin sync failed: connection refused", SyncFailureClass.Transport)]
    [InlineData("Plex is not configured.", SyncFailureClass.Auth)]
    [InlineData("timeout talking to navidrome", SyncFailureClass.Transport)]
    public void TransportAndAuthFailuresAreClassifiedForCircuit(string message, SyncFailureClass expected)
    {
        Assert.Equal(expected, WatchlistPostDownloadSyncService.ClassifyRetryFailureClass(message));
    }

    [Fact]
    public void OpenCircuitTextNeverIncludesMembershipMath()
    {
        var worker = File.ReadAllText(Path.Combine(
            Root,
            "DeezSpoTag.Web",
            "Services",
            "WatchlistPostDownloadSyncService.cs"));
        var fingerprintWrite = worker.IndexOf(
            "var fingerprint = $\"{NormalizeTargetService(targetService)}:{failureClass}:0\";",
            StringComparison.Ordinal);
        Assert.True(fingerprintWrite > 0);
        Assert.DoesNotContain("Source tracks:", worker[fingerprintWrite..(fingerprintWrite + 400)], StringComparison.Ordinal);
    }

    [Fact]
    public void FailedFactoryRequiresAKindAndRejectsSuccessKinds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PlaylistSyncResult.Failed("x", PlaylistSyncResultKind.Completed));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PlaylistSyncResult.Failed("x", PlaylistSyncResultKind.IdentityGap));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PlaylistSyncResult.Failed("x", PlaylistSyncResultKind.NoLocalTracks));
        Assert.True(PlaylistSyncResult.NoLocalTracks("No eligible playlist tracks are visible in the DeezSpoTag library yet.").Success);
        Assert.True(PlaylistSyncResult.IdentityGap("No Plex matches found for this playlist.").Success);
    }
}
