using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Controllers.Api;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class WatchlistControllerConcurrencyTests : IAsyncLifetime
{
    private string _tempRoot = string.Empty;
    private LibraryRepository _repository = default!;
    private LibraryConfigStore _configStore = default!;
    private PlaylistVisualService _playlistVisualService = default!;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-watchlist-controller-concurrency-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);

        var dbPath = Path.Join(_tempRoot, "library.db");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = $"Data Source={dbPath}"
            })
            .Build();

        var dbService = new LibraryDbService(configuration, NullLogger<LibraryDbService>.Instance);
        await dbService.EnsureSchemaAsync();

        _repository = new LibraryRepository(configuration, NullLogger<LibraryRepository>.Instance);
        _configStore = new LibraryConfigStore(
            _repository,
            NullLogger<LibraryConfigStore>.Instance,
            new StubHostEnvironment(_tempRoot));
        _playlistVisualService = new PlaylistVisualService(
            new StubHttpClientFactory(),
            new StubWebHostEnvironment(_tempRoot),
            NullLogger<PlaylistVisualService>.Instance);
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
    public async Task ArtistWatchlist_ConcurrentAddAndRemove_SameSpotifyKey_RemainsConsistent()
    {
        var controller = new LibraryWatchlistApiController(
            _repository,
            _configStore,
            artistWatchService: null!);

        var addTasks = Enumerable.Range(0, 24)
            .Select(index => controller.AddSpotify(
                new LibraryWatchlistApiController.SpotifyWatchlistRequest(
                    SpotifyId: index % 2 == 0 ? " sp-concurrent " : "SP-CONCURRENT",
                    ArtistName: "Concurrent Artist",
                    DeezerId: null),
                CancellationToken.None))
            .ToArray();
        var addResults = await Task.WhenAll(addTasks);
        Assert.All(addResults, result => Assert.IsType<OkObjectResult>(result));

        var statusAfterAdd = await controller.GetSpotifyStatus("sp-concurrent", CancellationToken.None);
        var addStatusOk = Assert.IsType<OkObjectResult>(statusAfterAdd);
        Assert.Contains("\"watching\":true", System.Text.Json.JsonSerializer.Serialize(addStatusOk.Value), StringComparison.OrdinalIgnoreCase);

        var entries = await _repository.GetWatchlistAsync(CancellationToken.None);
        var matchingEntries = entries.Where(item => string.Equals(item.SpotifyId, "sp-concurrent", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Single(matchingEntries);

        var removeTasks = Enumerable.Range(0, 24)
            .Select(_ => controller.RemoveSpotify(" SP-CONCURRENT ", CancellationToken.None))
            .ToArray();
        var removeResults = await Task.WhenAll(removeTasks);
        Assert.All(removeResults, result => Assert.IsType<OkObjectResult>(result));

        var statusAfterRemove = await controller.GetSpotifyStatus("sp-concurrent", CancellationToken.None);
        var removeStatusOk = Assert.IsType<OkObjectResult>(statusAfterRemove);
        Assert.Contains("\"watching\":false", System.Text.Json.JsonSerializer.Serialize(removeStatusOk.Value), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlaylistWatchlist_ConcurrentAddAndRemove_SameKey_RemainsConsistent()
    {
        var controller = new LibraryPlaylistWatchlistApiController(
            _repository,
            _configStore,
            playlistWatchService: null!,
            playlistSyncService: null!,
            playlistVisualService: _playlistVisualService);

        var addTasks = Enumerable.Range(0, 30)
            .Select(index => controller.Add(
                new LibraryPlaylistWatchlistApiController.PlaylistWatchlistRequest(
                    Source: index % 2 == 0 ? " SPOTIFY " : "spotify",
                    SourceId: index % 3 == 0 ? " pl-race " : "pl-race",
                    Name: "Race Playlist",
                    ImageUrl: null,
                    Description: null,
                    TrackCount: 10),
                CancellationToken.None))
            .ToArray();
        var addResults = await Task.WhenAll(addTasks);
        Assert.All(addResults, result => Assert.IsType<OkObjectResult>(result));

        var statusAfterAdd = await controller.GetStatus("spotify", "pl-race", CancellationToken.None);
        var addStatusOk = Assert.IsType<OkObjectResult>(statusAfterAdd);
        Assert.Contains("\"watching\":true", System.Text.Json.JsonSerializer.Serialize(addStatusOk.Value), StringComparison.OrdinalIgnoreCase);

        var allAfterAdd = await _repository.GetPlaylistWatchlistAsync(CancellationToken.None);
        var matchingRows = allAfterAdd.Where(item =>
            string.Equals(item.Source, "spotify", StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.SourceId, "pl-race", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Single(matchingRows);

        var removeTasks = Enumerable.Range(0, 30)
            .Select(_ => controller.Remove(" SPOTIFY ", " pl-race ", CancellationToken.None))
            .ToArray();
        var removeResults = await Task.WhenAll(removeTasks);
        Assert.All(removeResults, result => Assert.IsType<OkObjectResult>(result));

        var statusAfterRemove = await controller.GetStatus("spotify", "pl-race", CancellationToken.None);
        var removeStatusOk = Assert.IsType<OkObjectResult>(statusAfterRemove);
        Assert.Contains("\"watching\":false", System.Text.Json.JsonSerializer.Serialize(removeStatusOk.Value), StringComparison.OrdinalIgnoreCase);
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

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
