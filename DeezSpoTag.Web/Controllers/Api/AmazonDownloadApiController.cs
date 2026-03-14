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
public sealed class AmazonDownloadApiController : ControllerBase
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private readonly DownloadQueueRepository _queueRepository;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly DownloadOrchestrationService _orchestrationService;
    private readonly IDeezSpoTagListener _deezspotagListener;
    private readonly ISpotifyIdResolver _spotifyIdResolver;
    private readonly DeezSpoTag.Services.Library.LibraryRepository _libraryRepository;
    private readonly ILogger<AmazonDownloadApiController> _logger;

    public AmazonDownloadApiController(
        DownloadQueueRepository queueRepository,
        DeezSpoTagSettingsService settingsService,
        DownloadOrchestrationService orchestrationService,
        IDeezSpoTagListener deezspotagListener,
        ISpotifyIdResolver spotifyIdResolver,
        DeezSpoTag.Services.Library.LibraryRepository libraryRepository,
        ILogger<AmazonDownloadApiController> logger)
    {
        _queueRepository = queueRepository;
        _settingsService = settingsService;
        _orchestrationService = orchestrationService;
        _deezspotagListener = deezspotagListener;
        _spotifyIdResolver = spotifyIdResolver;
        _libraryRepository = libraryRepository;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Enqueue([FromBody] AmazonDownloadBatchRequest request)
    {
        var destinationFolderId = request?.DestinationFolderId;
        const string quality = "FLAC";
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
                    if (quality.Contains("atmos", StringComparison.OrdinalIgnoreCase))
                    {
                        return new BadRequestObjectResult(new { error = "Atmos downloads are restricted to Apple Music." });
                    }

                    return null;
                },
                PreparePayloadAsync = (track, settings, cancellationToken) => PreparePayloadAsync(track, quality, destinationFolderId, settings, cancellationToken),
                EnqueueAsync = (payload, redownloadCooldownMinutes, cancellationToken) => DownloadQueueEnqueueHelper.EnqueueWithDedupAsync(
                    payload,
                    redownloadCooldownMinutes,
                    _queueRepository,
                    _deezspotagListener,
                    _logger,
                    cancellationToken),
                OnQueued = payload => _deezspotagListener.SendAddedToQueue(payload.ToQueuePayload())
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
}

public sealed class AmazonDownloadBatchRequest : EngineDownloadBatchRequestBase
{
    public List<AmazonDownloadTrackDto> Tracks { get; set; } = new();
}

public sealed class AmazonDownloadTrackDto : EngineDownloadTrackDtoBase
{
    public string? AmazonId { get; set; }
}
