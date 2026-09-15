using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Web.Services.AutoTag;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Album-scoped provider identity: every provider keeps its own album/release/album-artist
/// ids, version-2 documents migrate only their confirmed release ids, and only album-scoped
/// fields ever reconcile across the tracks of one edition.
/// </summary>
public sealed class ProviderAlbumIdentityReconciliationTest
{
    [Fact]
    public void Registry_KeepsAlbumAndReleaseIdsSeparatePerProvider()
    {
        var spotify = new ProviderAlbumIdentity("spotify-album", "spotify-release", "spotify-album-artist");
        var deezer = new ProviderAlbumIdentity("123", "456", "789");
        var identity = AlbumIdentity.Empty
            .WithProviderIdentity("spotify", spotify, overwrite: true)
            .WithProviderIdentity("deezer", deezer, overwrite: true);

        Assert.Equal(spotify, identity.ProviderIdentities!["spotify"]);
        Assert.Equal(deezer, identity.ProviderIdentities!["deezer"]);
    }

    [Fact]
    public void Registry_MergesWithoutOverwriteAndNeverDeletesWithNullSubfields()
    {
        var established = AlbumIdentity.Empty
            .WithProviderIdentity("itunes", new ProviderAlbumIdentity("album-1", "release-1", "artist-1"), overwrite: true);

        var merged = established.WithProviderIdentity(
            "itunes",
            new ProviderAlbumIdentity(null, "release-2", null),
            overwrite: false);

        var current = merged.GetProviderIdentity("itunes")!;
        Assert.Equal("album-1", current.AlbumId);
        Assert.Equal("release-1", current.ReleaseId);
        Assert.Equal("artist-1", current.AlbumArtistId);

        var overwritten = established.WithProviderIdentity(
            "itunes",
            new ProviderAlbumIdentity("album-2", "release-2", "artist-2"),
            overwrite: true);
        Assert.Equal("album-2", overwritten.GetProviderIdentity("itunes")!.AlbumId);
    }

    [Fact]
    public void AppleProviderIds_NormalizeOntoItunes()
    {
        var identity = AlbumIdentity.Empty
            .WithProviderIdentity("Apple Music", new ProviderAlbumIdentity(null, "release-1", null), overwrite: true);

        Assert.True(identity.ProviderIdentities!.ContainsKey("itunes"));
        Assert.Equal("release-1", identity.GetProviderIdentity("apple")!.ReleaseId);
    }

    [Fact]
    public void Version2Document_MigratesOnlyConfirmedReleaseIds()
    {
        var path = Path.Combine(Path.GetTempPath(), $"album-identity-v2-{Guid.NewGuid():N}.json");
        var document = new
        {
            version = 2,
            identities = new object[]
            {
                new
                {
                    key = "artist\u001falbum",
                    releaseDate = "2026-03-27",
                    albumId = "legacy-generic-album-id",
                    albumArtistId = "legacy-generic-artist-id",
                    updatedAt = DateTimeOffset.UtcNow,
                    platformReleaseIds = new Dictionary<string, string>
                    {
                        ["ITUNES_RELEASE_ID"] = "1446918509",
                        ["DEEZER_RELEASE_ID"] = "82411002",
                        ["SHAZAM_RELEASE_ID"] = "179363330"
                    },
                    confirmedPlatformReleaseIdKeys = new[] { "ITUNES_RELEASE_ID", "DEEZER_RELEASE_ID" }
                }
            }
        };

        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(document));
            var store = AlbumIdentityStore.Load(path);
            var identity = Assert.Single(store.Entries).Identity;

            var itunes = identity.GetProviderIdentity("itunes");
            var deezer = identity.GetProviderIdentity("deezer");
            Assert.NotNull(itunes);
            Assert.NotNull(deezer);
            Assert.Equal("1446918509", itunes!.ReleaseId);
            Assert.Equal("82411002", deezer!.ReleaseId);

            // Unconfirmed legacy values are not trusted.
            Assert.Null(identity.GetProviderIdentity("shazam"));

            // Legacy generic album ids never become a provider identity.
            Assert.All(
                identity.ProviderIdentities!.Values,
                value => Assert.Null(value.AlbumId));
            Assert.All(
                identity.ProviderIdentities!.Values,
                value => Assert.Null(value.AlbumArtistId));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Version3Document_RoundTripsProviderIdentitiesOnly()
    {
        var path = Path.Combine(Path.GetTempPath(), $"album-identity-v3-{Guid.NewGuid():N}.json");
        try
        {
            var store = new AlbumIdentityStore();
            store.Merge(
            [
                (
                    "artist\u001falbum",
                    new AlbumIdentity(
                        "2026-03-27",
                        null,
                        null,
                        ProviderIdentities: new Dictionary<string, ProviderAlbumIdentity>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["spotify"] = new ProviderAlbumIdentity("sp-album", "sp-release", null),
                            ["itunes"] = new ProviderAlbumIdentity(null, "1446918509", null)
                        }),
                    DateTimeOffset.UtcNow)
            ]);
            store.Save(path);

            var json = File.ReadAllText(path);
            Assert.Contains("\"version\": 3", json, StringComparison.Ordinal);
            Assert.Contains("\"providerIdentities\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("\"platformReleaseIds\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("\"confirmedPlatformReleaseIdKeys\"", json, StringComparison.Ordinal);

            var reloaded = AlbumIdentityStore.Load(path);
            var identity = Assert.Single(reloaded.Entries).Identity;
            Assert.Equal("sp-release", identity.GetProviderIdentity("spotify")!.ReleaseId);
            Assert.Equal("1446918509", identity.GetProviderIdentity("itunes")!.ReleaseId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void StandardAndDeluxeEditions_NeverShareProviderIdentity()
    {
        var registry = new AlbumIdentityRegistry();
        var standardKey = AlbumIdentity.BuildEditionAwareKey("Artist", "Album");
        var deluxeKey = AlbumIdentity.BuildEditionAwareKey("Artist", "Album (Deluxe Edition)");
        Assert.NotEqual(standardKey, deluxeKey);

        var standard = registry.Establish(
            standardKey,
            AlbumIdentity.Empty,
            providerId: "spotify",
            providerIdentity: new ProviderAlbumIdentity("standard-album", "standard-release", null),
            hasAuthoritativePlatformResult: true,
            overwriteAuthoritativeProviderIdentity: true);
        var deluxe = registry.Establish(
            deluxeKey,
            AlbumIdentity.Empty,
            providerId: "spotify",
            providerIdentity: new ProviderAlbumIdentity("deluxe-album", "deluxe-release", null),
            hasAuthoritativePlatformResult: true,
            overwriteAuthoritativeProviderIdentity: true);

        Assert.Equal("standard-release", standard.GetProviderIdentity("spotify")!.ReleaseId);
        Assert.Equal("deluxe-release", deluxe.GetProviderIdentity("spotify")!.ReleaseId);
        Assert.True(registry.TryGet(standardKey, out var reReadStandard));
        Assert.Equal("standard-release", reReadStandard.GetProviderIdentity("spotify")!.ReleaseId);
    }

    [Fact]
    public void AlbumScopedIdentity_PropagatesOnlyAlbumReleaseAndAlbumArtist()
    {
        var track = new AutoTagTrack { Title = "Song" };
        var identity = AlbumIdentity.Empty
            .WithProviderIdentity(
                "spotify",
                new ProviderAlbumIdentity("sp-album", "sp-release", "sp-album-artist"),
                overwrite: true);

        var apply = typeof(LocalAutoTagRunner).GetMethod(
            "ApplyEstablishedAlbumIdentity",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ApplyEstablishedAlbumIdentity not found.");
        apply.Invoke(null, [track, identity, "spotify"]);

        Assert.Equal("sp-release", track.ReleaseId);
        Assert.Equal("sp-release", track.Other["SPOTIFY_RELEASE_ID"].First());

        // Track-scoped identity is never part of an album identity.
        Assert.False(track.Other.ContainsKey("SPOTIFY_TRACK_ID"));
        Assert.Null(track.TrackId);
        Assert.False(track.Other.ContainsKey("SPOTIFY_ARTIST_ID"));
        Assert.Null(track.ArtistId);
        Assert.False(track.Other.ContainsKey("SPOTIFY_URL"));
        Assert.Null(track.Url);

        // Generic compatibility fields stay untouched by album reconciliation.
        foreach (var name in LocalAutoTagRunner.GenericIdentityCompatibilityFields)
        {
            Assert.False(track.Other.ContainsKey(name), name);
        }
    }

    [Fact]
    public void AlbumScopedIdentity_IsNotPropagatedToAnotherProvider()
    {
        var track = new AutoTagTrack { Title = "Song" };
        var identity = AlbumIdentity.Empty
            .WithProviderIdentity(
                "spotify",
                new ProviderAlbumIdentity("sp-album", "sp-release", null),
                overwrite: true);

        var apply = typeof(LocalAutoTagRunner).GetMethod(
            "ApplyEstablishedAlbumIdentity",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ApplyEstablishedAlbumIdentity not found.");
        apply.Invoke(null, [track, identity, "deezer"]);

        Assert.Null(track.ReleaseId);
        Assert.False(track.Other.ContainsKey("DEEZER_RELEASE_ID"));
    }

    [Fact]
    public void MusicBrainzAlbumIdentity_UsesOnlyItsOwnFamilyAliases()
    {
        const string albumId = "de02d778-2f56-4702-bb80-a93563a375f0";
        const string releaseId = "f67cd8b2-1ac6-4e21-8451-4d6a58eb0ee5";
        var track = new AutoTagTrack { Title = "Song" };
        var identity = AlbumIdentity.Empty
            .WithProviderIdentity(
                "musicbrainz",
                new ProviderAlbumIdentity(albumId, releaseId, albumId),
                overwrite: true);

        var apply = typeof(LocalAutoTagRunner).GetMethod(
            "ApplyEstablishedAlbumIdentity",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        apply.Invoke(null, [track, identity, "musicbrainz"]);

        Assert.Equal(albumId, track.Other["MUSICBRAINZ_ALBUMID"].First());
        Assert.Equal(releaseId, track.Other["MUSICBRAINZ_RELEASE_ID"].First());
        Assert.Equal(albumId, track.Other["MUSICBRAINZ_ALBUMARTISTID"].First());
        Assert.False(track.Other.ContainsKey("ALBUMID"));
        Assert.False(track.Other.ContainsKey("ALBUMARTISTID"));
        Assert.False(track.Other.ContainsKey("ARTISTID"));
        Assert.False(track.Other.ContainsKey("RECORDINGID"));
    }
}