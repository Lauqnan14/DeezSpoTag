using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

[Collection("Settings Config Isolation")]
public sealed class DownloadCancellationRegistryTests
{
    [Fact]
    public void Register_CancelsToken_WhenUserCancelWasRequestedBeforeEngineRegistered()
    {
        var registry = new DownloadCancellationRegistry();
        using var itemCts = new CancellationTokenSource();

        registry.MarkUserCanceled("queue-active-race");
        var activeBeforeRegister = registry.Cancel("queue-active-race");

        registry.Register("queue-active-race", itemCts);

        Assert.False(activeBeforeRegister);
        Assert.True(itemCts.IsCancellationRequested);
    }

    [Fact]
    public void Register_CancelsToken_WhenPauseWasRequestedBeforeEngineRegistered()
    {
        var registry = new DownloadCancellationRegistry();
        using var itemCts = new CancellationTokenSource();

        registry.MarkUserPaused("queue-pause-race");

        registry.Register("queue-pause-race", itemCts);

        Assert.True(itemCts.IsCancellationRequested);
    }

    [Fact]
    public void Register_CancelsToken_WhenTimeoutWasRequestedBeforeEngineRegistered()
    {
        var registry = new DownloadCancellationRegistry();
        using var itemCts = new CancellationTokenSource();

        registry.MarkTimedOut("queue-timeout-race");

        registry.Register("queue-timeout-race", itemCts);

        Assert.True(itemCts.IsCancellationRequested);
    }

    [Fact]
    public async Task CancelDownloadAsync_ActiveItemPersistsCanceledStateImmediately()
    {
        await using var context = await TestContext.CreateAsync();
        const string queueUuid = "active-cancel-persists";
        await context.QueueRepository.EnqueueAsync(CreateRunningQueueItem(queueUuid), CancellationToken.None);

        using var activeCts = new CancellationTokenSource();
        context.CancellationRegistry.Register(queueUuid, activeCts);

        await context.App.CancelDownloadAsync(queueUuid);

        var persisted = await context.QueueRepository.GetByUuidAsync(queueUuid, CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal("canceled", persisted!.Status);
        Assert.True(activeCts.IsCancellationRequested);
        Assert.Contains(context.Events, item =>
            item.EventName == "updateQueue"
            && item.Data?.ToString()?.Contains("canceled", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static DownloadQueueItem CreateRunningQueueItem(string queueUuid)
    {
        return new DownloadQueueItem(
            Id: 0,
            QueueUuid: queueUuid,
            Engine: "qobuz",
            ArtistName: "Artist",
            TrackTitle: "Track",
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
            DurationMs: 180000,
            DestinationFolderId: null,
            QualityRank: 27,
            QueueOrder: null,
            ContentType: "stereo",
            FinalizationStatus: null,
            EnrichmentStatus: "not_required",
            Status: "running",
            PayloadJson: "{}",
            Progress: 10,
            Downloaded: 0,
            Failed: 0,
            Error: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);
    }

    private sealed record CapturedEvent(string EventName, object Data);

    private sealed class AlwaysOpenExecutionGate : IDownloadQueueExecutionGate
    {
        public Task<DownloadQueueExecutionDecision> EvaluateDownloadExecutionAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DownloadQueueExecutionDecision(true, "open", string.Empty));

    }

    private sealed class TestContext : IAsyncDisposable
    {
        private TestContext(
            string tempRoot,
            TestConfigRootScope configScope,
            DownloadQueueRepository queueRepository,
            DownloadCancellationRegistry cancellationRegistry,
            DeezSpoTagApp app,
            List<CapturedEvent> events)
        {
            TempRoot = tempRoot;
            ConfigScope = configScope;
            QueueRepository = queueRepository;
            CancellationRegistry = cancellationRegistry;
            App = app;
            Events = events;
        }

        public string TempRoot { get; }
        private TestConfigRootScope ConfigScope { get; }
        public DownloadQueueRepository QueueRepository { get; }
        public DownloadCancellationRegistry CancellationRegistry { get; }
        public DeezSpoTagApp App { get; }
        public List<CapturedEvent> Events { get; }

        public static async Task<TestContext> CreateAsync()
        {
            var tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-cancel-tests-" + Path.GetRandomFileName());
            Directory.CreateDirectory(tempRoot);
            var configScope = new TestConfigRootScope(tempRoot);
            var queueDbPath = Path.Join(tempRoot, "queue.db");
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Queue"] = $"Data Source={queueDbPath}",
                    ["DataDirectory"] = tempRoot
                })
                .Build();

            var queueRepository = new DownloadQueueRepository(config, NullLogger<DownloadQueueRepository>.Instance);
            var cancellationRegistry = new DownloadCancellationRegistry();
            var settingsService = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
            var events = new List<CapturedEvent>();
            var listener = new DeezSpoTagListener((eventName, data) => events.Add(new CapturedEvent(eventName, data)));
            var retryScheduler = new DownloadRetryScheduler(
                queueRepository,
                settingsService,
                new NullActivityLogWriter(),
                listener,
                NullLogger<DownloadRetryScheduler>.Instance,
                cancellationRegistry);
            var serviceProvider = new ServiceCollection().BuildServiceProvider();
            var app = new DeezSpoTagApp(
                NullLogger<DeezSpoTagApp>.Instance,
                new DeezSpoTagApp.Dependencies(
                    settingsService,
                    listener,
                    retryScheduler,
                    queueRepository,
                    cancellationRegistry,
                    new AlwaysOpenExecutionGate()),
                serviceProvider);
            return new TestContext(tempRoot, configScope, queueRepository, cancellationRegistry, app, events);
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                ConfigScope.Dispose();
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
