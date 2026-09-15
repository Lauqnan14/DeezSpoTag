using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Web.Services.AutoTag;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Regression guard for the cross-platform release-id leak: the folder album identity
/// must never stamp one provider's ids into another provider's namespace, and only the
/// provider's own alias family may carry an album-scoped identity.
/// </summary>
public sealed class AlbumIdentityReleaseIdGuardTest
{
    private static AutoTagTrack ApplyEstablishedAlbumIdentity(AutoTagTrack track, AlbumIdentity identity, string platformId)
    {
        var method = typeof(LocalAutoTagRunner).GetMethod(
            "ApplyEstablishedAlbumIdentity",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        method!.Invoke(null, [track, identity, platformId]);
        return track;
    }

    private static bool IsPlatformReleaseIdShapeValid(string? platformId, string value)
    {
        var method = typeof(LocalAutoTagRunner).GetMethod(
            "IsPlatformReleaseIdShapeValid",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<bool>(method!.Invoke(null, [platformId, value]));
    }

    private static AlbumIdentity BuildAlbumIdentityCandidate(AutoTagTrack track, string providerId, string? releaseId, string? albumId = null, string? albumArtistId = null)
    {
        var method = typeof(LocalAutoTagRunner).GetMethod(
            "BuildAlbumIdentityCandidate",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var payload = new ProviderIdentityPayload(
            providerId,
            TrackId: null,
            albumId,
            releaseId,
            ArtistId: null,
            albumArtistId,
            Url: null,
            IsNativeProviderResult: true);
        return Assert.IsType<AlbumIdentity>(method!.Invoke(null, [track, payload]));
    }

    private static AlbumIdentity NormalizeSharedAlbumIdentity(AlbumIdentity identity)
    {
        var method = typeof(LocalAutoTagRunner).GetMethod(
            "NormalizeSharedAlbumIdentity",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<AlbumIdentity>(method!.Invoke(null, [identity]));
    }

    private static AlbumIdentity WithProvider(string providerId, string? albumId, string? releaseId, string? albumArtistId)
        => AlbumIdentity.Empty.WithProviderIdentity(
            providerId,
            new ProviderAlbumIdentity(albumId, releaseId, albumArtistId),
            overwrite: true);

    [Fact]
    public void EstablishedIdentity_DoesNotStampForeignAlbumIdIntoPlatformReleaseId()
    {
        var track = new AutoTagTrack
        {
            Title = "Song",
            ReleaseId = "2flqcgHiEwy6XHlUuGe2ab"
        };
        var identity = WithProvider("audiomack", "943733001", "943733001", null);

        ApplyEstablishedAlbumIdentity(track, identity, "spotify");

        // The spotify stage's own release id is untouched; the foreign Audiomack id
        // never reaches the spotify release-id namespace.
        Assert.Equal("2flqcgHiEwy6XHlUuGe2ab", track.ReleaseId);
        Assert.False(track.Other.TryGetValue("SPOTIFY_RELEASE_ID", out var spotifyReleaseId));
        Assert.False(spotifyReleaseId?.Contains("943733001") == true);
        // The shared MusicBrainz-shaped slots reject the non-GUID id entirely.
        Assert.False(track.Other.ContainsKey("MUSICBRAINZ_ALBUMID"));
        Assert.False(track.Other.ContainsKey("MUSICBRAINZ_RELEASE_ID"));
        Assert.False(track.Other.ContainsKey("ALBUMID"));
    }

    [Fact]
    public void EstablishedIdentity_StillStampsMusicBrainzSharedIds()
    {
        var track = new AutoTagTrack { Title = "Song" };
        var guid = "3f6b4d4c-8f8f-4d5a-9f61-1b6e20e2b0f1";
        var identity = WithProvider("musicbrainz", guid, guid, guid);

        ApplyEstablishedAlbumIdentity(track, identity, "musicbrainz");

        Assert.Equal(guid, track.ReleaseId);
        Assert.Equal(guid, track.AlbumId);
        Assert.Equal(guid, track.Other["MUSICBRAINZ_ALBUMID"].First());
        Assert.Equal(guid, track.Other["MUSICBRAINZ_RELEASE_ID"].First());
        Assert.Equal(guid, track.Other["MUSICBRAINZ_ALBUMARTISTID"].First());
        // Generic compatibility fields are never written by an identity pass.
        Assert.False(track.Other.ContainsKey("ALBUMID"));
        Assert.False(track.Other.ContainsKey("ALBUMARTISTID"));
    }

    [Fact]
    public void EstablishedIdentity_PerPlatformEntryWinsForItsOwnPlatform()
    {
        var track = new AutoTagTrack
        {
            Title = "Song",
            ReleaseId = "999000111"
        };
        var identity = WithProvider("audiomack", "943733001", "943733001", null);

        ApplyEstablishedAlbumIdentity(track, identity, "audiomack");

        Assert.Equal("943733001", track.ReleaseId);
        Assert.Equal("943733001", track.Other["AUDIOMACK_RELEASE_ID"].First());
    }

    [Fact]
    public void PlatformReleaseIdShape_Contract()
    {
        // MusicBrainz: GUID only.
        Assert.True(IsPlatformReleaseIdShapeValid("musicbrainz", "3f6b4d5a-9f61-4d5a-9f61-1b6e20e2b0f1"));
        Assert.False(IsPlatformReleaseIdShapeValid("musicbrainz", "943733001"));
        // Spotify: 22-character base62.
        Assert.True(IsPlatformReleaseIdShapeValid("spotify", "2flqcgHiEwy6XHlUuGe2ab"));
        Assert.False(IsPlatformReleaseIdShapeValid("spotify", "943733001"));
        // Numeric catalog platforms.
        Assert.True(IsPlatformReleaseIdShapeValid("audiomack", "943733001"));
        Assert.True(IsPlatformReleaseIdShapeValid("shazam", "179363330"));
        Assert.False(IsPlatformReleaseIdShapeValid("audiomack", "2flqcgHiEwy6XHlUuGe2ab"));
        // Unknown platforms are unrestricted.
        Assert.True(IsPlatformReleaseIdShapeValid("customplatform", "abc123xyz"));
    }

    [Fact]
    public void IdentityIntake_RejectsForeignShapedValues()
    {
        var track = new AutoTagTrack { Title = "Song" };

        // A numeric Audiomack id must not enter the spotify release namespace.
        var spotify = BuildAlbumIdentityCandidate(track, "spotify", "943733001");
        Assert.Null(spotify.GetProviderIdentity("spotify"));

        // A 22-character Spotify id must not enter the MusicBrainz release namespace.
        var musicBrainz = BuildAlbumIdentityCandidate(track, "musicbrainz", "2flqcgHiEwy6XHlUuGe2ab");
        Assert.Null(musicBrainz.GetProviderIdentity("musicbrainz")?.ReleaseId);

        var audiomack = BuildAlbumIdentityCandidate(track, "audiomack", "943733001");
        Assert.Equal("943733001", audiomack.GetProviderIdentity("audiomack")!.ReleaseId);

        var validMusicBrainz = BuildAlbumIdentityCandidate(
            track,
            "musicbrainz",
            "3f6b4d5a-9f61-4d5a-9f61-1b6e20e2b0f1");
        Assert.Equal(
            "3f6b4d5a-9f61-4d5a-9f61-1b6e20e2b0f1",
            validMusicBrainz.GetProviderIdentity("musicbrainz")!.ReleaseId);
    }

    [Fact]
    public void AlbumIdentityCandidate_DoesNotUseProviderLocalIdAsSharedAlbumId()
    {
        var track = new AutoTagTrack
        {
            Title = "Song",
            AlbumId = "533077002",
            ReleaseId = "533077002"
        };

        var identity = BuildAlbumIdentityCandidate(track, "deezer", "533077002");

        Assert.Null(identity.AlbumId);
        Assert.Equal("533077002", identity.GetProviderIdentity("deezer")!.ReleaseId);
    }

    [Fact]
    public void AlbumIdentityCandidate_DoesNotImportMusicBrainzIdFromAnotherProvider()
    {
        const string musicBrainzAlbumId = "de02d778-2f56-4702-bb80-a93563a375f0";
        var track = new AutoTagTrack
        {
            Title = "Song",
            AlbumId = "533077002",
            Other =
            {
                ["MUSICBRAINZ_ALBUMID"] = new List<string> { musicBrainzAlbumId }
            }
        };

        var identity = BuildAlbumIdentityCandidate(track, "shazam", "179363330");

        Assert.Null(identity.AlbumId);
        Assert.Null(identity.GetProviderIdentity("musicbrainz"));
    }

    [Fact]
    public void PersistedProviderLocalIds_AreRemovedFromSharedAlbumIdentity()
    {
        var identity = NormalizeSharedAlbumIdentity(new AlbumIdentity(
            ReleaseDate: "2024-01-12",
            AlbumId: "533077002",
            AlbumArtistId: "27",
            ReleaseGroupId: "17a64abf-7e23-4fd2-9b8d-42625a3a5976"));

        Assert.Null(identity.AlbumId);
        Assert.Null(identity.AlbumArtistId);
        Assert.Equal("17a64abf-7e23-4fd2-9b8d-42625a3a5976", identity.ReleaseGroupId);
    }

    [Fact]
    public void ProviderReleaseIdAliases_KeepWriteAndCleanupBoundariesSeparate()
    {
        var itunes = AutoTagIdentityTags.ResolveFamily("itunes", ProviderIdentityField.ReleaseId);
        Assert.Equal(["ITUNES_RELEASE_ID"], itunes.WriteNames);
        var appleCleanup = itunes.CleanupNames;
        Assert.Contains("ITUNES_RELEASE_ID", appleCleanup);
        Assert.Contains("APPLE_RELEASE_ID", appleCleanup);
        // An album id alias is not part of the release-id family.
        Assert.DoesNotContain("APPLE_ALBUM_ID", appleCleanup);
        Assert.DoesNotContain("ITUNESALBUMID", appleCleanup);
        Assert.DoesNotContain("ITUNES_ALBUM_ID", appleCleanup);

        Assert.Equal(
            ["DEEZER_RELEASE_ID"],
            AutoTagIdentityTags.ResolveFamily("deezer", ProviderIdentityField.ReleaseId).WriteNames);
        Assert.DoesNotContain(
            "ALBUMID",
            AutoTagIdentityTags.ResolveFamily("musicbrainz", ProviderIdentityField.ReleaseId).CleanupNames);
        Assert.DoesNotContain(
            "ALBUMID",
            AutoTagIdentityTags.ResolveFamily("musicbrainz", ProviderIdentityField.AlbumId).WriteNames.Concat(
                AutoTagIdentityTags.ResolveFamily("musicbrainz", ProviderIdentityField.AlbumId).CleanupNames));
    }
}
