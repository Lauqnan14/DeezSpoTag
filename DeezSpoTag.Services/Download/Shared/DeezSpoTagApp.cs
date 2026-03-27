using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Linq;
using DeezSpoTag.Services.Download.Utils;
using Microsoft.Extensions.DependencyInjection;
using DeezSpoTag.Core.Models.Download;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Services.Download.Shared;

public class DeezSpoTagApp : DeezSpoTag.Services.Download.Deezer.IDeezerQueueContext
{
    private const string DeezerEngine = "deezer";
    private const string DeezSpoTagEngineAlias = "deezspotag";
    private readonly ILogger<DeezSpoTagApp> _logger;
    private readonly DeezSpoTag.Services.Settings.DeezSpoTagSettingsService _settingsService;
    private readonly IServiceProvider _serviceProvider;
    private readonly DownloadRetryScheduler _retryScheduler;
    private readonly DownloadQueueRepository _queueRepository;
    private readonly DownloadCancellationRegistry _cancellationRegistry;

    public object? CurrentJob { get; private set; }

    public DeezSpoTagSettings Settings { get; private set; }
    public IDeezSpoTagListener? Listener { get; set; }

    public DeezSpoTagApp(
        ILogger<DeezSpoTagApp> logger,
        DeezSpoTag.Services.Settings.DeezSpoTagSettingsService settingsService,
        IDeezSpoTagListener listener,
        DownloadRetryScheduler retryScheduler,
        DownloadQueueRepository queueRepository,
        DownloadCancellationRegistry cancellationRegistry,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _settingsService = settingsService;
        Listener = listener;
        _retryScheduler = retryScheduler;
        _queueRepository = queueRepository;
        _cancellationRegistry = cancellationRegistry;
        _serviceProvider = serviceProvider;

        Settings = LoadSettings();
    }

    private DeezSpoTagSettings LoadSettings()
    {
        try
        {
            var settings = _settingsService.LoadSettings();
            _logger.LogInformation("Loaded deezspotag settings with DownloadLocation: {DownloadLocation}", settings.DownloadLocation);
            return settings;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to load settings, using defaults");
            return new DeezSpoTagSettings();
        }
    }

    public async Task<Dictionary<string, object>> GetQueueAsync()
    {
        if (!DownloadQueueRepository.IsConfigured)
        {
            return new Dictionary<string, object>
            {
                ["queue"] = new Dictionary<string, Dictionary<string, object>>(),
                ["queueOrder"] = new List<string>()
            };
        }

        var tasks = await _queueRepository.GetTasksAsync();
        var queue = new Dictionary<string, Dictionary<string, object>>();
        var queueOrder = new List<string>();

        foreach (var task in tasks)
        {
            if (string.IsNullOrWhiteSpace(task.QueueUuid))
            {
                continue;
            }

            var payload = string.IsNullOrWhiteSpace(task.PayloadJson)
                ? new Dictionary<string, object>()
                : JsonSerializer.Deserialize<Dictionary<string, object>>(task.PayloadJson) ?? new Dictionary<string, object>();

            payload["status"] = MapStatusForUi(task.Status);
            payload["progress"] = task.Progress ?? 0;
            payload["downloaded"] = task.Downloaded ?? 0;
            payload["failed"] = task.Failed ?? 0;
            payload["engine"] = NormalizeEngineName(task.Engine);
            payload["uuid"] = task.QueueUuid;
            if (!payload.ContainsKey("contentType") && !string.IsNullOrWhiteSpace(task.ContentType))
            {
                payload["contentType"] = task.ContentType!;
            }

            queue[task.QueueUuid] = payload;

            if (task.Status == "queued" || task.Status == "running")
            {
                queueOrder.Add(task.QueueUuid);
            }
        }

        var result = new Dictionary<string, object>
        {
            ["queue"] = queue,
            ["queueOrder"] = queueOrder
        };

        if (CurrentJob != null)
        {
            result["current"] = CurrentJob;
        }

        return result;
    }

    public async Task<List<Dictionary<string, object>>> AddToQueueAsync(string[] urls, int bitrate, bool retry = false, long? destinationFolderId = null)
    {
        using var scope = _serviceProvider.CreateScope();
        var queueService = scope.ServiceProvider.GetRequiredService<DeezSpoTag.Services.Download.Deezer.DeezerQueueService>();
        var result = await queueService.AddToQueueAsync(urls, bitrate, retry, destinationFolderId);
        if (result.Count > 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(100);
                    await EnsureQueueProcessorRunningAsync();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Error ensuring queue processor is running");
                }
            });
        }

        return result;
    }


    private readonly object _queueLock = new object();
    private bool _isProcessingQueue = false;
    private bool _stopRequested = false;
    private bool _startRequested = false;
    private string? _currentQueueUuid;
    private string? _pausedByUserUuid;

    public static string Engine => DeezerEngine;

    public async Task StartQueueAsync()
    {
        var shouldStartProcessor = false;

        // Prevent multiple queue processors from running simultaneously
        lock (_queueLock)
        {
            if (_isProcessingQueue)
            {
                _startRequested = true;
                _stopRequested = false;
                _logger.LogDebug("Queue processor already running; marked start request.");
                return;
            }
            _isProcessingQueue = true;
            _stopRequested = false;
            _startRequested = false;
            shouldStartProcessor = true;
        }

        if (!shouldStartProcessor)
        {
            return;
        }

        try
        {
            while (true)
            {
                if (_stopRequested)
                {
                    _logger.LogInformation("Queue processing stopped by request.");
                    break;
                }

                var newestFirst = string.Equals(Settings.QueueOrder, "recent", StringComparison.OrdinalIgnoreCase);
                var nextItem = await _queueRepository.DequeueNextAnyAsync(newestFirst);
                if (nextItem == null || string.IsNullOrWhiteSpace(nextItem.QueueUuid))
                {
                    break;
                }

                await ProcessQueueItemAsync(nextItem, CancellationToken.None);
            }

            _logger.LogInformation("Queue processing completed.");
        }
        finally
        {
            var shouldRestart = false;
            lock (_queueLock)
            {
                _isProcessingQueue = false;
                shouldRestart = _startRequested && !_stopRequested;
                _startRequested = false;
            }

            if (shouldRestart)
            {
                _logger.LogInformation("Restarting queue processor after pending start request.");
                _ = Task.Run(StartQueueAsync);
            }
        }
    }

    public async Task ProcessQueueItemAsync(DownloadQueueItem nextItem, CancellationToken cancellationToken = default)
    {
        CurrentJob = nextItem.QueueUuid;
        _currentQueueUuid = nextItem.QueueUuid;

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var engineName = NormalizeEngineName(nextItem.Engine);
            if (string.Equals(engineName, DeezSpoTagEngineAlias, StringComparison.OrdinalIgnoreCase))
            {
                engineName = DeezerEngine;
            }
            var processors = scope.ServiceProvider.GetServices<IQueueEngineProcessor>();
            var processor = processors.FirstOrDefault(p =>
                string.Equals(p.Engine, engineName, StringComparison.OrdinalIgnoreCase));

            if (processor == null)
            {
                _logger.LogWarning("Unsupported engine '{Engine}' for queue item {QueueUuid}", nextItem.Engine, nextItem.QueueUuid);
                await _queueRepository.UpdateStatusAsync(nextItem.QueueUuid, "failed", "Unsupported engine", cancellationToken: cancellationToken);
                _retryScheduler.ScheduleRetry(nextItem.QueueUuid, nextItem.Engine ?? "unknown", "unsupported engine");
                return;
            }

            try
            {
                await processor.ProcessQueueItemAsync(nextItem, this, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Engine processing failed for {QueueUuid}", nextItem.QueueUuid);
                await _queueRepository.UpdateStatusAsync(nextItem.QueueUuid, "failed", ex.Message, cancellationToken: cancellationToken);
                _retryScheduler.ScheduleRetry(nextItem.QueueUuid, nextItem.Engine ?? "unknown", ex.Message);
            }
        }
        finally
        {
            CurrentJob = null;
            _currentQueueUuid = null;
        }
    }

    public async Task CancelDownloadAsync(string uuid)
    {
        _cancellationRegistry.MarkUserCanceled(uuid);
        _retryScheduler.Clear(uuid);
        if (_cancellationRegistry.Cancel(uuid))
        {
            Listener?.Send("cancellingCurrentItem", uuid);
        }

        var queueItem = await _queueRepository.GetByUuidAsync(uuid, CancellationToken.None);
        await _queueRepository.UpdateStatusAsync(uuid, "canceled");
        await UpdateWatchlistTrackStatusAsync(queueItem?.PayloadJson ?? string.Empty, "canceled", CancellationToken.None);
        Listener?.Send("removedFromQueue", new { uuid });
        DeezSpoTagSpeedTracker.Clear(uuid);
    }

    private async Task UpdateWatchlistTrackStatusAsync(string payloadJson, string status, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return;
        }

        if (!TryReadWatchlistIds(payloadJson, out var source, out var playlistId, out var trackId))
        {
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var libraryRepository = scope.ServiceProvider.GetService<LibraryRepository>();
        if (libraryRepository == null || !libraryRepository.IsConfigured)
        {
            return;
        }

        await libraryRepository.UpdatePlaylistWatchTrackStatusAsync(
            source,
            playlistId,
            trackId,
            status,
            cancellationToken);
    }

    private static bool TryReadWatchlistIds(string payloadJson, out string source, out string playlistId, out string trackId)
    {
        source = "";
        playlistId = "";
        trackId = "";

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (TryReadWatchlistFromSourceIds(document.RootElement, out source, out playlistId, out trackId))
            {
                return true;
            }

            if (TryReadWatchlistFromPayload(document.RootElement, out source, out playlistId, out trackId))
            {
                return true;
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadWatchlistFromSourceIds(JsonElement root, out string source, out string playlistId, out string trackId)
    {
        source = "";
        playlistId = "";
        trackId = "";

        if (!TryGetSourceIdsElement(root, out var sourceIds))
        {
            return false;
        }

        if (!TryGetString(sourceIds, "watchlist_source", out source)
            || !TryGetString(sourceIds, "watchlist_playlist", out playlistId)
            || !TryGetString(sourceIds, "watchlist_track", out trackId))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(source)
               && !string.IsNullOrWhiteSpace(playlistId)
               && !string.IsNullOrWhiteSpace(trackId);
    }

    private static bool TryReadWatchlistFromPayload(JsonElement root, out string source, out string playlistId, out string trackId)
    {
        source = "";
        playlistId = "";
        trackId = "";

        if (!TryGetString(root, "watchlistSource", out source)
            && !TryGetString(root, "watchlist_source", out source)
            && !TryGetString(root, "WatchlistSource", out source))
        {
            return false;
        }

        if (!TryGetString(root, "watchlistPlaylistId", out playlistId)
            && !TryGetString(root, "watchlist_playlist", out playlistId)
            && !TryGetString(root, "WatchlistPlaylistId", out playlistId))
        {
            return false;
        }

        if (!TryGetString(root, "watchlistTrackId", out trackId)
            && !TryGetString(root, "watchlist_track", out trackId)
            && !TryGetString(root, "WatchlistTrackId", out trackId))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(source)
               && !string.IsNullOrWhiteSpace(playlistId)
               && !string.IsNullOrWhiteSpace(trackId);
    }

    bool DeezSpoTag.Services.Download.Deezer.IDeezerQueueContext.IsUserPaused(string uuid) => IsUserPaused(uuid);

    Task DeezSpoTag.Services.Download.Deezer.IDeezerQueueContext.UpdateWatchlistTrackStatusAsync(
        string payloadJson,
        string status,
        CancellationToken cancellationToken) =>
        UpdateWatchlistTrackStatusAsync(payloadJson, status, cancellationToken);

    private static bool TryGetSourceIdsElement(JsonElement root, out JsonElement sourceIds)
    {
        if (root.TryGetProperty("source_ids", out sourceIds))
        {
            return sourceIds.ValueKind == JsonValueKind.Object;
        }

        if (root.TryGetProperty("sourceIds", out sourceIds))
        {
            return sourceIds.ValueKind == JsonValueKind.Object;
        }

        sourceIds = default;
        return false;
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = "";
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? "";
            return true;
        }

        return false;
    }

    public async Task PauseQueueAsync()
    {
        string? currentQueueUuid;
        lock (_queueLock)
        {
            _stopRequested = true;
            _startRequested = false;
            currentQueueUuid = _currentQueueUuid;
            if (!string.IsNullOrWhiteSpace(currentQueueUuid))
            {
                _pausedByUserUuid = currentQueueUuid;
            }
        }

        if (!string.IsNullOrWhiteSpace(currentQueueUuid))
        {
            _cancellationRegistry.MarkUserPaused(currentQueueUuid);
            _cancellationRegistry.Cancel(currentQueueUuid);
            await _queueRepository.UpdateStatusAsync(currentQueueUuid, "paused");
            Listener?.Send("updateQueue", new { uuid = currentQueueUuid, status = "paused" });
            lock (_queueLock)
            {
                _currentQueueUuid = null;
            }
            CurrentJob = null;
        }
    }

    public async Task EnsureQueueProcessorRunningAsync()
    {
        lock (_queueLock)
        {
            _stopRequested = false;
            _startRequested = true;
        }
        await StartQueueAsync();
    }

    public Task<int> GetQueuedCountAsync()
    {
        return _queueRepository.GetQueuedCountAsync();
    }

    private bool IsUserPaused(string uuid)
    {
        lock (_queueLock)
        {
            if (!string.Equals(_pausedByUserUuid, uuid, StringComparison.Ordinal))
            {
                return false;
            }

            _pausedByUserUuid = null;
            return true;
        }
    }

    private static string MapStatusForUi(string status)
    {
        return status switch
        {
            "queued" => "inQueue",
            "running" => "downloading",
            "complete" => "completed",
            "canceled" => "cancelled",
            _ => status
        };
    }

    private static string NormalizeEngineName(string? engine)
    {
        if (string.IsNullOrWhiteSpace(engine))
        {
            return DeezerEngine;
        }

        var normalized = engine.Trim().ToLowerInvariant();
        return normalized == DeezSpoTagEngineAlias ? DeezerEngine : normalized;
    }

}
