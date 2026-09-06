using System;
using System.Collections.Generic;
using System.IO;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Utils;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Folder/album awareness: files of one album must converge onto identical album
/// identity tags across sessions, while a standard album and a deluxe edition stay
/// separate. Covers the edition-aware album title helpers, the edition-aware
/// consensus key, the registry, and the cross-run store.
/// </summary>
public sealed class AlbumAwarenessTests
{
    [Theory]
    [InlineData("Album (Deluxe Edition)", "album")]
    [InlineData("Album (Deluxe)", "album")]
    [InlineData("Album - Deluxe", "album")]
    [InlineData("Album [2011 Remaster]", "album")]
    [InlineData("Album", "album")]
    [InlineData("ALBUM  ", "album")]
    public void CoreTitle_StripsEditionSections(string title, string expected)
    {
        Assert.Equal(expected, AlbumTitleNormalizer.CoreTitle(title));
    }

    [Fact]
    public void CoreTitle_KeepsNonEditionSubtitles()
    {
        Assert.Equal("album part 2", AlbumTitleNormalizer.CoreTitle("Album - Part 2"));
        Assert.Equal("album live from nowhere", AlbumTitleNormalizer.CoreTitle("Album (Live from Nowhere)"));
    }

    [Fact]
    public void EditionIntent_DetectsEditions()
    {
        Assert.Empty(AlbumTitleNormalizer.EditionIntent("Album"));
        Assert.Contains("deluxe", AlbumTitleNormalizer.EditionIntent("Album (Deluxe Edition)"));
        Assert.Contains("remaster", AlbumTitleNormalizer.EditionIntent("Album [2011 Remaster]"));
    }

    [Fact]
    public void IsSameEdition_MatchesDifferentWordingOfTheSameEdition()
    {
        Assert.True(AlbumTitleNormalizer.IsSameEdition("Album (Deluxe)", "Album (Deluxe Edition)"));
        Assert.False(AlbumTitleNormalizer.IsSameEdition("Album (Deluxe)", "Album"));
        Assert.False(AlbumTitleNormalizer.IsSameEdition("Album", "Album (Deluxe)"));
        Assert.False(AlbumTitleNormalizer.IsSameEdition("Album One", "Album Two"));
    }

    [Fact]
    public void IsEditionConflict_DetectsSameAlbumDifferentEdition()
    {
        Assert.True(AlbumTitleNormalizer.IsEditionConflict("Album", "Album (Deluxe Edition)"));
        Assert.True(AlbumTitleNormalizer.IsEditionConflict("Album (Remastered)", "Album"));
        // Different albums entirely are not edition conflicts.
        Assert.False(AlbumTitleNormalizer.IsEditionConflict("Album One", "Album Two (Deluxe)"));
    }

    [Fact]
    public void BuildEditionAwareKey_ConvergesEditionWordingButNotEditions()
    {
        var withEdition = AlbumIdentity.BuildEditionAwareKey("artist", "Album (Deluxe)");
        var otherWording = AlbumIdentity.BuildEditionAwareKey("artist", "Album (Deluxe Edition)");
        var standard = AlbumIdentity.BuildEditionAwareKey("artist", "Album");
        var otherArtist = AlbumIdentity.BuildEditionAwareKey("other artist", "Album (Deluxe)");

        Assert.Equal(withEdition, otherWording);
        Assert.NotEqual(standard, withEdition);
        Assert.NotEqual(otherArtist, withEdition);
        Assert.Null(AlbumIdentity.BuildEditionAwareKey("artist", ""));
    }

    [Fact]
    public void Registry_EstablishCoalescesAndTracksDirtyState()
    {
        var registry = new AlbumIdentityRegistry();
        Assert.False(registry.IsDirty);

        var first = registry.Establish("key", new AlbumIdentity(null, "mbid-1", null));
        Assert.Equal("mbid-1", first.AlbumId);
        Assert.True(registry.IsDirty);

        // Later candidates only fill gaps; they never overwrite established values.
        var second = registry.Establish("key", new AlbumIdentity("2024-01-01", "mbid-2", "artist-2"));
        Assert.Equal("mbid-1", second.AlbumId);
        Assert.Equal("artist-2", second.AlbumArtistId);
        Assert.Equal("2024-01-01", second.ReleaseDate);
    }

    [Fact]
    public void Registry_SeedPrePopulatesWithoutDirtyingAndCoalesces()
    {
        var registry = new AlbumIdentityRegistry();
        registry.Seed("key", new AlbumIdentity("2024-01-01", "mbid-1", null), DateTimeOffset.UtcNow);
        Assert.False(registry.IsDirty);

        var established = registry.Establish("key", new AlbumIdentity(null, "mbid-2", "artist-2"));
        Assert.Equal("2024-01-01", established.ReleaseDate);
        Assert.Equal("mbid-1", established.AlbumId);
    }

    [Fact]
    public void Store_RoundTripsAndMergesSnapshots()
    {
        var path = Path.Combine(Path.GetTempPath(), $"album-identities-{Guid.NewGuid():N}.json");
        try
        {
            var store = new AlbumIdentityStore();
            store.Merge(new[]
            {
                ("key-a", new AlbumIdentity("2024-01-01", "mbid-a", "artist-a"), DateTimeOffset.UtcNow),
                ("key-b", new AlbumIdentity(null, "mbid-b", null), DateTimeOffset.UtcNow),
            });
            store.Save(path);

            var reloaded = AlbumIdentityStore.Load(path);
            Assert.Equal(2, reloaded.Entries.Count);
            Assert.Contains(reloaded.Entries, entry => entry.Key == "key-a" && entry.Identity.AlbumId == "mbid-a");

            // A later snapshot fills gaps but never overwrites established values.
            reloaded.Merge(new[]
            {
                ("key-a", new AlbumIdentity("1999-01-01", "mbid-overwrite", null), DateTimeOffset.UtcNow.AddMinutes(1)),
            });
            var merged = Assert.Single(reloaded.Entries, entry => entry.Key == "key-a");
            Assert.Equal("mbid-a", merged.Identity.AlbumId);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    // --- artist ordering key ---------------------------------------------------------

    [Theory]
    [InlineData(new[] { "21 Savage", "Drake" }, "21 savage")]
    [InlineData(new[] { "Drake", "21 Savage" }, "21 savage")]
    [InlineData(new[] { "Beyoncé" }, "beyonce")]
    public void ResolveMainArtistKey_UsesAlphabeticallyFirstMainArtist(string[] artists, string expected)
    {
        Assert.Equal(expected, ArtistOrderKey.ResolveMainArtistKey(artists, null));
    }

    [Fact]
    public void ResolveMainArtistKey_FallsBackToProvidedArtist()
    {
        Assert.Equal("fleetwood mac", ArtistOrderKey.ResolveMainArtistKey(Array.Empty<string>(), "Fleetwood Mac"));
        Assert.Equal(string.Empty, ArtistOrderKey.ResolveMainArtistKey(Array.Empty<string>(), null));
    }

    [Fact]
    public void OrdinalSortPutsNumbersBeforeLetters()
    {
        var keys = new List<string> { "adele", "21 savage", "drake" };
        keys.Sort(StringComparer.Ordinal);
        Assert.Equal(new[] { "21 savage", "adele", "drake" }, keys);
    }
}
