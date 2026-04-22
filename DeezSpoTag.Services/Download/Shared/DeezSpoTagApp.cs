using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Linq;
using DeezSpoTag.Services.Download.Utils;
using Microsoft.Extensions.DependencyInjection;
using DeezSpoTag.Core.Models.Download;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Services.Download.Shared;

public class DeezSpoTagApp : DeezSpoTag.Services.Download.Deezer.IDeezerQueueContext
{
    private const string DeezerEngine = "deezer";
    private const string DeezSpoTagEngineAlias = "deezspotag";
    private const string FailedStatus = "failed";
    private const string CanceledStatus = "canceled";
    private const string PausedStatus = "paused";
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
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Loaded deezspotag settings with DownloadLocation: {DownloadLocation}", settings.DownloadLocation);            }
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

            var payload = QueuePayloadJsonParser.Parse(task.PayloadJson);

            payload["status"] = MapStatusForUi(task.Status);
            payload["progress"] = task.Progress ?? 0;
            payload["downloaded"] = task.Downloaded ?? 0;
            payload[FailedStatus] = task.Failed ?? 0;
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

    private readonly object _queueLock = new object();
    private bool _isProcessingQueue = false;
    private bool _stopRequested = false;
    private bool _startRequested = false;
    private readonly HashSet<string> _activeQueueUuids = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pausedByUserUuids = new(StringComparer.OrdinalIgnoreCase);

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
            Settings = _settingsService.LoadSettings();
            var workerConcurrency = ResolveQueueWorkerConcurrency(Settings);
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Queue processing started with {WorkerConcurrency} worker(s).", workerConcurrency);
            }

            var workers = Enumerable.Range(0, workerConcurrency)
                .Select(_ => RunQueueWorkerAsync())
                .ToArray();
            await Task.WhenAll(workers);

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

    private async Task RunQueueWorkerAsync()
    {
        while (true)
        {
            if (IsStopRequested())
            {
                _logger.LogInformation("Queue processing stopped by request.");
                return;
            }

            // Reload queue settings every dequeue cycle so user preference changes apply immediately.
            Settings = _settingsService.LoadSettings();
            var newestFirst = string.Equals(Settings.QueueOrder, "recent", StringComparison.OrdinalIgnoreCase);
            var nextItem = await _queueRepository.DequeueNextAnyAsync(newestFirst);
            if (nextItem == null || string.IsNullOrWhiteSpace(nextItem.QueueUuid))
            {
                return;
            }

            try
            {
                await ProcessQueueItemAsync(nextItem, CancellationToken.None);
            }
            catch (OperationCanceledException ex)
            {
                await HandleUnhandledProcessorCancellationAsync(nextItem, ex);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await HandleUnhandledProcessorFailureAsync(nextItem, ex);
            }
        }
    }

    private bool IsStopRequested()
    {
        lock (_queueLock)
        {
            return _stopRequested;
        }
    }

    private static int ResolveQueueWorkerConcurrency(DeezSpoTagSettings settings)
    {
        return Math.Clamp(settings.MaxConcurrentDownloads, 1, 8);
    }

    private async Task HandleUnhandledProcessorCancellationAsync(DownloadQueueItem item, OperationCanceledException ex)
    {
        if (_cancellationRegistry.WasTimedOut(item.QueueUuid))
        {
            var stallTimeoutException = new TimeoutException(
                DownloadQueueRecoveryPolicy.BuildStallTimeoutMessage(item.Engine),
                ex);
            _logger.LogError(stallTimeoutException, "Queue processor timeout escaped engine for {QueueUuid}", item.QueueUuid);
            await MarkQueueItemAsFailedAndRetryAsync(item, stallTimeoutException.Message);
            return;
        }

        if (_cancellationRegistry.WasUserPaused(item.QueueUuid))
        {
            await _queueRepository.UpdateStatusAsync(
                item.QueueUuid,
                PausedStatus,
                "Paused by user",
                cancellationToken: CancellationToken.None);
            _retryScheduler.Clear(item.QueueUuid);
            return;
        }

        if (_cancellationRegistry.WasUserCanceled(item.QueueUuid))
        {
            await _queueRepository.UpdateStatusAsync(
                item.QueueUuid,
                CanceledStatus,
                "Canceled by user",
                cancellationToken: CancellationToken.None);
            _retryScheduler.Clear(item.QueueUuid);
            _cancellationRegistry.ClearUserCanceled(item.QueueUuid);
            return;
        }

        var timeoutException = new TimeoutException(
            $"Unhandled queue processor cancellation for {item.QueueUuid}.",
            ex);
        _logger.LogError(timeoutException, "Queue processor cancellation escaped engine for {QueueUuid}", item.QueueUuid);
        await MarkQueueItemAsFailedAndRetryAsync(item, timeoutException.Message);
    }

    private async Task HandleUnhandledProcessorFailureAsync(DownloadQueueItem item, Exception ex)
    {
        _logger.LogError(ex, "Queue processor failure escaped engine for {QueueUuid}", item.QueueUuid);
        await MarkQueueItemAsFailedAndRetryAsync(item, ex.Message);
    }

    private async Task MarkQueueItemAsFailedAndRetryAsync(DownloadQueueItem item, string error)
    {
        if (string.IsNullOrWhiteSpace(item.QueueUuid))
        {
            return;
        }

        await _queueRepository.UpdateStatusAsync(
            item.QueueUuid,
            FailedStatus,
            string.IsNullOrWhiteSpace(error) ? "Unhandled processor failure." : error,
            cancellationToken: CancellationToken.None);
        _retryScheduler.ScheduleRetry(item.QueueUuid, item.Engine ?? "unknown", error);
    }

    public async Task ProcessQueueItemAsync(DownloadQueueItem nextItem, CancellationToken cancellationToken = default)
    {
        MarkQueueItemStarted(nextItem.QueueUuid);

        try
        {
            var effectiveItem = await NormalizeFallbackPayloadAsync(nextItem, cancellationToken);
            using var scope = _serviceProvider.CreateScope();
            var engineName = NormalizeEngineName(effectiveItem.Engine);
            if (string.Equals(engineName, DeezSpoTagEngineAlias, StringComparison.OrdinalIgnoreCase))
            {
                engineName = DeezerEngine;
            }
            var processors = scope.ServiceProvider.GetServices<IQueueEngineProcessor>();
            var processor = processors.FirstOrDefault(p =>
                string.Equals(p.Engine, engineName, StringComparison.OrdinalIgnoreCase));

            if (processor == null)
            {
                _logger.LogWarning("Unsupported engine '{Engine}' for queue item {QueueUuid}", effectiveItem.Engine, effectiveItem.QueueUuid);
                await _queueRepository.UpdateStatusAsync(effectiveItem.QueueUuid, FailedStatus, "Unsupported engine", cancellationToken: cancellationToken);
                _retryScheduler.ScheduleRetry(effectiveItem.QueueUuid, effectiveItem.Engine ?? "unknown", "unsupported engine");
                return;
            }

            try
            {
                await processor.ProcessQueueItemAsync(effectiveItem, this, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Engine processing failed for {QueueUuid}", effectiveItem.QueueUuid);
                await _queueRepository.UpdateStatusAsync(effectiveItem.QueueUuid, FailedStatus, ex.Message, cancellationToken: cancellationToken);
                _retryScheduler.ScheduleRetry(effectiveItem.QueueUuid, effectiveItem.Engine ?? "unknown", ex.Message);
            }
        }
        finally
        {
            MarkQueueItemFinished(nextItem.QueueUuid);
        }
    }

    private void MarkQueueItemStarted(string queueUuid)
    {
        lock (_queueLock)
        {
            _activeQueueUuids.Add(queueUuid);
            CurrentJob = queueUuid;
        }
    }

    private void MarkQueueItemFinished(string queueUuid)
    {
        lock (_queueLock)
        {
            _activeQueueUuids.Remove(queueUuid);
            CurrentJob = _activeQueueUuids.FirstOrDefault();
        }
    }

    private async Task<DownloadQueueItem> NormalizeFallbackPayloadAsync(DownloadQueueItem item, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.PayloadJson))
        {
            return item;
        }

        JsonObject? payloadObj;
        try
        {
            payloadObj = JsonNode.Parse(item.PayloadJson) as JsonObject;
        }
        catch (JsonException)
        {
            return item;
        }

        if (payloadObj == null)
        {
            return item;
        }

        var state = FallbackPayloadNormalizer.ResolveCanonicalState(item, Settings, payloadObj);
        var changed = FallbackPayloadNormalizer.ApplyCanonicalState(payloadObj, state, resetIndexAndHistory: false);
        if (!changed)
        {
            return item;
        }

        var normalizedPayload = payloadObj.ToJsonString();
        await _queueRepository.UpdatePayloadAsync(item.QueueUuid, normalizedPayload, cancellationToken);
        return item with { PayloadJson = normalizedPayload };
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
        await _queueRepository.UpdateStatusAsync(uuid, CanceledStatus);
        await UpdateWatchlistTrackStatusAsync(queueItem?.PayloadJson ?? string.Empty, CanceledStatus, CancellationToken.None);
        Listener?.Send("removedFromQueue", new { uuid });
        DeezSpoTagSpeedTracker.Clear(uuid);
    }

    public async Task PauseDownloadAsync(string uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid))
        {
            return;
        }

        _cancellationRegistry.MarkUserPaused(uuid);
        _retryScheduler.Clear(uuid);
        if (_cancellationRegistry.Cancel(uuid))
        {
            Listener?.Send("cancellingCurrentItem", uuid);
        }

        await _queueRepository.UpdateStatusAsync(uuid, PausedStatus);
        Listener?.Send("updateQueue", new { uuid, status = PausedStatus });
        DeezSpoTagSpeedTracker.Clear(uuid);
    }

    public async Task<bool> RetryDownloadAsync(string uuid, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(uuid))
        {
            return false;
        }

        var queueItem = await _queueRepository.GetByUuidAsync(uuid, cancellationToken);
        if (queueItem == null)
        {
            return false;
        }

        var firstStepEngine = queueItem.Engine ?? string.Empty;
        var firstStepQuality = string.Empty;

        if (!string.IsNullOrWhiteSpace(queueItem.PayloadJson))
        {
            try
            {
                if (JsonNode.Parse(queueItem.PayloadJson) is JsonObject payloadObj)
                {
                    var settings = _settingsService.LoadSettings();
                    var canonicalState = FallbackPayloadNormalizer.ResolveCanonicalState(queueItem, settings, payloadObj);
                    _ = FallbackPayloadNormalizer.ApplyCanonicalState(payloadObj, canonicalState, resetIndexAndHistory: true);
                    ResetPayloadRetryState(payloadObj);

                    var updatedPayload = payloadObj.ToJsonString();
                    await _queueRepository.UpdatePayloadAsync(uuid, updatedPayload, cancellationToken);

                    firstStepEngine = canonicalState.FirstStep.Source;
                    firstStepQuality = canonicalState.FirstStep.Quality ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(firstStepEngine))
                    {
                        await _queueRepository.UpdateEngineAsync(uuid, firstStepEngine, cancellationToken);
                    }
                }
            }
            catch (JsonException)
            {
                // Keep retry best-effort for legacy payloads that cannot be normalized.
            }
        }

        _retryScheduler.Clear(uuid);
        _cancellationRegistry.ClearUserCanceled(uuid);
        await _queueRepository.ClearRetryArtifactsAsync(uuid, cancellationToken);

        var requeued = await _queueRepository.RequeueAsync(uuid, cancellationToken);
        if (!requeued)
        {
            return false;
        }

        await UpdateWatchlistTrackStatusAsync(queueItem.PayloadJson ?? string.Empty, "queued", cancellationToken);
        Listener?.Send("updateQueue", new
        {
            uuid,
            status = "inQueue",
            progress = 0,
            downloaded = 0,
            failed = 0,
            error = default(string),
            engine = firstStepEngine,
            quality = firstStepQuality,
            lyricsStatus = string.Empty,
            lyrics_status = string.Empty
        });
        DeezSpoTagSpeedTracker.Clear(uuid);
        await EnsureQueueProcessorRunningAsync();

        return true;
    }

    private static void ResetPayloadRetryState(JsonObject payloadObj)
    {
        payloadObj["Progress"] = 0;
        payloadObj["progress"] = 0;
        payloadObj["Downloaded"] = 0;
        payloadObj["downloaded"] = 0;
        payloadObj["Failed"] = 0;
        payloadObj["failed"] = 0;
        payloadObj["Files"] = new JsonArray();
        payloadObj["files"] = new JsonArray();
        payloadObj["FinalDestinations"] = new JsonObject();
        payloadObj["finalDestinations"] = new JsonObject();
        payloadObj["ErrorMessage"] = string.Empty;
        payloadObj["errorMessage"] = string.Empty;
        payloadObj["error"] = string.Empty;
        payloadObj["FilePath"] = string.Empty;
        payloadObj["filePath"] = string.Empty;

        payloadObj.Remove("LyricsStatus");
        payloadObj.Remove("lyricsStatus");
        payloadObj.Remove("lyrics_status");
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
        List<string> activeQueueUuids;
        lock (_queueLock)
        {
            _stopRequested = true;
            _startRequested = false;
            activeQueueUuids = _activeQueueUuids.ToList();
            foreach (var activeUuid in activeQueueUuids)
            {
                _pausedByUserUuids.Add(activeUuid);
            }
        }

        foreach (var activeQueueUuid in activeQueueUuids)
        {
            _cancellationRegistry.MarkUserPaused(activeQueueUuid);
            if (_cancellationRegistry.Cancel(activeQueueUuid))
            {
                Listener?.Send("cancellingCurrentItem", activeQueueUuid);
            }

            await _queueRepository.UpdateStatusAsync(activeQueueUuid, PausedStatus);
            Listener?.Send("updateQueue", new { uuid = activeQueueUuid, status = PausedStatus });
            DeezSpoTagSpeedTracker.Clear(activeQueueUuid);
        }

        lock (_queueLock)
        {
            CurrentJob = _activeQueueUuids.FirstOrDefault();
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
            if (!_pausedByUserUuids.Contains(uuid))
            {
                return false;
            }

            _pausedByUserUuids.Remove(uuid);
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
