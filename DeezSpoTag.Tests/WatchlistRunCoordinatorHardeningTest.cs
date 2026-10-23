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
using DeezSpoTag.Services.Runtime;
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
public sealed class WatchlistRunCoordinatorHardeningTest : IAsyncLifetime
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
    private readonly ListLogger<WatchlistEngine> _engineLogger = new();
    private readonly StubHttpClientFactory _snapshotHttp = new();
    internal ServiceProvider MeasurementTestServices => _provider;
    internal void SetHeadResponse(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response)
        => _snapshotHttp.AsyncResponse = response;

    [Fact]
    public async Task HundredPlaylistsWithThreeThousandCandidatesUseBoundedDurableHandoff()
    {
        var settings = _settingsService.LoadSettings();
        settings.PlaylistSnapshotTrackLimit = 3000;
        settings.WatchDelayBetweenPlaylistsSeconds = 0;
        _settingsService.SaveSettings(settings);
        var playlists = new List<PlaylistWatchlistDto>();
        var generations = new Dictionary<string, string>();
        for (var n = 0; n < 100; n++)
        {
            var id = $"12345678-1234-1234-1234-{n:000000000000}";
            playlists.Add((await _repository.AddPlaylistWatchlistAsync("tidal", id, new PlaylistWatchlistMetadataInput("Synthetic", null, null, n == 0 ? 3000 : 0)))!);
            await _repository.EnqueueWatchlistReconciliationRequestAsync("playlist", "tidal", id);
        }
        foreach (var request in await _repository.GetWatchlistReconciliationRequestsAsync(includePreparedSnapshotJson: false))
            generations[request.Identifier] = request.Generation;
        var requests = 0;
        var expansions = new ConcurrentQueue<string>();
        var active = 0;
        var peak = 0;
        _snapshotHttp.AsyncResponse = async (request, token) =>
        {
            Interlocked.Increment(ref requests);
            var count = Interlocked.Increment(ref active);
            int before;
            do { before = Volatile.Read(ref peak); if (before >= count) break; }
            while (Interlocked.CompareExchange(ref peak, count, before) != before);
            try
            {
                await Task.Yield();
                token.ThrowIfCancellationRequested();
                var first = request.RequestUri!.AbsolutePath.Contains(playlists[0].SourceId);
                var total = first ? 3000 : 0;
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
                var offset = query.ContainsKey("offset") ? int.Parse(query["offset"].ToString()) : 0;
                if (request.RequestUri.AbsolutePath.EndsWith("/items") && offset == 0)
                    expansions.Enqueue(request.RequestUri.AbsolutePath.Split('/')[3]);
                var items = Enumerable.Range(offset + 1, Math.Min(100, total - offset)).Select(id => new
                { item = new { id, title = "Synthetic " + id, duration = 180, artist = new { name = "Fixture" }, album = new { title = "Album" } } });
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { Content = new StringContent(request.RequestUri.AbsolutePath.EndsWith("/items")
                    ? System.Text.Json.JsonSerializer.Serialize(new { totalNumberOfItems = total, items })
                    : System.Text.Json.JsonSerializer.Serialize(new { title = "Synthetic", numberOfTracks = total })) };
            }
            finally { Interlocked.Decrement(ref active); }
        };
        var published = new List<string>();
        var coordinator = new PlaylistPlatformSnapshotCoordinator(_provider, _settingsService, NullLogger<PlaylistPlatformSnapshotCoordinator>.Instance);
        await coordinator.PrepareSnapshotsAsync(playlists, (_, _, _) => Task.FromResult(false), _ => { },
            async (playlist, snapshot, failure, token) =>
            {
                Assert.Null(failure);
                Assert.NotNull(snapshot);
                Assert.True(await _repository.PublishPreparedWatchlistPlaylistAsync("tidal", playlist.SourceId,
                    generations[playlist.SourceId], System.Text.Json.JsonSerializer.Serialize(snapshot), token));
                lock (published) published.Add(playlist.SourceId);
            }, CancellationToken.None);
        Assert.Equal(100, published.Count);
        Assert.Equal(playlists.Select(item => item.SourceId), expansions);
        Assert.InRange(peak, 1, 3);
        Assert.Equal(0, active);
        Assert.Equal(229, requests); // 100 heads, 30 pages for the large playlist, 99 empty pages
        var lean = await _repository.GetWatchlistReconciliationRequestsAsync(includePreparedSnapshotJson: false);
        Assert.Equal(100, lean.Count);
        Assert.All(lean, item => { Assert.Equal("reconcile", item.Stage); Assert.Null(item.PreparedSnapshotJson); });
        var claim = Assert.Single(await _repository.ClaimDueWatchlistReconciliationRequestsAsync(1, TimeSpan.FromMinutes(1), "load-one",
            stage: "reconcile", playlistKey: "tidal:" + playlists[0].SourceId, includePreparedSnapshotJson: false));
        var json = await _repository.GetOwnedPreparedWatchlistSnapshotAsync("tidal", claim.Identifier, claim.Generation, "load-one");
        var prepared = System.Text.Json.JsonSerializer.Deserialize<PreparedPlaylistSnapshot>(json!)!;
        Assert.Equal(Enumerable.Range(1, 3000).Select(id => id.ToString()), prepared.Candidates.Select(item => item.TrackSourceId));
        Assert.Equal(3000, prepared.SnapshotTrackLimit);
        Assert.False(prepared.IsComplete); // Preserve the existing ceiling behavior at exactly the limit.
        Assert.Null(await _repository.GetOwnedPreparedWatchlistSnapshotAsync("tidal", claim.Identifier, generations[claim.Identifier], "load-one"));
        Assert.Empty(await _queueRepository.GetTasksAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task EarlierPriorityPlaylistsReachQuotaWhileLowerPrioritySourceWorkIsHeld(bool holdExpansion)
        => RunAdmissionScenarioAsync(holdExpansion);

    [Fact]
    public Task UnchangedSnapshotRevisitsKnownMissingWithoutFullExpansion()
        => RunAdmissionScenarioAsync(false, cached: true);

    [Fact]
    public Task SharedIdentityAcrossPlaylistsCreatesOneAttempt()
        => RunAdmissionScenarioAsync(false, shared: true);

    [Fact]
    public Task FinalAdmissionStillQueuesBelowQuotaRemainder()
        => RunAdmissionScenarioAsync(false, belowQuota: true);

    private async Task RunAdmissionScenarioAsync(bool holdExpansion, bool cached = false, bool shared = false, bool belowQuota = false)
    {
        EnableRealQueueAdmissionFixture();
        const string firstId = "12345678-1234-1234-1234-123456789ab1";
        const string secondId = "12345678-1234-1234-1234-123456789ab2";
        const string thirdId = "12345678-1234-1234-1234-123456789ab3";
        var settings = _settingsService.LoadSettings();
        settings.WatchMaxItemsPerRun = belowQuota ? 3 : 2;
        settings.WatchDelayBetweenPlaylistsSeconds = 0;
        settings.Service = "deezer";
        _settingsService.SaveSettings(settings);
        foreach (var item in new[] { ("spotify", firstId), ("deezer", secondId), ("spotify", thirdId) })
        {
            await _repository.AddPlaylistWatchlistAsync(item.Item1, item.Item2, new PlaylistWatchlistMetadataInput(item.Item2, null, null, 1));
            await ConfigurePlaylistDestinationAsync(item.Item1, item.Item2);
        }
        await _repository.UpdatePlaylistWatchlistPrioritiesAsync(new[] { ("spotify", firstId, 1), ("deezer", secondId, 2), ("spotify", thirdId, 3) });
        if (cached)
        {
            var engine = _provider.GetRequiredService<WatchlistEngine>();
            var buildRevision = typeof(WatchlistEngine).GetMethod("BuildProviderReadinessRevisionAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var revision = await (Task<string>)buildRevision.Invoke(engine, new object[] { "spotify" })!;
            var candidates = new[] { new PlaylistTrackCandidate("0000000000000000000001", "USAAA2600001", "Synthetic 1", "Fixture Artist", "Album 1", null, 180000, null, Array.Empty<string>()) };
            var identity = PlaylistCandidateContract.BuildIdentityRevision("spotify", candidates);
            await _repository.UpsertPlaylistTrackCandidateCacheAsync("spotify", firstId, "one", System.Text.Json.JsonSerializer.Serialize(candidates),
                PlaylistCandidateContract.CurrentCacheSchemaVersion, identity, revision, true, sourceItemsObserved: 1);
            await _repository.UpsertPlaylistWatchMissingTracksAsync("spotify", firstId,
                new[] { new PlaylistWatchMissingTrackUpsert(candidates[0].TrackSourceId, candidates[0].Isrc, 1, "Synthetic 1", "Fixture Artist", "Album 1", 180000, null, null, null, "one", identity, revision) });
        }
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        _snapshotHttp.AsyncResponse = async (request, token) =>
        {
            Interlocked.Increment(ref calls);
            var path = request.RequestUri!.AbsolutePath;
            var isItems = path.EndsWith("/items", StringComparison.Ordinal);
            if (path.Contains(secondId) && !isItems)
            { secondEntered.TrySetResult(); await releaseSecond.Task.WaitAsync(token); }
            if (path.Contains(thirdId) && isItems == holdExpansion)
            { heldEntered.TrySetResult(); await releaseHeld.Task.WaitAsync(token); }
            var number = path.Contains(firstId) ? 1 : path.Contains(secondId) ? 2 : 3;
            if (cached && number == 1 && isItems) throw new InvalidOperationException("Unchanged known-missing snapshot must reuse its applied cache.");
            var id = number == 2 ? "2002" : number == 1 ? "0000000000000000000001" : "0000000000000000000003";
            var isrc = "USAAA260000" + (shared && number == 2 ? 1 : number);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent(isItems
                ? System.Text.Json.JsonSerializer.Serialize(new { totalNumberOfItems = number == 3 ? 0 : 1, items = number == 3 ? Array.Empty<object>() : new object[] { new { item = new { id, title = "Synthetic " + number,
                    isrc, duration = 180, artist = new { name = "Fixture Artist" }, album = new { title = "Album " + number } } } } })
                : "{\"title\":\"Synthetic\",\"numberOfTracks\":1,\"lastUpdated\":\"one\"}") };
        };
        var logger = new ListLogger<WatchlistRunCoordinator>();
        var hosted = new WatchlistRunCoordinator(_provider, logger);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var run = InvokeRunOnceAsync(hosted);
        try
        {
            await secondEntered.Task.WaitAsync(cts.Token);
            while ((await _repository.GetPlaylistWatchTrackStatusesAsync("spotify", firstId)).Count == 0)
            {
                if (run.IsCompleted) { await run; Assert.Fail(string.Join("\n", _engineLogger.Entries.Select(entry => entry.Message))); }
                await Task.Delay(10, cts.Token);
            }
            Assert.Empty(await _queueRepository.GetTasksAsync()); // one eligible identity cannot fill quota two
            releaseSecond.TrySetResult();
            await heldEntered.Task.WaitAsync(cts.Token);
            if (shared || belowQuota)
            {
                while (hosted.GetRuntimeHealth().Platforms?.FirstOrDefault(platform => platform.Source == "deezer")?.Settled != 1)
                    await Task.Delay(10, cts.Token);
                Assert.Empty(await _queueRepository.GetTasksAsync());
                releaseHeld.TrySetResult();
                await run.WaitAsync(cts.Token);
                var finalQueue = await _queueRepository.GetTasksAsync();
                Assert.Equal(shared ? 1 : 2, finalQueue.Count);
                Assert.Single(await _repository.GetPlaylistWatchTrackStatusesAsync("spotify", firstId));
                Assert.Single(await _repository.GetPlaylistWatchTrackStatusesAsync("deezer", secondId));
                return;
            }
            while ((await _queueRepository.GetTasksAsync()).Count < 2)
            {
                if (run.IsCompleted) { await run; Assert.Fail(string.Join("\n", _engineLogger.Entries.Select(entry => entry.Message))); }
                await Task.Delay(10, cts.Token);
            }
            var queue = (await _queueRepository.GetTasksAsync()).OrderBy(item => item.Id).ToList();
            Assert.Equal(new[] { "Synthetic 1", "Synthetic 2" }, queue.Select(item => item.TrackTitle));
            Assert.All(queue, item => Assert.Equal("queued", item.Status));
            Assert.False(run.IsCompleted);
            var requests = await _repository.GetWatchlistReconciliationRequestsAsync(includePreparedSnapshotJson: false);
            Assert.All(requests, request => Assert.Null(request.PreparedSnapshotJson));
            Console.WriteLine($"Early real admission: heldExpansion={holdExpansion}, callsBeforeRelease={calls}, queued={queue.Count}");
        }
        catch (Exception error)
        {
            var due = await _repository.GetDuePlaylistWatchMissingTracksInPriorityOrderAsync();
            throw new Xunit.Sdk.XunitException(error + "\nDue ledger rows: " + System.Text.Json.JsonSerializer.Serialize(due)
                + "\nEngine logs: " + string.Join("\n", _engineLogger.Entries.Select(entry => entry.Message))
                + "\nCoordinator logs: " + string.Join("\n", logger.Entries.Select(entry => entry.Message))
                + "\nRuntime: " + System.Text.Json.JsonSerializer.Serialize(hosted.GetRuntimeHealth())
                + "\nRequests: " + System.Text.Json.JsonSerializer.Serialize(await _repository.GetWatchlistReconciliationRequestsAsync(includePreparedSnapshotJson: false))
                + "\nOrder: " + System.Text.Json.JsonSerializer.Serialize((await _repository.GetPlaylistWatchlistAsync()).Select(item => new { item.Source, item.SourceId, item.SyncPriority })));

        }
        finally
        {
            releaseSecond.TrySetResult();
            releaseHeld.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(20));
            var summary = logger.Entries.LastOrDefault(entry => entry.Fields?.GetValueOrDefault("source") as string == "run")?.Fields;
            Assert.NotNull(summary);
            var firstPrepared = Assert.IsType<long>(summary!["first_prepared_persisted_ms"]);
            var firstMissing = Assert.IsType<long>(summary["first_missing_persisted_ms"]);
            var firstQueue = Assert.IsType<long>(summary["first_successful_enqueue_ms"]);
            Assert.InRange(firstPrepared, 0, firstMissing);
            Assert.True(firstMissing <= firstQueue);
            File.AppendAllText(Path.Join(Path.GetTempPath(), "watchlist-admission-throughput-observations.jsonl"),
                System.Text.Json.JsonSerializer.Serialize(new { holdExpansion, cached, shared, belowQuota, calls,
                    queued = (await _queueRepository.GetTasksAsync()).Count, metrics = summary }) + Environment.NewLine);
        }
    }

    private void EnableRealQueueAdmissionFixture()
    {
        // Reuse real queue/identity/destination/admission code. Only source HTTP and idle runtime
        // collaborators are synthetic; no download worker or provider network service is started.
        var engine = _provider.GetRequiredService<WatchlistEngine>();
        SetPrivateField(engine, "_deezerClient", new DeezSpoTag.Integrations.Deezer.DeezerClient(
            NullLogger<DeezSpoTag.Integrations.Deezer.DeezerClient>.Instance, null!));
        var reconciler = _provider.GetRequiredService<PlaylistWatchReconciler>();
        var queueAdmission = _provider.GetRequiredService<WatchlistQueueAdmissionService>();
        var artist = _provider.GetRequiredService<ArtistWatchService>();
        var profiles = _provider.GetRequiredService<AutoTagProfileResolutionService>();
        var autoTag = (AutoTagService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(AutoTagService));
        SetPrivateField(autoTag, "_activeJobIds", new ConcurrentDictionary<string, byte>());
        SetPrivateField(autoTag, "_activeJobStages", new ConcurrentDictionary<string, string>());
        var orchestration = (DownloadOrchestrationService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(DownloadOrchestrationService));
        SetPrivateField(orchestration, "_autoTagService", autoTag);
        SetPrivateField(orchestration, "_phaseLock", new object());
        SetPrivateField(orchestration, "_enhancementResumeLock", new object());
        SetPrivateField(orchestration, "_pendingEnhancementResumeFolderIds", new HashSet<string>());
        SetPrivateField(orchestration, "_pendingEnhancementResumeRootPaths", new HashSet<string>());
        SetPrivateField(orchestration, "_wakeSignal", new SemaphoreSlim(0, 1));
        var intent = (DownloadIntentService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(DownloadIntentService));
        var dedupe = new DeezSpoTag.Services.Download.DownloadDedupeService(_queueRepository, _repository,
            NullLogger<DeezSpoTag.Services.Download.DownloadDedupeService>.Instance, new PassthroughLocalTrackAmbiguityResolver());
        SetPrivateField(intent, "_queueRepository", _queueRepository);
        SetPrivateField(intent, "_settingsService", _settingsService);
        SetPrivateField(intent, "_libraryRepository", _repository);
        SetPrivateField(intent, "_orchestrationService", orchestration);
        SetPrivateField(intent, "_downloadTagSettingsResolver", new QueueFixtureTagSettings());
        SetPrivateField(intent, "_activityLog", new DeezSpoTag.Services.Download.NullActivityLogWriter());
        SetPrivateField(intent, "_deezspotagListener", new DeezSpoTag.Services.Download.Shared.Models.DeezSpoTagListener());
        SetPrivateField(intent, "_logger", NullLogger<DownloadIntentService>.Instance);
        SetPrivateField(intent, "_dedupeService", dedupe);
        SetPrivateField(intent, "_apiHealthTracker", new DeezSpoTag.Services.Download.DownloadApiHealthTracker());
        var services = new ServiceCollection();
        services.AddSingleton(_settingsService); services.AddSingleton(_repository); services.AddSingleton(_queueRepository);
        services.AddSingleton(engine); services.AddSingleton(reconciler); services.AddSingleton(queueAdmission);
        services.AddSingleton(artist); services.AddSingleton(profiles); services.AddSingleton(intent); services.AddSingleton(dedupe);
        services.AddSingleton(new DeezSpoTag.Services.Download.Utils.EnhancedPathTemplateProcessor(
            NullLogger<DeezSpoTag.Services.Download.Utils.EnhancedPathTemplateProcessor>.Instance));
        services.AddSingleton((PlaylistSyncService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PlaylistSyncService)));
        services.AddSingleton<PlaylistPlatformSnapshotCoordinator>(provider => new(provider, _settingsService, NullLogger<PlaylistPlatformSnapshotCoordinator>.Instance));
        _provider.Dispose();
        _provider = services.BuildServiceProvider();
        SetPrivateField(engine, "_serviceProvider", _provider);
        SetPrivateField(intent, "_serviceProvider", _provider);
        // Use the existing adapter implementation with fake HTTP for both seed providers, keeping
        // each source's real candidate contracts, identity, missing ledger and queue policy.
        var adapters = (System.Collections.IDictionary)GetPrivateField(engine, "_sourceAdapters");
        var tidal = adapters["tidal"]!;
        foreach (var source in new[] { "spotify", "deezer" })
            adapters[source] = Activator.CreateInstance(tidal.GetType(), source,
                GetPrivateField(tidal, "_fetch"), GetPrivateField(tidal, "_fetchHead"));
    }

    private sealed class QueueFixtureTagSettings : DeezSpoTag.Services.Download.Shared.IDownloadTagSettingsResolver
    {
        public Task<DeezSpoTag.Core.Models.Settings.TagSettings?> ResolveAsync(long? id, CancellationToken token)
            => Task.FromResult<DeezSpoTag.Core.Models.Settings.TagSettings?>(new());
        public Task<DeezSpoTag.Services.Download.Shared.DownloadTagProfileSettings?> ResolveProfileAsync(long? id, CancellationToken token)
            => Task.FromResult<DeezSpoTag.Services.Download.Shared.DownloadTagProfileSettings?>(new(new(), "deezer", null, null));
    }

    [Fact]
    public async Task FirstOccurrenceMeasurementsAreMonotonicAndThreadSafe()
    {
        var logger = new ListLogger<WatchlistEngine>();
        using (var scope = WatchlistPerformanceMeasurement.Start(logger, "run", "measurement"))
        {
            var record = typeof(WatchlistPerformanceMeasurement).GetMethod("RecordFirst");
            Assert.NotNull(record);
            Parallel.For(0, 20, _ => record!.Invoke(scope, new object[] { "first_prepared" }));
            var values = GetPrivateField<ConcurrentDictionary<string, long>>(scope, "_values");
            var first = values["first_prepared_ms"];
            await Task.Delay(10);
            record!.Invoke(scope, new object[] { "first_prepared" });
            Assert.Equal(first, values["first_prepared_ms"]);
            record.Invoke(scope, new object[] { "first_successful_enqueue" });
            Assert.True(values["first_successful_enqueue_ms"] >= first);
        }
        Assert.Single(logger.Entries);
    }

    [Fact]
    public async Task DirectReconciliation_InvalidatesAnOlderInFlightPreparation()
    {
        var playlist = await _repository.AddPlaylistWatchlistAsync("unsupported", "manual-newer", new PlaylistWatchlistMetadataInput("Manual", null, null, 0));
        await _repository.EnqueueWatchlistReconciliationRequestAsync("playlist", "unsupported", "manual-newer");
        var older = Assert.Single(await _repository.GetWatchlistReconciliationRequestsAsync());
        await _provider.GetRequiredService<PlaylistWatchReconciler>().ReconcilePlaylistAsync(playlist!, CancellationToken.None);
        var current = Assert.Single(await _repository.GetWatchlistReconciliationRequestsAsync());
        Assert.NotEqual(older.Generation, current.Generation);
        Assert.Equal("fetch", current.Stage);
        Assert.False(await _repository.PublishPreparedWatchlistPlaylistAsync("unsupported", "manual-newer", older.Generation, "obsolete"));
    }

    [Fact]
    public async Task ObsoletePreparationFailure_DoesNotBackOffANewerExplicitRequest()
    {
        const string id = "12345678-1234-1234-1234-123456789abc";
        await _repository.AddPlaylistWatchlistAsync("tidal", id, new PlaylistWatchlistMetadataInput("Refreshed", null, null, 0));
        await ConfigurePlaylistDestinationAsync("tidal", id);
        await _repository.EnqueueWatchlistReconciliationRequestAsync("playlist", "tidal", id);
        string? refreshedGeneration = null;
        _snapshotHttp.AsyncResponse = async (_, _) =>
        {
            await _repository.EnqueueWatchlistReconciliationRequestAsync("playlist", "tidal", id);
            refreshedGeneration = Assert.Single(await _repository.GetWatchlistReconciliationRequestsAsync()).Generation;
            throw new HttpRequestException("obsolete preparation failure");
        };
        await InvokeRunOnceAsync(new WatchlistRunCoordinator(_provider, NullLogger<WatchlistRunCoordinator>.Instance));
        var current = Assert.Single(await _repository.GetWatchlistReconciliationRequestsAsync());
        Assert.Equal(refreshedGeneration, current.Generation);
        Assert.Equal("pending", current.Status);
        Assert.Null(current.LastError);
        Assert.Equal("fetch", current.Stage);
    }

    [Fact]
    public async Task RestartedConsumer_UsesDurableSnapshotWithoutReadingTheSourceAgain()
    {
        const string id = "12345678-1234-1234-1234-123456789abc";
        var playlist = await _repository.AddPlaylistWatchlistAsync("tidal", id, new PlaylistWatchlistMetadataInput("Restored", null, null, 0));
        await ConfigurePlaylistDestinationAsync("tidal", id);
        _snapshotHttp.Response = _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        { Content = new StringContent("{\"items\":[],\"totalNumberOfItems\":0,\"numberOfTracks\":0,\"title\":\"Restored\"}") };
        var prepared = await _provider.GetRequiredService<PlaylistWatchReconciler>().PreparePlaylistSnapshotAsync(playlist!,
            new PlaylistHeadSnapshot("tidal", id, Name: "Restored", TrackCount: 0), CancellationToken.None);
        Assert.True(prepared.IsComplete);
        // Supply explicitly authoritative empty evidence; an ordinary empty Tidal page alone is insufficient.
        prepared = prepared with { IsAuthoritativeEmpty = true, SourceItemsObserved = 0 };
        await _repository.EnqueueWatchlistReconciliationRequestAsync("playlist", "tidal", id);
        var request = Assert.Single(await _repository.GetWatchlistReconciliationRequestsAsync());
        Assert.True(await _repository.PublishPreparedWatchlistPlaylistAsync("tidal", id, request.Generation,
            System.Text.Json.JsonSerializer.Serialize(prepared)));
        var calls = 0;
        _snapshotHttp.Response = _ => { Interlocked.Increment(ref calls); throw new InvalidOperationException("Unexpected source read after restart"); };
        await InvokeRunOnceAsync(new WatchlistRunCoordinator(_provider, NullLogger<WatchlistRunCoordinator>.Instance));
        Assert.Equal(0, calls);
        Assert.Equal("completed", (await _repository.GetPlaylistWatchStateAsync("tidal", id))?.LastRunStatus);
        Assert.Empty(await _repository.GetWatchlistReconciliationRequestsAsync());
    }

    [Fact]
    public async Task PlatformPreparation_ContinuesWhileEarlierReconciliationWaitsAndResumesPersistedWork()
    {
        var first = await _repository.AddPlaylistWatchlistAsync("unsupported", "prepare-first", new PlaylistWatchlistMetadataInput("First", null, null, 0));
        await _repository.AddPlaylistWatchlistAsync("unsupported", "prepare-second", new PlaylistWatchlistMetadataInput("Second", null, null, 0));
        await ConfigurePlaylistDestinationAsync("unsupported", "prepare-first");
        await ConfigurePlaylistDestinationAsync("unsupported", "prepare-second");
        var ordered = await _repository.GetPlaylistWatchlistAsync();
        var firstId = ordered[0].SourceId;
        var laterId = ordered[1].SourceId;
        var reconciler = _provider.GetRequiredService<PlaylistWatchReconciler>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var held = reconciler.WithPlaylistExecutionAsync("unsupported", firstId, async () =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellation.Token);
            return true;
        }, cancellation.Token);
        await entered.Task.WaitAsync(cancellation.Token);
        var hosted = new WatchlistRunCoordinator(_provider, NullLogger<WatchlistRunCoordinator>.Instance);
        var run = InvokeRunOnceAsync(hosted);
        try
        {
            while (!(await _repository.GetWatchlistReconciliationRequestsAsync(cancellation.Token))
                .Any(item => item.Identifier == laterId && item.Stage == "reconcile"))
                await Task.Delay(20, cancellation.Token);
            Assert.False(run.IsCompleted);
            Assert.Null(await _repository.GetPlaylistTrackCandidateCacheAsync("unsupported", laterId));
        }
        finally
        {
            release.TrySetResult();
            await held;
            await run.WaitAsync(cancellation.Token);
        }
        Assert.NotNull((await _repository.GetPlaylistWatchStateAsync("unsupported", laterId))?.LastCheckedUtc);
    }

    [Fact]
    public async Task Preparation_IsReadOnlyAndPreparedConsumptionDoesNotFetchAgain()
    {
        const string id = "12345678-1234-1234-1234-123456789abc";
        var playlist = await _repository.AddPlaylistWatchlistAsync("tidal", id, new PlaylistWatchlistMetadataInput("Prepared", null, null, 0));
        _snapshotHttp.Response = _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        { Content = new StringContent("{\"items\":[],\"totalNumberOfItems\":0,\"numberOfTracks\":0,\"title\":\"Prepared\"}") };
        var reconciler = _provider.GetRequiredService<PlaylistWatchReconciler>();
        var prepared = await reconciler.PreparePlaylistSnapshotAsync(playlist!,
            new PlaylistHeadSnapshot("tidal", id, Name: "Prepared", TrackCount: 0), CancellationToken.None);
        Assert.Equal("Prepared", prepared.Name);
        Assert.Null(await _repository.GetPlaylistTrackCandidateCacheAsync("tidal", id));
        Assert.Empty(await _repository.GetPlaylistWatchTrackStatusesAsync("tidal", id));
        Assert.Empty(await _queueRepository.GetTasksAsync());
        _snapshotHttp.Response = _ => throw new InvalidOperationException("Prepared consumption performed a second source request");
        await reconciler.ReconcilePlaylistAsync(playlist!, CancellationToken.None, preparedSnapshot: prepared);
    }

    [Fact]
    public async Task PerformanceScopes_IsolateConcurrentOperationsAndRestoreNestedScope()
    {
        var logger = new ListLogger<WatchlistEngine>();
        var firstReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Run(string source, long count, TaskCompletionSource ready, Task wait)
        {
            using var measurement = WatchlistPerformanceMeasurement.Start(logger, source, "synthetic");
            ready.SetResult();
            await wait;
            Assert.Same(measurement, WatchlistPerformanceMeasurement.Current);
            measurement.Increment("tracks_examined", count);
            using (var nested = WatchlistPerformanceMeasurement.Start(logger, source, "nested"))
                Assert.Same(nested, WatchlistPerformanceMeasurement.Current);
            Assert.Same(measurement, WatchlistPerformanceMeasurement.Current);
            using (measurement.Measure("cache_lookup")) { }
            measurement.SetOutcome("completed");
        }
        await Task.WhenAll(
            Run("spotify", 100, firstReady, secondReady.Task),
            Run("deezer", 3000, secondReady, firstReady.Task));
        Assert.Null(WatchlistPerformanceMeasurement.Current);
        var summaries = logger.Entries.Where(entry => entry.Fields?.ContainsKey("tracks_examined") == true).ToList();
        Assert.Equal(2, summaries.Count);
        Assert.Contains(summaries, entry => Equals(entry.Fields!["tracks_examined"], 100L));
        Assert.Contains(summaries, entry => Equals(entry.Fields!["tracks_examined"], 3000L));
        Assert.All(summaries, entry => Assert.True(Convert.ToInt64(entry.Fields!["cache_lookup_ms"]) >= 0));
        Assert.All(summaries, entry => Assert.False(entry.Fields!.ContainsKey("snapshot_expand_ms")));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public void PerformanceScope_EmitsOneTerminalSummaryAndPreservesOriginalFailure(string outcome)
    {
        var logger = new ListLogger<WatchlistEngine>();
        var measurement = WatchlistPerformanceMeasurement.Start(logger, "spotify", "test");
        var original = new OperationCanceledException("original");
        var caught = Assert.Throws<OperationCanceledException>((Action)(() =>
        {
            using (measurement)
            {
                measurement.SetOutcome(outcome);
                throw original;
            }
        }));
        measurement.Dispose();
        Assert.Same(original, caught);
        Assert.Single(logger.Entries);
        Assert.Equal(outcome, logger.Entries[0].Fields!["outcome"]);
        Assert.Null(WatchlistPerformanceMeasurement.Current);
    }

    [Fact]
    public async Task ReconciliationMeasurement_ReportsEarlyRejectedPlaylistOnce()
    {
        var result = await _provider.GetRequiredService<WatchlistEngine>().ReconcilePlaylistAsync(null!, CancellationToken.None);
        Assert.False(result.Success);
        var summary = Assert.Single(_engineLogger.Entries.Where(entry => entry.Fields?.ContainsKey("outcome") == true));
        Assert.Equal("failed", summary.Fields!["outcome"]);
        Assert.Equal(0L, summary.Fields["queue_deferred_tracks"]);
        Assert.Equal(0L, summary.Fields["attempted_tracks"]);
        Assert.False(summary.Fields.ContainsKey("snapshot_expand_ms"));
    }

    [Theory]
    [InlineData(100, 100)]
    [InlineData(100, 3000)]
    [InlineData(1000, 3000)]
    [InlineData(1000, 1000)]
    [InlineData(3000, 3000)]
    [InlineData(3100, 3000)]
    public async Task SyntheticTidalExpansion_ReportsRealPagesAndPreservesOrderAndLimit(int total, int limit)
    {
        var offsets = new List<int>();
        _snapshotHttp.Response = request =>
        {
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri!.Query);
            Assert.Equal("100", query["limit"]);
            var offset = int.Parse(query["offset"].ToString());
            offsets.Add(offset);
            var items = Enumerable.Range(offset + 1, Math.Min(100, total - offset)).Select(id => new
            {
                item = new { id, title = "Synthetic " + id, artist = new { name = "Fixture Artist" }, album = new { title = "Fixture Album" }, duration = 180 }
            });
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { totalNumberOfItems = total, items })) };
        };
        var logger = new ListLogger<WatchlistEngine>();
        object snapshot;
        long processAllocationDelta;
        using (var measurement = WatchlistPerformanceMeasurement.Start(logger, "tidal", "synthetic"))
        {
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var method = typeof(WatchlistEngine).GetMethod("GetTidalSnapshotAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var task = (Task)method.Invoke(_provider.GetRequiredService<WatchlistEngine>(), new object[] { "12345678-1234-1234-1234-123456789abc", limit, CancellationToken.None })!;
            await task;
            snapshot = task.GetType().GetProperty("Result")!.GetValue(task)!;
            processAllocationDelta = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            measurement.SetOutcome("completed");
        }
        var candidates = (IReadOnlyList<PlaylistTrackCandidate>)snapshot.GetType().GetProperty("Candidates")!.GetValue(snapshot)!;
        var collected = Math.Min(total, limit);
        Assert.Equal(collected, candidates.Count);
        Assert.Equal(Enumerable.Range(1, collected).Select(id => id.ToString()), candidates.Select(candidate => candidate.TrackSourceId));
        Assert.Equal(Enumerable.Range(0, (collected + 99) / 100).Select(page => page * 100), offsets);
        var fields = Assert.Single(logger.Entries).Fields!;
        Assert.Equal((long)offsets.Count, fields["snapshot_pages_attempted"]);
        Assert.Equal((long)offsets.Count, fields["snapshot_pages_succeeded"]);
        Assert.Equal(total < limit, (bool)snapshot.GetType().GetProperty("IsComplete")!.GetValue(snapshot)!);
        Assert.Empty(await _queueRepository.GetTasksAsync());
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "watchlist-synthetic-observations.jsonl"),
            System.Text.Json.JsonSerializer.Serialize(new { provider = "tidal", total, collected, pages = offsets.Count,
                elapsedMs = fields["total_elapsed_ms"], processAllocationDeltaBytes = processAllocationDelta,
                dotnet = Environment.Version.ToString(), kind = "synthetic-fake-http" }) + Environment.NewLine);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(3000)]
    public async Task SyntheticCandidateCache_ReusesValidatedRowsAndInvalidatesChangedReadiness(int count)
    {
        var engine = _provider.GetRequiredService<WatchlistEngine>();
        var revisionMethod = typeof(WatchlistEngine).GetMethod("BuildProviderReadinessRevisionAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var revision = await (Task<string>)revisionMethod.Invoke(engine, new object[] { "spotify" })!;
        var candidates = Enumerable.Range(1, count).Select(id => new PlaylistTrackCandidate(
            id.ToString(), null, "Synthetic " + id, "Fixture Artist", "Fixture Album", null, 180000, null, Array.Empty<string>())).ToList();
        await _repository.UpsertPlaylistTrackCandidateCacheAsync("spotify", "measurement-cache", "revision-one",
            System.Text.Json.JsonSerializer.Serialize(candidates), PlaylistCandidateContract.CurrentCacheSchemaVersion,
            PlaylistCandidateContract.BuildIdentityRevision("spotify", candidates), revision, true);
        var logger = new ListLogger<WatchlistEngine>();
        using (var measurement = WatchlistPerformanceMeasurement.Start(logger, "spotify", "measurement-cache"))
        {
            var cached = await engine.GetCachedPlaylistTrackCandidatesAsync("spotify", "measurement-cache", CancellationToken.None);
            Assert.Equal(candidates.Select(candidate => candidate.TrackSourceId), cached.Select(candidate => candidate.TrackSourceId));
            measurement.SetOutcome("completed");
        }
        var fields = Assert.Single(logger.Entries).Fields!;
        Assert.True(Convert.ToInt64(fields["cache_lookup_ms"]) >= 0);
        Assert.True(Convert.ToInt64(fields["cache_deserialize_ms"]) >= 0);
        Assert.Equal(0L, fields["snapshot_pages_attempted"]);
        candidates[0] = candidates[0] with { Title = "Updated Synthetic" };
        await _repository.UpsertPlaylistTrackCandidateCacheAsync("spotify", "measurement-cache", "revision-two",
            System.Text.Json.JsonSerializer.Serialize(candidates), PlaylistCandidateContract.CurrentCacheSchemaVersion,
            PlaylistCandidateContract.BuildIdentityRevision("spotify", candidates), revision, true);
        var updated = await engine.GetCachedPlaylistTrackCandidatesAsync("spotify", "measurement-cache", CancellationToken.None);
        Assert.Equal("Updated Synthetic", updated[0].Title);
        Assert.Equal(count, updated.Count);
        await _repository.UpsertPlaylistTrackCandidateCacheAsync("spotify", "measurement-cache", "revision-three",
            System.Text.Json.JsonSerializer.Serialize(candidates), PlaylistCandidateContract.CurrentCacheSchemaVersion,
            PlaylistCandidateContract.BuildIdentityRevision("spotify", candidates), "changed-readiness", true);
        Assert.Empty(await engine.GetCachedPlaylistTrackCandidatesAsync("spotify", "measurement-cache", CancellationToken.None));
        Assert.Empty(await _queueRepository.GetTasksAsync());
    }

    [Fact]
    public void PerformanceScope_LoggerFailureCannotReplaceOperationalError()
    {
        var logger = new ListLogger<WatchlistEngine>(failLogging: true);
        var original = new InvalidOperationException("original operation failed");
        var caught = Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var measurement = WatchlistPerformanceMeasurement.Start(logger, "spotify", "test");
            measurement.SetOutcome("failed");
            throw original;
        }));
        Assert.Same(original, caught);
        Assert.Null(WatchlistPerformanceMeasurement.Current);
    }

    [Fact]
    public async Task CancelledRun_ReportsCancellationWhileWaitingForCycleGate()
    {
        var logger = new ListLogger<WatchlistRunCoordinator>();
        var coordinator = new WatchlistRunCoordinator(_provider, logger);
        var run = typeof(WatchlistRunCoordinator).GetMethod("RunOnceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var task = (Task)run.Invoke(coordinator, new object[] { cancellation.Token })!;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        var summary = Assert.Single(logger.Entries.Where(entry => entry.Fields?.ContainsKey("outcome") == true));
        Assert.Equal("cancelled", summary.Fields!["outcome"]);
        Assert.True(summary.Fields.ContainsKey("cycle_gate_wait_ms"));
    }

    [Fact]
    public async Task ReconciliationMeasurement_ReportsRealProviderFailureWithoutReplacingException()
    {
        var id = "12345678-1234-1234-1234-123456789abc";
        var playlist = await _repository.AddPlaylistWatchlistAsync("tidal", id, new PlaylistWatchlistMetadataInput("Synthetic", null, null, null));
        var original = new HttpRequestException("synthetic transport failure");
        _snapshotHttp.Response = _ => throw original;
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => _provider.GetRequiredService<WatchlistEngine>()
            .ReconcilePlaylistAsync(playlist!, CancellationToken.None, prefetchedHead: new PlaylistHeadSnapshot("tidal", id, Name: "Synthetic", TrackCount: 100)));
        Assert.Same(original, exception);
        var fields = Assert.Single(_engineLogger.Entries.Where(entry => entry.Fields?.ContainsKey("outcome") == true)).Fields!;
        Assert.Equal("failed", fields["outcome"]);
        Assert.Equal("provider_io", fields["failure_category"]);
        Assert.Equal(1L, fields["snapshot_pages_attempted"]);
        Assert.Equal(0L, fields["snapshot_pages_succeeded"]);
        Assert.True(fields.ContainsKey("snapshot_expand_ms"));
        Assert.Empty(await _queueRepository.GetTasksAsync());
    }

    [Fact]
    public async Task ReconciliationMeasurement_ReportsCancellationDuringProviderExpansion()
    {
        var id = "12345678-1234-1234-1234-123456789abc";
        var playlist = await _repository.AddPlaylistWatchlistAsync("tidal", id, new PlaylistWatchlistMetadataInput("Synthetic", null, null, null));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _snapshotHttp.AsyncResponse = async (_, token) => { entered.SetResult(); return await never.Task.WaitAsync(token); };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var task = _provider.GetRequiredService<WatchlistEngine>().ReconcilePlaylistAsync(playlist!, cancellation.Token,
            prefetchedHead: new PlaylistHeadSnapshot("tidal", id, Name: "Synthetic", TrackCount: 100));
        await entered.Task.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        var fields = Assert.Single(_engineLogger.Entries.Where(entry => entry.Fields?.ContainsKey("outcome") == true)).Fields!;
        Assert.Equal("cancelled", fields["outcome"]);
        Assert.Equal(1L, fields["snapshot_pages_attempted"]);
        Assert.Equal(0L, fields["snapshot_pages_succeeded"]);
        Assert.Empty(await _queueRepository.GetTasksAsync());
    }

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
                HttpClientFactory = _snapshotHttp,
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
            logger: _engineLogger);

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
            new PlaylistWatchReconciler(playlistWatchService),
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
    public async Task RunOnce_AccountsForEveryPlaylistDespitePersistedBackoff()
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

        await InvokeRunOnceAsync(hosted);
        var failuresAfterSecond = GetFailureMap(hosted);
        failuresAfterSecond.TryGetValue(failKey, out var secondFailures);
        Assert.True(secondFailures >= firstFailures);

        await InvokeRunOnceAsync(hosted);
        var failuresAfterThird = GetFailureMap(hosted);
        failuresAfterThird.TryGetValue(failKey, out var thirdFailures);
        Assert.True(thirdFailures >= secondFailures);
    }

    [Fact]
    public void SystemicAuthBackoffObserver_DoesNotCountAsAnotherIncident()
    {
        var observer = new PlaylistReconciliationResult(
            false,
            "auth observer",
            0,
            0,
            0,
            0,
            0,
            0,
            1,
            null,
            SystemicFailures: 1,
            FailureFingerprint: "spotify_auth_client_token_failed:incident-1",
            FailureIsIncidentOrigin: false);
        var origin = observer with { FailureIsIncidentOrigin = true };

        Assert.False(WatchlistRunCoordinator.ShouldRecordSystemicFailure(observer));
        Assert.True(WatchlistRunCoordinator.ShouldRecordSystemicFailure(origin));
    }

    [Fact]
    public async Task SourceCircuit_CountsOneFingerprintOnce_AndSeparateIncidentsIndependently()
    {
        await InvokeOpenSourceCircuitAsync("spotify", "spotify_auth_client_token_failed:incident-1");
        await InvokeOpenSourceCircuitAsync("spotify", "spotify_auth_client_token_failed:incident-1");

        var first = await _repository.GetWatchlistSourceCircuitStateAsync("playlist", "spotify");
        Assert.NotNull(first);
        Assert.Equal(1, first!.FailureCount);
        Assert.False(first.IsOpen);

        await InvokeOpenSourceCircuitAsync("spotify", "spotify_auth_client_token_failed:incident-2");

        var second = await _repository.GetWatchlistSourceCircuitStateAsync("playlist", "spotify");
        Assert.NotNull(second);
        Assert.Equal(2, second!.FailureCount);
        Assert.True(second.IsOpen);
    }

    [Fact]
    public async Task SourceCircuit_DoesNotDeduplicateUnrelatedRepeatedFailures()
    {
        await InvokeOpenSourceCircuitAsync("spotify", "spotify_rate_limited");
        await InvokeOpenSourceCircuitAsync("spotify", "spotify_rate_limited");

        var state = await _repository.GetWatchlistSourceCircuitStateAsync("playlist", "spotify");
        Assert.NotNull(state);
        Assert.Equal(2, state!.FailureCount);
        Assert.True(state.IsOpen);
    }

    [Fact]
    public async Task SpotifyAuthRecovery_ClearsOnlyAuthFailures_AndQueuesImmediateReconciliation()
    {
        await _repository.AddPlaylistWatchlistAsync(
            "spotify",
            "auth-failed",
            new PlaylistWatchlistMetadataInput("Auth failed", null, null, 10));
        await _repository.AddPlaylistWatchlistAsync(
            "spotify",
            "rate-limited",
            new PlaylistWatchlistMetadataInput("Rate limited", null, null, 20));
        var stateService = new WatchlistStateService(_repository);
        await stateService.TransitionPlaylistAsync(
            new WatchlistPlaylistStateTransition(
                "spotify",
                "auth-failed",
                WatchlistPlaylistState.CircuitOpen,
                "Playlist source failed (spotify_auth_client_token_failed); the previous valid snapshot was preserved.",
                10,
                "snapshot-auth",
                DateTimeOffset.UtcNow.AddMinutes(5),
                2,
                TouchLastChecked: false),
            CancellationToken.None);
        await stateService.TransitionPlaylistAsync(
            new WatchlistPlaylistStateTransition(
                "spotify",
                "rate-limited",
                WatchlistPlaylistState.SourceFailure,
                "Playlist source failed (spotify_rate_limited); the previous valid snapshot was preserved.",
                20,
                "snapshot-rate",
                DateTimeOffset.UtcNow.AddMinutes(5),
                1,
                TouchLastChecked: false),
            CancellationToken.None);
        await _repository.UpsertWatchlistSourceCircuitStateAsync(
            new LibraryRepository.WatchlistSourceCircuitStateUpsertInput(
                "playlist",
                "spotify",
                IsOpen: true,
                OpenUntilUtc: DateTimeOffset.UtcNow.AddMinutes(5),
                Reason: "auth failed",
                Fingerprint: "spotify_auth_client_token_failed:incident-1",
                FailureCount: 2));

        var hosted = new WatchlistRunCoordinator(_provider, NullLogger<WatchlistRunCoordinator>.Instance);
        await InvokeSpotifyAuthRecoveryAsync(
            hosted,
            new SpotifyPathfinderMetadataClient.PathfinderAuthRecovery(
                "incident-1",
                "spotify_auth_client_token_failed"));

        var recovered = await _repository.GetPlaylistWatchStateAsync("spotify", "auth-failed");
        var unrelated = await _repository.GetPlaylistWatchStateAsync("spotify", "rate-limited");
        var circuit = await _repository.GetWatchlistSourceCircuitStateAsync("playlist", "spotify");
        Assert.NotNull(recovered);
        Assert.Equal("pending", recovered!.LastRunStatus);
        Assert.Null(recovered.LastRunMessage);
        Assert.Null(recovered.NextAttemptUtc);
        Assert.Equal(0, recovered.ConsecutiveFailures);
        Assert.NotNull(unrelated);
        Assert.Equal("source_failure", unrelated!.LastRunStatus);
        Assert.Contains("spotify_rate_limited", unrelated.LastRunMessage, StringComparison.Ordinal);
        Assert.NotNull(circuit);
        Assert.False(circuit!.IsOpen);
        Assert.Equal(0, circuit.FailureCount);
        Assert.True(await _repository.GetWatchlistReconciliationRequestCountAsync() > 0);
    }

    [Fact]
    public async Task SpotifyAuthRecovery_DoesNotCreateWatchlistWorkWhileDisabled()
    {
        await _repository.AddPlaylistWatchlistAsync(
            "spotify",
            "auth-failed-disabled",
            new PlaylistWatchlistMetadataInput("Auth failed while disabled", null, null, 10));
        var stateService = new WatchlistStateService(_repository);
        await stateService.TransitionPlaylistAsync(
            new WatchlistPlaylistStateTransition(
                "spotify",
                "auth-failed-disabled",
                WatchlistPlaylistState.SourceFailure,
                "Playlist source failed (spotify_auth_client_token_failed); the previous valid snapshot was preserved.",
                10,
                "snapshot-auth-disabled",
                DateTimeOffset.UtcNow.AddMinutes(5),
                1,
                TouchLastChecked: false),
            CancellationToken.None);
        var settings = _settingsService.LoadSettings();
        settings.WatchEnabled = false;
        _settingsService.SaveSettings(settings);

        var hosted = new WatchlistRunCoordinator(_provider, NullLogger<WatchlistRunCoordinator>.Instance);
        await InvokeSpotifyAuthRecoveryAsync(
            hosted,
            new SpotifyPathfinderMetadataClient.PathfinderAuthRecovery(
                "incident-disabled",
                "spotify_auth_client_token_failed"));

        var unchanged = await _repository.GetPlaylistWatchStateAsync("spotify", "auth-failed-disabled");
        Assert.NotNull(unchanged);
        Assert.Equal("source_failure", unchanged!.LastRunStatus);
        Assert.Equal(0, await _repository.GetWatchlistReconciliationRequestCountAsync());
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
    public async Task DisableWatchlist_CancelsTheActiveCycleImmediately()
    {
        var settings = _settingsService.LoadSettings();
        settings.WatchEnabled = false;
        _settingsService.SaveSettings(settings);
        var hosted = new WatchlistRunCoordinator(_provider, NullLogger<WatchlistRunCoordinator>.Instance);
        using var activeCycle = new CancellationTokenSource();
        var activeCycleField = typeof(WatchlistRunCoordinator).GetField(
            "_activeCycleCancellation",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(activeCycleField);
        activeCycleField!.SetValue(hosted, activeCycle);

        var disableMethod = typeof(WatchlistRunCoordinator).GetMethod("DisableWatchlistAsync");
        Assert.NotNull(disableMethod);
        var disableTask = Assert.IsAssignableFrom<Task>(disableMethod!.Invoke(hosted, [CancellationToken.None]));
        await disableTask;

        Assert.True(activeCycle.IsCancellationRequested);
        Assert.False(hosted.GetRuntimeHealth().IsRunning);
    }

    [Fact]
    public async Task RunOnce_WhileDisabled_SchedulesTheNextCycleInsteadOfBusyLooping()
    {
        var settings = _settingsService.LoadSettings();
        settings.WatchEnabled = false;
        _settingsService.SaveSettings(settings);
        var hosted = new WatchlistRunCoordinator(_provider, NullLogger<WatchlistRunCoordinator>.Instance);

        await InvokeRunOnceAsync(hosted);
        var firstState = await _repository.GetWatchlistSchedulerStateAsync("playlist", CancellationToken.None);
        Assert.NotNull(firstState);
        Assert.Equal("completed", firstState!.CycleStatus);
        Assert.True(
            firstState.NextCycleUtc is { } firstNext && firstNext > DateTimeOffset.UtcNow,
            "A disabled coordinator must schedule a future next cycle so the wait phase sleeps.");

        // Running the next cycle once more must not strand the scheduler without a future
        // next_cycle_utc — that is the condition behind the disabled busy-loop.
        await InvokeRunOnceAsync(hosted);
        var secondState = await _repository.GetWatchlistSchedulerStateAsync("playlist", CancellationToken.None);
        Assert.NotNull(secondState);
        Assert.True(secondState!.NextCycleUtc is { } secondNext && secondNext > DateTimeOffset.UtcNow);
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
        await ConfigurePlaylistDestinationAsync("deezer", "pl-stale");

        var hosted = new WatchlistRunCoordinator(_provider, NullLogger<WatchlistRunCoordinator>.Instance);
        var staleKey = "playlist:deezer:pl-stale";

        await InvokeRunOnceAsync(hosted);
        var failures = GetFailureMap(hosted);
        Assert.True(failures.ContainsKey(staleKey));

        await _repository.RemovePlaylistWatchlistAsync("deezer", "pl-stale");
        await InvokeRunOnceAsync(hosted);

        var failuresAfterCleanup = GetFailureMap(hosted);
        Assert.False(failuresAfterCleanup.ContainsKey(staleKey));
    }

    [Fact]
    public async Task RunOnce_HighVolumePlaylistLoad_ProcessesEveryMonitoredPlaylist()
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
            await ConfigurePlaylistDestinationAsync("unsupported", $"pl-load-{index:D4}");
        }

        var hosted = new WatchlistRunCoordinator(_provider, NullLogger<WatchlistRunCoordinator>.Instance);
        await InvokeRunOnceAsync(hosted);

        var processedUnsupported = (await _repository.GetPlaylistWatchlistAsync())
            .Count(playlist => playlist.SourceId.StartsWith("pl-load-", StringComparison.Ordinal)
                               && playlist.LastCheckedUtc.HasValue);
        Assert.Equal(total, processedUnsupported);

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
        await ConfigurePlaylistDestinationAsync("unsupported", "pl-circuit-1");
        await ConfigurePlaylistDestinationAsync("unsupported", "pl-circuit-2");

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
        Assert.NotNull(stateOne);
        Assert.Equal("circuit_open", stateOne!.LastRunStatus);
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
    public async Task RunOnce_FullRunRechecksPersistedItemsAfterRestart()
    {
        var settings = _settingsService.LoadSettings();
        settings.WatchDelayBetweenPlaylistsSeconds = 3600;
        settings.WatchPollIntervalSeconds = 3600;
        _settingsService.SaveSettings(settings);

        await _repository.AddPlaylistWatchlistAsync("unsupported", "pl-persisted-delay", new PlaylistWatchlistMetadataInput("Persisted Delay", null, null, null));
        await ConfigurePlaylistDestinationAsync("unsupported", "pl-persisted-delay");

        var firstHosted = new WatchlistRunCoordinator(_provider, NullLogger<WatchlistRunCoordinator>.Instance);
        await InvokeRunOnceAsync(firstHosted);

        var state = await _repository.GetPlaylistWatchStateAsync("unsupported", "pl-persisted-delay", CancellationToken.None);
        Assert.NotNull(state?.LastCheckedUtc);
        var firstLastCheckedUtc = state!.LastCheckedUtc;

        var restartedHosted = new WatchlistRunCoordinator(_provider, NullLogger<WatchlistRunCoordinator>.Instance);
        await InvokeRunOnceAsync(restartedHosted);

        var stateAfterRestart = await _repository.GetPlaylistWatchStateAsync("unsupported", "pl-persisted-delay", CancellationToken.None);
        Assert.True(stateAfterRestart?.LastCheckedUtc > firstLastCheckedUtc);
        var restartedFailures = GetFailureMap(restartedHosted);
        Assert.Empty(restartedFailures);
    }

    [Fact]
    public async Task RunOnce_BackoffWarnings_OnlyLogAtThresholdMilestones()
    {
        await _repository.AddPlaylistWatchlistAsync("deezer", "pl-log-threshold", new PlaylistWatchlistMetadataInput("Failing", null, null, null));
        await ConfigurePlaylistDestinationAsync("deezer", "pl-log-threshold");

        var logger = new ListLogger<WatchlistRunCoordinator>();
        var hosted = new WatchlistRunCoordinator(_provider, logger);

        for (var run = 0; run < 6; run++)
        {
            await InvokeRunOnceAsync(hosted);
        }

        var warningCount = logger.Entries.Count(entry =>
            entry.Level == LogLevel.Warning
            && entry.Message.Contains("Watchlist item failed:", StringComparison.Ordinal));
        var debugCount = logger.Entries.Count(entry =>
            entry.Level == LogLevel.Debug
            && entry.Message.Contains("still failing under backoff threshold", StringComparison.Ordinal));

        // Canonical monitor flow should keep warning noise low.
        Assert.InRange(warningCount, 1, 4);
        Assert.InRange(debugCount, 1, 3);
    }

    [Fact]
    public async Task RunOnce_IgnoresPlaylistUntilExplicitDestinationIsAssigned()
    {
        await _repository.AddPlaylistWatchlistAsync(
            "unsupported",
            "pl-no-destination",
            new PlaylistWatchlistMetadataInput("No Destination", null, null, null));

        var hosted = new WatchlistRunCoordinator(_provider, NullLogger<WatchlistRunCoordinator>.Instance);
        await InvokeRunOnceAsync(hosted);

        var ignored = await _repository.GetPlaylistWatchStateAsync(
            "unsupported",
            "pl-no-destination",
            CancellationToken.None);
        Assert.Null(ignored?.LastCheckedUtc);

        await ConfigurePlaylistDestinationAsync("unsupported", "pl-no-destination");
        await InvokeRunOnceAsync(hosted);

        var eligible = await _repository.GetPlaylistWatchStateAsync(
            "unsupported",
            "pl-no-destination",
            CancellationToken.None);
        Assert.NotNull(eligible?.LastCheckedUtc);
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
        await InvokeRecoverCoordinatorStateAsync(hosted);

        var state = await _repository.GetPlaylistWatchStateAsync("unsupported", "pl-smooth-recovery");
        Assert.Equal("pending", state!.LastRunStatus);
        Assert.Equal("pending", state.CurrentPhase);
        Assert.Equal(0, state.ConsecutiveFailures);
        await InvokeRecoverCoordinatorStateAsync(hosted);
    }

    [Fact]
    public void WatchlistCycle_UsesBoundedDrainAndAttemptDeadlineWithoutPerPlaylistTargetSlices()
    {
        var hostedSource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "DeezSpoTag.Web", "Services", "WatchlistRunCoordinator.cs"));
        var syncSource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "DeezSpoTag.Web", "Services", "WatchlistPostDownloadSyncService.cs"));

        Assert.DoesNotContain("SteadyCycleBudget", hostedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("RecoveryCycleBudget", hostedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("PlaylistStartReserve", hostedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("RemainingCycleBudget", hostedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("RunInterleavedPlaylistSliceAsync", hostedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolvePreSweepDrainBudget", hostedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("WatchSmoothSyncEnabled", hostedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SliceMembershipMaxJobs", syncSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ResidualTargetSyncMaxJobs", syncSource, StringComparison.Ordinal);
        // Cycle-end drain is capped by a fixed job count and every attempt is deadline-bounded.
        // Neither is a wall-clock cycle budget, and neither reintroduces per-playlist slicing.
        Assert.Contains("MaxDrainJobsPerCycle", hostedSource, StringComparison.Ordinal);
        Assert.Contains("TargetSyncAttemptDeadline", syncSource, StringComparison.Ordinal);
        Assert.Contains("CancelAfter(TargetSyncAttemptDeadline)", syncSource, StringComparison.Ordinal);
        Assert.Contains("PersistPlaylistProgressAsync", hostedSource, StringComparison.Ordinal);
        Assert.Contains("WaitForFullRunDeadlineAsync", hostedSource, StringComparison.Ordinal);
        Assert.Contains("UpdateWatchlistCycleStateAsync", hostedSource, StringComparison.Ordinal);
        Assert.Contains("EnqueueMembershipCatchUpForIncompletePlaylistsAsync", hostedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("IsPlaylistMembershipCatchUp", hostedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectDuePlaylistItems", hostedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("HasDueIdentityRetryPlaylistAsync", hostedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("GetPlaylistsDueForIdentityRetryAsync", hostedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("identityRetryOnly", hostedSource, StringComparison.Ordinal);
        var loopStart = hostedSource.IndexOf("foreach (var activeItem in playlistItems)", StringComparison.Ordinal);
        var loopEnd = hostedSource.IndexOf("private static async Task PersistPlaylistProgressAsync(", loopStart, StringComparison.Ordinal);
        var loopBody = hostedSource[loopStart..loopEnd];
        Assert.Contains("PersistPlaylistProgressAsync(repository, activeItem, pipelineToken)", loopBody, StringComparison.Ordinal);
        Assert.Contains("CancellationTokenSource.CreateLinkedTokenSource(stoppingToken)", hostedSource, StringComparison.Ordinal);
        Assert.Contains("TryProcessItemAsync(", loopBody, StringComparison.Ordinal);
        Assert.Contains("GetWatchlistReconciliationRequestCountAsync", hostedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("IsSnapshotExpandedAsync", hostedSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunOnce_PersistsLastProgressAfterEachPlaylist()
    {
        await _repository.AddPlaylistWatchlistAsync("unsupported", "pl-progress-1", new PlaylistWatchlistMetadataInput("Progress One", null, null, null));
        await _repository.AddPlaylistWatchlistAsync("unsupported", "pl-progress-2", new PlaylistWatchlistMetadataInput("Progress Two", null, null, null));
        await ConfigurePlaylistDestinationAsync("unsupported", "pl-progress-1");
        await ConfigurePlaylistDestinationAsync("unsupported", "pl-progress-2");

        var hosted = new WatchlistRunCoordinator(_provider, NullLogger<WatchlistRunCoordinator>.Instance);
        await InvokeRunOnceAsync(hosted);
        var afterFlagOff = await _repository.GetWatchlistSchedulerStateAsync("playlist", CancellationToken.None);
        Assert.NotNull(afterFlagOff?.LastProgressUtc);

        await InvokeRunOnceAsync(hosted);
        var afterFlagOn = await _repository.GetWatchlistSchedulerStateAsync("playlist", CancellationToken.None);
        Assert.NotNull(afterFlagOn?.LastProgressUtc);
        Assert.True(afterFlagOn!.LastProgressUtc >= afterFlagOff!.LastProgressUtc);
    }

    [Fact]
    public async Task RunOnce_SnapshotsDuePlaylistsAndDrainsTargetJobs()
    {
        var settings = _settingsService.LoadSettings();
        settings.WatchPollIntervalSeconds = 3600;
        _settingsService.SaveSettings(settings);

        for (var index = 1; index <= 3; index++)
        {
            var sourceId = $"pl-slice-{index}";
            await _repository.AddPlaylistWatchlistAsync(
                "unsupported",
                sourceId,
                new PlaylistWatchlistMetadataInput($"Slice {index}", null, null, null));
            await ConfigurePlaylistDestinationAsync("unsupported", sourceId);
            await _repository.UpsertPlaylistWatchPreferenceAsync(
                new LibraryRepository.PlaylistWatchPreferenceUpsertInput(
                    Source: "unsupported",
                    SourceId: sourceId,
                    DestinationFolderId: _destinationFolderId,
                    Service: "plex",
                    SyncTargets: ["plex", "jellyfin", "navidrome"],
                    PreferredEngine: null,
                    DownloadEngineOrder: null,
                    DownloadVariantMode: null,
                    SyncMode: "mirror",
                    UpdateArtwork: true,
                    ReuseSavedArtwork: false));
            var jobs = await _repository.EnqueueWatchlistPlaylistSyncJobsAsync(
                "unsupported",
                sourceId,
                $"snapshot-{index}");
            Assert.Equal(3, jobs.Count);
        }

        var hosted = new WatchlistRunCoordinator(_provider, NullLogger<WatchlistRunCoordinator>.Instance);
        await InvokeRunOnceAsync(hosted);

        var scheduler = await _repository.GetWatchlistSchedulerStateAsync("playlist", CancellationToken.None);
        Assert.NotNull(scheduler?.LastProgressUtc);
        var checkedPlaylists = 0;
        for (var index = 1; index <= 3; index++)
        {
            var state = await _repository.GetPlaylistWatchStateAsync("unsupported", $"pl-slice-{index}", CancellationToken.None);
            if (state?.LastCheckedUtc != null)
            {
                checkedPlaylists++;
            }
        }

        Assert.Equal(3, checkedPlaylists);
        var remainingJobs = await _repository.GetWatchlistSyncJobStatusCountsAsync();
        Assert.True(remainingJobs.Due + remainingJobs.Processing + remainingJobs.RetryWaiting >= 1);
    }

    [Fact]
    public async Task RunOnce_ProcessesEveryPlaylistEvenWhenOneWasRecentlyChecked()
    {
        var settings = _settingsService.LoadSettings();
        settings.WatchPollIntervalSeconds = 3600;
        settings.WatchDelayBetweenPlaylistsSeconds = 1;
        _settingsService.SaveSettings(settings);

        await _repository.AddPlaylistWatchlistAsync("unsupported", "pl-head", new PlaylistWatchlistMetadataInput("Head", null, null, null));
        await _repository.AddPlaylistWatchlistAsync("unsupported", "pl-tail", new PlaylistWatchlistMetadataInput("Tail", null, null, null));
        await ConfigurePlaylistDestinationAsync("unsupported", "pl-head");
        await ConfigurePlaylistDestinationAsync("unsupported", "pl-tail");
        var headChecked = DateTimeOffset.UtcNow;
        await _repository.UpsertPlaylistWatchStateAsync(
            new LibraryRepository.PlaylistWatchStateUpsertInput(
                "unsupported",
                "pl-head",
                null,
                1,
                null,
                null,
                headChecked,
                "unchanged",
                null,
                null,
                0,
                "unchanged",
                0,
                1,
                headChecked,
                headChecked.AddMinutes(45)));
        await _repository.UpsertPlaylistWatchStateAsync(
            new LibraryRepository.PlaylistWatchStateUpsertInput(
                "unsupported",
                "pl-tail",
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

        var hosted = new WatchlistRunCoordinator(_provider, NullLogger<WatchlistRunCoordinator>.Instance);
        await InvokeRunOnceAsync(hosted);

        var headAfter = await _repository.GetPlaylistWatchStateAsync("unsupported", "pl-head", CancellationToken.None);
        var tailAfter = await _repository.GetPlaylistWatchStateAsync("unsupported", "pl-tail", CancellationToken.None);
        Assert.NotNull(headAfter?.LastCheckedUtc);
        Assert.True(headAfter!.LastCheckedUtc > headChecked);
        Assert.NotNull(tailAfter?.LastCheckedUtc);
        Assert.True(tailAfter!.LastCheckedUtc > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task RunOnce_ChecksNeverCheckedAndPreviouslyVisitedPlaylists()
    {
        var settings = _settingsService.LoadSettings();
        settings.WatchPollIntervalSeconds = 3600;
        settings.WatchDelayBetweenPlaylistsSeconds = 0;
        _settingsService.SaveSettings(settings);

        await _repository.AddPlaylistWatchlistAsync(
            "unsupported",
            "pl-never-checked",
            new PlaylistWatchlistMetadataInput("Never Checked", null, null, null));
        await ConfigurePlaylistDestinationAsync("unsupported", "pl-never-checked");

        await _repository.AddPlaylistWatchlistAsync(
            "unsupported",
            "pl-catch-up",
            new PlaylistWatchlistMetadataInput("Catch Up", null, null, null));
        await ConfigurePlaylistDestinationAsync("unsupported", "pl-catch-up");
        var catchUpChecked = DateTimeOffset.UtcNow.AddMinutes(-5);
        await _repository.UpsertPlaylistWatchStateAsync(
            new LibraryRepository.PlaylistWatchStateUpsertInput(
                "unsupported",
                "pl-catch-up",
                null,
                1,
                null,
                null,
                catchUpChecked,
                "waiting_for_target_sync",
                "Waiting for selected target servers.",
                null,
                0,
                "waiting_for_target_sync",
                0,
                1,
                catchUpChecked,
                catchUpChecked.AddMinutes(45)));

        var hosted = new WatchlistRunCoordinator(_provider, NullLogger<WatchlistRunCoordinator>.Instance);
        await InvokeRunOnceAsync(hosted);

        var neverCheckedAfter = await _repository.GetPlaylistWatchStateAsync(
            "unsupported",
            "pl-never-checked",
            CancellationToken.None);
        Assert.NotNull(neverCheckedAfter?.LastCheckedUtc);
        Assert.False(string.IsNullOrWhiteSpace(neverCheckedAfter!.LastRunStatus));
        Assert.NotNull(neverCheckedAfter.LastCheckedUtc);
    }

    [Fact]
    public async Task SchedulerState_PersistsCompletionAnchoredDeadline()
    {
        var completedUtc = DateTimeOffset.UtcNow;
        var nextCycleUtc = completedUtc.AddHours(1);
        await _repository.UpdateWatchlistCycleStateAsync(
            "playlist",
            "completed",
            completedUtc.AddMinutes(-5),
            completedUtc,
            nextCycleUtc);

        var scheduler = await _repository.GetWatchlistSchedulerStateAsync("playlist");
        Assert.NotNull(scheduler);
        Assert.Equal("completed", scheduler!.CycleStatus);
        Assert.Equal(completedUtc, scheduler.CycleCompletedUtc);
        Assert.Equal(nextCycleUtc, scheduler.NextCycleUtc);
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("running")]
    [InlineData("paused")]
    [InlineData("retrying")]
    public void BatchGate_LiveWatchlistClaimBlocksAnotherRun(string status)
    {
        var startedUtc = DateTimeOffset.UtcNow.AddMinutes(-5);
        var queueItem = CreateQueueItem("watch-owned", status, "{}", startedUtc.AddSeconds(1), startedUtc.AddMinutes(1));
        var claims = new[] { CreateClaim(queueItem.QueueUuid, "pending", startedUtc.AddSeconds(1)) };

        var gate = WatchlistRunCoordinator.EvaluateBatchGate(
            claims, [queueItem], startedUtc, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));

        Assert.True(gate.HasActiveDownloads);
        Assert.Null(gate.LastBatchCompletedUtc);
        Assert.Null(gate.NextRunUtc);
    }

    [Fact]
    public void BatchGate_ManualQueueItemDoesNotBlockWatchlist()
    {
        var startedUtc = DateTimeOffset.UtcNow.AddMinutes(-5);
        var manual = CreateQueueItem("manual", "running", "{}", startedUtc.AddSeconds(1), startedUtc.AddMinutes(1));

        var gate = WatchlistRunCoordinator.EvaluateBatchGate(
            [], [manual], startedUtc, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));

        Assert.False(gate.HasActiveDownloads);
    }

    [Fact]
    public void BatchGate_TerminalBatchStartsIntervalAtLastDownloadCompletion()
    {
        var startedUtc = DateTimeOffset.UtcNow.AddMinutes(-20);
        var firstCompleted = startedUtc.AddMinutes(8);
        var lastCompleted = startedUtc.AddMinutes(10);
        var items = new[]
        {
            CreateQueueItem("watch-1", "completed", "{}", startedUtc.AddSeconds(1), firstCompleted),
            CreateQueueItem("watch-2", "failed", "{}", startedUtc.AddSeconds(2), lastCompleted)
        };
        var claims = new[]
        {
            CreateClaim("watch-1", "completed", firstCompleted),
            CreateClaim("watch-2", "failed", lastCompleted)
        };

        var gate = WatchlistRunCoordinator.EvaluateBatchGate(
            claims, items, startedUtc, startedUtc.AddMinutes(12), TimeSpan.FromMinutes(5));

        Assert.False(gate.HasActiveDownloads);
        Assert.Equal(lastCompleted, gate.LastBatchCompletedUtc);
        Assert.Equal(lastCompleted.AddMinutes(5), gate.NextRunUtc);
        Assert.False(gate.CanRun(startedUtc.AddMinutes(12)));
        Assert.True(gate.CanRun(startedUtc.AddMinutes(15)));
    }

    [Fact]
    public void BatchGate_RestartUsesPersistedClaimsAndQueueOwnership()
    {
        var startedUtc = DateTimeOffset.UtcNow.AddHours(-1);
        var queueItem = CreateQueueItem("persisted", "retrying", "{}", startedUtc.AddMinutes(1), DateTimeOffset.UtcNow);

        var gate = WatchlistRunCoordinator.EvaluateBatchGate(
            [CreateClaim("persisted", "pending", DateTimeOffset.UtcNow)],
            [queueItem],
            startedUtc,
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(1));

        Assert.True(gate.HasActiveDownloads);
    }

    [Fact]
    public async Task WaitingBatch_ReconciliationSignalDoesNotStartAnotherRunOrConsumeRequest()
    {
        var startedUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        var queueItem = CreateQueueItem("waiting-batch", "queued", "{}", startedUtc, DateTimeOffset.UtcNow);
        await _queueRepository.EnqueueAsync(queueItem);
        await _repository.UpsertPlaylistWatchDownloadClaimsAsync(
            "spotify", "playlist", "track", [queueItem.QueueUuid], 1);
        await _repository.UpdateWatchlistCycleStateAsync(
            "playlist", "waiting_downloads", startedUtc, null, null);
        await _repository.EnqueueWatchlistReconciliationRequestAsync("all", null, null);
        var signal = new WatchlistRunSignal();
        var hosted = new WatchlistRunCoordinator(
            _provider,
            new BackgroundWorkCoordinator(),
            signal,
            NullLogger<WatchlistRunCoordinator>.Instance);
        using var cancellation = new CancellationTokenSource();

        var waitTask = InvokeWaitForFullRunDeadlineAsync(hosted, cancellation.Token);
        signal.Request(WatchlistWakeReason.Reconciliation);
        await Task.Delay(100);

        Assert.False(waitTask.IsCompleted);
        Assert.Equal(1, await _repository.GetWatchlistReconciliationRequestCountAsync());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitTask);
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
        Assert.Equal(WatchlistPlaylistState.Pending, WatchlistStateService.Parse("waiting_for_target_sync"));
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

    private static PlaylistWatchDownloadClaimDto CreateClaim(
        string queueUuid,
        string status,
        DateTimeOffset updatedAt)
        => new("spotify", "playlist", queueUuid, queueUuid, 1, status, updatedAt);

    private static DownloadQueueItem CreateQueueItem(
        string queueUuid,
        string status,
        string payloadJson,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? updatedAt = null)
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
            CreatedAt: createdAt ?? DateTimeOffset.UtcNow,
            UpdatedAt: updatedAt ?? DateTimeOffset.UtcNow);

    private static ConcurrentDictionary<string, int> GetFailureMap(WatchlistRunCoordinator hosted)
        => (ConcurrentDictionary<string, int>)GetPrivateField(hosted, "_consecutiveFailures");

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

    private static async Task InvokeWaitForFullRunDeadlineAsync(
        WatchlistRunCoordinator hosted,
        CancellationToken cancellationToken)
    {
        var method = typeof(WatchlistRunCoordinator).GetMethod(
            "WaitForFullRunDeadlineAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var result = method!.Invoke(hosted, new object[] { cancellationToken });
        Assert.NotNull(result);
        await (Task)result!;
    }

    private async Task InvokeOpenSourceCircuitAsync(string source, string fingerprint)
    {
        var method = typeof(WatchlistRunCoordinator).GetMethod(
            "OpenSourceCircuitAsync",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var result = method!.Invoke(
            null,
            new object?[]
            {
                _repository,
                source,
                fingerprint,
                "test failure",
                CancellationToken.None,
                "playlist"
            });
        Assert.NotNull(result);
        await (Task)result!;
    }

    private static async Task InvokeSpotifyAuthRecoveryAsync(
        WatchlistRunCoordinator hosted,
        SpotifyPathfinderMetadataClient.PathfinderAuthRecovery recovery)
    {
        var method = typeof(WatchlistRunCoordinator).GetMethod(
            "HandleSpotifyAuthenticationRecoveredAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var result = method!.Invoke(hosted, new object[] { recovery, CancellationToken.None });
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
        public Func<HttpRequestMessage, HttpResponseMessage>? Response { get; set; }
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? AsyncResponse { get; set; }
        public HttpClient CreateClient(string name) => AsyncResponse is not null ? new(new AsyncSnapshotHandler(AsyncResponse))
            : Response is null ? new() : new(new SnapshotHandler(Response));
        private sealed class AsyncSnapshotHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => response(request, cancellationToken);
        }
        private sealed class SnapshotHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(response(request));
            }
        }
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

    internal sealed class ListLogger<T>(bool failLogging = false) : ILogger<T>
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
            if (failLogging) throw new InvalidOperationException("Diagnostic logger failed.");
            lock (Entries)
                Entries.Add(new LogEntry(logLevel, formatter(state, exception),
                    state is IEnumerable<KeyValuePair<string, object?>> fields
                        ? fields.ToDictionary(pair => pair.Key, pair => pair.Value) : null));
        }
    }

    internal sealed record LogEntry(LogLevel Level, string Message, IReadOnlyDictionary<string, object?>? Fields = null);

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
