using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Library.Dj;
using DeezSpoTag.Services.Library.Sonic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Phase 10: the batch pairwise primitive, and the measurement that showed it was
/// needed.
///
/// <para>Phase 10 was deferred on the premise that exact search was fast enough. It is
/// — but only if pairwise distances are resolved in a batch. Called one at a time,
/// each call revalidates the index with a database query, so measuring one 40-track
/// playlist cost 780 round trips and ran twenty to four hundred times slower than the
/// arithmetic it wrapped.</para>
///
/// <para>These tests pin the batch primitive's behaviour. The timing comparison lives in
/// the benchmark, because the claim is about cost and a unit test cannot make it.</para>
/// </summary>
public sealed class SonicPairDistanceTest : IAsyncLifetime
{
    private const long LibraryId = 4_400;

    private string _tempRoot = string.Empty;
    private string _dbPath = string.Empty;
    private LibraryRepository _repository = default!;
    private ISonicSimilarityService _service = default!;
    private Microsoft.Extensions.DependencyInjection.ServiceProvider _provider = default!;

    /// <summary>
    /// Resolved through the public interface and the host's own extension method,
    /// rather than by naming the internal concrete type. The wiring under test is the
    /// one the application uses.
    /// </summary>
    private ISonicSimilarityService NewService()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton(_repository);
        services.AddSingleton(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Library"] = $"Data Source={_dbPath}",
                })
                .Build());
        services.AddSonicSimilarity();
        var provider = services.BuildServiceProvider();
        _provider = provider;
        return provider.GetRequiredService<ISonicSimilarityService>();
    }

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-pairs-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
        _dbPath = Path.Join(_tempRoot, "library.db");

        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = $"Data Source={_dbPath}",
            })
            .Build();

        var dbService = new LibraryDbService(configuration, Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryDbService>.Instance);
        await dbService.EnsureSchemaAsync();
        _repository = new LibraryRepository(configuration, Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryRepository>.Instance);

        await SeedAsync();
        _service = NewService();
    }

    public Task DisposeAsync()
    {
        _provider?.Dispose();
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
    public async Task AnEmptyPairListNeedsNoIndexAndReturnsNothing()
    {
        Assert.Empty(await _service.PairDistancesAsync(LibraryId, Array.Empty<(long, long)>()));
        Assert.Empty(await _service.PairDistancesAsync(LibraryId, null!));
    }

    [Fact]
    public async Task ADistanceIsReturnedForAPairOfEmbeddedTracks()
    {
        // Unit vectors built from fixed axes, so the expected cosine is exact rather
        // than approximately right.
        await EmbedAsync(1, Vector(0.0d, 1.0d));
        await EmbedAsync(2, Vector(1.0d, 0.0d));

        var result = await _service.PairDistancesAsync(LibraryId, new[] { (1L, 2L) });

        var pair = Assert.Single(result);
        Assert.Equal(1, pair.TrackA);
        Assert.Equal(2, pair.TrackB);
        Assert.Equal(1d, pair.Distance, 4);
    }

    [Fact]
    public async Task ADistanceOfZeroMeansTheTwoTracksAreIdentical()
    {
        await EmbedAsync(1, Vector(0.0d, 1.0d));
        await EmbedAsync(2, Vector(0.0d, 1.0d));

        var pair = Assert.Single(await _service.PairDistancesAsync(LibraryId, new[] { (1L, 2L) }));

        Assert.Equal(0d, pair.Distance, 4);
    }

    [Fact]
    public async Task ADistanceOfOneMeansTheTwoTracksAreUnrelated()
    {
        // Orthogonal in a two-dimensional space, which is the furthest two unit vectors
        // can be from each other.
        await EmbedAsync(1, Vector(0.6d, 0.8d));
        await EmbedAsync(2, Vector(-0.8d, 0.6d));

        var pair = Assert.Single(await _service.PairDistancesAsync(LibraryId, new[] { (1L, 2L) }));

        Assert.Equal(1d, pair.Distance, 4);
    }

    [Fact]
    public async Task APairWithAnUnembeddedTrackIsOmittedRatherThanReportedAsZero()
    {
        // "Not comparable" and "identical" are different answers, and a caller that
        // measures the spread of a playlist must not treat a missing track as a perfect
        // match.
        await EmbedAsync(1, Vector(0.0d, 1.0d));
        await EmbedAsync(2, Vector(0.0d, 1.0d));

        var result = await _service.PairDistancesAsync(LibraryId, new[] { (1L, 2L), (1L, 999L) });

        Assert.Single(result);
        Assert.DoesNotContain(result, pair => pair.TrackB == 999L);
    }

    [Fact]
    public async Task APairOfTwoMissingTracksIsOmitted()
    {
        var result = await _service.PairDistancesAsync(LibraryId, new[] { (500L, 600L) });

        Assert.Empty(result);
    }

    [Fact]
    public async Task ABatchReturnsEveryComparablePairInTheOrderAsked()
    {
        await EmbedAsync(1, Vector(0.0d, 1.0d));
        await EmbedAsync(2, Vector(1.0d, 0.0d));
        await EmbedAsync(3, Vector(0.0d, -1.0d));

        var result = await _service.PairDistancesAsync(LibraryId, new[]
        {
            (2L, 3L),
            (1L, 2L),
            (1L, 3L),
        });

        Assert.Equal(3, result.Count);
        Assert.Equal((2L, 3L), (result[0].TrackA, result[0].TrackB));
        Assert.Equal((1L, 2L), (result[1].TrackA, result[1].TrackB));
        Assert.Equal((1L, 3L), (result[2].TrackA, result[2].TrackB));
    }

    [Fact]
    public async Task ABatchAgreesWithTheSinglePairCall()
    {
        // The batch is an optimisation, not a different definition of distance. If it
        // ever diverges, every measurement taken with it is wrong in a way nothing else
        // would catch.
        await EmbedAsync(1, Vector(0.3d, 0.9d));
        await EmbedAsync(2, Vector(0.8d, 0.5d));
        await EmbedAsync(3, Vector(0.2d, 0.1d));

        var pairs = new[] { (1L, 2L), (2L, 3L), (1L, 3L) };
        var batched = await _service.PairDistancesAsync(LibraryId, pairs);

        for (var index = 0; index < pairs.Length; index++)
        {
            var single = await _service.SimilarityAsync(LibraryId, pairs[index].Item1, pairs[index].Item2);
            Assert.NotNull(single);
            Assert.Equal(1d - single!.Value, batched[index].Distance, 4);
        }
    }

    [Fact]
    public async Task ABatchIsUnaffectedByAPairRepeating()
    {
        // A caller building pairs naively can emit the same pair twice, and the result
        // must still be correct rather than double-counted.
        await EmbedAsync(1, Vector(0.0d, 1.0d));
        await EmbedAsync(2, Vector(1.0d, 0.0d));

        var result = await _service.PairDistancesAsync(LibraryId, new[] { (1L, 2L), (1L, 2L) });

        Assert.Equal(2, result.Count);
        Assert.All(result, pair => Assert.Equal(1d, pair.Distance, 4));
    }

    [Fact]
    public async Task ADistanceIsAlwaysInTheUnitRange()
    {
        // Cosine similarity of normalised vectors drifts slightly outside [-1, 1], and a
        // negative distance would corrupt every average taken over it.
        await EmbedAsync(1, Vector(0.3d, 0.95d));
        await EmbedAsync(2, Vector(0.31d, 0.94d));
        await EmbedAsync(3, Vector(-0.2d, -0.9d));

        var result = await _service.PairDistancesAsync(LibraryId, new[]
        {
            (1L, 2L),
            (1L, 3L),
            (2L, 3L),
        });

        Assert.NotEmpty(result);
        Assert.All(result, pair => Assert.InRange(pair.Distance, 0d, 2d));
    }

    [Fact]
    public async Task TracksFromAnotherLibraryAreNotCompared()
    {
        // The library boundary is what stops a DJ reading another library's vectors.
        await EmbedAsync(1, Vector(0.0d, 1.0d), libraryId: LibraryId);
        await EmbedAsync(2, Vector(0.0d, 1.0d), libraryId: 4_401);

        Assert.Empty(await _service.PairDistancesAsync(LibraryId, new[] { (1L, 2L) }));
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// A two-component vector standing in for the real 1280 dimensions.
    ///
    /// <para>Fixed-axis vectors make cosine expectations exact: two of them at
    /// right angles are a distance of exactly 1, and two identical ones are exactly 0.
    /// A random vector would only be approximately either.</para>
    /// </summary>
    private static float[] Vector(double x, double y)
    {
        var vector = new float[1280];
        vector[0] = (float)x;
        vector[1] = (float)y;
        var norm = Math.Sqrt(x * x + y * y);
        vector[0] = (float)(x / norm);
        vector[1] = (float)(y / norm);
        return vector;
    }

    private async Task EmbedAsync(long trackId, float[] vector, long libraryId = LibraryId)
    {
        // The index only loads vectors matching the current model identity, so an
        // embedding written under a different identity is invisible to it — and a test
        // using its own identity would measure an empty index and pass for the wrong
        // reason.
        await _repository.UpsertSonicEmbeddingAsync(new SonicEmbeddingDto(
            trackId,
            libraryId,
            SonicModelIdentity.Current.ModelId,
            SonicModelIdentity.Current.ModelVersion,
            SonicModelIdentity.Current.EmbeddingVersion,
            1280,
            "mean-v1",
            "l2-v1",
            "cosine",
            vector,
            1000,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z", null, System.Globalization.DateTimeStyles.RoundtripKind),
            DateTimeOffset.UtcNow));
    }

    private async Task SeedAsync()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = @"
INSERT OR IGNORE INTO library (id, name) VALUES ($id, $name);";
            command.Parameters.AddWithValue("$id", LibraryId);
            command.Parameters.AddWithValue("$name", "Pair Library");
            await command.ExecuteNonQueryAsync();
        }

        await using (var command = connection.CreateCommand())
        {
            command.Parameters.AddWithValue("$id", 4_401);
            command.Parameters.AddWithValue("$name", "Other Library");
            command.CommandText = "INSERT OR IGNORE INTO library (id, name) VALUES ($id, $name);";
            await command.ExecuteNonQueryAsync();
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = @"
INSERT OR IGNORE INTO artist (id, name) VALUES (1, 'Artist');
INSERT OR IGNORE INTO album (id, artist_id, title) VALUES (1, 1, 'Album');
INSERT OR IGNORE INTO folder (id, root_path, display_name, library_id) VALUES (1, '/pairs', 'Pairs', $libraryId);";
            command.Parameters.AddWithValue("$libraryId", LibraryId);
            await command.ExecuteNonQueryAsync();
        }

        // track_sonic_embedding references track, so a track row has to exist before an
        // embedding can be written. Only the tracks these tests embed are needed.
        foreach (var trackId in new long[] { 1, 2, 3 })
        {
            await using var track = connection.CreateCommand();
            track.CommandText = "INSERT OR IGNORE INTO track (id, album_id, title, duration_ms) VALUES ($id, 1, $title, 180000);";
            track.Parameters.AddWithValue("$id", trackId);
            track.Parameters.AddWithValue("$title", "Track " + trackId);
            await track.ExecuteNonQueryAsync();

            await using var file = connection.CreateCommand();
            file.CommandText = @"
INSERT OR IGNORE INTO audio_file (id, path, relative_path, folder_id, size, mtime, duration_ms)
VALUES ($id, $path, $relative, 1, 1000, '2026-01-01 00:00:00', 180000);";
            file.Parameters.AddWithValue("$id", trackId);
            file.Parameters.AddWithValue("$path", $"/pairs/{trackId}.flac");
            file.Parameters.AddWithValue("$relative", $"{trackId}.flac");
            await file.ExecuteNonQueryAsync();

            // After the audio file: track_local references both, and a link row
            // inserted before its file fails the foreign key.
            await using var local = connection.CreateCommand();
            local.CommandText = "INSERT OR IGNORE INTO track_local (track_id, audio_file_id) VALUES ($trackId, $trackId);";
            local.Parameters.AddWithValue("$trackId", trackId);
            await local.ExecuteNonQueryAsync();
        }
    }
}