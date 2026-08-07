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
    [InlineData(406, 405, true)] // 1 short of 406 -- within the absolute exception tolerance
    [InlineData(406, 404, true)] // exactly at the absolute exception tolerance (2)
    [InlineData(406, 403, true)] // 3 short but 403/406 = 99.26% clears the coverage ratio
    [InlineData(50, 47, false)] // 3 short of 50 (94%) exceeds both the absolute cap and the ratio
    [InlineData(50, 49, true)]
    [InlineData(50, 50, false)] // already fully verified -- not this method's job to report that
    [InlineData(0, 0, false)] // nothing configured/intended, nothing to accept
    [InlineData(1000, 980, true)] // exactly the 98% coverage boundary
    [InlineData(1000, 978, false)] // just under the 98% coverage boundary, and over the absolute cap
    public void ShouldAcceptMembershipWithExceptions_ToleratesASmallPermanentGap(
        int intended,
        int verified,
        bool expected)
    {
        Assert.Equal(
            expected,
            DeezSpoTag.Web.Services.PlaylistSyncService.ShouldAcceptMembershipWithExceptions(intended, verified));
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
