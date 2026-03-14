using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Services;
using DeezSpoTag.Web.Services.CoverPort;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/cover-maintenance")]
[Authorize]
public sealed class CoverMaintenanceApiController : ControllerBase
{
    private readonly CoverLibraryMaintenanceService _maintenanceService;
    private readonly LibraryRepository _repository;
    private readonly LibraryConfigStore _configStore;
    private readonly DeezSpoTagSettingsService _settingsService;

    public CoverMaintenanceApiController(
        CoverLibraryMaintenanceService maintenanceService,
        LibraryRepository repository,
        LibraryConfigStore configStore,
        DeezSpoTagSettingsService settingsService)
    {
        _maintenanceService = maintenanceService;
        _repository = repository;
        _configStore = configStore;
        _settingsService = settingsService;
    }

    [HttpPost("run")]
    public async Task<IActionResult> Run([FromBody] CoverMaintenanceRunRequest request, CancellationToken cancellationToken)
    {
        var rootPaths = await ResolveRootPathsAsync(request, cancellationToken);
        if (rootPaths.Count == 0)
        {
            return BadRequest(new { error = "Unable to resolve target library root path. Select one or more enabled music folders, or leave the scope on all enabled music folders." });
        }

        var targetResolution = ResolveTargetResolution(request);
        var enabledSources = NormalizeSourceNames(request.Sources);
        var settings = _settingsService.LoadSettings();
        var runRequest = new CoverLibraryMaintenanceRequest(
            RootPaths: rootPaths,
            IncludeSubfolders: request.IncludeSubfolders ?? true,
            WorkerCount: request.WorkerCount,
            UpgradeLowResolutionCovers: request.UpgradeLowResolutionCovers ?? true,
            MinResolution: request.MinResolution,
            TargetResolution: targetResolution,
            SizeTolerancePercent: request.SizeTolerancePercent,
            PreserveSourceFormat: request.PreserveSourceFormat == true,
            ReplaceMissingEmbeddedCovers: request.ReplaceMissingEmbeddedCovers ?? true,
            SyncExternalCovers: request.SyncExternalCovers ?? true,
            QueueAnimatedArtwork: request.QueueAnimatedArtwork == true,
            AppleStorefront: string.IsNullOrWhiteSpace(settings.AppleMusic?.Storefront) ? "us" : settings.AppleMusic!.Storefront,
            AnimatedArtworkMaxResolution: settings.Video?.AppleMusicVideoMaxResolution ?? 2160,
            EnabledSources: enabledSources);

        var result = await _maintenanceService.RunAsync(runRequest, cancellationToken);
        return Ok(result);
    }

    private async Task<IReadOnlyList<string>> ResolveRootPathsAsync(CoverMaintenanceRunRequest request, CancellationToken cancellationToken)
    {
        var allFolders = await ResolveLibraryFoldersAsync(cancellationToken);
        var enabledFolders = allFolders
            .Where(folder => folder.Enabled && !string.IsNullOrWhiteSpace(folder.RootPath) && IsMusicCapableFolder(folder))
            .ToList();

        var requestedFolderIds = (request.FolderIds ?? Array.Empty<long>())
            .Where(id => id > 0)
            .Distinct()
            .ToList();
        if (request.FolderId.HasValue && request.FolderId.Value > 0 && !requestedFolderIds.Contains(request.FolderId.Value))
        {
            requestedFolderIds.Add(request.FolderId.Value);
        }

        if (requestedFolderIds.Count > 0)
        {
            return enabledFolders
                .Where(item => requestedFolderIds.Contains(item.Id))
                .Select(item => item.RootPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (!string.IsNullOrWhiteSpace(request.RootPath))
        {
            var candidate = Path.GetFullPath(request.RootPath);
            if (enabledFolders.Count == 0)
            {
                return Directory.Exists(candidate)
                    ? new[] { candidate }
                    : Array.Empty<string>();
            }

            return enabledFolders.Any(folder => IsPathUnderRoot(candidate, folder.RootPath))
                ? new[] { candidate }
                : Array.Empty<string>();
        }

        return enabledFolders
            .Select(folder => folder.RootPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyList<FolderDto>> ResolveLibraryFoldersAsync(CancellationToken cancellationToken)
    {
        if (_repository.IsConfigured)
        {
            return await _repository.GetFoldersAsync(cancellationToken);
        }

        return _configStore.GetFolders();
    }

    private static IReadOnlyCollection<CoverSourceName>? NormalizeSourceNames(IReadOnlyCollection<string>? sources)
    {
        return CoverSacadOptionMapper.ParseSources(sources);
    }

    private static int ResolveTargetResolution(CoverMaintenanceRunRequest request)
    {
        var manual = Math.Clamp(request.TargetResolution, 300, 5000);
        var mode = string.IsNullOrWhiteSpace(request.TargetResolutionMode)
            ? "manual"
            : request.TargetResolutionMode.Trim().ToLowerInvariant();
        if (!string.Equals(mode, "platform", StringComparison.OrdinalIgnoreCase))
        {
            return manual;
        }

        if (!request.InheritedTargetResolution.HasValue)
        {
            return manual;
        }

        return Math.Clamp(request.InheritedTargetResolution.Value, 300, 5000);
    }

    private static bool IsPathUnderRoot(string path, string root)
    {
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rootWithSlash = normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMusicCapableFolder(FolderDto folder)
    {
        var normalized = (folder.DesiredQuality ?? string.Empty).Trim().ToLowerInvariant();
        return !normalized.Contains("video", StringComparison.Ordinal)
            && !normalized.Contains("podcast", StringComparison.Ordinal);
    }
}

public sealed class CoverMaintenanceRunRequest
{
    public long? FolderId { get; set; }
    public IReadOnlyCollection<long>? FolderIds { get; set; }
    public string? RootPath { get; set; }
    public bool? IncludeSubfolders { get; set; }
    public int WorkerCount { get; set; } = 8;
    public bool? UpgradeLowResolutionCovers { get; set; }
    public int MinResolution { get; set; } = 500;
    public int TargetResolution { get; set; } = 1200;
    public string? TargetResolutionMode { get; set; }
    public string? TargetResolutionPlatform { get; set; }
    public int? InheritedTargetResolution { get; set; }
    public int SizeTolerancePercent { get; set; } = 25;
    public bool? PreserveSourceFormat { get; set; }
    public bool? ReplaceMissingEmbeddedCovers { get; set; }
    public bool? SyncExternalCovers { get; set; }
    public bool? QueueAnimatedArtwork { get; set; }
    public IReadOnlyCollection<string>? Sources { get; set; }
}
