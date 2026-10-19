using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Integrations.Jellyfin;
using DeezSpoTag.Integrations.Plex;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class PlaylistSyncReadinessTest : IAsyncLifetime
{
    [Fact]
    public async Task DirectReconciliation_UsesTheSamePlaylistGateAndCancelsWithoutLeakingIt()
    {
        var reconciler = new PlaylistWatchReconciler(null!);
        var playlist = CreatePlaylist();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = reconciler.WithPlaylistExecutionAsync(playlist.Source, playlist.SourceId, async () =>
        {
            entered.TrySetResult();
            await release.Task;
            return true;
        }, CancellationToken.None);
        await entered.Task;
        using var cancellation = new CancellationTokenSource();
        var direct = reconciler.ReconcilePlaylistAsync(playlist, cancellation.Token);
        Assert.False(direct.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => direct);
        release.TrySetResult();
        await held;
        Assert.True(await reconciler.WithPlaylistExecutionAsync(playlist.Source, playlist.SourceId,
            () => Task.FromResult(true), CancellationToken.None));
    }

    [Theory]
    [InlineData(3, 0, true)]
    [InlineData(3, 2, true)]
    [InlineData(3, 3, false)]
    [InlineData(0, 0, false)]
    public void AuthoritativeSnapshotCountDisagreement_RemainsIncomplete(
        int sourceTrackCount,
        int candidateCount,
        bool expected)
    {
        Assert.Equal(
            expected,
            WatchlistEngine.HasAuthoritativeCandidateCountDisagreement(sourceTrackCount, candidateCount));
    }

    private string _tempRoot = string.Empty;
    private LibraryRepository _repository = default!;
    private PlaylistSyncService _syncService = default!;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-sync-readiness-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);

        var dbPath = Path.Join(_tempRoot, "library.db");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = $"Data Source={dbPath}",
                ["DataDirectory"] = _tempRoot
            })
            .Build();

        var dbService = new LibraryDbService(configuration, NullLogger<LibraryDbService>.Instance);
        await dbService.EnsureSchemaAsync();

        _repository = new LibraryRepository(configuration, NullLogger<LibraryRepository>.Instance);
        var environment = new StubWebHostEnvironment(_tempRoot);
        var authService = new PlatformAuthService(
            environment,
            NullLogger<PlatformAuthService>.Instance,
            DataProtectionProvider.Create(new DirectoryInfo(Path.Join(_tempRoot, "keys"))));
        var plexClient = new PlexApiClient(
            NullLogger<PlexApiClient>.Instance,
            new HttpClient(new StubHttpMessageHandler()));
        var jellyfinClient = new JellyfinApiClient(new HttpClient(new StubHttpMessageHandler()));
        var navidromeClient = new DeezSpoTag.Integrations.Navidrome.NavidromeApiClient(
            new HttpClient(new StubHttpMessageHandler()));
        _syncService = new PlaylistSyncService(new PlaylistSyncService.PlaylistSyncDependencies
        {
            LibraryRepository = _repository,
            LocalIdentityResolver = new PassthroughLocalTrackAmbiguityResolver(),
            SpotifyMetadataService = null!,
            PlexApiClient = plexClient,
            JellyfinApiClient = jellyfinClient,
            NavidromeApiClient = navidromeClient,
            AuthService = authService,
            PlaylistVisualService = null!,
            MediaServerRefreshService = null!,
            MediaServerRefreshOutboxService = new MediaServerRefreshOutboxService(
                _repository,
                null!,
                NullLogger<MediaServerRefreshOutboxService>.Instance),
            SharedIdentityResolver = new SharedIdentityResolver(_repository),
            Logger = NullLogger<PlaylistSyncService>.Instance
        });
    }

    public Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
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
    public async Task CheckTrackReadyForAutomaticSyncAsync_ReturnsTerminal_WhenNoTargetServerSelected()
    {
        var readiness = await _syncService.CheckTrackReadyForAutomaticSyncAsync(
            CreatePlaylist(),
            preference: null,
            CreateCandidate(),
            CancellationToken.None);

        Assert.False(readiness.Ready);
        Assert.True(readiness.Terminal);
        Assert.Equal("No target server selected.", readiness.Message);
    }

    [Fact]
    public async Task CheckTrackReadyForAutomaticSyncAsync_Waits_WhenTrackIsMissingFromLocalLibrary()
    {
        var readiness = await _syncService.CheckTrackReadyForAutomaticSyncAsync(
            CreatePlaylist(),
            new PlaylistWatchPreferenceDto(
                Source: "spotify",
                SourceId: "playlist-1",
                DestinationFolderId: 12,
                Service: "plex",
                SyncTargets: null,
                PreferredEngine: null,
                DownloadEngineOrder: null,
                DownloadVariantMode: null,
                SyncMode: "mirror",
                UpdateArtwork: false,
                ReuseSavedArtwork: false,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow,
                RoutingRules: null,
                IgnoreRules: null,
                AtmosDestinationFolderId: null),
            CreateCandidate(),
            CancellationToken.None);

        Assert.False(readiness.Ready);
        Assert.False(readiness.Terminal);
        Assert.Equal("plex", readiness.Service);
        Assert.Equal("Track is not visible in the DeezSpoTag library yet.", readiness.Message);
    }

    [Fact]
    public async Task LoadTracksForSyncAsync_UsesWatchlistCandidatesBeforeReloadingSpotify()
    {
        var method = typeof(PlaylistSyncService).GetMethod(
            "LoadTracksForSyncAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = (Task)method!.Invoke(
            _syncService,
            new object?[]
            {
                CreatePlaylist(),
                new List<PlaylistTrackCandidate>
                {
                    CreateCandidate(),
                    CreateCandidate("track-2", "Song Two")
                },
                CancellationToken.None
            })!;
        await task;

        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var tracks = (System.Collections.IEnumerable)result.GetType().GetField("Item1")!.GetValue(result)!;

        Assert.Equal(2, tracks.Cast<object>().Count());
    }

    [Fact]
    public void PreserveVerifiedIdentity_WhenWeakerRematchFindsNothingAndFileStillExists()
    {
        var existing = new PlaylistWatchTrackStatusDto(
            "track-1", null, "completed", DateTimeOffset.UtcNow,
            null, null, null, null, null,
            LocalTrackId: 42,
            IdentityStatus: "identity_verified");
        var unresolved = new LibraryRepository.LocalTrackIdentityResult(
            null,
            "none",
            "No local track metadata match.",
            Array.Empty<long>());

        Assert.True(WatchlistEngine.ShouldPreserveVerifiedLocalIdentity(
            existing, unresolved, existingFileExists: true, out var preservedReason));
        Assert.False(string.IsNullOrWhiteSpace(preservedReason));
    }

    [Fact]
    public void DoNotPreserveVerifiedIdentity_WhenTheIndexedFileNoLongerExists()
    {
        var existing = new PlaylistWatchTrackStatusDto(
            "track-1", null, "completed", DateTimeOffset.UtcNow,
            null, null, null, null, null,
            LocalTrackId: 42,
            IdentityStatus: "identity_verified");
        var unresolved = new LibraryRepository.LocalTrackIdentityResult(
            null,
            "none",
            "No local track metadata match.",
            Array.Empty<long>());

        Assert.False(WatchlistEngine.ShouldPreserveVerifiedLocalIdentity(
            existing, unresolved, existingFileExists: false, out _));
    }

    [Fact]
    public void PreserveVerifiedIdentity_WhenARematchPointsAtADifferentTrackWithoutEvidence()
    {
        var existing = new PlaylistWatchTrackStatusDto(
            "track-1", null, "completed", DateTimeOffset.UtcNow,
            null, null, null, null, null,
            LocalTrackId: 42,
            IdentityStatus: "identity_verified");
        var conflicting = new LibraryRepository.LocalTrackIdentityResult(
            99,
            "metadata_match",
            "Matched a different local track.",
            new[] { 99L });

        Assert.True(WatchlistEngine.ShouldPreserveVerifiedLocalIdentity(
            existing, conflicting, existingFileExists: true, out var reason));
        Assert.Contains("local track 99", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void DoNotPreserveVerifiedIdentity_WhenTheRematchPointsAtTheSameTrack()
    {
        var existing = new PlaylistWatchTrackStatusDto(
            "track-1", null, "completed", DateTimeOffset.UtcNow,
            null, null, null, null, null,
            LocalTrackId: 42,
            IdentityStatus: "identity_verified");
        var same = new LibraryRepository.LocalTrackIdentityResult(
            42,
            "metadata_match",
            "Matched the same local track.",
            new[] { 42L });

        Assert.False(WatchlistEngine.ShouldPreserveVerifiedLocalIdentity(
            existing, same, existingFileExists: true, out _));
    }

    private static PlaylistWatchlistDto CreatePlaylist()
        => new(
            Id: 1,
            Source: "spotify",
            SourceId: "playlist-1",
            Name: "Road Mix",
            ImageUrl: null,
            Description: null,
            TrackCount: 1,
            CreatedAt: DateTimeOffset.UtcNow);

    [Fact]
    public async Task VerifiedSyncTrackIdentities_PreserveOrderAndDuplicatesWithoutRematchingLocalMetadata()
    {
        var load = typeof(PlaylistSyncService).GetMethod("LoadTracksForSyncAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)load.Invoke(_syncService, new object[] { CreatePlaylist(),
            new List<PlaylistTrackCandidate> { CreateCandidate(), CreateCandidate("track-2", "Song Two"), CreateCandidate() },
            CancellationToken.None })!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var tracks = result.GetType().GetField("Item1")!.GetValue(result)!;
        var rows = ((System.Collections.IEnumerable)tracks).Cast<object>().ToList();
        Assert.Equal(3, rows.Count);
        var verifiedId = rows[0].GetType().GetProperty("VerifiedLocalTrackId");
        Assert.NotNull(verifiedId);
        verifiedId.SetValue(rows[0], 71L);
        verifiedId.SetValue(rows[1], 29L);
        verifiedId.SetValue(rows[2], 71L);
        var resolve = typeof(PlaylistSyncService).GetMethod("ResolveOrderedTrackIdsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        // The fixture library contains no matching metadata. Re-matching would turn these verified IDs into zeroes.
        for (var target = 0; target < 3; target++)
        {
            var ids = await (Task<List<long>>)resolve.Invoke(_syncService, new[] { (object)"spotify", tracks, CancellationToken.None })!;
            Assert.Equal(new long[] { 71, 29, 71 }, ids);
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await (Task<List<long>>)resolve.Invoke(_syncService, new[] { (object)"spotify", tracks, cancelled.Token })!);
    }

    [Fact]
    public async Task CurrentRunIdentities_AreCarriedIntoSyncButReviewCandidatesRemainExcluded()
    {
        var load = typeof(PlaylistSyncService).GetMethod("LoadTracksForSyncAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)load.Invoke(_syncService, new object[] { CreatePlaylist(),
            new List<PlaylistTrackCandidate> { CreateCandidate(), CreateCandidate("track-2"), CreateCandidate() }, CancellationToken.None })!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var tracks = result.GetType().GetField("Item1")!.GetValue(result)!;
        var statuses = new[] {
            new PlaylistWatchTrackStatusDto("track-1", null, "completed", DateTimeOffset.UtcNow, null, null, null, null, null,
                LocalTrackId: 71, IdentityStatus: "identity_verified"),
            new PlaylistWatchTrackStatusDto("track-2", null, "review", DateTimeOffset.UtcNow, null, null, null, null, null,
                LocalTrackId: 29, IdentityStatus: "review") };
        var bind = typeof(PlaylistSyncService).GetMethod("ResolvePersistedAvailableTrackRowsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var binding = (Task)bind.Invoke(_syncService, new[] { (object)"spotify", "playlist-1", tracks, CancellationToken.None, statuses })!;
        await binding;
        var rows = ((System.Collections.IEnumerable)binding.GetType().GetProperty("Result")!.GetValue(binding)!).Cast<object>().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.All(rows, row => {
            var track = row.GetType().GetField("Item1")!.GetValue(row)!;
            Assert.Equal("track-1", track.GetType().GetProperty("SourceTrackId")!.GetValue(track));
            Assert.Equal(71L, track.GetType().GetProperty("VerifiedLocalTrackId")!.GetValue(track));
        });

        // Without a current-run verification, stale persisted decisions must still go through matching.
        var ordinary = (Task)load.Invoke(_syncService, new object[] { CreatePlaylist(), new[] { CreateCandidate() }, CancellationToken.None })!;
        await ordinary;
        var tuple = ordinary.GetType().GetProperty("Result")!.GetValue(ordinary)!;
        var unverified = tuple.GetType().GetField("Item1")!.GetValue(tuple)!;
        var resolve = typeof(PlaylistSyncService).GetMethod("ResolveOrderedTrackIdsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Equal(new long[] { 0 }, await (Task<List<long>>)resolve.Invoke(_syncService,
            new[] { (object)"spotify", unverified, CancellationToken.None })!);
    }

    private static PlaylistTrackCandidate CreateCandidate(
        string trackSourceId = "track-1",
        string title = "Song One")
        => new(
            TrackSourceId: trackSourceId,
            Isrc: "USRC17607839",
            Title: title,
            Artist: "Artist One",
            Album: "Album One",
            ReleaseYear: 2024,
            DurationMs: 180000,
            Explicit: false,
            Genres: Array.Empty<string>());

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }

    private sealed class StubWebHostEnvironment(string contentRootPath) : IWebHostEnvironment, IAppDataRootOverride
    {
        public string WebRootPath { get; set; } = contentRootPath;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "DeezSpoTag.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRootPath);
        public string ContentRootPath { get; set; } = contentRootPath;
        public string EnvironmentName { get; set; } = Environments.Development;
        public string? AppDataRoot => contentRootPath;
    }
}
