using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/autotag")]
[Authorize]
[Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryToken]
public class AutoTagJobsController : ControllerBase
{
    private readonly AutoTagService _autoTagService;
    private readonly AutoTagConfigBuilder _autoTagConfigBuilder;
    private readonly TaggingProfileService _profileService;
    private readonly DeezSpoTag.Services.Download.Queue.DownloadQueueRepository _queueRepository;
    private readonly DeezSpoTag.Services.Library.LibraryRepository _libraryRepository;
    private readonly LibraryConfigStore _libraryConfigStore;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly AutoTagProfileResolutionService _profileResolutionService;

    public AutoTagJobsController(
        AutoTagService autoTagService,
        AutoTagConfigBuilder autoTagConfigBuilder,
        TaggingProfileService profileService,
        DeezSpoTag.Services.Download.Queue.DownloadQueueRepository queueRepository,
        DeezSpoTag.Services.Library.LibraryRepository libraryRepository,
        LibraryConfigStore libraryConfigStore,
        DeezSpoTagSettingsService settingsService,
        AutoTagProfileResolutionService profileResolutionService)
    {
        _autoTagService = autoTagService;
        _autoTagConfigBuilder = autoTagConfigBuilder;
        _profileService = profileService;
        _queueRepository = queueRepository;
        _libraryRepository = libraryRepository;
        _libraryConfigStore = libraryConfigStore;
        _settingsService = settingsService;
        _profileResolutionService = profileResolutionService;
    }

    [HttpPost("start")]
    public async Task<IActionResult> Start([FromBody] AutoTagStartRequest? request, CancellationToken cancellationToken)
    {
        if (!TryNormalizeStartRequest(request, out var normalizedPath, out var validationError))
        {
            return validationError;
        }

        var startRequest = request!;
        var scopeError = await ValidateStartScopeAsync(normalizedPath, cancellationToken);
        if (scopeError != null)
        {
            return scopeError;
        }

        var selectedProfileResult = await ResolveSelectedProfileAsync(startRequest.ProfileId);
        if (selectedProfileResult.Error != null)
        {
            return selectedProfileResult.Error;
        }

        if (!TryBuildEffectiveConfigNode(selectedProfileResult.Profile, out var configNode, out var configError))
        {
            return configError!;
        }
        var selectedProfile = selectedProfileResult.Profile!;

        if (!TryValidateEnrichmentScope(normalizedPath, configNode, startRequest.RunIntent, out var enrichmentError))
        {
            return enrichmentError;
        }

        configNode.Remove("playlistPath");
        configNode.Remove("isPlaylist");
        configNode["path"] = normalizedPath;

        var job = await _autoTagService.StartJob(
            normalizedPath,
            SerializeConfig(configNode),
            new AutoTagService.StartJobOptions(
                ProfileId: selectedProfile.Id,
                ProfileName: selectedProfile.Name,
                RunIntent: startRequest.RunIntent,
                FolderStructureOverride: selectedProfile.FolderStructure));
        return CreateStartJobResponse(job);
    }

    [HttpPost("enhancement/start")]
    public async Task<IActionResult> StartEnhancement(
        [FromBody] AutoTagEnhancementStartRequest? request,
        CancellationToken cancellationToken)
    {
        if (request == null)
        {
            return BadRequest("Enhancement request is required.");
        }

        var folders = await AutoTagFolderScopeHelper.ResolveLibraryFoldersAsync(
            _libraryRepository,
            _libraryConfigStore,
            cancellationToken);
        var enabledFolders = folders
            .Where(folder => folder.Enabled
                && !string.IsNullOrWhiteSpace(folder.RootPath)
                && LibraryFolderPathSafety.IsMusicFolder(folder))
            .ToList();
        var requestedFolderIds = AutoTagFolderScopeHelper.NormalizeFolderIds(request.FolderIds, enabledFolders);
        if (request.FolderIds is { Count: > 0 } && requestedFolderIds.Count == 0)
        {
            return BadRequest("Selected library folders were not found or are disabled.");
        }
        var scopedFolders = requestedFolderIds.Count > 0
            ? enabledFolders.Where(folder => requestedFolderIds.Contains(folder.Id)).ToList()
            : enabledFolders;
        if (scopedFolders.Count == 0)
        {
            return BadRequest("No enabled music library folders are available in the selected enhancement scope.");
        }

        var profileState = await _profileResolutionService.LoadNormalizedStateAsync(
            includeFolders: true,
            cancellationToken);
        var assignedProfiles = scopedFolders
            .Select(folder => AutoTagProfileResolutionService.ResolveFolderProfile(
                profileState,
                folder.Id,
                folder.AutoTagProfileId))
            .Where(profile => profile != null)
            .Cast<DeezSpoTag.Core.Models.Settings.TaggingProfile>()
            .GroupBy(profile => profile.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (assignedProfiles.Count != 1)
        {
            return BadRequest(
                "A central enhancement job can cover only folders assigned to the same profile. Run folders with different profiles as separate enhancement jobs.");
        }

        var selectedProfile = assignedProfiles[0];
        if (!TryBuildEffectiveConfigNode(selectedProfile, out var configNode, out var configError))
        {
            return configError!;
        }

        var targetFiles = NormalizeEnhancementTargetFiles(request.TargetFiles, scopedFolders);
        if (targetFiles.Count == 0 && ShouldRunMissingCoreMetadataEnhancement(request, configNode))
        {
            var missingFiles = await _libraryRepository.GetMissingCoreMetadataFilesAsync(
                scopedFolders.Select(folder => folder.Id).ToList(),
                cancellationToken);
            targetFiles = missingFiles
                .Select(file => file.FilePath)
                .Where(path => !string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var runIntent = string.Equals(
            request.Scope,
            "recent",
            StringComparison.OrdinalIgnoreCase)
                ? AutoTagLiterals.RunIntentEnhancementRecentDownloads
                : AutoTagLiterals.RunIntentEnhancementOnly;
        if (string.Equals(runIntent, AutoTagLiterals.RunIntentEnhancementRecentDownloads, StringComparison.OrdinalIgnoreCase)
            && targetFiles.Count == 0)
        {
            return BadRequest("Recent-files enhancement requires at least one existing audio file in the selected library scope.");
        }

        ApplyEnhancementRunSelection(configNode, request, requestedFolderIds, targetFiles);
        var rootPath = targetFiles.Count > 0
            ? ResolveEnhancementTargetRootPath(targetFiles)
            : Path.GetFullPath(scopedFolders[0].RootPath);
        configNode["path"] = rootPath;

        var job = await _autoTagService.StartJob(
            rootPath,
            SerializeConfig(configNode),
            new AutoTagService.StartJobOptions(
                ProfileId: selectedProfile.Id,
                ProfileName: selectedProfile.Name,
                RunIntent: runIntent,
                FolderStructureOverride: selectedProfile.FolderStructure));
        return CreateStartJobResponse(job);
    }

    private static void ApplyEnhancementRunSelection(
        JsonObject configNode,
        AutoTagEnhancementStartRequest request,
        IReadOnlyList<long> folderIds,
        IReadOnlyList<string> targetFiles)
    {
        var enhancement = configNode[AutoTagLiterals.EnhancementStage] as JsonObject ?? new JsonObject();
        configNode[AutoTagLiterals.EnhancementStage] = enhancement;

        var selectedFeatures = (request.Features ?? Array.Empty<string>())
            .Select(value => value?.Trim().ToLowerInvariant())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selectedFeatures.Count > 0)
        {
            SetEnhancementFeatureEnabled(enhancement, "folderUniformity", selectedFeatures.Contains("folder-uniformity"));
            SetEnhancementFeatureEnabled(enhancement, "coverMaintenance", selectedFeatures.Contains("cover-maintenance"));
            SetEnhancementFeatureEnabled(enhancement, "qualityChecks", selectedFeatures.Contains("quality-checks"));
            if (!selectedFeatures.Contains("tag-gap-fill")
                && !ShouldKeepGapFillForMissingCoreMetadataScan(configNode, targetFiles))
            {
                configNode["gapFillTags"] = new JsonArray();
            }
        }

        ApplyEnhancementFolderScope(enhancement, "folderUniformity", folderIds);
        ApplyEnhancementFolderScope(enhancement, "coverMaintenance", folderIds);
        ApplyEnhancementFolderScope(enhancement, "qualityChecks", folderIds);
        if (targetFiles.Count > 0)
        {
            configNode[AutoTagLiterals.TargetFilesKey] = new JsonArray(
                targetFiles.Select(value => JsonValue.Create(value)).ToArray());
        }
    }

    private static void SetEnhancementFeatureEnabled(JsonObject enhancement, string name, bool enabled)
    {
        var feature = enhancement[name] as JsonObject ?? new JsonObject();
        feature["enabled"] = enabled;
        enhancement[name] = feature;
    }

    private static bool ShouldRunMissingCoreMetadataEnhancement(
        AutoTagEnhancementStartRequest request,
        JsonObject configNode)
    {
        var selectedFeatures = (request.Features ?? Array.Empty<string>())
            .Select(value => value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return selectedFeatures.Contains("quality-checks")
            && IsMissingCoreMetadataScanEnabled(configNode);
    }

    private static bool ShouldKeepGapFillForMissingCoreMetadataScan(
        JsonObject configNode,
        IReadOnlyList<string> targetFiles)
    {
        return targetFiles.Count > 0 && IsMissingCoreMetadataScanEnabled(configNode);
    }

    private static bool IsMissingCoreMetadataScanEnabled(JsonObject configNode)
    {
        return configNode[AutoTagLiterals.EnhancementStage] is JsonObject enhancement
            && enhancement["qualityChecks"] is JsonObject qualityChecks
            && ReadBoolean(qualityChecks, "flagMissingTags") == true;
    }

    private static bool? ReadBoolean(JsonObject node, string propertyName)
    {
        if (!node.TryGetPropertyValue(propertyName, out var valueNode) || valueNode is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<bool>(out var boolValue))
        {
            return boolValue;
        }

        return value.TryGetValue<string>(out var raw) && bool.TryParse(raw, out var parsed)
            ? parsed
            : null;
    }

    private static string ResolveEnhancementTargetRootPath(IReadOnlyList<string> targetFiles)
    {
        var directories = targetFiles
            .Select(Path.GetDirectoryName)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (directories.Count == 0)
        {
            return Directory.GetCurrentDirectory();
        }

        var commonRoot = directories[0].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var directory in directories.Skip(1))
        {
            commonRoot = ResolveCommonPathPrefix(commonRoot, directory);
            if (string.IsNullOrWhiteSpace(commonRoot))
            {
                return Path.GetPathRoot(directories[0]) ?? Directory.GetCurrentDirectory();
            }
        }

        return string.IsNullOrWhiteSpace(commonRoot)
            ? Path.GetPathRoot(directories[0]) ?? Directory.GetCurrentDirectory()
            : commonRoot;
    }

    private static string ResolveCommonPathPrefix(string left, string right)
    {
        var leftParts = left.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var rightParts = right.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var count = Math.Min(leftParts.Length, rightParts.Length);
        var commonParts = new List<string>();
        for (var i = 0; i < count; i++)
        {
            if (!string.Equals(leftParts[i], rightParts[i], StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            commonParts.Add(leftParts[i]);
        }

        if (commonParts.Count == 0)
        {
            return Path.GetPathRoot(left) ?? string.Empty;
        }

        var root = Path.GetPathRoot(left) ?? string.Empty;
        return Path.Combine(new[] { root }.Concat(commonParts).ToArray());
    }

    private static void ApplyEnhancementFolderScope(
        JsonObject enhancement,
        string name,
        IReadOnlyList<long> folderIds)
    {
        if (folderIds.Count == 0 || enhancement[name] is not JsonObject feature)
        {
            return;
        }
        feature["folderIds"] = new JsonArray(
            folderIds.Select(value => JsonValue.Create(value)).ToArray());
    }

    private static List<string> NormalizeEnhancementTargetFiles(
        IReadOnlyList<string>? targetFiles,
        IReadOnlyList<DeezSpoTag.Services.Library.FolderDto> scopedFolders)
    {
        if (targetFiles == null || targetFiles.Count == 0)
        {
            return new List<string>();
        }

        var roots = scopedFolders.Select(folder => Path.GetFullPath(folder.RootPath)).ToList();
        return targetFiles
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Where(path => System.IO.File.Exists(path)
                && AutoTagFolderScopeHelper.IsPathInAllowedRoots(path, roots))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool TryNormalizeStartRequest(
        AutoTagStartRequest? request,
        out string normalizedPath,
        out IActionResult validationError)
    {
        normalizedPath = string.Empty;
        validationError = new BadRequestObjectResult("Invalid request.");

        if (request == null)
        {
            validationError = new BadRequestObjectResult("Invalid request.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.Path))
        {
            validationError = new BadRequestObjectResult("Path is required.");
            return false;
        }

        try
        {
            normalizedPath = Path.GetFullPath(request.Path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            validationError = new BadRequestObjectResult("Path is invalid.");
            return false;
        }

        return true;
    }

    private static IActionResult CreateStartJobResponse(AutoTagJob? job)
    {
        if (job == null)
        {
            var skippedPayload = new
            {
                jobId = string.Empty,
                status = AutoTagLiterals.SkippedStatus,
                error = "Downloads are active. AutoTag did not start."
            };
            return new ConflictObjectResult(skippedPayload);
        }

        var payload = new
        {
            jobId = job.Id,
            status = job.Status,
            error = job.Error
        };

        if (string.Equals(job.Status, AutoTagLiterals.RunningStatus, StringComparison.OrdinalIgnoreCase))
        {
            return new OkObjectResult(payload);
        }

        if (string.Equals(job.Status, AutoTagLiterals.SkippedStatus, StringComparison.OrdinalIgnoreCase))
        {
            return new UnprocessableEntityObjectResult(payload);
        }

        if (string.Equals(job.Status, "blocked", StringComparison.OrdinalIgnoreCase)
            || string.Equals(job.Status, AutoTagLiterals.CanceledStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(job.Status, AutoTagLiterals.InterruptedStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(job.Status, AutoTagLiterals.PausedStatus, StringComparison.OrdinalIgnoreCase))
        {
            return new ConflictObjectResult(payload);
        }

        if (string.Equals(job.Status, AutoTagLiterals.FailedStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(job.Status, AutoTagLiterals.ErrorStatus, StringComparison.OrdinalIgnoreCase))
        {
            return new ObjectResult(payload) { StatusCode = StatusCodes.Status500InternalServerError };
        }

        return string.IsNullOrWhiteSpace(job.Id)
            ? new ConflictObjectResult(payload)
            : new ObjectResult(payload) { StatusCode = StatusCodes.Status500InternalServerError };
    }

    private bool TryBuildEffectiveConfigNode(
        DeezSpoTag.Core.Models.Settings.TaggingProfile? selectedProfile,
        out JsonObject configNode,
        out IActionResult? validationError)
    {
        configNode = new JsonObject();
        validationError = null;
        if (selectedProfile == null)
        {
            validationError = BadRequest("Profile is required.");
            return false;
        }

        var profileConfigJson = _autoTagConfigBuilder.BuildConfigJson(selectedProfile);
        if (string.IsNullOrWhiteSpace(profileConfigJson))
        {
            validationError = BadRequest("Selected profile has no AutoTag configuration.");
            return false;
        }

        try
        {
            configNode = JsonNode.Parse(profileConfigJson) as JsonObject ?? new JsonObject();
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            validationError = BadRequest("Selected profile configuration is invalid.");
            return false;
        }
    }

    private async Task<IActionResult?> ValidateStartScopeAsync(string normalizedPath, CancellationToken cancellationToken)
    {
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

        if (await _queueRepository.HasActiveDownloadsAsync(cancellationToken))
        {
            return StatusCode(409, "Downloads are active. AutoTag cannot start until the queue is idle.");
        }

        return null;
    }

    private bool TryValidateEnrichmentScope(string normalizedPath, JsonObject configNode, string? runIntent, out IActionResult validationError)
    {
        validationError = new EmptyResult();
        if (!ShouldEnforceEnrichmentScope(runIntent))
        {
            return true;
        }

        if (!HasRequestedEnrichmentTags(configNode))
        {
            return true;
        }

        if (!TryResolveConfiguredDownloadRoot(out var downloadRoot, out var error))
        {
            validationError = BadRequest(error);
            return false;
        }

        if (AutoTagFolderScopeHelper.IsPathInAllowedRoots(normalizedPath, new[] { downloadRoot }))
        {
            return true;
        }

        validationError = BadRequest("Enrichment runs are restricted to the configured Download/Staging folder.");
        return false;
    }

    private static bool ShouldEnforceEnrichmentScope(string? runIntent)
    {
        if (string.IsNullOrWhiteSpace(runIntent))
        {
            return true;
        }

        var normalized = runIntent.Trim().ToLowerInvariant();
        return normalized switch
        {
            AutoTagLiterals.RunIntentEnhancementOnly => false,
            AutoTagLiterals.RunIntentEnhancementRecentDownloads => false,
            _ => true
        };
    }

    private async Task<(IActionResult? Error, DeezSpoTag.Core.Models.Settings.TaggingProfile? Profile)> ResolveSelectedProfileAsync(
        string? profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return (BadRequest("Profile is required."), null);
        }

        var profiles = await _profileService.LoadAsync();
        var selectedProfile = TaggingProfileService.FindByIdOrName(profiles, profileId);
        return selectedProfile == null
            ? (BadRequest("Profile was not found."), null)
            : (null, selectedProfile);
    }

    private static string SerializeConfig(JsonObject configNode)
    {
        return configNode.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    private bool TryResolveConfiguredDownloadRoot(out string downloadRoot, out string error)
    {
        return ConfiguredDownloadRootResolver.TryResolve(
            _settingsService,
            "Download/Staging folder",
            "Set Settings > Download/Staging folder before running enrichment.",
            out downloadRoot,
            out error);
    }

    private static bool HasRequestedEnrichmentTags(JsonObject configNode)
    {
        if (configNode["tags"] is not JsonArray tags)
        {
            return false;
        }

        foreach (var tagNode in tags)
        {
            string? value;
            try
            {
                value = tagNode?.GetValue<string>();
            }
            catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                return true;
            }
        }

        return false;
    }

    [HttpGet("jobs/{id}")]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult GetJob(
        string id,
        [FromQuery] bool includeLogs = true,
        [FromQuery] bool includeStatusHistory = true)
    {
        var job = _autoTagService.GetJob(id);
        if (job == null)
        {
            return NotFound();
        }

        return Ok(ToJobResponse(job, includeLogs, includeStatusHistory));
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
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult GetLatestJob(
        [FromQuery] bool includeLogs = false,
        [FromQuery] bool includeStatusHistory = false)
    {
        var job = _autoTagService.GetLatestJob();
        if (job == null)
        {
            return Ok(CreateIdleJobResponse());
        }

        return Ok(ToJobResponse(job, includeLogs, includeStatusHistory));
    }

    private static object ToJobResponse(
        AutoTagJob job,
        bool includeLogs = true,
        bool includeStatusHistory = true)
    {
        var logCount = job.Logs?.Count ?? 0;
        var statusEntryCount = job.StatusHistory?.Count ?? 0;
        var lastLogLine = logCount > 0 ? job.Logs![logCount - 1] : null;

        return new
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
            job.ReviewCount,
            job.SkippedCount,
            job.RootPath,
            job.Trigger,
            job.ProfileId,
            job.ProfileName,
            job.AutoMoveSummary,
            job.EnhancementWorkflows,
            job.CurrentPlatform,
            job.LastStatus,
            logCount,
            statusEntryCount,
            lastLogLine,
            logs = includeLogs ? job.Logs : null,
            statusHistory = includeStatusHistory ? job.StatusHistory : null
        };
    }

    private static object CreateIdleJobResponse()
    {
        string? id = null;
        DateTimeOffset? startedAt = null;
        DateTimeOffset? finishedAt = null;
        int? exitCode = null;
        string? error = null;
        string? rootPath = null;
        string? profileId = null;
        string? profileName = null;
        object? autoMoveSummary = null;
        string? currentPlatform = null;
        object? lastStatus = null;
        string? lastLogLine = null;

        return new
        {
            id,
            status = "idle",
            startedAt,
            finishedAt,
            exitCode,
            error,
            progress = 0d,
            okCount = 0,
            errorCount = 0,
            reviewCount = 0,
            skippedCount = 0,
            rootPath,
            trigger = "manual",
            profileId,
            profileName,
            autoMoveSummary,
            currentPlatform,
            lastStatus,
            logCount = 0,
            statusEntryCount = 0,
            lastLogLine,
            logs = Array.Empty<string>(),
            statusHistory = Array.Empty<object>()
        };
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
    public string? ProfileId { get; set; }
    public string? RunIntent { get; set; }
}

public sealed class AutoTagEnhancementStartRequest
{
    public string Scope { get; set; } = "full";
    public IReadOnlyList<string>? Features { get; set; }
    public IReadOnlyList<long>? FolderIds { get; set; }
    public IReadOnlyList<string>? TargetFiles { get; set; }
}
