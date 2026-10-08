using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Phase 1 database contract for Sonic embeddings.
///
/// Proves the round trip that the similarity index in Phase 2 depends on: a
/// normalized vector survives storage unchanged, the declared width is enforced
/// on the way out, and the lifecycle rules (delete cascade, version identity)
/// behave as documented.
/// </summary>
public sealed class SonicEmbeddingPersistenceTest : IAsyncLifetime
{
    private const string ModelId = "discogs-effnet-bs64-1";
    private const string ModelVersion = "1";
    private const string EmbeddingVersion = "embedding-v1";

    private string _tempRoot = string.Empty;
    private string _dbPath = string.Empty;
    private IConfiguration _configuration = default!;
    private LibraryRepository _repository = default!;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-sonic-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
        _dbPath = Path.Join(_tempRoot, "library.db");
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = $"Data Source={_dbPath}"
            })
            .Build();

        var dbService = new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance);
        await dbService.EnsureSchemaAsync();
        _repository = new LibraryRepository(_configuration, NullLogger<LibraryRepository>.Instance);
    }

    public Task DisposeAsync()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_tempRoot) && Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup.
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Embedding_RoundTripsWithoutLosingPrecision()
    {
        var (libraryId, trackId) = await SeedTrackAsync("Round Trip");
        var vector = NormalizedVector(seed: 11);

        await _repository.UpsertSonicEmbeddingAsync(
            Create(trackId, libraryId, vector));

        var stored = await _repository.GetSonicEmbeddingAsync(trackId, ModelId, ModelVersion, EmbeddingVersion);

        Assert.NotNull(stored);
        Assert.Equal(trackId, stored!.TrackId);
        Assert.Equal(libraryId, stored.LibraryId);
        Assert.Equal(vector.Count, stored.Dimensions);
        Assert.Equal("mean-v1", stored.PoolingMethod);
        Assert.Equal("l2-v1", stored.NormalizationMethod);
        Assert.Equal("cosine", stored.DistanceMetric);
        Assert.True(stored.IsUsable);

        // float32 storage is the contract, so compare at that precision.
        Assert.Equal(vector.Count, stored.Vector.Count);
        for (var index = 0; index < vector.Count; index++)
        {
            Assert.Equal(vector[index], stored.Vector[index], 5);
        }
    }

    [Fact]
    public async Task StoredVector_RemainsL2Normalized()
    {
        var (libraryId, trackId) = await SeedTrackAsync("Normalized");
        await _repository.UpsertSonicEmbeddingAsync(
            Create(trackId, libraryId, NormalizedVector(seed: 21)));

        var stored = await _repository.GetSonicEmbeddingAsync(trackId, ModelId, ModelVersion, EmbeddingVersion);
        Assert.NotNull(stored);

        var norm = Math.Sqrt(stored!.Vector.Sum(value => value * (double)value));
        Assert.Equal(1.0d, norm, 4);
    }

    [Fact]
    public async Task Upsert_ReplacesTheVectorForTheSameIdentity()
    {
        var (libraryId, trackId) = await SeedTrackAsync("Replaced");
        await _repository.UpsertSonicEmbeddingAsync(
            Create(trackId, libraryId, NormalizedVector(seed: 31)));
        var first = await _repository.GetSonicEmbeddingAsync(trackId, ModelId, ModelVersion, EmbeddingVersion);

        await _repository.UpsertSonicEmbeddingAsync(
            Create(trackId, libraryId, NormalizedVector(seed: 41)));
        var second = await _repository.GetSonicEmbeddingAsync(trackId, ModelId, ModelVersion, EmbeddingVersion);

        Assert.NotEqual(
            first!.Vector.Take(8).ToArray(),
            second!.Vector.Take(8).ToArray());
    }

    [Fact]
    public async Task DifferentEmbeddingVersion_StoresASeparateRow()
    {
        // Version identity is what makes a model change invalidate stored
        // vectors instead of silently mixing representations.
        var (libraryId, trackId) = await SeedTrackAsync("Versions");
        await _repository.UpsertSonicEmbeddingAsync(
            Create(trackId, libraryId, NormalizedVector(seed: 51)));

        await _repository.UpsertSonicEmbeddingAsync(
            Create(trackId, libraryId, NormalizedVector(seed: 61)) with
            { EmbeddingVersion = "embedding-v2" });

        var v1 = await _repository.GetSonicEmbeddingAsync(trackId, ModelId, ModelVersion, "embedding-v1");
        var v2 = await _repository.GetSonicEmbeddingAsync(trackId, ModelId, ModelVersion, "embedding-v2");

        Assert.NotNull(v1);
        Assert.NotNull(v2);
        Assert.NotEqual(v1!.Vector.Take(8).ToArray(), v2!.Vector.Take(8).ToArray());
    }

    [Fact]
    public async Task CorruptBlob_IsRejectedRatherThanReturned()
    {
        // A blob whose width disagrees with the declared dimension is a corrupt
        // vector. Returning it would let a similarity query score against
        // garbage, so the read path must drop it.
        var (libraryId, trackId) = await SeedTrackAsync("Corrupt");
        await _repository.UpsertSonicEmbeddingAsync(
            Create(trackId, libraryId, NormalizedVector(seed: 71)));

        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
UPDATE track_sonic_embedding
   SET dimensions = 640
 WHERE track_id = $trackId;";
            command.Parameters.AddWithValue("$trackId", trackId);
            await command.ExecuteNonQueryAsync();
        }

        var stored = await _repository.GetSonicEmbeddingAsync(trackId, ModelId, ModelVersion, EmbeddingVersion);
        Assert.Null(stored);
    }

    [Fact]
    public async Task NonFiniteBlob_IsRejected()
    {
        var (libraryId, trackId) = await SeedTrackAsync("NaN");
        await _repository.UpsertSonicEmbeddingAsync(
            Create(trackId, libraryId, NormalizedVector(seed: 81)));

        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
UPDATE track_sonic_embedding
   SET vector_blob = $blob
 WHERE track_id = $trackId;";
            var payload = new byte[1280 * sizeof(float)];
            var notANumber = BitConverter.SingleToInt32Bits(float.NaN);
            BitConverter.TryWriteBytes(payload.AsSpan(0, 4), notANumber);
            command.Parameters.AddWithValue("$blob", payload);
            command.Parameters.AddWithValue("$trackId", trackId);
            await command.ExecuteNonQueryAsync();
        }

        var stored = await _repository.GetSonicEmbeddingAsync(trackId, ModelId, ModelVersion, EmbeddingVersion);
        Assert.Null(stored);
    }

    [Fact]
    public async Task EmptyVector_IsNotPersisted()
    {
        var (libraryId, trackId) = await SeedTrackAsync("Empty");
        await _repository.UpsertSonicEmbeddingAsync(
            Create(trackId, libraryId, Array.Empty<float>()));

        var stored = await _repository.GetSonicEmbeddingAsync(trackId, ModelId, ModelVersion, EmbeddingVersion);
        Assert.Null(stored);
    }

    [Fact]
    public async Task LibraryBulkRead_IsScopedToOneLibrary()
    {
        // A similarity index must never see another library's vectors.
        var (firstLibrary, firstTrack) = await SeedTrackAsync("Library One A");
        var (secondLibrary, secondTrack) = await SeedTrackAsync("Library Two A");
        await _repository.UpsertSonicEmbeddingAsync(
            Create(firstTrack, firstLibrary, NormalizedVector(seed: 91)));
        await _repository.UpsertSonicEmbeddingAsync(
            Create(secondTrack, secondLibrary, NormalizedVector(seed: 92)));

        var forFirst = await _repository.GetLibrarySonicEmbeddingsAsync(
            firstLibrary, ModelId, ModelVersion, EmbeddingVersion);
        var forSecond = await _repository.GetLibrarySonicEmbeddingsAsync(
            secondLibrary, ModelId, ModelVersion, EmbeddingVersion);

        Assert.All(forFirst, embedding => Assert.Equal(firstLibrary, embedding.LibraryId));
        Assert.All(forSecond, embedding => Assert.Equal(secondLibrary, embedding.LibraryId));
        Assert.DoesNotContain(forFirst, embedding => embedding.TrackId == secondTrack);
    }

    [Fact]
    public async Task DeletingATrack_RemovesItsEmbedding()
    {
        var (libraryId, trackId) = await SeedTrackAsync("Cascade");
        await _repository.UpsertSonicEmbeddingAsync(
            Create(trackId, libraryId, NormalizedVector(seed: 101)));

        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            await using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys=ON;";
            await pragma.ExecuteNonQueryAsync();

            await using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM track WHERE id = $trackId;";
            delete.Parameters.AddWithValue("$trackId", trackId);
            await delete.ExecuteNonQueryAsync();
        }

        var stored = await _repository.GetSonicEmbeddingAsync(trackId, ModelId, ModelVersion, EmbeddingVersion);
        Assert.Null(stored);
    }

    [Fact]
    public async Task Coverage_DistinguishesAnalysedFromEmbedded()
    {
        var (libraryId, coveredTrack) = await SeedTrackAsync("Coverage A");
        var (_, bareTrack) = await SeedTrackAsync("Coverage B", intoLibraryId: libraryId);

        await _repository.UpsertSonicEmbeddingAsync(
            Create(coveredTrack, libraryId, NormalizedVector(seed: 111)));

        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
UPDATE track_analysis
   SET status = 'completed'
 WHERE track_id IN ($covered, $bare);";
            command.Parameters.AddWithValue("$covered", coveredTrack);
            command.Parameters.AddWithValue("$bare", bareTrack);
            await command.ExecuteNonQueryAsync();
        }

        var coverage = await _repository.GetSonicCoverageAsync(
            libraryId, ModelId, ModelVersion, EmbeddingVersion);

        Assert.Equal(2, coverage.TotalTracks);
        Assert.Equal(2, coverage.TracksAnalyzed);
        Assert.Equal(1, coverage.TracksWithEmbedding);
        Assert.Equal(1, coverage.TracksUnavailable);
        Assert.Equal(50d, coverage.CoveragePercent);
    }

    [Fact]
    public async Task SourceRevision_IsPersistedForStalenessChecks()
    {
        var (libraryId, trackId) = await SeedTrackAsync("Revision");
        var size = 1234L;
        var mtime = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

        await _repository.UpsertSonicEmbeddingAsync(
            Create(trackId, libraryId, NormalizedVector(seed: 121)) with
            {
                SourceFileSize = size,
                SourceFileMtimeUtc = mtime,
            });

        var stored = await _repository.GetSonicEmbeddingAsync(trackId, ModelId, ModelVersion, EmbeddingVersion);
        Assert.NotNull(stored);
        Assert.Equal(size, stored!.SourceFileSize);
        Assert.Equal(mtime.ToUnixTimeSeconds(), stored.SourceFileMtimeUtc!.Value.ToUnixTimeSeconds());
    }

    // ---------------------------------------------------------------- helpers

    private static SonicEmbeddingDto Create(long trackId, long? libraryId, IReadOnlyList<float> vector)
        => new(
            trackId,
            libraryId,
            ModelId,
            ModelVersion,
            EmbeddingVersion,
            vector.Count,
            "mean-v1",
            "l2-v1",
            "cosine",
            vector,
            null,
            null,
            DateTimeOffset.UtcNow);

    private static IReadOnlyList<float> NormalizedVector(int seed)
    {
        var random = new Random(seed);
        var values = new float[1280];
        var magnitude = 0.0;
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = (float)((random.NextDouble() * 2.0) - 1.0);
            magnitude += (double)values[index] * values[index];
        }

        magnitude = Math.Sqrt(magnitude);
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = (float)(values[index] / magnitude);
        }

        return values;
    }

    private async Task<(long LibraryId, long TrackId)> SeedTrackAsync(string title, long? intoLibraryId = null)
    {
        var album = title + " Album";
        var artist = title + " Artist";
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();

        var trackId = 0L;
        long libraryId;
        await using (var libraryCommand = connection.CreateCommand())
        {
            libraryCommand.CommandText = @"
INSERT INTO library (id, name) VALUES ($id, $name)
ON CONFLICT(id) DO NOTHING;";
            libraryId = intoLibraryId
                ?? 9000 + Math.Abs(title.GetHashCode(StringComparison.Ordinal) % 500);
            libraryCommand.Parameters.AddWithValue("$id", libraryId);
            libraryCommand.Parameters.AddWithValue("$name", $"Sonic Test Library {libraryId}");
            await libraryCommand.ExecuteNonQueryAsync();
        }

        long artistId;
        await using (var artistCommand = connection.CreateCommand())
        {
            artistCommand.CommandText = @"
INSERT INTO artist (id, name) VALUES ($id, $name)
ON CONFLICT(id) DO NOTHING;";
            artistId = Math.Abs(HashCode.Combine(libraryId, title, "artist"));
            artistCommand.Parameters.AddWithValue("$id", artistId);
            artistCommand.Parameters.AddWithValue("$name", artist);
            await artistCommand.ExecuteNonQueryAsync();
        }

        long albumId;
        await using (var albumCommand = connection.CreateCommand())
        {
            albumCommand.CommandText = @"
INSERT INTO album (id, artist_id, title) VALUES ($id, $artistId, $title)
ON CONFLICT(id) DO NOTHING;";
            albumId = Math.Abs(HashCode.Combine(libraryId, title, "album"));
            albumCommand.Parameters.AddWithValue("$id", albumId);
            albumCommand.Parameters.AddWithValue("$artistId", artistId);
            albumCommand.Parameters.AddWithValue("$title", album);
            await albumCommand.ExecuteNonQueryAsync();
        }

        await using (var trackCommand = connection.CreateCommand())
        {
            // track has no library column; library membership is expressed by the
            // folder the audio file lives in, reached through track_local.
            trackCommand.CommandText = @"
INSERT INTO track (id, album_id, title, duration_ms)
VALUES ($id, $albumId, $title, 180000);";
            trackId = Math.Abs(HashCode.Combine(libraryId, title, "track"));
            if (trackId == 0)
            {
                trackId = 1;
            }

            trackCommand.Parameters.AddWithValue("$id", trackId);
            trackCommand.Parameters.AddWithValue("$albumId", albumId);
            trackCommand.Parameters.AddWithValue("$title", title);
            await trackCommand.ExecuteNonQueryAsync();
        }

        var folderId = Math.Abs(HashCode.Combine(libraryId, "folder"));
        await using (var folderCommand = connection.CreateCommand())
        {
            folderCommand.CommandText = @"
INSERT INTO folder (id, root_path, display_name, library_id)
VALUES ($id, $root, $display, $libraryId)
ON CONFLICT(id) DO NOTHING;";
            folderCommand.Parameters.AddWithValue("$id", folderId);
            folderCommand.Parameters.AddWithValue("$root", $"/music/{libraryId}");
            folderCommand.Parameters.AddWithValue("$display", "Sonic Test Folder");
            folderCommand.Parameters.AddWithValue("$libraryId", libraryId);
            await folderCommand.ExecuteNonQueryAsync();
        }

        var audioFileId = Math.Abs(HashCode.Combine(libraryId, title, "audio"));
        await using (var audioFileCommand = connection.CreateCommand())
        {
            audioFileCommand.CommandText = @"
INSERT INTO audio_file (id, path, relative_path, folder_id, size, mtime, duration_ms)
VALUES ($id, $path, $relative, $folderId, 4096, $mtime, 180000)
ON CONFLICT(id) DO NOTHING;";
            audioFileCommand.Parameters.AddWithValue("$id", audioFileId);
            audioFileCommand.Parameters.AddWithValue("$path", $"/music/{libraryId}/{artist}/{album}/{title}.flac");
            audioFileCommand.Parameters.AddWithValue("$relative", $"{artist}/{album}/{title}.flac");
            audioFileCommand.Parameters.AddWithValue("$folderId", folderId);
            audioFileCommand.Parameters.AddWithValue("$mtime", "2026-01-01T00:00:00Z");
            await audioFileCommand.ExecuteNonQueryAsync();
        }

        await using (var localCommand = connection.CreateCommand())
        {
            localCommand.CommandText = @"
INSERT INTO track_local (track_id, audio_file_id) VALUES ($trackId, $audioFileId)
ON CONFLICT(track_id, audio_file_id) DO NOTHING;";
            localCommand.Parameters.AddWithValue("$trackId", trackId);
            localCommand.Parameters.AddWithValue("$audioFileId", audioFileId);
            await localCommand.ExecuteNonQueryAsync();
        }

        await using (var analysisCommand = connection.CreateCommand())
        {
            analysisCommand.CommandText = @"
INSERT INTO track_analysis (track_id, library_id, status) VALUES ($trackId, $libraryId, 'pending')
ON CONFLICT(track_id) DO NOTHING;";
            analysisCommand.Parameters.AddWithValue("$trackId", trackId);
            analysisCommand.Parameters.AddWithValue("$libraryId", libraryId);
            await analysisCommand.ExecuteNonQueryAsync();
        }

        return (libraryId, trackId);
    }
}
