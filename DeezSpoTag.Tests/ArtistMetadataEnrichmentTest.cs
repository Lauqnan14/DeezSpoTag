using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Web.Services.ArtistLocation;
using DeezSpoTag.Web.Services.AutoTag;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ArtistMetadataEnrichmentTest
{
    [Theory]
    [InlineData("musicbrainz")]
    [InlineData("audiomack")]
    [InlineData("third-provider")]
    public async Task OrderedSourcesRetainIdentityProvenanceAndUnknownRole(string source)
    {
        var time = DateTimeOffset.Parse("2026-10-07T09:00:00Z");
        var selected = new ArtistLocationResult(null, null, "Kenya", "KE", source)
        { SourceReference = "artist:42", RetrievedAt = time, KnownAt = time };
        var first = new Source("empty", null);
        var second = new Source(source, selected);
        var third = new Source("other", new ArtistLocationResult(null, "Different City", "Other", null, "other"));
        var resolver = new ArtistLocationResolver(NullLogger<ArtistLocationResolver>.Instance,
            new IArtistLocationSource[] { first, second, third });
        var result = await resolver.ResolveAsync(42, "Artist");
        Assert.Equal(42, result!.ArtistId);
        Assert.Equal("Artist", result.ArtistName);
        Assert.Equal(ArtistMetadataRole.Unknown, result.ArtistRole);
        Assert.Equal(source, result.Source);
        Assert.Equal(time, result.KnownAt);
        Assert.Equal(time, result.RetrievedAt);
        Assert.Equal("artist:42", result.SourceReference);
        Assert.Null(result.City);
        Assert.Equal(0, third.Calls);
    }

    [Fact]
    public async Task CityOnlyResultDoesNotMergeLaterCountry()
    {
        var later = new Source("later", new ArtistLocationResult(null, null, "Kenya", "KE", "later"));
        var resolver = new ArtistLocationResolver(NullLogger<ArtistLocationResolver>.Instance,
            new IArtistLocationSource[] { new Source("first", new ArtistLocationResult(null, "Nairobi", null, null, "first")), later });
        var result = await resolver.ResolveAsync(42, "Artist");
        Assert.Equal("Nairobi", result!.City);
        Assert.Null(result.Country);
        Assert.Equal(0, later.Calls);
    }

    [Fact]
    public async Task PopulateArtistMetadataRetainsProvenanceAndLanguageEvidenceWhenWritesOff()
    {
        var time = DateTimeOffset.Parse("2026-10-07T09:00:00Z");
        var source = new Source("musicbrainz", new ArtistLocationResult(null, "Nairobi", "Kenya", "KE", "musicbrainz")
        { SourceReference = "artist:7", RetrievedAt = time, KnownAt = time });
        var resolver = new ArtistLocationResolver(NullLogger<ArtistLocationResolver>.Instance, new IArtistLocationSource[] { source });
        var (runner, cacheDb) = BuildRunner(resolver, out var config);
        try
        {
            var track = new AutoTagTrack { Title = "Song", Artists = ["Artist"], ArtistId = "7000" };
            await runner.PopulateArtistMetadataAsync(track, providerId: "deezer");

            Assert.NotNull(track.ArtistMetadata);
            Assert.Equal("deezer", track.ArtistMetadata!.BindingSource);
            Assert.Equal(7, track.ArtistMetadata.ArtistId);
            Assert.Equal("Nairobi", track.ArtistMetadata.Location?.City);
            Assert.Equal(ArtistMetadataRole.Unknown, track.ArtistMetadata.ArtistRole);

            var entry = await cacheDb.TryGetAsync("artist-enrichment", "7", CancellationToken.None);
            Assert.NotNull(entry);
            Assert.Contains("musicbrainz", entry!.PayloadJson, StringComparison.Ordinal);
            Assert.Contains("Nairobi", entry.PayloadJson, StringComparison.Ordinal);
        }
        finally
        {
            RestoreEnvironment(config);
        }
    }

    [Fact]
    public async Task BoundPrimaryArtistLocationSurvivesFeaturedCredits()
    {
        var source = new Source("musicbrainz", new ArtistLocationResult(null, "Nairobi", "Kenya", "KE", "musicbrainz"));
        var (runner, _) = BuildRunner(new ArtistLocationResolver(NullLogger<ArtistLocationResolver>.Instance,
            new IArtistLocationSource[] { source }), out var directory);
        try
        {
            var track = new AutoTagTrack { Artists = ["Artist", "Featured Artist"], ArtistId = "7000" };
            await runner.PopulateArtistMetadataAsync(track, providerId: "deezer", preferSingleArtist: true);
            Assert.Equal("Nairobi", track.ArtistMetadata?.Location?.City);
            Assert.Equal(ArtistMetadataRole.Unknown, track.ArtistMetadata?.ArtistRole);
            Assert.True(track.ArtistMetadataUsesSingleArtistPreference);
            Assert.Equal(2, track.ArtistMetadataRecords.Count);
        }
        finally { RestoreEnvironment(directory); }
    }

    [Fact]
    public async Task PopulateArtistMetadataLeavesAmbiguousIdentityInternal()
    {
        var source = new Source("musicbrainz", new ArtistLocationResult(null, "Nairobi", "Kenya", "KE", "musicbrainz"));
        var resolver = new ArtistLocationResolver(NullLogger<ArtistLocationResolver>.Instance, new IArtistLocationSource[] { source });
        var (runner, cacheDb) = BuildRunner(resolver, out var config);
        try
        {
            var track = new AutoTagTrack { Title = "Song", Artists = ["Artist"], ArtistId = "shazam:artist:9" };
            await runner.PopulateArtistMetadataAsync(track);

            Assert.NotNull(track.ArtistMetadata);
            Assert.Null(track.ArtistMetadata!.ArtistId);
            Assert.Equal(ArtistMetadataRole.Unknown, track.ArtistMetadata.ArtistRole);
            Assert.Equal(0, source.Calls);
            Assert.Null(await cacheDb.TryGetAsync("artist-enrichment", "shazam:artist:9", CancellationToken.None));
        }
        finally
        {
            RestoreEnvironment(config);
        }
    }

    [Fact]
    public async Task AlbumAssociationDoesNotPromoteArtistRole()
    {
        var source = new Source("musicbrainz", new ArtistLocationResult(null, "Nairobi", "Kenya", "KE", "musicbrainz"));
        var resolver = new ArtistLocationResolver(NullLogger<ArtistLocationResolver>.Instance, new IArtistLocationSource[] { source });
        var (runner, cacheDb) = BuildRunner(resolver, out var config);
        try
        {
            var track = new AutoTagTrack
            {
                Title = "Song",
                Artists = ["Artist"],
                AlbumArtists = ["AlbumOwner"],
                ArtistId = "7"
            };
            await runner.PopulateArtistMetadataAsync(track);

            Assert.Equal(ArtistMetadataRole.Unknown, track.ArtistMetadata!.ArtistRole);
        }
        finally
        {
            RestoreEnvironment(config);
        }
    }

    [Fact]
    public async Task NumericProviderIdIsNotALocalArtistId()
    {
        var source = new Source("musicbrainz", new ArtistLocationResult(null, "Nairobi", "Kenya", "KE", "musicbrainz"));
        var resolver = new ArtistLocationResolver(NullLogger<ArtistLocationResolver>.Instance, new IArtistLocationSource[] { source });
        var (runner, cache) = BuildRunner(resolver, out var directory);
        try
        {
            var track = new AutoTagTrack { Artists = ["Artist"], ArtistId = "999999", AlbumArtistId = "7" };
            await runner.PopulateArtistMetadataAsync(track);
            Assert.Equal(0, source.Calls);
            Assert.Null(track.ArtistMetadata?.ArtistId);
        }
        finally { RestoreEnvironment(directory); }
    }

    [Fact]
    public async Task MultipleArtistNamesRetainSeparateUnboundRecords()
    {
        var source = new Source("test", null);
        var (runner, cache) = BuildRunner(new ArtistLocationResolver(NullLogger<ArtistLocationResolver>.Instance, new IArtistLocationSource[] { source }), out var directory);
        try
        {
            var track = new AutoTagTrack { Artists = ["One", "Two"], ArtistId = "7000" };
            await runner.PopulateArtistMetadataAsync(track, providerId: "deezer");
            var property = typeof(AutoTagTrack).GetProperty("ArtistMetadataRecords");
            Assert.NotNull(property);
            var records = Assert.IsAssignableFrom<IReadOnlyList<ArtistEnrichmentMetadata>>(property!.GetValue(track));
            Assert.Equal(2, records.Count);
            Assert.All(records, record => { Assert.Null(record.ArtistId); Assert.Equal(ArtistMetadataRole.Unknown, record.ArtistRole); });
            Assert.Null(track.ArtistMetadata);
            Assert.Equal(0, source.Calls);
        }
        finally { RestoreEnvironment(directory); }
    }

    [Fact]
    public async Task RetainedArtistLanguageKeepsItsOriginalObservationTime()
    {
        var time = DateTimeOffset.Parse("2026-10-01T09:00:00Z");
        var source = new Source("test", new ArtistLocationResult(null, null, "Kenya", null, "test"));
        var (runner, cache) = BuildRunner(new ArtistLocationResolver(NullLogger<ArtistLocationResolver>.Instance, new IArtistLocationSource[] { source }), out var directory);
        try
        {
            var retained = new ArtistEnrichmentMetadata(7, "Artist", ArtistMetadataRole.Unknown, "deezer", "bound", "7000", null,
                [new LanguageMetadataEvidence(["English"], LanguageMetadataScope.Artist, "audio-file", "fixture", time)]);
            await cache.UpsertAsync("artist-enrichment", "7", System.Text.Json.JsonSerializer.Serialize(new { version = "v1", metadata = retained }), DateTimeOffset.UtcNow, CancellationToken.None);
            var track = new AutoTagTrack { Artists = ["Artist"], ArtistId = "7000" };
            await runner.PopulateArtistMetadataAsync(track, providerId: "deezer");
            var evidence = Assert.Single(track.ArtistMetadata!.Languages);
            Assert.Equal(time, evidence.KnownAt);
            Assert.Equal("audio-file", evidence.Source);
            Assert.Null(track.TrackLanguageEvidence);
        }
        finally { RestoreEnvironment(directory); }
    }

    [Fact]
    public async Task MetadataRefreshDoesNotEraseRetainedArtistLanguage()
    {
        var source = new Source("test", new ArtistLocationResult(null, "Nairobi", "Kenya", null, "test") { SourceReference = "test-only" });
        var resolver = new ArtistLocationResolver(NullLogger<ArtistLocationResolver>.Instance, new IArtistLocationSource[] { source });
        var (runner, cache) = BuildRunner(resolver, out var directory);
        try
        {
            var time = DateTimeOffset.Parse("2026-10-01T09:00:00Z");
            var retained = new ArtistEnrichmentMetadata(7, "Artist", ArtistMetadataRole.Unknown, "deezer", "bound", "7000", null,
                [new LanguageMetadataEvidence(["English"], LanguageMetadataScope.Artist, "audio-file", "fixture", time)]);
            await cache.UpsertAsync("artist-enrichment", "7", System.Text.Json.JsonSerializer.Serialize(new { version = "v1", metadata = retained }), DateTimeOffset.UtcNow, CancellationToken.None);
            var type = typeof(DeezSpoTag.Web.Services.ArtistMetadataCacheRefreshService);
            var service = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
            type.GetField("_locationResolver", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(service, resolver);
            type.GetField("_artistPageCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(service, cache);
            await (Task)type.GetMethod("RetainArtistEnrichmentAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(service, new object[] { 7L, "Artist", CancellationToken.None })!;
            var entry = await cache.TryGetAsync("artist-enrichment", "7", CancellationToken.None);
            using var json = System.Text.Json.JsonDocument.Parse(entry!.PayloadJson);
            Assert.Equal(time, json.RootElement.GetProperty("metadata").GetProperty("Languages")[0].GetProperty("KnownAt").GetDateTimeOffset());
        }
        finally { RestoreEnvironment(directory); }
    }

    private static (LocalAutoTagRunner Runner, DeezSpoTag.Services.Library.ArtistPageCacheRepository Cache) BuildRunner(
        ArtistLocationResolver resolver, out string? previousLibraryDb)
    {
        var directory = Path.Combine(Path.GetTempPath(), "artist-meta-cache-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var dbPath = Path.Combine(directory, "library.db");
        previousLibraryDb = directory;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["ConnectionStrings:Library"] = $"Data Source={dbPath}" }).Build();
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"CREATE TABLE IF NOT EXISTS artist_page_cache (
                source TEXT NOT NULL, source_id TEXT NOT NULL, payload_json TEXT NOT NULL,
                fetched_utc TEXT NOT NULL, created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP, PRIMARY KEY (source, source_id));
                CREATE TABLE artist_source (artist_id INTEGER, source TEXT, source_id TEXT);
                CREATE TABLE artist (id INTEGER PRIMARY KEY, name TEXT);
                INSERT INTO artist VALUES (7, 'Artist');
                INSERT INTO artist_source VALUES (7, 'deezer', '7000');";
            command.ExecuteNonQuery();
        }

        var cache = new DeezSpoTag.Services.Library.ArtistPageCacheRepository(
            configuration,
            NullLogger<DeezSpoTag.Services.Library.ArtistPageCacheRepository>.Instance);
        var collaborators = (LocalAutoTagRunner.LocalAutoTagRunnerCollaborators)Activator.CreateInstance(
            typeof(LocalAutoTagRunner.LocalAutoTagRunnerCollaborators))!;
        void Set(string name, object value) => collaborators.GetType().GetProperty(name)!.SetValue(collaborators, value);
        Set("Logger", NullLogger<LocalAutoTagRunner>.Instance);
        Set("ArtistLocationResolver", resolver);
        Set("ArtistPageCache", cache);
        Set("ArtistLibraryRepository", new DeezSpoTag.Services.Library.LibraryRepository(configuration, NullLogger<DeezSpoTag.Services.Library.LibraryRepository>.Instance));
        return (new LocalAutoTagRunner(collaborators), cache);
    }

    private static void RestoreEnvironment(string? previousLibraryDb)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (previousLibraryDb is not null) Directory.Delete(previousLibraryDb, true);
    }

    private sealed class Source(string name, ArtistLocationResult? result) : IArtistLocationSource
    {
        public string SourceName => name;
        public int Calls { get; private set; }
        public Task<ArtistLocationResult?> ResolveAsync(long artistId, string? artistName, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(result); }
    }
}
