using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class BoomplayDurationParsingTests
{
    private static readonly MethodInfo ParseDurationMsMethod =
        typeof(BoomplayMetadataService).GetMethod(
            "ParseDurationMs",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BoomplayMetadataService.ParseDurationMs not found.");

    private static readonly MethodInfo TryApplySongDetailFieldMethod =
        typeof(BoomplayMetadataService).GetMethod(
            "TryApplySongDetailField",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BoomplayMetadataService.TryApplySongDetailField not found.");

    private static readonly MethodInfo ParseOfficialSongMetadataMethod =
        typeof(BoomplayMetadataService).GetMethod(
            "ParseOfficialSongMetadata",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BoomplayMetadataService.ParseOfficialSongMetadata not found.");

    private static readonly MethodInfo ParseOfficialPlaylistTracksMethod =
        typeof(BoomplayMetadataService).GetMethod(
            "ParseOfficialPlaylistTracks",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BoomplayMetadataService.ParseOfficialPlaylistTracks not found.");

    [Theory]
    [InlineData("3:45", 225000)]
    [InlineData("03:45", 225000)]
    [InlineData("1:02:03", 3723000)]
    [InlineData("PT3M45S", 225000)]
    [InlineData("225000", 225000)]
    public void ParseDurationMs_UsesCorrectUnits(string raw, int expectedMilliseconds)
    {
        var actual = Assert.IsType<int>(ParseDurationMsMethod.Invoke(null, new object?[] { raw }));

        Assert.Equal(expectedMilliseconds, actual);
    }

    [Theory]
    [InlineData("duration")]
    [InlineData("length")]
    [InlineData("track duration")]
    public void TryApplySongDetailField_AppliesDurationLabels(string label)
    {
        var track = new BoomplayTrackMetadata();

        TryApplySongDetailFieldMethod.Invoke(null, new object?[] { track, label, "3:45" });

        Assert.Equal(225000, track.DurationMs);
    }

    [Fact]
    public void ParseOfficialSongMetadata_UsesBoomplayAlbumObject()
    {
        using var document = JsonDocument.Parse("""
        {
          "musicID": 256487581,
          "name": "Take a look at you!",
          "deaution": "00:02:40",
          "cover": "group10/M00/06/24/cover.jpeg",
          "beArtist": { "name": "FUNMY" },
          "beAlbum": {
            "colID": 134311842,
            "name": "TALAY & BEAUTIFUL GIRL",
            "bigIconID": "group10/M00/06/24/album.jpeg"
          },
          "publicYear": 2026,
          "recordLabel": "DEFABS"
        }
        """);

        var track = Assert.IsType<BoomplayTrackMetadata>(
            ParseOfficialSongMetadataMethod.Invoke(null, new object?[] { document.RootElement }));

        Assert.Equal("256487581", track.Id);
        Assert.Equal("Take a look at you!", track.Title);
        Assert.Equal("FUNMY", track.Artist);
        Assert.Equal("TALAY & BEAUTIFUL GIRL", track.Album);
        Assert.Equal(160000, track.DurationMs);
        Assert.Equal("2026", track.ReleaseDate);
        Assert.Equal("DEFABS", track.Publisher);
    }

    [Fact]
    public void ParseOfficialPlaylistTracks_KeepsBoomplayAlbumCollectionId()
    {
        using var document = JsonDocument.Parse("""
        [
          {
            "musicID": 256487581,
            "colID": 134311842,
            "name": "Take a look at you!",
            "deaution": "00:02:40",
            "cover": "group10/M00/06/24/cover.jpeg",
            "seq": 1,
            "singers": [{ "name": "FUNMY" }]
          }
        ]
        """);

        var tracks = Assert.IsAssignableFrom<IReadOnlyList<BoomplayTrackMetadata>>(
            ParseOfficialPlaylistTracksMethod.Invoke(null, new object?[] { document.RootElement }));

        var track = Assert.Single(tracks);
        Assert.Equal("256487581", track.Id);
        Assert.Equal("134311842", track.AlbumId);
        Assert.Equal("Take a look at you!", track.Title);
        Assert.Equal("FUNMY", track.Artist);
    }
}
