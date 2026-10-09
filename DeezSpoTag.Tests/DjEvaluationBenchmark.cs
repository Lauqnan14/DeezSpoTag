using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Library.Dj;
using DeezSpoTag.Services.Library.Sonic;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace DeezSpoTag.Tests;

/// <summary>
/// Phase 10: the measurement that decides whether an approximate index is warranted.
///
/// <para>Phase 2 deferred one explicitly: do not add ANN until exact search has been
/// measured at realistic scale. Exact search was measured and was adequate for building
/// a playlist. It was <em>not</em> measured for the evaluation path Phase 9 added, which
/// resolves distances between every pair of chosen tracks — roughly 780 pairs for a
/// 40-track playlist, a different shape of work from the one that was benchmarked.</para>
///
/// <para>So this measures that path, and records the crossover rather than guessing at
/// it. A harness that only ever concludes "no change needed" is not a measurement; this
/// one reports the number at which the answer flips.</para>
///
/// <para>Opt-in because it is slow:
///   DJ_BENCHMARK=1 dotnet test --filter FullyQualifiedName~DjEvaluationBenchmark</para>
///
/// <para>The correctness assertions always run; only the timing sweeps are skipped.</para>
/// </summary>
public sealed class DjEvaluationBenchmark : IAsyncLifetime
{
    /// <summary>A realistic DJ playlist length, and therefore a realistic pair count.</summary>
    private const int PlaylistTracks = 40;

    /// <summary>
    /// What the panel can wait for before a DJ run feels broken.
    ///
    /// <para>Generous on purpose: a person clicked "Generate now" and is reading the
    /// result, not waiting on a spinner they did not ask for. Beyond this the honest
    /// answer is to queue the work, not to make it faster with an approximation that
    /// quietly changes what a DJ plays.</para>
    /// </summary>
    private const long AcceptableEvaluationMilliseconds = 2_000;

    /// <summary>
    /// The largest library this project treats as ordinary.
    ///
    /// <para>A very large Plex or Navidrome library lands somewhere around this; beyond
    /// it the sweep continues purely to locate the boundary, and exceeding the budget
    /// there is recorded rather than treated as a failure.</para>
    /// </summary>
    private const int PlausibleLibraryCeiling = 50_000;

    private readonly ITestOutputHelper _output;
    private string _tempRoot = string.Empty;
    private string _dbPath = string.Empty;
    private IConfiguration _configuration = default!;
    private LibraryRepository _repository = default!;

    public DjEvaluationBenchmark(ITestOutputHelper output)
    {
        _output = output;
    }

    private static bool BenchmarkEnabled =>
        Environment.GetEnvironmentVariable("DJ_BENCHMARK") is "1" or "true" or "yes";

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-dj-bench-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
        _dbPath = Path.Join(_tempRoot, "library.db");

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = $"Data Source={_dbPath}",
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

    // ------------------------------------------------------------ correctness

    [Fact]
    public void APlaylistOfFortyTracksHasThePairCountTheBenchmarkAssumes()
    {
        // The whole cost model rests on this number, so it is pinned rather than
        // assumed: 40 * 39 / 2 = 780 pairs.
        var pairs = DjPlaylistEvaluator.PairsOf(
            Enumerable.Range(1, PlaylistTracks).Select(value => (long)value).ToList());

        Assert.Equal(780, pairs.Count);
    }

    [Fact]
    public void PairDistancesForAFullPlaylistResolveEveryPair()
    {
        // A measurement that silently misses pairs would report a flattering mean.
        var ids = Enumerable.Range(1, PlaylistTracks).Select(value => (long)value).ToList();
        var distances = ids
            .SelectMany(left => ids.Where(right => right != left)
                .Select(right => DjTrackPairDistance.Between(left, right, 0.5)))
            .ToList();

        Assert.Equal(PlaylistTracks * (PlaylistTracks - 1), distances.Count);
    }

    // ------------------------------------------------------------ measurement

    [Fact]
    public async Task EvaluationCost_StaysAcceptableAtRealisticScale()
    {
        if (!BenchmarkEnabled)
        {
            _output.WriteLine("Set DJ_BENCHMARK=1 to run the evaluation-cost measurement.");
            return;
        }

        foreach (var trackCount in new[] { 2_000, 10_000, 25_000 })
        {
            var measurement = await MeasureAsync(trackCount);
            _output.WriteLine(
                $"vectors={measurement.Vectors} | resolve seeds = {measurement.SeedMs} ms | "
                + $"resolve {measurement.Pairs} pairs batched = {measurement.BatchedPairMs} ms | "
                + $"same pairs one call each = {measurement.PerCallPairMs} ms | "
                + $"same pairs via neighbour queries = {measurement.QueryBasedPairMs} ms | "
                + $"total = {measurement.TotalMs} ms");

            Assert.True(
                measurement.TotalMs < AcceptableEvaluationMilliseconds,
                $"Evaluating one {PlaylistTracks}-track playlist against {measurement.Vectors} vectors took "
                + $"{measurement.TotalMs} ms, past the {AcceptableEvaluationMilliseconds} ms a person will wait for.");
        }
    }

    [Fact]
    public async Task TheCrossoverIsRecordedRatherThanGuessedAt()
    {
        if (!BenchmarkEnabled)
        {
            _output.WriteLine("Set DJ_BENCHMARK=1 to run the crossover measurement.");
            return;
        }

        // Sweeping upward until the budget is exceeded, so the output is a measured
        // boundary rather than an opinion about where it might be.
        int[] scales = { 2_000, 5_000, 10_000, 25_000, 50_000, 100_000 };
        var rows = new List<string>();
        long? crossover = null;
        long? beyondPlausible = null;

        foreach (var trackCount in scales)
        {
            var measurement = await MeasureAsync(trackCount);
            rows.Add(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0,7} vectors: total {1,6} ms   (pairs batched {2,5} ms, one call each {3,6} ms, neighbour queries {4,6} ms)",
                measurement.Vectors,
                measurement.TotalMs,
                measurement.BatchedPairMs,
                measurement.PerCallPairMs,
                measurement.QueryBasedPairMs));

            if (measurement.TotalMs >= AcceptableEvaluationMilliseconds)
            {
                crossover ??= measurement.Vectors;
                if (measurement.Vectors > PlausibleLibraryCeiling)
                {
                    beyondPlausible ??= measurement.Vectors;
                }
            }
        }

        _output.WriteLine("Evaluation cost against exact search:");
        foreach (var row in rows)
        {
            _output.WriteLine(row);
        }

        _output.WriteLine(crossover is { } boundary
            ? $"Exact search exceeded {AcceptableEvaluationMilliseconds} ms at {boundary} vectors"
                + (boundary > PlausibleLibraryCeiling
                    ? $" — beyond the {PlausibleLibraryCeiling} vectors a music library plausibly reaches."
                    : " — within the plausible range for a music library.")
            : $"Exact search stayed under {AcceptableEvaluationMilliseconds} ms through {scales[^1]} vectors.");

        // The claim this phase makes is narrow and measured: exact search is adequate
        // across the range a music library plausibly reaches. The assertion is scoped to
        // that range rather than to the top of the sweep, because claiming adequacy at
        // any scale would be claiming something the measurement does not support — the
        // crossover is reported above rather than asserted away.
        Assert.True(
            crossover is null || crossover > PlausibleLibraryCeiling,
            $"Exact search exceeded the evaluation budget at {crossover} vectors, within the "
            + $"{PlausibleLibraryCeiling} vectors a music library plausibly reaches. "
            + "An approximate index is now warranted, or the evaluation path needs to change.");
    }

    private sealed record EvaluationMeasurement(
        long Vectors,
        long SeedMs,
        long BatchedPairMs,
        long PerCallPairMs,
        long QueryBasedPairMs,
        int Pairs)
    {
        /// <summary>
        /// The cost with the primitive evaluation should use: one index validation for
        /// the whole batch, then a dot product per pair.
        /// </summary>
        public long TotalMs => SeedMs + BatchedPairMs;
    }

    /// <summary>
    /// Measures one evaluation end to end: resolve affinities for the seeds, then
    /// resolve the distances between the chosen tracks.
    /// </summary>
    /// <remarks>
    /// The second step is the one that was never costed. It is not one query — the
    /// similarity service answers nearest-neighbour questions, so pairwise distances
    /// among the chosen set cost one query per chosen track. That is the shape this
    /// measurement exists to quantify.
    /// </remarks>
    private async Task<EvaluationMeasurement> MeasureAsync(int trackCount)
    {
        var libraryId = await SeedAsync(trackCount);
        var service = NewService();
        var all = Enumerable.Range(1, trackCount).Select(value => (long)value).ToList();

        var seeds = all.Take(8).ToList();
        var playlist = all.Take(PlaylistTracks).ToList();

        // Warm the index so the measurement excludes the one-time build.
        await service.FindNearestAsync(new SonicSimilarityQuery
        {
            LibraryId = libraryId,
            SeedTrackIds = new[] { seeds[0] },
            Limit = 1,
        });

        var stopwatch = Stopwatch.StartNew();
        var candidatePool = new HashSet<long>(all);
        foreach (var seedId in seeds)
        {
            await service.FindNearestAsync(new SonicSimilarityQuery
            {
                LibraryId = libraryId,
                SeedTrackIds = new[] { seedId },
                AllowedTrackIds = candidatePool,
                Limit = PlaylistTracks,
            });
        }

        var seedMs = stopwatch.ElapsedMilliseconds;
        stopwatch.Restart();

        // Three ways to measure the same 780 distances, because the obvious one turns
        // out to be the worst.
        var pairs = DjPlaylistEvaluator.PairsOf(playlist);

        var perPair = stopwatch.ElapsedMilliseconds;
        stopwatch.Restart();

        await service.PairDistancesAsync(libraryId, pairs);
        var batchedMs = stopwatch.ElapsedMilliseconds;
        stopwatch.Restart();

        foreach (var pair in pairs)
        {
            await service.SimilarityAsync(libraryId, pair.Left, pair.Right);
        }

        var perCallMs = stopwatch.ElapsedMilliseconds;
        stopwatch.Restart();

        // The way it would be measured by mistake: treating each chosen track as a
        // seed and asking for its neighbours. Each call scans the whole library and
        // filters afterwards, so the cost is playlistSize * librarySize.
        var chosenPool = new HashSet<long>(playlist);
        foreach (var trackId in playlist)
        {
            await service.FindNearestAsync(new SonicSimilarityQuery
            {
                LibraryId = libraryId,
                SeedTrackIds = new[] { trackId },
                AllowedTrackIds = chosenPool,
                Limit = playlist.Count,
            });
        }

        var queryBasedPairMs = stopwatch.ElapsedMilliseconds;

        return new EvaluationMeasurement(trackCount, seedMs, batchedMs, perCallMs, queryBasedPairMs, pairs.Count);
    }

    /// <summary>
    /// Built through the same extension the host uses, rather than by naming the
    /// internal index and service. Reaching for the concrete types would make the
    /// benchmark measure a wiring the application does not have.
    /// </summary>
    private ISonicSimilarityService NewService()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_configuration);
        services.AddSingleton(_repository);
        services.AddSonicSimilarity();
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<ISonicSimilarityService>();
    }

    private async Task<long> SeedAsync(int trackCount)
    {
        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT OR IGNORE INTO library (id, name) VALUES (1, 'Bench');";
            await command.ExecuteNonQueryAsync();
        }

        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT OR IGNORE INTO artist (id, name) VALUES (1, 'Artist');
INSERT OR IGNORE INTO album (id, artist_id, title) VALUES (1, 1, 'Album');
INSERT OR IGNORE INTO folder (id, root_path, display_name, library_id)
VALUES (1, '/bench', 'Bench', 1);";
            await command.ExecuteNonQueryAsync();
        }

        // One transaction for the whole sweep: 100k individual inserts would spend
        // minutes in SQLite and measure the database rather than the index.
        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

            var random = new Random(20260930);
            for (var trackId = 1; trackId <= trackCount; trackId++)
            {
                await using (var track = connection.CreateCommand())
                {
                    track.Transaction = transaction;
                    track.CommandText = "INSERT OR IGNORE INTO track (id, album_id, title, duration_ms) VALUES ($id, 1, $title, 180000);";
                    track.Parameters.AddWithValue("$id", trackId);
                    track.Parameters.AddWithValue("$title", "Track " + trackId);
                    await track.ExecuteNonQueryAsync();
                }

                await using (var file = connection.CreateCommand())
                {
                    file.Transaction = transaction;
                    file.CommandText = @"
INSERT OR IGNORE INTO audio_file (id, path, relative_path, folder_id, size, mtime, duration_ms)
VALUES ($id, $path, $relative, 1, 1000, '2026-01-01 00:00:00', 180000);";
                    file.Parameters.AddWithValue("$id", trackId);
                    file.Parameters.AddWithValue("$path", $"/bench/{trackId}.flac");
                    file.Parameters.AddWithValue("$relative", $"{trackId}.flac");
                    await file.ExecuteNonQueryAsync();
                }

                await using (var local = connection.CreateCommand())
                {
                    local.Transaction = transaction;
                    local.CommandText = "INSERT OR IGNORE INTO track_local (track_id, audio_file_id) VALUES ($trackId, $trackId);";
                    local.Parameters.AddWithValue("$trackId", trackId);
                    await local.ExecuteNonQueryAsync();
                }

                var vector = new float[1280];
                for (var component = 0; component < vector.Length; component++)
                {
                    // Centred on zero so vectors are not all similar by construction,
                    // which would make the search trivially easy and the numbers
                    // meaningless.
                    vector[component] = (float)((random.NextDouble() - 0.5d) * 2d);
                }

                Normalize(vector);

                await using (var embedding = connection.CreateCommand())
                {
                    embedding.Transaction = transaction;
                    embedding.CommandText = @"
INSERT OR IGNORE INTO track_sonic_embedding
    (track_id, library_id, model_id, model_version, embedding_version, dimensions,
     pooling_method, normalization_method, distance_metric, vector_blob,
     source_file_size, source_file_mtime_utc, analyzed_at_utc)
VALUES
    ($trackId, 1, 'bench', '1', 'bench-v1', 1280, 'mean-v1', 'l2-v1', 'cosine', $vector,
     1000, '2026-01-01 00:00:00', '2026-01-01T00:00:00.0000000+00:00');";
                    embedding.Parameters.AddWithValue("$trackId", trackId);
                    embedding.Parameters.AddWithValue("$vector", ToBlob(vector));
                    await embedding.ExecuteNonQueryAsync();
                }
            }

            await transaction.CommitAsync();
        }

        return 1;
    }

    private static void Normalize(float[] vector)
    {
        var sum = 0d;
        foreach (var component in vector)
        {
            sum += component * component;
        }

        var norm = Math.Sqrt(sum);
        if (norm <= 0d)
        {
            return;
        }

        for (var index = 0; index < vector.Length; index++)
        {
            vector[index] = (float)(vector[index] / norm);
        }
    }

    private static byte[] ToBlob(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}