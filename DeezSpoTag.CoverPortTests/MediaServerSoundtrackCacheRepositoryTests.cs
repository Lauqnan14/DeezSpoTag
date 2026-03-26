using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.CoverPortTests;

public sealed class MediaServerSoundtrackCacheRepositoryTests : IAsyncLifetime
{
    private static readonly string[] MovieItemLookupIds = ["movie-1", "movie-2", "missing-item"];

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "DeezSpoTag.Tests";

        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();

        public string WebRootPath { get; set; } = string.Empty;

        public string EnvironmentName { get; set; } = Environments.Development;

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private string _tempRoot = string.Empty;
    private IWebHostEnvironment _environment = default!;

    public Task InitializeAsync()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-soundtrack-cache-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
        _environment = new TestWebHostEnvironment
        {
            ContentRootPath = _tempRoot,
            WebRootPath = _tempRoot
        };

        return Task.CompletedTask;
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
            // Best-effort cleanup.
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Upsert_Query_And_Deactivate_Items_RoundTrip()
    {
        var repository = CreateRepository();
        var now = DateTimeOffset.UtcNow;
        var items = new List<MediaServerSoundtrackItemDto>
        {
            CreateItem(
                itemId: "movie-1",
                title: "Alpha Movie",
                year: 2001,
                category: MediaServerSoundtrackConstants.MovieCategory,
                contentHash: "",
                soundtrack: new MediaServerSoundtrackMatchDto
                {
                    Kind = "spotify_album",
                    DeezerId = "123",
                    Title = "Alpha Movie OST",
                    Url = "https://open.spotify.com/album/alpha",
                    Score = 0.98
                },
                firstSeenUtc: now.AddHours(-3),
                lastSeenUtc: now.AddHours(-1)),
            CreateItem(
                itemId: "movie-2",
                title: "Beta Movie",
                year: 2005,
                category: "unknown-category",
                contentHash: "",
                soundtrack: null,
                firstSeenUtc: now.AddHours(-2),
                lastSeenUtc: now.AddMinutes(-30)),
            CreateItem(
                itemId: "show-1",
                title: "Gamma Show",
                year: 2010,
                category: MediaServerSoundtrackConstants.TvShowCategory,
                contentHash: "content-hash-gamma",
                soundtrack: null,
                firstSeenUtc: now.AddHours(-4),
                lastSeenUtc: now.AddMinutes(-20),
                libraryId: "lib-tv")
        };

        await repository.UpsertItemsAsync(items, CancellationToken.None);

        var movieItems = await repository.GetItemsAsync(
            category: MediaServerSoundtrackConstants.MovieCategory,
            serverType: "plex",
            libraryId: "lib-movies",
            offset: 0,
            limit: 200,
            cancellationToken: CancellationToken.None);
        Assert.Equal(2, movieItems.Count);
        Assert.Equal("Alpha Movie", movieItems[0].Title);
        Assert.Equal("Beta Movie", movieItems[1].Title);
        Assert.All(movieItems, item => Assert.Equal(MediaServerSoundtrackConstants.MovieCategory, item.Category));
        Assert.Contains(movieItems, item => !string.IsNullOrWhiteSpace(item.ContentHash));
        Assert.Contains(movieItems, item => item.Soundtrack?.Provider == "spotify");

        var byIds = await repository.GetItemsByIdsAsync(
            serverType: "plex",
            libraryId: "lib-movies",
            itemIds: MovieItemLookupIds,
            cancellationToken: CancellationToken.None);
        Assert.Equal(2, byIds.Count);
        Assert.True(byIds.ContainsKey("movie-1"));
        Assert.True(byIds.ContainsKey("movie-2"));

        var reloadedRepository = CreateRepository();
        var persistedMovieItems = await reloadedRepository.GetItemsAsync(
            category: MediaServerSoundtrackConstants.MovieCategory,
            serverType: "plex",
            libraryId: "lib-movies",
            offset: 0,
            limit: 200,
            cancellationToken: CancellationToken.None);
        Assert.Equal(2, persistedMovieItems.Count);

        await repository.DeactivateLibraryItemsNotSeenSinceAsync(
            serverType: "plex",
            libraryId: "lib-movies",
            cutoffUtc: now.AddDays(1),
            cancellationToken: CancellationToken.None);
        var afterDeactivate = await repository.GetItemsAsync(
            category: MediaServerSoundtrackConstants.MovieCategory,
            serverType: "plex",
            libraryId: "lib-movies",
            offset: 0,
            limit: 200,
            cancellationToken: CancellationToken.None);
        Assert.Empty(afterDeactivate);
    }

    [Fact]
    public async Task SyncState_RoundTrip_And_Listing_Works()
    {
        var repository = CreateRepository();
        var now = DateTimeOffset.UtcNow;

        await repository.UpsertLibrarySyncStateAsync(
            new MediaServerSoundtrackLibrarySyncStateDto
            {
                ServerType = "plex",
                LibraryId = "lib-movies",
                Category = MediaServerSoundtrackConstants.MovieCategory,
                Status = "running",
                LastOffset = 40,
                LastBatchCount = 20,
                TotalProcessed = 60,
                LastSyncUtc = now.AddMinutes(-5),
                LastSuccessUtc = now.AddMinutes(-30),
                LastError = null,
                UpdatedAtUtc = now.AddMinutes(-1)
            },
            CancellationToken.None);

        await repository.UpsertLibrarySyncStateAsync(
            new MediaServerSoundtrackLibrarySyncStateDto
            {
                ServerType = "jellyfin",
                LibraryId = "lib-tv",
                Category = MediaServerSoundtrackConstants.TvShowCategory,
                Status = "idle",
                LastOffset = 0,
                LastBatchCount = 0,
                TotalProcessed = 120,
                LastSyncUtc = now.AddMinutes(-10),
                LastSuccessUtc = now.AddMinutes(-10),
                LastError = "none",
                UpdatedAtUtc = now
            },
            CancellationToken.None);

        var plexState = await repository.GetLibrarySyncStateAsync("plex", "lib-movies", CancellationToken.None);
        Assert.NotNull(plexState);
        Assert.Equal("running", plexState!.Status);
        Assert.Equal(60, plexState.TotalProcessed);

        var allStates = await repository.GetLibrarySyncStatesAsync(CancellationToken.None);
        Assert.Equal(2, allStates.Count);
        Assert.Contains(allStates, entry => entry.ServerType == "plex" && entry.LibraryId == "lib-movies");
        Assert.Contains(allStates, entry => entry.ServerType == "jellyfin" && entry.LibraryId == "lib-tv");

        var missingState = await repository.GetLibrarySyncStateAsync("", "lib-movies", CancellationToken.None);
        Assert.Null(missingState);
    }

    private MediaServerSoundtrackCacheRepository CreateRepository()
    {
        return new MediaServerSoundtrackCacheRepository(
            _environment,
            NullLogger<MediaServerSoundtrackCacheRepository>.Instance);
    }

    private static MediaServerSoundtrackItemDto CreateItem(
        string itemId,
        string title,
        int year,
        string category,
        string contentHash,
        MediaServerSoundtrackMatchDto? soundtrack,
        DateTimeOffset firstSeenUtc,
        DateTimeOffset lastSeenUtc,
        string libraryId = "lib-movies")
    {
        return new MediaServerSoundtrackItemDto
        {
            ServerType = "plex",
            ServerLabel = "Plex",
            LibraryId = libraryId,
            LibraryName = libraryId == "lib-movies" ? "Movies" : "TV Shows",
            Category = category,
            ItemId = itemId,
            Title = title,
            Year = year,
            ImageUrl = $"https://img.example/{itemId}.jpg",
            ContentHash = contentHash,
            IsActive = true,
            FirstSeenUtc = firstSeenUtc,
            LastSeenUtc = lastSeenUtc,
            Soundtrack = soundtrack
        };
    }
}
