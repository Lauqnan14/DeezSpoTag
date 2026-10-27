using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Settings;
using System.Linq;
using System.Threading.Channels;
using DeezSpoTag.Services.Download.Shared;

namespace DeezSpoTag.Web.Services;

public sealed class LibraryArtistImageQueueService : BackgroundService
{
    private readonly LibraryRepository _repository;
    private readonly LibraryConfigStore _configStore;
    private readonly ArtistMetadataCacheRefreshService _cacheRefreshService;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly IDownloadTagSettingsResolver _profileSettingsResolver;
    private readonly ILogger<LibraryArtistImageQueueService> _logger;
    private readonly Channel<QueueItem> _channel = Channel.CreateUnbounded<QueueItem>();
    private readonly Dictionary<long, QueueItem> _queueItems = new();
    private readonly object _queueLock = new();
    private readonly string _dataRoot;
    private string QueuePath => Path.Join(_dataRoot, "artist-image-queue.json");

    public LibraryArtistImageQueueService(
        LibraryArtistImageQueueDependencies dependencies,
        ILogger<LibraryArtistImageQueueService> logger)
    {
        _repository = dependencies.Repository;
        _configStore = dependencies.ConfigStore;
        _cacheRefreshService = dependencies.CacheRefreshService;
        _settingsService = dependencies.SettingsService;
        _profileSettingsResolver = dependencies.ProfileSettingsResolver;
        _logger = logger;
        _dataRoot = AppDataPaths.GetDataRoot(dependencies.Environment);
    }

    public async Task EnqueueMissingAsync(CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return;
        }

        var missing = await _repository.GetArtistsMissingImageAsync(cancellationToken);
        if (!missing.Any())
        {
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                "Artist image fetch skipped; no missing images."));
            return;
        }

        var enqueued = await PersistentArtistQueueStore.EnqueueArtistsAsync(
            missing,
            static artist => artist.Id,
            static artist => artist.Name,
            static (_, _) => ValueTask.FromResult(false),
            static (artistId, artistName) => new QueueItem(artistId, artistName),
            TryEnqueue,
            cancellationToken);

        if (enqueued > 0)
        {
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                $"Artist image fetch queued ({enqueued} artists)."));
        }
    }

    public async Task<ArtistArtworkRefreshPlan> PlanArtistRefreshAsync(
        long trackId,
        CancellationToken cancellationToken)
    {
        var info = await _repository.GetTrackAudioInfoAsync(trackId, cancellationToken);
        if (info is null || info.DestinationFolderId <= 0)
        {
            return ArtistArtworkRefreshPlan.Skip(trackId, "Library track or assigned profile is unavailable.");
        }

        var profile = await _profileSettingsResolver.ResolveProfileAsync(info.DestinationFolderId, cancellationToken);
        var settings = _settingsService.LoadSettings();
        var saveArtistArtwork = profile?.RuntimeOverrides?.SaveArtworkArtist ?? settings.SaveArtworkArtist;
        if (!saveArtistArtwork)
        {
            return ArtistArtworkRefreshPlan.Skip(trackId, "Artist artwork is disabled by the assigned profile.");
        }

        var artistId = await _repository.GetArtistIdForTrackAsync(trackId, cancellationToken);
        if (!artistId.HasValue || artistId.Value <= 0)
        {
            return ArtistArtworkRefreshPlan.Skip(trackId, "Library artist is unavailable.");
        }

        var artist = await _repository.GetArtistAsync(artistId.Value, cancellationToken);
        if (artist is null)
        {
            return ArtistArtworkRefreshPlan.Skip(trackId, "Library artist is unavailable.");
        }

        var item = new QueueItem(artist.Id, artist.Name);
        var shouldFetch = await ShouldFetchAsync(item, cancellationToken);
        return new ArtistArtworkRefreshPlan(
            trackId,
            artist.Id,
            artist.Name,
            shouldFetch,
            shouldFetch ? null : "Existing artist artwork satisfies the assigned profile.");
    }

    public async Task<bool> RefreshArtistNowAsync(ArtistArtworkRefreshPlan plan, CancellationToken cancellationToken)
    {
        if (!plan.ShouldFetchArtistArtwork || plan.ArtistId <= 0 || string.IsNullOrWhiteSpace(plan.ArtistName))
        {
            return false;
        }

        return await ProcessQueueItemAsync(new QueueItem(plan.ArtistId, plan.ArtistName), cancellationToken);
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        PersistentArtistQueueStore.RestoreAndReplaySnapshot(
            _channel,
            _queueItems,
            _queueLock,
            QueuePath,
            static item => item.ArtistId,
            static item => !string.IsNullOrWhiteSpace(item.ArtistName),
            _logger);

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            if (item.NeedsAttention) continue;
            if (item.NextAttemptUtc > DateTimeOffset.UtcNow) { _ = ReplayWhenDueAsync(item, stoppingToken); continue; }
            try
            {
                if (await ProcessQueueItemAsync(item, stoppingToken)) CompleteItem(item);
                else Retain(item, "Artwork collection remains incomplete.", stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Artist image fetch failed for {ArtistName}; work retained.", item.ArtistName);
                Retain(item, ex.GetType().Name, stoppingToken);
            }
        }
    }

    private async Task<bool> ProcessQueueItemAsync(QueueItem item, CancellationToken cancellationToken)
    {
        var request = await _cacheRefreshService.BuildAutomaticRequestAsync(item.ArtistId, ArtistCacheRefreshScope.Artwork);
        if (await _cacheRefreshService.IsCompleteAsync(request, cancellationToken)) return true;
        return (await _cacheRefreshService.RefreshArtistWithOutcomeAsync(request, cancellationToken)).IsComplete;
    }

    private void Retain(QueueItem item, string reason, CancellationToken token)
    {
        var retained = item with { Attempts = item.Attempts + 1, NextAttemptUtc = DateTimeOffset.UtcNow.AddMinutes(30),
            NeedsAttention = item.Attempts + 1 >= 3, Reason = reason };
        lock (_queueLock)
        {
            _queueItems[item.ArtistId] = retained;
            PersistentArtistQueueStore.PersistRetainedQueueSnapshot(_queueItems, _queueLock, QueuePath, _logger);
        }
        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(DateTimeOffset.UtcNow, "warn",
            $"Artist artwork work retained for {item.ArtistName}: {reason}"));
        if (!retained.NeedsAttention) _ = ReplayWhenDueAsync(retained, token);
    }

    private async Task ReplayWhenDueAsync(QueueItem item, CancellationToken token)
    {
        try
        {
            var delay = (item.NextAttemptUtc ?? DateTimeOffset.UtcNow) - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
            await _channel.Writer.WriteAsync(item, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private bool TryEnqueue(QueueItem item)
    {
        return PersistentArtistQueueStore.TryEnqueue(
            item,
            _channel,
            _queueItems,
            _queueLock,
            QueuePath,
            static queuedItem => queuedItem.ArtistId);
    }

    private async Task<bool> ShouldFetchAsync(QueueItem item, CancellationToken token)
        => !await _cacheRefreshService.IsCompleteAsync(
            await _cacheRefreshService.BuildAutomaticRequestAsync(item.ArtistId, ArtistCacheRefreshScope.Artwork), token);

    private void CompleteItem(QueueItem item)
    {
        PersistentArtistQueueStore.CompleteItem(
            item,
            _queueItems,
            _queueLock,
            QueuePath,
            static queuedItem => queuedItem.ArtistId);
    }

    private sealed record QueueItem(long ArtistId, string ArtistName, int Attempts = 0, DateTimeOffset? NextAttemptUtc = null, bool NeedsAttention = false, string? Reason = null);
}

public sealed record ArtistArtworkRefreshPlan(
    long TrackId,
    long ArtistId,
    string ArtistName,
    bool ShouldFetchArtistArtwork,
    string? SkipReason)
{
    public static ArtistArtworkRefreshPlan Skip(long trackId, string reason)
        => new(trackId, 0, string.Empty, false, reason);
}
