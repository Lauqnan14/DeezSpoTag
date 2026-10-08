using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Download.Qobuz;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Soulseek;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

[Collection("Settings Config Isolation")]
public sealed class EngineFallbackCoordinatorProviderFailureTest : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "fallback-provider-" + Path.GetRandomFileName());
    private readonly TestConfigRootScope _configScope;
    private readonly DownloadQueueRepository _repository;
    private readonly DeezSpoTagSettingsService _settings;

    public EngineFallbackCoordinatorProviderFailureTest()
    {
        Directory.CreateDirectory(_root);
        _configScope = new TestConfigRootScope(_root);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Queue"] = $"Data Source={Path.Join(_root, "queue.db")}",
            ["DataDirectory"] = _root
        }).Build();
        _repository = new DownloadQueueRepository(configuration, NullLogger<DownloadQueueRepository>.Instance);
        _settings = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedProviderLookupAdvancesToLaterEnabledService(bool httpFailure)
    {
        var payload = BuildPayload();
        await EnqueueAsync(payload);
        var coordinator = BuildCoordinator(new ThrowingAmazonResolver(httpFailure));

        var advanced = await coordinator.TryAdvanceAsync(payload.Id, "qobuz", payload, CancellationToken.None);

        Assert.True(advanced);
        Assert.Equal("deezer", payload.Engine);
        Assert.Equal("9", payload.Quality);
        Assert.Equal(2, payload.AutoIndex);
        Assert.Contains(payload.FallbackHistory, attempt => attempt.StepId == "step-1" && attempt.Status == "skipped");
        var saved = await _repository.GetByUuidAsync(payload.Id);
        Assert.NotNull(saved);
        Assert.Equal("deezer", saved.Engine);
        Assert.Equal("queued", saved.Status);
        var persisted = JsonSerializer.Deserialize<QobuzQueueItem>(saved.PayloadJson!);
        Assert.NotNull(persisted);
        Assert.Contains(persisted.FallbackHistory, attempt => attempt.StepId == "step-1" && attempt.Status == "skipped");
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        var payload = BuildPayload();
        await EnqueueAsync(payload);
        using var cancellation = new CancellationTokenSource();
        var coordinator = BuildCoordinator(new CancelingAmazonResolver(cancellation));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.TryAdvanceAsync(payload.Id, "qobuz", payload, cancellation.Token));
    }

    [Fact]
    public async Task RepositoryFailurePropagates()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Queue"] = "Data Source=/dev/null/queue.db",
            ["DataDirectory"] = _root
        }).Build();
        var repository = new DownloadQueueRepository(configuration, NullLogger<DownloadQueueRepository>.Instance);
        var payload = BuildPayload();
        payload.AutoIndex = 1;
        var coordinator = BuildCoordinator(new ThrowingAmazonResolver(false), repository);

        await Assert.ThrowsAsync<IOException>(() =>
            coordinator.TryAdvanceAsync(payload.Id, "amazon", payload, CancellationToken.None));
    }

    [Fact]
    public async Task PersistedTidalId_DoesNotResolveWithoutTheTidalVariantCheck()
    {
        // A Tidal id sitting in the database is not proof of anything on its own. On Tidal an
        // Atmos master is a different track with a different id from its stereo version, and it
        // carries the same quality tags, so an id that was correct yesterday can hand back Atmos
        // audio for a stereo request today. Building the link straight from the saved id would
        // skip the only check that can catch that, and the mistake would surface at download time
        // with the file already written.
        //
        // So a persisted id may only ever be used as a hint to ask Tidal, never as the answer.
        // This service is built with no Tidal service wired in, which is exactly the situation in
        // which there is nobody to ask: the correct outcome is unresolved, and the run moves on to
        // the next fallback source instead of downloading the wrong variant.
        var service = BuildFallbackSearchService();

        var result = await service.ResolveAsync(
            BuildFallbackSearchRequest("tidal", tidalId: "125064025"),
            CancellationToken.None);

        Assert.Null(result.ResolvedUrl);
        Assert.Equal("unresolved", result.ResolutionSource);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("999999999999999999999999999999")]
    public async Task MissingOrInvalidTidalIdRetainsMetadataResolutionPath(string tidalId)
    {
        var service = BuildFallbackSearchService();

        var result = await service.ResolveAsync(
            BuildFallbackSearchRequest("tidal", tidalId),
            CancellationToken.None);

        Assert.Null(result.ResolvedUrl);
        Assert.Equal("unresolved", result.ResolutionSource);
    }

    [Fact]
    public async Task ManualSoulseekPeerSelectionCannotAdvanceEvenWithAnOldCrossEnginePlan()
    {
        var payload = new SoulseekQueueItem
        {
            Id = Guid.NewGuid().ToString("N"),
            SourceService = "soulseek",
            SourceUrl = "https://www.deezer.com/track/123",
            Artist = "Selected Artist",
            Title = "Selected Track",
            Quality = "FLAC",
            SoulseekUsername = "selected-peer",
            SoulseekRemotePath = "Album/Selected Track.flac",
            SoulseekQualityCode = "FLAC",
            AutoIndex = 0,
            FallbackPlan =
            [
                new("step-0", "soulseek", "FLAC", [], "mapped_url"),
                new("step-1", "deezer", "9", [], "direct_url")
            ]
        };

        var advanced = await BuildCoordinator(new ThrowingAmazonResolver(false))
            .TryAdvanceAsync(payload.Id, "soulseek", payload, CancellationToken.None);

        Assert.False(advanced);
        Assert.Equal("soulseek", payload.Engine);
        Assert.Equal("FLAC", payload.Quality);
        Assert.Empty(payload.FallbackHistory);
    }

    [Fact]
    public async Task FailedManualSoulseekPeerSelectionIsNotAutomaticallyRetried()
    {
        var payload = new SoulseekQueueItem
        {
            Id = Guid.NewGuid().ToString("N"),
            SourceService = "soulseek",
            Artist = "Selected Artist",
            Title = "Selected Track",
            Quality = "FLAC",
            SoulseekUsername = "selected-peer",
            SoulseekRemotePath = "Album/Selected Track.flac",
            SoulseekQualityCode = "FLAC"
        };
        await _repository.EnqueueAsync(new DownloadQueueItem(
            Id: 0, QueueUuid: payload.Id, Engine: payload.Engine, ArtistName: payload.Artist,
            TrackTitle: payload.Title, Isrc: null, DeezerTrackId: null,
            DeezerAlbumId: null, DeezerArtistId: null, SpotifyTrackId: null,
            SpotifyAlbumId: null, SpotifyArtistId: null, AppleTrackId: null,
            AppleAlbumId: null, AppleArtistId: null, DurationMs: null,
            DestinationFolderId: null, QualityRank: null, QueueOrder: null,
            Status: "failed", PayloadJson: JsonSerializer.Serialize(payload), Progress: 0,
            Downloaded: 0, Failed: 1, Error: "selected file failed", CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow));
        // Recreate the repository as a restarted process would: the selected peer, path and quality must
        // come back from durable queue JSON, not from an object retained in memory.
        var reopened = new DownloadQueueRepository(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Queue"] = $"Data Source={Path.Join(_root, "queue.db")}",
                ["DataDirectory"] = _root
            })
            .Build(), NullLogger<DownloadQueueRepository>.Instance);
        var saved = await reopened.GetByUuidAsync(payload.Id);
        Assert.NotNull(saved);
        var restored = JsonSerializer.Deserialize<SoulseekQueueItem>(saved.PayloadJson!);
        Assert.NotNull(restored);
        Assert.Equal("selected-peer", restored.SoulseekUsername);
        Assert.Equal("Album/Selected Track.flac", restored.SoulseekRemotePath);
        Assert.Equal("FLAC", restored.SoulseekQualityCode);
        Assert.True(SoulseekPinnedCandidatePolicy.IsManualSelection(restored));

        var retry = new DownloadRetryScheduler(
            reopened, _settings, new NullActivityLogWriter(), new DeezSpoTagListener(),
            NullLogger<DownloadRetryScheduler>.Instance, new DownloadCancellationRegistry());

        var scheduled = await retry.ScheduleRetryAsync(payload.Id, "soulseek", "selected file failed");

        Assert.False(scheduled);
        Assert.False(await _repository.HasScheduledRetriesAsync());

        // Older queued selections may already have a retry waiting when this rule is installed.
        Assert.True(await _repository.ScheduleRetryAsync(payload.Id, "soulseek", "selected file failed", 3));
        await using (var connection = new SqliteConnection($"Data Source={Path.Join(_root, "queue.db")}"))
        {
            await connection.OpenAsync();
            await using var due = connection.CreateCommand();
            due.CommandText = "UPDATE download_task SET retry_next_at = '2000-01-01T00:00:00Z' WHERE queue_uuid = $uuid";
            due.Parameters.AddWithValue("$uuid", payload.Id);
            await due.ExecuteNonQueryAsync();
        }

        Assert.False(await retry.RunRetrySweepAsync());
        Assert.Equal("failed", (await _repository.GetByUuidAsync(payload.Id))!.Status);
        Assert.False(await _repository.HasScheduledRetriesAsync());
    }

    private EngineFallbackCoordinator BuildCoordinator(
        IAmazonFallbackTrackResolver amazon,
        DownloadQueueRepository? repository = null)
    {
        var catalog = new AppleMusicCatalogService(new NotFoundHttpClientFactory(), _settings,
            NullLogger<AppleMusicCatalogService>.Instance, new MemoryCache(new MemoryCacheOptions()));
        var search = new EngineFallbackSearchService(catalog, NullLogger<EngineFallbackSearchService>.Instance,
            amazonFallbackTrackResolver: amazon);
        return new EngineFallbackCoordinator(repository ?? _repository, _settings,
            new DeezerIsrcResolver(null!, NullLogger<DeezerIsrcResolver>.Instance),
            search, new NullActivityLogWriter());
    }

    private EngineFallbackSearchService BuildFallbackSearchService()
    {
        var catalog = new AppleMusicCatalogService(new NotFoundHttpClientFactory(), _settings,
            NullLogger<AppleMusicCatalogService>.Instance, new MemoryCache(new MemoryCacheOptions()));
        return new EngineFallbackSearchService(catalog, NullLogger<EngineFallbackSearchService>.Instance);
    }

    private static EngineFallbackSearchRequest BuildFallbackSearchRequest(string engine, string tidalId) => new(
        Engine: engine,
        SourceUrl: "https://www.deezer.com/track/4249878541",
        SpotifyId: string.Empty,
        AppleId: string.Empty,
        QobuzId: string.Empty,
        TidalId: tidalId,
        AmazonId: string.Empty,
        Isrc: "KEUM71900021",
        Title: "Pandana",
        Artist: "Ethic Entertainment",
        Album: "Pandana",
        DurationMs: 223000,
        DeezerId: "4249878541",
        Quality: "HI_RES",
        ContentType: "stereo",
        Storefront: "us",
        Language: "en-US",
        MediaUserToken: null,
        UserCountry: "US",
        FallbackSearchEnabled: false);

    private static QobuzQueueItem BuildPayload() => new()
    {
        Id = Guid.NewGuid().ToString("N"), Engine = "qobuz", SourceService = "qobuz",
        SourceUrl = "https://play.qobuz.com/track/123", Title = "Track", Artist = "Artist",
        Album = "Album", Isrc = "USAAA1234567", DeezerId = "456", Quality = "27",
        ContentType = "stereo", AutoIndex = 0,
        FallbackPlan =
        [
            new("step-0", "qobuz", "27", [], "isrc"),
            new("step-1", "amazon", "ULTRA_HD_FLAC", [], "mapped_url"),
            new("step-2", "deezer", "9", [], "direct_url")
        ]
    };

    private Task<long?> EnqueueAsync(QobuzQueueItem payload) => _repository.EnqueueAsync(new DownloadQueueItem(
        Id: 0, QueueUuid: payload.Id, Engine: payload.Engine, ArtistName: payload.Artist,
        TrackTitle: payload.Title, Isrc: payload.Isrc, DeezerTrackId: payload.DeezerId,
        DeezerAlbumId: null, DeezerArtistId: null, SpotifyTrackId: null,
        SpotifyAlbumId: null, SpotifyArtistId: null, AppleTrackId: null,
        AppleAlbumId: null, AppleArtistId: null, DurationMs: null,
        DestinationFolderId: null, QualityRank: null, QueueOrder: null,
        Status: "running", PayloadJson: JsonSerializer.Serialize(payload), Progress: 0,
        Downloaded: 0, Failed: 0, Error: null, CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow));

    public void Dispose()
    {
        _configScope.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private sealed class ThrowingAmazonResolver(bool httpFailure) : IAmazonFallbackTrackResolver
    {
        public Task<AmazonFallbackTrackResolution?> ResolveAmazonFallbackTrackAsync(
            string title, string artist, string? album, int? durationMs, string? isrc, CancellationToken cancellationToken)
            => httpFailure
                ? Task.FromException<AmazonFallbackTrackResolution?>(new HttpRequestException("Amazon unavailable"))
                : Task.FromException<AmazonFallbackTrackResolution?>(new InvalidOperationException("Amazon Music metadata session could not be initialized."));

        public Task<AmazonFallbackTrackResolution?> ResolveAmazonAtmosFallbackTrackAsync(
            string title, string artist, string? album, int? durationMs, string? isrc, string? amazonId, CancellationToken cancellationToken)
            => ResolveAmazonFallbackTrackAsync(title, artist, album, durationMs, isrc, cancellationToken);
    }

    private sealed class CancelingAmazonResolver(CancellationTokenSource cancellation) : IAmazonFallbackTrackResolver
    {
        public Task<AmazonFallbackTrackResolution?> ResolveAmazonFallbackTrackAsync(
            string title, string artist, string? album, int? durationMs, string? isrc, CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return Task.FromCanceled<AmazonFallbackTrackResolution?>(cancellationToken);
        }

        public Task<AmazonFallbackTrackResolution?> ResolveAmazonAtmosFallbackTrackAsync(
            string title, string artist, string? album, int? durationMs, string? isrc, string? amazonId, CancellationToken cancellationToken)
            => ResolveAmazonFallbackTrackAsync(title, artist, album, durationMs, isrc, cancellationToken);
    }

    private sealed class NotFoundHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new NotFoundHandler());
        private sealed class NotFoundHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
