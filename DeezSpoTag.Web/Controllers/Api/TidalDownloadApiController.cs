using DeezSpoTag.Services.Download;
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
public sealed class TidalDownloadApiController : ControllerBase
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private readonly DownloadQueueRepository _queueRepository;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly DownloadOrchestrationService _orchestrationService;
    private readonly IDeezSpoTagListener _deezspotagListener;
    private readonly ISpotifyIdResolver _spotifyIdResolver;
    private readonly DeezSpoTag.Services.Library.LibraryRepository _libraryRepository;
    private readonly ILogger<TidalDownloadApiController> _logger;

    public TidalDownloadApiController(
        DownloadQueueRepository queueRepository,
        DeezSpoTagSettingsService settingsService,
        DownloadOrchestrationService orchestrationService,
        IDeezSpoTagListener deezspotagListener,
        ISpotifyIdResolver spotifyIdResolver,
        DeezSpoTag.Services.Library.LibraryRepository libraryRepository,
        ILogger<TidalDownloadApiController> logger)
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
    public async Task<IActionResult> Enqueue([FromBody] TidalDownloadBatchRequest request)
    {
        var destinationFolderId = request?.DestinationFolderId;
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
                ValidateSettings = settings =>
                {
                    var normalizedQuality = ResolveRequestedQuality(request, settings);
                    if (normalizedQuality.Contains("ATMOS", StringComparison.OrdinalIgnoreCase))
                    {
                        return new BadRequestObjectResult(new { error = "Atmos downloads are restricted to Apple Music." });
                    }

                    return null;
                },
                PreparePayloadAsync = (track, settings, cancellationToken) => PreparePayloadAsync(
                    track,
                    ResolveRequestedQuality(request, settings),
                    destinationFolderId,
                    settings,
                    cancellationToken),
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

    private Task<TidalQueueItem?> PreparePayloadAsync(
        TidalDownloadTrackDto track,
        string quality,
        long? destinationFolderId,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
        => EngineDownloadControllerCommon.PrepareTidalPayloadAsync(
            track,
            new EngineDownloadControllerCommon.TidalPayloadPreparationContext
            {
                Quality = quality,
                DestinationFolderId = destinationFolderId,
                Settings = settings,
                SpotifyIdResolver = _spotifyIdResolver,
                Logger = _logger,
                RegexTimeout = RegexTimeout,
                NormalizeSourceUrl = TryNormalizeTidalUrl,
                ExtractTrackId = TryExtractTidalTrackId
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
        var trackId = TryExtractTidalTrackId(sourceUrl);
        if (string.IsNullOrWhiteSpace(trackId))
        {
            return null;
        }

        return $"https://tidal.com/track/{trackId}";
    }

    private static string? TryExtractTidalTrackId(string sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return null;
        }

        var match = Regex.Match(sourceUrl, @"\/track\/(?<id>\d+)", RegexOptions.IgnoreCase, RegexTimeout);
        if (!match.Success)
        {
            return null;
        }

        return match.Groups["id"].Value;
    }

}

public sealed class TidalDownloadBatchRequest : EngineDownloadBatchRequestBase
{
    public List<TidalDownloadTrackDto> Tracks { get; set; } = new();
    public string? Quality { get; set; }
}

public sealed class TidalDownloadTrackDto : EngineDownloadTrackDtoBase
{
    public string? TidalId { get; set; }
}
