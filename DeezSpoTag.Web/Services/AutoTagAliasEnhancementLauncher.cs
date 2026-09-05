using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Controllers.Api;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;

namespace DeezSpoTag.Web.Services;

/// <summary>
/// Starts the merge-triggered, targeted AutoTag enhancement run after an
/// artist alias merge: affected files (resolved from the library DB by the
/// merge service) are grouped per AutoTag profile and each group becomes one
/// enhancement job whose config carries the exact targetFiles list, so only
/// files involving the merged artist are processed.
/// </summary>
public sealed class AutoTagAliasEnhancementLauncher
{
    private static readonly IReadOnlyList<string> MergeFeatures = new[]
    {
        EnhancementWorkflowSelection.GapFill,
        EnhancementWorkflowSelection.FolderUniformity,
        EnhancementWorkflowSelection.QualityChecks,
        EnhancementWorkflowSelection.Sidecars
    };

    private readonly LibraryRepository _libraryRepository;
    private readonly LibraryConfigStore _libraryConfigStore;
    private readonly AutoTagProfileResolutionService _profileResolutionService;
    private readonly AutoTagConfigBuilder _autoTagConfigBuilder;
    private readonly AutoTagService _autoTagService;
    private readonly ILogger<AutoTagAliasEnhancementLauncher> _logger;

    public AutoTagAliasEnhancementLauncher(
        LibraryRepository libraryRepository,
        LibraryConfigStore libraryConfigStore,
        AutoTagProfileResolutionService profileResolutionService,
        AutoTagConfigBuilder autoTagConfigBuilder,
        AutoTagService autoTagService,
        ILogger<AutoTagAliasEnhancementLauncher> logger)
    {
        _libraryRepository = libraryRepository;
        _libraryConfigStore = libraryConfigStore;
        _profileResolutionService = profileResolutionService;
        _autoTagConfigBuilder = autoTagConfigBuilder;
        _autoTagService = autoTagService;
        _logger = logger;
    }

    /// <summary>Starts one enhancement job per affected profile. Returns started job ids.</summary>
    public async Task<IReadOnlyList<string>> StartForFilesAsync(
        IReadOnlyList<string> affectedFilePaths,
        CancellationToken cancellationToken)
    {
        var existingPaths = (affectedFilePaths ?? Array.Empty<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (existingPaths.Count == 0)
        {
            return Array.Empty<string>();
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
        if (enabledFolders.Count == 0)
        {
            _logger.LogWarning("Alias merge enhancement skipped: no enabled music library folders.");
            return Array.Empty<string>();
        }

        // Map each affected file to its owning library folder.
        var filesByFolder = new Dictionary<long, List<string>>();
        foreach (var path in existingPaths)
        {
            var folder = enabledFolders.FirstOrDefault(folder =>
                AutoTagFolderScopeHelper.IsPathUnderRoot(path, folder.RootPath));
            if (folder == null)
            {
                _logger.LogInformation(
                    "Alias merge enhancement: file {Path} is not under an enabled music folder; skipped.",
                    path);
                continue;
            }

            if (!filesByFolder.TryGetValue(folder.Id, out var list))
            {
                list = new List<string>();
                filesByFolder[folder.Id] = list;
            }

            list.Add(path);
        }

        if (filesByFolder.Count == 0)
        {
            return Array.Empty<string>();
        }

        var profileState = await _profileResolutionService.LoadNormalizedStateAsync(
            includeFolders: true,
            cancellationToken);

        var jobIds = new List<string>();
        // One job per library folder: files of an alias live in one library and
        // are maintained in that library — jobs never span library roots, so
        // nothing can criss-cross between libraries.
        var jobGroups = new List<(
            DeezSpoTag.Core.Models.Settings.TaggingProfile Profile,
            long FolderId,
            string FolderName,
            string RootPath,
            List<string> TargetFiles)>();
        foreach (var folderId in filesByFolder.Keys)
        {
            var folder = enabledFolders.First(candidate => candidate.Id == folderId);
            var profile = AutoTagProfileResolutionService.ResolveFolderProfile(
                profileState,
                folder.Id,
                folder.AutoTagProfileId);
            if (profile == null)
            {
                _logger.LogWarning(
                    "Alias merge enhancement: folder {FolderId} ({FolderName}) has no resolvable AutoTag profile; its files were skipped.",
                    folder.Id,
                    folder.DisplayName);
                continue;
            }

            jobGroups.Add((
                profile,
                folder.Id,
                folder.DisplayName,
                Path.GetFullPath(folder.RootPath),
                filesByFolder[folderId]));
        }

        foreach (var (profile, folderId, folderName, rootPath, targetFiles) in jobGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var profileConfigJson = _autoTagConfigBuilder.BuildConfigJson(profile);
                var configNode = JsonNode.Parse(profileConfigJson) as JsonObject ?? new JsonObject();
                EnhancementWorkflowSelection.ApplyFeatureSelection(
                    configNode,
                    MergeFeatures,
                    new[] { folderId },
                    targetFiles);
                configNode["path"] = rootPath;

                var job = await _autoTagService.StartJob(
                    rootPath,
                    configNode.ToJsonString(),
                    new AutoTagService.StartJobOptions(
                        // The merge is user-initiated in Settings; enhancement
                        // runs only accept manual/schedule triggers.
                        Trigger: AutoTagLiterals.ManualTrigger,
                        ProfileId: profile.Id,
                        ProfileName: profile.Name,
                        RunIntent: AutoTagLiterals.RunIntentAliasMerge,
                        FolderStructureOverride: profile.FolderStructure));
                if (job != null)
                {
                    jobIds.Add(job.Id);
                    _logger.LogInformation(
                        "Alias merge enhancement started: job {JobId} for folder {FolderName} (profile {Profile}) with {FileCount} target file(s).",
                        job.Id,
                        folderName,
                        profile.Name,
                        targetFiles.Count);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Alias merge enhancement failed to start for profile {Profile}.",
                    profile.Name);
            }
        }

        return jobIds;
    }

    /// <summary>Deepest shared directory of the target files that sits under one of the scoped roots.</summary>
}
