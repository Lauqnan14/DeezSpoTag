using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Runtime;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

[Collection("Settings Config Isolation")]
public sealed class DownloadOrchestrationPendingMutationTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly TestConfigRootScope _configScope;
    private readonly DownloadQueueRepository _queueRepository;
    private readonly DownloadOrchestrationService _orchestration;

    public DownloadOrchestrationPendingMutationTests()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-pending-mutation-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
        _configScope = new TestConfigRootScope(_tempRoot);
        var queueDbPath = Path.Join(_tempRoot, "queue.db");
        var libraryDbPath = Path.Join(_tempRoot, "library.db");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Queue"] = $"Data Source={queueDbPath}",
                ["ConnectionStrings:Library"] = $"Data Source={libraryDbPath}",
                ["DataDirectory"] = _tempRoot
            })
            .Build();
        new LibraryDbService(configuration, NullLogger<LibraryDbService>.Instance)
            .EnsureSchemaAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        _queueRepository = new DownloadQueueRepository(configuration, NullLogger<DownloadQueueRepository>.Instance);
        var settingsService = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
        var settings = settingsService.LoadSettings();
        settings.DownloadLocation = _tempRoot;
        settingsService.SaveSettings(settings);
        _orchestration = CreateOrchestration(configuration, _queueRepository, settingsService, _tempRoot);
    }

    [Fact]
    public async Task HasPendingPostDownloadEnrichmentAsync_DoesNotMutateCompletedPendingRows()
    {
        var queueUuid = "pending-no-mutate";
        var audioPath = Path.Join(_tempRoot, "Artist", "Pending.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(audioPath)!);
        await File.WriteAllTextAsync(audioPath, "audio");
        var payload = JsonSerializer.Serialize(new
        {
            filePath = audioPath,
            AudioAcquired = true,
            DestinationFolderId = 5
        });
        await _queueRepository.EnqueueAsync(
            new DownloadQueueItem(
                Id: 0,
                QueueUuid: queueUuid,
                Engine: "qobuz",
                ArtistName: "Artist",
                TrackTitle: "Pending",
                Isrc: null,
                DeezerTrackId: null,
                DeezerAlbumId: null,
                DeezerArtistId: null,
                SpotifyTrackId: null,
                SpotifyAlbumId: null,
                SpotifyArtistId: null,
                AppleTrackId: null,
                AppleAlbumId: null,
                AppleArtistId: null,
                DurationMs: null,
                DestinationFolderId: 5,
                QualityRank: null,
                QueueOrder: null,
                ContentType: "stereo",
                FinalizationStatus: "pending",
                EnrichmentStatus: "pending",
                Status: "completed",
                PayloadJson: payload,
                Progress: 100,
                Downloaded: 1,
                Failed: 0,
                Error: null,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow),
            CancellationToken.None);

        var before = await _queueRepository.GetByUuidAsync(queueUuid, CancellationToken.None);
        Assert.NotNull(before);

        var hasPending = await InvokeHasPendingAsync();

        var after = await _queueRepository.GetByUuidAsync(queueUuid, CancellationToken.None);
        Assert.True(hasPending);
        Assert.NotNull(after);
        Assert.Equal(before!.Status, after!.Status);
        Assert.Equal(before.EnrichmentStatus, after.EnrichmentStatus);
        Assert.Equal(before.FinalizationStatus, after.FinalizationStatus);
        Assert.Equal(before.DestinationFolderId, after.DestinationFolderId);
        Assert.Equal(before.PayloadJson, after.PayloadJson);
        Assert.Equal(before.Error, after.Error);
    }

    public void Dispose()
    {
        _orchestration.Dispose();
        _configScope.Dispose();
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }

    private Task<bool> InvokeHasPendingAsync()
    {
        var method = typeof(DownloadOrchestrationService).GetMethod(
            "HasPendingPostDownloadEnrichmentAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("HasPendingPostDownloadEnrichmentAsync was not found.");
        return (Task<bool>)method.Invoke(_orchestration, [CancellationToken.None])!;
    }

    private static DownloadOrchestrationService CreateOrchestration(
        IConfiguration configuration,
        DownloadQueueRepository queueRepository,
        DeezSpoTagSettingsService settingsService,
        string contentRoot)
    {
        var cancellationRegistry = new DownloadCancellationRegistry();
        var retryScheduler = new DownloadRetryScheduler(
            queueRepository,
            settingsService,
            new NullActivityLogWriter(),
            new DeezSpoTagListener(),
            NullLogger<DownloadRetryScheduler>.Instance,
            cancellationRegistry);
        var recoveryRuntime = new DownloadQueueRecoveryRuntime(
            retryScheduler,
            new NullActivityLogWriter(),
            new DeezSpoTagListener());
        var recoveryService = new DownloadQueueRecoveryService(
            queueRepository,
            cancellationRegistry,
            recoveryRuntime,
            settingsService,
            NullLogger<DownloadQueueRecoveryService>.Instance);
        var libraryRepository = new LibraryRepository(configuration, NullLogger<LibraryRepository>.Instance);
        var environment = new StubWebHostEnvironment(contentRoot);
        var configStore = new LibraryConfigStore(
            libraryRepository,
            NullLogger<LibraryConfigStore>.Instance,
            environment);
        var services = new ServiceCollection()
            .AddSingleton<INotificationSink>(new NullNotificationSink())
            .AddSingleton(queueRepository)
            .AddSingleton(recoveryService)
            .AddSingleton(libraryRepository)
            .AddSingleton(Uninitialized<AutoTagService>())
            .AddSingleton(Uninitialized<AutoTagDownloadMoveService>())
            .AddSingleton(settingsService)
            .AddSingleton(new AutoTagConfigBuilder())
            .AddSingleton(Uninitialized<AutoTagProfileResolutionService>())
            .AddSingleton(Uninitialized<KnownLibraryFileIngestionService>())
            .AddSingleton(Uninitialized<MediaServerRefreshOutboxService>())
            .AddSingleton(retryScheduler)
            .AddSingleton(Uninitialized<TrackAnalysisBackgroundService>())
            .AddSingleton(new VibeAnalysisSettingsStore(
                environment,
                configuration,
                NullLogger<VibeAnalysisSettingsStore>.Instance))
            .AddSingleton(configStore)
            .AddSingleton(new BackgroundWorkCoordinator())
            .AddSingleton(new DownloadQueueWakeSignal())
            .AddSingleton<IConfiguration>(configuration)
            .BuildServiceProvider();

        return new DownloadOrchestrationService(
            services,
            environment,
            NullLogger<DownloadOrchestrationService>.Instance);
    }

    private static T Uninitialized<T>()
        where T : class
        => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    private sealed class StubWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "DeezSpoTag.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = contentRootPath;
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRootPath);
    }
}
