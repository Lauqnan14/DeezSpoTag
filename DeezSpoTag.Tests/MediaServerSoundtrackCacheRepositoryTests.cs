using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class MediaServerSoundtrackCacheRepositoryTests : IAsyncLifetime
{
    private string _tempRoot = string.Empty;

    public Task InitializeAsync()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-soundtrack-cache-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
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
    public async Task TvShowEpisodes_RoundTripThroughPersistentCache()
    {
        var logger = new CaptureLogger<MediaServerSoundtrackCacheRepository>();
        var environment = new StubWebHostEnvironment(_tempRoot);
        var repository = new MediaServerSoundtrackCacheRepository(
            environment,
            logger);

        var cachedBefore = await repository.GetTvShowEpisodesAsync("plex", "tv-lib", "show-1", CancellationToken.None);
        Assert.Null(cachedBefore);

        var response = new MediaServerTvShowEpisodesResponseDto
        {
            ServerType = "plex",
            ServerLabel = "Plex",
            LibraryId = "tv-lib",
            LibraryName = "TV Library",
            ShowId = "show-1",
            ShowTitle = "Sample Show",
            ShowImageUrl = "https://example.com/show.jpg",
            SelectedSeasonId = null,
            TotalEpisodes = 2,
            Seasons = new List<MediaServerTvShowSeasonDto>
            {
                new()
                {
                    SeasonId = "season-1",
                    Title = "Season 1",
                    SeasonNumber = 1,
                    ImageUrl = "https://example.com/season-1.jpg",
                    EpisodeCount = 1
                },
                new()
                {
                    SeasonId = "season-2",
                    Title = "Season 2",
                    SeasonNumber = 2,
                    ImageUrl = "https://example.com/season-2.jpg",
                    EpisodeCount = 1
                }
            },
            Episodes = new List<MediaServerTvShowEpisodeDto>
            {
                new()
                {
                    EpisodeId = "episode-1",
                    SeasonId = "season-1",
                    SeasonTitle = "Season 1",
                    SeasonNumber = 1,
                    EpisodeNumber = 1,
                    Title = "Pilot",
                    Year = 2020,
                    ImageUrl = "https://example.com/episode-1.jpg"
                },
                new()
                {
                    EpisodeId = "episode-2",
                    SeasonId = "season-2",
                    SeasonTitle = "Season 2",
                    SeasonNumber = 2,
                    EpisodeNumber = 1,
                    Title = "Second",
                    Year = 2021,
                    ImageUrl = "https://example.com/episode-2.jpg"
                }
            }
        };

        await repository.UpsertTvShowEpisodesAsync(response, CancellationToken.None);
        Assert.True(logger.Warnings.Count == 0, string.Join(Environment.NewLine, logger.Warnings));

        var effectiveDataRoot = AppDataPaths.GetDataRoot(environment);
        await using (var connection = new SqliteConnection($"Data Source={Path.Join(effectiveDataRoot, "media-server", "soundtrack-cache.db")}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
SELECT payload_json
FROM soundtrack_tv_show_cache
WHERE server_type = 'plex'
  AND library_id = 'tv-lib'
  AND show_id = 'show-1'
LIMIT 1;
""";
            var rawPayload = await command.ExecuteScalarAsync();
            Assert.NotNull(rawPayload);
            Assert.IsType<string>(rawPayload);
        }

        var cachedAfter = await repository.GetTvShowEpisodesAsync("plex", "tv-lib", "show-1", CancellationToken.None);
        Assert.NotNull(cachedAfter);
        Assert.Equal("plex", cachedAfter!.ServerType);
        Assert.Equal("Plex", cachedAfter.ServerLabel);
        Assert.Equal("tv-lib", cachedAfter.LibraryId);
        Assert.Equal("TV Library", cachedAfter.LibraryName);
        Assert.Equal("show-1", cachedAfter.ShowId);
        Assert.Equal("Sample Show", cachedAfter.ShowTitle);
        Assert.Equal("https://example.com/show.jpg", cachedAfter.ShowImageUrl);
        Assert.Equal(2, cachedAfter.TotalEpisodes);
        Assert.Equal(2, cachedAfter.Seasons.Count);
        Assert.Equal(2, cachedAfter.Episodes.Count);
        Assert.Equal("https://example.com/season-1.jpg", cachedAfter.Seasons[0].ImageUrl);
        Assert.Equal("https://example.com/episode-1.jpg", cachedAfter.Episodes[0].ImageUrl);
    }

    private sealed class StubWebHostEnvironment : IWebHostEnvironment
    {
        public StubWebHostEnvironment(string rootPath)
        {
            ContentRootPath = rootPath;
            ContentRootFileProvider = new Microsoft.Extensions.FileProviders.NullFileProvider();
            EnvironmentName = Environments.Development;
        }

        public string EnvironmentName { get; set; }

        public string ApplicationName { get; set; } = "DeezSpoTag.Tests";

        public string WebRootPath { get; set; } = string.Empty;

        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();

        public string ContentRootPath { get; set; }

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
    }

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Warning)
            {
                return;
            }

            var message = formatter(state, exception);
            if (exception is not null)
            {
                message = $"{message} :: {exception.GetType().Name}: {exception.Message}";
            }

            Warnings.Add(message);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
