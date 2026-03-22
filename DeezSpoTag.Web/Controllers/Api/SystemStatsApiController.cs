using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

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

    public SystemStatsApiController(
        DownloadQueueRepository queueRepository,
        SystemStatsService systemStatsService,
        QualityScannerService qualityScannerService,
        DuplicateCleanerService duplicateCleanerService)
    {
        _queueRepository = queueRepository;
        _systemStatsService = systemStatsService;
        _qualityScannerService = qualityScannerService;
        _duplicateCleanerService = duplicateCleanerService;
    }

    [HttpGet("details")]
    public async Task<IActionResult> GetDetails(CancellationToken cancellationToken)
    {
        var tasks = await _queueRepository.GetTasksAsync(cancellationToken: cancellationToken);
        var utcToday = DateTimeOffset.UtcNow.Date;

        var activeDownloads = tasks.Count(task => IsActiveDownloadStatus(task.Status));
        var finishedDownloads = tasks.Count(task =>
            IsCompletedStatus(task.Status)
            && task.UpdatedAt.UtcDateTime.Date == utcToday);

        var qualityState = _qualityScannerService.GetState();
        var duplicateSummary = _duplicateCleanerService.GetLastRunSummary();

        return Ok(new
        {
            activeDownloads,
            finishedDownloads,
            // Download speed is not currently persisted in queue storage.
            downloadSpeed = activeDownloads > 0 ? "Active" : "0 KB/s",
            activeSyncs = 0,
            uptime = _systemStatsService.GetUptime(),
            memory = SystemStatsService.GetMemoryUsage(),
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

    private static bool IsActiveDownloadStatus(string? status)
    {
        var normalized = (status ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "running" or "downloading" or "inprogress" or "retrying";
    }

    private static bool IsCompletedStatus(string? status)
    {
        var normalized = (status ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "completed" or "complete" or "finished";
    }
}
