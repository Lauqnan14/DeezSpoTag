using System.Text.Json;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Apple;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System.Linq;
using Microsoft.AspNetCore.Authorization;
using GwTrack = DeezSpoTag.Core.Models.Deezer.GwTrack;

namespace DeezSpoTag.Web.Controllers.Api
{
    [ApiController]
    [Route("api/deezer/download")]
[Authorize]
    public sealed class DeezerDownloadApiController : ControllerBase
    {
        private const string DeezerSource = "deezer";
        private const string TrackType = "track";
        private const string DeezerDomain = "deezer.com";
        private readonly ILogger<DeezerDownloadApiController> _logger;
        private readonly DownloadIntentService _intentService;
        private readonly DeezerClient _deezerClient;
        private readonly IDownloadIntentBackgroundQueue _backgroundQueue;
        private readonly DownloadOrchestrationService _orchestrationService;
        private readonly DownloadQueueRepository _queueRepository;
        private readonly DeezSpoTagApp _deezSpoTagApp;
        private readonly DeezSpoTagSettingsService _settingsService;
        private readonly DeezerGatewayService _deezerGatewayService;
        private readonly BoomplayMetadataService _boomplayMetadataService;

        public DeezerDownloadApiController(
            ILogger<DeezerDownloadApiController> logger,
            DownloadIntentService intentService,
            DeezerClient deezerClient,
            IDownloadIntentBackgroundQueue backgroundQueue,
            DeezerDownloadCollaborators collaborators)
        {
            _logger = logger;
            _intentService = intentService;
            _deezerClient = deezerClient;
            _backgroundQueue = backgroundQueue;
            _orchestrationService = collaborators.OrchestrationService;
            _queueRepository = collaborators.QueueRepository;
            _deezSpoTagApp = collaborators.DeezSpoTagApp;
            _settingsService = collaborators.SettingsService;
            _deezerGatewayService = collaborators.DeezerGatewayService;
            _boomplayMetadataService = collaborators.BoomplayMetadataService;
        }

        [HttpPost("add-with-settings")]
        public async Task<IActionResult> AddWithSettings([FromBody] JsonElement payload, CancellationToken cancellationToken)
        {
            var downloadGate = await _orchestrationService.EvaluateDownloadGateAsync(cancellationToken);
            if (!downloadGate.Allowed)
            {
                    return StatusCode(409, new { error = string.IsNullOrWhiteSpace(downloadGate.Message) ? "Downloads paused while AutoTag is running." : downloadGate.Message });
            }

            var request = ParseAddWithSettingsRequest(payload, out var invalidResult);
            if (invalidResult != null)
            {
                return invalidResult;
            }

            var accumulator = new AddWithSettingsAccumulator();
            foreach (var url in request!.Urls)
            {
                await ProcessAddWithSettingsUrlAsync(url, request, accumulator, cancellationToken);
            }

            return await BuildAddWithSettingsResultAsync(request.Urls, accumulator, cancellationToken);
        }

        private AddWithSettingsRequest? ParseAddWithSettingsRequest(JsonElement payload, out IActionResult? invalidResult)
        {
            if (payload.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                invalidResult = BadRequest(new { error = "Payload is required." });
                return null;
            }

            var urls = ExtractUrls(payload);
            if (urls.Count == 0)
            {
                invalidResult = BadRequest(new { error = "No URLs supplied." });
                return null;
            }

            invalidResult = null;
            return new AddWithSettingsRequest(
                urls,
                ParseDestinationFolderId(payload),
                ParseMetadata(payload),
                _deezerClient.LoggedIn,
                _settingsService.LoadSettings());
        }

        private static List<string> ExtractUrls(JsonElement payload)
        {
            var urls = new List<string>();
            if (payload.TryGetProperty("url", out var urlElement))
            {
                var url = urlElement.GetString();
                if (!string.IsNullOrWhiteSpace(url))
                {
                    urls.Add(url);
                }
            }

            if (payload.TryGetProperty("urls", out var urlsElement) && urlsElement.ValueKind == JsonValueKind.Array)
            {
                urls.AddRange(urlsElement.EnumerateArray()
                    .Where(static entry => entry.ValueKind == JsonValueKind.String)
                    .Select(static entry => entry.GetString())
                    .Where(static url => !string.IsNullOrWhiteSpace(url))!);
            }

            return urls;
        }

        private static long? ParseDestinationFolderId(JsonElement payload)
        {
            if (!payload.TryGetProperty("destinationFolderId", out var destinationElement))
            {
                return null;
            }

            if (destinationElement.ValueKind == JsonValueKind.Number
                && destinationElement.TryGetInt64(out var destinationValue))
            {
                return destinationValue;
            }

            if (destinationElement.ValueKind == JsonValueKind.String
                && long.TryParse(destinationElement.GetString(), out var destinationValueFromString))
            {
                return destinationValueFromString;
            }

            return null;
        }

        private static DownloadIntent? ParseMetadata(JsonElement payload)
        {
            if (payload.TryGetProperty("metadata", out var metadataElement)
                && metadataElement.ValueKind == JsonValueKind.Object)
            {
                return ParseInputMetadata(metadataElement);
            }

            return null;
        }

        private async Task ProcessAddWithSettingsUrlAsync(
            string url,
            AddWithSettingsRequest request,
            AddWithSettingsAccumulator accumulator,
            CancellationToken cancellationToken)
        {
            var inferredSourceService = InferSourceService(url);
            var bypassDirectDeezerRouting = ShouldBypassDirectDeezerRouting(url, inferredSourceService, request.InputMetadata);
            if (await TryQueueDirectDeezerPodcastUrlAsync(url, request, bypassDirectDeezerRouting, accumulator))
            {
                return;
            }

            var isBatch = !bypassDirectDeezerRouting && IsBatchSourceUrl(url);
            var resolvedIntents = await ResolveIntentsForUrlAsync(
                url,
                request,
                inferredSourceService,
                bypassDirectDeezerRouting,
                cancellationToken);
            if (resolvedIntents.SkipUrl)
            {
                accumulator.Skipped++;
                accumulator.SetLastErrorIfEmpty(resolvedIntents.ErrorMessage);
                return;
            }

            foreach (var intent in resolvedIntents.Intents)
            {
                await EnqueueResolvedIntentAsync(
                    intent,
                    isBatch,
                    request.DestinationFolderId,
                    accumulator,
                    cancellationToken);
            }
        }

        private static bool ShouldBypassDirectDeezerRouting(string url, string inferredSourceService, DownloadIntent? inputMetadata)
        {
            var isDirectDeezerPodcastUrl = IsDeezerShowUrl(url) || IsDeezerEpisodeUrl(url);
            var hasNonDeezerSourceOverride = !string.IsNullOrWhiteSpace(inputMetadata?.SourceService)
                && !string.Equals(inputMetadata!.SourceService, DeezerSource, StringComparison.OrdinalIgnoreCase);
            var hasNonDeezerEngineOverride = !string.IsNullOrWhiteSpace(inputMetadata?.PreferredEngine)
                && !string.Equals(inputMetadata!.PreferredEngine, DeezerSource, StringComparison.OrdinalIgnoreCase);

            return string.Equals(inferredSourceService, DeezerSource, StringComparison.OrdinalIgnoreCase)
                && (hasNonDeezerSourceOverride || hasNonDeezerEngineOverride)
                && !isDirectDeezerPodcastUrl;
        }

        private async Task<bool> TryQueueDirectDeezerPodcastUrlAsync(
            string url,
            AddWithSettingsRequest request,
            bool bypassDirectDeezerRouting,
            AddWithSettingsAccumulator accumulator)
        {
            if (bypassDirectDeezerRouting)
            {
                return false;
            }

            if (IsDeezerShowUrl(url))
            {
                if (!request.DeezerLoggedIn)
                {
                    accumulator.Skipped++;
                    accumulator.SetLastErrorIfEmpty("Deezer login required for Deezer show downloads.");
                    return true;
                }

                var showEpisodeUrls = await GetShowEpisodeUrlsAsync(url);
                if (showEpisodeUrls.Count == 0)
                {
                    accumulator.Skipped++;
                    accumulator.SetLastErrorIfEmpty("No episodes found for show.");
                    return true;
                }

                var queuedCount = await QueueDirectDeezerUrlsAsync(showEpisodeUrls, request, accumulator);
                if (queuedCount == 0)
                {
                    accumulator.Skipped++;
                    accumulator.SetLastErrorIfEmpty("Show episodes could not be queued.");
                }

                return true;
            }

            if (!IsDeezerEpisodeUrl(url))
            {
                return false;
            }

            if (!request.DeezerLoggedIn)
            {
                accumulator.Skipped++;
                accumulator.SetLastErrorIfEmpty("Deezer login required for Deezer episode downloads.");
                return true;
            }

            var queuedItems = await QueueDirectDeezerUrlsAsync(new[] { url }, request, accumulator);
            if (queuedItems == 0)
            {
                accumulator.Skipped++;
                accumulator.SetLastErrorIfEmpty("Episode could not be queued.");
            }

            return true;
        }

        private async Task<int> QueueDirectDeezerUrlsAsync(
            IReadOnlyCollection<string> urls,
            AddWithSettingsRequest request,
            AddWithSettingsAccumulator accumulator)
        {
            var resolvedBitrate = DownloadSourceOrder.ResolveDeezerBitrate(request.Settings, 0);
            foreach (var url in urls)
            {
                var intent = BuildFallbackIntent(url, request, DeezerSource);
                intent.SourceService = DeezerSource;
                intent.PreferredEngine = DeezerSource;
                intent.Quality = resolvedBitrate.ToString();
                intent.DestinationFolderId = request.DestinationFolderId;
                var result = await _intentService.EnqueueAsync(intent, HttpContext.RequestAborted);
                accumulator.Queued.AddRange(result.Queued);
                if (!result.Success && !string.IsNullOrWhiteSpace(result.Message))
                {
                    accumulator.SetLastErrorIfEmpty(result.Message);
                }
            }
            accumulator.Engine = DeezerSource;
            return accumulator.Queued.Count;
        }

        private async Task<ResolvedIntents> ResolveIntentsForUrlAsync(
            string url,
            AddWithSettingsRequest request,
            string inferredSourceService,
            bool bypassDirectDeezerRouting,
            CancellationToken cancellationToken)
        {
            var expandedIntents = bypassDirectDeezerRouting
                ? new List<DownloadIntent>()
                : await ExpandKnownUrlAsync(url, cancellationToken);
            var knownExpandableUrl = !bypassDirectDeezerRouting && IsKnownExpandableUrl(url);
            if (expandedIntents.Count == 0)
            {
                if (knownExpandableUrl)
                {
                    return ResolvedIntents.Skip("No downloadable tracks found for URL.");
                }

                var fallbackIntent = BuildFallbackIntent(url, request, inferredSourceService);
                expandedIntents.Add(fallbackIntent);
                return ResolvedIntents.From(expandedIntents);
            }

            foreach (var expandedIntent in expandedIntents)
            {
                ApplyInputMetadata(expandedIntent, request.InputMetadata);
            }

            return ResolvedIntents.From(expandedIntents);
        }

        private static DownloadIntent BuildFallbackIntent(
            string url,
            AddWithSettingsRequest request,
            string inferredSourceService)
        {
            var fallbackSourceService = !string.IsNullOrWhiteSpace(request.InputMetadata?.SourceService)
                ? request.InputMetadata!.SourceService
                : inferredSourceService;
            var fallbackSourceUrl = !string.IsNullOrWhiteSpace(request.InputMetadata?.SourceUrl)
                ? request.InputMetadata!.SourceUrl
                : url;
            var fallbackIntent = new DownloadIntent
            {
                SourceService = fallbackSourceService,
                SourceUrl = fallbackSourceUrl,
                DestinationFolderId = request.DestinationFolderId
            };
            ApplyInputMetadata(fallbackIntent, request.InputMetadata);
            return fallbackIntent;
        }

        private async Task EnqueueResolvedIntentAsync(
            DownloadIntent intent,
            bool isBatch,
            long? destinationFolderId,
            AddWithSettingsAccumulator accumulator,
            CancellationToken cancellationToken)
        {
            intent.DestinationFolderId ??= destinationFolderId;
            var requiresAutoTagProfile = RequiresAutoTagDefaults(intent.ContentType, intent.SourceUrl);

            if (string.IsNullOrWhiteSpace(intent.Isrc) && isBatch)
            {
                if (_backgroundQueue.Enqueue(intent))
                {
                    accumulator.Deferred++;
                }
                else
                {
                    accumulator.Skipped++;
                }
                return;
            }

            var result = await _intentService.EnqueueAsync(intent, cancellationToken, preferIsrcOnly: isBatch);
            if (!string.IsNullOrWhiteSpace(result.Engine))
            {
                accumulator.Engine = result.Engine;
            }

            if (result.Success && result.Queued.Count > 0)
            {
                accumulator.Queued.AddRange(result.Queued);
                if (requiresAutoTagProfile)
                {
                    accumulator.QueuedMusicItems = true;
                }
                return;
            }

            accumulator.Skipped += Math.Max(result.Skipped, 1);
            if (result.SkipReasonCodes.Count > 0)
            {
                accumulator.ReasonCodes.AddRange(result.SkipReasonCodes);
            }

            if (!string.IsNullOrWhiteSpace(result.Message))
            {
                accumulator.LastError = result.Message;
            }
        }

        private async Task<IActionResult> BuildAddWithSettingsResultAsync(
            IReadOnlyCollection<string> urls,
            AddWithSettingsAccumulator accumulator,
            CancellationToken cancellationToken)
        {
            if (accumulator.Queued.Count == 0 && accumulator.Deferred > 0)
            {
                _logger.LogInformation(
                    "Deezer download deferred: queued 0 deferred {Deferred} skipped {Skipped}",
                    accumulator.Deferred,
                    accumulator.Skipped);
                return Ok(new
                {
                    success = true,
                    deferred = true,
                    deferredCount = accumulator.Deferred,
                    downloadIds = Array.Empty<string>(),
                    linkType = accumulator.Engine,
                    message = "Queued remaining tracks for background matching."
                });
            }

            if (accumulator.Queued.Count == 0)
            {
                var alreadyQueued = await FindAlreadyQueuedDownloadIdsAsync(urls, cancellationToken);
                if (alreadyQueued.Count > 0)
                {
                    return Ok(new
                    {
                        success = true,
                        alreadyQueued = true,
                        downloadIds = alreadyQueued,
                        linkType = accumulator.Engine,
                        message = "Item already queued."
                    });
                }

                _logger.LogInformation("Deezer download mapped via intent; nothing queued.");
                return BadRequest(new
                {
                    success = false,
                    message = string.IsNullOrWhiteSpace(accumulator.LastError) ? "Nothing queued." : accumulator.LastError,
                    reasonCodes = accumulator.ReasonCodes
                });
            }

            _logger.LogInformation(
                "Deezer download mapped via intent: queued {Queued} deferred {Deferred} skipped {Skipped}",
                accumulator.Queued.Count,
                accumulator.Deferred,
                accumulator.Skipped);
            if (accumulator.QueuedMusicItems)
            {
                _orchestrationService.MarkDownloadQueued();
            }

            return Ok(new
            {
                success = true,
                downloadId = accumulator.Queued[0],
                downloadIds = accumulator.Queued,
                linkType = accumulator.Engine,
                deferredCount = accumulator.Deferred,
                reasonCodes = accumulator.ReasonCodes
            });
        }

        private async Task<List<string>> FindAlreadyQueuedDownloadIdsAsync(
            IReadOnlyCollection<string> urls,
            CancellationToken cancellationToken)
        {
            var alreadyQueued = new List<string>();
            foreach (var parsedId in urls
                         .Select(static url =>
                         {
                             if (TryParseDeezerUrl(url, out var parsedType, out var trackId)
                                 && string.Equals(parsedType, TrackType, StringComparison.OrdinalIgnoreCase))
                             {
                                 return trackId;
                             }

                             return null;
                         })
                         .Where(static trackId => !string.IsNullOrWhiteSpace(trackId)))
            {
                var existing = await _queueRepository.GetByDeezerTrackIdAsync(DeezerSource, parsedId!, cancellationToken);
                if (existing != null && !string.IsNullOrWhiteSpace(existing.QueueUuid))
                {
                    alreadyQueued.Add(existing.QueueUuid);
                }
            }

            return alreadyQueued;
        }

        private sealed record AddWithSettingsRequest(
            IReadOnlyCollection<string> Urls,
            long? DestinationFolderId,
            DownloadIntent? InputMetadata,
            bool DeezerLoggedIn,
            DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings Settings);

        private sealed class AddWithSettingsAccumulator
        {
            public List<string> Queued { get; } = new();
            public string Engine { get; set; } = string.Empty;
            public int Skipped { get; set; }
            public int Deferred { get; set; }
            public string? LastError { get; set; }
            public List<string> ReasonCodes { get; } = new();
            public bool QueuedMusicItems { get; set; }

            public void SetLastErrorIfEmpty(string? message)
            {
                if (!string.IsNullOrWhiteSpace(message))
                {
                    LastError ??= message;
                }
            }
        }

        private sealed record ResolvedIntents(
            List<DownloadIntent> Intents,
            bool SkipUrl,
            string? ErrorMessage)
        {
            public static ResolvedIntents From(List<DownloadIntent> intents) => new(intents, false, null);

            public static ResolvedIntents Skip(string errorMessage) => new(new List<DownloadIntent>(), true, errorMessage);
        }

        [HttpPost("pause/{uuid}")]
        public async Task<IActionResult> Pause(string uuid, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(uuid))
            {
                return BadRequest(new { error = "UUID is required." });
            }

            var item = await _queueRepository.GetByUuidAsync(uuid, cancellationToken);
            if (item == null)
            {
                return NotFound(new { error = "Download not found." });
            }

            if (string.Equals(item.Status, "running", StringComparison.OrdinalIgnoreCase))
            {
                await _deezSpoTagApp.PauseQueueAsync();
            }
            else if (string.Equals(item.Status, "queued", StringComparison.OrdinalIgnoreCase))
            {
                await _queueRepository.UpdateStatusAsync(uuid, "paused", cancellationToken: cancellationToken);
            }

            return Ok(new { success = true });
        }

        [HttpPost("resume/{uuid}")]
        public async Task<IActionResult> Resume(string uuid, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(uuid))
            {
                return BadRequest(new { error = "UUID is required." });
            }

            var item = await _queueRepository.GetByUuidAsync(uuid, cancellationToken);
            if (item == null)
            {
                return NotFound(new { error = "Download not found." });
            }

            if (string.Equals(item.Status, "paused", StringComparison.OrdinalIgnoreCase))
            {
                await _queueRepository.UpdateStatusAsync(uuid, "queued", error: null, cancellationToken: cancellationToken);
            }

            await _deezSpoTagApp.EnsureQueueProcessorRunningAsync();
            return Ok(new { success = true });
        }

        [HttpPost("cancel/{uuid}")]
        public async Task<IActionResult> Cancel(string uuid, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(uuid))
            {
                return BadRequest(new { error = "UUID is required." });
            }

            await _deezSpoTagApp.CancelDownloadAsync(uuid);
            return Ok(new { success = true });
        }

        [HttpGet("queue/status")]
        public async Task<IActionResult> GetQueueStatus()
        {
            try
            {
                var queueData = await _deezSpoTagApp.GetQueueAsync();
                return Ok(queueData);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error getting queue status");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpGet("queue/active")]
        public async Task<IActionResult> GetActiveDownloads()
        {
            try
            {
                var queueData = await _deezSpoTagApp.GetQueueAsync();
                if (!queueData.TryGetValue("queue", out var queueObj)
                    || queueObj is not Dictionary<string, object> queue)
                {
                    return Ok(queueData);
                }

                var activeQueue = new Dictionary<string, object>();
                foreach (var entry in queue)
                {
                    if (entry.Value is not Dictionary<string, object> payload)
                    {
                        continue;
                    }

                    if (!payload.TryGetValue("status", out var statusObj))
                    {
                        continue;
                    }

                    var status = statusObj?.ToString() ?? string.Empty;
                    if (string.Equals(status, "inQueue", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(status, "downloading", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(status, "paused", StringComparison.OrdinalIgnoreCase))
                    {
                        activeQueue[entry.Key] = payload;
                    }
                }

                var filtered = new Dictionary<string, object>(queueData)
                {
                    ["queue"] = activeQueue
                };

                if (queueData.TryGetValue("queueOrder", out var orderObj)
                    && orderObj is List<string> order)
                {
                    filtered["queueOrder"] = order.Where(id => activeQueue.ContainsKey(id)).ToList();
                }

                return Ok(filtered);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error getting active downloads");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpPost("parse")]
        public async Task<IActionResult> ParseUrl([FromBody] ParseUrlRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Url))
            {
                return BadRequest("URL is required");
            }

            try
            {
                var settings = _settingsService.LoadSettings();
                var resolvedBitrate = DownloadSourceOrder.ResolveDeezerBitrate(settings, DownloadSourceOrder.DeezerFlac);
                var inferredContentType = IsDeezerEpisodeUrl(request.Url) || IsDeezerShowUrl(request.Url)
                    ? DownloadContentTypes.Podcast
                    : "music";

                var intent = new DownloadIntent
                {
                    SourceService = DeezerSource,
                    SourceUrl = request.Url,
                    PreferredEngine = DeezerSource,
                    Quality = resolvedBitrate.ToString(),
                    ContentType = inferredContentType
                };

                var intentResult = await _intentService.EnqueueAsync(intent, HttpContext.RequestAborted);
                if (!intentResult.Success || intentResult.Queued.Count == 0)
                {
                    return BadRequest("Invalid or unsupported URL");
                }

                var linkId = intentResult.Queued[0];
                var linkType = "unknown";
                if (TryParseDeezerUrl(request.Url, out var parsedType, out _))
                {
                    linkType = parsedType;
                }

                if (!string.IsNullOrWhiteSpace(linkId))
                {
                    await _deezSpoTagApp.CancelDownloadAsync(linkId);
                }

                return Ok(new ParseUrlResponse
                {
                    OriginalUrl = request.Url,
                    ParsedUrl = request.Url,
                    LinkType = linkType,
                    LinkId = linkId,
                    IsSupported = true
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error parsing URL: Url");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        private static bool RequiresAutoTagDefaults(string? contentType, string? sourceUrl)
        {
            if (AppleVideoClassifier.IsVideo(sourceUrl, contentType: contentType))
            {
                return false;
            }

            var normalizedContentType = NormalizeContentType(contentType);
            if (string.Equals(normalizedContentType, DownloadContentTypes.Video, StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedContentType, DownloadContentTypes.Podcast, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (IsPodcastSourceUrl(sourceUrl))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(sourceUrl)
                && (IsDeezerEpisodeUrl(sourceUrl) || IsDeezerShowUrl(sourceUrl)))
            {
                return false;
            }

            return true;
        }

        private static bool IsPodcastSourceUrl(string? sourceUrl)
        {
            if (string.IsNullOrWhiteSpace(sourceUrl))
            {
                return false;
            }

            return sourceUrl.Contains("/episode/", StringComparison.OrdinalIgnoreCase)
                || sourceUrl.Contains("/podcast/", StringComparison.OrdinalIgnoreCase)
                || sourceUrl.Contains("/podcasts/", StringComparison.OrdinalIgnoreCase);
        }

        private static string? NormalizeContentType(string? contentType)
        {
            var normalized = contentType?.Trim().ToLowerInvariant();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        private static string InferSourceService(string url)
        {
            if (url.Contains(DeezerDomain, StringComparison.OrdinalIgnoreCase))
            {
                return DeezerSource;
            }

            if (BoomplayMetadataService.IsBoomplayUrl(url))
            {
                return "boomplay";
            }

            if (url.Contains("music.apple.com", StringComparison.OrdinalIgnoreCase))
            {
                return "apple";
            }

            if (url.Contains("tidal.com", StringComparison.OrdinalIgnoreCase))
            {
                return "tidal";
            }

            if (url.Contains("qobuz.com", StringComparison.OrdinalIgnoreCase))
            {
                return "qobuz";
            }

            if (url.Contains("amazon.", StringComparison.OrdinalIgnoreCase)
                || url.Contains("music.amazon", StringComparison.OrdinalIgnoreCase))
            {
                return "amazon";
            }

            return string.Empty;
        }

        private static bool IsBatchSourceUrl(string url)
        {
            if (TryParseDeezerUrl(url, out var deezerType, out _))
            {
                return !string.Equals(deezerType, TrackType, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(deezerType, "episode", StringComparison.OrdinalIgnoreCase);
            }

            if (BoomplayMetadataService.TryParseBoomplayUrl(url, out var boomplayType, out _))
            {
                return !string.Equals(boomplayType, TrackType, StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        private async Task<List<DownloadIntent>> ExpandKnownUrlAsync(string url, CancellationToken cancellationToken)
        {
            if (TryParseDeezerUrl(url, out _, out _))
            {
                return await ExpandDeezerUrlAsync(url);
            }

            if (BoomplayMetadataService.TryParseBoomplayUrl(url, out _, out _))
            {
                return await ExpandBoomplayUrlAsync(url, cancellationToken);
            }

            return new List<DownloadIntent>();
        }

        private async Task<List<DownloadIntent>> ExpandDeezerUrlAsync(string url)
        {
            if (!TryParseDeezerUrl(url, out var type, out var id))
            {
                return new List<DownloadIntent>();
            }

            switch (type)
            {
                case TrackType:
                    return await BuildTrackIntentAsync(id, url);
                case "album":
                    return await BuildAlbumIntentsAsync(id);
                case "playlist":
                    return await BuildPlaylistIntentsAsync(id);
                case "artist":
                    return await BuildArtistTopTrackIntentsAsync(id);
                default:
                    return new List<DownloadIntent>();
            }
        }

        private static bool IsKnownExpandableUrl(string url)
        {
            if (TryParseDeezerUrl(url, out _, out _))
            {
                return true;
            }

            return BoomplayMetadataService.TryParseBoomplayUrl(url, out _, out _);
        }

        private async Task<List<DownloadIntent>> ExpandBoomplayUrlAsync(string url, CancellationToken cancellationToken)
        {
            if (!BoomplayMetadataService.TryParseBoomplayUrl(url, out var type, out var id))
            {
                return new List<DownloadIntent>();
            }

            var normalizedType = type.Trim().ToLowerInvariant();
            return normalizedType switch
            {
                TrackType => await ExpandBoomplayTrackAsync(id, cancellationToken),
                "playlist" => await ExpandBoomplayPlaylistAsync(id, cancellationToken),
                "trending" => await ExpandBoomplayTrendingAsync(cancellationToken),
                _ => new List<DownloadIntent>()
            };
        }

        private async Task<List<DownloadIntent>> ExpandBoomplayTrackAsync(string id, CancellationToken cancellationToken)
        {
            var track = await _boomplayMetadataService.GetSongAsync(id, cancellationToken);
            if (track == null)
            {
                return new List<DownloadIntent>();
            }

            return new List<DownloadIntent> { BuildIntentFromBoomplayTrack(track, track.Url) };
        }

        private async Task<List<DownloadIntent>> ExpandBoomplayPlaylistAsync(string id, CancellationToken cancellationToken)
        {
            var playlist = await _boomplayMetadataService.GetPlaylistAsync(id, includeTracks: true, cancellationToken);
            if (playlist == null)
            {
                return new List<DownloadIntent>();
            }

            var tracks = playlist.Tracks.Count > 0
                ? playlist.Tracks
                : (await _boomplayMetadataService.GetSongsAsync(playlist.TrackIds, cancellationToken)).ToList();
            return BuildBoomplayIntents(tracks);
        }

        private async Task<List<DownloadIntent>> ExpandBoomplayTrendingAsync(CancellationToken cancellationToken)
        {
            var trending = await _boomplayMetadataService.GetTrendingSongsAsync(includeTracks: true, cancellationToken);
            if (trending == null)
            {
                return new List<DownloadIntent>();
            }

            var tracks = trending.Tracks.Count > 0
                ? trending.Tracks
                : (await _boomplayMetadataService.GetSongsAsync(trending.TrackIds, cancellationToken)).ToList();
            return BuildBoomplayIntents(tracks);
        }

        private static List<DownloadIntent> BuildBoomplayIntents(IReadOnlyCollection<BoomplayTrackMetadata> tracks)
        {
            var intents = new List<DownloadIntent>(tracks.Count);
            foreach (var track in tracks)
            {
                intents.Add(BuildIntentFromBoomplayTrack(track, track.Url));
            }

            return intents;
        }

        private async Task<List<DownloadIntent>> BuildTrackIntentAsync(
            string trackId,
            string sourceUrl)
        {
            var track = await _deezerClient.GetTrackWithFallbackAsync(trackId);
            if (track == null || track.SngId <= 0)
            {
                return new List<DownloadIntent>();
            }

            var intent = BuildIntentFromTrack(track, sourceUrl);
            return new List<DownloadIntent> { intent };
        }

        private async Task<List<DownloadIntent>> BuildAlbumIntentsAsync(string albumId)
        {
            var tracks = await _deezerClient.GetAlbumTracksAsync(albumId);
            var intents = new List<DownloadIntent>(tracks.Count);
            foreach (var track in tracks)
            {
                if (track == null || track.SngId <= 0)
                {
                    continue;
                }

                var url = $"https://www.deezer.com/track/{track.SngId}";
                intents.Add(BuildIntentFromTrack(track, url));
            }

            return intents;
        }

        private async Task<List<DownloadIntent>> BuildPlaylistIntentsAsync(string playlistId)
        {
            var tracks = await _deezerClient.GetPlaylistTracksAsync(playlistId);
            var intents = new List<DownloadIntent>(tracks.Count);
            foreach (var track in tracks)
            {
                if (track == null || track.SngId <= 0)
                {
                    continue;
                }

                var url = $"https://www.deezer.com/track/{track.SngId}";
                intents.Add(BuildIntentFromTrack(track, url));
            }

            return intents;
        }

        private async Task<List<DownloadIntent>> BuildArtistTopTrackIntentsAsync(string artistId)
        {
            var tracks = await _deezerGatewayService.GetArtistTopTracksAsync(artistId, 100);
            var intents = new List<DownloadIntent>(tracks.Count);
            foreach (var track in tracks)
            {
                if (track == null || track.SngId <= 0)
                {
                    continue;
                }

                var url = $"https://www.deezer.com/track/{track.SngId}";
                intents.Add(BuildIntentFromTrack(track, url));
            }

            return intents;
        }

        private static DownloadIntent BuildIntentFromTrack(GwTrack track, string sourceUrl)
        {
            var position = track.Position > 0 ? track.Position : track.TrackNumber;
            var durationMs = track.Duration > 0 ? track.Duration * 1000 : 0;
            return new DownloadIntent
            {
                SourceService = DeezerSource,
                SourceUrl = sourceUrl,
                Isrc = track.Isrc ?? string.Empty,
                Title = track.SngTitle ?? string.Empty,
                Artist = track.ArtName ?? string.Empty,
                Album = track.AlbTitle ?? string.Empty,
                AlbumArtist = track.ArtName ?? string.Empty,
                Cover = BuildDeezerCoverUrl(track.AlbPicture),
                DurationMs = durationMs,
                Position = position
            };
        }

        private static DownloadIntent BuildIntentFromBoomplayTrack(BoomplayTrackMetadata track, string sourceUrl)
        {
            var genres = track.Genres
                .Where(static genre => !string.IsNullOrWhiteSpace(genre))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var position = track.TrackNumber > 0 ? track.TrackNumber : 0;
            var title = System.Net.WebUtility.HtmlDecode(track.Title ?? string.Empty).Trim();
            var artist = System.Net.WebUtility.HtmlDecode(track.Artist ?? string.Empty).Trim();
            var album = System.Net.WebUtility.HtmlDecode(track.Album ?? string.Empty).Trim();
            var cover = System.Net.WebUtility.HtmlDecode(track.CoverUrl ?? string.Empty).Trim();

            return new DownloadIntent
            {
                SourceService = "boomplay",
                SourceUrl = sourceUrl ?? string.Empty,
                Isrc = track.Isrc ?? string.Empty,
                Title = title,
                Artist = artist,
                Album = album,
                AlbumArtist = artist,
                Cover = cover,
                DurationMs = track.DurationMs > 0 ? track.DurationMs : 0,
                Position = position,
                Genres = genres,
                ReleaseDate = track.ReleaseDate ?? string.Empty,
                TrackNumber = track.TrackNumber > 0 ? track.TrackNumber : 0,
                Url = track.Url ?? string.Empty
            };
        }

        private static string BuildDeezerCoverUrl(string? coverId)
        {
            if (string.IsNullOrWhiteSpace(coverId))
            {
                return string.Empty;
            }

            return $"https://cdns-images.dzcdn.net/images/cover/{coverId}/1000x1000-000000-80-0-0.jpg";
        }

        private static bool TryParseDeezerUrl(string url, out string type, out string id)
        {
            type = string.Empty;
            id = string.Empty;

            if (string.IsNullOrWhiteSpace(url) || !url.Contains(DeezerDomain, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var parts = url.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (var i = 0; i < parts.Length - 1; i++)
            {
                var part = parts[i];
                if (part is "track" or "album" or "playlist" or "artist" or "episode" or "show")
                {
                    var valuePart = parts[i + 1];
                    var separatorIndex = valuePart.IndexOfAny(['?', '#']);
                    var candidate = separatorIndex >= 0
                        ? valuePart[..separatorIndex]
                        : valuePart;
                    if (long.TryParse(candidate, out _))
                    {
                        type = part;
                        id = candidate;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsDeezerEpisodeUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return false;
            }

            if (!uri.Host.Contains(DeezerDomain, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return uri.AbsolutePath.Contains("/episode/", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDeezerShowUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return false;
            }

            if (!uri.Host.Contains(DeezerDomain, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return uri.AbsolutePath.Contains("/show/", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<List<string>> GetShowEpisodeUrlsAsync(string showUrl)
        {
            if (!TryParseDeezerUrl(showUrl, out var type, out var id)
                || !string.Equals(type, "show", StringComparison.OrdinalIgnoreCase))
            {
                return new List<string>();
            }

            try
            {
                var showPage = await _deezerGatewayService.GetShowPageAsync(id);
                var results = showPage["results"] as JObject ?? showPage;
                var episodes = results["EPISODES"] as JObject ?? results["episodes"] as JObject;
                var episodesData = episodes?["data"] as JArray ?? episodes?["DATA"] as JArray;
                if (episodesData == null)
                {
                    return new List<string>();
                }

                var urls = new List<string>();
                foreach (var episodeToken in episodesData)
                {
                    if (episodeToken is not JObject episode)
                    {
                        continue;
                    }

                    var episodeId = episode.Value<string>("EPISODE_ID")
                                   ?? episode.Value<string>("id");
                    if (string.IsNullOrWhiteSpace(episodeId))
                    {
                        continue;
                    }

                    urls.Add($"https://www.deezer.com/episode/{episodeId}");
                }

                return urls;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to expand show episodes for {ShowUrl}", showUrl);
                return new List<string>();
            }
        }

        private static DownloadIntent ParseInputMetadata(JsonElement metadata)
        {
            var genres = new List<string>();
            if (metadata.TryGetProperty("genres", out var genresElement)
                && genresElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in genresElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var value = item.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        genres.Add(value.Trim());
                    }
                }
            }

            return new DownloadIntent
            {
                SourceService = ReadString(metadata, "sourceService") ?? string.Empty,
                SourceUrl = ReadString(metadata, "sourceUrl") ?? string.Empty,
                PreferredEngine = ReadString(metadata, "preferredEngine") ?? string.Empty,
                DeezerId = ReadString(metadata, "deezerId") ?? string.Empty,
                DeezerAlbumId = ReadString(metadata, "deezerAlbumId") ?? string.Empty,
                DeezerArtistId = ReadString(metadata, "deezerArtistId") ?? string.Empty,
                SpotifyId = ReadString(metadata, "spotifyId") ?? string.Empty,
                Isrc = ReadString(metadata, "isrc") ?? string.Empty,
                Title = ReadString(metadata, "title") ?? string.Empty,
                Artist = ReadString(metadata, "artist") ?? string.Empty,
                Album = ReadString(metadata, "album") ?? string.Empty,
                AlbumArtist = ReadString(metadata, "albumArtist") ?? string.Empty,
                Cover = ReadString(metadata, "cover") ?? string.Empty,
                DurationMs = ReadInt(metadata, "durationMs"),
                Position = ReadInt(metadata, "position"),
                Genres = genres,
                Quality = ReadString(metadata, "quality") ?? string.Empty,
                ContentType = ReadString(metadata, "contentType") ?? string.Empty,
                HasAtmos = ReadBool(metadata, "hasAtmos"),
                HasAppleDigitalMaster = ReadBool(metadata, "hasAppleDigitalMaster"),
                AllowQualityUpgrade = ReadBool(metadata, "allowQualityUpgrade")
            };
        }

        private static void ApplyInputMetadata(DownloadIntent target, DownloadIntent? metadata)
        {
            if (metadata == null)
            {
                return;
            }

            CopyIdentityMetadata(target, metadata);
            CopyDescriptiveMetadata(target, metadata);
            CopyNumericMetadata(target, metadata);
            CopyGenresMetadata(target, metadata);
            CopyBehaviorMetadata(target, metadata);
        }

        private static void CopyIdentityMetadata(DownloadIntent target, DownloadIntent metadata)
        {
            SetTextIfEmpty(target.DeezerId, metadata.DeezerId, value => target.DeezerId = value);
            SetTextIfEmpty(target.DeezerAlbumId, metadata.DeezerAlbumId, value => target.DeezerAlbumId = value);
            SetTextIfEmpty(target.DeezerArtistId, metadata.DeezerArtistId, value => target.DeezerArtistId = value);
            SetTextIfEmpty(target.SourceService, metadata.SourceService, value => target.SourceService = value);
            SetTextIfEmpty(target.SourceUrl, metadata.SourceUrl, value => target.SourceUrl = value);
            SetTextIfEmpty(target.SpotifyId, metadata.SpotifyId, value => target.SpotifyId = value);
            SetTextIfEmpty(target.Isrc, metadata.Isrc, value => target.Isrc = value);
        }

        private static void CopyDescriptiveMetadata(DownloadIntent target, DownloadIntent metadata)
        {
            SetTextIfEmpty(target.Title, metadata.Title, value => target.Title = value);
            SetTextIfEmpty(target.Artist, metadata.Artist, value => target.Artist = value);
            SetTextIfEmpty(target.Album, metadata.Album, value => target.Album = value);
            SetTextIfEmpty(target.AlbumArtist, metadata.AlbumArtist, value => target.AlbumArtist = value);
            SetTextIfEmpty(target.Cover, metadata.Cover, value => target.Cover = value);
            SetTextIfEmpty(target.Quality, metadata.Quality, value => target.Quality = value);
            SetTextIfEmpty(target.ContentType, metadata.ContentType, value => target.ContentType = value);
        }

        private static void CopyNumericMetadata(DownloadIntent target, DownloadIntent metadata)
        {
            if (target.DurationMs <= 0 && metadata.DurationMs > 0)
            {
                target.DurationMs = metadata.DurationMs;
            }

            if (target.Position <= 0 && metadata.Position > 0)
            {
                target.Position = metadata.Position;
            }
        }

        private static void CopyGenresMetadata(DownloadIntent target, DownloadIntent metadata)
        {
            if (target.Genres.Count == 0 && metadata.Genres.Count > 0)
            {
                target.Genres = metadata.Genres
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        private static void CopyBehaviorMetadata(DownloadIntent target, DownloadIntent metadata)
        {
            SetTextIfEmpty(target.PreferredEngine, metadata.PreferredEngine, value => target.PreferredEngine = value);

            if (!target.HasAtmos && metadata.HasAtmos)
            {
                target.HasAtmos = true;
            }

            if (!target.HasAppleDigitalMaster && metadata.HasAppleDigitalMaster)
            {
                target.HasAppleDigitalMaster = true;
            }

            if (!target.AllowQualityUpgrade && metadata.AllowQualityUpgrade)
            {
                target.AllowQualityUpgrade = true;
            }
        }

        private static void SetTextIfEmpty(string? currentValue, string? metadataValue, Action<string> applyValue)
        {
            if (string.IsNullOrWhiteSpace(currentValue) && !string.IsNullOrWhiteSpace(metadataValue))
            {
                applyValue(metadataValue);
            }
        }

        private static string? ReadString(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var value))
            {
                return null;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            }

            return null;
        }

        private static int ReadInt(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var value))
            {
                return 0;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numeric))
            {
                return numeric;
            }

            if (value.ValueKind == JsonValueKind.String
                && int.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }

            return 0;
        }

        private static bool ReadBool(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var value))
            {
                return false;
            }

            if (value.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (value.ValueKind == JsonValueKind.False)
            {
                return false;
            }

            if (value.ValueKind == JsonValueKind.String
                && bool.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numeric))
            {
                return numeric != 0;
            }

            return false;
        }
    }

    public sealed class DeezerDownloadCollaborators
    {
        public DeezerDownloadCollaborators(
            DownloadOrchestrationService orchestrationService,
            DownloadQueueRepository queueRepository,
            DeezSpoTagApp deezSpoTagApp,
            DeezSpoTagSettingsService settingsService,
            DeezerGatewayService deezerGatewayService,
            BoomplayMetadataService boomplayMetadataService)
        {
            OrchestrationService = orchestrationService;
            QueueRepository = queueRepository;
            DeezSpoTagApp = deezSpoTagApp;
            SettingsService = settingsService;
            DeezerGatewayService = deezerGatewayService;
            BoomplayMetadataService = boomplayMetadataService;
        }

        public DownloadOrchestrationService OrchestrationService { get; }
        public DownloadQueueRepository QueueRepository { get; }
        public DeezSpoTagApp DeezSpoTagApp { get; }
        public DeezSpoTagSettingsService SettingsService { get; }
        public DeezerGatewayService DeezerGatewayService { get; }
        public BoomplayMetadataService BoomplayMetadataService { get; }
    }

    public sealed class ParseUrlRequest
    {
        public string Url { get; set; } = string.Empty;
    }

    public sealed class ParseUrlResponse
    {
        public string OriginalUrl { get; set; } = string.Empty;
        public string ParsedUrl { get; set; } = string.Empty;
        public string LinkType { get; set; } = string.Empty;
        public string LinkId { get; set; } = string.Empty;
        public bool IsSupported { get; set; }
    }
}
