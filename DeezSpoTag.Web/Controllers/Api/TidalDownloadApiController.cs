using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Tidal;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/tidal/download")]
[Authorize]
[Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryToken]
public sealed class TidalDownloadApiController : ControllerBase
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private readonly DownloadQueueRepository _queueRepository;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly DownloadOrchestrationService _orchestrationService;
    private readonly IDeezSpoTagListener _deezspotagListener;
    private readonly ISpotifyIdResolver _spotifyIdResolver;
    private readonly DeezSpoTag.Services.Library.LibraryRepository _libraryRepository;
    private readonly DownloadDedupeService _dedupeService;
    private readonly TidalDownloadService _tidalDownloadService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TidalDownloadApiController> _logger;

    public TidalDownloadApiController(
        DownloadControllerServices services,
        TidalDownloadService tidalDownloadService,
        ILogger<TidalDownloadApiController> logger)
    {
        _queueRepository = services.QueueRepository;
        _settingsService = services.SettingsService;
        _orchestrationService = services.OrchestrationService;
        _deezspotagListener = services.DeezSpoTagListener;
        _spotifyIdResolver = services.SpotifyIdResolver;
        _libraryRepository = services.LibraryRepository;
        _dedupeService = services.DedupeService;
        _tidalDownloadService = tidalDownloadService;
        _serviceProvider = services.ServiceProvider;
        _logger = logger;
    }

    [HttpGet("public-session")]
    public async Task<IActionResult> GetPublicSession(CancellationToken cancellationToken)
        => Ok(new
        {
            authenticated = await _tidalDownloadService.HasPublicDownloadSessionAsync(cancellationToken)
        });

    [HttpPost("public-session/start")]
    public async Task<IActionResult> StartPublicSession(CancellationToken cancellationToken)
    {
        try
        {
            var verificationUrl = await _tidalDownloadService.BeginPublicDownloadVerificationAsync(
                cancellationToken,
                publicAppBaseUrl: BuildPublicAppBaseUrl());
            return Ok(new
            {
                authenticated = string.IsNullOrWhiteSpace(verificationUrl),
                verificationUrl
            });
        }
        catch (ZarzSessionRateLimitException ex)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                authenticated = false,
                error = ex.Message,
                retryAfterSeconds = ex.RetryAfterSeconds
            });
        }
    }

    private string BuildPublicAppBaseUrl()
        => $"{Request.Scheme}://{Request.Host}{Request.PathBase}".TrimEnd('/');

    [HttpPost("public-session/complete")]
    public async Task<IActionResult> CompletePublicSession(
        [FromBody] TidalPublicDownloadGrantRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Grant))
        {
            return BadRequest(new { error = "Tidal public download verification grant is required." });
        }

        await _tidalDownloadService.CompletePublicDownloadVerificationAsync(request.Grant, cancellationToken);
        return Ok(new { authenticated = true });
    }

    [HttpPost]
    public async Task<IActionResult> Enqueue([FromBody] TidalDownloadBatchRequest request)
        => await EnqueueCoreAsync(request, forceVideo: false);

    [HttpPost("videos/download")]
    public async Task<IActionResult> EnqueueVideo([FromBody] TidalDownloadBatchRequest request)
        => await EnqueueCoreAsync(request, forceVideo: true);

    private async Task<IActionResult> EnqueueCoreAsync(TidalDownloadBatchRequest request, bool forceVideo)
    {
        var destinationFolderId = request?.DestinationFolderId;
        var enqueue = DownloadQueueEnqueueHelper.CreateDedupEnqueueDelegate<TidalQueueItem>(
            _queueRepository,
            _dedupeService,
            _settingsService,
            _serviceProvider);
        var onQueued = DownloadQueueEnqueueHelper.CreateQueueAddedNotifier<TidalQueueItem>(
            _deezspotagListener,
            static payload => payload.ToQueuePayload());
        return await EngineDownloadControllerCommon.HandleBatchEnqueueAsync(
            this,
            request?.Tracks,
            destinationFolderId,
            new EngineDownloadControllerCommon.BatchEnqueueContext<TidalDownloadTrackDto, TidalQueueItem>
            {
                EngineLabel = "Tidal",
                EmptyTracksError = "No Tidal tracks supplied.",
                OrchestrationService = _orchestrationService,
                SettingsService = _settingsService,
                LibraryRepository = _libraryRepository,
                Logger = _logger,
                ValidateSettings = _ => null,
                PreparePayloadAsync = (track, settings, cancellationToken) => PreparePayloadAsync(
                    track,
                    ResolveRequestedQuality(request, settings),
                    destinationFolderId,
                    settings,
                    forceVideo,
                    cancellationToken),
                EnqueueAsync = enqueue,
                OnQueued = onQueued
            });
    }

    private Task<TidalQueueItem?> PreparePayloadAsync(
        TidalDownloadTrackDto track,
        string quality,
        long? destinationFolderId,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        bool forceVideo,
        CancellationToken cancellationToken)
        => EngineDownloadControllerCommon.PrepareTidalPayloadAsync(
            track,
            new EngineDownloadControllerCommon.TidalPayloadPreparationContext
            {
                Quality = quality,
                ContentType = forceVideo ? DeezSpoTag.Services.Download.Shared.Models.DownloadContentTypes.Video : null,
                DestinationFolderId = destinationFolderId,
                Settings = settings,
                SpotifyIdResolver = _spotifyIdResolver,
                Logger = _logger,
                RegexTimeout = RegexTimeout,
                NormalizeSourceUrl = forceVideo ? TryNormalizeTidalVideoUrl : TryNormalizeTidalUrl,
                ExtractTrackId = forceVideo ? TryExtractTidalVideoId : TryExtractTidalTrackId
            },
            cancellationToken);

    private static string ResolveRequestedQuality(TidalDownloadBatchRequest? request, DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings)
    {
        var service = settings.Service?.Trim();
        string quality;
        if (!string.IsNullOrWhiteSpace(request?.Quality))
        {
            quality = request.Quality;
        }
        else if (string.Equals(service, "auto", StringComparison.OrdinalIgnoreCase))
        {
            quality = "HI_RES_LOSSLESS";
        }
        else
        {
            quality = settings.TidalQuality ?? "HI_RES_LOSSLESS";
        }

        return quality.ToUpperInvariant();
    }

    private static string? TryNormalizeTidalUrl(string sourceUrl)
    {
        var trackId = TryExtractInternalTidalEntityId(sourceUrl, "track");
        if (string.IsNullOrWhiteSpace(trackId))
        {
            return null;
        }

        return BuildInternalTidalTrackIdentity(trackId);
    }

    private static string? TryNormalizeTidalVideoUrl(string sourceUrl)
    {
        var videoId = TryExtractTidalVideoId(sourceUrl);
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return null;
        }

        return $"https://tidal.com/video/{videoId}";
    }

    private static string? TryExtractTidalTrackId(string sourceUrl)
        => TryExtractInternalTidalEntityId(sourceUrl, "track");

    private static string? TryExtractTidalVideoId(string sourceUrl)
        => TryExtractPublicTidalEntityId(sourceUrl, "video");

    private static string? TryExtractInternalTidalEntityId(string sourceUrl, string entityType)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return null;
        }

        var trimmed = sourceUrl.Trim();
        if (trimmed.All(char.IsDigit))
        {
            return trimmed;
        }

        var internalPrefix = $"tidal:{entityType}:";
        if (trimmed.StartsWith(internalPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var internalId = trimmed[internalPrefix.Length..].Trim();
            return internalId.All(char.IsDigit) ? internalId : null;
        }

        return null;
    }

    private static string? TryExtractPublicTidalEntityId(string sourceUrl, string entityType)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return null;
        }

        var trimmed = sourceUrl.Trim();
        var pattern = $@"\/{Regex.Escape(entityType)}\/(?<id>\d+)";
        var match = Regex.Match(trimmed, pattern, RegexOptions.IgnoreCase, RegexTimeout);
        if (!match.Success)
        {
            return null;
        }

        return match.Groups["id"].Value;
    }

    private static string BuildInternalTidalTrackIdentity(string trackId)
        => $"tidal:track:{trackId}";

}

[ApiController]
[Route("api/tidal/download/videos")]
[Authorize]
[Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryToken]
public sealed class TidalVideoPreviewApiController : ControllerBase
{
    private readonly TidalDownloadService _tidalDownloadService;
    private readonly ILogger<TidalVideoPreviewApiController> _logger;

    public TidalVideoPreviewApiController(
        TidalDownloadService tidalDownloadService,
        ILogger<TidalVideoPreviewApiController> logger)
    {
        _tidalDownloadService = tidalDownloadService;
        _logger = logger;
    }

    [HttpGet("preview")]
    public async Task<IActionResult> PreviewVideo([FromQuery] long id, CancellationToken cancellationToken)
    {
        if (id <= 0)
        {
            return BadRequest(new { error = "Invalid Tidal video ID." });
        }

        try
        {
            var streamUrl = await _tidalDownloadService.ResolveVideoStreamUrlAsync(id, cancellationToken);
            return Redirect(streamUrl);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(ex, "Tidal video stream lookup failed for {VideoId}.", id);
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "Tidal video stream lookup failed." });
        }
    }
}

public sealed class TidalDownloadBatchRequest : EngineDownloadBatchRequestBase
{
    public List<TidalDownloadTrackDto> Tracks { get; set; } = new();
    public string? Quality { get; set; }
}

public sealed class TidalPublicDownloadGrantRequest
{
    public string? Grant { get; set; }
}

public sealed class TidalDownloadTrackDto : EngineDownloadTrackDtoBase
{
    public string? TidalId { get; set; }
}
