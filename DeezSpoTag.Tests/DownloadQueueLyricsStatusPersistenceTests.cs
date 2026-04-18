using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download.Queue;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DownloadQueueLyricsStatusPersistenceTests
{
    [Fact]
    public async Task UpdateFinalDestinationsAsync_PersistsLyricsStatus_FromExistingSidecars()
    {
        await using var context = await CreateContextAsync();
        var queueUuid = "lyrics-sidecars-1";
        var outputPath = Path.Join(context.TempRoot, "Artist", "Album", "01 - Track.m4a");
        var ttmlPath = Path.ChangeExtension(outputPath, ".ttml");
        var lrcPath = Path.ChangeExtension(outputPath, ".lrc");

        CreateFile(outputPath, "audio");
        CreateFile(ttmlPath, "<tt></tt>");
        CreateFile(lrcPath, "[00:00.00]line");

        await context.QueueRepository.EnqueueAsync(CreateQueueItem(queueUuid), CancellationToken.None);

        var finalDestinationsJson = JsonSerializer.Serialize(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [outputPath] = outputPath,
            [ttmlPath] = ttmlPath,
            [lrcPath] = lrcPath
        });

        await context.QueueRepository.UpdateFinalDestinationsAsync(queueUuid, finalDestinationsJson, cancellationToken: CancellationToken.None);

        var lyricsStatus = await context.GetLyricsStatusAsync(queueUuid);
        Assert.Equal("time-synced,synced", lyricsStatus);
    }

    [Fact]
    public async Task UpdateFinalDestinationsAsync_DoesNotPersistLyricsStatus_WhenAudioOutputExistsWithoutSidecars()
    {
        await using var context = await CreateContextAsync();
        var queueUuid = "lyrics-none-1";
        var outputPath = Path.Join(context.TempRoot, "Artist", "Album", "01 - Track.m4a");

        CreateFile(outputPath, "audio");
        await context.QueueRepository.EnqueueAsync(CreateQueueItem(queueUuid), CancellationToken.None);

        var finalDestinationsJson = JsonSerializer.Serialize(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [outputPath] = outputPath
        });

        await context.QueueRepository.UpdateFinalDestinationsAsync(queueUuid, finalDestinationsJson, cancellationToken: CancellationToken.None);

        var lyricsStatus = await context.GetLyricsStatusAsync(queueUuid);
        Assert.Null(lyricsStatus);
    }

    [Fact]
    public async Task UpdateFinalDestinationsAsync_PersistsLyricsStatus_FromDeclaredSidecarsEvenWhenFilesMoved()
    {
        await using var context = await CreateContextAsync();
        var queueUuid = "lyrics-sidecars-declared-1";
        var outputPath = Path.Join(context.TempRoot, "Artist", "Album", "01 - Track.m4a");
        var ttmlPath = Path.ChangeExtension(outputPath, ".ttml");
        var lrcPath = Path.ChangeExtension(outputPath, ".lrc");

        await context.QueueRepository.EnqueueAsync(CreateQueueItem(queueUuid), CancellationToken.None);

        var finalDestinationsJson = JsonSerializer.Serialize(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [outputPath] = outputPath,
            [ttmlPath] = ttmlPath,
            [lrcPath] = lrcPath
        });

        await context.QueueRepository.UpdateFinalDestinationsAsync(queueUuid, finalDestinationsJson, cancellationToken: CancellationToken.None);

        var lyricsStatus = await context.GetLyricsStatusAsync(queueUuid);
        Assert.Equal("time-synced,synced", lyricsStatus);
    }

    [Fact]
    public async Task UpdatePayloadAsync_PersistsLyricsStatus_FromPayloadFilesWhenSidecarExists()
    {
        await using var context = await CreateContextAsync();
        var queueUuid = "lyrics-payload-1";
        var outputPath = Path.Join(context.TempRoot, "Artist", "Album", "01 - Track.m4a");
        var lrcPath = Path.ChangeExtension(outputPath, ".lrc");

        CreateFile(outputPath, "audio");
        CreateFile(lrcPath, "[00:00.00]line");
        await context.QueueRepository.EnqueueAsync(CreateQueueItem(queueUuid), CancellationToken.None);

        var payloadJson = JsonSerializer.Serialize(new
        {
            FilePath = outputPath,
            Files = new[]
            {
                new Dictionary<string, object>
                {
                    ["path"] = outputPath
                },
                new Dictionary<string, object>
                {
                    ["path"] = lrcPath
                }
            }
        });

        await context.QueueRepository.UpdatePayloadAsync(queueUuid, payloadJson, CancellationToken.None);

        var lyricsStatus = await context.GetLyricsStatusAsync(queueUuid);
        Assert.Equal("synced", lyricsStatus);
    }

    [Fact]
    public async Task UpdatePayloadAsync_PersistsLyricsStatus_FromPayloadStatusTokenWithoutFiles()
    {
        await using var context = await CreateContextAsync();
        var queueUuid = "lyrics-payload-status-1";
        var outputPath = Path.Join(context.TempRoot, "Artist", "Album", "01 - Track.m4a");

        await context.QueueRepository.EnqueueAsync(CreateQueueItem(queueUuid), CancellationToken.None);

        var payloadJson = JsonSerializer.Serialize(new
        {
            FilePath = outputPath,
            LyricsStatus = "time-synced,synced"
        });

        await context.QueueRepository.UpdatePayloadAsync(queueUuid, payloadJson, CancellationToken.None);

        var lyricsStatus = await context.GetLyricsStatusAsync(queueUuid);
        Assert.Equal("time-synced,synced", lyricsStatus);
    }

    private static Task<TestContext> CreateContextAsync()
    {
        var tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-queue-lyrics-status-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);
        var queueDbPath = Path.Join(tempRoot, "queue.db");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Queue"] = $"Data Source={queueDbPath}",
                ["DataDirectory"] = tempRoot
            })
            .Build();

        var queueRepository = new DownloadQueueRepository(config, NullLogger<DownloadQueueRepository>.Instance);
        return Task.FromResult(new TestContext(tempRoot, queueDbPath, queueRepository));
    }

    private static DownloadQueueItem CreateQueueItem(string queueUuid)
    {
        return new DownloadQueueItem(
            Id: 0,
            QueueUuid: queueUuid,
            Engine: "apple",
            ArtistName: "Artist",
            TrackTitle: "Track",
            Isrc: null,
            DeezerTrackId: null,
            DeezerAlbumId: null,
            DeezerArtistId: null,
            SpotifyTrackId: null,
            SpotifyAlbumId: null,
            SpotifyArtistId: null,
            AppleTrackId: "123",
            AppleAlbumId: "456",
            AppleArtistId: "789",
            DurationMs: 150000,
            DestinationFolderId: null,
            QualityRank: 50,
            QueueOrder: null,
            ContentType: "stereo",
            Status: "queued",
            PayloadJson: null,
            Progress: 0,
            Downloaded: 0,
            Failed: 0,
            Error: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);
    }

    private static void CreateFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Missing directory path."));
        File.WriteAllText(path, content);
    }

    private sealed class TestContext : IAsyncDisposable
    {
        public TestContext(string tempRoot, string queueDbPath, DownloadQueueRepository queueRepository)
        {
            TempRoot = tempRoot;
            QueueDbPath = queueDbPath;
            QueueRepository = queueRepository;
        }

        public string TempRoot { get; }
        public string QueueDbPath { get; }
        public DownloadQueueRepository QueueRepository { get; }

        public async Task<string?> GetLyricsStatusAsync(string queueUuid)
        {
            await using var connection = new SqliteConnection($"Data Source={QueueDbPath}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT lyrics_status FROM download_task WHERE queue_uuid = $queueUuid LIMIT 1;";
            command.Parameters.AddWithValue("$queueUuid", queueUuid);
            var result = await command.ExecuteScalarAsync();
            return result is null or DBNull ? null : Convert.ToString(result);
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                if (Directory.Exists(TempRoot))
                {
                    Directory.Delete(TempRoot, recursive: true);
                }
            }
            catch
            {
                // Best effort cleanup.
            }

            return ValueTask.CompletedTask;
        }
    }
}
