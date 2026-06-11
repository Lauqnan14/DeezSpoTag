using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Download.Shared;
using System.Linq;
using System.Text.Json;
using DeezSpoTag.Core.Security;

namespace DeezSpoTag.Web.Controllers;

[Authorize]
[AutoValidateAntiforgeryToken]
public class ActivitiesController : Controller
{
    private const string CompletedStatus = "completed";
    private const string CompleteStatus = "complete";
    private const string CanceledStatus = "canceled";
    private const string CancelledStatus = "cancelled";
    private const string DownloadingStatus = "downloading";
    private const string FailedStatus = "failed";
    private const string SkippedStatus = "skipped";
    private const string QueuedStatus = "queued";
    private const string InQueueStatus = "inqueue";
    private const string PausedStatus = "paused";
    private const string ResolvingStatus = "resolving";
    private const string RetryingStatus = "retrying";
    private const string RunningStatus = "running";
    private const string FinishedStatus = "finished";
    private const string DownloadFinishedStatus = "download finished";
    private const string DoneStatus = "done";
    private const string SuccessStatus = "success";
    private const string ErrorStatus = "error";
    private const string UiQueuedStatus = "queued";
    private const string UiCompleteStatus = "complete";
    private const string DownloadNotFoundMessage = "Download not found in queue";
    private const string DeezerSource = "deezer";
    private const string ArtistKey = "artist";
    private const string QualityTitleKey = "Quality";
    private const string FilesField = "files";
    private const string LyricsStatusField = "lyrics_status";
    private const string TtmlExtension = ".ttml";
    private static readonly ConcurrentDictionary<string, CachedQueuePayload> QueuePayloadCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan QueuePayloadCacheTtl = TimeSpan.FromMinutes(5);
    private const int QueuePayloadCacheMaxEntries = 1024;
    private const int ActivitiesTerminalItemLimit = 200;
    private static readonly string[] CompletedActivityStatuses =
    [
        CompletedStatus,
        CompleteStatus,
        FinishedStatus,
        DownloadFinishedStatus,
        DoneStatus,
        SuccessStatus,
        SkippedStatus
    ];
    private static readonly string[] CanceledActivityStatuses = [CanceledStatus, CancelledStatus];
    private static readonly string[] FailedActivityStatuses = [FailedStatus, ErrorStatus];
    private readonly ILogger<ActivitiesController> _logger;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly DownloadQueueRepository _queueRepository;
    private readonly IDeezSpoTagListener _deezspotagListener;
    private readonly IActivityLogWriter _activityLog;
    private readonly IServiceProvider _serviceProvider;

    public ActivitiesController(
        ILogger<ActivitiesController> logger,
        DeezSpoTagSettingsService settingsService,
        DownloadQueueRepository queueRepository,
        IDeezSpoTagListener deezspotagListener,
        IActivityLogWriter activityLog,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _settingsService = settingsService;
        _queueRepository = queueRepository;
        _deezspotagListener = deezspotagListener;
        _activityLog = activityLog;
        _serviceProvider = serviceProvider;
    }

    public IActionResult Index()
    {
        ViewData["Title"] = "Activities";
        return View();
    }

    [HttpGet]
    public async Task<IActionResult> GetDownloadQueue()
    {
        try
        {
            var queueData = await GetEngineQueueAsync();
            return Json(new
            {
                success = true,
                data = queueData
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error getting download queue");
            return ErrorJson("Failed to load download queue.");
        }
    }

    [HttpPost]
    public async Task<IActionResult> PauseTask([FromBody] CancelDownloadRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            var item = await _queueRepository.GetByUuidAsync(request.Uuid, HttpContext.RequestAborted);
            if (item == null)
            {
                return NotFound(DownloadNotFoundMessage);
            }

            var activityStatus = GetActivityStatus(item.Status);
            if (activityStatus == ActivityStatus.Running)
            {
                await GetDeezSpoTagApp().PauseDownloadAsync(request.Uuid);
            }
            else if (activityStatus is ActivityStatus.Queued or ActivityStatus.Retrying)
            {
                await _queueRepository.UpdateStatusAsync(request.Uuid, PausedStatus, cancellationToken: HttpContext.RequestAborted);
            }
            else if (IsTerminalActivityStatus(activityStatus))
            {
                return BadRequest("Completed, failed, or canceled downloads cannot be paused.");
            }
            else if (activityStatus != ActivityStatus.Paused)
            {
                return BadRequest("Only active downloads can be paused.");
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Paused download {Uuid}", LogSanitizer.OneLine(request.Uuid));
            }
            return Json(new { success = true, message = "Download paused" });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error pausing download {Uuid}", LogSanitizer.OneLine(request.Uuid));
            return ErrorJson("Failed to pause download.");
        }
    }

    [HttpPost]
    public async Task<IActionResult> ResumeTask([FromBody] CancelDownloadRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            var item = await _queueRepository.GetByUuidAsync(request.Uuid, HttpContext.RequestAborted);
            if (item == null)
            {
                return NotFound(DownloadNotFoundMessage);
            }

            var activityStatus = GetActivityStatus(item.Status);
            if (activityStatus == ActivityStatus.Paused)
            {
                await _queueRepository.UpdateStatusAsync(request.Uuid, QueuedStatus, error: null, cancellationToken: HttpContext.RequestAborted);
            }
            else if (IsTerminalActivityStatus(activityStatus))
            {
                return BadRequest("Completed, failed, or canceled downloads cannot be resumed.");
            }
            else
            {
                return BadRequest("Only paused downloads can be resumed.");
            }

            await GetDeezSpoTagApp().EnsureQueueProcessorRunningAsync();
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Resumed download {Uuid}", LogSanitizer.OneLine(request.Uuid));
            }
            return Json(new { success = true, message = "Download resumed" });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error resuming download {Uuid}", LogSanitizer.OneLine(request.Uuid));
            return ErrorJson("Failed to resume download.");
        }
    }

    [HttpPost]
    public async Task<IActionResult> CancelTask([FromBody] CancelDownloadRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            var item = await _queueRepository.GetByUuidAsync(request.Uuid, HttpContext.RequestAborted);
            if (item == null)
            {
                return NotFound(DownloadNotFoundMessage);
            }

            if (!CanCancelActivityItem(item))
            {
                return BadRequest("Only active downloads can be canceled.");
            }

            await GetDeezSpoTagApp().CancelDownloadAsync(request.Uuid);
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Cancelled download {Uuid}", LogSanitizer.OneLine(request.Uuid));
            }
            return Json(new { success = true, message = "Download cancelled" });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error cancelling download {Uuid}", LogSanitizer.OneLine(request.Uuid));
            return ErrorJson("Failed to cancel download.");
        }
    }

    [HttpPost]
    public async Task<IActionResult> ClearCompleted()
    {
        try
        {
            var hidden = await _queueRepository.MarkActivitiesClearedByStatusesAsync(
                CompletedActivityStatuses,
                HttpContext.RequestAborted);
            var deleted = await _queueRepository.DeleteClearableByStatusesAsync(
                CompletedActivityStatuses,
                HttpContext.RequestAborted);
            if (hidden > 0 || deleted > 0)
            {
                _deezspotagListener.SendRemovedFinishedDownloads();
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Cleared completed downloads (removed={Deleted})", deleted);
            }
            return Json(new
            {
                success = true,
                message = hidden > 0 || deleted > 0 ? "Completed downloads cleared" : "No completed downloads to clear",
                deleted,
                hidden
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error clearing completed downloads");
            return ErrorJson("Failed to clear completed downloads.");
        }
    }

    [HttpPost]
    public async Task<IActionResult> ClearCanceled()
    {
        try
        {
            var hidden = await _queueRepository.MarkActivitiesClearedByStatusesAsync(
                CanceledActivityStatuses,
                HttpContext.RequestAborted);
            var deleted = await _queueRepository.DeleteClearableByStatusesAsync(
                CanceledActivityStatuses,
                HttpContext.RequestAborted);
            if (hidden > 0 || deleted > 0)
            {
                _deezspotagListener.SendRemovedFinishedDownloads();
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Cleared canceled downloads (removed={Deleted})", deleted);
            }
            return Json(new
            {
                success = true,
                message = hidden > 0 || deleted > 0 ? "Canceled downloads cleared" : "No canceled downloads to clear",
                deleted,
                hidden
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error clearing canceled downloads");
            return ErrorJson("Failed to clear canceled downloads.");
        }
    }

    [HttpPost]
    public async Task<IActionResult> PauseAll()
    {
        try
        {
            await GetDeezSpoTagApp().PauseQueueAsync();
            await _queueRepository.PauseQueuedAsync();
            _logger.LogInformation("Paused all downloads");
            return Json(new { success = true, message = "All downloads paused" });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error pausing all downloads");
            return ErrorJson("Failed to pause all downloads.");
        }
    }

    [HttpPost]
    public async Task<IActionResult> ResumeAll()
    {
        try
        {
            await _queueRepository.ResumePausedAsync();
            await GetDeezSpoTagApp().EnsureQueueProcessorRunningAsync();
            _logger.LogInformation("Resumed all downloads");
            return Json(new { success = true, message = "All downloads resumed" });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error resuming all downloads");
            return ErrorJson("Failed to resume all downloads.");
        }
    }

    [HttpPost]
    public async Task<IActionResult> CancelAll()
    {
        try
        {
            var tasks = await _queueRepository.GetTasksAsync();
            var canceled = 0;
            var failed = 0;
            foreach (var task in tasks)
            {
                if (string.IsNullOrWhiteSpace(task.QueueUuid))
                {
                    continue;
                }

                if (!CanCancelActivityItem(task))
                {
                    continue;
                }

                try
                {
                    await GetDeezSpoTagApp().CancelDownloadAsync(task.QueueUuid);
                    canceled++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    _logger.LogWarning(ex, "Failed to cancel download during CancelAll {Uuid}", LogSanitizer.OneLine(task.QueueUuid));
                }
            }

            if (failed > 0)
            {
                _logger.LogWarning("CancelAll completed with partial failures (canceled={Canceled}, failed={Failed})", canceled, failed);
            }
            else
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Cancelled all downloads (canceled={Canceled})", canceled);
                }
            }

            return Json(new
            {
                success = true,
                message = failed > 0
                    ? $"Canceled {canceled} download(s). {failed} item(s) could not be canceled."
                    : "All downloads cancelled",
                canceled,
                failed
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error cancelling all downloads");
            return ErrorJson("Failed to cancel all downloads.");
        }
    }

    [HttpPost]
    public async Task<IActionResult> DeleteFailed([FromBody] CancelDownloadRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            var item = await _queueRepository.GetByUuidAsync(request.Uuid);
            if (item == null)
            {
                return NotFound(DownloadNotFoundMessage);
            }

            if (!CanDeleteActivityItem(item))
            {
                return BadRequest("Only failed or canceled downloads can be deleted");
            }

            var hidden = await _queueRepository.MarkActivitiesClearedByUuidAsync(request.Uuid, HttpContext.RequestAborted);
            var deleted = await _queueRepository.DeleteClearableByUuidAsync(request.Uuid);
            if (deleted == 0)
            {
                var itemHidden = hidden > 0;
                if (!itemHidden)
                {
                    return BadRequest("Download cannot be removed until its destination move has completed.");
                }
            }
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Removed failed download {Uuid} from queue", LogSanitizer.OneLine(request.Uuid));
            }
            _deezspotagListener.SendRemovedFromQueue(request.Uuid);
            return Json(new { success = true, message = "Download removed from queue" });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error removing failed download {Uuid}", LogSanitizer.OneLine(request.Uuid));
            return ErrorJson("Failed to remove download from queue.");
        }
    }

    [HttpPost]
    public async Task<IActionResult> RetryFailed([FromBody] CancelDownloadRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            if (string.IsNullOrWhiteSpace(request.Uuid))
            {
                return BadRequest("UUID is required");
            }

            var item = await _queueRepository.GetByUuidAsync(request.Uuid);
            if (item == null)
            {
                return NotFound(DownloadNotFoundMessage);
            }

            if (!CanRetryActivityItem(item))
            {
                return BadRequest("Only failed or canceled downloads can be retried");
            }

            var retryQueued = await GetDeezSpoTagApp().RetryDownloadAsync(request.Uuid, HttpContext.RequestAborted);
            if (!retryQueued)
            {
                return BadRequest("Retry blocked: invalid payload for this download.");
            }

            var updated = await _queueRepository.GetByUuidAsync(request.Uuid, HttpContext.RequestAborted);
            var resolvedEngine = updated?.Engine ?? item.Engine ?? string.Empty;
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Retried download with fallback reset: {Uuid} (engine={Engine})",
                    LogSanitizer.OneLine(request.Uuid),
                    LogSanitizer.OneLine(resolvedEngine));
            }
            _activityLog.Info($"Retry queued (fallback reset): {request.Uuid} engine={resolvedEngine}");
            return Json(new { success = true, message = "Download retry initiated successfully", originalUuid = request.Uuid, newUuid = request.Uuid, engine = resolvedEngine });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error retrying download {Uuid}", LogSanitizer.OneLine(request.Uuid));
            _activityLog.Error($"Retry failed: {request.Uuid} {ex.Message}");
            return ErrorJson("Failed to retry download.");
        }
    }

    [HttpPost]
    public async Task<IActionResult> ClearAll()
    {
        try
        {
            var hidden = await _queueRepository.MarkTerminalActivitiesClearedAsync(HttpContext.RequestAborted);
            var deleted = await _queueRepository.DeleteClearableAllAsync(HttpContext.RequestAborted);
            if (hidden > 0 || deleted > 0)
            {
                _deezspotagListener.SendRemovedAllDownloads(null);
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Cleared all downloads from queue (removed={Deleted})", deleted);
            }
            return Json(new
            {
                success = true,
                message = hidden > 0 || deleted > 0 ? "All downloads cleared" : "Queue is already empty",
                deleted,
                hidden
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error clearing all downloads");
            return ErrorJson("Failed to clear all downloads.");
        }
    }

    [HttpGet]
    public IActionResult GetAccountInfo()
    {
        return Json(new { success = true, data = new Dictionary<string, object>() });
    }

    private async Task<Dictionary<string, object>> GetEngineQueueAsync()
    {
        var settings = _settingsService.LoadSettings();
        var selectedItems = await _queueRepository.GetActivitiesTasksAsync(
            ActivitiesTerminalItemLimit,
            HttpContext.RequestAborted);

        var queue = new Dictionary<string, Dictionary<string, object>>();
        var queueOrder = new List<string>();

        foreach (var item in selectedItems)
        {
            if (string.IsNullOrWhiteSpace(item.QueueUuid))
            {
                continue;
            }

            var payload = BuildQueuePayload(item, settings);
            queue[item.QueueUuid] = payload;
            queueOrder.Add(item.QueueUuid);
        }

        return new Dictionary<string, object>
        {
            ["queue"] = queue,
            ["queueOrder"] = queueOrder
        };
    }

    private static bool IsTerminalQueueStatus(string? status)
    {
        return IsTerminalActivityStatus(GetActivityStatus(status));
    }

    private static bool IsTerminalActivityStatus(ActivityStatus status)
    {
        return status is ActivityStatus.Complete
            or ActivityStatus.Failed
            or ActivityStatus.Canceled;
    }

    private static bool CanPauseActivityItem(DownloadQueueItem item)
    {
        return GetActivityStatus(item.Status) is ActivityStatus.Queued
            or ActivityStatus.Running
            or ActivityStatus.Retrying;
    }

    private static bool CanResumeActivityItem(DownloadQueueItem item)
    {
        return GetActivityStatus(item.Status) == ActivityStatus.Paused;
    }

    private static bool CanCancelActivityItem(DownloadQueueItem item)
    {
        return GetActivityStatus(item.Status) is ActivityStatus.Queued
            or ActivityStatus.Running
            or ActivityStatus.Paused
            or ActivityStatus.Retrying;
    }

    private static bool CanRetryActivityItem(DownloadQueueItem item)
    {
        var activityStatus = GetActivityStatus(item.Status);
        return activityStatus is ActivityStatus.Failed or ActivityStatus.Canceled
            || (activityStatus == ActivityStatus.Complete && (item.Failed ?? 0) > 0);
    }

    private static bool CanDeleteActivityItem(DownloadQueueItem item)
    {
        return GetActivityStatus(item.Status) is ActivityStatus.Failed or ActivityStatus.Canceled;
    }

    private static bool CanClearActivityItem(DownloadQueueItem item)
    {
        return IsTerminalActivityStatus(GetActivityStatus(item.Status));
    }

    private DeezSpoTag.Services.Download.Shared.DeezSpoTagApp GetDeezSpoTagApp()
        => _serviceProvider.GetRequiredService<DeezSpoTag.Services.Download.Shared.DeezSpoTagApp>();

    private static Dictionary<string, object> BuildQueuePayload(DownloadQueueItem item, DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings)
    {
        var payload = ParsePayload(item.PayloadJson);
        NormalizePayloadKeys(payload);
        payload["rawStatus"] = item.Status;
        payload["status"] = MapStatusForUi(item.Status);
        payload["progress"] = item.Progress ?? 0;
        payload["downloaded"] = item.Downloaded ?? 0;
        payload["failed"] = item.Failed ?? 0;
        payload["engine"] = item.Engine;
        payload["uuid"] = item.QueueUuid;
        payload["canPause"] = CanPauseActivityItem(item);
        payload["canResume"] = CanResumeActivityItem(item);
        payload["canCancel"] = CanCancelActivityItem(item);
        payload["canRetry"] = CanRetryActivityItem(item);
        payload["canDelete"] = CanDeleteActivityItem(item);
        payload["canClear"] = CanClearActivityItem(item);
        if (!string.IsNullOrWhiteSpace(item.Error))
        {
            payload["error"] = item.Error;
        }
        if (!payload.TryGetValue("quality", out var quality) || quality is null || string.IsNullOrWhiteSpace(quality.ToString()))
        {
            payload["quality"] = ResolveSourceQuality(item.Engine, settings);
        }
        if (ShouldAttachLyricsFiles(item.Status) && NeedsLyricsAttachment(payload))
        {
            var cacheKey = BuildQueuePayloadCacheKey(item);
            if (TryGetCachedQueuePayload(cacheKey, out var cachedPayload))
            {
                return cachedPayload;
            }

            AttachLyricsFiles(payload);
            CacheQueuePayload(cacheKey, payload);
        }
        return payload;
    }

    private static bool ShouldAttachLyricsFiles(string? status)
    {
        return IsTerminalQueueStatus(status);
    }

    private static bool NeedsLyricsAttachment(Dictionary<string, object> payload)
    {
        if (HasKnownLyricsStatus(payload))
        {
            return false;
        }

        return !HasAttachedLyricsFiles(payload);
    }

    private static bool HasKnownLyricsStatus(Dictionary<string, object> payload)
    {
        var value = GetPayloadString(payload, "lyricsStatus", "LyricsStatus", LyricsStatusField);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized is not ("unknown" or "none" or "n/a" or "na");
    }

    private static bool HasAttachedLyricsFiles(Dictionary<string, object> payload)
    {
        foreach (var filePath in ExtractFiles(payload).Select(GetFilePath))
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                continue;
            }

            var ext = Path.GetExtension(filePath);
            if (string.Equals(ext, ".lrc", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, TtmlExtension, StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".txt", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildQueuePayloadCacheKey(DownloadQueueItem item)
    {
        var normalizedStatus = (item.Status ?? string.Empty).Trim().ToLowerInvariant();
        return $"{item.QueueUuid}|{normalizedStatus}|{item.UpdatedAt.UtcTicks}";
    }

    private static bool TryGetCachedQueuePayload(string cacheKey, out Dictionary<string, object> payload)
    {
        payload = new Dictionary<string, object>();
        if (!QueuePayloadCache.TryGetValue(cacheKey, out var cached))
        {
            return false;
        }

        var nowUtc = DateTimeOffset.UtcNow;
        if (nowUtc - cached.CachedAtUtc > QueuePayloadCacheTtl)
        {
            QueuePayloadCache.TryRemove(cacheKey, out _);
            return false;
        }

        payload = ClonePayloadDictionary(cached.Payload);
        return true;
    }

    private static void CacheQueuePayload(string cacheKey, Dictionary<string, object> payload)
    {
        QueuePayloadCache[cacheKey] = new CachedQueuePayload(
            DateTimeOffset.UtcNow,
            ClonePayloadDictionary(payload));
        PruneQueuePayloadCache();
    }

    internal static Dictionary<string, object> ClonePayloadDictionary(Dictionary<string, object> payload)
    {
        var clone = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in payload)
        {
            var key = string.Equals(entry.Key, "finalDestinations", StringComparison.OrdinalIgnoreCase)
                ? "finalDestinations"
                : entry.Key;
            clone[key] = entry.Value;
        }

        return clone;
    }

    private static void PruneQueuePayloadCache()
    {
        var nowUtc = DateTimeOffset.UtcNow;
        foreach (var entry in QueuePayloadCache.Where(entry => nowUtc - entry.Value.CachedAtUtc > QueuePayloadCacheTtl).ToArray())
        {
            QueuePayloadCache.TryRemove(entry.Key, out _);
        }

        if (QueuePayloadCache.Count <= QueuePayloadCacheMaxEntries)
        {
            return;
        }

        var excess = QueuePayloadCache.Count - QueuePayloadCacheMaxEntries;
        foreach (var entry in QueuePayloadCache.OrderBy(entry => entry.Value.CachedAtUtc).Take(excess).ToArray())
        {
            QueuePayloadCache.TryRemove(entry.Key, out _);
        }
    }

    private static string ResolveSourceQuality(string engine, DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings)
    {
        return engine switch
        {
            DeezerSource => DownloadSourceOrder.ResolveDeezerBitrate(settings, 0).ToString(),
            "qobuz" => MapQobuzQuality(settings.QobuzQuality),
            "tidal" => MapTidalQuality(settings.TidalQuality),
            "amazon" => "FLAC",
            _ => ""
        };
    }

    private static string MapQobuzQuality(string? quality)
    {
        if (quality is null)
        {
            return string.Empty;
        }

        return quality switch
        {
            "6" => "FLAC 16-bit",
            "7" => "FLAC 24-bit",
            "27" => "Hi-Res 24-bit",
            _ => quality
        };
    }

    private static string MapTidalQuality(string? quality)
    {
        if (quality is null)
        {
            return string.Empty;
        }

        return quality switch
        {
            "LOSSLESS" => "Lossless",
            "HI_RES_LOSSLESS" => "Hi-Res Lossless",
            _ => quality
        };
    }

    private static Dictionary<string, object> ParsePayload(string? payloadJson)
    {
        return QueuePayloadJsonParser.Parse(payloadJson);
    }

    private static void NormalizePayloadKeys(Dictionary<string, object> payload)
    {
        EnsurePayloadField(payload, "title", "Title", "trackTitle", "TrackTitle");
        EnsurePayloadField(payload, ArtistKey, "Artist", "artistName", "ArtistName");
        EnsurePayloadField(payload, "album", "Album", "albumName", "AlbumName");
        EnsurePayloadField(payload, "albumArtist", "AlbumArtist", "album_artist", "Album_Artist");
        EnsurePayloadField(payload, "cover", "Cover", "coverUrl", "CoverUrl", "albumCover", "AlbumCover");
        EnsurePayloadField(payload, "sourceService", "SourceService", "source_service");
        EnsurePayloadField(payload, "sourceUrl", "SourceUrl", "source_url");
        EnsurePayloadField(payload, "contentType", "ContentType", "content_type");
        EnsurePayloadField(payload, "collectionType", "CollectionType", "collection_type");
        EnsurePayloadField(payload, "quality", QualityTitleKey, "bitrate", "Bitrate");
        EnsurePayloadFieldRaw(payload, "autoSources", "AutoSources");
        EnsurePayloadFieldRaw(payload, "autoIndex", "AutoIndex");
        EnsurePayloadFieldRaw(payload, "fallbackPlan", "FallbackPlan");
        EnsurePayloadFieldRaw(payload, "fallbackHistory", "FallbackHistory");
        EnsurePayloadFieldRaw(payload, "fallbackQueuedExternally", "FallbackQueuedExternally");
        EnsurePayloadField(payload, "videoResolution", "VideoResolution", "videoResolutionTier", "VideoResolutionTier");
        EnsurePayloadField(payload, "videoHdr", "VideoHdr");
        EnsurePayloadField(payload, "videoAudioProfile", "VideoAudioProfile");
        EnsurePayloadField(payload, "lyricsStatus", "LyricsStatus", "lyrics_status", "lyricsStatus");
        EnsurePayloadField(payload, "filePath", "FilePath", "path", "Path");
        EnsurePayloadField(payload, "extrasPath", "ExtrasPath", "extras_path", "Extras_Path");
        if (!payload.ContainsKey(FilesField) && payload.TryGetValue("Files", out var files))
        {
            payload[FilesField] = files;
        }
    }

    private static void EnsurePayloadField(Dictionary<string, object> payload, string target, params string[] candidates)
    {
        if (payload.ContainsKey(target))
        {
            return;
        }

        foreach (var candidate in candidates)
        {
            if (!payload.TryGetValue(candidate, out var value))
            {
                continue;
            }

            var normalized = NormalizePayloadValue(value);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                payload[target] = normalized;
                return;
            }
        }
    }

    private static void EnsurePayloadFieldRaw(Dictionary<string, object> payload, string target, params string[] candidates)
    {
        if (payload.ContainsKey(target))
        {
            return;
        }

        foreach (var candidate in candidates)
        {
            if (!payload.TryGetValue(candidate, out var value))
            {
                continue;
            }

            payload[target] = value;
            return;
        }
    }

    private static string? NormalizePayloadValue(object? value)
    {
        if (value == null)
        {
            return null;
        }

        if (value is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => element.ToString()
            };
        }

        return value.ToString();
    }

    private static string MapStatusForUi(string status)
    {
        return GetActivityStatus(status) switch
        {
            ActivityStatus.Queued => UiQueuedStatus,
            ActivityStatus.Running => RunningStatus,
            ActivityStatus.Paused => PausedStatus,
            ActivityStatus.Retrying => RetryingStatus,
            ActivityStatus.Complete => UiCompleteStatus,
            ActivityStatus.Failed => FailedStatus,
            ActivityStatus.Canceled => CanceledStatus,
            _ => UiQueuedStatus
        };
    }

    private static ActivityStatus GetActivityStatus(string? status)
    {
        var normalized = (status ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            ResolvingStatus or QueuedStatus or InQueueStatus => ActivityStatus.Queued,
            RunningStatus or DownloadingStatus => ActivityStatus.Running,
            PausedStatus => ActivityStatus.Paused,
            RetryingStatus => ActivityStatus.Retrying,
            CompletedStatus or CompleteStatus or FinishedStatus or DownloadFinishedStatus or DoneStatus or SuccessStatus or SkippedStatus => ActivityStatus.Complete,
            FailedStatus or ErrorStatus => ActivityStatus.Failed,
            CanceledStatus or CancelledStatus => ActivityStatus.Canceled,
            _ => ActivityStatus.Queued
        };
    }

    private static void AttachLyricsFiles(Dictionary<string, object> payload)
    {
        var files = ExtractFiles(payload);
        var audioPath = ResolveAudioPath(payload, files);
        if (string.IsNullOrWhiteSpace(audioPath))
        {
            return;
        }

        var searchDirs = BuildLyricsSearchDirectories(payload, audioPath);
        if (searchDirs.Count == 0)
        {
            return;
        }

        var ioPath = DownloadPathResolver.ResolveIoPath(audioPath);
        var baseName = Path.GetFileNameWithoutExtension(ioPath);
        var existing = BuildExistingFilePathSet(files);
        var lyricPresence = new LyricPresenceState();

        AttachPrimaryLyricsFiles(baseName, searchDirs, files, existing, lyricPresence);

        if (!lyricPresence.HasAny)
        {
            AttachLyricsByTitle(payload, searchDirs, files, existing, lyricPresence);
        }

        if (!lyricPresence.HasAny)
        {
            AttachSingleLyrics(searchDirs, files, existing, lyricPresence);
        }

        if (files.Count > 0)
        {
            payload[FilesField] = files;
        }

        ApplyLyricsStatus(payload, lyricPresence);
    }

    private static List<string> BuildLyricsSearchDirectories(Dictionary<string, object> payload, string audioPath)
    {
        var ioPath = DownloadPathResolver.ResolveIoPath(audioPath);
        var dir = Path.GetDirectoryName(ioPath);
        if (string.IsNullOrWhiteSpace(dir))
        {
            return new List<string>();
        }

        var searchDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { dir };
        var extrasDir = GetPayloadString(payload, "extrasPath", "ExtrasPath");
        if (!string.IsNullOrWhiteSpace(extrasDir))
        {
            var extrasIo = DownloadPathResolver.ResolveIoPath(extrasDir);
            if (!string.IsNullOrWhiteSpace(extrasIo))
            {
                searchDirs.Add(extrasIo);
            }
        }

        return searchDirs.Where(Directory.Exists).ToList();
    }

    private static HashSet<string> BuildExistingFilePathSet(List<Dictionary<string, object>> files)
    {
        return new HashSet<string>(
            files.Select(GetFilePath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path!),
            StringComparer.OrdinalIgnoreCase);
    }

    private static void AttachPrimaryLyricsFiles(
        string baseName,
        List<string> searchDirs,
        List<Dictionary<string, object>> files,
        HashSet<string> existing,
        LyricPresenceState lyricPresence)
    {
        foreach (var extension in new[] { ".lrc", TtmlExtension, ".txt" })
        {
            var lyricIo = FindPrimaryLyricPath(baseName, extension, searchDirs);
            if (string.IsNullOrWhiteSpace(lyricIo))
            {
                continue;
            }

            AddLyricsFile(files, existing, lyricIo, lyricPresence);
        }
    }

    private static string? FindPrimaryLyricPath(string baseName, string extension, List<string> searchDirs)
    {
        foreach (var lyricIo in searchDirs
            .Select(searchDir => Path.Join(searchDir, baseName + extension))
            .Where(System.IO.File.Exists))
        {
            return lyricIo;
        }

        return null;
    }

    private static void AttachLyricsByTitle(
        Dictionary<string, object> payload,
        List<string> searchDirs,
        List<Dictionary<string, object>> files,
        HashSet<string> existing,
        LyricPresenceState lyricPresence)
    {
        foreach (var searchDir in searchDirs)
        {
            TryAttachLyricsByTitle(payload, files, searchDir, existing, lyricPresence);
        }
    }

    private static void AttachSingleLyrics(
        List<string> searchDirs,
        List<Dictionary<string, object>> files,
        HashSet<string> existing,
        LyricPresenceState lyricPresence)
    {
        foreach (var searchDir in searchDirs)
        {
            TryAttachSingleLyrics(searchDir, files, existing, lyricPresence);
        }
    }

    private static void ApplyLyricsStatus(Dictionary<string, object> payload, LyricPresenceState lyricPresence)
    {
        if (payload.ContainsKey(LyricsStatusField))
        {
            return;
        }

        if (lyricPresence.HasTtml)
        {
            payload[LyricsStatusField] = "time-synced";
            return;
        }

        if (lyricPresence.HasLrc)
        {
            payload[LyricsStatusField] = "synced";
            return;
        }

        if (lyricPresence.HasTxt)
        {
            payload[LyricsStatusField] = "unsynced";
        }
    }

    private sealed class LyricPresenceState
    {
        public bool HasLrc { get; set; }
        public bool HasTtml { get; set; }
        public bool HasTxt { get; set; }
        public bool HasAny => HasLrc || HasTtml || HasTxt;
    }

    private static string? GetPayloadString(Dictionary<string, object> payload, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!payload.TryGetValue(key, out var value) || value is null)
            {
                continue;
            }

            var normalized = NormalizePayloadValue(value);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }
        }

        return null;
    }

    private static void AddLyricsFile(
        List<Dictionary<string, object>> files,
        HashSet<string> existing,
        string lyricIo,
        LyricPresenceState lyricPresence)
    {
        var displayPath = DownloadPathResolver.NormalizeDisplayPath(lyricIo);
        if (!existing.Contains(displayPath))
        {
            files.Add(new Dictionary<string, object> { ["path"] = displayPath });
            existing.Add(displayPath);
        }

        var ext = Path.GetExtension(lyricIo);
        if (string.Equals(ext, ".lrc", StringComparison.OrdinalIgnoreCase))
        {
            lyricPresence.HasLrc = true;
        }
        else if (string.Equals(ext, TtmlExtension, StringComparison.OrdinalIgnoreCase))
        {
            lyricPresence.HasTtml = true;
        }
        else if (string.Equals(ext, ".txt", StringComparison.OrdinalIgnoreCase))
        {
            lyricPresence.HasTxt = true;
        }
    }

    private static void TryAttachLyricsByTitle(
        Dictionary<string, object> payload,
        List<Dictionary<string, object>> files,
        string dir,
        HashSet<string> existing,
        LyricPresenceState lyricPresence)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }

        if (!payload.TryGetValue("title", out var titleRaw) || titleRaw is null)
        {
            return;
        }

        var title = NormalizeFileToken(titleRaw.ToString() ?? string.Empty);
        if (string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        var artistToken = NormalizeFileToken(GetPayloadString(payload, ArtistKey, "Artist") ?? string.Empty);

        var candidates = Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => path.EndsWith(".lrc", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(TtmlExtension, StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));

        foreach (var lyricIo in candidates)
        {
            var fileToken = NormalizeFileToken(Path.GetFileNameWithoutExtension(lyricIo));
            if (string.IsNullOrWhiteSpace(fileToken) || !fileToken.Contains(title, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(artistToken) && !fileToken.Contains(artistToken, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            AddLyricsFile(files, existing, lyricIo, lyricPresence);
        }
    }

    private static void TryAttachSingleLyrics(
        string dir,
        List<Dictionary<string, object>> files,
        HashSet<string> existing,
        LyricPresenceState lyricPresence)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }

        var candidates = Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => path.EndsWith(".lrc", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(TtmlExtension, StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count != 1)
        {
            return;
        }

        AddLyricsFile(files, existing, candidates[0], lyricPresence);
    }

    private static string NormalizeFileToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var chars = value.Where(ch => char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch)).ToArray();
        return new string(chars).Trim().ToLowerInvariant();
    }

    private static List<Dictionary<string, object>> ExtractFiles(Dictionary<string, object> payload)
    {
        if (!payload.TryGetValue(FilesField, out var raw))
        {
            return new List<Dictionary<string, object>>();
        }

        if (raw is List<Dictionary<string, object>> list)
        {
            return list;
        }

        if (raw is JsonElement element && element.ValueKind == JsonValueKind.Array)
        {
            return ExtractFilesFromJsonArray(element);
        }

        if (raw is IEnumerable<object> objects)
        {
            return ExtractFilesFromObjects(objects);
        }

        return new List<Dictionary<string, object>>();
    }

    private static List<Dictionary<string, object>> ExtractFilesFromJsonArray(JsonElement element)
    {
        var parsed = new List<Dictionary<string, object>>();
        foreach (var item in element.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object))
        {
            var parsedDict = JsonSerializer.Deserialize<Dictionary<string, object>>(item.GetRawText());
            if (parsedDict != null)
            {
                parsed.Add(parsedDict);
            }
        }

        return parsed;
    }

    private static List<Dictionary<string, object>> ExtractFilesFromObjects(IEnumerable<object> objects)
    {
        var parsed = new List<Dictionary<string, object>>();
        foreach (var item in objects)
        {
            if (item is Dictionary<string, object> dict)
            {
                parsed.Add(dict);
                continue;
            }

            if (TryDeserializeFileDictionary(item, out var parsedDict))
            {
                parsed.Add(parsedDict!);
            }
        }

        return parsed;
    }

    private static bool TryDeserializeFileDictionary(object item, out Dictionary<string, object>? parsedDict)
    {
        parsedDict = null;
        if (item is not JsonElement objEl || objEl.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        parsedDict = JsonSerializer.Deserialize<Dictionary<string, object>>(objEl.GetRawText());
        return parsedDict != null;
    }

    private static string? ResolveAudioPath(Dictionary<string, object> payload, List<Dictionary<string, object>> files)
    {
        foreach (var path in files.Select(GetFilePath))
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }
            var ext = Path.GetExtension(path);
            if (!string.Equals(ext, ".lrc", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(ext, TtmlExtension, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(ext, ".txt", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
        }

        if (payload.TryGetValue("filePath", out var filePath) && filePath is string filePathStr && !string.IsNullOrWhiteSpace(filePathStr))
        {
            return filePathStr;
        }

        if (payload.TryGetValue("FilePath", out var filePathUpper) && filePathUpper is string filePathUpperStr && !string.IsNullOrWhiteSpace(filePathUpperStr))
        {
            return filePathUpperStr;
        }

        return null;
    }

    private static string? GetFilePath(Dictionary<string, object> file)
    {
        if (file.TryGetValue("path", out var path) && path is string pathStr)
        {
            return pathStr;
        }
        if (file.TryGetValue("Path", out var pathUpper) && pathUpper is string pathUpperStr)
        {
            return pathUpperStr;
        }
        return null;
    }

    private JsonResult ErrorJson(string message)
    {
        return Json(new { success = false, error = message });
    }
}

internal enum ActivityStatus
{
    Queued,
    Running,
    Paused,
    Retrying,
    Complete,
    Failed,
    Canceled
}

internal sealed record CachedQueuePayload(DateTimeOffset CachedAtUtc, Dictionary<string, object> Payload);

public sealed class CancelDownloadRequest
{
    [Required(AllowEmptyStrings = false)]
    public string Uuid { get; set; } = "";
}
