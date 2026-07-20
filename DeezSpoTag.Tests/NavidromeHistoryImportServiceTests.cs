using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations.Navidrome;
using DeezSpoTag.Integrations.Jellyfin;
using DeezSpoTag.Integrations.Plex;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class NavidromeHistoryImportServiceTests : IAsyncLifetime
{
    private string _root = string.Empty;
    private string _dbPath = string.Empty;
    private LibraryRepository _repository = default!;
    private PlatformAuthService _authService = default!;

    public async Task InitializeAsync()
    {
        _root = Path.Join(Path.GetTempPath(), $"deezspotag-navidrome-history-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Join(_root, "library.db");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = $"Data Source={_dbPath}"
            })
            .Build();
        await new LibraryDbService(configuration, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();
        _repository = new LibraryRepository(configuration, NullLogger<LibraryRepository>.Instance);
        await SeedLocalTrackAsync();

        var keyRoot = Path.Join(_root, "keys");
        Directory.CreateDirectory(keyRoot);
        _authService = new PlatformAuthService(
            new StubWebHostEnvironment(_root),
            NullLogger<PlatformAuthService>.Instance,
            DataProtectionProvider.Create(new DirectoryInfo(keyRoot)));
        await _authService.SaveAsync(new PlatformAuthState
        {
            Navidrome = new NavidromeAuth
            {
                Url = "http://navidrome.local",
                Username = "listener",
                Password = "secret",
                ServerName = "Test Navidrome"
            }
        });
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task ImportAsync_ResolvesByMetadata_AssignsLibrary_AndDeduplicates()
    {
        using var handler = new HistoryHandler();
        using var httpClient = new HttpClient(handler);
        var navidromeClient = new NavidromeApiClient(httpClient);
        var catalog = new MelodayRemoteLibraryCatalog(
            new PlexApiClient(NullLogger<PlexApiClient>.Instance, httpClient),
            new JellyfinApiClient(httpClient),
            navidromeClient,
            NullLogger<MelodayRemoteLibraryCatalog>.Instance);
        var service = new NavidromeHistoryImportService(
            navidromeClient,
            _authService,
            _repository,
            catalog,
            NullLogger<NavidromeHistoryImportService>.Instance);

        Assert.Equal(1, await service.ImportAsync());
        Assert.Equal(0, await service.ImportAsync());

        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT ph.source, ph.library_id, ph.track_id, ph.plex_track_key, pu.plex_user_id, ph.remote_library_id
FROM play_history ph
JOIN plex_user pu ON pu.id = ph.plex_user_id;";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("navidrome", reader.GetString(0));
        Assert.Equal(1, reader.GetInt64(1));
        Assert.Equal(1, reader.GetInt64(2));
        Assert.Equal("/remote/music/Artist One/Album One/Track One.flac", reader.GetString(3));
        Assert.Equal("navidrome:listener", reader.GetString(4));
        Assert.Equal("1", reader.GetString(5));
        Assert.False(await reader.ReadAsync());
    }

    private async Task SeedLocalTrackAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO library (id, name) VALUES (1, 'Music');
INSERT INTO folder (id, root_path, display_name, enabled, library_id, desired_quality_value)
VALUES (1, '/local/music', 'Music', 1, 1, 'cd_lossless');
INSERT INTO artist (id, name) VALUES (1, 'Artist One');
INSERT INTO album (id, artist_id, title) VALUES (1, 1, 'Album One');
INSERT INTO track (id, album_id, title, duration_ms) VALUES (1, 1, 'Track One', 181500);
INSERT INTO audio_file (id, path, relative_path, folder_id, duration_ms)
VALUES (1, '/local/music/Artist One/Album One/Track One.flac', 'Artist One/Album One/Track One.flac', 1, 181500);
INSERT INTO track_local (track_id, audio_file_id) VALUES (1, 1);";
        await command.ExecuteNonQueryAsync();
    }

    private sealed class HistoryHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var json = request.RequestUri!.AbsolutePath.EndsWith("/auth/login", StringComparison.Ordinal)
                ? """{"token":"jwt-token"}"""
                : request.RequestUri.AbsolutePath.Contains("getMusicFolders", StringComparison.Ordinal)
                    ? """{"subsonic-response":{"status":"ok","musicFolders":{"musicFolder":[{"id":1,"name":"Music"}]}}}"""
                    : """
                  [
                    {
                      "id":"nav-song-1",
                      "title":"Track One",
                      "artist":"Artist One",
                      "duration":181.5,
                      "path":"Artist One/Album One/Track One.flac",
                      "libraryPath":"/remote/music",
                      "playDate":"2026-07-12T12:00:00Z",
                      "playCount":3
                    }
                  ]
                  """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class StubWebHostEnvironment(string contentRoot) : IWebHostEnvironment, IAppDataRootOverride
    {
        public string ApplicationName { get; set; } = "DeezSpoTag.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = contentRoot;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string? AppDataRoot { get; } = contentRoot;
    }
}
