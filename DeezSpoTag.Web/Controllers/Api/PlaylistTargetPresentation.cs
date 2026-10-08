using DeezSpoTag.Web.Services;

namespace DeezSpoTag.Web.Controllers.Api;

/// <summary>
///     The display label and surface kind for a playlist sync or merge target.
/// </summary>
/// <remarks>
///     Both the sync-target and merge-target endpoints offered the same target switch, so the
///     mapping lived twice and a label added to one was easy to forget in the other. It is here
///     once, and the endpoints call it.
///     <para>
///         An unrecognised target falls through to its own id rather than being dropped, so a
///         destination the app learns about later still appears in the UI with a usable label.
///     </para>
/// </remarks>
internal static class PlaylistTargetPresentation
{
    /// <summary>The surface a target is synced from: a self-hosted library server, or a platform.</summary>
    internal const string LibraryKind = "library";

    /// <summary>The surface a target is synced from: a self-hosted library server, or a platform.</summary>
    internal const string PlatformKind = "platform";

    /// <summary>
    ///     The label to show for <paramref name="target" />, or the target id itself when it is not
    ///     one of the known destinations.
    /// </summary>
    internal static string Label(string target) => target switch
    {
        MediaServerTargetServices.Plex => "Plex",
        MediaServerTargetServices.Jellyfin => "Jellyfin",
        MediaServerTargetServices.Navidrome => "Navidrome",
        YouTubeMusicPlatform => "YouTube Music",
        PlatformTrackIdentityResolver.SpotifyService => "Spotify",
        PlatformTrackIdentityResolver.DeezerService => "Deezer",
        PlatformTrackIdentityResolver.QobuzService => "Qobuz",
        PlatformTrackIdentityResolver.TidalService => "TIDAL",
        PlatformTrackIdentityResolver.AppleMusicService => "Apple Music",
        _ => target,
    };

    /// <summary>
    ///     YouTube Music's target id. It has no canonical constants class of its own, so it is
    ///     declared here beside the mapping that uses it rather than repeated as a raw literal.
    /// </summary>
    private const string YouTubeMusicPlatform = "ytmusic";

    /// <summary>
    ///     Whether a target is a self-hosted library server.
    /// </summary>
    /// <remarks>
    ///     Driven off the same three constants the label is, so a new library server cannot be
    ///     labelled as one and simultaneously be classified as a platform.
    /// </remarks>
    internal static bool IsLibraryServer(string target)
        => target is MediaServerTargetServices.Plex
            or MediaServerTargetServices.Jellyfin
            or MediaServerTargetServices.Navidrome;
}