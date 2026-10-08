using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Library.Sonic;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Phase 2 unit tests for the vector math and the schema upgrade path.
///
/// Two things here have no other coverage. The math is the numerical core every
/// ranking depends on, so it is tested directly rather than only through the
/// service. And the Sonic table has to appear in an existing installation that
/// was created before Sonic existed, which is a different path from a fresh
/// database.
/// </summary>
public sealed class SonicVectorMathTest
{
    [Fact]
    public void IdenticalVectors_AreExactlyOne()
    {
        var vector = Normalized(11);
        Assert.Equal(1.0d, SonicVectorMath.Similarity(vector, vector), 6);
    }

    [Fact]
    public void OppositeVectors_AreExactlyMinusOne()
    {
        var vector = Normalized(12);
        var opposite = vector.Select(value => -value).ToArray();
        Assert.Equal(-1.0d, SonicVectorMath.Similarity(vector, opposite), 6);
    }

    [Fact]
    public void SimilarityIsSymmetric()
    {
        var left = Normalized(13);
        var right = Normalized(14);
        Assert.Equal(
            SonicVectorMath.Similarity(left, right),
            SonicVectorMath.Similarity(right, left),
            12);
    }

    [Fact]
    public void SelfComparisonNeverDriftsPastOne()
    {
        // A self-comparison that returns marginally more than 1 would produce a
        // negative distance, which would then sort above a genuine perfect match.
        for (var seed = 0; seed < 25; seed++)
        {
            var vector = Normalized(seed);
            var similarity = SonicVectorMath.Similarity(vector, vector);
            Assert.True(similarity <= 1.0d, $"seed {seed} produced {similarity}");
        }
    }

    [Fact]
    public void NonFiniteVectorsScoreZeroRatherThanNaN()
    {
        var good = Normalized(15);
        var withNaN = good.ToArray();
        withNaN[3] = float.NaN;
        var withInfinity = good.ToArray();
        withInfinity[9] = float.NegativeInfinity;

        // NaN would propagate into every ranking that touched it. Zero is
        // deliberately the least-committal answer: unusable, never a match.
        Assert.Equal(0d, SonicVectorMath.Similarity(withNaN, good), 6);
        Assert.Equal(0d, SonicVectorMath.Similarity(good, withNaN), 6);
        Assert.Equal(0d, SonicVectorMath.Similarity(withInfinity, good), 6);
        Assert.Equal(0d, SonicVectorMath.Similarity(good, withInfinity), 6);
    }

    [Fact]
    public void EmptyVectorsScoreZero()
    {
        Assert.Equal(0d, SonicVectorMath.Similarity(Array.Empty<float>(), Array.Empty<float>()), 6);
        Assert.Equal(0d, SonicVectorMath.Similarity(Normalized(16), Array.Empty<float>()), 6);
    }

    [Fact]
    public void MismatchedWidthsScoreZeroInsteadOfThrowing()
    {
        var vector = Normalized(17);
        Assert.Equal(0d, SonicVectorMath.Similarity(vector, vector.Take(10).ToArray()), 6);
    }

    [Theory]
    [InlineData(0, 1280, false)]
    [InlineData(10, 1280, false)]
    [InlineData(1280, 1280, true)]
    [InlineData(1279, 1280, false)]
    [InlineData(2560, 1280, false)]
    public void IsUsable_RequiresExactWidthAndFiniteValues(int count, int declared, bool expected)
    {
        var vector = new float[count];
        for (var index = 0; index < count; index++)
        {
            vector[index] = 0.5f;
        }

        Assert.Equal(expected, SonicVectorMath.IsUsable(vector, declared));
    }

    [Fact]
    public void IsUsable_RejectsNaN()
    {
        var vector = new float[1280];
        vector[17] = float.NaN;
        Assert.False(SonicVectorMath.IsUsable(vector, 1280));
    }

    [Fact]
    public void DistanceIsOneMinusSimilarity()
    {
        var match = new SonicSimilarityMatch(1, 0.8d, 1);
        Assert.Equal(0.2d, match.Distance, 10);
    }

    [Fact]
    public void ArrayFastPathAndInterfacePathAgree()
    {
        // The index scores through concrete float[] arrays while the public
        // contract is IReadOnlyList<float>. Two implementations of the same dot
        // product is a real divergence risk, and a silent one would only surface
        // as subtly wrong rankings much later.
        for (var seed = 0; seed < 20; seed++)
        {
            var left = Normalized(seed);
            var right = Normalized(seed + 100);

            var viaArray = SonicVectorMath.Similarity(left, right);
            var viaInterface = SonicVectorMath.Similarity(
                (IReadOnlyList<float>)left,
                (IReadOnlyList<float>)right);

            Assert.Equal(viaArray, viaInterface, 5);
        }
    }

    [Fact]
    public void ArrayFastPathIsSelectedForArrayInputs()
    {
        // Guards the optimisation itself. The four-accumulator loop is what took a
        // 50-seed query against 50k vectors from 78s to 6.6s; without this the
        // interface walk would silently take over again and the cost would return
        // with nothing failing.
        var source = File.ReadAllText(Path.Join(
            ResolveRepoRoot(), "DeezSpoTag.Services", "Library", "Sonic", "SonicSimilarityIndex.cs"));

        Assert.Contains(
            "internal static double Similarity(float[] left, float[] right)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (left is float[] leftArray && right is float[] rightArray)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "float a0 = 0f, a1 = 0f, a2 = 0f, a3 = 0f",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FastPathStillRejectsMismatchedAndNonFiniteInput()
    {
        // The optimisation must not have relaxed the safety checks.
        var good = Normalized(21);
        var shortVector = good.Take(64).ToArray();
        Assert.Equal(0d, SonicVectorMath.Similarity(good, shortVector), 6);

        var withNaN = good.ToArray();
        withNaN[0] = float.NaN;
        Assert.Equal(0d, SonicVectorMath.Similarity(withNaN, good), 6);
        Assert.Equal(0d, SonicVectorMath.Similarity(good, withNaN), 6);

        Assert.Equal(0d, SonicVectorMath.Similarity(Array.Empty<float>(), Array.Empty<float>()), 6);
    }

    [Fact]
    public void FastPathHandlesWidthsThatAreNotAMultipleOfFour()
    {
        // The tail loop is what keeps short widths correct; without it the last
        // elements of the vector would be silently ignored.
        var left = new float[6];
        var right = new float[6];
        for (var index = 0; index < left.Length; index++)
        {
            left[index] = 1f;
            right[index] = 1f;
        }

        // Unit vectors, so the dot product is 1 and the cosine is 1.
        Assert.Equal(1.0d, SonicVectorMath.Similarity(left, right), 5);
    }

    private static string ResolveRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Join(directory.FullName, "DeezSpoTag.Web")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private static float[] Normalized(int seed)
    {
        var random = new Random(seed);
        var values = new float[1280];
        double magnitude = 0.0;
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
}

/// <summary>
/// The Sonic table has to reach an installation that predates it. This opens a
/// database without the table, runs the normal schema ensure, and proves the
/// table and its indexes are created and usable.
/// </summary>
public sealed class SonicSchemaUpgradeTest
{
    [Fact]
    public async Task SchemaUpgrade_CreatesTheSonicTableOnAPreExistingDatabase()
    {
        var root = Path.Join(Path.GetTempPath(), "deezspotag-sonic-upgrade-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            var dbPath = Path.Join(root, "library.db");
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Library"] = $"Data Source={dbPath}"
                })
                .Build();

            // Stand up a realistic pre-Sonic database: current schema, then drop
            // the Sonic table so the upgrade has something real to repair.
            var dbService = new LibraryDbService(configuration, NullLogger<LibraryDbService>.Instance);
            await dbService.EnsureSchemaAsync();

            await using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                await connection.OpenAsync();
                await using var drop = connection.CreateCommand();
                drop.CommandText = "DROP TABLE IF EXISTS track_sonic_embedding;";
                await drop.ExecuteNonQueryAsync();
            }

            // Re-running the migration is what a restarting application does.
            await dbService.EnsureSchemaAsync();

            await using var verify = new SqliteConnection($"Data Source={dbPath}");
            await verify.OpenAsync();

            await using var exists = verify.CreateCommand();
            exists.CommandText = @"
SELECT COUNT(*) FROM sqlite_master
 WHERE type = 'table' AND name = 'track_sonic_embedding';";
            Assert.Equal(1L, (long)(await exists.ExecuteScalarAsync())!);

            await using var indexes = verify.CreateCommand();
            indexes.CommandText = @"
SELECT COUNT(*) FROM sqlite_master
 WHERE type = 'index'
   AND name IN ('idx_track_sonic_embedding_library', 'idx_track_sonic_embedding_analyzed');";
            Assert.Equal(2L, (long)(await indexes.ExecuteScalarAsync())!);

            // And the table is genuinely writable afterwards.
            var repository = new LibraryRepository(configuration, NullLogger<LibraryRepository>.Instance);
            var libraryId = await SeedLibraryAsync(verify);
            var trackId = await SeedTrackAsync(verify, libraryId);

            var vector = new float[1280];
            for (var index = 0; index < vector.Length; index++)
            {
                vector[index] = 1f / 40f;
            }

            await repository.UpsertSonicEmbeddingAsync(new SonicEmbeddingDto(
                trackId, libraryId,
                SonicModelIdentity.Current.ModelId,
                SonicModelIdentity.Current.ModelVersion,
                SonicModelIdentity.Current.EmbeddingVersion,
                vector.Length, "mean-v1", "l2-v1", "cosine",
                vector, 4096, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));

            var stored = await repository.GetSonicEmbeddingAsync(
                trackId,
                SonicModelIdentity.Current.ModelId,
                SonicModelIdentity.Current.ModelVersion,
                SonicModelIdentity.Current.EmbeddingVersion);
            Assert.NotNull(stored);
            Assert.Equal(1280, stored!.Dimensions);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    private static async Task<long> SeedLibraryAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO library (id, name) VALUES (77001, 'Upgrade Library');";
        await command.ExecuteNonQueryAsync();
        return 77001;
    }

    private static async Task<long> SeedTrackAsync(SqliteConnection connection, long libraryId)
    {
        await using (var artist = connection.CreateCommand())
        {
            artist.CommandText = "INSERT INTO artist (id, name) VALUES (77011, 'Upgrade Artist');";
            await artist.ExecuteNonQueryAsync();
        }

        await using (var album = connection.CreateCommand())
        {
            album.CommandText = "INSERT INTO album (id, artist_id, title) VALUES (77021, 77011, 'Upgrade Album');";
            await album.ExecuteNonQueryAsync();
        }

        await using (var track = connection.CreateCommand())
        {
            track.CommandText = "INSERT INTO track (id, album_id, title, duration_ms) VALUES (77031, 77021, 'Upgrade Track', 180000);";
            await track.ExecuteNonQueryAsync();
        }

        await using (var folder = connection.CreateCommand())
        {
            folder.CommandText = "INSERT INTO folder (id, root_path, display_name, library_id) VALUES (77041, '/upgrade', 'Upgrade', $libraryId);";
            folder.Parameters.AddWithValue("$libraryId", libraryId);
            await folder.ExecuteNonQueryAsync();
        }

        await using (var file = connection.CreateCommand())
        {
            file.CommandText = @"
INSERT INTO audio_file (id, path, relative_path, folder_id, size, mtime, duration_ms)
VALUES (77051, '/upgrade/t.flac', 't.flac', 77041, 4096, '2026-01-01T00:00:00Z', 180000);";
            await file.ExecuteNonQueryAsync();
        }

        await using (var local = connection.CreateCommand())
        {
            local.CommandText = "INSERT INTO track_local (track_id, audio_file_id) VALUES (77031, 77051);";
            await local.ExecuteNonQueryAsync();
        }

        return 77031;
    }
}
