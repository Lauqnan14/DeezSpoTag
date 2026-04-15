using System.Collections.Generic;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Services.Utils;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class TrackIdNormalizationTests
{
    [Theory]
    [InlineData("12345", "12345")]
    [InlineData(" 00123 ", "123")]
    [InlineData("deezer:track:3135556", "3135556")]
    [InlineData("https://www.deezer.com/track/3135556", "3135556")]
    [InlineData("https://dzr.page.link/3135556", "3135556")]
    public void TryNormalizeDeezerTrackId_ReturnsNormalizedId_WhenInputIsValid(string input, string expected)
    {
        var ok = TrackIdNormalization.TryNormalizeDeezerTrackId(input, out var normalized);

        Assert.True(ok);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("abc")]
    [InlineData("https://example.com/track/3135556")]
    public void TryNormalizeDeezerTrackId_ReturnsFalse_WhenInputIsInvalid(string? input)
    {
        var ok = TrackIdNormalization.TryNormalizeDeezerTrackId(input, out var normalized);

        Assert.False(ok);
        Assert.Null(normalized);
    }

    [Fact]
    public void TryResolveDeezerTrackId_PrefersExplicitTrackId()
    {
        var track = new Track
        {
            Source = "deezer",
            SourceId = "555"
        };

        var ok = TrackIdNormalization.TryResolveDeezerTrackId(track, out var resolved, explicitTrackId: "777");

        Assert.True(ok);
        Assert.Equal("777", resolved);
    }

    [Fact]
    public void TryResolveDeezerTrackId_ResolvesFromUrls_WhenPresent()
    {
        var track = new Track
        {
            Urls = new Dictionary<string, string>
            {
                ["deezer_track_id"] = "888",
                ["source_url"] = "https://www.deezer.com/track/999"
            }
        };

        var ok = TrackIdNormalization.TryResolveDeezerTrackId(track, out var resolved);

        Assert.True(ok);
        Assert.Equal("888", resolved);
    }

    [Fact]
    public void TryResolveDeezerTrackId_FallsBackToDownloadUrl_WhenNeeded()
    {
        var track = new Track
        {
            DownloadURL = "https://www.deezer.com/track/1234567"
        };

        var ok = TrackIdNormalization.TryResolveDeezerTrackId(track, out var resolved);

        Assert.True(ok);
        Assert.Equal("1234567", resolved);
    }

    [Theory]
    [InlineData("3n3Ppam7vgaVa1iaRUc9Lp")]
    [InlineData("spotify:track:3n3Ppam7vgaVa1iaRUc9Lp")]
    [InlineData("https://open.spotify.com/track/3n3Ppam7vgaVa1iaRUc9Lp?si=abc")]
    public void TryNormalizeSpotifyTrackId_ReturnsId_WhenInputIsValid(string input)
    {
        var ok = TrackIdNormalization.TryNormalizeSpotifyTrackId(input, out var normalized);

        Assert.True(ok);
        Assert.Equal("3n3Ppam7vgaVa1iaRUc9Lp", normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("spotify:track:short")]
    [InlineData("https://example.com/track/3n3Ppam7vgaVa1iaRUc9Lp")]
    public void TryNormalizeSpotifyTrackId_ReturnsFalse_WhenInputIsInvalid(string? input)
    {
        var ok = TrackIdNormalization.TryNormalizeSpotifyTrackId(input, out var normalized);

        Assert.False(ok);
        Assert.Null(normalized);
    }

    [Fact]
    public void TryResolveSpotifyTrackId_ResolvesFromSourceId_First()
    {
        var track = new Track
        {
            SourceId = "3n3Ppam7vgaVa1iaRUc9Lp",
            Source = "spotify",
            Id = "4iV5W9uYEdYUVa79Axb7Rh"
        };

        var ok = TrackIdNormalization.TryResolveSpotifyTrackId(track, out var resolved);

        Assert.True(ok);
        Assert.Equal("3n3Ppam7vgaVa1iaRUc9Lp", resolved);
    }

    [Fact]
    public void TryResolveSpotifyTrackId_ResolvesFromSourceAndId_WhenSourceIdIsMissing()
    {
        var track = new Track
        {
            Source = "spotify",
            Id = "4iV5W9uYEdYUVa79Axb7Rh"
        };

        var ok = TrackIdNormalization.TryResolveSpotifyTrackId(track, out var resolved);

        Assert.True(ok);
        Assert.Equal("4iV5W9uYEdYUVa79Axb7Rh", resolved);
    }

    [Fact]
    public void TryResolveSpotifyTrackId_ResolvesFromUrls_WhenPresent()
    {
        var track = new Track
        {
            Urls = new Dictionary<string, string>
            {
                ["spotify_url"] = "https://open.spotify.com/track/3n3Ppam7vgaVa1iaRUc9Lp"
            }
        };

        var ok = TrackIdNormalization.TryResolveSpotifyTrackId(track, out var resolved);

        Assert.True(ok);
        Assert.Equal("3n3Ppam7vgaVa1iaRUc9Lp", resolved);
    }
}
