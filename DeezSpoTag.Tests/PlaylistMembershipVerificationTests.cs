using Xunit;

namespace DeezSpoTag.Tests;

public sealed class PlaylistMembershipVerificationTests
{
    [Theory]
    [InlineData(50, 50, true, true)]
    [InlineData(50, 50, false, false)]
    [InlineData(50, 49, true, false)]
    [InlineData(0, 0, true, true)]
    [InlineData(267, 50, true, false)]
    public void IsIntendedMembershipVerified_RequiresEveryLocalTrackOnTarget(
        int intended,
        int verified,
        bool writeComplete,
        bool expected)
    {
        Assert.Equal(
            expected,
            DeezSpoTag.Web.Services.PlaylistSyncService.IsIntendedMembershipVerified(
                intended,
                verified,
                writeComplete));
    }

    [Fact]
    public void IsIntendedMembershipVerified_RejectsPartialTargetResolution()
    {
        Assert.False(DeezSpoTag.Web.Services.PlaylistSyncService.IsIntendedMembershipVerified(267, 50));
        Assert.True(DeezSpoTag.Web.Services.PlaylistSyncService.HasUnresolvedTargetIdentities(267, 50));
        Assert.False(DeezSpoTag.Web.Services.PlaylistSyncService.HasUnresolvedTargetIdentities(50, 50));
    }

    [Theory]
    [InlineData(true, true, true, "Jellyfin playlist created.")]
    [InlineData(false, true, true, "Jellyfin playlist already exists.")]
    [InlineData(true, false, true, "Jellyfin playlist created. Name/description did not verify.")]
    [InlineData(true, true, false, "Jellyfin playlist created. Artwork was not applied (no cached cover yet, or the update failed).")]
    [InlineData(true, false, false, "Jellyfin playlist created. Name/description did not verify. Artwork was not applied (no cached cover yet, or the update failed).")]
    public void BuildProvisioningMessage_ReportsCreationAndVerificationState(
        bool created,
        bool metadataSynced,
        bool artworkSynced,
        string expected)
    {
        Assert.Equal(
            expected,
            DeezSpoTag.Web.Services.PlaylistSyncService.BuildProvisioningMessage(
                "Jellyfin",
                created,
                metadataSynced,
                artworkSynced));
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
