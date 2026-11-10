using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Behavioral fresh-check tests for watchlist finalization recovery: classification of outcomes,
/// bounded processing with deletion-free blocking, and the idempotent missing-outbox repair sweep.
/// Reuses the real LibraryRepository/DownloadQueueRepository/WatchlistFinalizationService stack on
/// a temporary SQLite database; no second finalization implementation is created here.
/// </summary>
[Collection("Settings Config Isolation")]
public sealed class WatchlistFinalizationRecoveryTest : IAsyncLifetime
{
    private string _tempRoot = string.Empty;
    private string _dbPath = string.Empty;
    private TestConfigRootScope _configScope = default!;
    private IConfiguration _configuration = default!;
    private LibraryRepository _repository = default!;
    private DownloadQueueRepository _queueRepository = default!;
    private DeezSpoTagSettingsService _settingsService = default!;
    private WatchlistFinalizationService _finalizationService = default!;
    private WatchlistPostDownloadSyncService _syncService = default!;
    private ServiceProvider _provider = default!;
    private long _destinationFolderId;
    private readonly WatchlistRunCoordinatorHardeningTest.ListLogger<WatchlistPostDownloadSyncService> _syncLogger = new();
    private readonly FinalizationFaultLogger _finalizationLogger = new();

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-finalization-recovery-" + Path.GetRandomFileName());
        _configScope = new TestConfigRootScope(_tempRoot);

        _dbPath = Path.Join(_tempRoot, "library.db");
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = $"Data Source={_dbPath}",
                ["ConnectionStrings:Queue"] = $"Data Source={Path.Join(_tempRoot, "queue.db")}",
                ["DataDirectory"] = _tempRoot
            })
            .Build();
        await new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();
        _repository = new LibraryRepository(_configuration, NullLogger<LibraryRepository>.Instance);
        _queueRepository = new DownloadQueueRepository(_configuration, NullLogger<DownloadQueueRepository>.Instance);
        _settingsService = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
        var settings = _settingsService.LoadSettings();
        settings.WatchEnabled = true;
        _settingsService.SaveSettings(settings);

        var engine = new WatchlistEngine(
            _repository,
            new WatchlistEngine.PlaylistWatchPlatformServices
            {
                SpotifyMetadataService = null!,
                SpotifyPathfinderMetadataClient = null!,
                SpotifyArtistService = null!,
                DeezerClient = new DeezSpoTag.Integrations.Deezer.DeezerClient(
                    NullLogger<DeezSpoTag.Integrations.Deezer.DeezerClient>.Instance,
                    sessionManager: null!),
                DeezerGatewayService = null!,
                AppleCatalogService = null!,
                BoomplayMetadataService = null!,
                BoomplayWatchlistMappingService = null!,
                LibraryRecommendationService = null!,
                HttpClientFactory = new StubHttpClientFactory(),
                TidalAccessTokenProvider = null!,
            },
            new WatchlistEngine.PlaylistWatchRuntimeServices
            {
                PlaylistVisualService = null!,
                WatchlistQueueAdmissionService = new WatchlistQueueAdmissionService(),
                ActivitiesRealtimeService = null!
            },
            _settingsService,
            serviceProvider: null!,
            localIdentityResolver: new PassthroughLocalTrackAmbiguityResolver(),
            logger: NullLogger<WatchlistEngine>.Instance);
        var reconciler = new PlaylistWatchReconciler(engine);
        var shazam = new ShazamRecognitionService(
            new StubWebHostEnvironment(_tempRoot),
            null!,
            NullLogger<ShazamRecognitionService>.Instance);
        var profileResolution = new AutoTagProfileResolutionService(
            new TaggingProfileService(new StubWebHostEnvironment(_tempRoot), NullLogger<TaggingProfileService>.Instance),
            new AutoTagDefaultsStore(new StubWebHostEnvironment(_tempRoot), NullLogger<AutoTagDefaultsStore>.Instance),
            _repository,
            NullLogger<AutoTagProfileResolutionService>.Instance);
        var localIdentityResolver = new WatchlistLocalIdentityResolver(
            _repository,
            shazam,
            profileResolution,
            new AutoTagLibraryOrganizer(
                NullLogger<AutoTagLibraryOrganizer>.Instance,
                NullLoggerFactory.Instance,
                _settingsService,
                shazam),
            NullLogger<WatchlistLocalIdentityResolver>.Instance);
        _finalizationService = new WatchlistFinalizationService(
            _queueRepository,
            _repository,
            reconciler,
            new WatchlistRunSignal(),
            localIdentityResolver,
            _settingsService,
            _finalizationLogger);

        var services = new ServiceCollection();
        services.AddSingleton(_settingsService);
        services.AddSingleton(_repository);
        services.AddSingleton(_queueRepository);
        services.AddSingleton(reconciler);
        services.AddSingleton(_finalizationService);
        _provider = services.BuildServiceProvider();
        _syncService = new WatchlistPostDownloadSyncService(
            _provider,
            _settingsService,
            new WatchlistRunSignal(),
            _syncLogger);

        var profileService = new TaggingProfileService(
            new StubWebHostEnvironment(_tempRoot),
            NullLogger<TaggingProfileService>.Instance);
        var profiles = await profileService.LoadAsync();
        profiles.Add(new DeezSpoTag.Core.Models.Settings.TaggingProfile
        {
            Id = "finalization-recovery-profile",
            Name = "Finalization Recovery Profile"
        });
        await profileService.SaveAsync(profiles);

        var destinationFolder = await _repository.AddFolderAsync(
            new LibraryRepository.FolderUpsertInput(
                RootPath: Path.Join(_tempRoot, "music"),
                DisplayName: "Recovery Library",
                Enabled: true,
                LibraryName: "Music",
                DesiredQuality: "flac",
                ConvertEnabled: false,
                ConvertFormat: null,
                ConvertBitrate: null,
                AutoTagProfileId: "finalization-recovery-profile"));
        _destinationFolderId = destinationFolder.Id;
    }

    public async Task DisposeAsync()
    {
        _provider?.Dispose();
        _configScope?.Dispose();
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort test cleanup.
        }

        await Task.CompletedTask;
    }

    // ---------------------------------------------------------------------------
    // Scoped admission gate (Task 1)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task HistoricalRetryBacklogWithoutOwnedPendingWorkDoesNotHoldAdmission()
    {
        await AddWatchlistAsync("deezer", "current-list");
        // 88 historical retry jobs like the NAS backlog: no active batch owns their claims, their
        // original queue rows are absent, and no pending claim remains for the watched playlist.
        for (var n = 0; n < 88; n++)
        {
            await SeedFinalizationRowAsync(
                queueUuid: $"q-stale-{n}",
                payloadJson: null,
                finalFilePaths: ["/music/final/missing-" + n + ".flac"],
                status: "retry",
                attemptCount: 100 + n,
                lastError: "Finalized files or Watchlist ownership are not verifiable yet.");
        }

        var backlog = await _repository.GetWatchlistFinalizationBacklogCountsAsync();
        Assert.Equal(88, backlog.PendingRetry);
        Assert.Equal(0, backlog.BlockingAdmission);
        Assert.False(await _repository.HasUnfinishedWatchlistFinalizationAsync());
        Assert.NotEmpty((await _repository.GetPlaylistWatchlistAsync()).Where(static playlist => playlist.SourceId == "current-list"));
    }

    [Fact]
    public async Task AdmittedDownloadWithCurrentWatchlistOwnershipStillHoldsAdmission()
    {
        await AddWatchlistAsync("deezer", "current-list");
        await SeedFinalizationRowAsync(
            queueUuid: "q-import",
            payloadJson: null,
            finalFilePaths: ["/music/final/verified.flac"],
            status: "pending",
            attemptCount: 0,
            lastError: null);
        // The claim is still pending: the audio is downloaded (network finished) but the verified
        // import and receipt are not, so this batch stays meaningfully busy.
        await _repository.UpsertPlaylistWatchDownloadClaimsAsync(
            "deezer", "current-list", "dz-track-1", ["q-import"], _destinationFolderId);

        Assert.True(await _repository.HasUnfinishedWatchlistFinalizationAsync());
        Assert.Equal(1, (await _repository.GetWatchlistFinalizationBacklogCountsAsync()).BlockingAdmission);

        // As soon as the verified receipt completes the claim, admission releases.
        await _repository.UpdatePlaylistWatchDownloadClaimStatusAsync(
            "q-import", "deezer", "current-list", "dz-track-1", "completed");
        Assert.False(await _repository.HasUnfinishedWatchlistFinalizationAsync());
    }

    [Fact]
    public async Task SharedDownloadKeepsBlockingWhileAnotherWatchedOwnerOwesWork()
    {
        await AddWatchlistAsync("deezer", "owner-kept");
        await AddWatchlistAsync("deezer", "owner-removed");
        await SeedFinalizationRowAsync(
            queueUuid: "q-shared",
            payloadJson: null,
            finalFilePaths: ["/music/final/shared.flac"],
            status: "pending",
            attemptCount: 0,
            lastError: null);
        await _repository.UpsertPlaylistWatchDownloadClaimsAsync(
            "deezer", "owner-kept", "dz-shared", ["q-shared"], _destinationFolderId);
        await _repository.UpsertPlaylistWatchDownloadClaimsAsync(
            "deezer", "owner-removed", "dz-shared", ["q-shared"], _destinationFolderId);
        // Removing one playlist's watch row must not discard the other watched owner's unfinished work.
        await ExecuteSqlAsync("DELETE FROM playlist_watchlist WHERE source='deezer' AND source_id='owner-removed';");

        Assert.True(await _repository.HasUnfinishedWatchlistFinalizationAsync());

        await _repository.UpdatePlaylistWatchDownloadClaimStatusAsync(
            "q-shared", "deezer", "owner-kept", "dz-shared", "completed");
        Assert.False(await _repository.HasUnfinishedWatchlistFinalizationAsync());
    }

    [Fact]
    public async Task OwnerlessHistoricalClaimsForWatchedPlaylistsDoNotBlock()
    {
        await AddWatchlistAsync("deezer", "current-list");
        // Historical job whose claims are resolved/failed through the normal recovery paths.
        await SeedFinalizationRowAsync(
            queueUuid: "q-history",
            payloadJson: null,
            finalFilePaths: ["/music/final/gone.flac"],
            status: "retry",
            attemptCount: 18765,
            lastError: "Finalized files or Watchlist ownership are not verifiable yet.");
        await _repository.UpsertPlaylistWatchDownloadClaimsAsync(
            "deezer", "current-list", "dz-old", ["q-history"], _destinationFolderId);
        await _repository.UpdatePlaylistWatchDownloadClaimStatusAsync(
            "q-history", "deezer", "current-list", "dz-old", "failed");

        Assert.False(await _repository.HasUnfinishedWatchlistFinalizationAsync());
        Assert.Equal(0, (await _repository.GetWatchlistFinalizationBacklogCountsAsync()).BlockingAdmission);
    }

    [Fact]
    public async Task BacklogCountsSeparateDispositionsAndKeepBlockingWorkVisible()
    {
        await AddWatchlistAsync("deezer", "counts-list");
        await SeedFinalizationRowAsync("q-pending", null, ["/no/such-a.flac"], "pending", 0, null);
        await SeedFinalizationRowAsync("q-retry", null, ["/no/such-b.flac"], "retry", 2, null);
        await SeedFinalizationRowAsync("q-processing", null, ["/no/such-c.flac"], "processing", 1, null);
        await SeedFinalizationRowAsync("q-completed", null, ["/real/done.flac"], "completed", 0, null);
        await SeedFinalizationRowAsync("q-blocked", null, ["/real/blocked.flac"], "blocked", 10, "needs attention");
        await SeedFinalizationRowAsync("q-obsolete", null, ["/real/obsolete.flac"], "obsolete", 3, "unmonitored");
        await _repository.UpsertPlaylistWatchDownloadClaimsAsync(
            "deezer", "counts-list", "dz-counted", ["q-processing"], _destinationFolderId);

        var backlog = await _repository.GetWatchlistFinalizationBacklogCountsAsync();
        Assert.Equal(2, backlog.PendingRetry);
        Assert.Equal(1, backlog.Processing);
        Assert.Equal(1, backlog.Completed);
        Assert.Equal(1, backlog.Blocked);
        Assert.Equal(1, backlog.Obsolete);
        Assert.Equal(1, backlog.BlockingAdmission);
        Assert.Equal(3, backlog.Unresolved);
    }

    // ---------------------------------------------------------------------------
    // Bounded processing, exclusion, retries, and ceiling (Task 3)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ClaimExclusionPreventsASecondAttemptOfTheSameRowWithinOnePass()
    {
        for (var n = 0; n < 5; n++)
        {
            await SeedFinalizationRowAsync(
                $"q-excl-{n}", null, [$"/nox/{n}.flac"], "pending", 0, null);
        }

        var lease = TimeSpan.FromMinutes(5);
        var firstBatch = (await _repository.ClaimDueWatchlistFinalizationOutboxAsync(3, lease, "pass-owner"))
            .OrderBy(work => work.Id)
            .ToList();
        Assert.Equal(3, firstBatch.Count);
        var excludedIds = firstBatch.Select(work => work.Id).ToList();
        // A slow pass can outlive the retry deadline. Exclusion must still apply after
        // the attempted rows return to due retry, not merely while their leases are live.
        foreach (var work in firstBatch)
            Assert.True(await _repository.RetryWatchlistFinalizationOutboxAsync(
                work.Id, "pass-owner", 1, DateTimeOffset.UtcNow.AddMinutes(-1), "temporary"));
        var remaining = await _repository.ClaimDueWatchlistFinalizationOutboxAsync(
            10, lease, "pass-owner", excludedIds);
        Assert.Equal(2, remaining.Count);
        Assert.All(remaining, work => Assert.DoesNotContain(work.Id, excludedIds));
        // Trying to claim again with everything already attempted returns nothing: no job is
        // ever attempted twice inside one processing pass.
        Assert.Empty(await _repository.ClaimDueWatchlistFinalizationOutboxAsync(
            10, lease, "pass-owner", remaining.Concat(firstBatch).Select(work => work.Id).ToList()));
    }

    [Fact]
    public async Task ExhaustedWorkDoesNotProbeAudioAgain()
    {
        await SeedFinalizationRowAsync("q-exhausted", null, ["/missing/exhausted.flac"], "retry", 18765, "original failure");
        _finalizationLogger.OnLog = _ => throw new InvalidOperationException("Audio verifier must not run for exhausted work");
        await RunFinalizationPassAsync();
        var row = (await ReadFinalizationRowAsync("q-exhausted"))!;
        Assert.Equal("blocked", row.Status);
        Assert.Equal(18765, row.AttemptCount);
        Assert.Contains("original failure", row.LastError);
        Assert.DoesNotContain("Audio verifier", row.LastError);
    }

    [Fact]
    public async Task RegexTimeoutRetainsFailureAndOtherJobsStillProgress()
    {
        await SeedFinalizationRowAsync("q-regex", null, ["/missing/regex.flac"], "pending", 0, null);
        await SeedFinalizationRowAsync("q-next", null, ["/missing/next.flac"], "pending", 0, null);
        _finalizationLogger.OnLog = message =>
        {
            if (message.Contains("q-regex", StringComparison.Ordinal))
                throw new System.Text.RegularExpressions.RegexMatchTimeoutException("synthetic", "pattern", TimeSpan.FromMilliseconds(1));
        };
        await RunFinalizationPassAsync();
        var failed = (await ReadFinalizationRowAsync("q-regex"))!;
        Assert.Equal("retry", failed.Status);
        Assert.Equal(1, failed.AttemptCount);
        Assert.Null(failed.LeaseOwner);
        Assert.Contains("Regex", failed.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, (await ReadFinalizationRowAsync("q-next"))!.AttemptCount);
        Assert.False(await _repository.HasUnfinishedWatchlistFinalizationAsync());
    }

    [Fact]
    public async Task CancellationDuringVerificationReleasesLeaseAndAllowsRecovery()
    {
        await SeedFinalizationRowAsync("q-cancel-active", null, ["/missing/cancel.flac"], "pending", 0, null);
        await SeedFinalizationRowAsync("q-after-cancel", null, ["/missing/next.flac"], "pending", 0, null);
        using var cancellation = new CancellationTokenSource();
        _finalizationLogger.OnLog = _ =>
        {
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _syncService.ProcessFinalizationWorkAsync(cancellation.Token));
        var interrupted = (await ReadFinalizationRowAsync("q-cancel-active"))!;
        Assert.Equal("retry", interrupted.Status);
        Assert.Equal(0, interrupted.AttemptCount);
        Assert.Null(interrupted.LeaseOwner);
        Assert.Equal(0, (await ReadFinalizationRowAsync("q-after-cancel"))!.AttemptCount);
        _finalizationLogger.OnLog = null;
        await RunFinalizationPassAsync();
        Assert.Equal(1, (await ReadFinalizationRowAsync("q-cancel-active"))!.AttemptCount);
        Assert.Equal(1, (await ReadFinalizationRowAsync("q-after-cancel"))!.AttemptCount);
    }

    [Fact]
    public async Task FinalizationPassProcessesAtMostTwentyFiveJobsAndEachExactlyOnce()
    {
        for (var n = 0; n < 30; n++)
        {
            await SeedFinalizationRowAsync(
                $"q-cap-{n}", null, [$"/music/final/absent-{n}.flac"], "pending", 0, null);
        }

        await RunFinalizationPassAsync();

        // Exactly 25 rows were claimed and attempted once; five were untouched and stay durably
        // queued with zero attempts.
        Assert.Equal(25, await CountSqlAsync("SELECT COUNT(*) FROM watchlist_finalization_outbox WHERE attempt_count>0;"));
        Assert.Equal(30, (await _repository.GetWatchlistFinalizationBacklogCountsAsync()).PendingRetry);

        // A second pass (without new due time) only surfaces the five untouched rows: claimed
        // rows returned to 'retry' with a future deadline and are never attempted twice in one
        // pass window, and untouched rows are picked up first.
        await RunFinalizationPassAsync();
        Assert.Equal(30, await CountSqlAsync("SELECT COUNT(*) FROM watchlist_finalization_outbox WHERE attempt_count=1;"));

        // A third pass with everything forced due is again capped at 25: five rows keep their
        // single attempt.
        await ForceRowsDueAsync();
        await RunFinalizationPassAsync();
        Assert.Equal(25, await CountSqlAsync("SELECT COUNT(*) FROM watchlist_finalization_outbox WHERE attempt_count=2;"));
        Assert.Equal(5, await CountSqlAsync("SELECT COUNT(*) FROM watchlist_finalization_outbox WHERE attempt_count=1;"));
    }

    [Fact]
    public async Task BoundedRetriesBackOffAndThenBlockWithTheRowAndReasonPreserved()
    {
        await SeedFinalizationRowAsync(
            "q-ceiling", null, ["/music/final/never.flac"], "pending", 0, null);
        for (var pass = 0; pass < 14; pass++)
        {
            await ForceRowsDueAsync();
            await RunFinalizationPassAsync();
        }

        var row = await ReadFinalizationRowAsync("q-ceiling");
        Assert.NotNull(row);
        Assert.Equal("blocked", row!.Status);
        Assert.InRange(row.AttemptCount, 10, 14);
        Assert.StartsWith("Gave up after 10 attempts:", row.LastError, StringComparison.Ordinal);
        Assert.Null(row.LeaseOwner);
        // The blocked row stays on disk for attention: it is never deleted, never completed.
        Assert.Equal(1, await CountOutboxRowsAsync());
    }

    [Fact]
    public async Task ExistingOverCeilingRowsAreClassifiedInsteadOfRetriedIndefinitely()
    {
        await SeedFinalizationRowAsync(
            "q-maxed", null, ["/music/final/maxed.flac"], "retry", 18765, "Finalized files or Watchlist ownership are not verifiable yet.");
        await ForceRowsDueAsync();
        await RunFinalizationPassAsync();

        var row = await ReadFinalizationRowAsync("q-maxed");
        Assert.NotNull(row);
        Assert.Equal("blocked", row!.Status);
        Assert.InRange(row.AttemptCount, 18765, 18766);
        Assert.DoesNotContain("Completed", row.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationStopsThePassWithoutStrandingRemainingRows()
    {
        for (var n = 0; n < 4; n++)
        {
            await SeedFinalizationRowAsync($"q-cancel-{n}", null, [$"/music/final/x-{n}.flac"], "pending", 0, null);
        }

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _syncService.ProcessFinalizationWorkAsync(cancellation.Token));

        var counts = await _repository.GetWatchlistFinalizationBacklogCountsAsync();
        Assert.Equal(4, counts.PendingRetry);
        Assert.Equal(0, counts.Processing);
        Assert.Equal(0, counts.Blocked);

        // A coordinator signaled to stop mid-pass also cannot strand claimed rows invisibly: the
        // whole pass returns without claiming anything, so nothing is left leased or mutated.
        await _syncService.ProcessFinalizationWorkAsync(CancellationToken.None, shouldStop: static () => true);
        var afterStop = await _repository.GetWatchlistFinalizationBacklogCountsAsync();
        Assert.Equal(4, afterStop.PendingRetry + afterStop.Processing);
    }

    // ---------------------------------------------------------------------------
    // Classification (Task 2)
    // ---------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifiedAudioCompletesReceiptsReleasesGateAndNeverDeletesWork(bool temporarilyUnavailable)
    {
        var seeded = await SeedLibraryAsync(("Verify Song", "dz-verified-1", "sp-verified-1", "am-verified-1"));
        await AddWatchlistAsync("deezer", "verify-list");
        await ConfigureDestinationAsync("deezer", "verify-list");
        // seed candidates + claim
        var candidates = new[]
        {
            new PlaylistTrackCandidate("dz-verified-1", seeded.IsrcByTitle["Verify Song"], "Verify Song", "Artist One", "Album One", null, 180000, null, Array.Empty<string>())
        };
        await UpsertCandidateCacheAsync("deezer", "verify-list", candidates);
        await _repository.UpsertPlaylistWatchDownloadClaimsAsync(
            "deezer", "verify-list", "dz-verified-1", ["q-verify"], _destinationFolderId);
        var payload = FinalizationPayload(
            title: "Verify Song", artist: "Artist One", isrc: seeded.IsrcByTitle["Verify Song"],
            durationMs: 180000, deezerTrackId: "dz-verified-1",
            watchlistSource: "deezer", watchlistPlaylistId: "verify-list", watchlistTrackId: "dz-verified-1");
        await _repository.UpsertWatchlistFinalizationOutboxAsync("q-verify", payload, [seeded.TrackPathsByTitle["Verify Song"]]);

        Assert.True(await _repository.HasUnfinishedWatchlistFinalizationAsync());
        var path = seeded.TrackPathsByTitle["Verify Song"];
        var originalAudio = await File.ReadAllBytesAsync(path);
        if (temporarilyUnavailable)
        {
            File.Delete(path); // simulate a temporarily unavailable mounted audio file
            await RunFinalizationPassAsync();
            Assert.Equal("retry", (await ReadFinalizationRowAsync("q-verify"))!.Status);
            Assert.True(await _repository.HasUnfinishedWatchlistFinalizationAsync());
            await File.WriteAllBytesAsync(path, originalAudio);
            await ForceRowsDueAsync();
        }
        await RunFinalizationPassAsync();

        var row = await ReadFinalizationRowAsync("q-verify");
        Assert.NotNull(row);
        Assert.Equal("completed", row!.Status);
        var claims = await _repository.GetPlaylistWatchDownloadClaimsAsync("q-verify");
        Assert.All(claims, claim => Assert.Equal("completed", claim.Status));
        Assert.False(await _repository.HasUnfinishedWatchlistFinalizationAsync());
        Assert.Equal(0, (await _repository.GetWatchlistFinalizationBacklogCountsAsync()).BlockingAdmission);
        Assert.Equal(1, (await _repository.GetWatchlistFinalizationBacklogCountsAsync()).Completed);
        Assert.Equal(originalAudio, await File.ReadAllBytesAsync(path));
        Assert.Empty(await _queueRepository.GetTasksAsync()); // payload recovery did not redownload
    }

    [Fact]
    public async Task UnmonitoredOwnedWorkIsObsoleteNotCompletedAndNotDeleted()
    {
        var seeded = await SeedLibraryAsync(("Obsolete Song", "dz-obsolete-1", "sp-obsolete-1", "am-obsolete-1"));
        // The owning playlist is not watched at all: only its historical claim references it.
        await _repository.UpsertPlaylistWatchDownloadClaimsAsync(
            "deezer", "removed-playlist", "dz-obsolete-1", ["q-obsolete"], _destinationFolderId);
        var payload = FinalizationPayload(
            title: "Obsolete Song", artist: "Artist One", isrc: seeded.IsrcByTitle["Obsolete Song"],
            durationMs: 180000, deezerTrackId: "dz-obsolete-1",
            watchlistSource: "deezer", watchlistPlaylistId: "removed-playlist", watchlistTrackId: "dz-obsolete-1");
        await _repository.UpsertWatchlistFinalizationOutboxAsync(
            "q-obsolete", payload, [seeded.TrackPathsByTitle["Obsolete Song"]]);

        var outcome = await _finalizationService.NotifyQueueItemFinalizedAsync(
            CompletedQueueItem("q-obsolete", payload, deezerTrackId: "dz-obsolete-1", isrc: seeded.IsrcByTitle["Obsolete Song"]),
            payload,
            [seeded.TrackPathsByTitle["Obsolete Song"]],
            CancellationToken.None);

        Assert.Equal(WatchlistFinalizationDisposition.ObsoleteOwnership, outcome.Disposition);
        Assert.Equal(0, outcome.NotificationCount);
        var row = await ReadFinalizationRowAsync("q-obsolete");
        Assert.Equal("pending", row!.Status); // disposition is applied by the sync service, not the probe

        await RunFinalizationPassAsync();
        row = await ReadFinalizationRowAsync("q-obsolete");
        Assert.Equal("obsolete", row!.Status);
        Assert.NotNull(row.LastError);
        Assert.NotEmpty(row.LastError);
        var claims = await _repository.GetPlaylistWatchDownloadClaimsAsync("q-obsolete");
        Assert.All(claims, claim => Assert.NotEqual("completed", claim.Status));
        var backlog = await _repository.GetWatchlistFinalizationBacklogCountsAsync();
        Assert.Equal(1, backlog.Obsolete);
        Assert.Equal(0, backlog.Completed);
        Assert.Equal(0, backlog.BlockingAdmission);

        // A repeated repair sweep must leave the obsolete disposition untouched.
        var before = JsonSerializer.Serialize(row);
        await _repository.UpsertWatchlistFinalizationOutboxAsync(
            "q-obsolete", payload, [seeded.TrackPathsByTitle["Obsolete Song"]]);
        var after = await ReadFinalizationRowAsync("q-obsolete");
        Assert.Equal(before, JsonSerializer.Serialize(after));
    }

    [Fact]
    public async Task MissingAudioIsAWarmTemporaryFailureNotObsolescence()
    {
        await AddWatchlistAsync("deezer", "mount-list");
        await _repository.UpsertPlaylistWatchDownloadClaimsAsync(
            "deezer", "mount-list", "dz-mount-1", ["q-mount"], _destinationFolderId);
        var payload = FinalizationPayload(
            title: "Mount Song", artist: "Artist One", isrc: null,
            durationMs: 180000, deezerTrackId: "dz-mount-1",
            watchlistSource: "deezer", watchlistPlaylistId: "mount-list", watchlistTrackId: "dz-mount-1");
        await _repository.UpsertWatchlistFinalizationOutboxAsync("q-mount", payload, ["/unavailable/mount/mount-song.flac"]);

        await RunFinalizationPassAsync();
        var row = await ReadFinalizationRowAsync("q-mount");
        Assert.Equal("retry", row!.Status);
        Assert.Equal(1, row.AttemptCount);
        Assert.Contains("missing or not indexed", row.LastError, StringComparison.Ordinal);
        // Still owned admitted work: blocked admission, but bounded rather than infinite.
        Assert.True(await _repository.HasUnfinishedWatchlistFinalizationAsync());
    }

    // ---------------------------------------------------------------------------
    // Repair sweep idempotency (Task 3)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task RepeatedRepairSweepsLeaveTerminalRowsUntouchedEvenAcrossRestarts()
    {
        await SeedFinalizationRowAsync("q-stop", null, ["/music/one.flac"], "blocked", 18, "older failure");
        await SeedFinalizationRowAsync("q-old", null, ["/music/two.flac"], "obsolete", 7, "unmonitored");
        await SeedFinalizationRowAsync("q-done", null, ["/music/three.flac"], "completed", 2, null);

        // Simulate a far-future snapshot stamp: any write churn on the untouched rows will be caught.
        await ExecuteSqlAsync("UPDATE watchlist_finalization_outbox SET updated_at='2000-01-01 00:00:00';");

        foreach (var repo in new[] { _repository, NewRepository(), _repository })
        {
            await repo.UpsertWatchlistFinalizationOutboxAsync("q-stop", null, ["/music/one.flac"]);
            await repo.UpsertWatchlistFinalizationOutboxAsync("q-old", null, ["/music/two.flac"]);
            await repo.UpsertWatchlistFinalizationOutboxAsync("q-done", null, ["/music/three.flac"]);
        }

        var untouched = await CountSqlAsync("SELECT COUNT(*) FROM watchlist_finalization_outbox WHERE updated_at='2000-01-01 00:00:00';");
        Assert.Equal(3, untouched);
        var stopRow = await ReadFinalizationRowAsync("q-stop");
        Assert.Equal("blocked", stopRow!.Status);
        Assert.Equal(18, stopRow.AttemptCount);
        Assert.Equal("older failure", stopRow.LastError);
    }

    [Fact]
    public async Task ChangedEvidenceReopensABlockedRowButReorderedPayloadDoesNot()
    {
        await SeedFinalizationRowAsync("q-evidence", "{\"Artist\":\"X\"}", ["/music/a.flac"], "blocked", 20000, "older failure");

        // JSON member ordering and an incidental timestamp must not reopen the row.
        await _repository.UpsertWatchlistFinalizationOutboxAsync(
            "q-evidence", "{\"RequestedAtUtc\":\"2026-11-10T00:00:00Z\",\"Artist\":\"X\"}", ["/music/A.flac"]);
        var row = await ReadFinalizationRowAsync("q-evidence");
        Assert.Equal("blocked", row!.Status);
        Assert.Equal(20000, row.AttemptCount);

        // Materially changed final audio evidence is the sanctioned reopen path.
        await _repository.UpsertWatchlistFinalizationOutboxAsync(
            "q-evidence", "{\"Artist\":\"X\"}", ["/music/a.flac", "/music/a-extra.flac"]);
        row = await ReadFinalizationRowAsync("q-evidence");
        Assert.Equal("pending", row!.Status);
        Assert.Equal(0, row.AttemptCount);
        Assert.Null(row.LastError);
        Assert.Equal(2, row.FinalFilePaths.Count);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("processing")]
    public async Task ConcurrentEvidenceCannotUndoCompletionOrStealAClaim(string newStatus)
    {
        await SeedFinalizationRowAsync("q-race", null, ["/music/old.flac"], "pending", 4, null);
        await using var writer = new SqliteConnection($"Data Source={_dbPath}");
        await writer.OpenAsync();
        await using var transaction = writer.BeginTransaction(deferred: false);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var entered = new ManualResetEventSlim();
        // SQLite's async commands wait synchronously for a writer. Use a dedicated,
        // observed thread so this test can commit the competing durable transition.
        var thread = new Thread(async () =>
        {
            entered.Set();
            try
            {
                await _repository.UpsertWatchlistFinalizationOutboxAsync("q-race", null, ["/music/new.flac"]);
                finished.TrySetResult();
            }
            catch (Exception error) { finished.TrySetException(error); }
        }) { IsBackground = true };
        thread.Start();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        await Task.Delay(200); // let the old read-before-write path reach the held writer lock
        await using (var command = writer.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE watchlist_finalization_outbox SET status=@status, lease_owner=@owner, lease_until_utc='2099-01-01' WHERE queue_uuid='q-race';";
            command.Parameters.AddWithValue("status", newStatus);
            command.Parameters.AddWithValue("owner", newStatus == "processing" ? "other-worker" : DBNull.Value);
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var row = (await ReadFinalizationRowAsync("q-race"))!;
        Assert.Equal(newStatus, row.Status);
        Assert.Equal(4, row.AttemptCount);
        Assert.Equal(newStatus == "processing" ? "other-worker" : null, row.LeaseOwner);
    }

    // ---------------------------------------------------------------------------
    // Shared helpers
    // ---------------------------------------------------------------------------

    private LibraryRepository NewRepository()
        => new(_configuration, NullLogger<LibraryRepository>.Instance);

    private async Task<int> ExecuteSqlAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteNonQueryAsync();
    }

    private async Task<int> CountOutboxRowsAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM watchlist_finalization_outbox;";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<int> CountSqlAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task RunFinalizationPassAsync()
    {
        try
        {
            await _syncService.ProcessFinalizationWorkAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            throw new Xunit.Sdk.XunitException(
                "Finalization pass failed: " + ex.Message
                + " | Logs: " + string.Join(" || ", _syncLogger.Entries.Select(entry => entry.Message)));
        }
    }

    private async Task ForceRowsDueAsync()
        => await ExecuteSqlAsync(
            "UPDATE watchlist_finalization_outbox SET next_attempt_utc='2000-01-01 00:00:00' WHERE status IN ('pending','retry');");

    private async Task SeedFinalizationRowAsync(
        string queueUuid,
        string? payloadJson,
        IReadOnlyList<string> finalFilePaths,
        string status,
        int attemptCount,
        string? lastError)
    {
        Assert.True(finalFilePaths.Count > 0, "Final paths are required for outbox evidence.");
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO watchlist_finalization_outbox (queue_uuid,payload_json,final_file_paths_json,status,attempt_count,next_attempt_utc,last_error)
VALUES(@queueUuid,@payloadJson,@paths,@status,@attemptCount,'2000-01-01 00:00:00',@lastError);";
        command.Parameters.AddWithValue("queueUuid", queueUuid);
        command.Parameters.AddWithValue("payloadJson", (object?)payloadJson ?? DBNull.Value);
        command.Parameters.AddWithValue("paths", JsonSerializer.Serialize(finalFilePaths));
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("attemptCount", attemptCount);
        command.Parameters.AddWithValue("lastError", (object?)lastError ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static string FinalizationPayload(
        string title,
        string artist,
        string? isrc,
        int durationMs,
        string? deezerTrackId,
        string watchlistSource,
        string watchlistPlaylistId,
        string watchlistTrackId)
    {
        var sourceIds = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (deezerTrackId is not null)
        {
            sourceIds["deezer"] = deezerTrackId;
        }

        sourceIds["watchlist_source"] = watchlistSource;
        sourceIds["watchlist_playlist"] = watchlistPlaylistId;
        sourceIds["watchlist_track"] = watchlistTrackId;
        return JsonSerializer.Serialize(new
        {
            artist,
            title,
            isrc,
            durationMs,
            source_ids = sourceIds
        });
    }

    private static DownloadQueueItem CompletedQueueItem(
        string queueUuid,
        string payloadJson,
        string? deezerTrackId = null,
        string? isrc = null)
        => new(
            Id: 0,
            QueueUuid: queueUuid,
            Engine: "deezer",
            ArtistName: "Artist One",
            TrackTitle: "Verify Song",
            Isrc: isrc,
            DeezerTrackId: deezerTrackId,
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
            QualityRank: null,
            QueueOrder: null,
            ContentType: "stereo",
            Status: "completed",
            PayloadJson: payloadJson,
            Progress: 100,
            Downloaded: 1,
            Failed: 0,
            Error: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

    private async Task<WatchlistFinalizationOutboxDto?> ReadFinalizationRowAsync(string queueUuid)
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT id,queue_uuid,payload_json,final_file_paths_json,status,attempt_count,next_attempt_utc,lease_owner,lease_until_utc,last_error,updated_at
FROM watchlist_finalization_outbox WHERE queue_uuid=@queueUuid LIMIT 1;";
        command.Parameters.AddWithValue("queueUuid", queueUuid);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new WatchlistFinalizationOutboxDto(
            reader.GetInt64(0),
            reader.GetString(1),
            await reader.IsDBNullAsync(2) ? null : reader.GetString(2),
            JsonSerializer.Deserialize<List<string>>(reader.GetString(3)) ?? [],
            reader.GetString(4),
            reader.GetInt32(5),
            DateTimeOffset.Parse(reader.GetString(6)),
            await reader.IsDBNullAsync(7) ? null : reader.GetString(7),
            await reader.IsDBNullAsync(8) ? null : DateTimeOffset.Parse(reader.GetString(8)),
            await reader.IsDBNullAsync(9) ? null : reader.GetString(9),
            DateTimeOffset.Parse(reader.GetString(10)));
    }

    private async Task AddWatchlistAsync(string source, string sourceId)
        => _ = await _repository.AddPlaylistWatchlistAsync(source, sourceId, new PlaylistWatchlistMetadataInput(sourceId, null, null, 1));

    private async Task ConfigureDestinationAsync(string source, string sourceId)
    {
        await _repository.UpsertPlaylistWatchPreferenceAsync(
            new LibraryRepository.PlaylistWatchPreferenceUpsertInput(
                Source: source,
                SourceId: sourceId,
                DestinationFolderId: _destinationFolderId,
                Service: null,
                SyncTargets: null,
                PreferredEngine: null,
                DownloadEngineOrder: null,
                DownloadVariantMode: null,
                SyncMode: null,
                UpdateArtwork: true,
                ReuseSavedArtwork: false));
    }

    private async Task UpsertCandidateCacheAsync(string source, string sourceId, IReadOnlyList<PlaylistTrackCandidate> candidates)
    {
        var engine = _provider.GetRequiredService<PlaylistWatchReconciler>() is { } reconciler
            ? GetEngine(reconciler)
            : throw new InvalidOperationException("Reconciler unavailable.");
        var revision = await (Task<string>)typeof(WatchlistEngine)
            .GetMethod("BuildProviderReadinessRevisionAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(engine, new object[] { source })!;
        var identity = PlaylistCandidateContract.BuildIdentityRevision(source, candidates);
        await _repository.UpsertPlaylistTrackCandidateCacheAsync(
            source,
            sourceId,
            "one",
            JsonSerializer.Serialize(candidates),
            PlaylistCandidateContract.CurrentCacheSchemaVersion,
            identity,
            revision,
            isComplete: true,
            cancellationToken: CancellationToken.None,
            sourceItemsObserved: candidates.Count);
    }

    private static WatchlistEngine GetEngine(PlaylistWatchReconciler reconciler)
    {
        var field = typeof(PlaylistWatchReconciler).GetField("_playlistWatchService", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? typeof(PlaylistWatchReconciler).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (WatchlistEngine)field!.GetValue(reconciler)!;
    }

    private async Task<SeededLibraryFixture> SeedLibraryAsync(params (string Title, string DeezerTrackId, string SpotifyTrackId, string AppleTrackId)[] tracks)
    {
        Assert.NotEmpty(tracks);
        var libraryRoot = Path.Join(_tempRoot, "music", "library-a");
        var folder = await _repository.AddFolderAsync(
            new LibraryRepository.FolderUpsertInput(
                RootPath: libraryRoot,
                DisplayName: "Library A Recovery",
                Enabled: true,
                LibraryName: "Music Recovery",
                DesiredQuality: "flac",
                ConvertEnabled: false,
                ConvertFormat: null,
                ConvertBitrate: null,
                AutoTagProfileId: "finalization-recovery-profile"));
        var artists = new List<LocalArtistScanDto> { new("Artist One", "/covers/artist-one.jpg") };
        var albums = new List<LocalAlbumScanDto>
        {
            new(
                ArtistName: "Artist One",
                Title: "Album One",
                PreferredCoverPath: "/covers/album-one.jpg",
                LocalFolders: new[] { folder.DisplayName },
                HasAnimatedArtwork: false)
        };
        var trackDtos = new List<LocalTrackScanDto>();
        var pathsByTitle = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < tracks.Length; i++)
        {
            var item = tracks[i];
            var isrc = $"ISRC{i:00000000}";
            var filePath = Path.Join(libraryRoot, "Artist One", "Album One", $"{i + 1:00} - {item.Title}.flac");
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            await File.WriteAllTextAsync(filePath, "audio placeholder");
            pathsByTitle[item.Title] = filePath;
            trackDtos.Add(new LocalTrackScanDto(
                ArtistName: "Artist One",
                AlbumTitle: "Album One",
                Title: item.Title,
                FilePath: filePath,
                TagTitle: item.Title,
                TagArtist: "Artist One",
                TagAlbum: "Album One",
                TagAlbumArtist: "Artist One",
                TagVersion: null,
                TagLabel: "Label One",
                TagCatalogNumber: "CAT-001",
                TagBpm: 120,
                TagKey: null,
                TagTrackTotal: 1,
                TagDurationMs: 180000,
                TagYear: 2025,
                TagTrackNo: i + 1,
                TagDisc: 1,
                TagGenre: "Soundtrack",
                TagIsrc: isrc,
                TagReleaseDate: "2025-01-01",
                TagPublishDate: "2025-01-01",
                TagUrl: null,
                TagReleaseId: null,
                TagTrackId: null,
                TagMetaTaggedDate: null,
                LyricsUnsynced: null,
                LyricsSynced: null,
                TagGenres: ["Soundtrack"],
                TagStyles: Array.Empty<string>(),
                TagMoods: Array.Empty<string>(),
                TagRemixers: Array.Empty<string>(),
                TagOtherTags: Array.Empty<LocalTrackOtherTag>(),
                TrackNo: i + 1,
                Disc: 1,
                DurationMs: 180000,
                LyricsStatus: null,
                LyricsType: null,
                Codec: "flac",
                BitrateKbps: 1000,
                SampleRateHz: 48000,
                BitsPerSample: 24,
                Channels: 2,
                QualityRank: 4,
                AudioVariant: "stereo",
                DeezerTrackId: item.DeezerTrackId,
                Isrc: isrc,
                DeezerAlbumId: null,
                DeezerArtistId: null,
                SpotifyTrackId: item.SpotifyTrackId,
                SpotifyAlbumId: null,
                SpotifyArtistId: null,
                AppleTrackId: item.AppleTrackId,
                AppleAlbumId: null,
                AppleArtistId: null,
                Source: "spotify",
                SourceId: item.SpotifyTrackId));
        }

        await _repository.IngestLocalScanAsync(
            await _repository.GetFoldersAsync(),
            artists,
            albums,
            trackDtos,
            pruneMissingArtists: true);
        var trackIdsByTitle = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var isrcByTitle = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in tracks)
        {
            var trackId = await _repository.GetTrackIdForFilePathAsync(pathsByTitle[item.Title]);
            Assert.NotNull(trackId);
            trackIdsByTitle[item.Title] = trackId!.Value;
            isrcByTitle[item.Title] = $"ISRC{Array.IndexOf(tracks, item):00000000}";
        }

        return new SeededLibraryFixture(
            folder,
            trackIdsByTitle,
            pathsByTitle,
            isrcByTitle);
    }

    private sealed record SeededLibraryFixture(
        FolderDto Folder,
        IReadOnlyDictionary<string, long> TrackIdsByTitle,
        IReadOnlyDictionary<string, string> TrackPathsByTitle,
        IReadOnlyDictionary<string, string> IsrcByTitle);

    private sealed class StubWebHostEnvironment(string rootPath) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "DeezSpoTag.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new PhysicalFileProvider(rootPath);
        public string WebRootPath { get; set; } = rootPath;
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ContentRootPath { get; set; } = rootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(rootPath);
    }

    private sealed class StubHttpClientFactory : System.Net.Http.IHttpClientFactory
    {
        public System.Net.Http.HttpClient CreateClient(string name) => new(new NotFoundHandler());

        private sealed class NotFoundHandler : System.Net.Http.HttpMessageHandler
        {
            protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
                System.Net.Http.HttpRequestMessage request,
                CancellationToken cancellationToken)
                => Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    // Inject faults at the real verifier's audio boundary without replacing its state changes.
    private sealed class FinalizationFaultLogger : ILogger<WatchlistFinalizationService>
    {
        public Action<string>? OnLog { get; set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error,
            Func<TState, Exception?, string> formatter) => OnLog?.Invoke(formatter(state, error));
    }
}
