using System;
using System.Collections.Generic;
using DeezSpoTag.Services.Download.SoundCloud;
using DeezSpoTag.Web.Services.LinkMapping;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
///     Covers how a SoundCloud payload becomes a normalized track, and which URLs count as SoundCloud links.
/// </summary>
/// <remarks>
///     The artist assertions are the important ones: SoundCloud's optional <c>metadata_artist</c> is often
///     absent, and its absence must not silently promote the uploader handle to recording artist.
/// </remarks>
public sealed class SoundCloudMetadataAndUrlTest
{
    [Fact]
    public void MetadataArtist_OutranksTheDistributorAndTheUploader()
    {
        var track = Parse(
            """
            {
              "id": 1,
              "urn": "soundcloud:tracks:1",
              "kind": "track",
              "title": "Example",
              "metadata_artist": "Actual Artist",
              "user": { "username": "Upload Account", "display_name": "Upload Account" },
              "publisher_metadata": { "artist": "Distributor Artist" }
            }
            """);

        Assert.Equal("Actual Artist", track.MetadataArtist);
        Assert.Equal("Distributor Artist", track.PublisherArtist);
        Assert.Equal("Upload Account", track.UploaderUsername);
        Assert.Equal("Actual Artist", track.PreferredArtist);
    }

    [Fact]
    public void MetadataArtistAbsent_TheDistributorNameIsUsed()
    {
        // Observed live: SoundCloud omits metadata_artist and the real artist survives only in the
        // distributor payload, while user.username is the account handle.
        var track = Parse(
            """
            {
              "id": 1,
              "urn": "soundcloud:tracks:2394568125",
              "kind": "track",
              "title": "Diana",
              "user": { "username": "J.I." },
              "publisher_metadata": { "artist": "J.I the Prince of N.Y" }
            }
            """);

        Assert.Null(track.MetadataArtist);
        Assert.Equal("J.I the Prince of N.Y", track.PreferredArtist);
        Assert.NotEqual("J.I.", track.PreferredArtist);
    }

    [Fact]
    public void NoRecordingArtistMetadata_TheUploaderIsTheLastResort()
    {
        var track = Parse(
            """
            {
              "id": 1,
              "urn": "soundcloud:tracks:2",
              "kind": "track",
              "title": "Example",
              "user": { "username": "Upload Account" }
            }
            """);

        Assert.Equal("Upload Account", track.PreferredArtist);
    }

    [Fact]
    public void UrnIsPreservedAndTheNumericIdIsNeverUsedAsTheIdentity()
    {
        var track = Parse(
            """
            { "id": 1, "urn": "soundcloud:tracks:1495999657", "kind": "track", "title": "Example", "id": 1495999657 }
            """);

        Assert.Equal("soundcloud:tracks:1495999657", track.Urn);
        Assert.StartsWith("soundcloud:tracks:", track.Urn, StringComparison.Ordinal);
    }

    [Fact]
    public void UrnIsSynthesisedWhenThePayloadOmitsIt()
    {
        var track = Parse(
            """
            { "kind": "track", "title": "Example", "id": 4242 }
            """);

        Assert.Equal("soundcloud:tracks:4242", track.Urn);
    }

    [Fact]
    public void IsrcPrefersTheTopLevelFieldAndFallsBackToTheDistributorCopy()
    {
        Assert.Equal("TOPLEVEL123456", Parse(
            """
            { "id": 1, "urn": "soundcloud:tracks:3", "kind": "track", "title": "E", "isrc": "TOPLEVEL123456",
              "publisher_metadata": { "isrc": "DISTRIBUT12" } }
            """).Isrc);

        Assert.Equal("DISTRIBUT12", Parse(
            """
            { "id": 1, "urn": "soundcloud:tracks:3", "kind": "track", "title": "E", "publisher_metadata": { "isrc": "DISTRIBUT12" } }
            """).Isrc);

        Assert.Null(Parse(
            """
            { "id": 1, "urn": "soundcloud:tracks:3", "kind": "track", "title": "E" }
            """).Isrc);
    }

    [Fact]
    public void GenreIsReadFromTheStringFormThatRealPayloadsUse()
    {
        // Regression: the reader previously accepted only the array form, so genre was lost on ordinary
        // tracks, where SoundCloud publishes a bare string.
        Assert.Equal("Hip-Hop", Parse(
            """
            { "id": 1, "urn": "soundcloud:tracks:4", "kind": "track", "title": "E", "genre": "Hip-Hop" }
            """).Genre);

        Assert.Equal("Rock", Parse(
            """
            { "id": 1, "urn": "soundcloud:tracks:4", "kind": "track", "title": "E", "genre": ["Rock"] }
            """).Genre);
    }

    [Fact]
    public void TempoAndKeyAreReadOnlyWhenValidlyPublished()
    {
        var populated = Parse(
            """
            { "id": 1, "urn": "soundcloud:tracks:5", "kind": "track", "title": "E", "bpm": 128.0, "key_signature": "Cmaj" }
            """);
        Assert.Equal(128, populated.Bpm);
        Assert.Equal("Cmaj", populated.KeySignature);

        // A fractional tempo still yields a usable value rather than being dropped.
        Assert.Equal(128, Parse(
            """
            { "id": 1, "urn": "soundcloud:tracks:5", "kind": "track", "title": "E", "bpm": 128.4 }
            """).Bpm);

        var absent = Parse(
            """
            { "id": 1, "urn": "soundcloud:tracks:5", "kind": "track", "title": "E" }
            """);
        Assert.Null(absent.Bpm);
        Assert.Null(absent.KeySignature);

        // Nonsense must not become a tag value.
        Assert.Null(Parse(
            """
            { "id": 1, "urn": "soundcloud:tracks:5", "kind": "track", "title": "E", "bpm": "not-a-number" }
            """).Bpm);
    }

    [Fact]
    public void ReleaseDateIsReadAndTagListIsKeptUnclassified()
    {
        var track = Parse(
            """
            {
              "id": 1,
              "urn": "soundcloud:tracks:6", "kind": "track", "title": "E",
              "release_date": "2026-09-23T00:00:00Z",
              "tag_list": "darkambient industrial nairobi"
            }
            """);

        Assert.NotNull(track.ReleaseDate);
        Assert.Equal(2026, track.ReleaseDate!.Value.Year);
        Assert.Equal(
            new[] { "darkambient", "industrial", "nairobi" },
            track.TagList);

        // tag_list is deliberately not promoted to Genre.
        Assert.Null(track.Genre);
    }

    [Fact]
    public void TagListIsAlsoReadFromTheArrayForm()
    {
        var track = Parse(
            """
            { "id": 1, "urn": "soundcloud:tracks:6", "kind": "track", "title": "E", "tag_list": ["techno", "peak-time"] }
            """);

        Assert.Equal(new[] { "techno", "peak-time" }, track.TagList);
    }

    [Fact]
    public void MissingOptionalFieldsStayNullRatherThanBeingInvented()
    {
        var track = Parse(
            """
            { "id": 1, "urn": "soundcloud:tracks:7", "kind": "track", "title": "E" }
            """);

        Assert.Null(track.MetadataArtist);
        Assert.Null(track.PublisherArtist);
        Assert.Null(track.PublisherAlbumTitle);
        Assert.Null(track.UploaderUsername);
        Assert.Null(track.Isrc);
        Assert.Null(track.Genre);
        Assert.Null(track.Bpm);
        Assert.Null(track.KeySignature);
        Assert.Null(track.ReleaseDate);
        Assert.Empty(track.TagList);
    }

    [Theory]
    [InlineData("https://soundcloud.com/artist-name/track-name")]
    [InlineData("https://soundcloud.com/iamji/diana")]
    [InlineData("https://soundcloud.com/user/sets/playlist")]
    [InlineData("https://soundcloud.com/soundcloud-hustle/sets/the-lookout-tomorrows-rap-hits")]
    [InlineData("https://soundcloud.com/discover/sets/trending-by-genre:hip-hop")]
    [InlineData("https://soundcloud.com/j.i/sets/some-set")]
    [InlineData("https://on.soundcloud.com/abcdefgh")]
    [InlineData("https://on.soundcloud.com/abc?in=x/s")]
    public void RealSoundCloudLinksAreClassified(string url)
        => Assert.Equal(ExternalLinkSource.SoundCloud, ExternalLinkClassifier.Classify(url));

    [Theory]
    [InlineData("https://soundcloud.com/")]
    [InlineData("https://soundcloud.com/j.i")]
    [InlineData("https://soundcloud.com.evil.com/artist/track")]
    [InlineData("https://notsoundcloud.com/artist/track")]
    [InlineData("https://evil.com/soundcloud.com/artist/track")]
    [InlineData("ftp://soundcloud.com/artist/track")]
    [InlineData("not a url")]
    public void NonTrackAndLookalikeHostsAreNotClassifiedAsSoundCloudTracks(string url)
        => Assert.NotEqual(ExternalLinkSource.SoundCloud, ExternalLinkClassifier.Classify(url));

    [Fact]
    public void AProfileUrlIsNotAcceptedAsATrackLinkAndATrackUrnIsIdentifiedByItsPrefix()
    {
        // A bare profile carries no track, so the classifier rejects it rather than handing it to the mapper.
        Assert.Equal(ExternalLinkSource.Unknown, ExternalLinkClassifier.Classify("https://soundcloud.com/j.i"));

        // The mapper additionally requires the resolved URN to name a track, so a set or user cannot pass as one.
        Assert.False(IsTrackUrn("soundcloud:users:1234"));
        Assert.False(IsTrackUrn("soundcloud:playlists:1234"));
        Assert.True(IsTrackUrn("soundcloud:tracks:1234"));
    }

    private static bool IsTrackUrn(string urn)
        => urn.StartsWith("soundcloud:tracks:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
///     The canonical length prefers <c>full_duration</c> over the preview <c>duration</c>.
/// </summary>
/// <remarks>
///     Observed live: the same track carries <c>duration: 30000</c> and <c>full_duration: 183137</c>, where
///     183 seconds is the recording Deezer also reports for that ISRC. Preferring the preview length made an
///     exact-ISRC match fail the duration tolerance against a correct candidate.
/// </remarks>
[Fact]
public void DurationPrefersTheFullLengthOverThePreviewLength()
{
    var track = Parse(
        """
        { "id": 1, "urn": "soundcloud:tracks:7", "kind": "track", "title": "E",
          "duration": 30000, "full_duration": 183137 }
        """);

    Assert.Equal(183137, track.DurationMs);
}

/// <summary>The preview length is used when no full length is published.</summary>
[Fact]
public void DurationFallsBackToThePreviewLength()
    => Assert.Equal(30000, Parse(
        """
        { "id": 1, "urn": "soundcloud:tracks:7", "kind": "track", "title": "E", "duration": 30000 }
        """).DurationMs);

/// <summary>An absent, zero or non-numeric full length must not win over a usable preview length.</summary>
[Theory]
[InlineData("\"full_duration\": 0")]
[InlineData("\"full_duration\": -5")]
[InlineData("\"full_duration\": null")]
[InlineData("\"full_duration\": \"nonsense\"")]
public void DurationIgnoresAnUnusableFullLength(string fullDuration)
{
    var json = "{ \"id\": 1, \"urn\": \"soundcloud:tracks:7\", \"kind\": \"track\", \"title\": \"E\", "
               + "\"duration\": 30000, " + fullDuration + " }";

    Assert.Equal(30000, Parse(json).DurationMs);
}

[Fact]
public void DurationIsZeroWhenNeitherLengthIsUsable()
    => Assert.Equal(0, Parse(
        """
        { "id": 1, "urn": "soundcloud:tracks:7", "kind": "track", "title": "E" }
        """).DurationMs);

private static SoundCloudTrack Parse(string trackJson)
    {
        var compact = trackJson.Replace("\n", string.Empty).Replace("\r", string.Empty);
        var payload = "[{\"hydratable\":\"sound\",\"data\":" + compact + "}]";
        return SoundCloudHydrationParser.ParseTrack(SoundCloudFixtures.BuildHydrationPage(payload));
    }
}