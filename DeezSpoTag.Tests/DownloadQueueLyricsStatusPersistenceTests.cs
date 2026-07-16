using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download.Queue;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DownloadQueueLyricsArtifactPersistenceTests
{
    [Fact]
    public async Task UpdateLyricsArtifactsAsync_PersistsResolvedAndDownloadedState()
    {
        await using var context = await CreateContextAsync();
        var queueUuid = "lyrics-prefetch-active-1";
        await context.QueueRepository.EnqueueAsync(CreateQueueItem(queueUuid), CancellationToken.None);

        var state = new LyricsArtifactState
        {
            Revision = 10,
            Status = "completed",
            RequestedFormats = ["ttml", "lrc"],
            ResolvedFormats = ["ttml", "lrc"],
            DownloadedFormats = ["lrc"],
            FilesByFormat = new Dictionary<string, string> { ["lrc"] = "/music/track.lrc" }
        };
        await context.QueueRepository.UpdateLyricsArtifactsAsync(queueUuid, state, CancellationToken.None);

        var payload = await context.GetPayloadAsync(queueUuid);
        using var document = JsonDocument.Parse(payload!);
        var artifacts = document.RootElement.GetProperty("lyricsArtifacts");
        Assert.Equal(10, artifacts.GetProperty("revision").GetInt64());
        Assert.Equal("completed", artifacts.GetProperty("status").GetString());
        Assert.Equal(["ttml", "lrc"], artifacts.GetProperty("resolvedFormats").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public async Task PayloadAndEngineUpdates_PreserveNewestLyricsArtifactRevision()
    {
        await using var context = await CreateContextAsync();
        var queueUuid = "lyrics-prefetch-retry-1";
        await context.QueueRepository.EnqueueAsync(CreateQueueItem(queueUuid), CancellationToken.None);
        var state = new LyricsArtifactState
        {
            Revision = 20,
            Status = "resolved",
            ResolvedFormats = ["lrc"]
        };
        await context.QueueRepository.UpdateLyricsArtifactsAsync(queueUuid, state, CancellationToken.None);
        var staleAccepted = await context.QueueRepository.UpdateLyricsArtifactsAsync(queueUuid, new LyricsArtifactState
        {
            Revision = 10,
            Status = "unavailable"
        }, CancellationToken.None);
        Assert.False(staleAccepted);

        await context.QueueRepository.UpdatePayloadAsync(
            queueUuid,
            """{"Title":"Track","lyricsArtifacts":{"revision":1,"status":"fetching"}}""",
            CancellationToken.None);
        using (var preserved = JsonDocument.Parse((await context.GetPayloadAsync(queueUuid))!))
        {
            Assert.Equal(20, preserved.RootElement.GetProperty("lyricsArtifacts").GetProperty("revision").GetInt64());
            Assert.Equal("resolved", preserved.RootElement.GetProperty("lyricsArtifacts").GetProperty("status").GetString());
        }

        await context.QueueRepository.UpdatePayloadAndEngineAsync(
            queueUuid,
            "qobuz",
            """{"Title":"Track","lyricsArtifacts":{"revision":0,"status":"disabled"}}""",
            CancellationToken.None);
        using var handedOff = JsonDocument.Parse((await context.GetPayloadAsync(queueUuid))!);
        Assert.Equal(20, handedOff.RootElement.GetProperty("lyricsArtifacts").GetProperty("revision").GetInt64());
        Assert.Equal("resolved", handedOff.RootElement.GetProperty("lyricsArtifacts").GetProperty("status").GetString());
    }

    [Fact]
    public async Task UpdateFinalDestinationsAsync_RebasesCanonicalLyricsSidecarPaths()
    {
        await using var context = await CreateContextAsync();
        var queueUuid = "lyrics-rebase-1";
        await context.QueueRepository.EnqueueAsync(CreateQueueItem(queueUuid), CancellationToken.None);
        var source = Path.Join(context.TempRoot, "staging", "track.lrc");
        var destination = Path.Join(context.TempRoot, "library", "track.lrc");
        await context.QueueRepository.UpdateLyricsArtifactsAsync(queueUuid, new LyricsArtifactState
        {
            Revision = 30,
            Status = "completed",
            ResolvedFormats = ["lrc"],
            DownloadedFormats = ["lrc"],
            FilesByFormat = new Dictionary<string, string> { ["lrc"] = source }
        }, CancellationToken.None);

        await context.QueueRepository.UpdateFinalDestinationsAsync(
            queueUuid,
            JsonSerializer.Serialize(new Dictionary<string, string> { [source] = destination }),
            cancellationToken: CancellationToken.None);

        using var payload = JsonDocument.Parse((await context.GetPayloadAsync(queueUuid))!);
        var artifacts = payload.RootElement.GetProperty("lyricsArtifacts");
        Assert.Equal(31, artifacts.GetProperty("revision").GetInt64());
        Assert.Equal(destination, artifacts.GetProperty("filesByFormat").GetProperty("lrc").GetString());
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
            PayloadJson: "{}",
            Progress: 0,
            Downloaded: 0,
            Failed: 0,
            Error: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);
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

        public async Task<string?> GetPayloadAsync(string queueUuid)
        {
            await using var connection = new SqliteConnection($"Data Source={QueueDbPath}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload FROM download_task WHERE queue_uuid = $queueUuid LIMIT 1;";
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
