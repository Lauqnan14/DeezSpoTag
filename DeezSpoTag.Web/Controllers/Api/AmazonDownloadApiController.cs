using System.Text.RegularExpressions;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Amazon;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/amazon/download")]
[Authorize]
[Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryToken]
public sealed class AmazonDownloadApiController : ControllerBase
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private readonly DownloadQueueRepository _queueRepository;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly DownloadOrchestrationService _orchestrationService;
    private readonly IDeezSpoTagListener _deezspotagListener;
    private readonly ISpotifyIdResolver _spotifyIdResolver;
    private readonly DeezSpoTag.Services.Library.LibraryRepository _libraryRepository;
    private readonly DownloadDedupeService _dedupeService;
    private readonly IAmazonDownloadService _amazonDownloadService;
    private readonly ILogger<AmazonDownloadApiController> _logger;

    public AmazonDownloadApiController(
        DownloadControllerServices services,
        IAmazonDownloadService amazonDownloadService,
        ILogger<AmazonDownloadApiController> logger)
    {
        _queueRepository = services.QueueRepository;
        _settingsService = services.SettingsService;
        _orchestrationService = services.OrchestrationService;
        _deezspotagListener = services.DeezSpoTagListener;
        _spotifyIdResolver = services.SpotifyIdResolver;
        _libraryRepository = services.LibraryRepository;
        _dedupeService = services.DedupeService;
        _amazonDownloadService = amazonDownloadService;
        _logger = logger;
    }

    [HttpGet("public-session")]
    public async Task<IActionResult> GetPublicSession(CancellationToken cancellationToken)
        => Ok(new
        {
            authenticated = await _amazonDownloadService.HasPublicDownloadSessionAsync(cancellationToken)
        });

    [HttpPost("public-session/start")]
    public async Task<IActionResult> StartPublicSession(CancellationToken cancellationToken)
    {
        var verificationUrl = await _amazonDownloadService.BeginPublicDownloadVerificationAsync(cancellationToken);
        return Ok(new
        {
            authenticated = string.IsNullOrWhiteSpace(verificationUrl),
            verificationUrl
        });
    }

    [HttpPost("public-session/complete")]
    public async Task<IActionResult> CompletePublicSession(
        [FromBody] AmazonPublicDownloadGrantRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Grant))
        {
            return BadRequest(new { error = "Amazon public download verification grant is required." });
        }

        await _amazonDownloadService.CompletePublicDownloadVerificationAsync(request.Grant, cancellationToken);
        return Ok(new { authenticated = true });
    }

    [HttpPost]
    public async Task<IActionResult> Enqueue([FromBody] AmazonDownloadBatchRequest request)
    {
        var destinationFolderId = request?.DestinationFolderId;
        var quality = ResolveRequestedQuality(request);
        var enqueue = DownloadQueueEnqueueHelper.CreateDedupEnqueueDelegate<AmazonQueueItem>(
            _queueRepository,
            _dedupeService);
        var onQueued = DownloadQueueEnqueueHelper.CreateQueueAddedNotifier<AmazonQueueItem>(
            _deezspotagListener,
            static payload => payload.ToQueuePayload());
        return await EngineDownloadControllerCommon.HandleBatchEnqueueAsync(
            this,
            request?.Tracks,
            destinationFolderId,
            new EngineDownloadControllerCommon.BatchEnqueueContext<AmazonDownloadTrackDto, AmazonQueueItem>
            {
                EngineLabel = "Amazon",
                EmptyTracksError = "No Amazon tracks supplied.",
                OrchestrationService = _orchestrationService,
                SettingsService = _settingsService,
                LibraryRepository = _libraryRepository,
                Logger = _logger,
                ValidateSettings = _ =>
                {
                    if (!IsSupportedAmazonQuality(quality))
                    {
                        return new BadRequestObjectResult(new { error = "Unsupported Amazon Music quality." });
                    }

                    return null;
                },
                PreparePayloadAsync = (track, settings, cancellationToken) => PreparePayloadAsync(track, quality, destinationFolderId, settings, cancellationToken),
                EnqueueAsync = enqueue,
                OnQueued = onQueued
            });
    }

    private Task<AmazonQueueItem?> PreparePayloadAsync(
        AmazonDownloadTrackDto track,
        string quality,
        long? destinationFolderId,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
        => EngineDownloadControllerCommon.PrepareAmazonPayloadAsync(
            track,
            new EngineDownloadControllerCommon.EnginePayloadPreparationContext
            {
                Quality = quality,
                DestinationFolderId = destinationFolderId,
                Settings = settings,
                SpotifyIdResolver = _spotifyIdResolver,
                Logger = _logger,
                RegexTimeout = RegexTimeout
            },
            cancellationToken);

    private static string ResolveRequestedQuality(AmazonDownloadBatchRequest? request)
    {
        var quality = request?.Quality?.Trim();
        return string.IsNullOrWhiteSpace(quality) ? "ULTRA_HD_FLAC" : quality.ToUpperInvariant();
    }

    private static bool IsSupportedAmazonQuality(string quality)
        => QualityCatalog.GetEngineQualityOptions().TryGetValue("amazon", out var options)
           && options.Any(option => string.Equals(option.Value, quality, StringComparison.OrdinalIgnoreCase));
}

public sealed class AmazonDownloadBatchRequest : EngineDownloadBatchRequestBase
{
    public List<AmazonDownloadTrackDto> Tracks { get; set; } = new();
    public string? Quality { get; set; }
}

public sealed class AmazonDownloadTrackDto : EngineDownloadTrackDtoBase
{
    public string? AmazonId { get; set; }
}

public sealed class AmazonPublicDownloadGrantRequest
{
    public string Grant { get; set; } = string.Empty;
}
