using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class LibrarySettingsSchemaMigrationTests : IAsyncLifetime
{
    private string _tempRoot = string.Empty;
    private string _dbPath = string.Empty;
    private IConfiguration _configuration = default!;

    public Task InitializeAsync()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-library-settings-migration-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
        _dbPath = Path.Join(_tempRoot, "library.db");
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = $"Data Source={_dbPath}"
            })
            .Build();
        return Task.CompletedTask;
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
    public async Task EnsureSchema_RemovesLegacyLibrarySettingsColumns_AndPreservesSupportedValues()
    {
        await using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
CREATE TABLE library_settings (
    id SMALLINT PRIMARY KEY DEFAULT 1,
    fuzzy_threshold REAL NOT NULL DEFAULT 0.85,
    include_all_folders INTEGER NOT NULL DEFAULT 1,
    live_preview_ingest INTEGER NOT NULL DEFAULT 0,
    enable_signal_analysis INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);
INSERT INTO library_settings (id, fuzzy_threshold, include_all_folders, live_preview_ingest, enable_signal_analysis)
VALUES (1, 0.91, 1, 1, 0);";
            await command.ExecuteNonQueryAsync();
        }

        var dbService = new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance);
        await dbService.EnsureSchemaAsync();

        await using var migratedConnection = new SqliteConnection($"Data Source={_dbPath}");
        await migratedConnection.OpenAsync();

        var columns = await ReadTableColumnsAsync(migratedConnection, "library_settings");
        Assert.DoesNotContain(columns, name => string.Equals(name, "fuzzy_threshold", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(columns, name => string.Equals(name, "include_all_folders", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(columns, name => string.Equals(name, "live_preview_ingest", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(columns, name => string.Equals(name, "enable_signal_analysis", StringComparison.OrdinalIgnoreCase));

        await using var settingsCommand = migratedConnection.CreateCommand();
        settingsCommand.CommandText = "SELECT live_preview_ingest, enable_signal_analysis FROM library_settings WHERE id = 1;";
        await using var reader = await settingsCommand.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.False(reader.GetBoolean(1));
    }

    private static async Task<IReadOnlyList<string>> ReadTableColumnsAsync(SqliteConnection connection, string tableName)
    {
        var columns = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info($table);";
        command.Parameters.AddWithValue("$table", tableName);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }
}
