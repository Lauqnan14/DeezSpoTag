using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations.Jellyfin;
using DeezSpoTag.Integrations.Plex;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class PlaylistSyncReadinessTests : IAsyncLifetime
{
    private string _tempRoot = string.Empty;
    private LibraryRepository _repository = default!;
    private PlaylistSyncService _syncService = default!;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-sync-readiness-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);

        var dbPath = Path.Join(_tempRoot, "library.db");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = $"Data Source={dbPath}",
                ["DataDirectory"] = _tempRoot
            })
            .Build();

        var dbService = new LibraryDbService(configuration, NullLogger<LibraryDbService>.Instance);
        await dbService.EnsureSchemaAsync();

        _repository = new LibraryRepository(configuration, NullLogger<LibraryRepository>.Instance);
        var environment = new StubWebHostEnvironment(_tempRoot);
        var authService = new PlatformAuthService(environment, NullLogger<PlatformAuthService>.Instance);
        var plexClient = new PlexApiClient(
            NullLogger<PlexApiClient>.Instance,
            new HttpClient(new StubHttpMessageHandler()));
        var jellyfinClient = new JellyfinApiClient(new HttpClient(new StubHttpMessageHandler()));
        _syncService = new PlaylistSyncService(new PlaylistSyncService.PlaylistSyncDependencies
        {
            LibraryRepository = _repository,
            SpotifyMetadataService = null!,
            PlexApiClient = plexClient,
            JellyfinApiClient = jellyfinClient,
            AuthService = authService,
            PlaylistVisualService = null!,
            MediaServerRefreshService = null!,
            Logger = NullLogger<PlaylistSyncService>.Instance
        });
    }

    public Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
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
    public async Task CheckTrackReadyForAutomaticSyncAsync_ReturnsTerminal_WhenNoTargetServerSelected()
    {
        var readiness = await _syncService.CheckTrackReadyForAutomaticSyncAsync(
            CreatePlaylist(),
            preference: null,
            CreateCandidate(),
            CancellationToken.None);

        Assert.False(readiness.Ready);
        Assert.True(readiness.Terminal);
        Assert.Equal("No target server selected.", readiness.Message);
    }

    [Fact]
    public async Task CheckTrackReadyForAutomaticSyncAsync_Waits_WhenTrackIsMissingFromLocalLibrary()
    {
        var readiness = await _syncService.CheckTrackReadyForAutomaticSyncAsync(
            CreatePlaylist(),
            new PlaylistWatchPreferenceDto(
                Source: "spotify",
                SourceId: "playlist-1",
                DestinationFolderId: 12,
                Service: "plex",
                PreferredEngine: null,
                DownloadVariantMode: null,
                SyncMode: "mirror",
                UpdateArtwork: false,
                ReuseSavedArtwork: false,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow,
                RoutingRules: null,
                IgnoreRules: null,
                AtmosDestinationFolderId: null),
            CreateCandidate(),
            CancellationToken.None);

        Assert.False(readiness.Ready);
        Assert.False(readiness.Terminal);
        Assert.Equal("plex", readiness.Service);
        Assert.Equal("Track is not visible in the DeezSpoTag library yet.", readiness.Message);
    }

    private static PlaylistWatchlistDto CreatePlaylist()
        => new(
            Id: 1,
            Source: "spotify",
            SourceId: "playlist-1",
            Name: "Road Mix",
            ImageUrl: null,
            Description: null,
            TrackCount: 1,
            CreatedAt: DateTimeOffset.UtcNow);

    private static PlaylistWatchService.PlaylistTrackCandidate CreateCandidate()
        => new(
            TrackSourceId: "track-1",
            Isrc: "USRC17607839",
            Title: "Song One",
            Artist: "Artist One",
            Album: "Album One",
            ReleaseYear: 2024,
            DurationMs: 180000,
            Explicit: false,
            Genres: Array.Empty<string>());

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }

    private sealed class StubWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = contentRootPath;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "DeezSpoTag.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRootPath);
        public string ContentRootPath { get; set; } = contentRootPath;
        public string EnvironmentName { get; set; } = Environments.Development;
    }
}
