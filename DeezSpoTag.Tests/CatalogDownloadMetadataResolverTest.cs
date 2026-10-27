using System;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class CatalogDownloadMetadataResolverTest
{
    [Theory]
    [InlineData("tidal")]
    [InlineData("apple")]
    [InlineData("amazon")]
    public async Task Resolver_DoesNotUseForeignOrMissingIdentities(string source)
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var resolver = new CatalogDownloadMetadataResolver(source, provider);
        var track = new Track { Source = "spotify", SourceId = "foreign-id", Title = "Keep" };
        await resolver.ResolveTrackAsync(track, new DeezSpoTagSettings(), CancellationToken.None);
        track.Source = source;
        track.SourceId = "";
        await resolver.ResolveTrackAsync(track, new DeezSpoTagSettings(), CancellationToken.None);
        Assert.Equal("Keep", track.Title);
    }

    [Theory]
    [InlineData("tidal")]
    [InlineData("apple")]
    [InlineData("amazon")]
    public void CatalogMetadata_KeepsKnownDatesWhenTheProviderOmitsThem(string source)
    {
        var track = new Track
        {
            Source = source, SourceId = "123", Title = "Before", Date = CustomDate.FromString("2022-04-22"),
            DateString = "2022-04-22", Album = new Album("Album")
        };
        typeof(CatalogDownloadMetadataResolver).GetMethod("ApplyCatalogTrack", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object?[] { track, "After", "Artist", "Album", "ISRC", 123, null, new DeezSpoTagSettings() });
        Assert.Equal("After", track.Title);
        Assert.Equal("Artist", track.MainArtist?.Name);
        Assert.Equal("2022-04-22", track.DateString);
        Assert.Equal(source, track.Source);
        Assert.Equal("123", track.SourceId);
    }

    [Theory]
    [InlineData("123", "2022-04-22", "After")]
    [InlineData("456", "2022-04-22", "Before")]
    public void AppleCatalog_MapsOnlyTheAcceptedSong(string returnedId, string date, string expectedTitle)
    {
        var track = new Track { Source = "apple", SourceId = "123", Title = "Before" };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            data = new[] { new { id = returnedId, attributes = new
            {
                name = "After", artistName = "Artist", albumName = "Album", isrc = "ISRC",
                durationInMillis = 123000, releaseDate = date
            } } }
        }));
        typeof(CatalogDownloadMetadataResolver).GetMethod("ApplyAppleSong", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { track, document.RootElement, new DeezSpoTagSettings() });
        Assert.Equal(expectedTitle, track.Title);
        if (returnedId == "123")
        {
            Assert.Equal("2022", track.Date.Year);
            Assert.Equal("2022-04-22", track.Album?.DateString);
            Assert.Equal("ISRC", track.ISRC);
        }
    }
}
