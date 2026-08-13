using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Controllers.Api;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Integrations.Tidal;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

[Collection("Settings Config Isolation")]
public sealed class WatchlistRunCoordinatorHardeningTests : IAsyncLifetime
{
    private string _tempRoot = string.Empty;
    private TestConfigRootScope _configScope = default!;
    private LibraryRepository _repository = default!;
    private LibraryConfigStore _configStore = default!;
    private PlaylistVisualService _playlistVisualService = default!;
    private DeezSpoTagSettingsService _settingsService = default!;
    private DownloadQueueRepository _queueRepository = default!;
    private ServiceProvider _provider = default!;
    private long _destinationFolderId;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-watch-hosted-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
        _configScope = new TestConfigRootScope(_tempRoot);

        var dbPath = Path.Join(_tempRoot, "library.db");
        var queueDbPath = Path.Join(_tempRoot, "queue.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = $"Data Source={dbPath}",
                ["ConnectionStrings:Queue"] = $"Data Source={queueDbPath}",
                ["DataDirectory"] = _tempRoot
            })
            .Build();

        var dbService = new LibraryDbService(config, NullLogger<LibraryDbService>.Instance);
        await dbService.EnsureSchemaAsync();

        _repository = new LibraryRepository(config, NullLogger<LibraryRepository>.Instance);
        _configStore = new LibraryConfigStore(
            _repository,
            NullLogger<LibraryConfigStore>.Instance,
            new StubHostEnvironment(_tempRoot));
        _playlistVisualService = new PlaylistVisualService(
            new StubHttpClientFactory(),
            new StubWebHostEnvironment(_tempRoot),
            NullLogger<PlaylistVisualService>.Instance);
        _settingsService = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
        var settings = _settingsService.LoadSettings();
        settings.WatchEnabled = true;
        settings.WatchPollIntervalSeconds = 1;
        settings.WatchDelayBetweenArtistsSeconds = 1;
        settings.WatchDelayBetweenPlaylistsSeconds = 1;
        settings.WatchMaxItemsPerRun = 50;
        _settingsService.SaveSettings(settings);
        var profileService = new TaggingProfileService(
            new StubWebHostEnvironment(_tempRoot),
            NullLogger<TaggingProfileService>.Instance);
        var profiles = await profileService.LoadAsync();
        profiles.Add(new DeezSpoTag.Core.Models.Settings.TaggingProfile
        {
            Id = "watchlist-test-profile",
            Name = "Watchlist Test Profile"
        });
        await profileService.SaveAsync(profiles);
        var destinationFolder = await _repository.AddFolderAsync(
            new LibraryRepository.FolderUpsertInput(
                RootPath: Path.Join(_tempRoot, "music"),
                DisplayName: "Watchlist Test Library",
                Enabled: true,
                LibraryName: "Music",
                DesiredQuality: "flac",
                ConvertEnabled: false,
                ConvertFormat: null,
                ConvertBitrate: null,
                AutoTagProfileId: "watchlist-test-profile"));
        _destinationFolderId = destinationFolder.Id;
        var queueAdmission = new WatchlistQueueAdmissionService();
        _queueRepository = new DownloadQueueRepository(
            config,
            NullLogger<DownloadQueueRepository>.Instance);

        var playlistWatchService = new WatchlistEngine(
            _repository,
            new WatchlistEngine.PlaylistWatchPlatformServices
            {
                SpotifyMetadataService = null!,
                SpotifyPathfinderMetadataClient = null!,
                SpotifyArtistService = null!,
                DeezerClient = null!,
                DeezerGatewayService = null!,
                AppleCatalogService = null!,
                BoomplayMetadataService = null!,
                BoomplayWatchlistMappingService = null!,
                LibraryRecommendationService = null!,
                HttpClientFactory = new StubHttpClientFactory(),
                TidalAccessTokenProvider = new StubTidalAccessTokenProvider()
            },
            new WatchlistEngine.PlaylistWatchRuntimeServices
            {
                PlaylistVisualService = null!,
                WatchlistQueueAdmissionService = queueAdmission,
                ActivitiesRealtimeService = null!
            },
            _settingsService,
            serviceProvider: null!,
            localIdentityResolver: new PassthroughLocalTrackAmbiguityResolver(),
            logger: NullLogger<WatchlistEngine>.Instance);

        var artistWatchService = new ArtistWatchService(
            _repository,
            new ArtistWatchPlatformDependencies(
                spotifyArtistService: null!,
                spotifyMetadataService: null!,
                appleCatalogService: null!,
                deezerClient: null!,
                qobuzArtistService: null!,
                qobuzApiClient: null!,
                tidalTokens: null!,
                httpClientFactory: null!),
            new WatchlistQueueService(playlistWatchService),
            _settingsService,
            activitiesRealtime: null!,
            NullLogger<ArtistWatchService>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton(_settingsService);
        services.AddSingleton(_repository);
        services.AddSingleton(_queueRepository);
        services.AddSingleton(queueAdmission);
        services.AddSingleton(playlistWatchService);
        services.AddSingleton(new PlaylistWatchReconciler(playlistWatchService));
        services.AddSingleton(artistWatchService);
        services.AddSingleton(CreateProfileResolutionService());
        _provider = services.BuildServiceProvider();
    }

    public Task DisposeAsync()
    {
        _provider?.Dispose();
        _configScope?.Dispose();
        try
        {
            if (!string.IsNullOrWhiteSpace(_tempRoot) && Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup.
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task RunOnce_UsesBackoff_ToAvoidImmediateFailureThrash()
    {
        // Failing source path: deezer (service dependencies intentionally null).
        await _repository.AddPlaylistWatchlistAsync("deezer", "pl-fail", new PlaylistWatchlistMetadataInput("Failing", null, null, null));
        // Successful/no-op source path: unsupported source branch.
        await _repository.AddPlaylistWatchlistAsync("unsupported", "pl-ok", new PlaylistWatchlistMetadataInput("Noop", null, null, null));

        var hosted = new WatchlistRunCoordinator(_provider, NullLogger<WatchlistRunCoordinator>.Instance);
        var failKey = "playlist:deezer:pl-fail";

        await InvokeRunOnceAsync(hosted);
        var failuresAfterFirst = GetFailureMap(hosted);
        failuresAfterFirst.TryGetValue(failKey, out var firstFailures);

        // Immediate rerun should skip fail key due to nextAllowedRun backoff.
        await InvokeRunOnceAsync(hosted);
        var failuresAfterSecond = GetFailureMap(hosted);
        failuresAfterSecond.TryGetValue(failKey, out var secondFailures);
        Assert.True(secondFailures <= Math.Max(1, firstFailures));

        // Force eligibility and rerun; canonical flow should not thrash failures.
        var nextAllowed = GetNextAllowedMap(hosted);
        if (nextAllowed.ContainsKey(failKey))
        {
            nextAllowed[failKey] = DateTimeOffset.UtcNow.AddSeconds(-1);
        }
        await InvokeRunOnceAsync(hosted);
        var failuresAfterThird = GetFailureMap(hosted);
        failuresAfterThird.TryGetValue(failKey, out var thirdFailures);
        Assert.True(thirdFailures <= Math.Max(1, secondFailures));
    }

    [Fact]
    public async Task RunOnce_DoesNotDeferAdmissionWhenDownloadPipelineIsBusy()
    {
        await _repository.AddPlaylistWatchlistAsync("unsupported", "pl-queue-gate", new PlaylistWatchlistMetadataInput("Queue Gate", null, null, null));
        await ConfigurePlaylistDestinationAsync("unsupported", "pl-queue-gate");
        await _queueRepository.EnqueueAsync(
            CreateQueueItem(
                "previous-watch-active",
                "queued",
                "{\"WatchlistOrigin\":\"playlist\",\"WatchlistSource\":\"spotify\",\"WatchlistPlaylistId\":\"playlist-1\",\"WatchlistTrackId\":\"track-1\"}"),
            CancellationToken.None);
        var logger = new ListLogger<WatchlistRunCoordinator>();
        var hosted = new WatchlistRunCoordinator(_provider, logger);

        await InvokeRunOnceAsync(hosted);

        Assert.DoesNotContain(
            logger.Entries,
            entry => entry.Message.Contains(
                "Watchlist download admission deferred while source metadata and target synchronization continue",
                StringComparison.Ordinal));
        var state = await _repository.GetPlaylistWatchStateAsync("unsupported", "pl-queue-gate", CancellationToken.None);
        Assert.NotNull(state?.LastCheckedUtc);
    }

    [Fact]
    public async Task RunOnce_DoesNotTreatActiveManualDownloadAsPreviousWatchlistWork()
    {
        await _queueRepository.EnqueueAsync(
            CreateQueueItem(
                "manual-active",
                "downloading",
                "{\"title\":\"Manual Track\",\"artist\":\"Artist\"}"),
            CancellationToken.None);
        var logger = new ListLogger<WatchlistRunCoordinator>();
        var hosted = new WatchlistRunCoordinator(_provider, logger);

        await InvokeRunOnceAsync(hosted);

        Assert.DoesNotContain(
            logger.Entries,
            entry => entry.Message.Contains(
                "previous Watchlist run to finish",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunOnce_CleansStaleFailureState_WhenWatchItemIsRemoved()
    {
        await _repository.AddPlaylistWatchlistAsync("deezer", "pl-stale", new PlaylistWatchlistMetadataInput("StaleFailing", null, null, null));

        var hosted = new WatchlistRunCoordinator(_provider, NullLogger<WatchlistRunCoordinator>.Instance);
        var staleKey = "playlist:deezer:pl-stale";

        await InvokeRunOnceAsync(hosted);
        var failures = GetFailureMap(hosted);
        Assert.True(failures.ContainsKey(staleKey));

        await _repository.RemovePlaylistWatchlistAsync("deezer", "pl-stale");
        await InvokeRunOnceAsync(hosted);

        var failuresAfterCleanup = GetFailureMap(hosted);
        Assert.False(failuresAfterCleanup.ContainsKey(staleKey));
        var nextAllowedAfterCleanup = GetNextAllowedMap(hosted);
        Assert.False(nextAllowedAfterCleanup.ContainsKey(staleKey));
    }

    [Fact]
    public async Task RunOnce_HighVolumePlaylistLoad_FailFastAvoidsNoopFullSweep()
    {
        var apiController = CreatePlaylistWatchlistController(_provider.GetRequiredService<WatchlistEngine>());

        var total = 220;
        for (var index = 0; index < total; index++)
        {
            var result = await apiController.Add(
                new WatchlistApiController.PlaylistWatchlistRequest(
                    Source: "unsupported",
                    SourceId: $"pl-load-{index:D4}",
                    Name: $"Load Playlist {index:D4}",
                    ImageUrl: null,
                    Description: null,
                    TrackCount: null),
                CancellationToken.None);
            Assert.IsType<OkObjectResult>(result);
        }

        var hosted = new WatchlistRunCoordinator(_provider, NullLogger<WatchlistRunCoordinator>.Instance);
        var roundCount = (int)Math.Ceiling(total / 50.0) + 1;
        for (var round = 0; round < roundCount; round++)
        {
            await InvokeRunOnceAsync(hosted);
        }

        var lastRun = GetLastRunMap(hosted);
        var processedUnsupported = lastRun.Keys.Count(key => key.StartsWith("playlist:unsupported:pl-load-", StringComparison.Ordinal));
        Assert.True(processedUnsupported >= 1);

        var failures = GetFailureMap(hosted);
        Assert.DoesNotContain(failures.Keys, key => key.StartsWith("playlist:unsupported:pl-load-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunOnce_ZeroQueueActivePlaylist_AdvancesWhenThereIsNoBlocker()
    {
        await _repository.AddPlaylistWatchlistAsync("unsupported", "pl-zero-1", new PlaylistWatchlistMetadataInput("Zero One", null, null, null));
        await _repository.AddPlaylistWatchlistAsync("unsupported", "pl-zero-2", new PlaylistWatchlistMetadataInput("Zero Two", null, null, null));
        await ConfigurePlaylistDestinationAsync("unsupported", "pl-zero-1");
        await ConfigurePlaylistDestinationAsync("unsupported", "pl-zero-2");

        var hosted = new WatchlistRunCoordinator(_provider, NullLogger<WatchlistRunCoordinator>.Instance);
        await InvokeRunOnceAsync(hosted);

        var firstState = await _repository.GetPlaylistWatchStateAsync("unsupported", "pl-zero-2", CancellationToken.None);
        var secondState = await _repository.GetPlaylistWatchStateAsync("unsupported", "pl-zero-1", CancellationToken.None);
        Assert.NotNull(firstState?.LastCheckedUtc);
        Assert.NotNull(secondState?.LastCheckedUtc);
    }

    [Fact]
    public async Task RunOnce_SourceCircuitOpen_SkipsPlaylistProcessingForThatSource()
    {
        await _repository.AddPlaylistWatchlistAsync("unsupported", "pl-circuit-1", new PlaylistWatchlistMetadataInput("Circuit One", null, null, null));
        await _repository.AddPlaylistWatchlistAsync("unsupported", "pl-circuit-2", new PlaylistWatchlistMetadataInput("Circuit Two", null, null, null));

        await _repository.UpsertWatchlistSchedulerStateAsync(
            new LibraryRepository.WatchlistSchedulerStateUpsertInput(
                "playlist",
                "unsupported",
                "pl-circuit-2",
                DateTimeOffset.UtcNow,
                null),
            CancellationToken.None);
        await _repository.UpsertWatchlistSourceCircuitStateAsync(
            new LibraryRepository.WatchlistSourceCircuitStateUpsertInput(
                "playlist",
                "unsupported",
                IsOpen: true,
                OpenUntilUtc: DateTimeOffset.UtcNow.AddMinutes(5),
                Reason: "Rate limited",
                Fingerprint: "provider_http_429",
                FailureCount: 3),
            CancellationToken.None);

        var hosted = new WatchlistRunCoordinator(_provider, NullLogger<WatchlistRunCoordinator>.Instance);
        await InvokeRunOnceAsync(hosted);

        var stateOne = await _repository.GetPlaylistWatchStateAsync("unsupported", "pl-circuit-1", CancellationToken.None);
        var stateTwo = await _repository.GetPlaylistWatchStateAsync("unsupported", "pl-circuit-2", CancellationToken.None);
        Assert.True(stateOne == null || stateOne.LastCheckedUtc == null);
        Assert.NotNull(stateTwo);
        Assert.Equal("circuit_open", stateTwo!.LastRunStatus);
    }

    [Fact]
    public async Task RunOnce_Restart_DoesNotPreserveActivePlaylistCursor()
    {
        await _repository.AddPlaylistWatchlistAsync("unsupported", "pl-cursor-1", new PlaylistWatchlistMetadataInput("Cursor One", null, null, null));
        await _repository.AddPlaylistWatchlistAsync("unsupported", "pl-cursor-2", new PlaylistWatchlistMetadataInput("Cursor Two", null, null, null));
        await ConfigurePlaylistDestinationAsync("unsupported", "pl-cursor-1");
        await ConfigurePlaylistDestinationAsync("unsupported", "pl-cursor-2");

        var firstHosted = new WatchlistRunCoordinator(_provider, NullLogger<WatchlistRunCoordinator>.Instance);
        await InvokeRunOnceAsync(firstHosted);
        var schedulerBefore = await _repository.GetWatchlistSchedulerStateAsync("playlist", CancellationToken.None);
        Assert.Null(schedulerBefore?.ActiveSourceId);

        var restartedHosted = new WatchlistRunCoordinator(_provider, NullLogger<WatchlistRunCoordinator>.Instance);
        await InvokeRunOnceAsync(restartedHosted);
        var schedulerAfter = await _repository.GetWatchlistSchedulerStateAsync("playlist", CancellationToken.None);
        Assert.Null(schedulerAfter?.ActiveSourceId);
    }

    [Fact]
    public async Task RunOnce_StalePlaylistFocus_DoesNotOverridePriorityOrder()
    {
        var settings = _settingsService.LoadSettings();
        settings.WatchMaxTracksPerPlaylistCheck = 1;
        _settingsService.SaveSettings(settings);

        await _repository.AddPlaylistWatchlistAsync("unsupported", "pl-stale-focus-target", new PlaylistWatchlistMetadataInput("Stale Focus Target", null, null, null));
        await _repository.AddPlaylistWatchlistAsync("unsupported", "pl-priority-first", new PlaylistWatchlistMetadataInput("Priority First", null, null, null));
        await ConfigurePlaylistDestinationAsync("unsupported", "pl-stale-focus-target");
        await ConfigurePlaylistDestinationAsync("unsupported", "pl-priority-first");
        await _repository.UpsertWatchlistSchedulerStateAsync(
            new LibraryRepository.WatchlistSchedulerStateUpsertInput(
                "playlist",
                "unsupported",
                "pl-stale-focus-target",
                DateTimeOffset.UtcNow.AddHours(-2),
                null),
            CancellationToken.None);

        var hosted = new WatchlistRunCoordinator(_provider, NullLogger<WatchlistRunCoordinator>.Instance);
        await InvokeRunOnceAsync(hosted);

        var firstState = await _repository.GetPlaylistWatchStateAsync("unsupported", "pl-priority-first", CancellationToken.None);
        var staleFocusState = await _repository.GetPlaylistWatchStateAsync("unsupported", "pl-stale-focus-target", CancellationToken.None);
        Assert.NotNull(firstState?.LastCheckedUtc);
        Assert.NotNull(staleFocusState?.LastCheckedUtc);
        Assert.True(firstState!.LastCheckedUtc <= staleFocusState!.LastCheckedUtc);
    }

    [Fact]
    public async Task RunOnce_RecentExplicitPlaylistFocus_DoesNotOverridePriorityOrder()
    {
        var settings = _settingsService.LoadSettings();
        settings.WatchMaxTracksPerPlaylistCheck = 1;
        _settingsService.SaveSettings(settings);

        await _repository.AddPlaylistWatchlistAsync("unsupported", "pl-explicit-focus-target", new PlaylistWatchlistMetadataInput("Explicit Focus Target", null, null, null));
        await _repository.AddPlaylistWatchlistAsync("unsupported", "pl-priority-first", new PlaylistWatchlistMetadataInput("Priority First", null, null, null));
        await ConfigurePlaylistDestinationAsync("unsupported", "pl-explicit-focus-target");
        await ConfigurePlaylistDestinationAsync("unsupported", "pl-priority-first");
        await _repository.UpsertWatchlistSchedulerStateAsync(
            new LibraryRepository.WatchlistSchedulerStateUpsertInput(
                "playlist",
                "unsupported",
                "pl-explicit-focus-target",
                DateTimeOffset.UtcNow,
                null),
            CancellationToken.None);

        var hosted = new WatchlistRunCoordinator(_provider, NullLogger<WatchlistRunCoordinator>.Instance);
        await InvokeRunOnceAsync(hosted);

        var firstState = await _repository.GetPlaylistWatchStateAsync("unsupported", "pl-priority-first", CancellationToken.None);
        var explicitFocusState = await _repository.GetPlaylistWatchStateAsync("unsupported", "pl-explicit-focus-target", CancellationToken.None);
        Assert.NotNull(explicitFocusState?.LastCheckedUtc);
        Assert.NotNull(firstState?.LastCheckedUtc);
        Assert.True(firstState!.LastCheckedUtc <= explicitFocusState!.LastCheckedUtc);
    }

    [Fact]
    public async Task GetAll_RejectsBulkSourceRefreshAndKeepsListCacheOnly()
    {
        await _repository.AddPlaylistWatchlistAsync("spotify", "pl-cache-only", new PlaylistWatchlistMetadataInput("Cache Only", null, null, null));

        var apiController = CreatePlaylistWatchlistController(_provider.GetRequiredService<WatchlistEngine>());

        var rejected = await apiController.GetAll(CancellationToken.None, refreshFromSource: true);
        var badRequest = Assert.IsType<BadRequestObjectResult>(rejected);
        Assert.Contains("cache-only", Assert.IsType<string>(badRequest.Value), StringComparison.OrdinalIgnoreCase);

        var cached = await apiController.GetAll(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(cached);
        var items = Assert.IsAssignableFrom<IReadOnlyList<PlaylistWatchlistDto>>(ok.Value);
        Assert.Single(items);
        Assert.Equal("Cache Only", items[0].Name);
    }

    [Fact]
    public async Task TriggerAll_SchedulesRateLimitedRunWithoutInlinePlaylistRefresh()
    {
        await _repository.AddPlaylistWatchlistAsync("spotify", "pl-trigger-scheduled", new PlaylistWatchlistMetadataInput("Scheduled", null, null, null));

        var apiController = CreatePlaylistWatchlistController(_provider.GetRequiredService<WatchlistEngine>());

        var result = await apiController.TriggerAll(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result);
        var queued = ok.Value?.GetType().GetProperty("queued")?.GetValue(ok.Value);
        var pending = ok.Value?.GetType().GetProperty("pending")?.GetValue(ok.Value);
        var queuedFlag = Assert.IsType<bool>(queued);
        Assert.False(queuedFlag);
        Assert.Equal(1, pending);

        var state = await _repository.GetPlaylistWatchStateAsync("spotify", "pl-trigger-scheduled", CancellationToken.None);
        Assert.Null(state?.LastCheckedUtc);
    }

    [Fact]
    public async Task GetWatchRuntime_ReturnsSchedulerAndCircuitTelemetry()
    {
        await _repository.AddPlaylistWatchlistAsync("unsupported", "pl-runtime-1", new PlaylistWatchlistMetadataInput("Runtime One", null, null, null));
        await _repository.UpsertWatchlistSchedulerStateAsync(
            new LibraryRepository.WatchlistSchedulerStateUpsertInput(
                "playlist",
                "unsupported",
                "pl-runtime-1",
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow),
            CancellationToken.None);
        await _repository.UpsertWatchlistSourceCircuitStateAsync(
            new LibraryRepository.WatchlistSourceCircuitStateUpsertInput(
                "playlist",
                "unsupported",
                IsOpen: true,
                OpenUntilUtc: DateTimeOffset.UtcNow.AddMinutes(3),
                Reason: "Rate limited",
                Fingerprint: "provider_http_429",
                FailureCount: 2),
            CancellationToken.None);

        var controller = CreatePlaylistWatchlistController(_provider.GetRequiredService<WatchlistEngine>());

        var result = await controller.GetWatchRuntime(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public async Task RunOnce_UsesPersistedLastChecked_ToRespectIntervalAfterRestart()
    {
        var settings = _settingsService.LoadSettings();
        settings.WatchDelayBetweenPlaylistsSeconds = 3600;
        _settingsService.SaveSettings(settings);

        await _repository.AddPlaylistWatchlistAsync("unsupported", "pl-persisted-delay", new PlaylistWatchlistMetadataInput("Persisted Delay", null, null, null));

        var firstHosted = new WatchlistRunCoordinator(_provider, NullLogger<WatchlistRunCoordinator>.Instance);
        await InvokeRunOnceAsync(firstHosted);

        var state = await _repository.GetPlaylistWatchStateAsync("unsupported", "pl-persisted-delay", CancellationToken.None);
        Assert.NotNull(state?.LastCheckedUtc);
        var firstLastCheckedUtc = state!.LastCheckedUtc;

        var restartedHosted = new WatchlistRunCoordinator(_provider, NullLogger<WatchlistRunCoordinator>.Instance);
        await InvokeRunOnceAsync(restartedHosted);

        var stateAfterRestart = await _repository.GetPlaylistWatchStateAsync("unsupported", "pl-persisted-delay", CancellationToken.None);
        Assert.Equal(firstLastCheckedUtc, stateAfterRestart?.LastCheckedUtc);
        var restartedLastRun = GetLastRunMap(restartedHosted);
        Assert.True(restartedLastRun.ContainsKey("playlist:unsupported:pl-persisted-delay"));
        var restartedFailures = GetFailureMap(restartedHosted);
        Assert.Empty(restartedFailures);
    }

    [Fact]
    public async Task RunOnce_BackoffWarnings_OnlyLogAtThresholdMilestones()
    {
        await _repository.AddPlaylistWatchlistAsync("deezer", "pl-log-threshold", new PlaylistWatchlistMetadataInput("Failing", null, null, null));

        var logger = new ListLogger<WatchlistRunCoordinator>();
        var hosted = new WatchlistRunCoordinator(_provider, logger);
        var failKey = "playlist:deezer:pl-log-threshold";

        for (var run = 0; run < 6; run++)
        {
            var nextAllowed = GetNextAllowedMap(hosted);
            nextAllowed[failKey] = DateTimeOffset.UtcNow.AddSeconds(-1);
            await InvokeRunOnceAsync(hosted);
        }

        var warningCount = logger.Entries.Count(entry =>
            entry.Level == LogLevel.Warning
            && entry.Message.Contains("Watchlist item failed:", StringComparison.Ordinal));
        var debugCount = logger.Entries.Count(entry =>
            entry.Level == LogLevel.Debug
            && entry.Message.Contains("still failing under backoff threshold", StringComparison.Ordinal));

        // Canonical monitor flow should keep warning noise low.
        Assert.Equal(1, warningCount);
        Assert.Equal(0, debugCount);
    }

    [Fact]
    public async Task RecoverCoordinatorState_AppliesOneShotSmoothSyncRecoveryAndForcesSourceRefresh()
    {
        await _repository.AddPlaylistWatchlistAsync(
            "unsupported",
            "pl-smooth-recovery",
            new PlaylistWatchlistMetadataInput("Smooth Recovery", null, null, null));
        await _repository.UpsertPlaylistWatchStateAsync(
            new LibraryRepository.PlaylistWatchStateUpsertInput(
                "unsupported",
                "pl-smooth-recovery",
                null,
                1,
                null,
                null,
                DateTimeOffset.UtcNow.AddHours(-1),
                "backoff",
                "Recovered stale Watchlist work after its persisted deadline expired.",
                DateTimeOffset.UtcNow.AddHours(-2),
                3,
                "stale_recovered",
                1,
                1,
                DateTimeOffset.UtcNow.AddHours(-1),
                null));

        var hosted = new WatchlistRunCoordinator(_provider, NullLogger<WatchlistRunCoordinator>.Instance);
        SetPrivateField(hosted, "_lastSourceRefreshCompletedUtc", DateTimeOffset.UtcNow);
        GetNextAllowedMap(hosted)["playlist:unsupported:pl-smooth-recovery"] = DateTimeOffset.UtcNow.AddHours(1);

        await InvokeRecoverCoordinatorStateAsync(hosted);

        var state = await _repository.GetPlaylistWatchStateAsync("unsupported", "pl-smooth-recovery");
        Assert.Equal("pending", state!.LastRunStatus);
        Assert.Equal("pending", state.CurrentPhase);
        Assert.Equal(0, state.ConsecutiveFailures);
        Assert.Equal(DateTimeOffset.MinValue, GetPrivateField<DateTimeOffset>(hosted, "_lastSourceRefreshCompletedUtc"));
        Assert.False(GetNextAllowedMap(hosted).ContainsKey("playlist:unsupported:pl-smooth-recovery"));

        SetPrivateField(hosted, "_lastSourceRefreshCompletedUtc", DateTimeOffset.UtcNow);
        await InvokeRecoverCoordinatorStateAsync(hosted);
        Assert.NotEqual(DateTimeOffset.MinValue, GetPrivateField<DateTimeOffset>(hosted, "_lastSourceRefreshCompletedUtc"));
    }

    [Fact]
    public void CycleBox_StopsNewPlaylistsAndPersistsProgressAfterEachSlice()
    {
        var hostedSource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "DeezSpoTag.Web", "Services", "WatchlistRunCoordinator.cs"));

        Assert.Contains("SteadyCycleBudget = TimeSpan.FromMinutes(4)", hostedSource, StringComparison.Ordinal);
        Assert.Contains("RecoveryCycleBudget = TimeSpan.FromMinutes(10)", hostedSource, StringComparison.Ordinal);
        Assert.Contains("PlaylistStartReserve = TimeSpan.FromSeconds(15)", hostedSource, StringComparison.Ordinal);
        Assert.Contains("RemainingCycleBudget(cycleDeadline) < PlaylistStartReserve", hostedSource, StringComparison.Ordinal);
        Assert.Contains("PersistPlaylistProgressAsync", hostedSource, StringComparison.Ordinal);
        Assert.Contains("WatchSmoothSyncEnabled", hostedSource, StringComparison.Ordinal);
        Assert.Contains("RunInterleavedPlaylistSliceAsync", hostedSource, StringComparison.Ordinal);
        Assert.Contains("HasPollOverduePlaylistAsync", hostedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("watchlist_shared_identity", hostedSource, StringComparison.Ordinal);
        var loopStart = hostedSource.IndexOf("foreach (var activeItem in scheduledItems)", StringComparison.Ordinal);
        var loopEnd = hostedSource.IndexOf("private async Task<WatchItemExecutionOutcome> RunInterleavedPlaylistSliceAsync(", loopStart, StringComparison.Ordinal);
        var loopBody = hostedSource[loopStart..loopEnd];
        Assert.Contains("PersistPlaylistProgressAsync(repository, activeItem, stoppingToken)", loopBody, StringComparison.Ordinal);
        Assert.Contains("RunInterleavedPlaylistSliceAsync(", loopBody, StringComparison.Ordinal);
        Assert.Contains("TryProcessItemAsync(", loopBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunOnce_PersistsLastProgressAfterEachPlaylistOnBothFlagValues()
    {
        await _repository.AddPlaylistWatchlistAsync("unsupported", "pl-progress-1", new PlaylistWatchlistMetadataInput("Progress One", null, null, null));
        await _repository.AddPlaylistWatchlistAsync("unsupported", "pl-progress-2", new PlaylistWatchlistMetadataInput("Progress Two", null, null, null));
        await ConfigurePlaylistDestinationAsync("unsupported", "pl-progress-1");
        await ConfigurePlaylistDestinationAsync("unsupported", "pl-progress-2");

        var hosted = new WatchlistRunCoordinator(_provider, NullLogger<WatchlistRunCoordinator>.Instance);
        await InvokeRunOnceAsync(hosted);
        var afterFlagOff = await _repository.GetWatchlistSchedulerStateAsync("playlist", CancellationToken.None);
        Assert.NotNull(afterFlagOff?.LastProgressUtc);

        var settings = _settingsService.LoadSettings();
        settings.WatchSmoothSyncEnabled = true;
        _settingsService.SaveSettings(settings);
        await InvokeRunOnceAsync(hosted);
        var afterFlagOn = await _repository.GetWatchlistSchedulerStateAsync("playlist", CancellationToken.None);
        Assert.NotNull(afterFlagOn?.LastProgressUtc);
        Assert.True(afterFlagOn!.LastProgressUtc >= afterFlagOff!.LastProgressUtc);
    }

    [Fact]
    public async Task GetNextWake_PrefersDuePlaylistsOverDueJobs()
    {
        var settings = _settingsService.LoadSettings();
        settings.WatchPollIntervalSeconds = 3600;
        _settingsService.SaveSettings(settings);

        await _repository.AddPlaylistWatchlistAsync("unsupported", "pl-overdue", new PlaylistWatchlistMetadataInput("Overdue", null, null, null));
        await _repository.UpsertPlaylistWatchStateAsync(
            new LibraryRepository.PlaylistWatchStateUpsertInput(
                "unsupported",
                "pl-overdue",
                null,
                1,
                null,
                null,
                DateTimeOffset.UtcNow.AddHours(-2),
                "pending",
                null,
                null,
                0,
                "pending",
                0,
                1,
                DateTimeOffset.UtcNow.AddHours(-2),
                null));
        await _repository.UpsertPlaylistWatchPreferenceAsync(
            new LibraryRepository.PlaylistWatchPreferenceUpsertInput(
                Source: "unsupported",
                SourceId: "pl-overdue",
                DestinationFolderId: _destinationFolderId,
                Service: "plex",
                SyncTargets: ["plex"],
                PreferredEngine: null,
                DownloadEngineOrder: null,
                DownloadVariantMode: null,
                SyncMode: "mirror",
                UpdateArtwork: true,
                ReuseSavedArtwork: false));
        await _repository.EnqueueWatchlistPlaylistSyncJobsAsync("unsupported", "pl-overdue", "snapshot-due");

        var hosted = new WatchlistRunCoordinator(_provider, NullLogger<WatchlistRunCoordinator>.Instance);
        var wake = await InvokeGetNextWakeAsync(hosted);
        Assert.Equal(TimeSpan.Zero, wake.Delay);
        Assert.True(wake.Reason.HasFlag(WatchlistWakeReason.ScheduledRefresh) || wake.Reason.HasFlag(WatchlistWakeReason.Reconciliation));
        Assert.False(wake.Reason == WatchlistWakeReason.TargetSync);

        await _repository.UpsertPlaylistWatchStateAsync(
            new LibraryRepository.PlaylistWatchStateUpsertInput(
                "unsupported",
                "pl-overdue",
                null,
                1,
                null,
                null,
                DateTimeOffset.UtcNow,
                "unchanged",
                null,
                null,
                0,
                "unchanged",
                0,
                1,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(45)));

        var residual = await InvokeGetNextWakeAsync(hosted);
        Assert.Equal(WatchlistWakeReason.TargetSync, residual.Reason);
    }

    [Fact]
    public void HeartbeatAndProgress_DoNotOverwriteCurrentPhase()
    {
        var repository = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "DeezSpoTag.Services", "Library", "LibraryRepository.cs"));
        var admission = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "DeezSpoTag.Web", "Services", "WatchlistQueueAdmissionService.cs"));

        Assert.Contains("TouchPlaylistWatchHeartbeatAsync", repository, StringComparison.Ordinal);
        Assert.DoesNotContain("SET current_phase=@phase", repository, StringComparison.Ordinal);
        Assert.DoesNotContain("heartbeat_utc=@heartbeatUtc", repository, StringComparison.Ordinal);
        Assert.Contains("current_track_index=@currentTrackIndex", repository, StringComparison.Ordinal);
        Assert.Contains("now.AddMinutes(45)", admission, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_UnknownWatchlistStatus_ReturnsPending()
    {
        var logger = new ListLogger<WatchlistStateService>();
        _ = new WatchlistStateService(_repository, logger);
        var unknown = "unknown_token_" + Guid.NewGuid().ToString("N");

        Assert.Equal(WatchlistPlaylistState.Pending, WatchlistStateService.Parse("stale_recovered"));
        Assert.Equal(WatchlistPlaylistState.Pending, WatchlistStateService.Parse(unknown));
        Assert.Equal(WatchlistPlaylistState.Pending, WatchlistStateService.Parse(unknown));
        Assert.Equal(WatchlistPlaylistState.WaitingForTargetSync, WatchlistStateService.Parse("waiting_for_target_sync"));
        Assert.Equal(WatchlistPlaylistState.Pending, WatchlistStateService.Parse("pending"));
        Assert.Equal(1, logger.Entries.Count(entry =>
            entry.Level == LogLevel.Warning
            && entry.Message.Contains(unknown, StringComparison.Ordinal)));
    }

    private static async Task InvokeRunOnceAsync(WatchlistRunCoordinator hosted)
    {
        var method = typeof(WatchlistRunCoordinator).GetMethod("RunOnceAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var result = method!.Invoke(hosted, new object[] { CancellationToken.None });
        Assert.NotNull(result);
        await (Task)result!;
    }

    private static DownloadQueueItem CreateQueueItem(string queueUuid, string status, string payloadJson)
        => new(
            Id: 0,
            QueueUuid: queueUuid,
            Engine: "qobuz",
            ArtistName: "Artist",
            TrackTitle: queueUuid,
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
            DestinationFolderId: 1,
            QualityRank: null,
            QueueOrder: null,
            ContentType: "stereo",
            Status: status,
            PayloadJson: payloadJson,
            Progress: 0,
            Downloaded: 0,
            Failed: 0,
            Error: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

    private static ConcurrentDictionary<string, int> GetFailureMap(WatchlistRunCoordinator hosted)
        => (ConcurrentDictionary<string, int>)GetPrivateField(hosted, "_consecutiveFailures");

    private static ConcurrentDictionary<string, DateTimeOffset> GetNextAllowedMap(WatchlistRunCoordinator hosted)
        => (ConcurrentDictionary<string, DateTimeOffset>)GetPrivateField(hosted, "_nextAllowedRun");

    private static ConcurrentDictionary<string, DateTimeOffset> GetLastRunMap(WatchlistRunCoordinator hosted)
        => (ConcurrentDictionary<string, DateTimeOffset>)GetPrivateField(hosted, "_lastRun");

    private static object GetPrivateField(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field!.GetValue(instance)!;
    }

    private static T GetPrivateField<T>(object instance, string fieldName)
        => (T)GetPrivateField(instance, fieldName);

    private static void SetPrivateField(object instance, string fieldName, object value)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(instance, value);
    }

    private static async Task<(TimeSpan Delay, WatchlistWakeReason Reason)> InvokeGetNextWakeAsync(WatchlistRunCoordinator hosted)
    {
        var method = typeof(WatchlistRunCoordinator).GetMethod(
            "GetNextWakeAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var result = method!.Invoke(hosted, new object[] { CancellationToken.None });
        Assert.NotNull(result);
        return await (Task<(TimeSpan Delay, WatchlistWakeReason Reason)>)result!;
    }

    private static async Task InvokeRecoverCoordinatorStateAsync(WatchlistRunCoordinator hosted)
    {
        var method = typeof(WatchlistRunCoordinator).GetMethod(
            "RecoverCoordinatorStateAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var result = method!.Invoke(hosted, new object[] { CancellationToken.None });
        Assert.NotNull(result);
        await (Task)result!;
    }

    private async Task ConfigurePlaylistDestinationAsync(string source, string sourceId)
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

    private AutoTagProfileResolutionService CreateProfileResolutionService()
    {
        var environment = new StubWebHostEnvironment(_tempRoot);
        return new AutoTagProfileResolutionService(
            new TaggingProfileService(environment, NullLogger<TaggingProfileService>.Instance),
            new AutoTagDefaultsStore(environment, NullLogger<AutoTagDefaultsStore>.Instance),
            _repository,
            NullLogger<AutoTagProfileResolutionService>.Instance);
    }

    private WatchlistApiController CreatePlaylistWatchlistController(WatchlistEngine playlistWatchService)
        => new(new LibraryPlaylistWatchlistDependencies
        {
            Repository = _repository,
            ConfigStore = _configStore,
            PlaylistWatchReconciler = new PlaylistWatchReconciler(playlistWatchService),
            PlaylistSyncService = null!,
            PlaylistVisualService = _playlistVisualService,
            QueueRepository = null!,
            ProfileResolutionService = CreateProfileResolutionService(),
            BoomplayMetadataService = null!
        });

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public StubHostEnvironment(string rootPath)
        {
            ContentRootPath = rootPath;
            ContentRootFileProvider = new PhysicalFileProvider(rootPath);
        }

        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "DeezSpoTag.Tests";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
    }

    private sealed class StubWebHostEnvironment : IWebHostEnvironment
    {
        public StubWebHostEnvironment(string rootPath)
        {
            ContentRootPath = rootPath;
            ContentRootFileProvider = new PhysicalFileProvider(rootPath);
            WebRootPath = rootPath;
            WebRootFileProvider = new PhysicalFileProvider(rootPath);
        }

        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "DeezSpoTag.Tests";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
        public string WebRootPath { get; set; }
        public IFileProvider WebRootFileProvider { get; set; }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class StubTidalAccessTokenProvider : ITidalAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
            => Task.FromResult("test-token");

        public Task<string> GetCountryCodeAsync(CancellationToken cancellationToken)
            => Task.FromResult("US");

        public Task<bool> HasAuthenticatedSessionAsync(CancellationToken cancellationToken)
            => Task.FromResult(false);

        public Task<bool> ValidateCredentialsAsync(CancellationToken cancellationToken)
            => Task.FromResult(true);

        public void Invalidate()
        {
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
