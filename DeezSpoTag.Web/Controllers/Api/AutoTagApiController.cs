using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

#pragma warning disable CA2016
namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/autotag")]
[Authorize]
public class AutoTagJobsController : ControllerBase
{
    private readonly AutoTagService _autoTagService;
    private readonly TaggingProfileService _profileService;
    private readonly DeezSpoTag.Services.Download.Queue.DownloadQueueRepository _queueRepository;
    private readonly DeezSpoTag.Services.Library.LibraryRepository _libraryRepository;
    private readonly LibraryConfigStore _libraryConfigStore;
    private readonly DeezSpoTagSettingsService _settingsService;

    public AutoTagJobsController(
        AutoTagService autoTagService,
        TaggingProfileService profileService,
        DeezSpoTag.Services.Download.Queue.DownloadQueueRepository queueRepository,
        DeezSpoTag.Services.Library.LibraryRepository libraryRepository,
        LibraryConfigStore libraryConfigStore,
        DeezSpoTagSettingsService settingsService)
    {
        _autoTagService = autoTagService;
        _profileService = profileService;
        _queueRepository = queueRepository;
        _libraryRepository = libraryRepository;
        _libraryConfigStore = libraryConfigStore;
        _settingsService = settingsService;
    }

    [HttpPost("start")]
    public async Task<IActionResult> Start([FromBody] AutoTagStartRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            return BadRequest("Path is required.");
        }

        if (!request.Config.HasValue
            || request.Config.Value.ValueKind == JsonValueKind.Undefined
            || request.Config.Value.ValueKind == JsonValueKind.Null)
        {
            return BadRequest("Config is required.");
        }

        string normalizedPath;
        try
        {
            normalizedPath = Path.GetFullPath(request.Path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return BadRequest("Path is invalid.");
        }

        var allowedRoots = await AutoTagFolderScopeHelper.ResolveAllowedAutoTagStartRootsAsync(
            _libraryRepository,
            _libraryConfigStore,
            _settingsService,
            cancellationToken);
        if (allowedRoots.Count == 0)
        {
            return StatusCode(503, "No accessible AutoTag start roots are configured.");
        }

        if (!AutoTagFolderScopeHelper.IsPathInAllowedRoots(normalizedPath, allowedRoots))
        {
            return BadRequest("Path is outside configured AutoTag roots.");
        }

        if (await _queueRepository.HasActiveDownloadsAsync())
        {
            return StatusCode(409, "Downloads are active. AutoTag cannot start until the queue is idle.");
        }

        var configNode = JsonNode.Parse(request.Config.Value.GetRawText()) as JsonObject;
        if (configNode == null)
        {
            return BadRequest("Config must be an object.");
        }

        configNode.Remove("playlistPath");
        configNode.Remove("isPlaylist");

        configNode["path"] = normalizedPath;

        var configJson = configNode.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        DeezSpoTag.Core.Models.Settings.TaggingProfile? selectedProfile = null;
        if (!string.IsNullOrWhiteSpace(request.ProfileId))
        {
            var profiles = await _profileService.LoadAsync();
            selectedProfile = TaggingProfileService.FindByIdOrName(profiles, request.ProfileId);
            if (selectedProfile == null)
            {
                return BadRequest("Profile was not found.");
            }
        }

        var job = await _autoTagService.StartJob(
            normalizedPath,
            configJson,
            "manual",
            selectedProfile?.Technical,
            selectedProfile?.Id,
            selectedProfile?.Name);
        return Ok(new { jobId = job.Id, status = job.Status });
    }

    [HttpGet("jobs/{id}")]
    public IActionResult GetJob(string id)
    {
        var job = _autoTagService.GetJob(id);
        if (job == null)
        {
            return NotFound();
        }

        return Ok(new
        {
            job.Id,
            job.Status,
            job.StartedAt,
            job.FinishedAt,
            job.ExitCode,
            job.Error,
            job.Progress,
            job.OkCount,
            job.ErrorCount,
            job.SkippedCount,
            job.RootPath,
            job.Trigger,
            job.ProfileId,
            job.ProfileName,
            job.CurrentPlatform,
            job.LastStatus,
            logs = job.Logs,
            statusHistory = job.StatusHistory
        });
    }

    [HttpGet("jobs/{id}/tag-diff")]
    public async Task<IActionResult> GetTagDiff(
        string id,
        [FromQuery] string path,
        [FromQuery] string? platform,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return BadRequest("Path is required.");
        }

        string normalizedPath;
        try
        {
            normalizedPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return BadRequest("Path is invalid.");
        }

        var allowedRoots = await AutoTagFolderScopeHelper.ResolveAllowedAutoTagStartRootsAsync(
            _libraryRepository,
            _libraryConfigStore,
            _settingsService,
            cancellationToken);
        if (allowedRoots.Count == 0)
        {
            return StatusCode(503, "No accessible AutoTag roots are configured.");
        }

        if (!AutoTagFolderScopeHelper.IsPathInAllowedRoots(normalizedPath, allowedRoots))
        {
            return BadRequest("Path is outside configured AutoTag roots.");
        }

        var diff = _autoTagService.GetTagDiff(id, normalizedPath, platform);
        if (diff == null)
        {
            return NotFound(new
            {
                message = "No before/after tag snapshot was captured for this track in the selected run."
            });
        }

        return Ok(diff);
    }

    [HttpGet("jobs/latest")]
    public IActionResult GetLatestJob()
    {
        var job = _autoTagService.GetLatestJob();
        if (job == null)
        {
            return Ok(new
            {
                id = (string?)null,
                status = "idle",
                startedAt = (DateTimeOffset?)null,
                finishedAt = (DateTimeOffset?)null,
                exitCode = (int?)null,
                error = (string?)null,
                progress = 0d,
                okCount = 0,
                errorCount = 0,
                skippedCount = 0,
                rootPath = (string?)null,
                trigger = "manual",
                profileId = (string?)null,
                profileName = (string?)null,
                currentPlatform = (string?)null,
                lastStatus = (object?)null,
                logs = Array.Empty<string>(),
                statusHistory = Array.Empty<object>()
            });
        }

        return Ok(new
        {
            job.Id,
            job.Status,
            job.StartedAt,
            job.FinishedAt,
            job.ExitCode,
            job.Error,
            job.Progress,
            job.OkCount,
            job.ErrorCount,
            job.SkippedCount,
            job.RootPath,
            job.Trigger,
            job.ProfileId,
            job.ProfileName,
            job.CurrentPlatform,
            job.LastStatus,
            logs = job.Logs,
            statusHistory = job.StatusHistory
        });
    }

    [HttpGet("history/calendar")]
    public IActionResult GetHistoryCalendar([FromQuery] int year, [FromQuery] int month)
    {
        if (year < 2000 || year > 3000 || month is < 1 or > 12)
        {
            return BadRequest("Valid year and month are required.");
        }

        var days = _autoTagService.GetArchivedRunCalendar(year, month);
        return Ok(new
        {
            year,
            month,
            days
        });
    }

    [HttpGet("history/runs")]
    public IActionResult GetHistoryRuns([FromQuery] string date)
    {
        if (!DateOnly.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var selectedDate))
        {
            return BadRequest("A valid date is required.");
        }

        var runs = _autoTagService.GetArchivedRunsByDate(selectedDate);
        return Ok(new
        {
            date = selectedDate.ToString("yyyy-MM-dd"),
            runs
        });
    }

    [HttpGet("history/runs/{id}")]
    public IActionResult GetHistoryRun(string id)
    {
        var archive = _autoTagService.GetArchivedRun(id);
        if (archive == null)
        {
            return NotFound();
        }

        return Ok(archive);
    }

    [HttpPost("jobs/{id}/stop")]
    public async Task<IActionResult> StopJob(string id)
    {
        var stopped = await _autoTagService.StopJobAsync(id);
        if (!stopped)
        {
            return NotFound();
        }

        return Ok(new { id, status = "canceled" });
    }
}

public class AutoTagStartRequest
{
    public string? Path { get; set; }
    public JsonElement? Config { get; set; }
    public string? ProfileId { get; set; }
}

public sealed class EnhancementFolderUniformityRequest
{
    public long? FolderId { get; set; }
    public IReadOnlyList<long>? FolderIds { get; set; }
    public bool? EnforceFolderStructure { get; set; }
    public bool? MoveMisplacedFiles { get; set; }
    public bool? RenameFilesToTemplate { get; set; }
    public bool? RemoveEmptyFolders { get; set; }
    public bool? DryRunMode { get; set; }
    public bool? IncludeSubfolders { get; set; }
}

public sealed class EnhancementQualityChecksRequest
{
    public long? FolderId { get; set; }
    public IReadOnlyList<long>? FolderIds { get; set; }
    public string Scope { get; set; } = "all";
    public bool? FlagDuplicates { get; set; }
    public bool? FlagMissingTags { get; set; }
    public bool? FlagMismatchedMetadata { get; set; }
    public bool? UseDuplicatesFolder { get; set; }
    public bool? QueueAtmosAlternatives { get; set; }
    public bool? QueueLyricsRefresh { get; set; }
    public string? MinFormat { get; set; }
    public int? MinBitDepth { get; set; }
    public double? MinSampleRateKhz { get; set; }
    public int? CooldownMinutes { get; set; }
    public IReadOnlyList<string>? TechnicalProfiles { get; set; }
}
