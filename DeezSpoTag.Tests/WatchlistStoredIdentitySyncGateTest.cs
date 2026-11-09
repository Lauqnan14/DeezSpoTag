using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations;
using DeezSpoTag.Integrations.Jellyfin;
using DeezSpoTag.Integrations.Navidrome;
using DeezSpoTag.Integrations.Plex;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// A Watchlist sync job must not call Plex, Jellyfin, or Navidrome until the existing
/// server-id fetch has stored an id. Storing that id is what makes exactly one job due.
/// </summary>
[Collection("Settings Config Isolation")]
public sealed class WatchlistStoredIdentitySyncGateTest : IAsyncLifetime
{
    private string _tempRoot = string.Empty;
    private string _dbPath = string.Empty;
    private IConfiguration _configuration = default!;
    private LibraryRepository _repository = default!;
    private DeezSpoTagSettingsService _settings = default!;
    private bool _previousWatchEnabled;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-sync-gate-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
        _dbPath = Path.Join(_tempRoot, "library.db");
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = $"Data Source={_dbPath}"
            })
            .Build();
        await new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance).EnsureSchemaAsync();
        _repository = new LibraryRepository(_configuration, NullLogger<LibraryRepository>.Instance);
        _settings = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
        var settings = _settings.LoadSettings();
        _previousWatchEnabled = settings.WatchEnabled;
        settings.WatchEnabled = true;
        _settings.SaveSettings(settings);
    }

    public Task DisposeAsync()
    {
        try
        {
            var settings = _settings.LoadSettings();
            settings.WatchEnabled = _previousWatchEnabled;
            _settings.SaveSettings(settings);
        }
        catch
        {
            // Best-effort settings restore.
        }

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

        return Task.CompletedTask;
    }

    [Fact]
    public async Task SyncJobDoesNotCallTargetsUntilStoredServerIdExists()
    {
        var calls = new TargetCallCounter();
        await SeedPlaylistAsync("spotify", "before-id", localTrackId: 41);
        await _repository.EnqueueWatchlistPlaylistSyncJobsAsync("spotify", "before-id", "snap-before");
        Assert.Equal(3, (await DueJobsAsync("spotify", "before-id")).Count);

        var processed = await ProcessDueJobsAsync(calls);

        Assert.Equal(3, processed);
        Assert.Equal(0, calls.Count);
        var deferred = await _repository.GetWatchlistSyncJobsAsync("spotify", "before-id");
        Assert.Equal(3, deferred.Count);
        Assert.All(deferred, job => Assert.Equal(
            "Waiting for a stored server id before target sync.",
            job.LastError));
        Assert.Empty(await DueJobsAsync("spotify", "before-id"));
    }

    [Fact]
    public async Task StoringServerIdCreatesExactlyOneDueSyncJob()
    {
        await SeedPlaylistAsync("spotify", "after-id", localTrackId: 77);
        Assert.Empty(await DueJobsAsync("spotify", "after-id"));

        await _repository.UpsertMediaServerTrackMetadataAsync([
            new MediaServerTrackMetadataUpsertDto(77, "plex", "rating-77", "/music/77.flac", DateTimeOffset.UtcNow, "stereo")
        ]);

        var due = await DueJobsAsync("spotify", "after-id");
        var job = Assert.Single(due);
        Assert.Equal("plex", job.TargetService);
        Assert.Equal("playlist", job.TrackId);

        await _repository.UpsertMediaServerTrackMetadataAsync([
            new MediaServerTrackMetadataUpsertDto(77, "plex", "rating-77", "/music/77.flac", DateTimeOffset.UtcNow, "stereo")
        ]);
        Assert.Equal(job.Id, Assert.Single(await DueJobsAsync("spotify", "after-id")).Id);
    }

    [Fact]
    public async Task StoredServerIdCreatesTheMissingPlaylistOnceAndSavesItsId()
    {
        var fixture = new WatchlistRunCoordinatorHardeningTest();
        await fixture.InitializeAsync();
        var authRoot = Path.Join(_tempRoot, "auth");
        Directory.CreateDirectory(authRoot);
        try
        {
            var repository = fixture.MeasurementTestServices.GetRequiredService<LibraryRepository>();
            var reconciler = fixture.MeasurementTestServices.GetRequiredService<PlaylistWatchReconciler>();
            await repository.AddPlaylistWatchlistAsync(
                "unsupported",
                "create-once",
                new PlaylistWatchlistMetadataInput("Created Once", null, null, 1));
            await repository.UpsertPlaylistWatchPreferenceAsync(new LibraryRepository.PlaylistWatchPreferenceUpsertInput(
                Source: "unsupported",
                SourceId: "create-once",
                DestinationFolderId: 7,
                Service: "plex",
                SyncTargets: ["plex"],
                PreferredEngine: null,
                DownloadEngineOrder: null,
                DownloadVariantMode: null,
                SyncMode: "mirror",
                UpdateArtwork: false,
                ReuseSavedArtwork: false));
            var playlist = (await repository.GetPlaylistWatchlistAsync()).Single(item => item.SourceId == "create-once");
            var prepared = await reconciler.PreparePlaylistSnapshotAsync(playlist, null, CancellationToken.None);
            var candidates = new[]
            {
                new PlaylistTrackCandidate(
                    "77",
                    "ISRC77",
                    "Stored",
                    "Artist",
                    "Album",
                    null,
                    180000,
                    false,
                    Array.Empty<string>(),
                    SourcePosition: 1)
            };
            prepared = prepared with
            {
                Candidates = candidates,
                SnapshotId = "snap-create",
                Name = "Created Once",
                TrackCount = 1,
                IsComplete = true,
                SourceItemsObserved = 1,
                IdentityRevision = PlaylistCandidateContract.BuildIdentityRevision("unsupported", candidates),
                WasExpanded = true
            };
            var reconciled = await reconciler.ReconcilePlaylistAsync(playlist, CancellationToken.None, preparedSnapshot: prepared);
            Assert.True(reconciled.Success, reconciled.Message);
            await repository.UpdatePlaylistWatchTrackVerificationsAsync(
                "unsupported",
                "create-once",
                [new PlaylistWatchTrackVerification("77", 77, "verified")]);
            await repository.UpsertMediaServerTrackMetadataAsync([
                new MediaServerTrackMetadataUpsertDto(77, "plex", "rating-77", "/music/77.flac", DateTimeOffset.UtcNow, "stereo")
            ]);

            var target = new RecordingLibraryTarget("plex");
            var auth = new PlatformAuthService(
                new IsolatedWebHost(authRoot),
                NullLogger<PlatformAuthService>.Instance,
                Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create(new DirectoryInfo(authRoot)));
            await auth.SaveAsync(new PlatformAuthState
            {
                Plex = new PlexAuth { Url = "http://plex.test", Token = "token", MachineIdentifier = "machine" }
            });
            var sync = new PlaylistSyncService(new PlaylistSyncService.PlaylistSyncDependencies
            {
                LibraryRepository = repository,
                LocalIdentityResolver = new PassthroughLocalTrackAmbiguityResolver(),
                SpotifyMetadataService = null!,
                PlexApiClient = new PlexApiClient(NullLogger<PlexApiClient>.Instance, new HttpClient(new RecordingHandler())),
                JellyfinApiClient = new JellyfinApiClient(new HttpClient(new RecordingHandler())),
                NavidromeApiClient = new NavidromeApiClient(new HttpClient(new RecordingHandler())),
                PlaylistSyncEngine = new PlaylistSyncEngine(
                    new PlaylistSyncTargetRegistry([target]),
                    (service, binding, ct) => repository.GetPlaylistSyncTargetPlaylistIdAsync(binding.SourceService, binding.SourcePlaylistId, service, ct),
                    (service, binding, playlistId, ct) => repository.UpdatePlaylistWatchTargetPlaylistIdAsync(binding.SourceService, binding.SourcePlaylistId, service, playlistId, ct),
                    (service, playlistId, ct) => Task.FromResult<int?>(null)),
                AuthService = auth,
                PlaylistVisualService = null!,
                MediaServerRefreshService = null!,
                MediaServerRefreshOutboxService = new MediaServerRefreshOutboxService(repository, null!, NullLogger<MediaServerRefreshOutboxService>.Instance),
                SharedIdentityResolver = new SharedIdentityResolver(repository),
                Logger = NullLogger<PlaylistSyncService>.Instance
            });
            var services = new ServiceCollection();
            services.AddSingleton(repository);
            services.AddSingleton(reconciler);
            services.AddSingleton(sync);
            var provider = services.BuildServiceProvider();
            var worker = new WatchlistPostDownloadSyncService(
                provider,
                fixture.MeasurementTestServices.GetRequiredService<DeezSpoTagSettingsService>(),
                new WatchlistRunSignal(),
                NullLogger<WatchlistPostDownloadSyncService>.Instance);

            Assert.Equal(1, await worker.ProcessTargetSyncWorkAsync(TargetSyncBudget.DrainBounded(WatchlistSyncJobKind.All, 5), CancellationToken.None));
            Assert.Equal(1, target.CreateCount);
            Assert.Equal(["rating-77"], target.CreatedItemIds);
            Assert.Equal("created-plex", await repository.GetPlaylistSyncTargetPlaylistIdAsync("unsupported", "create-once", "plex"));

            await repository.EnqueueWatchlistPlaylistSyncJobsAsync("unsupported", "create-once", "snap-create-again", force: true);
            Assert.Equal(1, await worker.ProcessTargetSyncWorkAsync(TargetSyncBudget.DrainBounded(WatchlistSyncJobKind.All, 5), CancellationToken.None));
            Assert.Equal(1, target.CreateCount);
            Assert.Equal(0, target.EmptyCreateCount);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task EmptyTrackListDoesNotCreateATargetPlaylistOrSyncJob()
    {
        var calls = new TargetCallCounter();
        await _repository.AddPlaylistWatchlistAsync(
            "spotify",
            "empty-list",
            new PlaylistWatchlistMetadataInput("Empty", null, null, 0));
        await _repository.UpsertPlaylistWatchPreferenceAsync(Preference("spotify", "empty-list"));
        await _repository.EnqueueWatchlistPlaylistSyncJobsAsync("spotify", "empty-list", "snap-empty");

        await ProcessDueJobsAsync(calls);

        Assert.Equal(0, calls.Count);
        Assert.Empty(await DueJobsAsync("spotify", "empty-list"));
    }

    private async Task SeedPlaylistAsync(string source, string sourceId, long localTrackId)
    {
        await _repository.AddPlaylistWatchlistAsync(
            source,
            sourceId,
            new PlaylistWatchlistMetadataInput("Stored identity", null, null, 1));
        await _repository.UpsertPlaylistWatchPreferenceAsync(Preference(source, sourceId));
        await _repository.UpdatePlaylistWatchTrackVerificationsAsync(
            source,
            sourceId,
            [new PlaylistWatchTrackVerification(localTrackId.ToString(), localTrackId, "verified")]);
    }

    private static LibraryRepository.PlaylistWatchPreferenceUpsertInput Preference(string source, string sourceId)
        => new(
            Source: source,
            SourceId: sourceId,
            DestinationFolderId: 7,
            Service: "plex",
            SyncTargets: ["plex", "jellyfin", "navidrome"],
            PreferredEngine: null,
            DownloadEngineOrder: null,
            DownloadVariantMode: null,
            SyncMode: "mirror",
            UpdateArtwork: false,
            ReuseSavedArtwork: false);

    private async Task<int> ProcessDueJobsAsync(TargetCallCounter calls)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_repository);
        services.AddSingleton(calls.Reject<PlaylistSyncService>());
        services.AddSingleton(calls.Reject<PlexApiClient>());
        services.AddSingleton(calls.Reject<JellyfinApiClient>());
        services.AddSingleton(calls.Reject<NavidromeApiClient>());
        services.AddSingleton(calls.Reject<PlaylistSyncEngine>());
        var provider = services.BuildServiceProvider();
        var worker = new WatchlistPostDownloadSyncService(
            provider,
            _settings,
            new WatchlistRunSignal(),
            NullLogger<WatchlistPostDownloadSyncService>.Instance);
        return await worker.ProcessTargetSyncWorkAsync(
            TargetSyncBudget.DrainBounded(WatchlistSyncJobKind.All, 10),
            CancellationToken.None);
    }

    private async Task<IReadOnlyList<WatchlistSyncJobDto>> DueJobsAsync(string source, string sourceId)
    {
        var now = DateTimeOffset.UtcNow.AddSeconds(1);
        return (await _repository.GetWatchlistSyncJobsAsync(source, sourceId))
            .Where(job => job.Status is "pending" or "retry"
                          && job.NextAttemptUtc <= now)
            .ToList();
    }

    private sealed class RecordingLibraryTarget : IPlaylistSyncTarget
    {
        private readonly Dictionary<string, List<string>> _items = new(StringComparer.Ordinal);

        public RecordingLibraryTarget(string targetId) => TargetId = targetId;

        public string TargetId { get; }
        public PlaylistTargetKind TargetKind => PlaylistTargetKind.Library;
        public int CreateCount { get; private set; }
        public int EmptyCreateCount { get; private set; }
        public IReadOnlyList<string> CreatedItemIds { get; private set; } = [];

        public Task<bool> IsConnectedAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<TargetPlaylistLookup<string>> FindPlaylistAsync(string? playlistId, string name, CancellationToken cancellationToken)
            => Task.FromResult(string.IsNullOrWhiteSpace(playlistId) || !_items.ContainsKey(playlistId)
                ? TargetPlaylistLookup<string>.Missing()
                : TargetPlaylistLookup<string>.Found(playlistId));

        public Task<string?> CreatePlaylistAsync(string name, string? description, CancellationToken cancellationToken)
        {
            EmptyCreateCount++;
            return Task.FromResult<string?>(null);
        }

        public Task<string?> CreateMissingPlaylistAsync(string name, string? description, IReadOnlyList<string> orderedItemIds, CancellationToken cancellationToken)
        {
            if (orderedItemIds.Count == 0)
            {
                EmptyCreateCount++;
                return Task.FromResult<string?>(null);
            }

            CreateCount++;
            CreatedItemIds = orderedItemIds.ToList();
            _items["created-plex"] = orderedItemIds.ToList();
            return Task.FromResult<string?>("created-plex");
        }

        public Task<TargetPlaylistItemsRead> ReadItemIdsAsync(string playlistId, CancellationToken cancellationToken)
            => Task.FromResult(TargetPlaylistItemsRead.Ok(_items.TryGetValue(playlistId, out var items) ? items : []));

        public Task<PlaylistMembershipWriteResult> WriteMembershipAsync(PlaylistMembershipWrite write, CancellationToken cancellationToken)
        {
            _items[write.PlaylistId] = write.OrderedItemIds.ToList();
            return Task.FromResult(PlaylistMembershipWriteResult.Ok(write.OrderedItemIds.Count, 0));
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<MediaContainer size=\"1\"><Track ratingKey=\"rating-77\" /></MediaContainer>")
            });
    }

    private sealed class IsolatedWebHost(string root) : IWebHostEnvironment, IAppDataRootOverride
    {
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "DeezSpoTag.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = Environments.Development;
        public string? AppDataRoot => root;
    }

    private sealed class TargetCallCounter
    {
        private int _count;
        public int Count => _count;

        public Func<IServiceProvider, T> Reject<T>() where T : class
            => _ =>
            {
                Interlocked.Increment(ref _count);
                throw new InvalidOperationException($"{typeof(T).Name} was resolved before a stored server id existed.");
            };
    }
}
