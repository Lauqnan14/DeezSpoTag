using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Controllers.Api;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class LibraryArtistsApiControllerTests : IAsyncLifetime
{
    private string _tempRoot = string.Empty;
    private string _dbPath = string.Empty;
    private LibraryRepository _repository = default!;
    private LibraryConfigStore _configStore = default!;
    private IWebHostEnvironment _environment = default!;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-library-artists-api-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);

        _dbPath = Path.Join(_tempRoot, "library.db");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = $"Data Source={_dbPath}"
            })
            .Build();

        var dbService = new LibraryDbService(configuration, NullLogger<LibraryDbService>.Instance);
        await dbService.EnsureSchemaAsync();

        _repository = new LibraryRepository(configuration, NullLogger<LibraryRepository>.Instance);
        _environment = new StubWebHostEnvironment(_tempRoot);
        _configStore = new LibraryConfigStore(
            _repository,
            NullLogger<LibraryConfigStore>.Instance,
            new StubHostEnvironment(_tempRoot));
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
            // Best-effort cleanup.
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task UpdateSpotifyId_ReturnsNotFound_WhenArtistDoesNotExist()
    {
        var controller = CreateController();

        var result = await controller.UpdateSpotifyId(
            id: 999_999,
            new LibraryArtistsApiController.SpotifyIdUpdateRequest("0du5cEVh5yTK9QJze8zA0C"),
            CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task UpdateSpotifyId_ReturnsBadRequest_WhenSpotifyIdIsInvalid()
    {
        var artistId = await SeedLocalArtistAsync("Jayz");
        var controller = CreateController();

        var result = await controller.UpdateSpotifyId(
            artistId,
            new LibraryArtistsApiController.SpotifyIdUpdateRequest("not-a-valid-spotify-id"),
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Spotify ID should be a 22-character alphanumeric value.", badRequest.Value);
    }

    [Fact]
    public async Task UpdateSpotifyId_PersistsSpotifyId_WhenRequestIsValid()
    {
        var artistId = await SeedLocalArtistAsync("2 Pac");
        var controller = CreateController();
        const string spotifyId = "1ZwdS5xdxEREPySFridCfh";

        var result = await controller.UpdateSpotifyId(
            artistId,
            new LibraryArtistsApiController.SpotifyIdUpdateRequest(spotifyId),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Contains(spotifyId, ok.Value?.ToString(), StringComparison.Ordinal);

        var persisted = await _repository.GetArtistSourceIdAsync(artistId, "spotify", CancellationToken.None);
        Assert.Equal(spotifyId, persisted);
    }

    private LibraryArtistsApiController CreateController()
    {
        return new LibraryArtistsApiController(
            _repository,
            _configStore,
            spotifyArtistService: null!,
            artistPageCache: null!,
            spotifyMetadataCache: null!,
            _environment,
            NullLogger<LibraryArtistsApiController>.Instance);
    }

    private async Task<long> SeedLocalArtistAsync(string artistName)
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

        var artistId = await InsertAndReturnIdAsync(
            connection,
            transaction,
            "INSERT INTO artist (name) VALUES (@name) RETURNING id;",
            new Dictionary<string, object?> { ["@name"] = artistName });

        var albumId = await InsertAndReturnIdAsync(
            connection,
            transaction,
            "INSERT INTO album (artist_id, title) VALUES (@artistId, @title) RETURNING id;",
            new Dictionary<string, object?>
            {
                ["@artistId"] = artistId,
                ["@title"] = $"{artistName} Album"
            });

        var trackId = await InsertAndReturnIdAsync(
            connection,
            transaction,
            "INSERT INTO track (album_id, title) VALUES (@albumId, @title) RETURNING id;",
            new Dictionary<string, object?>
            {
                ["@albumId"] = albumId,
                ["@title"] = "Track 01"
            });

        var libraryId = await InsertAndReturnIdAsync(
            connection,
            transaction,
            "INSERT INTO library (name) VALUES (@name) RETURNING id;",
            new Dictionary<string, object?> { ["@name"] = $"Library-{Guid.NewGuid():N}" });

        var folderId = await InsertAndReturnIdAsync(
            connection,
            transaction,
            "INSERT INTO folder (root_path, display_name, library_id, enabled) VALUES (@rootPath, @displayName, @libraryId, 1) RETURNING id;",
            new Dictionary<string, object?>
            {
                ["@rootPath"] = Path.Join(_tempRoot, $"music-{Guid.NewGuid():N}"),
                ["@displayName"] = "Music",
                ["@libraryId"] = libraryId
            });

        var audioFileId = await InsertAndReturnIdAsync(
            connection,
            transaction,
            "INSERT INTO audio_file (path, folder_id) VALUES (@path, @folderId) RETURNING id;",
            new Dictionary<string, object?>
            {
                ["@path"] = Path.Join(_tempRoot, $"track-{Guid.NewGuid():N}.flac"),
                ["@folderId"] = folderId
            });

        await ExecuteNonQueryAsync(
            connection,
            transaction,
            "INSERT INTO track_local (track_id, audio_file_id) VALUES (@trackId, @audioFileId);",
            new Dictionary<string, object?>
            {
                ["@trackId"] = trackId,
                ["@audioFileId"] = audioFileId
            });

        await transaction.CommitAsync();
        return artistId;
    }

    private static async Task<long> InsertAndReturnIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        IReadOnlyDictionary<string, object?> parameters)
    {
        await using var command = new SqliteCommand(sql, connection, transaction);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Key, parameter.Value ?? DBNull.Value);
        }

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result);
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        IReadOnlyDictionary<string, object?> parameters)
    {
        await using var command = new SqliteCommand(sql, connection, transaction);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Key, parameter.Value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public StubHostEnvironment(string rootPath)
        {
            ContentRootPath = rootPath;
            ContentRootFileProvider = new PhysicalFileProvider(rootPath);
        }

        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "DeezSpoTag.Tests";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
    }

    private sealed class StubWebHostEnvironment : IWebHostEnvironment
    {
        public StubWebHostEnvironment(string rootPath)
        {
            ContentRootPath = rootPath;
            ContentRootFileProvider = new PhysicalFileProvider(rootPath);
            WebRootPath = rootPath;
            WebRootFileProvider = new PhysicalFileProvider(rootPath);
        }

        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "DeezSpoTag.Tests";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
        public string WebRootPath { get; set; }
        public IFileProvider WebRootFileProvider { get; set; }
    }
}
