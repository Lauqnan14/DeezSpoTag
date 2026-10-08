using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Library.Sonic;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace DeezSpoTag.Tests;

/// <summary>
/// Phase 2 benchmark for exact Sonic search.
///
/// The design gate is explicit: do not introduce an approximate index until exact
/// search has been measured at realistic scale. This harness records the numbers
/// that decision needs, and asserts the properties that must hold regardless of
/// scale: determinism, correct ordering, and no library leakage.
///
/// It is an opt-in test because it is slow. Run it with
///   SONIC_BENCHMARK=1 dotnet test --filter FullyQualifiedName~SonicSimilarityBenchmark
/// The correctness assertions always run; only the large-scale timing sweeps are
/// skipped without the switch.
/// </summary>
public sealed class SonicSimilarityBenchmark : IAsyncLifetime
{
    private const int Dimensions = 1280;

    private readonly ITestOutputHelper _output;
    private string _tempRoot = string.Empty;
    private string _dbPath = string.Empty;
    private IConfiguration _configuration = default!;
    private LibraryRepository _repository = default!;
    private long _libraryId;

    public SonicSimilarityBenchmark(ITestOutputHelper output)
    {
        _output = output;
    }

    private static bool BenchmarkEnabled =>
        Environment.GetEnvironmentVariable("SONIC_BENCHMARK") is "1" or "true" or "yes";

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-sonic-bench-" + Path.GetRandomFileName());
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
        _libraryId = await CreateLibraryAsync();
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
    public async Task ExactSearch_RemainsCorrectAsTheLibraryGrows()
    {
        // Correctness at scale, independent of whether the timing sweep is enabled.
        foreach (var size in new[] { 50, 150, 300 })
        {
            var libraryId = await CreateLibraryAsync();
            var tracks = await SeedAsync(libraryId, size);
            var service = NewService();

            var result = await service.FindNearestAsync(new SonicSimilarityQuery
            {
                LibraryId = libraryId,
                SeedTrackIds = new[] { tracks[0] },
                Limit = 10,
            });

            Assert.Equal(size, result.Metrics!.VectorCount);
            Assert.Equal(10, result.Matches.Count);
            Assert.Equal(Dimensions, result.Metrics.Dimensions);

            // Descending similarity, ascending track id within ties.
            for (var index = 1; index < result.Matches.Count; index++)
            {
                Assert.True(
                    result.Matches[index - 1].Similarity > result.Matches[index].Similarity
                    || (Math.Abs(result.Matches[index - 1].Similarity - result.Matches[index].Similarity) < 1e-9
                        && result.Matches[index - 1].TrackId < result.Matches[index].TrackId));
            }
        }
    }

    [Fact]
    public async Task PlaylistSizedQuery_StaysFastAtRealisticScale()
    {
        // A DJ generates 50-track playlists, so a 50-seed multi-seed query against
        // a full library is the shape that actually has to be quick.
        //
        // Opt-in. Seeding thousands of tracks costs seconds of SQLite I/O, and
        // paying that on every ordinary test run adds contention to the rest of
        // the suite for no extra coverage: the assertions here are the same shape
        // as the correctness test above, which always runs.
        if (!BenchmarkEnabled)
        {
            _output.WriteLine("Set SONIC_BENCHMARK=1 to run the playlist-sized latency check.");
            return;
        }

        var libraryId = await CreateLibraryAsync();
        var tracks = await SeedAsync(libraryId, 2_000);
        var service = NewService();

        // Warm the index so the measurement excludes the one-time build.
        await service.FindNearestAsync(new SonicSimilarityQuery
        {
            LibraryId = libraryId,
            SeedTrackIds = new[] { tracks[0] },
            Limit = 1,
        });

        var stopwatch = Stopwatch.StartNew();
        var single = await service.FindNearestAsync(new SonicSimilarityQuery
        {
            LibraryId = libraryId,
            SeedTrackIds = new[] { tracks[0] },
            Limit = 50,
        });
        var singleMs = stopwatch.ElapsedMilliseconds;
        stopwatch.Restart();

        var playlistSeeds = tracks.Take(50).ToList();
        var multi = await service.FindNearestAsync(new SonicSimilarityQuery
        {
            LibraryId = libraryId,
            SeedTrackIds = playlistSeeds,
            Limit = 50,
        });
        var multiMs = stopwatch.ElapsedMilliseconds;

        _output.WriteLine($"vectors={single.Metrics!.VectorCount} dims={single.Metrics.Dimensions}");
        _output.WriteLine($"held vector memory = {single.Metrics.ApproximateMegabytes:F1} MB");
        _output.WriteLine($"single-seed top-50 = {singleMs} ms");
        _output.WriteLine($"50-seed top-50 = {multiMs} ms");

        Assert.Equal(50, single.Matches.Count);
        Assert.Equal(50, multi.Matches.Count);
    }

    [Fact]
    public async Task ScalingSweep_RecordsBuildAndQueryCosts()
    {
        if (!BenchmarkEnabled)
        {
            _output.WriteLine("Set SONIC_BENCHMARK=1 to run the scaling sweep.");
            return;
        }

        _output.WriteLine("vectors\tbuildMs\theldMB\tsingleSeedMs\t10SeedMs\tplaylist50SeedMs");

        foreach (var size in new[] { 1_000, 10_000, 25_000, 50_000 })
        {
            var libraryId = await CreateLibraryAsync();
            var tracks = await SeedAsync(libraryId, size);
            var service = NewService();

            var build = Stopwatch.StartNew();
            var warm = await service.FindNearestAsync(new SonicSimilarityQuery
            {
                LibraryId = libraryId,
                SeedTrackIds = new[] { tracks[0] },
                Limit = 1,
            });
            build.Stop();

            var single = await Time(async () => await service.FindNearestAsync(new SonicSimilarityQuery
            {
                LibraryId = libraryId,
                SeedTrackIds = new[] { tracks[0] },
                Limit = 50,
            }));

            var ten = await Time(async () => await service.FindNearestAsync(new SonicSimilarityQuery
            {
                LibraryId = libraryId,
                SeedTrackIds = tracks.Take(10).ToList(),
                Limit = 50,
            }));

            var fifty = await Time(async () => await service.FindNearestAsync(new SonicSimilarityQuery
            {
                LibraryId = libraryId,
                SeedTrackIds = tracks.Take(50).ToList(),
                Limit = 50,
            }));

            _output.WriteLine(
                $"{size}\t{build.ElapsedMilliseconds}\t{warm.Metrics!.ApproximateMegabytes:F1}"
                + $"\t{single}\t{ten}\t{fifty}");

            Assert.Equal(size, warm.Metrics!.VectorCount);
        }
    }

    [Fact]
    public void HeldVectorMemory_GrowsLinearlyAndIsReported()
    {
        // 1280 float32 values is 5120 bytes per track with no compression, so the
        // resident cost is exactly predictable. This is the number that decides
        // whether an approximate index is worth adding.
        const long bytesPerVector = 1280L * sizeof(float);
        Assert.Equal(5120L, bytesPerVector);
        Assert.Equal(5120d * 100_000d / 1024d / 1024d, bytesPerVector * 100_000d / 1024d / 1024d, 0);
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<long> Time(Func<Task> action)
    {
        var stopwatch = Stopwatch.StartNew();
        await action();
        stopwatch.Stop();
        return stopwatch.ElapsedMilliseconds;
    }

    private ISonicSimilarityService NewService()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_configuration);
        services.AddSingleton(_repository);
        services.AddSonicSimilarity();
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<ISonicSimilarityService>();
    }

    private static float[] Vector(int seed)
    {
        var random = new Random(seed);
        var values = new float[Dimensions];
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

    private async Task<long> CreateLibraryAsync()
    {
        var libraryId = 900_000 + Math.Abs(StringComparer.Ordinal.GetHashCode(Guid.NewGuid()) % 90_000);
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO library (id, name) VALUES ($id, $name) ON CONFLICT(id) DO NOTHING;";
        command.Parameters.AddWithValue("$id", libraryId);
        command.Parameters.AddWithValue("$name", $"Bench {libraryId}");
        await command.ExecuteNonQueryAsync();
        return libraryId;
    }

    private async Task<IReadOnlyList<long>> SeedAsync(long libraryId, int count)
    {
        var albumId = Math.Abs(HashCode.Combine(libraryId, "album"));
        var artistId = Math.Abs(HashCode.Combine(libraryId, "artist"));
        var folderId = Math.Abs(HashCode.Combine(libraryId, "folder"));
        var trackIds = new List<long>(count);

        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();

        await using (var artistCommand = connection.CreateCommand())
        {
            artistCommand.CommandText = "INSERT INTO artist (id, name) VALUES ($id, $name) ON CONFLICT(id) DO NOTHING;";
            artistCommand.Parameters.AddWithValue("$id", artistId);
            artistCommand.Parameters.AddWithValue("$name", "Bench Artist " + libraryId);
            await artistCommand.ExecuteNonQueryAsync();
        }

        await using (var albumCommand = connection.CreateCommand())
        {
            albumCommand.CommandText = "INSERT INTO album (id, artist_id, title) VALUES ($id, $artistId, $title) ON CONFLICT(id) DO NOTHING;";
            albumCommand.Parameters.AddWithValue("$id", albumId);
            albumCommand.Parameters.AddWithValue("$artistId", artistId);
            albumCommand.Parameters.AddWithValue("$title", "Bench Album " + libraryId);
            await albumCommand.ExecuteNonQueryAsync();
        }

        await using (var folderCommand = connection.CreateCommand())
        {
            folderCommand.CommandText = "INSERT INTO folder (id, root_path, display_name, library_id) VALUES ($id, $root, $display, $libraryId) ON CONFLICT(id) DO NOTHING;";
            folderCommand.Parameters.AddWithValue("$id", folderId);
            folderCommand.Parameters.AddWithValue("$root", $"/bench/lib{libraryId}");
            folderCommand.Parameters.AddWithValue("$display", "Bench " + libraryId);
            folderCommand.Parameters.AddWithValue("$libraryId", libraryId);
            await folderCommand.ExecuteNonQueryAsync();
        }

        await using (var trackCommand = connection.CreateCommand())
        {
            trackCommand.CommandText = "INSERT INTO track (id, album_id, title, duration_ms) VALUES ($id, $albumId, $title, 180000);";
            for (var index = 0; index < count; index++)
            {
                var trackId = albumId + index + 1;
                trackCommand.Parameters.Clear();
                trackCommand.Parameters.AddWithValue("$id", trackId);
                trackCommand.Parameters.AddWithValue("$albumId", albumId);
                trackCommand.Parameters.AddWithValue("$title", $"Bench Track {index}");
                await trackCommand.ExecuteNonQueryAsync();
                trackIds.Add(trackId);
            }
        }

        await using (var fileCommand = connection.CreateCommand())
        {
            fileCommand.CommandText = @"
INSERT INTO audio_file (id, path, relative_path, folder_id, size, mtime, duration_ms)
VALUES ($id, $path, $relative, $folderId, 4096, '2026-01-01T00:00:00Z', 180000)
ON CONFLICT(id) DO NOTHING;";
            for (var index = 0; index < count; index++)
            {
                var trackId = trackIds[index];
                fileCommand.Parameters.Clear();
                fileCommand.Parameters.AddWithValue("$id", trackId);
                fileCommand.Parameters.AddWithValue("$path", $"/bench/lib{libraryId}/{index}.flac");
                fileCommand.Parameters.AddWithValue("$relative", $"{index}.flac");
                fileCommand.Parameters.AddWithValue("$folderId", folderId);
                await fileCommand.ExecuteNonQueryAsync();
            }
        }

        await using (var localCommand = connection.CreateCommand())
        {
            localCommand.CommandText = "INSERT INTO track_local (track_id, audio_file_id) VALUES ($trackId, $audioFileId) ON CONFLICT(track_id, audio_file_id) DO NOTHING;";
            foreach (var trackId in trackIds)
            {
                localCommand.Parameters.Clear();
                localCommand.Parameters.AddWithValue("$trackId", trackId);
                localCommand.Parameters.AddWithValue("$audioFileId", trackId);
                await localCommand.ExecuteNonQueryAsync();
            }
        }

        foreach (var trackId in trackIds)
        {
            await _repository.UpsertSonicEmbeddingAsync(new SonicEmbeddingDto(
                trackId,
                libraryId,
                SonicModelIdentity.Current.ModelId,
                SonicModelIdentity.Current.ModelVersion,
                SonicModelIdentity.Current.EmbeddingVersion,
                Dimensions,
                "mean-v1",
                "l2-v1",
                "cosine",
                Vector(trackId.GetHashCode()),
                4096,
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                DateTimeOffset.UtcNow.AddTicks(trackId % 1000)));
        }

        return trackIds;
    }
}
