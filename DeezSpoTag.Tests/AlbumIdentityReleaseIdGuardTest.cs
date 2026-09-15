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
/// must never stamp its (platform-agnostic) shared AlbumId into a foreign platform's
/// release-id namespace, and per-platform release-id tags reject values from other
/// id families.
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

    private static AlbumIdentity BuildAlbumIdentityCandidate(AutoTagTrack track, string platformId)
    {
        var method = typeof(LocalAutoTagRunner).GetMethod(
            "BuildAlbumIdentityCandidate",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<AlbumIdentity>(method!.Invoke(null, [track, platformId]));
    }

    private static AlbumIdentity NormalizeSharedAlbumIdentity(AlbumIdentity identity)
    {
        var method = typeof(LocalAutoTagRunner).GetMethod(
            "NormalizeSharedAlbumIdentity",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<AlbumIdentity>(method!.Invoke(null, [identity]));
    }

    private static IReadOnlyList<string> ProviderReleaseIdRawNames(string methodName, string platformId)
    {
        var method = typeof(LocalAutoTagRunner).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<IReadOnlyList<string>>(method!.Invoke(null, [platformId]));
    }

    private static void SetOther(AutoTagTrack track, string key, string value)
        => track.Other[key] = new List<string> { value };

    [Fact]
    public void EstablishedIdentity_DoesNotStampForeignAlbumIdIntoPlatformReleaseId()
    {
        var track = new AutoTagTrack
        {
            Title = "Song",
            ReleaseId = "2flqcgHiEwy6XHlUuGe2ab"
        };
        var identity = new AlbumIdentity(
            ReleaseDate: "2026-03-27",
            AlbumId: "943733001",
            AlbumArtistId: null,
            PlatformReleaseIds: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["AUDIOMACK_RELEASE_ID"] = "943733001"
            });

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
        var identity = new AlbumIdentity(
            ReleaseDate: "2026-03-27",
            AlbumId: guid,
            AlbumArtistId: null,
            PlatformReleaseIds: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["MUSICBRAINZ_RELEASE_ID"] = guid
            },
            ConfirmedPlatformReleaseIdKeys: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "MUSICBRAINZ_RELEASE_ID"
            });

        ApplyEstablishedAlbumIdentity(track, identity, "musicbrainz");

        Assert.Equal(guid, track.ReleaseId);
        Assert.Equal(guid, track.AlbumId);
        Assert.Equal(guid, track.Other["MUSICBRAINZ_ALBUMID"].First());
        Assert.Equal(guid, track.Other["MUSICBRAINZ_RELEASE_ID"].First());
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
        var identity = new AlbumIdentity(
            ReleaseDate: "2026-03-27",
            AlbumId: "943733001",
            AlbumArtistId: null,
            PlatformReleaseIds: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["AUDIOMACK_RELEASE_ID"] = "943733001"
            },
            ConfirmedPlatformReleaseIdKeys: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "AUDIOMACK_RELEASE_ID"
            });

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
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var addMethod = typeof(LocalAutoTagRunner).GetMethod(
            "AddPlatformReleaseId",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(addMethod);
        addMethod!.Invoke(null, [values, "SPOTIFY_RELEASE_ID", "943733001"]);
        addMethod.Invoke(null, [values, "SPOTIFY_RELEASE_ID", "2flqcgHiEwy6XHlUuGe2ab"]);
        addMethod.Invoke(null, [values, "AUDIOMACK_RELEASE_ID", "943733001"]);
        addMethod.Invoke(null, [values, "MUSICBRAINZ_RELEASE_ID", "3f6b4d5a-9f61-4d5a-9f61-1b6e20e2b0f1"]);
        addMethod.Invoke(null, [values, "MUSICBRAINZ_RELEASE_ID", "943733001"]);

        Assert.Equal("2flqcgHiEwy6XHlUuGe2ab", values["SPOTIFY_RELEASE_ID"]);
        Assert.Equal("943733001", values["AUDIOMACK_RELEASE_ID"]);
        Assert.Equal("3f6b4d5a-9f61-4d5a-9f61-1b6e20e2b0f1", values["MUSICBRAINZ_RELEASE_ID"]);
        Assert.Equal(3, values.Count);
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

        var identity = BuildAlbumIdentityCandidate(track, "deezer");

        Assert.Null(identity.AlbumId);
        Assert.Equal("533077002", identity.PlatformReleaseIds!["DEEZER_RELEASE_ID"]);
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

        var identity = BuildAlbumIdentityCandidate(track, "shazam");

        Assert.Null(identity.AlbumId);
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
        Assert.Equal(
            new[] { "ITUNES_RELEASE_ID", "APPLE_ALBUM_ID" },
            ProviderReleaseIdRawNames("ProviderReleaseIdWriteRawNames", "itunes"));

        var appleCleanup = ProviderReleaseIdRawNames("ProviderReleaseIdCleanupRawNames", "itunes");
        Assert.Contains("ITUNES_RELEASE_ID", appleCleanup);
        Assert.Contains("APPLE_ALBUM_ID", appleCleanup);
        Assert.Contains("APPLE_RELEASE_ID", appleCleanup);
        Assert.Contains("APPLE_MUSIC_ALBUM_ID", appleCleanup);
        Assert.Contains("ITUNESALBUMID", appleCleanup);
        Assert.Contains("ITUNES_ALBUM_ID", appleCleanup);

        Assert.Equal(
            new[] { "DEEZER_RELEASE_ID" },
            ProviderReleaseIdRawNames("ProviderReleaseIdWriteRawNames", "deezer"));
        Assert.Equal(
            new[] { "SHAZAM_RELEASE_ID" },
            ProviderReleaseIdRawNames("ProviderReleaseIdCleanupRawNames", "shazam"));
    }
}
