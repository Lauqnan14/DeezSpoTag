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
            });

        ApplyEstablishedAlbumIdentity(track, identity, "musicbrainz");

        Assert.Equal(guid, track.ReleaseId);
        Assert.Equal(guid, track.AlbumId);
        Assert.Equal(guid, track.Other["MUSICBRAINZ_ALBUMID"].First());
        Assert.Equal(guid, track.Other["MUSICBRAINZ_RELEASE_ID"].First());
        Assert.Equal(guid, track.Other["ALBUMID"].First());
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
}