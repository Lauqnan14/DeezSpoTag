using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Download.Qobuz;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Integrations.Deezer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class QobuzQueueEndToEndTests
{
    [Fact]
    public async Task EnqueueAndProcessQobuzItem_CompletesQueueItem()
    {
        var tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-qobuz-test-" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);

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

        var services = new ServiceCollection()
            .AddHttpClient()
            .BuildServiceProvider();
        var songLinkResolver = new SongLinkResolver(
            services.GetRequiredService<IHttpClientFactory>(),
            null,
            Microsoft.Extensions.Options.Options.Create(new DeezSpoTag.Integrations.Qobuz.QobuzApiConfig()),
            loggerFactory.CreateLogger<SongLinkResolver>());
        var retryScheduler = new DownloadRetryScheduler(
            queueRepository,
            settingsService,
            new NullActivityLogWriter(),
            new NullDeezSpoTagListener(),
            loggerFactory.CreateLogger<DownloadRetryScheduler>());
        var cancellationRegistry = new DownloadCancellationRegistry();
        var deezspotagApp = new DeezSpoTag.Services.Download.Shared.DeezSpoTagApp(
            loggerFactory.CreateLogger<DeezSpoTag.Services.Download.Shared.DeezSpoTagApp>(),
            settingsService,
            new NullDeezSpoTagListener(),
            retryScheduler,
            queueRepository,
            cancellationRegistry,
            services);
        var fallbackCoordinator = new EngineFallbackCoordinator(
            queueRepository,
            settingsService,
            songLinkResolver,
            deezspotagApp,
            new NullActivityLogWriter(),
            loggerFactory.CreateLogger<EngineFallbackCoordinator>());
        var processor = new QobuzEngineProcessor(
            queueRepository,
            new DownloadCancellationRegistry(),
            settingsService,
            new NullDeezSpoTagListener(),
            retryScheduler,
            new FakeQobuzDownloadService(),
            services,
            fallbackCoordinator,
            new NullActivityLogWriter(),
            loggerFactory.CreateLogger<QobuzEngineProcessor>());

        await processor.ProcessQueueItemAsync(dequeued!, CancellationToken.None);

        var completed = await queueRepository.GetByUuidAsync(payload.Id, CancellationToken.None);
        Assert.NotNull(completed);
        Assert.Equal("completed", completed!.Status);
        Assert.Equal(100, completed.Progress);
        Assert.Equal(1, completed.Downloaded);
    }

    private sealed class FakeQobuzDownloadService : IQobuzDownloadService
    {
        public Task<bool> IsrcAvailableAsync(string isrc, CancellationToken cancellationToken)
        {
            return Task.FromResult(true);
        }

        public Task<string> DownloadByUrlAsync(QobuzDownloadRequest request, CancellationToken cancellationToken)
        {
            return DownloadCoreAsync(request.OutputDir, request.ProgressCallback, cancellationToken);
        }

        public async Task<string> DownloadByIsrcAsync(QobuzDownloadRequest request, CancellationToken cancellationToken)
        {
            return await DownloadCoreAsync(request.OutputDir, request.ProgressCallback, cancellationToken);
        }

        private static async Task<string> DownloadCoreAsync(
            string outputDir,
            Func<double, double, Task>? progressCallback,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(outputDir);
            var outputPath = Path.Join(outputDir, "test.flac");
            await File.WriteAllBytesAsync(outputPath, new byte[] { 1, 2, 3, 4 }, cancellationToken);
            if (progressCallback != null)
            {
                await progressCallback(100, 0);
            }
            return outputPath;
        }
    }

    private sealed class NullDeezSpoTagListener : IDeezSpoTagListener
    {
        public void SendStartDownload(string uuid)
        {
        }

        public void SendFinishDownload(string uuid, string? title = null)
        {
        }

        public void SendAddedToQueue(object payload)
        {
        }

        public void Send(string eventName, object? payload)
        {
        }

        public void Send(string eventName)
        {
        }
    }
}
