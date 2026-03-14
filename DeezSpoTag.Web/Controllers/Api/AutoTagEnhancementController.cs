using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/autotag")]
[Authorize]
public class AutoTagEnhancementController : ControllerBase
{
    private readonly LibraryRepository _libraryRepository;
    private readonly LibraryConfigStore _libraryConfigStore;
    private readonly AutoTagLibraryOrganizer _libraryOrganizer;
    private readonly QualityScannerService _qualityScannerService;
    private readonly DuplicateCleanerService _duplicateCleanerService;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly LyricsRefreshQueueService _lyricsRefreshQueueService;

    public AutoTagEnhancementController(
        LibraryRepository libraryRepository,
        LibraryConfigStore libraryConfigStore,
        AutoTagLibraryOrganizer libraryOrganizer,
        QualityScannerService qualityScannerService,
        DuplicateCleanerService duplicateCleanerService,
        DeezSpoTagSettingsService settingsService,
        LyricsRefreshQueueService lyricsRefreshQueueService)
    {
        _libraryRepository = libraryRepository;
        _libraryConfigStore = libraryConfigStore;
        _libraryOrganizer = libraryOrganizer;
        _qualityScannerService = qualityScannerService;
        _duplicateCleanerService = duplicateCleanerService;
        _settingsService = settingsService;
        _lyricsRefreshQueueService = lyricsRefreshQueueService;
    }

    [HttpPost("enhancement/folder-uniformity")]
    public async Task<IActionResult> RunFolderUniformity(
        [FromBody] EnhancementFolderUniformityRequest request,
        CancellationToken cancellationToken)
    {
        if (request.EnforceFolderStructure == false)
        {
            return Ok(new
            {
                success = true,
                skipped = true,
                message = "Folder uniformity is disabled by configuration.",
                foldersProcessed = 0
            });
        }

        var folders = await AutoTagFolderScopeHelper.ResolveLibraryFoldersAsync(_libraryRepository, _libraryConfigStore, cancellationToken);
        var enabledFolders = folders
            .Where(folder => folder.Enabled && !string.IsNullOrWhiteSpace(folder.RootPath))
            .ToList();
        var scopedFolderIds = AutoTagFolderScopeHelper.NormalizeFolderIds(request.FolderIds, request.FolderId, enabledFolders);
        if (scopedFolderIds.Count > 0)
        {
            enabledFolders = enabledFolders
                .Where(folder => scopedFolderIds.Contains(folder.Id))
                .ToList();
        }

        if (enabledFolders.Count == 0)
        {
            return BadRequest("No enabled library folders are available for organizer checks.");
        }

        var organizerOptions = new AutoTagOrganizerOptions
        {
            DryRun = request.DryRunMode == true,
            IncludeSubfolders = request.IncludeSubfolders ?? true,
            MoveMisplacedFiles = request.MoveMisplacedFiles ?? true,
            RenameFilesToTemplate = request.RenameFilesToTemplate == true,
            RemoveEmptyFolders = request.RemoveEmptyFolders == true
        };

        var logs = new List<string>();
        var processed = 0;
        var skipped = 0;
        foreach (var folder in enabledFolders)
        {
            var rootPath = folder.RootPath?.Trim();
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            {
                skipped++;
                continue;
            }

            processed++;
            await _libraryOrganizer.OrganizePathAsync(rootPath, organizerOptions, message =>
            {
                if (logs.Count < 600)
                {
                    logs.Add($"[{folder.DisplayName}] {message}");
                }
            });
        }

        return Ok(new
        {
            success = true,
            foldersProcessed = processed,
            foldersSkipped = skipped,
            options = new
            {
                DryRunMode = request.DryRunMode == true,
                MoveMisplacedFiles = request.MoveMisplacedFiles ?? true,
                RenameFilesToTemplate = request.RenameFilesToTemplate == true,
                RemoveEmptyFolders = request.RemoveEmptyFolders == true
            },
            logs
        });
    }

    [HttpPost("enhancement/quality-checks")]
    public async Task<IActionResult> RunEnhancementQualityChecks(
        [FromBody] EnhancementQualityChecksRequest request,
        CancellationToken cancellationToken)
    {
        var technicalProfiles = NormalizeTechnicalProfiles(request.TechnicalProfiles);
        var runQualityScanner = request.FlagMissingTags == true
            || request.FlagMismatchedMetadata == true
            || request.QueueAtmosAlternatives == true
            || technicalProfiles.Count > 0;
        var runDuplicateCheck = request.FlagDuplicates == true;
        var runLyricsRefresh = request.QueueLyricsRefresh == true;
        if (!runQualityScanner && !runDuplicateCheck && !runLyricsRefresh)
        {
            return BadRequest("Enable at least one quality check to run.");
        }

        var (scopeError, enabledFolders, scopedFolderIds) = await ResolveEnhancementScopeAsync(request, cancellationToken);
        if (scopeError != null)
        {
            return scopeError;
        }

        var qualityScanner = runQualityScanner
            ? await StartEnhancementQualityScannerAsync(request, technicalProfiles, scopedFolderIds, cancellationToken)
            : null;
        var duplicateCheck = runDuplicateCheck
            ? await RunEnhancementDuplicateCheckAsync(request, enabledFolders, cancellationToken)
            : null;
        var lyricsRefresh = runLyricsRefresh
            ? await RunEnhancementLyricsRefreshAsync(request, technicalProfiles, scopedFolderIds, cancellationToken)
            : null;

        return Ok(new
        {
            success = true,
            qualityScanner,
            duplicateCheck,
            lyricsRefresh
        });
    }

    [HttpGet("enhancement/technical-profiles")]
    public async Task<IActionResult> GetEnhancementTechnicalProfiles(
        [FromQuery] long? folderId,
        [FromQuery] string? folderIds,
        [FromQuery] string? scope,
        CancellationToken cancellationToken)
    {
        var folders = await AutoTagFolderScopeHelper.ResolveLibraryFoldersAsync(_libraryRepository, _libraryConfigStore, cancellationToken);
        var enabledFolders = folders
            .Where(folder => folder.Enabled && !string.IsNullOrWhiteSpace(folder.RootPath))
            .ToList();
        var folderIdsFromQuery = AutoTagFolderScopeHelper.ParseFolderIdsQuery(folderIds);
        var selectedFolderIds = AutoTagFolderScopeHelper.NormalizeFolderIds(folderIdsFromQuery, folderId, enabledFolders);

        if (selectedFolderIds.Count > 0 && enabledFolders.All(folder => !selectedFolderIds.Contains(folder.Id)))
        {
            return BadRequest("Selected library folders were not found or are disabled.");
        }

        var resolvedScope = string.Equals(scope, "watchlist", StringComparison.OrdinalIgnoreCase)
            ? "watchlist"
            : "all";
        var tracks = await _libraryRepository.GetQualityScanTracksAsync(
            resolvedScope,
            selectedFolderIds.Count == 1 ? selectedFolderIds[0] : null,
            minFormat: null,
            minBitDepth: null,
            minSampleRateHz: null,
            cancellationToken);
        if (selectedFolderIds.Count > 1)
        {
            var allowed = selectedFolderIds.ToHashSet();
            tracks = tracks
                .Where(track => track.DestinationFolderId.HasValue && allowed.Contains(track.DestinationFolderId.Value))
                .ToList();
        }

        var profiles = tracks
            .Select(FormatTechnicalProfile)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                value = group.First(),
                count = group.Count()
            })
            .OrderByDescending(item => item.count)
            .ThenBy(item => item.value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Ok(new
        {
            scope = resolvedScope,
            folderIds = selectedFolderIds,
            totalTracks = tracks.Count,
            profiles
        });
    }

    private async Task<(IActionResult? Error, List<FolderDto> EnabledFolders, IReadOnlyList<long> ScopedFolderIds)> ResolveEnhancementScopeAsync(
        EnhancementQualityChecksRequest request,
        CancellationToken cancellationToken)
    {
        var folders = await AutoTagFolderScopeHelper.ResolveLibraryFoldersAsync(_libraryRepository, _libraryConfigStore, cancellationToken);
        var enabledFolders = folders
            .Where(folder => folder.Enabled && !string.IsNullOrWhiteSpace(folder.RootPath))
            .ToList();
        var scopedFolderIds = AutoTagFolderScopeHelper.NormalizeFolderIds(request.FolderIds, request.FolderId, enabledFolders);
        if (scopedFolderIds.Count == 0)
        {
            return (null, enabledFolders, scopedFolderIds);
        }

        enabledFolders = enabledFolders
            .Where(folder => scopedFolderIds.Contains(folder.Id))
            .ToList();
        if (enabledFolders.Count == 0)
        {
            return (BadRequest("Selected library folders were not found or are disabled."), new List<FolderDto>(), new List<long>());
        }

        return (null, enabledFolders, scopedFolderIds);
    }

    private async Task<object> StartEnhancementQualityScannerAsync(
        EnhancementQualityChecksRequest request,
        IReadOnlyCollection<string> technicalProfiles,
        IReadOnlyList<long> scopedFolderIds,
        CancellationToken cancellationToken)
    {
        var minSampleRateHz = request.MinSampleRateKhz.HasValue && request.MinSampleRateKhz.Value > 0
            ? (int?)Math.Clamp((int)Math.Round(request.MinSampleRateKhz.Value * 1000d), 1000, 768000)
            : null;
        var started = await _qualityScannerService.StartAsync(
            new QualityScannerStartRequest
            {
                Scope = request.Scope,
                FolderId = scopedFolderIds.Count == 1 ? scopedFolderIds[0] : null,
                MinFormat = request.MinFormat,
                MinBitDepth = request.MinBitDepth,
                MinSampleRateHz = minSampleRateHz,
                QueueAtmosAlternatives = request.QueueAtmosAlternatives,
                CooldownMinutes = request.CooldownMinutes,
                Trigger = "enhancement",
                MarkAutomationWindow = false,
                TechnicalProfiles = technicalProfiles.ToList(),
                FolderIds = scopedFolderIds.ToList()
            },
            cancellationToken);
        return new
        {
            requested = true,
            started,
            state = _qualityScannerService.GetState()
        };
    }

    private async Task<object> RunEnhancementDuplicateCheckAsync(
        EnhancementQualityChecksRequest request,
        IReadOnlyList<FolderDto> enabledFolders,
        CancellationToken cancellationToken)
    {
        if (enabledFolders.Count == 0)
        {
            return new
            {
                filesScanned = 0,
                duplicatesFound = 0,
                deleted = 0,
                spaceFreedBytes = 0,
                duplicatesFolderName = DuplicateCleanerService.DuplicatesFolderName
            };
        }

        var result = await _duplicateCleanerService.ScanAsync(enabledFolders.ToList(), request.UseDuplicatesFolder ?? true, cancellationToken);
        return new
        {
            filesScanned = result.FilesScanned,
            duplicatesFound = result.DuplicatesFound,
            deleted = result.Deleted,
            spaceFreedBytes = result.SpaceFreedBytes,
            duplicatesFolderName = DuplicateCleanerService.DuplicatesFolderName
        };
    }

    private async Task<object> RunEnhancementLyricsRefreshAsync(
        EnhancementQualityChecksRequest request,
        IReadOnlyCollection<string> technicalProfiles,
        IReadOnlyList<long> scopedFolderIds,
        CancellationToken cancellationToken)
    {
        var settings = _settingsService.LoadSettings();
        if (!LyricsSettingsPolicy.CanFetchLyrics(settings))
        {
            return new
            {
                requested = 0,
                enqueued = 0,
                skipped = 0,
                disabledByTechnicalPreference = true
            };
        }

        var tracks = await _libraryRepository.GetQualityScanTracksAsync(
            request.Scope,
            scopedFolderIds.Count == 1 ? scopedFolderIds[0] : null,
            minFormat: null,
            minBitDepth: null,
            minSampleRateHz: null,
            cancellationToken);
        if (scopedFolderIds.Count > 1)
        {
            var allowed = scopedFolderIds.ToHashSet();
            tracks = tracks
                .Where(track => track.DestinationFolderId.HasValue && allowed.Contains(track.DestinationFolderId.Value))
                .ToList();
        }

        if (technicalProfiles.Count > 0)
        {
            var allowedProfiles = technicalProfiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
            tracks = tracks
                .Where(track => allowedProfiles.Contains(FormatTechnicalProfile(track)))
                .ToList();
        }

        var trackIds = tracks
            .Select(track => track.TrackId)
            .Distinct()
            .ToList();
        var enqueueResult = _lyricsRefreshQueueService.Enqueue(trackIds);
        return new
        {
            requested = enqueueResult.Requested,
            enqueued = enqueueResult.Enqueued,
            skipped = enqueueResult.Skipped,
            disabledByTechnicalPreference = false
        };
    }

    private static string FormatTechnicalProfile(QualityScanTrackDto track)
    {
        var extension = string.IsNullOrWhiteSpace(track.BestExtension)
            ? "UNKNOWN"
            : track.BestExtension.Trim().ToUpperInvariant();
        var bitDepth = track.BestBitsPerSample.HasValue && track.BestBitsPerSample.Value > 0
            ? $"{track.BestBitsPerSample.Value}-bit"
            : "unknown";
        var sampleRate = track.BestSampleRateHz.HasValue && track.BestSampleRateHz.Value > 0
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{track.BestSampleRateHz.Value / 1000d:0.0} kHz")
            : "unknown";
        return $"{extension} • {bitDepth} • {sampleRate}";
    }

    private static IReadOnlyList<string> NormalizeTechnicalProfiles(IReadOnlyList<string>? source)
    {
        if (source is null || source.Count == 0)
        {
            return Array.Empty<string>();
        }

        return source
            .Select(value => value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToList();
    }
}
