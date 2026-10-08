using System;
using System.Collections.Generic;
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

namespace DeezSpoTag.Tests;

/// <summary>
/// Phase 2 correctness gate for exact Sonic similarity.
///
/// These tests are the oracle. A later approximate index is only acceptable if it
/// reproduces the ordering these tests pin down, so the properties asserted here
/// are deliberately strict: exact cosine against a hand-computed value, fully
/// deterministic tie ordering, and no route by which a result can escape the
/// caller's candidate restrictions or the library boundary.
/// </summary>
public sealed class SonicSimilarityServiceTest : IAsyncLifetime
{
    private const int Dimensions = 1280;

    private string _tempRoot = string.Empty;
    private string _dbPath = string.Empty;
    private IConfiguration _configuration = default!;
    private LibraryRepository _repository = default!;
    private ISonicSimilarityService _service = default!;
    private long _libraryOneId;
    private long _libraryTwoId;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-sonic-sim-" + Path.GetRandomFileName());
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
        _service = BuildService();

        _libraryOneId = await SeedLibraryAsync("One");
        _libraryTwoId = await SeedLibraryAsync("Two");
    }

    /// <summary>
    /// Builds the service through the production registration rather than by
    /// naming the implementation, so this also proves the DI wiring resolves and
    /// that the concrete index is reachable behind its interface.
    /// </summary>
    private ISonicSimilarityService BuildService()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_configuration);
        services.AddSingleton(_repository);
        services.AddSonicSimilarity();

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<ISonicSimilarityService>();
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
    public async Task IdenticalVectors_AreTheirOwnNearestNeighbourAtOne()
    {
        // A track compared with itself is the calibration point: if this is not
        // exactly 1, every downstream similarity is wrong.
        var libraryId = await SeedLibraryAsync("Identical");
        var trackId = await AddSeedAsync(libraryId, "self", seed: 3);
        var otherLibrary = await SeedLibraryAsync("IdenticalTwo");
        var otherTrack = await AddSeedAsync(otherLibrary, "cross", seed: 4);

        var service = NewService();
        await service.FindNearestAsync(new SonicSimilarityQuery
        {
            LibraryId = libraryId,
            SeedTrackIds = new[] { trackId },
            AllowedTrackIds = new[] { trackId, otherTrack },
            Limit = 10,
        });

        var score = await service.SimilarityAsync(libraryId, trackId, trackId);
        Assert.NotNull(score);
        Assert.Equal(1.0d, score!.Value, 6);
    }

    [Fact]
    public async Task TopK_AgreesWithHandComputedExactCosine()
    {
        var libraryId = await SeedLibraryAsync("Exact");
        var uniform = Uniform();
        var seedTrack = await AddSeedAsync(libraryId, "seed", seed: 0, vector: uniform);

        // Candidates are built to have known pairwise angles against the uniform
        // seed: identical (1), orthogonal (0) and opposite (-1).
        var identical = await AddSeedAsync(libraryId, "identical", seed: 0, vector: uniform);
        var orthogonal = await AddSeedAsync(libraryId, "orthogonal", seed: 0, transform: MakeOrthogonal, vector: uniform);
        var opposite = await AddSeedAsync(libraryId, "opposite", seed: 0, transform: Negate, vector: uniform);

        var result = await NewService().FindNearestAsync(new SonicSimilarityQuery
        {
            LibraryId = libraryId,
            SeedTrackIds = new[] { seedTrack },
            Limit = 10,
        });

        Assert.Equal(identical, result.Matches[0].TrackId);
        Assert.Equal(1.0d, result.Matches[0].Similarity, 5);
        Assert.Equal(0d, result.Matches[0].Distance, 5);
        Assert.Equal(1, result.Matches[0].Rank);

        var orthogonalScore = result.Matches.Single(m => m.TrackId == orthogonal).Similarity;
        var oppositeScore = result.Matches.Single(m => m.TrackId == opposite).Similarity;
        Assert.Equal(0d, orthogonalScore, 4);
        Assert.Equal(-1d, oppositeScore, 4);

        // Ordering must follow the true geometry, not insertion order.
        Assert.True(result.Matches[0].TrackId == identical);
        Assert.True(result.Matches[1].TrackId == orthogonal);
        Assert.True(result.Matches[2].TrackId == opposite);
    }

    [Fact]
    public async Task SeedsAreNeverReturnedAsTheirOwnNeighbours()
    {
        var libraryId = await SeedLibraryAsync("SelfExclude");
        var seed = await AddSeedAsync(libraryId, "seed", seed: 12);
        await AddSeedAsync(libraryId, "other", seed: 13);

        var result = await NewService().FindNearestAsync(new SonicSimilarityQuery
        {
            LibraryId = libraryId,
            SeedTrackIds = new[] { seed },
            Limit = 10,
        });

        Assert.DoesNotContain(result.Matches, match => match.TrackId == seed);
    }

    [Fact]
    public async Task MultipleSeeds_TakeTheBestMatchPerCandidate()
    {
        var libraryId = await SeedLibraryAsync("MultiSeed");
        var seedA = await AddSeedAsync(libraryId, "seedA", seed: 20);
        var seedB = await AddSeedAsync(libraryId, "seedB", seed: 21);
        var nearA = await AddSeedAsync(libraryId, "nearA", seed: 20);
        var nearB = await AddSeedAsync(libraryId, "nearB", seed: 21);

        var result = await NewService().FindNearestAsync(new SonicSimilarityQuery
        {
            LibraryId = libraryId,
            SeedTrackIds = new[] { seedA, seedB },
            Limit = 10,
        });

        Assert.Equal(1.0d, result.Matches.Single(m => m.TrackId == nearA).Similarity, 5);
        Assert.Equal(1.0d, result.Matches.Single(m => m.TrackId == nearB).Similarity, 5);
        Assert.Equal(seedA, result.Matches.Single(m => m.TrackId == nearA).SeedTrackId);
        Assert.Equal(seedB, result.Matches.Single(m => m.TrackId == nearB).SeedTrackId);
    }

    [Fact]
    public async Task AllowedAndExcludedSets_AreBothHonoured()
    {
        var libraryId = await SeedLibraryAsync("Restrictions");
        var seed = await AddSeedAsync(libraryId, "seed", seed: 30);
        var allowed = await AddSeedAsync(libraryId, "allowed", seed: 31);
        var excluded = await AddSeedAsync(libraryId, "excluded", seed: 32);
        var notAllowed = await AddSeedAsync(libraryId, "notAllowed", seed: 33);

        var result = await NewService().FindNearestAsync(new SonicSimilarityQuery
        {
            LibraryId = libraryId,
            SeedTrackIds = new[] { seed },
            // notAllowed is deliberately absent from this set: it exercises the
            // allow list, while excluded exercises the exclusion set.
            AllowedTrackIds = new[] { seed, allowed, excluded },
            ExcludedTrackIds = new[] { excluded },
            Limit = 50,
        });

        Assert.Equal(new[] { allowed }, result.Matches.Select(m => m.TrackId).ToArray());
    }

    [Fact]
    public async Task CandidatesNeverCrossTheLibraryBoundary()
    {
        var oneTrack = await AddSeedAsync(_libraryOneId, "one", seed: 40);
        var oneOther = await AddSeedAsync(_libraryOneId, "oneOther", seed: 41);
        var twoTrack = await AddSeedAsync(_libraryTwoId, "two", seed: 40);
        var twoOther = await AddSeedAsync(_libraryTwoId, "twoOther", seed: 41);

        var service = NewService();
        var first = await service.FindNearestAsync(new SonicSimilarityQuery
        {
            LibraryId = _libraryOneId,
            SeedTrackIds = new[] { oneTrack },
            Limit = 50,
        });
        var second = await service.FindNearestAsync(new SonicSimilarityQuery
        {
            LibraryId = _libraryTwoId,
            SeedTrackIds = new[] { twoTrack },
            Limit = 50,
        });

        // Each library sees only its own vectors, and the seed is excluded from
        // its own results.
        Assert.Equal(new[] { oneOther }, first.Matches.Select(m => m.TrackId).ToArray());
        Assert.Equal(new[] { twoOther }, second.Matches.Select(m => m.TrackId).ToArray());
        Assert.DoesNotContain(first.Matches, m => m.TrackId == twoTrack || m.TrackId == twoOther);
        Assert.DoesNotContain(second.Matches, m => m.TrackId == oneTrack || m.TrackId == oneOther);

        // A cross-library pair is simply not comparable, not scored as dissimilar.
        var crossScore = await service.SimilarityAsync(_libraryOneId, oneTrack, twoTrack);
        Assert.Null(crossScore);
    }

    [Fact]
    public async Task EqualScores_TieBreakByTrackIdAscending()
    {
        // Determinism is what makes a generated playlist reproducible. Several
        // candidates are given exactly equal scores, so only the track id
        // tiebreak can order them.
        var libraryId = await SeedLibraryAsync("Ties");
        var seed = await AddSeedAsync(libraryId, "seed", seed: 50);

        var equals = new List<long>();
        foreach (var name in new[] { "c", "a", "d", "b" })
        {
            equals.Add(await AddSeedAsync(libraryId, "tie-" + name, seed: 50, transform: Negate));
        }

        var result = await NewService().FindNearestAsync(new SonicSimilarityQuery
        {
            LibraryId = libraryId,
            SeedTrackIds = new[] { seed },
            Limit = 50,
        });

        var tied = result.Matches.Where(m => equals.Contains(m.TrackId)).ToList();
        Assert.Equal(equals.Count, tied.Count);
        Assert.All(tied, match => Assert.Equal(-1d, match.Similarity, 5));
        Assert.Equal(equals.OrderBy(id => id).ToList(), tied.Select(m => m.TrackId).ToList());
    }

    [Fact]
    public async Task RepeatedQueries_ProduceIdenticalOrdering()
    {
        var libraryId = await SeedLibraryAsync("Repeat");
        var seed = await AddSeedAsync(libraryId, "seed", seed: 60);
        for (var index = 0; index < 12; index++)
        {
            await AddSeedAsync(libraryId, "cand" + index, seed: 60 + index);
        }

        var service = NewService();
        var first = await service.FindNearestAsync(new SonicSimilarityQuery
        {
            LibraryId = libraryId,
            SeedTrackIds = new[] { seed },
            Limit = 50,
        });
        var second = await service.FindNearestAsync(new SonicSimilarityQuery
        {
            LibraryId = libraryId,
            SeedTrackIds = new[] { seed },
            Limit = 50,
        });

        Assert.Equal(
            first.Matches.Select(m => (m.TrackId, m.Similarity, m.Rank)),
            second.Matches.Select(m => (m.TrackId, m.Similarity, m.Rank)));
    }

    [Fact]
    public async Task MaximumDistance_FiltersDistantCandidates()
    {
        var libraryId = await SeedLibraryAsync("Distance");
        var seed = await AddSeedAsync(libraryId, "seed", seed: 70);
        await AddSeedAsync(libraryId, "near", seed: 70);
        await AddSeedAsync(libraryId, "far", seed: 71, transform: Negate);

        var result = await NewService().FindNearestAsync(new SonicSimilarityQuery
        {
            LibraryId = libraryId,
            SeedTrackIds = new[] { seed },
            Limit = 50,
            MaximumDistance = 0.5d,
        });

        Assert.Single(result.Matches);
        Assert.All(result.Matches, match => Assert.True(match.Distance <= 0.5d));
    }

    [Fact]
    public async Task Limit_IsRespected()
    {
        var libraryId = await SeedLibraryAsync("Limit");
        var seed = await AddSeedAsync(libraryId, "seed", seed: 80);
        for (var index = 0; index < 20; index++)
        {
            await AddSeedAsync(libraryId, "c" + index, seed: 80 + index);
        }

        var result = await NewService().FindNearestAsync(new SonicSimilarityQuery
        {
            LibraryId = libraryId,
            SeedTrackIds = new[] { seed },
            Limit = 5,
        });

        Assert.Equal(5, result.Matches.Count);
        Assert.Equal(Enumerable.Range(1, 5), result.Matches.Select(m => m.Rank));
    }

    [Fact]
    public async Task NoSeeds_ReturnsEmptyRatherThanThrowing()
    {
        var libraryId = await SeedLibraryAsync("NoSeeds");
        var result = await NewService().FindNearestAsync(new SonicSimilarityQuery
        {
            LibraryId = libraryId,
            SeedTrackIds = Array.Empty<long>(),
            Limit = 10,
        });

        Assert.Empty(result.Matches);
        Assert.False(result.HasMatches);
    }

    [Fact]
    public async Task SeedWithoutAVector_ReturnsEmptyAndIsReported()
    {
        // A missing seed is a real condition, not an error: a DJ must be able to
        // detect it and choose another seed rather than silently scoring nothing.
        var libraryId = await SeedLibraryAsync("MissingSeed");
        var present = await AddSeedAsync(libraryId, "present", seed: 90);
        var (absentLibrary, absent) = await SeedTrackAsync("MissingSeedAbsent", libraryId);
        _ = absentLibrary;

        var result = await NewService().FindNearestAsync(new SonicSimilarityQuery
        {
            LibraryId = libraryId,
            SeedTrackIds = new[] { present, absent },
            Limit = 10,
        });

        Assert.Equal(1, result.SeedCount);
        Assert.DoesNotContain(result.Matches, match => match.TrackId == absent);
    }

    [Fact]
    public async Task CorruptVector_IsExcludedFromTheIndexEntirely()
    {
        var libraryId = await SeedLibraryAsync("Corrupt");
        var seed = await AddSeedAsync(libraryId, "seed", seed: 100);
        var corrupt = await AddSeedAsync(libraryId, "corrupt", seed: 100);

        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
UPDATE track_sonic_embedding
   SET dimensions = 640
 WHERE track_id = $trackId;";
            command.Parameters.AddWithValue("$trackId", corrupt);
            await command.ExecuteNonQueryAsync();
        }

        var result = await NewService().FindNearestAsync(new SonicSimilarityQuery
        {
            LibraryId = libraryId,
            SeedTrackIds = new[] { seed },
            Limit = 10,
        });

        Assert.DoesNotContain(result.Matches, match => match.TrackId == corrupt);
    }

    [Fact]
    public async Task Index_ReportsMetricsForItsVectors()
    {
        var libraryId = await SeedLibraryAsync("Metrics");
        var seed = await AddSeedAsync(libraryId, "seed", seed: 110);
        await AddSeedAsync(libraryId, "other", seed: 111);

        var result = await NewService().FindNearestAsync(new SonicSimilarityQuery
        {
            LibraryId = libraryId,
            SeedTrackIds = new[] { seed },
            Limit = 10,
        });

        Assert.NotNull(result.Metrics);
        Assert.Equal(2, result.Metrics!.VectorCount);
        Assert.Equal(Dimensions, result.Metrics.Dimensions);
        Assert.Equal((long)2 * Dimensions * sizeof(float), result.Metrics.ApproximateVectorBytes);
    }

    [Fact]
    public async Task Index_RebuildsWhenAVectorIsAdded()
    {
        var libraryId = await SeedLibraryAsync("Rebuild");
        var seed = await AddSeedAsync(libraryId, "seed", seed: 120);

        var service = NewService();
        var before = await service.FindNearestAsync(new SonicSimilarityQuery
        {
            LibraryId = libraryId,
            SeedTrackIds = new[] { seed },
            Limit = 10,
        });
        Assert.Equal(1, before.Metrics!.VectorCount);

        await AddSeedAsync(libraryId, "added", seed: 121);

        var after = await service.FindNearestAsync(new SonicSimilarityQuery
        {
            LibraryId = libraryId,
            SeedTrackIds = new[] { seed },
            Limit = 10,
        });
        Assert.Equal(2, after.Metrics!.VectorCount);
    }

    [Fact]
    public async Task Index_RebuildsWhenAVectorIsReplaced()
    {
        // A re-analysis replaces a vector without changing how many rows exist, so
        // a count-only staleness check would keep serving superseded values.
        var libraryId = await SeedLibraryAsync("Replace");
        var seed = await AddSeedAsync(libraryId, "seed", seed: 160);
        var target = await AddSeedAsync(libraryId, "target", seed: 160);

        var service = NewService();
        var before = await service.FindNearestAsync(new SonicSimilarityQuery
        {
            LibraryId = libraryId,
            SeedTrackIds = new[] { seed },
            Limit = 10,
        });
        Assert.Equal(1.0d, before.Matches.Single(m => m.TrackId == target).Similarity, 5);

        // Same row, different vector, newer timestamp.
        await _repository.UpsertSonicEmbeddingAsync(new SonicEmbeddingDto(
            target, libraryId,
            SonicModelIdentity.Current.ModelId,
            SonicModelIdentity.Current.ModelVersion,
            SonicModelIdentity.Current.EmbeddingVersion,
            1280, "mean-v1", "l2-v1", "cosine",
            Vector(seed: 999), 4096, DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow.AddDays(1)));

        var after = await service.FindNearestAsync(new SonicSimilarityQuery
        {
            LibraryId = libraryId,
            SeedTrackIds = new[] { seed },
            Limit = 10,
        });

        // Row count is unchanged, so only the timestamp can have forced a rebuild.
        Assert.Equal(2, after.Metrics!.VectorCount);
        Assert.True(
            after.Matches.Single(m => m.TrackId == target).Similarity < 1.0d,
            "The index still serves the superseded vector for a replaced track.");
    }

    [Fact]
    public async Task Index_IsReusedWhenNothingChanged()
    {
        // Rebuild cost is proportional to library size, so a stable library must
        // reuse its cached index rather than reloading every vector per query.
        var libraryId = await SeedLibraryAsync("Cached");
        var seed = await AddSeedAsync(libraryId, "seed", seed: 170);
        await AddSeedAsync(libraryId, "other", seed: 171);

        var service = NewService();
        var first = await service.FindNearestAsync(new SonicSimilarityQuery
        {
            LibraryId = libraryId,
            SeedTrackIds = new[] { seed },
            Limit = 10,
        });
        var second = await service.FindNearestAsync(new SonicSimilarityQuery
        {
            LibraryId = libraryId,
            SeedTrackIds = new[] { seed },
            Limit = 10,
        });

        Assert.Equal(first.Metrics!.VectorCount, second.Metrics!.VectorCount);
        Assert.Equal(
            first.Matches.Select(m => m.TrackId),
            second.Matches.Select(m => m.TrackId));
    }

    [Fact]
    public async Task RebuildIndex_ForcesAFreshLoad()
    {
        var libraryId = await SeedLibraryAsync("ForceRebuild");
        var seed = await AddSeedAsync(libraryId, "seed", seed: 130);
        var service = NewService();

        await service.FindNearestAsync(new SonicSimilarityQuery
        {
            LibraryId = libraryId,
            SeedTrackIds = new[] { seed },
            Limit = 10,
        });

        var metrics = await service.RebuildIndexAsync(libraryId);
        Assert.Equal(1, metrics.VectorCount);
    }

    [Fact]
    public async Task GetEmbedding_ReturnsNullForAnUnusableRow()
    {
        var libraryId = await SeedLibraryAsync("EmbeddingRead");
        var track = await AddSeedAsync(libraryId, "track", seed: 140);

        var usable = await NewService().GetEmbeddingAsync(track);
        Assert.NotNull(usable);
        Assert.Equal(Dimensions, usable!.Dimensions);
        Assert.True(usable.IsUsable);

        var absent = await NewService().GetEmbeddingAsync(-1);
        Assert.Null(absent);
    }

    [Fact]
    public async Task Coverage_IsReportedBeforeAQueryRuns()
    {
        var libraryId = await SeedLibraryAsync("Coverage");
        await AddSeedAsync(libraryId, "one", seed: 150);
        await AddSeedAsync(libraryId, "two", seed: 151);

        var coverage = await NewService().GetCoverageAsync(libraryId);
        Assert.Equal(2, coverage.TotalTracks);
        Assert.Equal(2, coverage.TracksWithEmbedding);
        Assert.Equal(100d, coverage.CoveragePercent);
    }

    // ---------------------------------------------------------------- helpers

    private ISonicSimilarityService NewService() => BuildService();

    private static float[] Vector(int seed) => Normalize(Generate(seed));

    private static float[] Generate(int seed)
    {
        var random = new Random(seed);
        var values = new float[Dimensions];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = (float)((random.NextDouble() * 2.0) - 1.0);
        }

        return values;
    }

    private static float[] MakeOrthogonal(float[] basis)
    {
        // Half the axes positive, half negative, with the same magnitude on both
        // sides, so the dot product against a *uniform* basis cancels to zero.
        // The basis must therefore be uniform too, which is why the caller that
        // uses this passes Uniform() rather than a random vector.
        var values = new float[basis.Length];
        var half = values.Length / 2;
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = index < half ? basis[0] : -basis[0];
        }

        return Normalize(values);
    }

    private static float[] Uniform()
    {
        var values = new float[Dimensions];
        Array.Fill(values, 1f);
        return Normalize(values);
    }

    private static float[] Negate(float[] basis)
    {
        var values = new float[basis.Length];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = -basis[index];
        }

        return values;
    }

    private static float[] Normalize(float[] values)
    {
        double magnitude = 0.0;
        foreach (var value in values)
        {
            magnitude += (double)value * value;
        }

        magnitude = Math.Sqrt(magnitude);
        if (magnitude <= 0.0)
        {
            return values;
        }

        for (var index = 0; index < values.Length; index++)
        {
            values[index] = (float)(values[index] / magnitude);
        }

        return values;
    }

    /// <summary>
    /// Creates a library without adding a track to it. Coverage assertions count
    /// tracks, so a helper that quietly seeded one would make them wrong.
    /// </summary>
    private async Task<long> SeedLibraryAsync(string name)
    {
        var libraryId = 50_000 + Math.Abs(StringComparer.Ordinal.GetHashCode(name + Guid.NewGuid()) % 40_000);
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO library (id, name) VALUES ($id, $name) ON CONFLICT(id) DO NOTHING;";
        command.Parameters.AddWithValue("$id", libraryId);
        command.Parameters.AddWithValue("$name", $"Lib {libraryId} {name}");
        await command.ExecuteNonQueryAsync();

        return libraryId;
    }

    private async Task<(long LibraryId, long TrackId)> SeedTrackAsync(string title, long? intoLibraryId)
    {
        var libraryId = intoLibraryId
            ?? 50_000 + Math.Abs(StringComparer.Ordinal.GetHashCode(title) % 40_000);
        var trackId = Math.Abs(HashCode.Combine(libraryId, title, "t"));
        if (trackId == 0)
        {
            trackId = 7;
        }

        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();

        await using (var libraryCommand = connection.CreateCommand())
        {
            libraryCommand.CommandText = "INSERT INTO library (id, name) VALUES ($id, $name) ON CONFLICT(id) DO NOTHING;";
            libraryCommand.Parameters.AddWithValue("$id", libraryId);
            libraryCommand.Parameters.AddWithValue("$name", $"Lib {libraryId}");
            await libraryCommand.ExecuteNonQueryAsync();
        }

        var artistId = Math.Abs(HashCode.Combine(libraryId, title, "ar"));
        await using (var artistCommand = connection.CreateCommand())
        {
            artistCommand.CommandText = "INSERT INTO artist (id, name) VALUES ($id, $name) ON CONFLICT(id) DO NOTHING;";
            artistCommand.Parameters.AddWithValue("$id", artistId);
            artistCommand.Parameters.AddWithValue("$name", title + " Artist");
            await artistCommand.ExecuteNonQueryAsync();
        }

        var albumId = Math.Abs(HashCode.Combine(libraryId, title, "al"));
        await using (var albumCommand = connection.CreateCommand())
        {
            albumCommand.CommandText = "INSERT INTO album (id, artist_id, title) VALUES ($id, $artistId, $title) ON CONFLICT(id) DO NOTHING;";
            albumCommand.Parameters.AddWithValue("$id", albumId);
            albumCommand.Parameters.AddWithValue("$artistId", artistId);
            albumCommand.Parameters.AddWithValue("$title", title + " Album");
            await albumCommand.ExecuteNonQueryAsync();
        }

        await using (var trackCommand = connection.CreateCommand())
        {
            trackCommand.CommandText = "INSERT INTO track (id, album_id, title, duration_ms) VALUES ($id, $albumId, $title, 180000);";
            trackCommand.Parameters.AddWithValue("$id", trackId);
            trackCommand.Parameters.AddWithValue("$albumId", albumId);
            trackCommand.Parameters.AddWithValue("$title", title);
            await trackCommand.ExecuteNonQueryAsync();
        }

        var folderId = Math.Abs(HashCode.Combine(libraryId, "folder"));
        await using (var folderCommand = connection.CreateCommand())
        {
            folderCommand.CommandText = "INSERT INTO folder (id, root_path, display_name, library_id) VALUES ($id, $root, $display, $libraryId) ON CONFLICT(id) DO NOTHING;";
            folderCommand.Parameters.AddWithValue("$id", folderId);
            folderCommand.Parameters.AddWithValue("$root", $"/music/lib{libraryId}");
            folderCommand.Parameters.AddWithValue("$display", "Folder " + libraryId);
            folderCommand.Parameters.AddWithValue("$libraryId", libraryId);
            await folderCommand.ExecuteNonQueryAsync();
        }

        var audioFileId = Math.Abs(HashCode.Combine(libraryId, title, "af"));
        await using (var audioFileCommand = connection.CreateCommand())
        {
            audioFileCommand.CommandText = @"
INSERT INTO audio_file (id, path, relative_path, folder_id, size, mtime, duration_ms)
VALUES ($id, $path, $relative, $folderId, 4096, '2026-01-01T00:00:00Z', 180000)
ON CONFLICT(id) DO NOTHING;";
            audioFileCommand.Parameters.AddWithValue("$id", audioFileId);
            audioFileCommand.Parameters.AddWithValue("$path", $"/music/lib{libraryId}/{title}.flac");
            audioFileCommand.Parameters.AddWithValue("$relative", $"{title}.flac");
            audioFileCommand.Parameters.AddWithValue("$folderId", folderId);
            await audioFileCommand.ExecuteNonQueryAsync();
        }

        await using (var localCommand = connection.CreateCommand())
        {
            localCommand.CommandText = "INSERT INTO track_local (track_id, audio_file_id) VALUES ($trackId, $audioFileId) ON CONFLICT(track_id, audio_file_id) DO NOTHING;";
            localCommand.Parameters.AddWithValue("$trackId", trackId);
            localCommand.Parameters.AddWithValue("$audioFileId", audioFileId);
            await localCommand.ExecuteNonQueryAsync();
        }

        await using (var analysisCommand = connection.CreateCommand())
        {
            analysisCommand.CommandText = "INSERT INTO track_analysis (track_id, library_id, status) VALUES ($trackId, $libraryId, 'pending') ON CONFLICT(track_id) DO NOTHING;";
            analysisCommand.Parameters.AddWithValue("$trackId", trackId);
            analysisCommand.Parameters.AddWithValue("$libraryId", libraryId);
            await analysisCommand.ExecuteNonQueryAsync();
        }


        return (libraryId, trackId);
    }

    private async Task<long> AddSeedAsync(
        long libraryId,
        string name,
        int seed,
        Func<float[], float[]>? transform = null,
        float[]? vector = null)
    {
        var (_, trackId) = await SeedTrackAsync($"Sim-{name}-{Guid.NewGuid():N}", libraryId);
        var resolved = vector ?? Vector(seed);
        await AddVectorAsync(libraryId, trackId, transform is null ? resolved : transform(resolved));
        return trackId;
    }

    private Task AddVectorAsync(long libraryId, long trackId, float[] vector)
        => _repository.UpsertSonicEmbeddingAsync(new SonicEmbeddingDto(
            trackId,
            libraryId,
            SonicModelIdentity.Current.ModelId,
            SonicModelIdentity.Current.ModelVersion,
            SonicModelIdentity.Current.EmbeddingVersion,
            vector.Length,
            "mean-v1",
            "l2-v1",
            "cosine",
            vector,
            4096,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            DateTimeOffset.UtcNow));
}
