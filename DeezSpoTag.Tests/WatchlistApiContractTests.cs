using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
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
using System.Net.Http;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class WatchlistApiContractTests : IAsyncLifetime
{
    private string _tempRoot = string.Empty;
    private LibraryRepository _repository = default!;
    private LibraryConfigStore _configStore = default!;
    private PlaylistVisualService _playlistVisualService = default!;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-watchlist-api-tests-" + Path.GetRandomFileName());
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
    public async Task PlaylistWatchlist_AddStatusRemove_IsIdempotent_And_Normalized()
    {
        var controller = new LibraryPlaylistWatchlistApiController(
            _repository,
            _configStore,
            playlistWatchService: null!,
            playlistSyncService: null!,
            playlistVisualService: _playlistVisualService,
            profileResolutionService: CreateProfileResolutionService());

        var addResultOne = await controller.Add(
            new LibraryPlaylistWatchlistApiController.PlaylistWatchlistRequest(
                Source: "  SPOTIFY ",
                SourceId: "  pl-123  ",
                Name: "Road Mix",
                ImageUrl: null,
                Description: "desc",
                TrackCount: 42),
            CancellationToken.None);
        var addOkOne = Assert.IsType<OkObjectResult>(addResultOne);
        using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(addOkOne.Value)))
        {
            Assert.Equal("spotify", GetStringProperty(doc.RootElement, "source"));
            Assert.Equal("pl-123", GetStringProperty(doc.RootElement, "sourceId"));
            Assert.Equal("Road Mix", GetStringProperty(doc.RootElement, "name"));
        }

        var addResultTwo = await controller.Add(
            new LibraryPlaylistWatchlistApiController.PlaylistWatchlistRequest(
                Source: "spotify",
                SourceId: "pl-123",
                Name: "Road Mix Updated",
                ImageUrl: null,
                Description: null,
                TrackCount: null),
            CancellationToken.None);
        Assert.IsType<OkObjectResult>(addResultTwo);

        var allResult = await controller.GetAll(CancellationToken.None);
        var allOk = Assert.IsType<OkObjectResult>(allResult);
        using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(allOk.Value)))
        {
            Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
            Assert.Single(doc.RootElement.EnumerateArray());
            var first = doc.RootElement[0];
            Assert.Equal("spotify", GetStringProperty(first, "source"));
            Assert.Equal("pl-123", GetStringProperty(first, "sourceId"));
            Assert.Equal("Road Mix Updated", GetStringProperty(first, "name"));
        }

        var statusResult = await controller.GetStatus("  SpOtIfY  ", "  pl-123 ", CancellationToken.None);
        var statusOk = Assert.IsType<OkObjectResult>(statusResult);
        using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(statusOk.Value)))
        {
            Assert.True(GetBooleanProperty(doc.RootElement, "watching"));
        }

        var removeResult = await controller.Remove(" SPOTIFY ", " pl-123 ", CancellationToken.None);
        var removeOk = Assert.IsType<OkObjectResult>(removeResult);
        using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(removeOk.Value)))
        {
            Assert.True(GetBooleanProperty(doc.RootElement, "removed"));
        }
    }

    [Fact]
    public async Task PlaylistWatchlist_Add_InvalidRequest_ReturnsBadRequest()
    {
        var controller = new LibraryPlaylistWatchlistApiController(
            _repository,
            _configStore,
            playlistWatchService: null!,
            playlistSyncService: null!,
            playlistVisualService: _playlistVisualService,
            profileResolutionService: CreateProfileResolutionService());

        var result = await controller.Add(
            new LibraryPlaylistWatchlistApiController.PlaylistWatchlistRequest(
                Source: "",
                SourceId: "",
                Name: "",
                ImageUrl: null,
                Description: null,
                TrackCount: null),
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task PlaylistWatchlist_Add_AppliesSavedGlobalRoutingRules()
    {
        var routeFolder = await AddEligibleFolderAsync("Route Folder", "/music/route");
        var controller = new LibraryPlaylistWatchlistApiController(
            _repository,
            _configStore,
            playlistWatchService: null!,
            playlistSyncService: null!,
            playlistVisualService: _playlistVisualService,
            profileResolutionService: CreateProfileResolutionService());

        var applyResult = await controller.ApplyRoutingRulesGlobally(
            "spotify",
            "template-source",
            new List<PlaylistTrackRoutingRule>
            {
                new("genre", "contains", "Reggae", routeFolder.Id, 0)
            },
            CancellationToken.None);
        Assert.IsType<OkObjectResult>(applyResult);

        var addResult = await controller.Add(
            new LibraryPlaylistWatchlistApiController.PlaylistWatchlistRequest(
                Source: "spotify",
                SourceId: "37i9dQZEVXcTTTHAwtPLUs",
                Name: "Test Playlist",
                ImageUrl: null,
                Description: null,
                TrackCount: 50),
            CancellationToken.None);
        Assert.IsType<OkObjectResult>(addResult);

        var preference = await _repository.GetPlaylistWatchPreferenceAsync(
            "spotify",
            "37i9dQZEVXcTTTHAwtPLUs",
            CancellationToken.None);
        Assert.NotNull(preference);
        var rule = Assert.Single(preference!.RoutingRules!);
        Assert.Equal("genre", rule.ConditionField);
        Assert.Equal("Reggae", rule.ConditionValue);
        Assert.Equal(routeFolder.Id, rule.DestinationFolderId);
    }

    [Fact]
    public async Task PlaylistWatchlist_Add_DoesNotApplyGlobalRoutingRuleForConfiguredDestinationFolder()
    {
        var defaultFolder = await AddEligibleFolderAsync("Default Folder", "/music/default");
        var routeFolder = await AddEligibleFolderAsync("Route Folder", "/music/route");
        var controller = new LibraryPlaylistWatchlistApiController(
            _repository,
            _configStore,
            playlistWatchService: null!,
            playlistSyncService: null!,
            playlistVisualService: _playlistVisualService,
            profileResolutionService: CreateProfileResolutionService());

        var applyResult = await controller.ApplyRoutingRulesGlobally(
            "spotify",
            "template-source",
            new List<PlaylistTrackRoutingRule>
            {
                new("genre", "contains", "Default", defaultFolder.Id, 0),
                new("genre", "contains", "Route", routeFolder.Id, 1)
            },
            CancellationToken.None);
        Assert.IsType<OkObjectResult>(applyResult);

        await _repository.UpsertPlaylistWatchPreferenceAsync(
            new LibraryRepository.PlaylistWatchPreferenceUpsertInput(
                Source: "spotify",
                SourceId: "37i9dQZEVXcTTTHAwtPLUs",
                DestinationFolderId: defaultFolder.Id,
                Service: null,
                PreferredEngine: null,
                DownloadVariantMode: null,
                SyncMode: null,
                UpdateArtwork: true,
                ReuseSavedArtwork: false,
                RoutingRules: null,
                IgnoreRules: null));

        var addResult = await controller.Add(
            new LibraryPlaylistWatchlistApiController.PlaylistWatchlistRequest(
                Source: "spotify",
                SourceId: "37i9dQZEVXcTTTHAwtPLUs",
                Name: "Test Playlist",
                ImageUrl: null,
                Description: null,
                TrackCount: 50),
            CancellationToken.None);
        Assert.IsType<OkObjectResult>(addResult);

        var preference = await _repository.GetPlaylistWatchPreferenceAsync(
            "spotify",
            "37i9dQZEVXcTTTHAwtPLUs",
            CancellationToken.None);
        Assert.NotNull(preference);
        var rule = Assert.Single(preference!.RoutingRules!);
        Assert.Equal("Route", rule.ConditionValue);
        Assert.Equal(routeFolder.Id, rule.DestinationFolderId);
    }

    [Fact]
    public async Task ArtistWatchlist_AddStatusRemove_SpotifyContract_Works()
    {
        var controller = new LibraryWatchlistApiController(
            _repository,
            _configStore,
            profileResolutionService: CreateProfileResolutionService());

        var addResult = await controller.AddSpotify(
            new LibraryWatchlistApiController.SpotifyWatchlistRequest(
                SpotifyId: "  sp-artist-1 ",
                ArtistName: "Artist One",
                DeezerId: null),
            CancellationToken.None);
        Assert.IsType<OkObjectResult>(addResult);

        var statusResult = await controller.GetSpotifyStatus("sp-artist-1", CancellationToken.None);
        var statusOk = Assert.IsType<OkObjectResult>(statusResult);
        using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(statusOk.Value)))
        {
            Assert.True(GetBooleanProperty(doc.RootElement, "watching"));
        }

        var removeResult = await controller.RemoveSpotify(" sp-artist-1 ", CancellationToken.None);
        var removeOk = Assert.IsType<OkObjectResult>(removeResult);
        using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(removeOk.Value)))
        {
            Assert.True(GetBooleanProperty(doc.RootElement, "removed"));
        }

        var statusAfterRemove = await controller.GetSpotifyStatus("sp-artist-1", CancellationToken.None);
        var statusAfterRemoveOk = Assert.IsType<OkObjectResult>(statusAfterRemove);
        using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(statusAfterRemoveOk.Value)))
        {
            Assert.False(GetBooleanProperty(doc.RootElement, "watching"));
        }
    }

    [Fact]
    public async Task ArtistWatchlist_Add_InvalidRequest_ReturnsBadRequest()
    {
        var controller = new LibraryWatchlistApiController(
            _repository,
            _configStore,
            profileResolutionService: CreateProfileResolutionService());

        var result = await controller.Add(
            new LibraryWatchlistApiController.WatchlistRequest(
                ArtistId: null,
                ArtistName: string.Empty),
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    private async Task<FolderDto> AddEligibleFolderAsync(string displayName, string rootPath)
    {
        var profileId = $"{displayName}-profile";
        var environment = new StubWebHostEnvironment(_tempRoot);
        var profileService = new TaggingProfileService(
            environment,
            NullLogger<TaggingProfileService>.Instance);
        var profiles = await profileService.LoadAsync();
        profiles.Add(new DeezSpoTag.Core.Models.Settings.TaggingProfile
        {
            Id = profileId,
            Name = displayName
        });
        await profileService.SaveAsync(profiles);

        var folder = await _repository.AddFolderAsync(
            new LibraryRepository.FolderUpsertInput(
                RootPath: rootPath,
                DisplayName: displayName,
                Enabled: true,
                LibraryName: "Music",
                DesiredQuality: "flac",
                ConvertEnabled: false,
                ConvertFormat: null,
                ConvertBitrate: null));

        var activated = await _repository.UpdateFolderProfileAsync(folder.Id, profileId);
        Assert.NotNull(activated);
        var enabled = await _repository.UpdateFolderAutoTagEnabledAsync(folder.Id, true);
        Assert.NotNull(enabled);
        return enabled!;
    }

    private AutoTagProfileResolutionService CreateProfileResolutionService()
    {
        var environment = new StubWebHostEnvironment(_tempRoot);
        return new AutoTagProfileResolutionService(
            new TaggingProfileService(environment, NullLogger<TaggingProfileService>.Instance),
            new AutoTagDefaultsStore(environment, NullLogger<AutoTagDefaultsStore>.Instance),
            _repository,
            NullLogger<AutoTagProfileResolutionService>.Instance);
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

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var exact))
        {
            return exact.GetString();
        }

        var pascal = char.ToUpperInvariant(propertyName[0]) + propertyName[1..];
        if (element.TryGetProperty(pascal, out var alternate))
        {
            return alternate.GetString();
        }

        throw new KeyNotFoundException($"Property '{propertyName}' not found.");
    }

    private static bool GetBooleanProperty(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var exact))
        {
            return exact.GetBoolean();
        }

        var pascal = char.ToUpperInvariant(propertyName[0]) + propertyName[1..];
        if (element.TryGetProperty(pascal, out var alternate))
        {
            return alternate.GetBoolean();
        }

        throw new KeyNotFoundException($"Property '{propertyName}' not found.");
    }
}
