namespace DeezSpoTag.Web.Services.AutoTag;

internal static class AutoTagIdentityTags
{
    public const string AppleTrackId = "APPLE_TRACK_ID";
    public const string AppleMusicTrackId = "APPLE_MUSIC_TRACK_ID";
    public const string ItunesTrackId = "ITUNES_TRACK_ID";

    public static readonly string[] AppleTrackIdAliases =
    [
        AppleTrackId,
        "APPLE_TRACKID",
        AppleMusicTrackId,
        "APPLE_MUSIC_TRACKID",
        "APPLEMUSIC_TRACK_ID",
        "APPLEMUSIC_TRACKID",
        "APPLEID",
        ItunesTrackId,
        "ITUNESCATALOGID",
        "ITUNES_TRACKID"
    ];

    public static readonly string[] AppleArtistIdAliases =
    [
        "APPLE_ARTIST_ID",
        "APPLE_ARTISTID",
        "APPLE_MUSIC_ARTIST_ID",
        "ITUNES_ARTIST_ID",
        "ITUNESARTISTID"
    ];

    public static readonly string[] AppleReleaseIdAliases =
    [
        "APPLE_ALBUM_ID",
        "APPLE_RELEASE_ID",
        "APPLE_MUSIC_ALBUM_ID",
        "ITUNES_RELEASE_ID",
        "ITUNESALBUMID",
        "ITUNES_ALBUM_ID"
    ];

    public static string? ReadAppleTrackId(AutoTagAudioInfo info)
        => AutoTagTagValueReader.ReadFirstTagValue(info, AppleTrackIdAliases);

    public static string? ReadAppleArtistId(AutoTagAudioInfo info)
        => AutoTagTagValueReader.ReadFirstTagValue(info, AppleArtistIdAliases);
}
