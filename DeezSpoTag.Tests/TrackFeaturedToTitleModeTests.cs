using System.Collections.Generic;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class TrackFeaturedToTitleModeTests
{
    [Fact]
    public void ApplySettings_FeaturedToTitleZero_RemovesFeaturedFromTrackAndAlbumTitles()
    {
        var settings = new DeezSpoTagSettings
        {
            FeaturedToTitle = "0",
            Tags = new TagSettings
            {
                MultiArtistSeparator = "default"
            }
        };

        var track = new Track
        {
            Title = "Dah! (feat. Alikiba)",
            MainArtist = new Artist("Nandy"),
            Album = new Album("album-1", "Dah! (feat. Alikiba)"),
            Artist = new Dictionary<string, List<string>>
            {
                ["Main"] = new List<string> { "Nandy" }
            },
            Artists = new List<string> { "Nandy" }
        };

        track.ApplySettings(settings);

        Assert.Equal("Dah!", track.Title);
        Assert.Equal("Dah!", track.Album!.Title);
    }
}
