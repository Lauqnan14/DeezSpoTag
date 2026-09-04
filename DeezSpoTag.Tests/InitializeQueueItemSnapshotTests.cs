using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Qobuz;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class InitializeQueueItemSnapshotTests
{
    [Fact]
    public async Task InitializeQueueItemAsync_AppliesEnqueueSnapshotOverLiveSettings()
    {
        await using var context = await CreateContextAsync();
        var settings = new DeezSpoTagSettings
        {
            SyncedLyrics = true,
            SaveLyrics = true,
            LrcTimingPreference = "line",
            LyricsFallbackEnabled = true
        };
        var payload = new QobuzQueueItem
        {
            Title = "Track",
            ContentType = "stereo",
            QualityBucket = "",
            SourceSettingsSnapshot = new QueueSourceSettingsSnapshot
            {
                SyncedLyrics = false,
                LrcTimingPreference = "word-enhanced",
                LyricsFallbackEnabled = false
            }
        };

        var result = await EngineAudioPostDownloadHelper.InitializeQueueItemAsync(
            CreateQueueItem("snapshot-apply-1"),
            JsonSerializer.Serialize(payload),
            json => JsonSerializer.Deserialize<QobuzQueueItem>(json),
            CreateContext(context, settings, profileTechnical: null),
            CancellationToken.None);

        Assert.NotNull(result);
        // Snapshot preferences captured at enqueue time win over the live
        // settings that happen to be configured at processing time.
        Assert.False(settings.SyncedLyrics);
        Assert.Equal(LrcTimingModes.WordEnhanced, settings.LrcTimingPreference);
        Assert.False(settings.LyricsFallbackEnabled);
    }

    [Fact]
    public async Task InitializeQueueItemAsync_ProfileStillWinsOverEnqueueSnapshot()
    {
        await using var context = await CreateContextAsync();
        var settings = new DeezSpoTagSettings
        {
            SyncedLyrics = true,
            SaveLyrics = true,
            LrcTimingPreference = "word-enhanced"
        };
        var payload = new QobuzQueueItem
        {
            Title = "Track",
            ContentType = "stereo",
            QualityBucket = "",
            SourceSettingsSnapshot = new QueueSourceSettingsSnapshot
            {
                SyncedLyrics = false
            }
        };
        var profileTechnical = new TechnicalTagSettings
        {
            SyncedLyrics = true,
            SaveLyrics = true
        };

        var result = await EngineAudioPostDownloadHelper.InitializeQueueItemAsync(
            CreateQueueItem("snapshot-apply-2"),
            JsonSerializer.Serialize(payload),
            json => JsonSerializer.Deserialize<QobuzQueueItem>(json),
            CreateContext(context, settings, profileTechnical),
            CancellationToken.None);

        Assert.NotNull(result);
        // The destination folder's profile keeps precedence over the snapshot.
        Assert.True(settings.SyncedLyrics);
    }

    [Fact]
    public async Task InitializeQueueItemAsync_EmptySnapshotLeavesLiveSettingsUntouched()
    {
        await using var context = await CreateContextAsync();
        var settings = new DeezSpoTagSettings
        {
            SyncedLyrics = true,
            SaveLyrics = true,
            LrcTimingPreference = "line"
        };
        var payload = new QobuzQueueItem
        {
            Title = "Track",
            ContentType = "stereo",
            QualityBucket = ""
        };

        var result = await EngineAudioPostDownloadHelper.InitializeQueueItemAsync(
            CreateQueueItem("snapshot-apply-3"),
            JsonSerializer.Serialize(payload),
            json => JsonSerializer.Deserialize<QobuzQueueItem>(json),
            CreateContext(context, settings, profileTechnical: null),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(settings.SyncedLyrics);
        Assert.Equal("line", settings.LrcTimingPreference);
    }

    private static EngineAudioPostDownloadHelper.InitializeQueueItemContext<QobuzQueueItem> CreateContext(
        QueueRepositoryTestContext context,
        DeezSpoTagSettings settings,
        TechnicalTagSettings? profileTechnical)
    {
        return new EngineAudioPostDownloadHelper.InitializeQueueItemContext<QobuzQueueItem>(
            context.QueueRepository,
            null!,
            new NoopActivityLogWriter(),
            new StaticDownloadTagSettingsResolver(new DownloadTagProfileSettings(
                TagSettings: new TagSettings(),
                DownloadTagSource: "deezer",
                FolderStructure: null,
                Technical: profileTechnical)),
            new NoopFolderConversionSettingsOverlay(),
            new NoopDeezSpoTagListener(),
            (_, _, _, _) => Task.FromResult(false),
            _ => new Dictionary<string, object>(),
            settings,
            "qobuz",
            NullLogger.Instance);
    }

    private static DownloadQueueItem CreateQueueItem(string queueUuid)
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

    private static async Task<QueueRepositoryTestContext> CreateContextAsync()
    {
        var tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-snapshot-tests-" + Path.GetRandomFileName());
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
        return new QueueRepositoryTestContext(tempRoot, queueDbPath, queueRepository);
    }

    private sealed class QueueRepositoryTestContext : IAsyncDisposable
    {
        public QueueRepositoryTestContext(string tempRoot, string queueDbPath, DownloadQueueRepository queueRepository)
        {
            TempRoot = tempRoot;
            QueueDbPath = queueDbPath;
            QueueRepository = queueRepository;
        }

        public string TempRoot { get; }
        public string QueueDbPath { get; }
        public DownloadQueueRepository QueueRepository { get; }

        public async ValueTask DisposeAsync()
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

            await Task.CompletedTask;
        }
    }

    private sealed class NoopActivityLogWriter : IActivityLogWriter
    {
        public void Info(string message)
        {
        }

        public void Warn(string message)
        {
        }

        public void Error(string message)
        {
        }
    }

    private sealed class NoopDeezSpoTagListener : IDeezSpoTagListener
    {
        public void Send(string eventName, object? data = null)
        {
        }
    }

    private sealed class NoopFolderConversionSettingsOverlay : IFolderConversionSettingsOverlay
    {
        public Task ApplyAsync(DeezSpoTagSettings settings, long? destinationFolderId, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class StaticDownloadTagSettingsResolver : IDownloadTagSettingsResolver
    {
        private readonly DownloadTagProfileSettings? _profile;

        public StaticDownloadTagSettingsResolver(DownloadTagProfileSettings? profile)
        {
            _profile = profile;
        }

        public Task<TagSettings?> ResolveAsync(long? destinationFolderId, CancellationToken cancellationToken)
        {
            return Task.FromResult<TagSettings?>(_profile?.TagSettings);
        }

        public Task<DownloadTagProfileSettings?> ResolveProfileAsync(long? destinationFolderId, CancellationToken cancellationToken)
        {
            return Task.FromResult(_profile);
        }
    }
}
