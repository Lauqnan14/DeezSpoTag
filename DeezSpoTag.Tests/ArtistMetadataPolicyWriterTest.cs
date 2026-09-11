using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// The Artist Metadata Updater writes per-artist policy rows with a FK to artist(id);
/// tracked artists whose library rows are gone must not abort a run with
/// "SQLite Error 19: 'FOREIGN KEY constraint failed'".
/// </summary>
public sealed class ArtistMetadataPolicyWriterTest : IAsyncLifetime
{
    private string _tempRoot = string.Empty;
    private string _dbPath = string.Empty;
    private IConfiguration _configuration = default!;
    private LibraryRepository _repository = default!;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-artist-policy-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
        _dbPath = Path.Join(_tempRoot, "library.db");
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = $"Data Source={_dbPath}"
            })
            .Build();
        var dbService = new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance);
        await dbService.EnsureSchemaAsync();
        _repository = new LibraryRepository(_configuration, NullLogger<LibraryRepository>.Instance);
    }

    public async Task DisposeAsync()
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

        await Task.CompletedTask;
    }

    private async Task InsertArtistAsync(long id, string name)
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO artist (id, name) VALUES (@id, @name);";
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", name);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Policy_Write_For_Missing_Artist_Does_Not_Throw()
    {
        var exception = await Record.ExceptionAsync(() =>
            _repository.SetArtistMetadataOcrTextArtBlockingAsync(424242, enabled: true));

        Assert.Null(exception);
    }

    [Fact]
    public async Task Policy_Write_For_Existing_Artist_Upserts_Value()
    {
        await InsertArtistAsync(7, "Alikiba");

        await _repository.SetArtistMetadataOcrTextArtBlockingAsync(7, enabled: true);
        await _repository.SetArtistMetadataOcrTextArtBlockingAsync(7, enabled: false);
        await _repository.SetArtistMetadataSyncBlockedAsync(7, blocked: true);

        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ocr_text_art_blocking_enabled, sync_blocked FROM artist_metadata_policy WHERE artist_id = 7;";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0, Convert.ToInt64(reader.GetValue(0)));
        Assert.Equal(1, Convert.ToInt64(reader.GetValue(1)));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task Existing_Artist_Id_List_Reflects_The_Artist_Table()
    {
        await InsertArtistAsync(1, "One");
        await InsertArtistAsync(2, "Two");

        var ids = await _repository.GetExistingArtistIdsAsync();

        Assert.Equal(new long[] { 1, 2 }, ids.OrderBy(id => id).ToArray());
    }
}
