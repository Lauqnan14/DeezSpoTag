using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed class PlaylistWatchHostedService : BackgroundService
{
    private const string ArtistKind = "artist";
    private const string PlaylistKind = "playlist";
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<PlaylistWatchHostedService> _logger;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _itemLocks = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRun = new();
    private readonly ConcurrentDictionary<string, int> _consecutiveFailures = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nextAllowedRun = new();
    private int _roundRobinIndex;

    public PlaylistWatchHostedService(
        IServiceProvider serviceProvider,
        ILogger<PlaylistWatchHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Playlist watch service started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);

            try
            {
                var delay = GetWatchInterval();
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Playlist watch service stopped.");
    }

    private TimeSpan GetWatchInterval()
    {
        using var scope = _serviceProvider.CreateScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<DeezSpoTagSettingsService>();
        var settings = settingsService.LoadSettings();
        var seconds = settings.WatchPollIntervalSeconds;
        if (seconds < 1)
        {
            seconds = 1;
        }
        return TimeSpan.FromSeconds(seconds);
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        if (!await _runLock.WaitAsync(0, stoppingToken))
        {
            return;
        }

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var settingsService = scope.ServiceProvider.GetRequiredService<DeezSpoTagSettingsService>();
            var settings = settingsService.LoadSettings();
            if (!settings.WatchEnabled)
            {
                _logger.LogDebug("Watchlist disabled in settings.");
                return;
            }

            var repository = scope.ServiceProvider.GetRequiredService<LibraryRepository>();
            if (!repository.IsConfigured)
            {
                _logger.LogDebug("Watchlist skipped - library DB not configured.");
                return;
            }

            var playlists = await repository.GetPlaylistWatchlistAsync(stoppingToken);
            var artists = await repository.GetWatchlistAsync(stoppingToken);
            var items = BuildWatchItems(playlists, artists);
            if (items.Count == 0)
            {
                CleanupStaleState(Array.Empty<WatchItem>());
                return;
            }

            CleanupStaleState(items);
            await ProcessWatchItemsAsync(items, settings, scope.ServiceProvider, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Playlist watch run failed.");
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task ProcessWatchItemsAsync(
        IReadOnlyList<WatchItem> items,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        IServiceProvider serviceProvider,
        CancellationToken stoppingToken)
    {
        var runStartedUtc = DateTimeOffset.UtcNow;
        var maxItemsPerRun = Math.Clamp(settings.WatchMaxItemsPerRun, 1, items.Count);
        var startIndex = _roundRobinIndex % items.Count;
        var processed = 0;
        var lastVisitedIndex = -1;
        var metrics = new WatchRunMetrics(items.Count, maxItemsPerRun, startIndex);

        for (var offset = 0; offset < items.Count && processed < maxItemsPerRun; offset++)
        {
            stoppingToken.ThrowIfCancellationRequested();

            var index = (startIndex + offset) % items.Count;
            lastVisitedIndex = index;
            var item = items[index];
            var eligibility = GetEligibility(item, settings);
            switch (eligibility)
            {
                case WatchItemEligibility.Backoff:
                    metrics.SkippedByBackoff++;
                    continue;
                case WatchItemEligibility.DelayWindow:
                    metrics.SkippedByDelayWindow++;
                    continue;
            }

            var outcome = await TryProcessItemAsync(item, settings, serviceProvider, stoppingToken);
            switch (outcome)
            {
                case WatchItemRunOutcome.Success:
                    processed++;
                    metrics.Processed++;
                    metrics.Succeeded++;
                    break;
                case WatchItemRunOutcome.Failure:
                    processed++;
                    metrics.Processed++;
                    metrics.Failed++;
                    break;
                case WatchItemRunOutcome.LockBusy:
                    metrics.SkippedByLockBusy++;
                    break;
            }
        }

        _roundRobinIndex = lastVisitedIndex >= 0
            ? (lastVisitedIndex + 1) % items.Count
            : (startIndex + 1) % items.Count;

        var elapsedMs = (DateTimeOffset.UtcNow - runStartedUtc).TotalMilliseconds;
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Watchlist run summary: total={TotalItems}, cap={RunCap}, processed={Processed}, ok={Succeeded}, failed={Failed}, skipBackoff={SkippedBackoff}, skipCooldown={SkippedCooldown}, skipLock={SkippedLock}, elapsedMs={ElapsedMs:0}",
                metrics.TotalItems,
                metrics.MaxItemsPerRun,
                metrics.Processed,
                metrics.Succeeded,
                metrics.Failed,
                metrics.SkippedByBackoff,
                metrics.SkippedByDelayWindow,
                metrics.SkippedByLockBusy,
                elapsedMs);
        }
    }

    private async Task<WatchItemRunOutcome> TryProcessItemAsync(
        WatchItem item,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        IServiceProvider serviceProvider,
        CancellationToken stoppingToken)
    {
        var itemLock = _itemLocks.GetOrAdd(item.Key, _ => new SemaphoreSlim(1, 1));
        if (!await itemLock.WaitAsync(0, stoppingToken))
        {
            return WatchItemRunOutcome.LockBusy;
        }

        var startedUtc = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await RunItemAsync(item, serviceProvider, stoppingToken);
            _lastRun[item.Key] = DateTimeOffset.UtcNow;
            _consecutiveFailures.TryRemove(item.Key, out _);
            _nextAllowedRun.TryRemove(item.Key, out _);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Watchlist item succeeded: key={WatchItemKey}, kind={Kind}, source={Source}, elapsedMs={ElapsedMs:0}",
                    item.Key,
                    item.Kind,
                    item.Source,
                    stopwatch.Elapsed.TotalMilliseconds);
            }
            return WatchItemRunOutcome.Success;
        }
        catch (OperationCanceledException ex) when (!stoppingToken.IsCancellationRequested)
        {
            return RecordItemFailure(item, settings, startedUtc, stopwatch, ex);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return RecordItemFailure(item, settings, startedUtc, stopwatch, ex);
        }
        finally
        {
            itemLock.Release();
        }
    }

    private WatchItemRunOutcome RecordItemFailure(
        WatchItem item,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        DateTimeOffset startedUtc,
        Stopwatch stopwatch,
        Exception ex)
    {
        var failures = _consecutiveFailures.AddOrUpdate(item.Key, 1, static (_, current) => Math.Min(current + 1, 12));
        var baseDelaySeconds = item.Kind == ArtistKind
            ? Math.Max(1, settings.WatchDelayBetweenArtistsSeconds)
            : Math.Max(1, settings.WatchDelayBetweenPlaylistsSeconds);
        var backoffSeconds = Math.Min(
            600,
            baseDelaySeconds * (int)Math.Pow(2, Math.Min(failures - 1, 6)));
        var nextRunUtc = startedUtc.AddSeconds(backoffSeconds);
        _nextAllowedRun[item.Key] = nextRunUtc;
        if (ShouldEmitBackoffWarning(failures))
        {
            _logger.LogWarning(
                ex,
                "Watchlist item failed: key={WatchItemKey}, kind={Kind}, source={Source}, failures={Failures}, backoffSeconds={BackoffSeconds}, nextRunUtc={NextRunUtc}, elapsedMs={ElapsedMs:0}",
                item.Key,
                item.Kind,
                item.Source,
                failures,
                backoffSeconds,
                nextRunUtc,
                stopwatch.Elapsed.TotalMilliseconds);
        }
        else
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Watchlist item still failing under backoff threshold: key={WatchItemKey}, kind={Kind}, source={Source}, failures={Failures}, backoffSeconds={BackoffSeconds}, nextRunUtc={NextRunUtc}, elapsedMs={ElapsedMs:0}",
                    item.Key,
                    item.Kind,
                    item.Source,
                    failures,
                    backoffSeconds,
                    nextRunUtc,
                    stopwatch.Elapsed.TotalMilliseconds);
            }
        }
        return WatchItemRunOutcome.Failure;
    }

    private static List<WatchItem> BuildWatchItems(
        IReadOnlyList<PlaylistWatchlistDto> playlists,
        IReadOnlyList<WatchlistArtistDto> artists)
    {
        var items = new List<WatchItem>(playlists.Count + artists.Count);
        foreach (var playlist in playlists)
        {
            if (playlist == null || string.IsNullOrWhiteSpace(playlist.SourceId))
            {
                continue;
            }
            var key = $"playlist:{playlist.Source}:{playlist.SourceId}";
            items.Add(new WatchItem(PlaylistKind, key, NormalizeSource(playlist.Source), playlist, null));
        }

        foreach (var artist in artists)
        {
            var key = $"artist:{artist.ArtistId}";
            items.Add(new WatchItem(ArtistKind, key, ArtistKind, null, artist));
        }

        return items;
    }

    private WatchItemEligibility GetEligibility(WatchItem item, DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings)
    {
        if (_nextAllowedRun.TryGetValue(item.Key, out var nextAllowedUtc) && DateTimeOffset.UtcNow < nextAllowedUtc)
        {
            return WatchItemEligibility.Backoff;
        }

        if (!_lastRun.TryGetValue(item.Key, out var lastRunUtc))
        {
            return WatchItemEligibility.Eligible;
        }

        var delaySeconds = item.Kind == ArtistKind
            ? settings.WatchDelayBetweenArtistsSeconds
            : settings.WatchDelayBetweenPlaylistsSeconds;
        var delay = TimeSpan.FromSeconds(Math.Max(1, delaySeconds));
        return DateTimeOffset.UtcNow - lastRunUtc >= delay
            ? WatchItemEligibility.Eligible
            : WatchItemEligibility.DelayWindow;
    }

    private void CleanupStaleState(IReadOnlyList<WatchItem> items)
    {
        var activeKeys = new HashSet<string>(items.Select(static item => item.Key), StringComparer.Ordinal);
        CleanupDictionary(_itemLocks, activeKeys, static semaphore =>
        {
            semaphore.Dispose();
            return true;
        });
        CleanupDictionary(_lastRun, activeKeys, static _ => true);
        CleanupDictionary(_consecutiveFailures, activeKeys, static _ => true);
        CleanupDictionary(_nextAllowedRun, activeKeys, static _ => true);
    }

    private static void CleanupDictionary<TValue>(
        ConcurrentDictionary<string, TValue> dictionary,
        HashSet<string> activeKeys,
        Func<TValue, bool> onRemoved)
    {
        foreach (var key in dictionary.Keys)
        {
            if (activeKeys.Contains(key))
            {
                continue;
            }

            if (dictionary.TryRemove(key, out var removedValue))
            {
                _ = onRemoved(removedValue);
            }
        }
    }

    private static async Task RunItemAsync(
        WatchItem item,
        IServiceProvider serviceProvider,
        CancellationToken stoppingToken)
    {
        if (item.Kind == PlaylistKind && item.Playlist != null)
        {
            var watcher = serviceProvider.GetRequiredService<PlaylistWatchService>();
            await watcher.CheckPlaylistWatchItemAsync(item.Playlist, stoppingToken);
            return;
        }

        if (item.Kind == ArtistKind && item.Artist != null)
        {
            var watcher = serviceProvider.GetRequiredService<ArtistWatchService>();
            await watcher.CheckArtistWatchItemAsync(item.Artist, stoppingToken);
        }
    }

    private sealed record WatchItem(
        string Kind,
        string Key,
        string Source,
        PlaylistWatchlistDto? Playlist,
        WatchlistArtistDto? Artist);

    private sealed class WatchRunMetrics
    {
        public WatchRunMetrics(int totalItems, int maxItemsPerRun, int startIndex)
        {
            TotalItems = totalItems;
            MaxItemsPerRun = maxItemsPerRun;
            StartIndex = startIndex;
        }

        public int TotalItems { get; }
        public int MaxItemsPerRun { get; }
        public int StartIndex { get; }
        public int Processed { get; set; }
        public int Succeeded { get; set; }
        public int Failed { get; set; }
        public int SkippedByBackoff { get; set; }
        public int SkippedByDelayWindow { get; set; }
        public int SkippedByLockBusy { get; set; }
    }

    private enum WatchItemRunOutcome
    {
        Success,
        Failure,
        LockBusy
    }

    private enum WatchItemEligibility
    {
        Eligible,
        Backoff,
        DelayWindow
    }

    private static string NormalizeSource(string? source)
        => string.IsNullOrWhiteSpace(source) ? "unknown" : source.Trim().ToLowerInvariant();

    internal static bool ShouldEmitBackoffWarning(int failures)
    {
        if (failures <= 2)
        {
            return true;
        }

        // Keep warnings on exponential milestones while reducing repetitive noise.
        return (failures & (failures - 1)) == 0;
    }
}
