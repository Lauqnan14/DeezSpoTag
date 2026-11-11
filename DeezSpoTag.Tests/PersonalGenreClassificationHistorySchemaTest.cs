using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DeezSpoTag.Services.Genre;
using DeezSpoTag.Services.Library;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// A provider-era classification history table still has <c>confidence REAL NOT NULL</c>
/// and the current insert does not write that column. Genre Intelligence then fails
/// the whole file with SQLite error 19. The column has to stay, including any value
/// it already holds, but it must not be required.
/// </summary>
public sealed class PersonalGenreClassificationHistorySchemaTest : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "genre-history-null-" + Guid.NewGuid().ToString("N"));

    public PersonalGenreClassificationHistorySchemaTest() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task StoreMigration_KeepsOldConfidence_AndAllowsAnInsertThatOmitsIt()
    {
        var path = Path.Combine(_directory, "library.db");
        await PlantLegacyHistoryAsync(path);

        var store = CreateStore(path);
        await store.GetSettingsAsync();
        await store.GetSettingsAsync();

        Assert.False(await ColumnIsNotNullAsync(path, "confidence"));
        Assert.False(await ColumnIsNotNullAsync(path, "sources_json"));
        Assert.False(await ColumnIsNotNullAsync(path, "evidence_state"));
        Assert.Equal("CASCADE", await ReadForeignKeyOnDeleteAsync(path));
        Assert.Equal(0.82, await ReadConfidenceAsync(path, "legacy-taxon"));
        Assert.Equal("[\"spotify\"]", await ReadTextAsync(path, "sources_json", "legacy-taxon"));

        await using (var connection = new SqliteConnection("Data Source=" + path))
        {
            await connection.OpenAsync();
            await using var track = connection.CreateCommand();
            track.CommandText = """
                CREATE TABLE IF NOT EXISTS track (id INTEGER PRIMARY KEY);
                INSERT OR IGNORE INTO track (id) VALUES (1);
                """;
            await track.ExecuteNonQueryAsync();
        }

        await store.SaveTrackResultAsync(new PersonalGenreTrackResult(
            1,
            PersonalGenreResolver.Resolve([new GenreTagObservation("Rock", PersonalGenreTaxonKind.Genre)]),
            DateTimeOffset.UtcNow));

        Assert.Equal(0.82, await ReadConfidenceAsync(path, "legacy-taxon"));
        Assert.Null(await ReadConfidenceAsync(path, "rock"));
    }

    [Fact]
    public async Task LibraryStartupMigration_RelaxesTheSameObsoleteNotNullFlags()
    {
        var path = Path.Combine(_directory, "startup.db");
        await PlantLegacyHistoryAsync(path);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Library"] = "Data Source=" + path
        }).Build();

        await new LibraryDbService(configuration, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();

        Assert.False(await ColumnIsNotNullAsync(path, "confidence"));
        Assert.False(await ColumnIsNotNullAsync(path, "sources_json"));
        Assert.False(await ColumnIsNotNullAsync(path, "evidence_state"));
        Assert.Equal(0.82, await ReadConfidenceAsync(path, "legacy-taxon"));
        Assert.Equal("corroborated", await ReadTextAsync(path, "evidence_state", "legacy-taxon"));
    }

    private PersonalGenreStore CreateStore(string path)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Library"] = "Data Source=" + path
        }).Build();
        return new PersonalGenreStore(configuration);
    }

    private static async Task PlantLegacyHistoryAsync(string path)
    {
        await using var connection = new SqliteConnection("Data Source=" + path);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = OFF;
            CREATE TABLE personal_genre_classification_history (
                resolution_id BIGINT NOT NULL REFERENCES personal_genre_resolution_history(id) ON DELETE CASCADE,
                taxon_id TEXT NOT NULL,
                name TEXT NOT NULL,
                kind TEXT NOT NULL,
                confidence REAL NOT NULL,
                sources_json TEXT NOT NULL DEFAULT '[]',
                user_locked INTEGER NOT NULL DEFAULT 0,
                status TEXT NOT NULL DEFAULT 'suggested',
                evidence_state TEXT NOT NULL DEFAULT 'single_source',
                origin_fields_json TEXT NOT NULL DEFAULT '[]',
                PRIMARY KEY (resolution_id, taxon_id)
            );
            INSERT INTO personal_genre_classification_history
                (resolution_id, taxon_id, name, kind, confidence, sources_json, user_locked, status, evidence_state)
            VALUES
                (7, 'legacy-taxon', 'Legacy', 'genre', 0.82, '["spotify"]', 0, 'suggested', 'corroborated');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> ColumnIsNotNullAsync(string path, string column)
    {
        await using var connection = new SqliteConnection("Data Source=" + path);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"notnull\" FROM pragma_table_info('personal_genre_classification_history') WHERE name = $column;";
        command.Parameters.AddWithValue("$column", column);
        var value = await command.ExecuteScalarAsync();
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private static async Task<double?> ReadConfidenceAsync(string path, string taxonId)
    {
        await using var connection = new SqliteConnection("Data Source=" + path);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT confidence FROM personal_genre_classification_history WHERE taxon_id = $taxonId;";
        command.Parameters.AddWithValue("$taxonId", taxonId);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ReadForeignKeyOnDeleteAsync(string path)
    {
        await using var connection = new SqliteConnection("Data Source=" + path);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT on_delete FROM pragma_foreign_key_list('personal_genre_classification_history');";
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ReadTextAsync(string path, string column, string taxonId)
    {
        await using var connection = new SqliteConnection("Data Source=" + path);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT \"{column}\" FROM personal_genre_classification_history WHERE taxon_id = $taxonId;";
        command.Parameters.AddWithValue("$taxonId", taxonId);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
