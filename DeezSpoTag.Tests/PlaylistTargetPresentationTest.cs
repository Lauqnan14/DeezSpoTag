using DeezSpoTag.Web.Controllers.Api;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// The playlist sync and merge target dropdowns both present the same destination list, and both
/// used to carry their own copy of the id-to-label switch. A label added to one was easy to forget
/// in the other, which is exactly the drift this file now prevents.
/// </summary>
public sealed class PlaylistTargetPresentationTest
{
    /// <summary>
    ///     Every destination the app can offer, with the label the UI is expected to show.
    /// </summary>
    /// <remarks>
    ///     "TIDAL" and "YouTube Music" are deliberate: they are the spellings the UI shipped with,
    ///     and changing one would be a visible rename rather than a cleanup. The test pins them so
    ///     that is a deliberate decision instead of an accident.
    /// </remarks>
    [Theory]
    [InlineData("plex", "Plex")]
    [InlineData("jellyfin", "Jellyfin")]
    [InlineData("navidrome", "Navidrome")]
    [InlineData("ytmusic", "YouTube Music")]
    [InlineData("spotify", "Spotify")]
    [InlineData("deezer", "Deezer")]
    [InlineData("qobuz", "Qobuz")]
    [InlineData("tidal", "TIDAL")]
    [InlineData("applemusic", "Apple Music")]
    public void Label_IsTheDisplayNameForEveryKnownDestination(string target, string expected)
        => Assert.Equal(expected, PlaylistTargetPresentation.Label(target));

    /// <summary>
    ///     A destination the app does not know about must still be offered, using its own id as the
    ///     label. Dropping it would make a newly configured destination invisible with no way to
    ///     select it, which is worse than showing a raw id.
    /// </summary>
    [Theory]
    [InlineData("boomplay")]
    [InlineData("soundcloud")]
    [InlineData("some-future-service")]
    public void Label_FallsBackToTheTargetIdWhenTheDestinationIsUnknown(string target)
        => Assert.Equal(target, PlaylistTargetPresentation.Label(target));

    /// <summary>
    ///     The self-hosted servers are the ones surfaced through the library paths rather than the
    ///     platform ones, so the classification has to agree with the label map. If the two ever
    ///     disagreed, a server would be labelled as one thing and offered as another.
    /// </summary>
    [Theory]
    [InlineData("plex", true)]
    [InlineData("jellyfin", true)]
    [InlineData("navidrome", true)]
    [InlineData("spotify", false)]
    [InlineData("deezer", false)]
    [InlineData("tidal", false)]
    [InlineData("applemusic", false)]
    [InlineData("boomplay", false)]
    public void IsLibraryServer_IsTrueOnlyForTheSelfHostedServers(string target, bool expected)
        => Assert.Equal(expected, PlaylistTargetPresentation.IsLibraryServer(target));

    /// <summary>
    ///     A labelled server and a platform are the two buckets the sync surface splits targets
    ///     into, and neither may be empty: a caller that receives an unknown kind has nothing to
    ///     branch on.
    /// </summary>
    [Fact]
    public void TheTwoSurfaceKindsAreDistinctAndNonEmpty()
    {
        Assert.NotEqual(PlaylistTargetPresentation.LibraryKind, PlaylistTargetPresentation.PlatformKind);
        Assert.False(string.IsNullOrWhiteSpace(PlaylistTargetPresentation.LibraryKind));
        Assert.False(string.IsNullOrWhiteSpace(PlaylistTargetPresentation.PlatformKind));
    }

    /// <summary>
    ///     The labels are presentation only, so they must be stable and free of the id they wrap.
    ///     "TIDAL" rather than "tidal" is the one that would otherwise look like a bug.
    /// </summary>
    [Fact]
    public void LabelsAreDisplayNamesAndNeverTheRawId()
    {
        var labelled = new[] { "plex", "jellyfin", "navidrome", "ytmusic", "spotify", "deezer", "qobuz", "tidal", "applemusic" };

        foreach (var target in labelled)
        {
            Assert.NotEqual(target, PlaylistTargetPresentation.Label(target));
        }
    }
}