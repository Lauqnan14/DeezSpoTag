using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using DeezSpoTag.Services.Runtime;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/system-stats")]
[Authorize]
public sealed class SystemStatsApiController : ControllerBase
{
    private readonly DownloadQueueRepository _queueRepository;
    private readonly SystemStatsService _systemStatsService;
    private readonly QualityScannerService _qualityScannerService;
    private readonly DuplicateCleanerService _duplicateCleanerService;
    private readonly DownloadOrchestrationService _downloadOrchestrationService;
    private readonly ShazamRecognitionService _shazamRecognitionService;
    private readonly BackgroundWorkCoordinator _backgroundWorkCoordinator;

    public SystemStatsApiController(
        DownloadQueueRepository queueRepository,
        SystemStatsService systemStatsService,
        QualityScannerService qualityScannerService,
        DuplicateCleanerService duplicateCleanerService,
        DownloadOrchestrationService downloadOrchestrationService,
        ShazamRecognitionService shazamRecognitionService,
        BackgroundWorkCoordinator backgroundWorkCoordinator)
    {
        _queueRepository = queueRepository;
        _systemStatsService = systemStatsService;
        _qualityScannerService = qualityScannerService;
        _duplicateCleanerService = duplicateCleanerService;
        _downloadOrchestrationService = downloadOrchestrationService;
        _shazamRecognitionService = shazamRecognitionService;
        _backgroundWorkCoordinator = backgroundWorkCoordinator;
    }

    [HttpGet("details")]
    public async Task<IActionResult> GetDetails(CancellationToken cancellationToken)
    {
        var utcToday = DateTimeOffset.UtcNow.Date;
        var queueCounts = await _queueRepository.GetStatusCountsAsync(
            new DateTimeOffset(utcToday, TimeSpan.Zero),
            cancellationToken);
        var activeDownloads = queueCounts.ActiveDownloads;
        var finishedDownloads = queueCounts.CompletedDownloads;

        var qualityState = _qualityScannerService.GetState();
        var duplicateSummary = _duplicateCleanerService.GetLastRunSummary();
        var orchestration = _downloadOrchestrationService.GetStatusSnapshot();
        var resources = SystemStatsService.GetResourceSnapshot();
        var backgroundWork = _backgroundWorkCoordinator.GetSnapshot();

        return Ok(new
        {
            activeDownloads,
            finishedDownloads,
            // Download speed is not currently persisted in queue storage.
            downloadSpeed = activeDownloads > 0 ? "Active" : "0 KB/s",
            activeSyncs = 0,
            uptime = _systemStatsService.GetUptime(),
            memory = SystemStatsService.GetMemoryUsage(),
            resources = new
            {
                resources.WorkingSetBytes,
                resources.ManagedMemoryBytes,
                resources.ManagedHeapBytes,
                resources.ManagedFragmentedBytes,
                resources.ProcessThreadCount,
                activeShazamProcesses = _shazamRecognitionService.ActiveRecognizerProcessCount,
                backgroundWork.ActiveHeavyOperation
            },
            orchestration = new
            {
                phase = orchestration.Phase.ToString(),
                phaseEnteredUtc = orchestration.PhaseEnteredUtc,
                queueIdleSinceUtc = orchestration.QueueIdleSinceUtc,
                countdownUntilUtc = orchestration.CountdownUntilUtc,
                lastEnrichmentFinishedUtc = orchestration.LastEnrichmentFinishedUtc,
                enhancementResumeNotBeforeUtc = orchestration.EnhancementResumeNotBeforeUtc,
                pipelineRequested = orchestration.PipelineRequested,
                retrySweepPending = orchestration.RetrySweepPending,
                enhancementInterruptedByEnrichment = orchestration.EnhancementInterruptedByEnrichment,
                activeDownloadCount = orchestration.ActiveDownloadCount,
                taggingInProgress = orchestration.TaggingInProgress
            },
            enhancement = new
            {
                qualityScanner = new
                {
                    status = qualityState.Status,
                    phase = qualityState.Phase,
                    progress = qualityState.Progress,
                    processed = qualityState.Processed,
                    total = qualityState.Total,
                    qualityMet = qualityState.QualityMet,
                    lowQuality = qualityState.LowQuality,
                    matched = qualityState.Matched,
                    upgradesQueued = qualityState.UpgradesQueued,
                    atmosQueued = qualityState.AtmosQueued,
                    duplicateSkipped = qualityState.DuplicateSkipped,
                    matchMissed = qualityState.MatchMissed,
                    errorMessage = qualityState.ErrorMessage,
                    scope = qualityState.Scope,
                    folderId = qualityState.FolderId,
                    trigger = qualityState.Trigger,
                    queueAtmosAlternatives = qualityState.QueueAtmosAlternatives,
                    cooldownMinutes = qualityState.CooldownMinutes,
                    runId = qualityState.RunId
                },
                duplicateCleaner = new
                {
                    status = duplicateSummary.Status,
                    startedUtc = duplicateSummary.StartedUtc == DateTimeOffset.MinValue
                        ? (DateTimeOffset?)null
                        : duplicateSummary.StartedUtc,
                    finishedUtc = duplicateSummary.FinishedUtc,
                    durationMs = duplicateSummary.DurationMs,
                    useDuplicatesFolder = duplicateSummary.UseDuplicatesFolder,
                    duplicatesFolderName = duplicateSummary.DuplicatesFolderName,
                    useShazamForIdentity = duplicateSummary.UseShazamForIdentity,
                    folderCount = duplicateSummary.FolderCount,
                    filesScanned = duplicateSummary.FilesScanned,
                    duplicatesFound = duplicateSummary.DuplicatesFound,
                    deleted = duplicateSummary.Deleted,
                    spaceFreedBytes = duplicateSummary.SpaceFreedBytes,
                    errorMessage = duplicateSummary.ErrorMessage
                }
            }
        });
    }

}
