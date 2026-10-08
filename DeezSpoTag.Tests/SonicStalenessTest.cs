using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Library.Sonic;
using DeezSpoTag.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Phase 3: staleness detection and the surface the Vibe Analysis panel uses.
///
/// <para>The invalidation rule is the substance here. Sonic records the source
/// file's size and modification time with every vector precisely so a later run
/// can tell whether that vector still describes the file it came from. If that
/// comparison is wrong in either direction the consequences are asymmetric: too
/// permissive and a library silently reports full coverage over stale audio, too
/// strict and every pass re-embeds the whole library forever.</para>
/// </summary>
public sealed class SonicStalenessTest : IAsyncLifetime
{
    private string _tempRoot = string.Empty;
    private string _dbPath = string.Empty;
    private IConfiguration _configuration = default!;
    private LibraryRepository _repository = default!;
    private int _sequence;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-sonic-stale-" + Path.GetRandomFileName());
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

    /// <summary>
    /// The two timestamp columns are serialised differently, on purpose, and the
    /// comparison has to cope with it.
    ///
    /// <para><c>audio_file.mtime</c> is written by binding a <see cref="DateTime"/>,
    /// which Microsoft.Data.Sqlite renders as <c>yyyy-MM-dd HH:mm:ss.FFFFFFF</c>.
    /// <c>source_file_mtime_utc</c> is written with <c>ToString("O")</c>, giving
    /// <c>yyyy-MM-ddTHH:mm:ss.fffffffzzz</c>. Both describe the same instant. Any
    /// staleness rule that compares them as text reports every stored vector as
    /// stale on every pass, and the library re-embeds itself indefinitely.</para>
    ///
    /// <para>These helpers keep the test on the same footing as production: the
    /// audio-file side goes through the <see cref="DateTime"/> binding and the
    /// embedding side through the repository's <c>"O"</c> write. Seeding both
    /// columns with one shared string would hide exactly the bug this guards.</para>
    /// </summary>
    [Fact]
    public void TheTwoTimestampColumnsAreSerialisedDifferentlyForTheSameInstant()
    {
        var instant = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        var audioFileText = AudioFileMtimeText(instant);
        var embeddingText = new DateTimeOffset(instant).ToString("O", CultureInfo.InvariantCulture);

        Assert.NotEqual(audioFileText, embeddingText);
        Assert.Equal(instant, DateTime.Parse(
            audioFileText, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal));
        Assert.Equal(instant, DateTimeOffset.Parse(
            embeddingText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).UtcDateTime);
    }

    /// <summary>
    /// The regression this whole file exists for: a file that has not changed must
    /// not be re-embedded, even though its two recorded timestamps are stored in
    /// different text formats.
    /// </summary>
    [Fact]
    public async Task AnUnchangedFileIsNotStale_EvenThoughTheTimestampsAreStoredDifferently()
    {
        var instant = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var (libraryId, trackId) = await SeedAsync("FormatMismatch", size: 4096, mtime: instant);
        await EmbedAsync(libraryId, trackId, size: 4096, mtime: instant);

        // The stored values genuinely differ as text.
        var (storedFileMtime, storedEmbeddingMtime) = await ReadStoredMtimesAsync(libraryId);
        Assert.NotEqual(storedFileMtime, storedEmbeddingMtime);

        // Yet the track must not be offered for re-analysis.
        Assert.Empty(await StaleAsync(libraryId));
        Assert.Equal(0, await CountStaleAsync(libraryId));
    }

    [Fact]
    public async Task ASubSecondChangeInModificationTimeIsStale()
    {
        // Guards the same trap from the other side: julian-day comparison has to
        // actually see the difference, not round it away.
        var (libraryId, trackId) = await SeedAsync(
            "SubSecond", size: 4096, mtime: new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc));
        await EmbedAsync(libraryId, trackId, size: 4096, mtime: new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc));

        await UpdateFileAsync(
            trackId, size: 4096, mtime: new DateTime(2026, 6, 1, 12, 0, 1, DateTimeKind.Utc));

        Assert.True(Assert.Single(await StaleAsync(libraryId)).HasEmbedding);
    }

    [Fact]
    public async Task ATrackWithNoEmbedding_IsReportedAsMissing()
    {
        var (libraryId, trackId) = await SeedAsync("Missing");

        var stale = await StaleAsync(libraryId);

        var entry = Assert.Single(stale);
        Assert.Equal(trackId, entry.TrackId);
        Assert.False(entry.HasEmbedding);
    }

    [Fact]
    public async Task AFreshEmbedding_IsNotReportedAsStale()
    {
        var instant = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var (libraryId, trackId) = await SeedAsync("Fresh", size: 4096, mtime: instant);
        await EmbedAsync(libraryId, trackId, size: 4096, mtime: instant);

        Assert.Empty(await StaleAsync(libraryId));
        Assert.Equal(0, await CountStaleAsync(libraryId));
    }

    [Fact]
    public async Task AChangedSize_IsReportedAsStale()
    {
        var instant = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var (libraryId, trackId) = await SeedAsync("SizeChanged", size: 4096, mtime: instant);
        await EmbedAsync(libraryId, trackId, size: 4096, mtime: instant);

        await UpdateFileAsync(trackId, size: 8192, mtime: instant);

        var stale = await StaleAsync(libraryId);
        Assert.Equal(trackId, Assert.Single(stale).TrackId);
        Assert.True(Assert.Single(stale).HasEmbedding);
    }

    [Fact]
    public async Task AChangedModificationTime_IsReportedAsStale()
    {
        // Same byte count, different mtime: the case a size-only rule would miss.
        var (libraryId, trackId) = await SeedAsync(
            "MtimeChanged", size: 4096, mtime: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await EmbedAsync(libraryId, trackId, size: 4096, mtime: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        await UpdateFileAsync(trackId, size: 4096, mtime: new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc));

        var stale = await StaleAsync(libraryId);
        Assert.True(Assert.Single(stale).HasEmbedding);
    }

    [Fact]
    public async Task AFileWithNoModificationTimeIsNotFalselyStale()
    {
        // Some libraries carry no usable mtime. When neither side recorded one,
        // there is nothing to compare and nothing to re-embed.
        var (libraryId, trackId) = await SeedAsync("NoMtime", size: 4096, mtime: null);
        await EmbedAsync(libraryId, trackId, size: 4096, mtime: null);

        Assert.Empty(await StaleAsync(libraryId));
        Assert.Equal(0, await CountStaleAsync(libraryId));
    }

    [Fact]
    public async Task ALostModificationTimeIsTreatedAsChanged()
    {
        // The other direction, and deliberately asymmetric with the test above.
        // A file that used to carry a timestamp and no longer does is a real
        // change: the previous vector was derived under a revision we can no
        // longer confirm, so it must be recomputed rather than assumed current.
        var instant = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var (libraryId, trackId) = await SeedAsync("LostMtime", size: 4096, mtime: instant);
        await EmbedAsync(libraryId, trackId, size: 4096, mtime: instant);

        await ClearFileMtimeAsync(trackId);

        var entry = Assert.Single(await StaleAsync(libraryId));
        Assert.True(entry.HasEmbedding);
        Assert.Equal(1, await CountStaleAsync(libraryId));
    }

    [Fact]
    public async Task StaleCountMatchesTheStaleTrackList()
    {
        var instant = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var (libraryId, first) = await SeedAsync("MixedA", size: 100, mtime: instant);
        var (_, second) = await SeedAsync("MixedB", size: 200, mtime: instant, intoLibraryId: libraryId);
        await SeedAsync("MixedC", size: 300, mtime: instant, intoLibraryId: libraryId);

        await EmbedAsync(libraryId, first, size: 100, mtime: instant);
        await EmbedAsync(libraryId, second, size: 200, mtime: instant);

        // Only the second goes stale.
        await UpdateFileAsync(second, size: 200, mtime: new DateTime(2026, 9, 9, 9, 9, 9, DateTimeKind.Utc));

        var list = await StaleAsync(libraryId);

        // MixedA fresh, MixedB stale, MixedC never embedded.
        Assert.Equal(2, list.Count);
        Assert.Equal(1, list.Count(entry => entry.TrackId == second && entry.HasEmbedding));
        Assert.Equal(1, list.Count(entry => entry.TrackId != first && !entry.HasEmbedding));
        Assert.Equal(1, await CountStaleAsync(libraryId));
    }

    [Fact]
    public async Task AnEmbeddingFromADifferentModelVersionIsTreatedAsAbsent()
    {
        // A vector produced by a different model is not a usable vector for this
        // one, so the track must come back as needing analysis rather than being
        // counted as covered.
        var instant = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var (libraryId, trackId) = await SeedAsync("OtherVersion", size: 4096, mtime: instant);
        await _repository.UpsertSonicEmbeddingAsync(new SonicEmbeddingDto(
            trackId, libraryId, SonicModelIdentity.Current.ModelId, "1", "embedding-v0",
            1280, "mean-v1", "l2-v1", "cosine", Vector(), 4096,
            new DateTimeOffset(instant), DateTimeOffset.UtcNow));

        var entry = Assert.Single(await StaleAsync(libraryId));
        Assert.Equal(trackId, entry.TrackId);
        Assert.False(entry.HasEmbedding);
    }

    [Fact]
    public async Task AnUnchangedVectorIsNotReRequested()
    {
        // The counter to the previous test: a matching revision must not appear
        // in the work list at all, or every pass would re-embed everything.
        var instant = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var (libraryId, trackId) = await SeedAsync("Stable", size: 4096, mtime: instant);
        await EmbedAsync(libraryId, trackId, size: 4096, mtime: instant);

        Assert.DoesNotContain(trackId, (await StaleAsync(libraryId)).Select(entry => entry.TrackId));
    }

    [Fact]
    public async Task AnotherLibraryIsNeverReportedStale()
    {
        var instant = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var (first, firstTrack) = await SeedAsync("ScopeA", size: 100, mtime: instant);
        var (second, _) = await SeedAsync("ScopeB", size: 100, mtime: instant);
        await EmbedAsync(first, firstTrack, size: 100, mtime: instant);
        await UpdateFileAsync(firstTrack, size: 100, mtime: new DateTime(2027, 7, 7, 7, 7, 7, DateTimeKind.Utc));

        Assert.NotEmpty(await StaleAsync(first));
        Assert.DoesNotContain(second, (await StaleAsync(second)).Select(entry => entry.TrackId));
    }

    [Fact]
    public async Task EnabledLibraryScopes_OnlyReturnLibrariesWithEnabledFolders()
    {
        var (libraryId, _) = await SeedAsync("ScopeEnabled", size: 10, mtime: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var (disabledLibraryId, _) = await SeedAsync("ScopeDisabled", size: 10, mtime: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        await ExecuteAsync("UPDATE folder SET enabled = 0 WHERE library_id = $id;", ("$id", disabledLibraryId));

        var scopes = await _repository.GetEnabledLibraryScopesAsync();
        Assert.Contains(libraryId, scopes.Select(scope => scope.LibraryId));
        Assert.DoesNotContain(disabledLibraryId, scopes.Select(scope => scope.LibraryId));
    }

    [Fact]
    public async Task TheStaleQueryIsBounded()
    {
        // A whole-library catch-up would monopolise the analysis queue, so the
        // caller can only ever ask for a slice.
        var instant = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var (libraryId, _) = await SeedAsync("BoundedA", size: 10, mtime: instant, intoLibraryId: 88_001);
        await SeedAsync("BoundedB", size: 20, mtime: instant, intoLibraryId: 88_001);
        await SeedAsync("BoundedC", size: 30, mtime: instant, intoLibraryId: 88_001);

        var identity = SonicModelIdentity.Current;
        var bounded = await _repository.GetStaleSonicTracksAsync(
            libraryId, identity.ModelId, identity.ModelVersion, identity.EmbeddingVersion, 2);
        Assert.Equal(2, bounded.Count);
    }

    /// <summary>
    /// Names the trap directly, so a future "simplification" of the comparison
    /// fails loudly here rather than silently re-embedding the whole library.
    /// </summary>
    [Fact]
    public void StalenessComparesTimestampsAsInstantsNotAsText()
    {
        var repository = File.ReadAllText(RepositoryFile(
            "DeezSpoTag.Services", "Library", "LibraryRepository.cs"));

        // audio_file.mtime is a bound DateTime ("2026-06-01 12:00:00"); the
        // embedding column is ToString("O") ("2026-06-01T12:00:00.0000000+00:00").
        // As text they never match, so every vector would look stale forever.
        Assert.Contains(
            "julianday(af.mtime) IS NOT julianday(e.source_file_mtime_utc)",
            repository,
            StringComparison.Ordinal);
        Assert.DoesNotContain("IFNULL(af.mtime", repository, StringComparison.Ordinal);
    }

    private static string RepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Join(directory.FullName, "DeezSpoTag.Web")))
        {
            directory = directory.Parent;
        }

        var path = directory?.FullName
            ?? throw new DirectoryNotFoundException("Repository root not found.");
        foreach (var part in parts)
        {
            path = Path.Join(path, part);
        }

        return path;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Mirrors how <c>audio_file.mtime</c> is stored: a bound <see cref="DateTime"/>,
    /// which Microsoft.Data.Sqlite renders without a zone suffix.
    /// </summary>
    private static string AudioFileMtimeText(DateTime utc)
        => utc.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);

    private async Task<(string FileMtime, string EmbeddingMtime)> ReadStoredMtimesAsync(long libraryId)
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT af.mtime, e.source_file_mtime_utc
  FROM audio_file af
  JOIN track_local tl ON tl.audio_file_id = af.id
  JOIN folder f ON f.id = af.folder_id
  LEFT JOIN track_sonic_embedding e ON e.track_id = tl.track_id
 WHERE f.library_id = $libraryId;";
        command.Parameters.AddWithValue("$libraryId", libraryId);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetString(1));
    }

    private Task<IReadOnlyList<TrackStaleSonicDto>> StaleAsync(long libraryId)
        => _repository.GetStaleSonicTracksAsync(
            libraryId,
            SonicModelIdentity.Current.ModelId,
            SonicModelIdentity.Current.ModelVersion,
            SonicModelIdentity.Current.EmbeddingVersion,
            500);

    private Task<int> CountStaleAsync(long libraryId)
        => _repository.CountStaleSonicEmbeddingsAsync(
            libraryId,
            SonicModelIdentity.Current.ModelId,
            SonicModelIdentity.Current.ModelVersion,
            SonicModelIdentity.Current.EmbeddingVersion);

    private Task EmbedAsync(long libraryId, long trackId, long size, DateTime? mtime)
        => _repository.UpsertSonicEmbeddingAsync(new SonicEmbeddingDto(
            trackId, libraryId,
            SonicModelIdentity.Current.ModelId,
            SonicModelIdentity.Current.ModelVersion,
            SonicModelIdentity.Current.EmbeddingVersion,
            1280, "mean-v1", "l2-v1", "cosine",
            Vector(), size,
            mtime is { } value
                ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc))
                : (DateTimeOffset?)null,
            DateTimeOffset.UtcNow));

    private Task UpdateFileAsync(long trackId, long size, DateTime mtime)
        => ExecuteAsync(
            "UPDATE audio_file SET size = $size, mtime = $mtime WHERE id = $id;",
            ("$id", trackId), ("$size", size), ("$mtime", AudioFileMtimeText(mtime)));

    private Task ClearFileMtimeAsync(long trackId)
        => ExecuteAsync("UPDATE audio_file SET mtime = NULL WHERE id = $id;", ("$id", trackId));

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static float[] Vector()
    {
        var values = new float[1280];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = 1f / 40f;
        }

        return values;
    }

    private async Task<(long LibraryId, long TrackId)> SeedAsync(
        string name,
        long? size = null,
        DateTime? mtime = null,
        long? intoLibraryId = null)
    {
        // Deterministic, collision-free ids rather than string hashes: a hash
        // collision would silently merge two fixtures into one library and turn
        // Assert.Single into a coin flip. Every test gets its own database, so a
        // per-instance counter is enough to keep fixtures isolated.
        var libraryId = intoLibraryId ?? 90_000 + _sequence * 10;
        var trackId = 900_000 + _sequence;
        _sequence++;

        // INSERT OR IGNORE rather than ON CONFLICT(id): these tables also carry
        // UNIQUE columns (library.name, folder.root_path), and a targeted upsert
        // does not absorb a violation on any other index.
        await ExecuteAsync(
            "INSERT OR IGNORE INTO library (id, name) VALUES ($id, $name);",
            ("$id", libraryId), ("$name", $"Lib {libraryId}"));

        var artistId = Math.Abs(HashCode.Combine(libraryId, name, "ar"));
        await ExecuteAsync(
            "INSERT OR IGNORE INTO artist (id, name) VALUES ($id, $name);",
            ("$id", artistId), ("$name", name + " Artist"));

        var albumId = Math.Abs(HashCode.Combine(libraryId, name, "al"));
        await ExecuteAsync(
            "INSERT OR IGNORE INTO album (id, artist_id, title) VALUES ($id, $artistId, $title);",
            ("$id", albumId), ("$artistId", artistId), ("$title", name + " Album"));

        await ExecuteAsync(
            "INSERT INTO track (id, album_id, title, duration_ms) VALUES ($id, $albumId, $title, 180000);",
            ("$id", trackId), ("$albumId", albumId), ("$title", name));

        var folderId = Math.Abs(HashCode.Combine(libraryId, "folder"));
        await ExecuteAsync(
            @"INSERT OR IGNORE INTO folder (id, root_path, display_name, library_id)
              VALUES ($id, $root, $display, $libraryId);",
            ("$id", folderId), ("$root", $"/stale/lib{libraryId}"),
            ("$display", "Folder " + libraryId), ("$libraryId", libraryId));

        await ExecuteAsync(
            @"
INSERT INTO audio_file (id, path, relative_path, folder_id, size, mtime, duration_ms)
VALUES ($id, $path, $relative, $folderId, $size, $mtime, 180000)
ON CONFLICT(id) DO NOTHING;",
            ("$id", trackId), ("$path", $"/stale/lib{libraryId}/{name}.flac"),
            ("$relative", name + ".flac"), ("$folderId", folderId),
            ("$size", size ?? 4096),
            ("$mtime", mtime is { } value
                ? (object)AudioFileMtimeText(DateTime.SpecifyKind(value, DateTimeKind.Utc))
                : DBNull.Value));

        await ExecuteAsync(
            @"INSERT INTO track_local (track_id, audio_file_id)
              VALUES ($trackId, $audioFileId) ON CONFLICT(track_id, audio_file_id) DO NOTHING;",
            ("$trackId", trackId), ("$audioFileId", trackId));

        await ExecuteAsync(
            @"INSERT INTO track_analysis (track_id, library_id, status)
              VALUES ($trackId, $libraryId, 'pending') ON CONFLICT(track_id) DO NOTHING;",
            ("$trackId", trackId), ("$libraryId", libraryId));

        return (libraryId, trackId);
    }
}
