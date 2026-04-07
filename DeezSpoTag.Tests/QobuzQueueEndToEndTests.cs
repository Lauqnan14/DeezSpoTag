using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download.Qobuz;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DeezSpoTag.Tests;

[Collection("Settings Config Isolation")]
public sealed class QobuzQueueEndToEndTests
{
    [Fact]
    public async Task EnqueueAndProcessQobuzItem_CompletesQueueItem()
    {
        var tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-qobuz-test-" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);
        using var configScope = new TestConfigRootScope(tempRoot);
        try
        {
            var queueDb = Path.Join(tempRoot, "queue.db");
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Queue"] = $"Data Source={queueDb}",
                    ["DataDirectory"] = tempRoot
                })
                .Build();

            using var loggerFactory = LoggerFactory.Create(builder => builder.AddDebug());
            var queueRepository = new DownloadQueueRepository(config, loggerFactory.CreateLogger<DownloadQueueRepository>());
            var settingsService = new DeezSpoTagSettingsService(config, loggerFactory.CreateLogger<DeezSpoTagSettingsService>());
            var settings = settingsService.LoadSettings();
            settings.DownloadLocation = Path.Join(tempRoot, "downloads");
            settingsService.SaveSettings(settings);

            var payload = new QobuzQueueItem
            {
                Id = "qobuz-test-1",
                Engine = "qobuz",
                QueueOrigin = "test",
                SourceService = "qobuz",
                Title = "Test Track",
                Artist = "Test Artist",
                Album = "Test Album",
                AlbumArtist = "Test Artist",
                Isrc = "TESTISRC1234",
                Quality = "6",
                Size = 1
            };

            var queueItem = new DownloadQueueItem(
                Id: 0,
                QueueUuid: payload.Id,
                Engine: payload.Engine,
                ArtistName: payload.Artist,
                TrackTitle: payload.Title,
                Isrc: payload.Isrc,
                DeezerTrackId: payload.DeezerId,
                DeezerAlbumId: null,
                DeezerArtistId: null,
                SpotifyTrackId: payload.SpotifyId,
                SpotifyAlbumId: null,
                SpotifyArtistId: null,
                AppleTrackId: null,
                AppleAlbumId: null,
                AppleArtistId: null,
                DurationMs: null,
                DestinationFolderId: null,
                QualityRank: null,
                QueueOrder: null,
                Status: "queued",
                PayloadJson: System.Text.Json.JsonSerializer.Serialize(payload),
                Progress: 0,
                Downloaded: 0,
                Failed: 0,
                Error: null,
                CreatedAt: System.DateTimeOffset.UtcNow,
                UpdatedAt: System.DateTimeOffset.UtcNow);

            await queueRepository.EnqueueAsync(queueItem, CancellationToken.None);

            var dequeued = await queueRepository.DequeueNextAsync("qobuz", newestFirst: false, CancellationToken.None);
            Assert.NotNull(dequeued);

            await queueRepository.UpdateStatusAsync(
                payload.Id,
                status: "completed",
                downloaded: 1,
                progress: 100,
                cancellationToken: CancellationToken.None);

            var completed = await queueRepository.GetByUuidAsync(payload.Id, CancellationToken.None);
            Assert.NotNull(completed);
            Assert.Equal("completed", completed!.Status);
            Assert.Equal(100, completed.Progress);
            Assert.Equal(1, completed.Downloaded);
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Best effort cleanup for temporary test artifacts.
            }
        }
    }
}
