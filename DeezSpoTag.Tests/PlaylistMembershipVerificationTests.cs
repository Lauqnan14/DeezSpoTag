using Xunit;

namespace DeezSpoTag.Tests;

public sealed class PlaylistMembershipVerificationTests
{
    [Theory]
    [InlineData(50, 50, true, true)]
    [InlineData(50, 50, false, false)]
    [InlineData(50, 49, true, false)]
    [InlineData(0, 0, true, true)]
    [InlineData(267, 50, true, false)] // intended is 50 resolved — not 267 local
    public void IsIntendedMembershipVerified_UsesResolvedSetNotLocalTrackCount(
        int intended,
        int verified,
        bool writeComplete,
        bool expected)
    {
        // When intended=50 and verified=50, success even if many local tracks lacked target ids.
        Assert.Equal(
            expected,
            DeezSpoTag.Web.Services.PlaylistSyncService.IsIntendedMembershipVerified(
                intended,
                verified,
                writeComplete));
    }

    [Fact]
    public void IsIntendedMembershipVerified_AllowsPartialTargetResolutionAsSuccess()
    {
        // 267 local tracks, only 50 resolved to Plex rating keys, all 50 verified on playlist.
        Assert.True(DeezSpoTag.Web.Services.PlaylistSyncService.IsIntendedMembershipVerified(50, 50));
        Assert.True(DeezSpoTag.Web.Services.PlaylistSyncService.HasUnresolvedTargetIdentities(267, 50));
        Assert.False(DeezSpoTag.Web.Services.PlaylistSyncService.HasUnresolvedTargetIdentities(50, 50));
    }

    [Theory]
    [InlineData("/music/Artist/Album/track.flac", "/music/Artist/Album/track.flac", true)]
    [InlineData("/data/media/Artist/Album/track.flac", "/music/Artist/Album/track.flac", true)]
    [InlineData("/music/Artist/Album/track.flac", "/music/Other/Album/track.flac", true)] // same parent+file name
    [InlineData("/music/a.flac", "/music/b.flac", false)]
    [InlineData(null, "/music/a.flac", false)]
    public void MediaServerPathsReferToSameFile_MatchesAcrossMountRoots(
        string? localPath,
        string? serverPath,
        bool expected)
    {
        Assert.Equal(
            expected,
            DeezSpoTag.Web.Services.PlaylistSyncService.MediaServerPathsReferToSameFile(localPath, serverPath));
    }
}
