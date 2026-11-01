using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
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
/// Publish/retain lifecycle for the four cached Spotify artist catalogue sections. Only a
/// whole-discography fetch publishes a validated revision; biography/artwork/enrichment
/// writes must preserve the retained lists and failed refreshes must not replace them.
/// </summary>
public class LibraryArtistCatalogueLifecycleTest
{
    private const long ArtistId = 4401;
    private const string Canonical = "Fixture Artist";
    private const string SpotifyId = "fixtureSpotifyId";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static (IConfiguration Configuration, string DbPath) CreateLibrary()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"artist-catalogue-{Guid.NewGuid():N}.db");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = $"Data Source={dbPath}"
            })
            .Build();
        return (configuration, dbPath);
    }

    private static async Task EnsureSchemaAsync(IConfiguration configuration)
        => await new LibraryDbService(configuration, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();

    private static async Task SeedArtistAsync(string dbPath)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO artist (id, name) VALUES ($id, $name);";
        command.Parameters.AddWithValue("$id", ArtistId);
        command.Parameters.AddWithValue("$name", Canonical);
        await command.ExecuteNonQueryAsync();
    }

    private static SpotifyArtistService CreateService(
        LibraryRepository repository,
        ArtistPageCacheRepository cache,
        string dbPath)
    {
        var service = (SpotifyArtistService)RuntimeHelpers.GetUninitializedObject(typeof(SpotifyArtistService));
        // An uninitialized instance skips field initializers, so the fields the persist
        // methods read have to be supplied explicitly.
        SetField(service, "_jsonOptions", new JsonSerializerOptions(JsonSerializerDefaults.Web));
        SetField(service, "_libraryRepository", repository);
        SetField(service, "_cacheRepository", cache);
        SetField(service, "_logger", NullLogger<SpotifyArtistService>.Instance);
        SetField(service, "_configStore", new LibraryConfigStore(
            repository,
            NullLogger<LibraryConfigStore>.Instance,
            new CatalogueEnvironment(dbPath + ".cache")));
        SetField(service, "_backgroundWorkCoordinator", new DeezSpoTag.Services.Runtime.BackgroundWorkCoordinator());
        return service;
    }

    private static void SetField(object target, string name, object? value)
        => typeof(SpotifyArtistService).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(target, value);

    private static object Invoke(object target, string name, params object?[] arguments)
    {
        var method = target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Method '{name}' was not found.");
        var parameters = method.GetParameters();
        if (arguments.Length < parameters.Length && parameters.Skip(arguments.Length).All(parameter => parameter.HasDefaultValue))
            arguments = arguments.Concat(parameters.Skip(arguments.Length).Select(parameter => parameter.DefaultValue)).ToArray();
        return method.Invoke(target, arguments)!;
    }

    private sealed class CatalogueEnvironment(string root) : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Catalogue fixtures";
        public string ContentRootPath { get; set; } = root;
        public string WebRootPath { get; set; } = root;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private static SpotifyArtistPageResult BuildResult(string albumId, string biography = "Fresh biography.")
    {
        var album = new SpotifyAlbum(albumId, "Fresh Album", "2024", "album", 10, new List<SpotifyImage>(), null);
        var profile = new SpotifyArtistProfile(
            Id: SpotifyId,
            Name: Canonical,
            Images: new List<SpotifyImage>(),
            Genres: new List<string>(),
            Followers: 0,
            Popularity: 0,
            SourceUrl: null,
            Biography: biography,
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

    private static SpotifyArtistPageResult BuildStoredResult()
    {
        var album = new SpotifyAlbum("stored-album", "Stored Album", "2020", "album", 8, new List<SpotifyImage>(), null);
        var appearsOn = new SpotifyAlbum("stored-appears", "Stored Appearance", "2021", "appears_on", 9, new List<SpotifyImage>(), null);
        var profile = new SpotifyArtistProfile(
            Id: SpotifyId,
            Name: Canonical,
            Images: new List<SpotifyImage>(),
            Genres: new List<string>(),
            Followers: 0,
            Popularity: 0,
            SourceUrl: null,
            Biography: "Stored biography.",
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
            new List<SpotifyAlbum> { appearsOn },
            new List<SpotifyTrack>(),
            new List<SpotifyRelatedArtist>());
    }

    private static SpotifyArtistCatalogueMetadata StoredMetadata(long revision)
        => new(
            revision,
            DateTimeOffset.UnixEpoch,
            SpotifyId,
            SpotifyCatalogueSectionPublication.Observed,
            SpotifyCatalogueSectionPublication.Observed,
            SpotifyCatalogueSectionPublication.Observed,
            SpotifyCatalogueSectionPublication.Observed);

    private static async Task<(LibraryRepository Repository, ArtistPageCacheRepository Cache, SpotifyArtistService Service, string DbPath)> Bootstrap()
    {
        var (configuration, dbPath) = CreateLibrary();
        await EnsureSchemaAsync(configuration);
        await SeedArtistAsync(dbPath);
        var repository = new LibraryRepository(configuration, NullLogger<LibraryRepository>.Instance);
        var cache = new ArtistPageCacheRepository(configuration, NullLogger<ArtistPageCacheRepository>.Instance);
        await repository.UpsertArtistSourceIdAsync(ArtistId, "spotify", SpotifyId, CancellationToken.None);
        var service = CreateService(repository, cache, dbPath);
        return (repository, cache, service, dbPath);
    }

    private static async Task<(string Json, ArtistCacheEntry Entry)> SeedStoredAsync(
        ArtistPageCacheRepository cache,
        long revision)
    {
        var envelope = new SpotifyArtistCacheEnvelope(10, BuildStoredResult(), StoredMetadata(revision));
        var json = JsonSerializer.Serialize(envelope, JsonOptions);
        await cache.UpsertAsync("spotify", SpotifyId, json, DateTimeOffset.UtcNow, CancellationToken.None);
        var entry = (await cache.TryGetAsync("spotify", SpotifyId, CancellationToken.None))!;
        return (json, entry);
    }

    /// <summary>
    /// Page visits and the refresh executor read catalogue completeness from the retained row
    /// without any provider work: a completed reflect-four-sections envelope is complete for
    /// its identity, a legacy envelope without metadata is not.
    /// </summary>
    [Fact]
    public async Task CatalogueSnapshotReadsStoredEnvelopeCompleteness()
    {
        var (repository, cache, service, dbPath) = await Bootstrap();
        try
        {
            // Legacy row without catalogue metadata: incomplete, recovery path republished it.
            var legacyJson = JsonSerializer.Serialize(new { version = 10, payload = BuildStoredResult() }, JsonOptions);
            await cache.UpsertAsync("spotify", SpotifyId, legacyJson, DateTimeOffset.UtcNow, CancellationToken.None);
            var legacyStatus = await service.TryReadArtistCatalogueStatusAsync(ArtistId, Canonical, CancellationToken.None);
            Assert.NotNull(legacyStatus);
            Assert.Equal(SpotifyId, legacyStatus!.SpotifyId);
            Assert.False(legacyStatus.IsComplete);

            await SeedStoredAsync(cache, revision: 5);
            var status = await service.TryReadArtistCatalogueStatusAsync(ArtistId, Canonical, CancellationToken.None);
            Assert.NotNull(status);
            Assert.True(status!.IsComplete);
            Assert.NotNull(status.Metadata);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(dbPath);
        }
    }

    /// <summary>
    /// A biography/artwork refresh fetches profile data without a discography and persists
    /// profile-only: the retained catalogue lists and their revision metadata survive.
    /// </summary>
    [Fact]
    public async Task ProfilePersistPreservesCatalogueListsAndMetadata()
    {
        var (repository, cache, service, dbPath) = await Bootstrap();
        try
        {
            await SeedStoredAsync(cache, revision: 7);

            // The fetched page has no discography content (a biography refresh) and would
            // otherwise write empty sections over the retained catalogue.
            var fetched = BuildResult(albumId: "fresh-album");
            Invoke(service, "PersistProfilePreservingCatalogueAsync", SpotifyId, Canonical, fetched, CancellationToken.None, true);

            var stored = (await cache.TryGetAsync("spotify", SpotifyId, CancellationToken.None))!;
            var restored = JsonSerializer.Deserialize<SpotifyArtistCacheEnvelope>(stored.PayloadJson, JsonOptions)!;
            var metadata = Assert.IsType<SpotifyArtistCatalogueMetadata>(restored.Catalogue);
            Assert.Equal(7, metadata.Revision);
            Assert.Equal("stored-album", Assert.Single(restored.Payload!.Albums).Id);
            Assert.Equal("stored-appears", Assert.Single(restored.Payload.AppearsOn).Id);
            // Only profile fields moved.
            Assert.Equal("Fresh biography.", restored.Payload.Artist.Biography);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(dbPath);
        }
    }

    /// <summary>A refresh whose response left sections unresolved retains the stored revision.</summary>
    [Fact]
    public async Task UnresolvedPublicationRetainsStoredRevision()
    {
        var (repository, cache, service, dbPath) = await Bootstrap();
        try
        {
            var (storedJson, _) = await SeedStoredAsync(cache, revision: 3);

            var unresolved = new SpotifyArtistCatalogueObservation(
                AlbumsObserved: true,
                DiscographyCompleted: true,
                AlbumsVerifiedEmpty: false,
                TopTracksObserved: false,
                TopTracksVerifiedEmpty: false,
                RelatedArtistsObserved: true,
                RelatedArtistsVerifiedEmpty: false,
                AppearsOnObserved: true,
                AppearsOnVerifiedEmpty: false);
            var hydratedPage = new SpotifyArtistHydratedPage(
                new SpotifyArtistOverview(
                    SpotifyId,
                    Canonical,
                    ImageUrl: null,
                    HeaderImageUrl: null,
                    new List<string>(),
                    Followers: null,
                    new List<string>(),
                    SourceUrl: null,
                    Popularity: null,
                    TotalAlbums: null,
                    DiscographyType: "album"),
                new SpotifyArtistExtras(Biography: null, Verified: false, MonthlyListeners: null, Rank: null),
                new List<SpotifyTrackSummary>(),
                new List<SpotifyRelatedArtist>(),
                new List<SpotifyAlbumSummary>(),
                new List<SpotifyAlbumSummary>(),
                unresolved);

            var fetched = BuildResult(albumId: "never-stored");
            Invoke(service, "PersistCatalogueRevisionAsync", SpotifyId, Canonical, hydratedPage, fetched, CancellationToken.None, true);

            var row = (await cache.TryGetAsync("spotify", SpotifyId, CancellationToken.None))!;
            Assert.Equal(storedJson, row.PayloadJson);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(dbPath);
        }
    }

    /// <summary>
    /// A validated refresh replaces the retained lists — including a verified-empty section —
    /// and bumps the revision; older lists are not resurrected.
    /// </summary>
    [Fact]
    public async Task ValidatedPublicationReplacesOlderListsIncludingVerifiedEmpty()
    {
        var (repository, cache, service, dbPath) = await Bootstrap();
        try
        {
            await SeedStoredAsync(cache, revision: 3);

            var observation = new SpotifyArtistCatalogueObservation(
                AlbumsObserved: true,
                DiscographyCompleted: true,
                AlbumsVerifiedEmpty: false,
                TopTracksObserved: true,
                TopTracksVerifiedEmpty: true,
                RelatedArtistsObserved: true,
                RelatedArtistsVerifiedEmpty: true,
                AppearsOnObserved: true,
                AppearsOnVerifiedEmpty: true);
            var hydratedPage = new SpotifyArtistHydratedPage(
                new SpotifyArtistOverview(
                    SpotifyId,
                    Canonical,
                    ImageUrl: null,
                    HeaderImageUrl: null,
                    new List<string>(),
                    Followers: null,
                    new List<string>(),
                    SourceUrl: null,
                    Popularity: null,
                    TotalAlbums: null,
                    DiscographyType: "album"),
                new SpotifyArtistExtras(Biography: null, Verified: false, MonthlyListeners: null, Rank: null),
                new List<SpotifyTrackSummary>(),
                new List<SpotifyRelatedArtist>(),
                new List<SpotifyAlbumSummary>(),
                new List<SpotifyAlbumSummary>(),
                observation);

            // The fetched content carries a new album list and empty top-tracks/related
            // sections: verified-empty means the older lists are authoritative-ly replaced.
            var fetched = BuildResult(albumId: "revision-album");
            Invoke(service, "PersistCatalogueRevisionAsync", SpotifyId, Canonical, hydratedPage, fetched, CancellationToken.None, true);

            var row = (await cache.TryGetAsync("spotify", SpotifyId, CancellationToken.None))!;
            var restored = JsonSerializer.Deserialize<SpotifyArtistCacheEnvelope>(row.PayloadJson, JsonOptions)!;
            var metadata = Assert.IsType<SpotifyArtistCatalogueMetadata>(restored.Catalogue);
            Assert.Equal("revision-album", Assert.Single(restored.Payload!.Albums).Id);
            Assert.Empty(restored.Payload.TopTracks);
            Assert.Empty(restored.Payload.RelatedArtists);
            Assert.Empty(restored.Payload.AppearsOn);
            Assert.Equal(SpotifyCatalogueSectionPublication.VerifiedEmpty, metadata.TopTracks);
            Assert.Equal(SpotifyCatalogueSectionPublication.VerifiedEmpty, metadata.RelatedArtists);
            Assert.Equal(SpotifyCatalogueSectionPublication.VerifiedEmpty, metadata.AppearsOn);
            Assert.Equal(SpotifyCatalogueSectionPublication.Observed, metadata.Albums);
            Assert.True(metadata.Revision > 3);
            Assert.True(metadata.IsCompleteFor(SpotifyId));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(dbPath);
        }
    }
}
