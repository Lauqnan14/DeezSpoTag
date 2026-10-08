using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Covers the repository surface that verification-driven retry selects on: the enqueue stamp,
/// the failed-item query, and the release clear.
/// </summary>
public sealed class VerificationRetryServiceTest : IDisposable
{
    private readonly string _tempRoot;

    public VerificationRetryServiceTest()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-verify-retry-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
    }

    [Fact]
    public async Task EnqueueAsync_DefaultsToNotStampedAndRecordsQueuedAt()
    {
        var repository = CreateRepository();
        await repository.EnqueueAsync(CreateItem("unstamped"), CancellationToken.None);

        var (flag, queuedAt) = await ReadStampAsync(repository, "unstamped");

        // Existing callers that never set the flag must behave exactly as before this column existed.
        Assert.False(flag);
        Assert.NotNull(queuedAt);
    }

    [Fact]
    public async Task EnqueueAsync_PersistsTheStampWhenTheCallerSetsIt()
    {
        var repository = CreateRepository();
        var queuedAt = new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero);
        await repository.EnqueueAsync(
            CreateItem("stamped") with
            {
                PublicApiUnverifiedWhenQueued = true,
                LastQueuedAtUtc = queuedAt
            },
            CancellationToken.None);

        var (flag, persistedAt) = await ReadStampAsync(repository, "stamped");

        Assert.True(flag);
        Assert.Equal(queuedAt, persistedAt);
    }

    [Fact]
    public async Task MarkPublicApiUnverifiedWhenQueuedAsync_StampsAnExistingRow()
    {
        var repository = CreateRepository();
        await repository.EnqueueAsync(CreateItem("late-stamp"), CancellationToken.None);

        var stampedAt = new DateTimeOffset(2026, 6, 7, 8, 9, 10, TimeSpan.Zero);
        await repository.MarkPublicApiUnverifiedWhenQueuedAsync("late-stamp", true, stampedAt, CancellationToken.None);

        var (flag, persistedAt) = await ReadStampAsync(repository, "late-stamp");
        Assert.True(flag);
        Assert.Equal(stampedAt, persistedAt);
    }

    [Fact]
    public async Task GetFailedForVerificationRetryAsync_ReturnsOnlyStampedFailedItems()
    {
        var repository = CreateRepository();
        await repository.EnqueueAsync(
            CreateItem("stamped-failed") with { Status = "failed", PublicApiUnverifiedWhenQueued = true },
            CancellationToken.None);
        await repository.EnqueueAsync(
            CreateItem("unstamped-failed") with { Status = "failed" },
            CancellationToken.None);
        await repository.EnqueueAsync(
            CreateItem("stamped-queued") with { Status = "queued", PublicApiUnverifiedWhenQueued = true },
            CancellationToken.None);

        var candidates = await repository.GetFailedForVerificationRetryAsync(
            requireUnverifiedFlag: true,
            CancellationToken.None);

        Assert.Equal(new[] { "stamped-failed" }, candidates.ToArray());
    }

    [Fact]
    public async Task GetFailedForVerificationRetryAsync_InitialSweepReturnsEveryFailedItem()
    {
        var repository = CreateRepository();
        await repository.EnqueueAsync(
            CreateItem("stamped-failed") with { Status = "failed", PublicApiUnverifiedWhenQueued = true },
            CancellationToken.None);
        await repository.EnqueueAsync(
            CreateItem("unstamped-failed") with { Status = "failed" },
            CancellationToken.None);

        var candidates = await repository.GetFailedForVerificationRetryAsync(
            requireUnverifiedFlag: false,
            CancellationToken.None);

        Assert.Equal(2, candidates.Count);
        Assert.Contains("stamped-failed", candidates);
        Assert.Contains("unstamped-failed", candidates);
    }

    [Theory]
    [InlineData("canceled")]
    [InlineData("cancelled")]
    public async Task GetFailedForVerificationRetryAsync_NeverReturnsUserCancelledItems(string status)
    {
        var repository = CreateRepository();
        await repository.EnqueueAsync(
            CreateItem("cancelled-item") with
            {
                Status = status,
                PublicApiUnverifiedWhenQueued = true
            },
            CancellationToken.None);

        // A user cancellation is final in both modes: the initial sweep included.
        Assert.Empty(await repository.GetFailedForVerificationRetryAsync(true, CancellationToken.None));
        Assert.Empty(await repository.GetFailedForVerificationRetryAsync(false, CancellationToken.None));
    }

    [Fact]
    public async Task ClearPublicApiRetryFlagAsync_StopsFurtherReleases()
    {
        var repository = CreateRepository();
        await repository.EnqueueAsync(
            CreateItem("released") with { Status = "failed", PublicApiUnverifiedWhenQueued = true },
            CancellationToken.None);

        await repository.ClearPublicApiRetryFlagAsync("released", CancellationToken.None);

        var (flag, _) = await ReadStampAsync(repository, "released");
        Assert.False(flag);
        Assert.Empty(await repository.GetFailedForVerificationRetryAsync(true, CancellationToken.None));
    }

    [Fact]
    public async Task OnSessionVerifiedAsync_ReleasesRegardlessOfEnabledEngines()
    {
        // Soulseek enabled alongside the public APIs must not disable this mechanism. There is no
        // configuration gate: verification always releases the items that were waiting on it.
        using var scope = new TestConfigRootScope(Path.Join(_tempRoot, "always"));
        var settingsService = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
        var settings = settingsService.LoadSettings();
        settings.Service = "qobuz";
        settings.Soulseek.AutomationEnabled = true;
        settingsService.SaveSettings(settings);

        var repository = CreateRepository();
        await repository.EnqueueAsync(
            CreateItem("mixed-engine-item") with { Status = "failed", PublicApiUnverifiedWhenQueued = true },
            CancellationToken.None);

        var releasedUuids = new List<string>();
        var service = new VerificationRetryService(
            repository,
            new PublicApiSessionVerificationStore(settingsService),
            settingsService,
            new NullActivityLogWriter(),
            NullLogger<VerificationRetryService>.Instance);

        var released = await service.OnSessionVerifiedAsync(
            "qobuz",
            (uuid, _) =>
            {
                releasedUuids.Add(uuid);
                return Task.FromResult(true);
            },
            CancellationToken.None);

        Assert.Equal(1, released);
        Assert.Equal(new[] { "mixed-engine-item" }, releasedUuids.ToArray());
    }

    [Fact]
    public async Task StampQueuedItemAsync_StampsWhenAnyPublicApiIsUnverified_RegardlessOfEngines()
    {
        using var scope = new TestConfigRootScope(Path.Join(_tempRoot, "stamp"));
        var settingsService = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
        var settings = settingsService.LoadSettings();
        settings.Service = "qobuz";
        settings.Soulseek.AutomationEnabled = true;
        settingsService.SaveSettings(settings);

        var repository = CreateRepository();
        await repository.EnqueueAsync(CreateItem("to-stamp"), CancellationToken.None);
        var service = new VerificationRetryService(
            repository,
            new PublicApiSessionVerificationStore(settingsService),
            settingsService,
            new NullActivityLogWriter(),
            NullLogger<VerificationRetryService>.Instance);

        // No public API has ever been verified, so the item is stamped.
        Assert.True(await service.StampQueuedItemAsync("to-stamp", CancellationToken.None));
        var (flag, _) = await ReadStampAsync(repository, "to-stamp");
        Assert.True(flag);
    }

    [Fact]
    public async Task OnSessionVerifiedAsync_InitialSweepReleasesEveryFailedItemThenOnlyStampedOnes()
    {
        using var scope = new TestConfigRootScope(Path.Join(_tempRoot, "sweep"));
        var settingsService = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
        var settings = settingsService.LoadSettings();
        settings.Service = "qobuz";
        settingsService.SaveSettings(settings);

        var repository = CreateRepository();
        await repository.EnqueueAsync(
            CreateItem("first-sweep-stamped") with { Status = "failed", PublicApiUnverifiedWhenQueued = true },
            CancellationToken.None);
        await repository.EnqueueAsync(
            CreateItem("first-sweep-unstamped") with { Status = "failed" },
            CancellationToken.None);

        var service = new VerificationRetryService(
            repository,
            new PublicApiSessionVerificationStore(settingsService),
            settingsService,
            new NullActivityLogWriter(),
            NullLogger<VerificationRetryService>.Instance);

        // Mirrors PublicApiVerificationRetry.ReleaseAsync: requeue, then clear the flag so the same
        // item is not released twice.
        async Task<bool> ReleaseAsync(string uuid, CancellationToken token)
        {
            await repository.UpdateStatusAsync(uuid, "queued", cancellationToken: token);
            await repository.ClearPublicApiRetryFlagAsync(uuid, token);
            return true;
        }

        var firstSweep = new List<string>();
        Assert.Equal(2, await service.OnSessionVerifiedAsync(
            "qobuz",
            (uuid, token) =>
            {
                firstSweep.Add(uuid);
                return ReleaseAsync(uuid, token);
            },
            CancellationToken.None));
        Assert.Equal(2, firstSweep.Count);

        // A second verification must not repeat the unfiltered sweep.
        await repository.EnqueueAsync(
            CreateItem("second-unstamped") with { Status = "failed" },
            CancellationToken.None);
        var secondSweep = new List<string>();
        Assert.Equal(0, await service.OnSessionVerifiedAsync(
            "tidal",
            (uuid, token) =>
            {
                secondSweep.Add(uuid);
                return ReleaseAsync(uuid, token);
            },
            CancellationToken.None));
        Assert.Empty(secondSweep);
    }

    [Fact]
    public async Task OnSessionVerifiedAsync_IgnoresNonPublicSlugs()
    {
        using var scope = new TestConfigRootScope(Path.Join(_tempRoot, "slug"));
        var settingsService = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
        var settings = settingsService.LoadSettings();
        settings.Service = "qobuz";
        settingsService.SaveSettings(settings);

        var store = new PublicApiSessionVerificationStore(settingsService);
        var service = new VerificationRetryService(
            CreateRepository(),
            store,
            settingsService,
            new NullActivityLogWriter(),
            NullLogger<VerificationRetryService>.Instance);

        var released = await service.OnSessionVerifiedAsync(
            "deezer",
            (_, _) => Task.FromResult(true),
            CancellationToken.None);

        Assert.Equal(0, released);
        Assert.Null(store.GetLastVerifiedAtUtc("deezer"));
    }

    private DownloadQueueRepository CreateRepository()
    {
        var queueDbPath = Path.Join(_tempRoot, Guid.NewGuid().ToString("N") + ".db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Queue"] = $"Data Source={queueDbPath}",
                ["DataDirectory"] = _tempRoot
            })
            .Build();
        return new DownloadQueueRepository(config, NullLogger<DownloadQueueRepository>.Instance);
    }

    private static async Task<(bool Flag, DateTimeOffset? QueuedAt)> ReadStampAsync(
        DownloadQueueRepository repository,
        string queueUuid)
    {
        var stamp = await repository.GetPublicApiRetryStampAsync(queueUuid, CancellationToken.None);
        Assert.NotNull(stamp);
        return stamp!.Value;
    }

    private static DownloadQueueItem CreateItem(string queueUuid)
        => new(
            Id: 0,
            QueueUuid: queueUuid,
            Engine: "qobuz",
            // Distinct per uuid: the repository treats identical artist/title/duration as a
            // duplicate and silently drops the second insert, which would hide these rows.
            ArtistName: "Artist-" + queueUuid,
            TrackTitle: "Track-" + queueUuid,
            Isrc: "ISRC" + queueUuid.ToUpperInvariant(),
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
            DestinationFolderId: null,
            QualityRank: null,
            QueueOrder: null,
            ContentType: null,
            Status: "queued",
            PayloadJson: "{}",
            Progress: 0,
            Downloaded: 0,
            Failed: 0,
            Error: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

    private sealed class NullActivityLogWriter : IActivityLogWriter
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
}
