using System;
using System.Collections.Generic;
using System.Linq;
using DeezSpoTag.Web.Services.AutoTag;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Provider-and-field separation contract: every provider owns its own namespace for
/// each identity field, cleanup aliases stay inside one provider+field family, and no
/// family ever contains a generic compatibility field.
/// </summary>
public sealed class ProviderIdentityContractTest
{
    private static readonly string[] GenericCompatibilityFields =
        ["ALBUMID", "ARTISTID", "ALBUMARTISTID", "RECORDINGID", "URL", "WWWAUDIOFILE"];

    [Theory]
    [InlineData("spotify", ProviderIdentityField.TrackId, "SPOTIFY_TRACK_ID")]
    [InlineData("spotify", ProviderIdentityField.AlbumId, "SPOTIFY_ALBUM_ID")]
    [InlineData("spotify", ProviderIdentityField.ReleaseId, "SPOTIFY_RELEASE_ID")]
    [InlineData("spotify", ProviderIdentityField.ArtistId, "SPOTIFY_ARTIST_ID")]
    [InlineData("spotify", ProviderIdentityField.AlbumArtistId, "SPOTIFY_ALBUM_ARTIST_ID")]
    [InlineData("spotify", ProviderIdentityField.Url, "SPOTIFY_URL")]
    public void ResolveFamily_ReturnsFieldSpecificProviderNamespace(
        string provider,
        ProviderIdentityField field,
        string expectedCanonical)
    {
        var family = AutoTagIdentityTags.ResolveFamily(provider, field);
        Assert.Contains(expectedCanonical, family.WriteNames);
        Assert.All(family.WriteNames, name => Assert.StartsWith("SPOTIFY_", name));
    }

    [Fact]
    public void ResolveFamily_NeverIncludesGenericCompatibilityFields()
    {
        var forbidden = new[] { "ALBUMID", "ARTISTID", "ALBUMARTISTID", "RECORDINGID", "URL", "WWWAUDIOFILE" };
        var allNames = AutoTagIdentityTags.KnownProviders
            .SelectMany(provider => Enum.GetValues<ProviderIdentityField>()
                .SelectMany(field => AutoTagIdentityTags.ResolveFamily(provider, field).CleanupNames));
        Assert.DoesNotContain(allNames, forbidden.Contains);
    }

    [Fact]
    public void ResolveFamily_MusicBrainzKeepsAlbumAndReleaseSeparate()
    {
        var album = AutoTagIdentityTags.ResolveFamily("musicbrainz", ProviderIdentityField.AlbumId);
        var release = AutoTagIdentityTags.ResolveFamily("musicbrainz", ProviderIdentityField.ReleaseId);

        Assert.Contains("MUSICBRAINZ_ALBUMID", album.WriteNames);
        Assert.DoesNotContain("MUSICBRAINZ_ALBUMID", release.WriteNames);
        Assert.Contains("MUSICBRAINZ_RELEASE_ID", release.WriteNames);
        Assert.DoesNotContain("MUSICBRAINZ_RELEASE_ID", album.WriteNames);

        Assert.DoesNotContain("ALBUMID", album.WriteNames);
        Assert.DoesNotContain("ALBUMID", album.CleanupNames);
        Assert.DoesNotContain("ALBUMID", release.WriteNames);
        Assert.DoesNotContain("ALBUMID", release.CleanupNames);
    }

    [Fact]
    public void ResolveFamily_AppleAlbumAndReleaseFamiliesSplitTheLegacyAliases()
    {
        var album = AutoTagIdentityTags.ResolveFamily("itunes", ProviderIdentityField.AlbumId);
        var release = AutoTagIdentityTags.ResolveFamily("itunes", ProviderIdentityField.ReleaseId);

        Assert.Contains("APPLE_ALBUM_ID", album.CleanupNames);
        Assert.Contains("APPLE_MUSIC_ALBUM_ID", album.CleanupNames);
        Assert.Contains("ITUNESALBUMID", album.CleanupNames);
        Assert.Contains("ITUNES_ALBUM_ID", album.CleanupNames);
        Assert.DoesNotContain("APPLE_RELEASE_ID", album.CleanupNames);
        Assert.DoesNotContain("ITUNES_RELEASE_ID", album.CleanupNames);

        Assert.Contains("APPLE_RELEASE_ID", release.CleanupNames);
        Assert.Contains("ITUNES_RELEASE_ID", release.CleanupNames);
        Assert.DoesNotContain("APPLE_ALBUM_ID", release.CleanupNames);
        Assert.DoesNotContain("ITUNES_ALBUM_ID", release.CleanupNames);
    }

    [Fact]
    public void ResolveFamily_AppleAndItunesNormalizeToOneProvider()
    {
        foreach (var field in Enum.GetValues<ProviderIdentityField>())
        {
            var apple = AutoTagIdentityTags.ResolveFamily("apple", field);
            var itunes = AutoTagIdentityTags.ResolveFamily("itunes", field);
            Assert.Equal(itunes.WriteNames, apple.WriteNames);
            Assert.Equal(itunes.CleanupNames, apple.CleanupNames);
            Assert.Equal(itunes.SupportedTag, apple.SupportedTag);
        }
    }

    [Fact]
    public void NormalizeProviderId_IsCaseInsensitiveAndTrims()
    {
        Assert.Equal("spotify", AutoTagIdentityTags.NormalizeProviderId("  SPOTIFY "));
        Assert.Equal("musicbrainz", AutoTagIdentityTags.NormalizeProviderId("MusicBrainz"));
        Assert.Equal("itunes", AutoTagIdentityTags.NormalizeProviderId("APPLE"));
        Assert.Equal("itunes", AutoTagIdentityTags.NormalizeProviderId("itunes"));
        Assert.Equal("", AutoTagIdentityTags.NormalizeProviderId("   "));
    }

    [Fact]
    public void ResolveFamily_WriteNamesAreAlwaysPartOfTheCleanupFamily()
    {
        foreach (var provider in AutoTagIdentityTags.KnownProviders)
        {
            foreach (var field in Enum.GetValues<ProviderIdentityField>())
            {
                var family = AutoTagIdentityTags.ResolveFamily(provider, field);
                Assert.All(
                    family.WriteNames,
                    name => Assert.Contains(name, family.CleanupNames));
            }
        }
    }

    [Fact]
    public void ResolveFamily_NoCleanupAliasIsSharedAcrossFamilies()
    {
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in AutoTagIdentityTags.KnownProviders)
        {
            foreach (var field in Enum.GetValues<ProviderIdentityField>())
            {
                foreach (var name in AutoTagIdentityTags.ResolveFamily(provider, field).CleanupNames)
                {
                    var owner = $"{provider}/{field}";
                    Assert.False(
                        owners.TryGetValue(name, out var existing) && !string.Equals(existing, owner, StringComparison.Ordinal),
                        $"{name} is claimed by both {owners.GetValueOrDefault(name)} and {owner}");
                    owners[name] = owner;
                }
            }
        }
    }

    [Fact]
    public void KnownProviders_CoverEveryRegisteredMetadataProvider()
    {
        string[] expected =
        [
            "musicbrainz", "beatport", "discogs", "traxsource", "bandcamp", "itunes",
            "spotify", "deezer", "boomplay", "audiomack", "shazam", "qobuz", "tidal", "amazon"
        ];

        foreach (var provider in expected)
        {
            Assert.Contains(provider, AutoTagIdentityTags.KnownProviders);
        }

        Assert.DoesNotContain("lyrics", AutoTagIdentityTags.KnownProviders);
    }

    [Fact]
    public void ResolveFamily_UnknownProviderStillGetsItsOwnNamespace()
    {
        var family = AutoTagIdentityTags.ResolveFamily("customplatform", ProviderIdentityField.Url);
        Assert.Contains("CUSTOMPLATFORM_URL", family.WriteNames);
        Assert.Equal(SupportedTag.URL, family.SupportedTag);
    }

    [Fact]
    public void ResolveFamily_SupportedTagMatchesTheIdentityField()
    {
        Assert.Equal(SupportedTag.TrackId, AutoTagIdentityTags.ResolveFamily("deezer", ProviderIdentityField.TrackId).SupportedTag);
        Assert.Equal(SupportedTag.AlbumId, AutoTagIdentityTags.ResolveFamily("deezer", ProviderIdentityField.AlbumId).SupportedTag);
        Assert.Equal(SupportedTag.ReleaseId, AutoTagIdentityTags.ResolveFamily("deezer", ProviderIdentityField.ReleaseId).SupportedTag);
        Assert.Equal(SupportedTag.ArtistId, AutoTagIdentityTags.ResolveFamily("deezer", ProviderIdentityField.ArtistId).SupportedTag);
        Assert.Equal(SupportedTag.AlbumArtistId, AutoTagIdentityTags.ResolveFamily("deezer", ProviderIdentityField.AlbumArtistId).SupportedTag);
        Assert.Equal(SupportedTag.URL, AutoTagIdentityTags.ResolveFamily("deezer", ProviderIdentityField.Url).SupportedTag);
    }

    [Fact]
    public void Payload_ValueForReturnsTheCapturedField()
    {
        var payload = new ProviderIdentityPayload(
            "spotify",
            "track",
            "album",
            "release",
            "artist",
            "album-artist",
            "https://open.spotify.com/track/track",
            true);

        Assert.Equal("track", payload.ValueFor(ProviderIdentityField.TrackId));
        Assert.Equal("album", payload.ValueFor(ProviderIdentityField.AlbumId));
        Assert.Equal("release", payload.ValueFor(ProviderIdentityField.ReleaseId));
        Assert.Equal("artist", payload.ValueFor(ProviderIdentityField.ArtistId));
        Assert.Equal("album-artist", payload.ValueFor(ProviderIdentityField.AlbumArtistId));
        Assert.Equal("https://open.spotify.com/track/track", payload.ValueFor(ProviderIdentityField.Url));
        Assert.True(payload.IsNativeProviderResult);
    }

    [Fact]
    public void GenericCompatibilityFields_AreNeverProviderOwned()
    {
        foreach (var provider in AutoTagIdentityTags.KnownProviders)
        {
            foreach (var field in Enum.GetValues<ProviderIdentityField>())
            {
                var family = AutoTagIdentityTags.ResolveFamily(provider, field);
                Assert.DoesNotContain(family.WriteNames, GenericCompatibilityFields.Contains);
                Assert.DoesNotContain(family.CleanupNames, GenericCompatibilityFields.Contains);
            }
        }
    }
}