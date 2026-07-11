using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DeezSpoTag.Services.Download.Deezer;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Qobuz;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Controllers.Api;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Core.Models.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DownloadQueueEnqueueHelperTests
{
    [Fact]
    public async Task EnqueueWithDedupAsync_ReturnsQueueDuplicate_WhenMatchingItemIsQueued()
    {
        await using var context = await CreateContextAsync();
        var payload = CreatePayload("queued-1");

        await context.QueueRepository.EnqueueAsync(CreateQueueItem(payload, "queued"), CancellationToken.None);

        var outcome = await DownloadQueueEnqueueHelper.EnqueueWithDedupAsync(
            payload,
            redownloadCooldownMinutes: 720,
            context.QueueRepository,
            context.DedupeService,
            context.SettingsService,
            context.ServiceProvider,
            CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.True(outcome.AlreadyQueued);
        Assert.Equal("queue_duplicate", outcome.ReasonCode);
    }

    [Fact]
    public async Task EnqueueWithDedupAsync_DoesNotAutoRequeueCancelledDuplicate()
    {
        await using var context = await CreateContextAsync();
        var payload = CreatePayload("cancelled-dup-1");

        await context.QueueRepository.EnqueueAsync(CreateQueueItem(payload, "queued"), CancellationToken.None);
        await context.QueueRepository.UpdateStatusAsync(
            payload.Id,
            "canceled",
            cancellationToken: CancellationToken.None);

        var outcome = await DownloadQueueEnqueueHelper.EnqueueWithDedupAsync(
            payload,
            redownloadCooldownMinutes: 720,
            context.QueueRepository,
            context.DedupeService,
            context.SettingsService,
            context.ServiceProvider,
            CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.True(outcome.AlreadyQueued);
        Assert.Equal("queue_duplicate", outcome.ReasonCode);

        var persisted = await context.QueueRepository.GetByUuidAsync(payload.Id, CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal("canceled", persisted!.Status, ignoreCase: true);
    }

    [Fact]
    public async Task EnqueueWithDedupAsync_DoesNotAutoRehydrateFailedDuplicate()
    {
        await using var context = await CreateContextAsync();
        var existingPayload = CreatePayload("failed-stale-output-1");
        existingPayload.Isrc = "USAT20900265";
        existingPayload.FilePath = Path.Join(context.TempRoot, "downs", "Smash", "Mega Freestyle Box", "Smash - Crazy for Love.flac");
        existingPayload.Files = new List<Dictionary<string, object>>
        {
            new()
            {
                ["path"] = existingPayload.FilePath,
                ["albumPath"] = Path.GetDirectoryName(existingPayload.FilePath)!,
                ["artistPath"] = Path.GetDirectoryName(Path.GetDirectoryName(existingPayload.FilePath)!)!
            }
        };

        await context.QueueRepository.EnqueueAsync(CreateQueueItem(existingPayload, "failed"), CancellationToken.None);
        await context.QueueRepository.UpdateFinalDestinationsAsync(
            existingPayload.Id,
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                [existingPayload.FilePath] = Path.Join(context.TempRoot, "library", "Smash", "Mega Freestyle Box", "Smash - Crazy for Love.flac")
            }),
            JsonSerializer.Serialize(existingPayload),
            CancellationToken.None);

        var replacementPayload = CreatePayload("new-request-id");
        replacementPayload.Title = "Day Dreaming (feat. Akon, Snoop Dogg & T.I.)";
        replacementPayload.Artist = "DJ Drama";
        replacementPayload.Album = "Gangsta Grillz: The Album Vol. 2";
        replacementPayload.Isrc = existingPayload.Isrc;

        var outcome = await DownloadQueueEnqueueHelper.EnqueueWithDedupAsync(
            replacementPayload,
            redownloadCooldownMinutes: 720,
            context.QueueRepository,
            context.DedupeService,
            context.SettingsService,
            context.ServiceProvider,
            CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.True(outcome.AlreadyQueued);
        Assert.Equal("queue_duplicate", outcome.ReasonCode);
        Assert.Equal(existingPayload.Id, outcome.QueueUuid);

        var persisted = await context.QueueRepository.GetByUuidAsync(existingPayload.Id, CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal("failed", persisted!.Status, ignoreCase: true);
        Assert.Equal("Shared Artist", persisted.ArtistName);
        Assert.Equal("Shared Track", persisted.TrackTitle);
        Assert.NotNull(persisted.FinalDestinationsJson);

        Assert.False(string.IsNullOrWhiteSpace(persisted.PayloadJson));
        var payload = JsonSerializer.Deserialize<QobuzQueueItem>(persisted.PayloadJson!);
        Assert.NotNull(payload);
        Assert.Equal(existingPayload.Id, payload!.Id);
        Assert.Equal("Shared Artist", payload.Artist);
        Assert.Equal("Shared Track", payload.Title);
    }

    [Fact]
    public async Task EnqueueWithDedupAsync_ReturnsRecentlyDownloaded_WhenMatchingItemCompleted()
    {
        await using var context = await CreateContextAsync();
        var payload = CreatePayload("completed-1");
        payload.FilePath = Path.Join(context.TempRoot, "downloads", "Shared Artist", "Shared Track.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(payload.FilePath)!);
        await File.WriteAllTextAsync(payload.FilePath, "audio", CancellationToken.None);

        await context.QueueRepository.EnqueueAsync(CreateQueueItem(payload, "queued"), CancellationToken.None);
        await context.QueueRepository.UpdateStatusAsync(
            payload.Id,
            "completed",
            cancellationToken: CancellationToken.None);

        var outcome = await DownloadQueueEnqueueHelper.EnqueueWithDedupAsync(
            payload,
            redownloadCooldownMinutes: 720,
            context.QueueRepository,
            context.DedupeService,
            context.SettingsService,
            context.ServiceProvider,
            CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.True(outcome.AlreadyQueued);
        Assert.Equal("queue_duplicate", outcome.ReasonCode);
    }

    [Fact]
    public async Task EnqueueWithDedupAsync_BlocksCompletedDuplicateWhenPayloadFileWasDeleted()
    {
        await using var context = await CreateContextAsync();
        var payload = CreatePayload("completed-deleted-1");
        payload.FilePath = Path.Join(context.TempRoot, "downloads", "Shared Artist", "Shared Track.flac");

        await context.QueueRepository.EnqueueAsync(CreateQueueItem(payload, "queued"), CancellationToken.None);
        await context.QueueRepository.UpdateStatusAsync(
            payload.Id,
            "completed",
            cancellationToken: CancellationToken.None);

        var retryPayload = CreatePayload("completed-deleted-2");
        retryPayload.FilePath = payload.FilePath;
        var outcome = await DownloadQueueEnqueueHelper.EnqueueWithDedupAsync(
            retryPayload,
            redownloadCooldownMinutes: 720,
            context.QueueRepository,
            context.DedupeService,
            context.SettingsService,
            context.ServiceProvider,
            CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.True(outcome.AlreadyQueued);
        Assert.Equal("queue_duplicate", outcome.ReasonCode);
    }

    [Fact]
    public async Task EnqueueWithDedupAsync_AllowsSameTrackInDifferentDestinationFolder()
    {
        await using var context = await CreateContextAsync();
        var firstPayload = CreatePayload("dest-queued-1", destinationFolderId: 101);
        var secondPayload = CreatePayload("dest-queued-2", destinationFolderId: 202);

        await context.QueueRepository.EnqueueAsync(CreateQueueItem(firstPayload, "queued"), CancellationToken.None);

        var outcome = await DownloadQueueEnqueueHelper.EnqueueWithDedupAsync(
            secondPayload,
            redownloadCooldownMinutes: 720,
            context.QueueRepository,
            context.DedupeService,
            context.SettingsService,
            context.ServiceProvider,
            CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.False(outcome.AlreadyQueued);

        var queuedItems = await context.QueueRepository.GetTasksAsync(firstPayload.Engine, CancellationToken.None);
        Assert.Equal(2, queuedItems.Count);
    }

    [Fact]
    public async Task EnqueueWithDedupAsync_PersistsDestinationFolderIdAcrossRepositoryInstances()
    {
        await using var context = await CreateContextAsync();
        var payload = CreatePayload("dest-persist-1", destinationFolderId: 909);

        await context.QueueRepository.EnqueueAsync(CreateQueueItem(payload, "queued"), CancellationToken.None);

        var restartedRepository = BuildRepository(context.TempRoot, context.QueueDbPath);
        var persisted = await restartedRepository.GetByUuidAsync(payload.Id, CancellationToken.None);

        Assert.NotNull(persisted);
        Assert.Equal(payload.DestinationFolderId, persisted!.DestinationFolderId);
    }

    [Fact]
    public async Task EnqueueAsync_ReturnsNull_WhenInsertIsIgnoredByQueueUuidConstraint()
    {
        await using var context = await CreateContextAsync();
        var existingPayload = CreatePayload("insert-ignore-1");
        await context.QueueRepository.EnqueueAsync(CreateQueueItem(existingPayload, "queued"), CancellationToken.None);

        var conflictingPayload = CreatePayload("insert-ignore-1");
        conflictingPayload.Artist = "Different Artist";
        conflictingPayload.Title = "Different Track";

        var result = await context.QueueRepository.EnqueueAsync(CreateQueueItem(conflictingPayload, "queued"), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task EnqueueWithDedupAsync_ReturnsQueueDuplicate_WhenInsertIsIgnored()
    {
        await using var context = await CreateContextAsync();
        var existingPayload = CreatePayload("helper-ignore-1");
        await context.QueueRepository.EnqueueAsync(CreateQueueItem(existingPayload, "queued"), CancellationToken.None);

        var conflictingPayload = CreatePayload("helper-ignore-1");
        conflictingPayload.Artist = "Different Artist";
        conflictingPayload.Title = "Different Track";

        var outcome = await DownloadQueueEnqueueHelper.EnqueueWithDedupAsync(
            conflictingPayload,
            redownloadCooldownMinutes: 720,
            context.QueueRepository,
            context.DedupeService,
            context.SettingsService,
            context.ServiceProvider,
            CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.True(outcome.AlreadyQueued);
        Assert.Equal("queue_duplicate", outcome.ReasonCode);
    }

    [Fact]
    public async Task EnqueueWithDedupAsync_AllowsDifferentTracksWithSharedAlbumAndArtistIds()
    {
        await using var context = await CreateContextAsync();
        var firstPayload = CreateDeezerPayload("deezer-shared-1", "First Track", "dz-track-1");
        var secondPayload = CreateDeezerPayload("deezer-shared-2", "Second Track", "dz-track-2");

        await context.QueueRepository.EnqueueAsync(CreateQueueItem(firstPayload, "queued"), CancellationToken.None);

        var outcome = await DownloadQueueEnqueueHelper.EnqueueWithDedupAsync(
            secondPayload,
            redownloadCooldownMinutes: 720,
            context.QueueRepository,
            context.DedupeService,
            context.SettingsService,
            context.ServiceProvider,
            CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.False(outcome.AlreadyQueued);

        var queuedItems = await context.QueueRepository.GetTasksAsync(firstPayload.Engine, CancellationToken.None);
        Assert.Equal(2, queuedItems.Count);
    }

    private static async Task<TestContext> CreateContextAsync()
    {
        var tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-download-queue-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);

        var queueDb = Path.Join(tempRoot, "queue.db");
        var queueRepository = BuildRepository(tempRoot, queueDb);
        var libraryDb = Path.Join(tempRoot, "library.db");
        var libraryConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = $"Data Source={libraryDb}"
            })
            .Build();
        var libraryDbService = new LibraryDbService(libraryConfiguration, NullLogger<LibraryDbService>.Instance);
        await libraryDbService.EnsureSchemaAsync(CancellationToken.None);
        var libraryRepository = new LibraryRepository(libraryConfiguration, NullLogger<LibraryRepository>.Instance);
        var dedupeService = new DownloadDedupeService(
            queueRepository,
            libraryRepository,
            NullLogger<DownloadDedupeService>.Instance);
        var previousConfigDir = Environment.GetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR");
        Environment.SetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR", Path.Join(tempRoot, "config"));
        var settingsService = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
        var settings = settingsService.LoadSettings();
        settings.DownloadLocation = Path.Join(tempRoot, "library");
        settings.TracknameTemplate = "%artist% - %title%";
        settingsService.SaveSettings(settings);
        var serviceProvider = new ServiceCollection()
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(new EnhancedPathTemplateProcessor(NullLogger<EnhancedPathTemplateProcessor>.Instance))
            .AddSingleton<IDownloadTagSettingsResolver>(new TestDownloadTagSettingsResolver())
            .BuildServiceProvider();
        return new TestContext(tempRoot, queueDb, queueRepository, dedupeService, settingsService, serviceProvider, previousConfigDir);
    }

    private static DownloadQueueRepository BuildRepository(string tempRoot, string queueDbPath)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Queue"] = $"Data Source={queueDbPath}",
                ["DataDirectory"] = tempRoot
            })
            .Build();

        return new DownloadQueueRepository(config, NullLogger<DownloadQueueRepository>.Instance);
    }

    private static QobuzQueueItem CreatePayload(string queueUuid, long? destinationFolderId = null)
    {
        return new QobuzQueueItem
        {
            Id = queueUuid,
            Title = "Shared Track",
            Artist = "Shared Artist",
            Album = "Shared Album",
            AlbumArtist = "Shared Artist",
            Cover = "",
            Quality = "27",
            SourceUrl = "https://play.qobuz.com/track/123",
            QobuzId = "123",
            DestinationFolderId = destinationFolderId,
            ContentType = string.Empty,
            DurationSeconds = 0
        };
    }

    private static DownloadQueueItem CreateQueueItem(QobuzQueueItem payload, string status)
    {
        return new DownloadQueueItem(
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
            AppleTrackId: payload.AppleId,
            AppleAlbumId: null,
            AppleArtistId: null,
            DurationMs: null,
            DestinationFolderId: payload.DestinationFolderId,
            QualityRank: null,
            QueueOrder: null,
            ContentType: payload.ContentType,
            Status: status,
            PayloadJson: JsonSerializer.Serialize(payload),
            Progress: 0,
            Downloaded: 0,
            Failed: 0,
            Error: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);
    }

    private static DeezerQueueItem CreateDeezerPayload(string queueUuid, string title, string deezerTrackId)
    {
        return new DeezerQueueItem
        {
            Id = queueUuid,
            Title = title,
            Artist = "Shared Artist",
            Album = "Shared Album",
            AlbumArtist = "Shared Artist",
            DeezerId = deezerTrackId,
            DeezerAlbumId = "dz-album-1",
            DeezerArtistId = "dz-artist-1",
            SourceUrl = "https://www.deezer.com/track/123",
            DestinationFolderId = 303,
            ContentType = "stereo",
            DurationSeconds = 0
        };
    }

    private static DownloadQueueItem CreateQueueItem(DeezerQueueItem payload, string status)
    {
        return new DownloadQueueItem(
            Id: 0,
            QueueUuid: payload.Id,
            Engine: payload.Engine,
            ArtistName: payload.Artist,
            TrackTitle: payload.Title,
            Isrc: payload.Isrc,
            DeezerTrackId: payload.DeezerId,
            DeezerAlbumId: payload.DeezerAlbumId,
            DeezerArtistId: payload.DeezerArtistId,
            SpotifyTrackId: payload.SpotifyId,
            SpotifyAlbumId: null,
            SpotifyArtistId: null,
            AppleTrackId: payload.AppleId,
            AppleAlbumId: null,
            AppleArtistId: null,
            DurationMs: null,
            DestinationFolderId: payload.DestinationFolderId,
            QualityRank: null,
            QueueOrder: null,
            ContentType: payload.ContentType,
            Status: status,
            PayloadJson: JsonSerializer.Serialize(payload),
            Progress: 0,
            Downloaded: 0,
            Failed: 0,
            Error: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);
    }

    private sealed class TestContext : IAsyncDisposable
    {
        public TestContext(
            string tempRoot,
            string queueDbPath,
            DownloadQueueRepository queueRepository,
            DownloadDedupeService dedupeService,
            DeezSpoTagSettingsService settingsService,
            ServiceProvider serviceProvider,
            string? previousConfigDir)
        {
            TempRoot = tempRoot;
            QueueDbPath = queueDbPath;
            QueueRepository = queueRepository;
            DedupeService = dedupeService;
            SettingsService = settingsService;
            ServiceProvider = serviceProvider;
            PreviousConfigDir = previousConfigDir;
        }

        public string TempRoot { get; }
        public string QueueDbPath { get; }
        public DownloadQueueRepository QueueRepository { get; }
        public DownloadDedupeService DedupeService { get; }
        public DeezSpoTagSettingsService SettingsService { get; }
        public ServiceProvider ServiceProvider { get; }
        private string? PreviousConfigDir { get; }

        public ValueTask DisposeAsync()
        {
            try
            {
                ServiceProvider.Dispose();
                Environment.SetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR", PreviousConfigDir);
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

    private sealed class TestDownloadTagSettingsResolver : IDownloadTagSettingsResolver
    {
        private static readonly DownloadTagProfileSettings Profile = new(
            new TagSettings(),
            "follow-download-engine",
            new FolderStructureSettings(),
            null,
            new DownloadProfileRuntimeOverrides(
                TracknameTemplate: "%artist% - %title%",
                SaveArtwork: null,
                SaveAnimatedArtwork: null,
                AnimatedArtworkFormats: null,
                DlAlbumcoverForPlaylist: null,
                SaveArtworkArtist: null,
                CoverImageTemplate: null,
                ArtistImageTemplate: null,
                LocalArtworkFormat: null,
                EmbedMaxQualityCover: null,
                JpegImageQuality: null));

        public Task<TagSettings?> ResolveAsync(long? destinationFolderId, CancellationToken cancellationToken)
            => Task.FromResult<TagSettings?>(Profile.TagSettings);

        public Task<DownloadTagProfileSettings?> ResolveProfileAsync(long? destinationFolderId, CancellationToken cancellationToken)
            => Task.FromResult<DownloadTagProfileSettings?>(Profile);
    }
}
