using System.Text.RegularExpressions;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Qobuz;
using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/qobuz/download")]
[Authorize]
public sealed class QobuzDownloadApiController : ControllerBase
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private readonly DownloadQueueRepository _queueRepository;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly DownloadOrchestrationService _orchestrationService;
    private readonly IDeezSpoTagListener _deezspotagListener;
    private readonly ISpotifyIdResolver _spotifyIdResolver;
    private readonly DeezSpoTag.Services.Library.LibraryRepository _libraryRepository;
    private readonly ILogger<QobuzDownloadApiController> _logger;

    public QobuzDownloadApiController(
        DownloadQueueRepository queueRepository,
        DeezSpoTagSettingsService settingsService,
        DownloadOrchestrationService orchestrationService,
        IDeezSpoTagListener deezspotagListener,
        ISpotifyIdResolver spotifyIdResolver,
        DeezSpoTag.Services.Library.LibraryRepository libraryRepository,
        ILogger<QobuzDownloadApiController> logger)
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
    public async Task<IActionResult> Enqueue([FromBody] QobuzDownloadBatchRequest request)
    {
        var destinationFolderId = request?.DestinationFolderId;
        var enqueue = DownloadQueueEnqueueHelper.CreateDedupEnqueueDelegate<QobuzQueueItem>(_queueRepository, _deezspotagListener, _logger);
        var onQueued = DownloadQueueEnqueueHelper.CreateQueueAddedNotifier<QobuzQueueItem>(
            _deezspotagListener,
            static payload => payload.ToQueuePayload());
        return await EngineDownloadControllerCommon.HandleBatchEnqueueAsync(
            this,
            request?.Tracks,
            destinationFolderId,
            new EngineDownloadControllerCommon.BatchEnqueueContext<QobuzDownloadTrackDto, QobuzQueueItem>
            {
                EngineLabel = "Qobuz",
                EmptyTracksError = "No Qobuz tracks supplied.",
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
                    cancellationToken),
                EnqueueAsync = enqueue,
                OnQueued = onQueued
            });
    }

    private async Task<QobuzQueueItem?> PreparePayloadAsync(
        QobuzDownloadTrackDto track,
        string quality,
        long? destinationFolderId,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(track.Isrc)
            && (string.IsNullOrWhiteSpace(track.Title) || string.IsNullOrWhiteSpace(track.Artist)))
        {
            _logger.LogWarning(
                "Skip enqueue (engine=qobuz reason=missing_isrc_or_metadata): title='{Title}' artist='{Artist}'",
                track.Title ?? string.Empty,
                track.Artist ?? string.Empty);
            return null;
        }

        if (string.IsNullOrWhiteSpace(track.SpotifyId))
        {
            track.SpotifyId = EngineDownloadControllerCommon.TryExtractSpotifyId(track.SourceUrl, RegexTimeout)
                ?? await _spotifyIdResolver.ResolveTrackIdAsync(
                    track.Title ?? string.Empty,
                    track.Artist ?? string.Empty,
                    track.Album,
                    track.Isrc,
                    cancellationToken);
        }

        return BuildPayload(track, quality, destinationFolderId, settings);
    }

    private static QobuzQueueItem BuildPayload(
        QobuzDownloadTrackDto track,
        string quality,
        long? destinationFolderId,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings)
    {
        var (autoSources, autoIndex, resolvedQuality) = EngineDownloadControllerCommon.ResolveAutoSourceState(
            settings,
            includeDeezer: false,
            engine: "qobuz",
            quality: quality);
        var payload = new QobuzQueueItem
        {
            QobuzId = track.QobuzId ?? string.Empty,
        };
        EngineDownloadControllerCommon.PopulateSharedQueueFields(
            payload,
            EngineDownloadControllerCommon.CreateQueueTrackSeed(track),
            resolvedQuality,
            destinationFolderId,
            autoSources,
            autoIndex);

        return payload;
    }

    private static string ResolveRequestedQuality(QobuzDownloadBatchRequest? request, DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings)
    {
        return NormalizeQobuzQuality(
            string.IsNullOrWhiteSpace(request?.Quality)
                ? settings.QobuzQuality
                : request.Quality);
    }

    private static string NormalizeQobuzQuality(string? quality)
    {
        return QobuzQualityCodeNormalizer.Normalize(quality, defaultCode: "27");
    }

}

public sealed class QobuzDownloadBatchRequest : EngineDownloadBatchRequestBase
{
    public List<QobuzDownloadTrackDto> Tracks { get; set; } = new();
    public string? Quality { get; set; }
}

public sealed class QobuzDownloadTrackDto : EngineDownloadTrackDtoBase
{
    public string? QobuzId { get; set; }
}
