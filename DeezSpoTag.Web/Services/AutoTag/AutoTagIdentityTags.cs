namespace DeezSpoTag.Web.Services.AutoTag;

/// <summary>
/// The single contract for provider identity namespaces. Every provider owns one
/// alias family per <see cref="ProviderIdentityField"/>; aliases never cross providers
/// or fields, and the generic compatibility fields (ALBUMID, ARTISTID, ALBUMARTISTID,
/// RECORDINGID, URL, WWWAUDIOFILE) are owned by no family at all.
/// </summary>
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

    /// <summary>Apple album-ID aliases only. Release aliases live in
    /// <see cref="AppleReleaseIdAliases"/> — the two fields are separate.</summary>
    public static readonly string[] AppleAlbumIdAliases =
    [
        "APPLE_ALBUM_ID",
        "APPLE_MUSIC_ALBUM_ID",
        "ITUNESALBUMID",
        "ITUNES_ALBUM_ID",
        "ITUNES_ALBUMID"
    ];

    /// <summary>Apple release-ID aliases only.</summary>
    public static readonly string[] AppleReleaseIdAliases =
    [
        "APPLE_RELEASE_ID",
        "ITUNES_RELEASE_ID"
    ];

    /// <summary>Legacy union of both Apple identity fields, kept only for the
    /// album-identity seed/migration readers that must not skip a field.</summary>
    public static readonly string[] AppleLegacyIdentityAliases =
        AppleAlbumIdAliases.Concat(AppleReleaseIdAliases).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static readonly string[] AppleAlbumArtistIdAliases =
    [
        "APPLE_ALBUM_ARTIST_ID",
        "APPLE_MUSIC_ALBUM_ARTIST_ID",
        "ITUNESALBUMARTISTID"
    ];

    private static readonly string[] AppleUrlAliases =
    [
        "APPLE_MUSIC_URL",
        "APPLE_URL"
    ];

    private static readonly string[] SpotifyTrackIdAliases =
    [
        "SPOTIFY_TRACKID",
        "SPOTIFY_ID",
        "SPOTIFYID"
    ];

    private static readonly string[] DeezerTrackIdAliases =
    [
        "DEEZERID",
        "DEEZER_ID"
    ];

    /// <summary>Every provider ID AutoTag registers for metadata matching. A provider is
    /// listed even when it currently returns no value for some identity field: an empty
    /// authoritative field must still leave existing aliases untouched.</summary>
    public static readonly string[] KnownProviders =
    [
        "musicbrainz",
        "beatport",
        "discogs",
        "traxsource",
        "bandcamp",
        "itunes",
        "spotify",
        "deezer",
        "boomplay",
        "audiomack",
        "shazam",
        "qobuz",
        "tidal",
        "amazon"
    ];

    private const string MusicBrainzProvider = "musicbrainz";
    private const string ItunesProvider = "itunes";

    /// <summary>Canonicalizes a provider ID so that Apple and iTunes share one
    /// provider-owned namespace and lookups are case/whitespace insensitive.</summary>
    public static string NormalizeProviderId(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return string.Empty;
        }

        var normalized = providerId.Trim().ToLowerInvariant();
        return normalized switch
        {
            "apple" or "applemusic" or "apple_music" or "apple music" => ItunesProvider,
            _ => normalized
        };
    }

    /// <summary>Resolves the exact provider+field alias family used for writing,
    /// cleanup, presence checks, and verification.</summary>
    public static ProviderIdentityTagFamily ResolveFamily(string? providerId, ProviderIdentityField field)
    {
        var provider = NormalizeProviderId(providerId);
        if (provider.Length == 0)
        {
            provider = "unknown";
        }

        if (provider == MusicBrainzProvider)
        {
            return ResolveMusicBrainzFamily(field);
        }

        if (provider == ItunesProvider)
        {
            return ResolveItunesFamily(field);
        }

        return ResolveDefaultFamily(provider, field);
    }

    private static ProviderIdentityTagFamily ResolveMusicBrainzFamily(ProviderIdentityField field) => field switch
    {
        ProviderIdentityField.TrackId => Family(
            SupportedTag.TrackId,
            ["MUSICBRAINZ_TRACK_ID"],
            ["MUSICBRAINZ_TRACK_ID", "MUSICBRAINZ_TRACKID", "MUSICBRAINZ_RECORDINGID", "MUSICBRAINZ_RECORDING_ID"]),
        ProviderIdentityField.AlbumId => Family(
            SupportedTag.AlbumId,
            ["MUSICBRAINZ_ALBUMID"],
            ["MUSICBRAINZ_ALBUMID", "MUSICBRAINZ_ALBUM_ID"]),
        ProviderIdentityField.ReleaseId => Family(
            SupportedTag.ReleaseId,
            ["MUSICBRAINZ_RELEASE_ID"],
            ["MUSICBRAINZ_RELEASE_ID", "MUSICBRAINZ_RELEASEID"]),
        ProviderIdentityField.ArtistId => Family(
            SupportedTag.ArtistId,
            ["MUSICBRAINZ_ARTISTID"],
            ["MUSICBRAINZ_ARTISTID", "MUSICBRAINZ_ARTIST_ID"]),
        ProviderIdentityField.AlbumArtistId => Family(
            SupportedTag.AlbumArtistId,
            ["MUSICBRAINZ_ALBUMARTISTID"],
            ["MUSICBRAINZ_ALBUMARTISTID", "MUSICBRAINZ_ALBUM_ARTIST_ID"]),
        ProviderIdentityField.Url => Family(SupportedTag.URL, ["MUSICBRAINZ_URL"], ["MUSICBRAINZ_URL"]),
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };

    private static ProviderIdentityTagFamily ResolveItunesFamily(ProviderIdentityField field) => field switch
    {
        ProviderIdentityField.TrackId => Family(
            SupportedTag.TrackId,
            [ItunesTrackId],
            [ItunesTrackId, .. AppleTrackIdAliases]),
        ProviderIdentityField.AlbumId => Family(
            SupportedTag.AlbumId,
            ["ITUNES_ALBUM_ID"],
            ["ITUNES_ALBUM_ID", .. AppleAlbumIdAliases]),
        ProviderIdentityField.ReleaseId => Family(
            SupportedTag.ReleaseId,
            ["ITUNES_RELEASE_ID"],
            ["ITUNES_RELEASE_ID", .. AppleReleaseIdAliases]),
        ProviderIdentityField.ArtistId => Family(
            SupportedTag.ArtistId,
            ["ITUNES_ARTIST_ID"],
            ["ITUNES_ARTIST_ID", .. AppleArtistIdAliases]),
        ProviderIdentityField.AlbumArtistId => Family(
            SupportedTag.AlbumArtistId,
            ["ITUNES_ALBUM_ARTIST_ID"],
            ["ITUNES_ALBUM_ARTIST_ID", .. AppleAlbumArtistIdAliases]),
        ProviderIdentityField.Url => Family(
            SupportedTag.URL,
            ["ITUNES_URL"],
            ["ITUNES_URL", .. AppleUrlAliases]),
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };

    private static ProviderIdentityTagFamily ResolveDefaultFamily(string provider, ProviderIdentityField field)
    {
        var prefix = provider.ToUpperInvariant();
        return field switch
        {
            ProviderIdentityField.TrackId => Family(
                SupportedTag.TrackId,
                [$"{prefix}_TRACK_ID"],
                [$"{prefix}_TRACK_ID", .. LegacyAliases(provider, field)]),
            ProviderIdentityField.AlbumId => Family(
                SupportedTag.AlbumId,
                [$"{prefix}_ALBUM_ID"],
                [$"{prefix}_ALBUM_ID", .. LegacyAliases(provider, field)]),
            ProviderIdentityField.ReleaseId => Family(
                SupportedTag.ReleaseId,
                [$"{prefix}_RELEASE_ID"],
                [$"{prefix}_RELEASE_ID", .. LegacyAliases(provider, field)]),
            ProviderIdentityField.ArtistId => Family(
                SupportedTag.ArtistId,
                [$"{prefix}_ARTIST_ID"],
                [$"{prefix}_ARTIST_ID", .. LegacyAliases(provider, field)]),
            ProviderIdentityField.AlbumArtistId => Family(
                SupportedTag.AlbumArtistId,
                [$"{prefix}_ALBUM_ARTIST_ID"],
                [$"{prefix}_ALBUM_ARTIST_ID", .. LegacyAliases(provider, field)]),
            ProviderIdentityField.Url => Family(
                SupportedTag.URL,
                [$"{prefix}_URL"],
                [$"{prefix}_URL", .. LegacyAliases(provider, field)]),
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
    }

    private static string[] LegacyAliases(string provider, ProviderIdentityField field)
    {
        if (field != ProviderIdentityField.TrackId)
        {
            return [];
        }

        return provider switch
        {
            "spotify" => SpotifyTrackIdAliases,
            "deezer" => DeezerTrackIdAliases,
            _ => []
        };
    }

    private static ProviderIdentityTagFamily Family(
        SupportedTag supportedTag,
        string[] writeNames,
        string[] cleanupNames)
    {
        var cleanup = cleanupNames
            .Concat(writeNames)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var writes = writeNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new ProviderIdentityTagFamily(supportedTag, writes, cleanup);
    }

    public static string? ReadAppleTrackId(AutoTagAudioInfo info)
        => AutoTagTagValueReader.ReadFirstTagValue(info, AppleTrackIdAliases);

    public static string? ReadAppleArtistId(AutoTagAudioInfo info)
        => AutoTagTagValueReader.ReadFirstTagValue(info, AppleArtistIdAliases);
}