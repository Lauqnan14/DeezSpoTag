using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Storage contract for the four cached Spotify artist catalogue sections (discography, top
/// songs, appears on, similar artists). The envelope carries revision metadata beside the
/// payload so retention decisions can distinguish observed authoritative content — including
/// verified empty sections — from legacy rows and unresolved fetches.
/// </summary>
public class SpotifyLibraryCatalogueCacheTest
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static SpotifyArtistCatalogueMetadata CompleteMetadata(string spotifyId, long revision = 100)
        => new(
            revision,
            DateTimeOffset.UnixEpoch,
            spotifyId,
            SpotifyCatalogueSectionPublication.Observed,
            SpotifyCatalogueSectionPublication.VerifiedEmpty,
            SpotifyCatalogueSectionPublication.Observed,
            SpotifyCatalogueSectionPublication.Observed);

    private static SpotifyArtistPageResult BuildResult(string spotifyId, string albumId)
    {
        var album = new SpotifyAlbum(
            albumId,
            "Held Album",
            "2020",
            "album",
            10,
            new List<SpotifyImage>(),
            null);
        var profile = new SpotifyArtistProfile(
            Id: spotifyId,
            Name: "Fixture Artist",
            Images: new List<SpotifyImage>(),
            Genres: new List<string>(),
            Followers: 0,
            Popularity: 0,
            SourceUrl: null,
            Biography: "Fixture biography.",
            Verified: false,
            MonthlyListeners: null,
            Rank: null,
            HeaderImageUrl: null,
            Gallery: new List<string>(),
            DiscographyType: null,
            TotalAlbums: 0);
        return new SpotifyArtistPageResult(
            true,
            profile,
            new List<SpotifyAlbum> { album },
            new List<SpotifyAlbum>(),
            new List<SpotifyTrack>(),
            new List<SpotifyRelatedArtist>());
    }

    [Fact]
    public void EnvelopeRoundTrip_PreservesCatalogueMetadata()
    {
        var payload = BuildResult("fixture-id", "album-1");
        var envelope = new SpotifyArtistCacheEnvelope(10, payload, CompleteMetadata("fixture-id", revision: 77));

        var json = JsonSerializer.Serialize(envelope, JsonOptions);
        var restored = JsonSerializer.Deserialize<SpotifyArtistCacheEnvelope>(json, JsonOptions);

        Assert.NotNull(restored);
        Assert.Equal(10, restored!.Version);
        Assert.NotNull(restored.Payload);
        Assert.Equal("album-1", Assert.Single(restored.Payload!.Albums).Id);
        var metadata = restored.Catalogue;
        Assert.NotNull(metadata);
        Assert.Equal(77, metadata!.Revision);
        Assert.Equal("fixture-id", metadata.SourceSpotifyId);
        Assert.Equal(SpotifyCatalogueSectionPublication.Observed, metadata.Albums);
        Assert.Equal(SpotifyCatalogueSectionPublication.VerifiedEmpty, metadata.AppearsOn);
        Assert.True(metadata.IsCompleteFor("fixture-id"));
    }

    /// <summary>Rows written before the catalogue schema keep rendering: no Catalogue node.</summary>
    [Fact]
    public void LegacyEnvelopeWithoutCatalogue_DeserializesWithNullMetadata()
    {
        var payload = BuildResult("legacy-id", "album-9");
        var legacyJson = JsonSerializer.Serialize(new { version = 10, payload }, JsonOptions);

        var restored = JsonSerializer.Deserialize<SpotifyArtistCacheEnvelope>(legacyJson, JsonOptions);

        Assert.NotNull(restored);
        Assert.Equal(10, restored!.Version);
        Assert.Equal("album-9", Assert.Single(restored.Payload!.Albums).Id);
        Assert.Null(restored.Catalogue);
        // Legacy rows are treated as incomplete so the recovery path republishes them.
        Assert.False(restored.Catalogue?.IsCompleteFor("legacy-id") ?? false);
    }

    [Theory]
    [InlineData("spotify", true)]
    [InlineData("other", false)]
    public void Completeness_RequiresEverySectionForMatchingIdentity(string queriedId, bool expected)
    {
        var metadata = CompleteMetadata("spotify");
        Assert.Equal(expected, metadata.IsCompleteFor(queriedId));
    }

    [Fact]
    public void Completeness_FailsWhenAnySectionIsUnobserved()
    {
        var metadata = new SpotifyArtistCatalogueMetadata(
            5,
            DateTimeOffset.UnixEpoch,
            "spotify",
            SpotifyCatalogueSectionPublication.Observed,
            SpotifyCatalogueSectionPublication.Observed,
            SpotifyCatalogueSectionPublication.Observed,
            SpotifyCatalogueSectionPublication.Unobserved);
        Assert.False(metadata.IsCompleteFor("spotify"));
    }

    [Fact]
    public void FromObservation_MapsVerifiedEmptySectionsAndPublisherStates()
    {
        var observation = new SpotifyArtistCatalogueObservation(
            AlbumsObserved: true,
            DiscographyCompleted: true,
            AlbumsVerifiedEmpty: false,
            TopTracksObserved: true,
            TopTracksVerifiedEmpty: true,
            RelatedArtistsObserved: true,
            RelatedArtistsVerifiedEmpty: true,
            AppearsOnObserved: true,
            AppearsOnVerifiedEmpty: false);
        Assert.True(observation.IsPublishable);

        var metadata = SpotifyArtistCatalogueMetadata.FromObservation(9, DateTimeOffset.UnixEpoch, "spotify", observation);

        Assert.Equal(9, metadata.Revision);
        Assert.Equal(SpotifyCatalogueSectionPublication.Observed, metadata.Albums);
        Assert.Equal(SpotifyCatalogueSectionPublication.VerifiedEmpty, metadata.TopTracks);
        Assert.Equal(SpotifyCatalogueSectionPublication.VerifiedEmpty, metadata.RelatedArtists);
        Assert.Equal(SpotifyCatalogueSectionPublication.Observed, metadata.AppearsOn);
    }

    [Fact]
    public void IncompleteDiscographyObservation_IsNotPublishable()
    {
        // A discography fetch that did not complete (or skipped the page entirely) never
        // publishes: the walk would otherwise truncate the retained list without evidence.
        var observation = new SpotifyArtistCatalogueObservation(
            AlbumsObserved: true,
            DiscographyCompleted: false,
            AlbumsVerifiedEmpty: false,
            TopTracksObserved: true,
            TopTracksVerifiedEmpty: false,
            RelatedArtistsObserved: true,
            RelatedArtistsVerifiedEmpty: false,
            AppearsOnObserved: true,
            AppearsOnVerifiedEmpty: false);
        Assert.False(observation.IsPublishable);

        var missingPageObservation = observation with { DiscographyCompleted = true, AlbumsObserved = false };
        Assert.False(missingPageObservation.IsPublishable);
    }

    [Fact]
    public void ConditionalUpsert_WritesOnlyWhenStoredPayloadMatches()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"artist-catalogue-{Guid.NewGuid():N}.db");
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Library"] = "Data Source=" + dbPath })
                .Build();
            new DeezSpoTag.Services.Library.LibraryDbService(configuration, NullLogger<DeezSpoTag.Services.Library.LibraryDbService>.Instance)
                .EnsureSchemaAsync().GetAwaiter().GetResult();
            var cache = new ArtistPageCacheRepository(configuration, NullLogger<ArtistPageCacheRepository>.Instance);
            cache.UpsertAsync("spotify", "fixture", "stored-json", DateTimeOffset.UtcNow, CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.True(cache.UpsertIfUnchangedAsync(
                "spotify", "fixture", "stored-json", "expected-json", DateTimeOffset.UtcNow, CancellationToken.None)
                .GetAwaiter().GetResult());
            var expected = cache.TryGetAsync("spotify", "fixture", CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal("expected-json", expected!.PayloadJson);

            // A row moved by an unrelated write is left alone instead of rolled back.
            Assert.False(cache.UpsertIfUnchangedAsync(
                "spotify", "fixture", "stored-json", "rollback-json", DateTimeOffset.UtcNow, CancellationToken.None)
                .GetAwaiter().GetResult());
            var after = cache.TryGetAsync("spotify", "fixture", CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal("expected-json", after!.PayloadJson);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(dbPath);
        }
    }

    [Fact]
    public void Clear_RetainsSpotifyCatalogueRows()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"artist-catalogue-clear-{Guid.NewGuid():N}.db");
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Library"] = "Data Source=" + dbPath })
                .Build();
            new DeezSpoTag.Services.Library.LibraryDbService(configuration, NullLogger<DeezSpoTag.Services.Library.LibraryDbService>.Instance)
                .EnsureSchemaAsync().GetAwaiter().GetResult();
            var cache = new ArtistPageCacheRepository(configuration, NullLogger<ArtistPageCacheRepository>.Instance);
            cache.UpsertAsync("spotify", "keep", "spotify-json", DateTimeOffset.UtcNow, CancellationToken.None).GetAwaiter().GetResult();
            cache.UpsertAsync("apple", "drop", "apple-json", DateTimeOffset.UtcNow, CancellationToken.None).GetAwaiter().GetResult();

            cache.ClearAsync(null, CancellationToken.None, preserveSource: "spotify").GetAwaiter().GetResult();

            // Spotify catalogue rows survive a cache clear: only a Refresh cache run replaces them.
            Assert.NotNull(cache.TryGetAsync("spotify", "keep", CancellationToken.None).GetAwaiter().GetResult());
            Assert.Null(cache.TryGetAsync("apple", "drop", CancellationToken.None).GetAwaiter().GetResult());

            // Clearing exactly the preserved source would erase the catalogue, so it is a no-op.
            cache.UpsertAsync("apple", "other", "apple-json", DateTimeOffset.UtcNow, CancellationToken.None).GetAwaiter().GetResult();
            cache.ClearAsync("spotify", CancellationToken.None, preserveSource: "spotify").GetAwaiter().GetResult();
            Assert.NotNull(cache.TryGetAsync("spotify", "keep", CancellationToken.None).GetAwaiter().GetResult());
            Assert.NotNull(cache.TryGetAsync("apple", "other", CancellationToken.None).GetAwaiter().GetResult());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(dbPath);
        }
    }
}
