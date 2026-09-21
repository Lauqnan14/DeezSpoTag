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
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Settings;
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
